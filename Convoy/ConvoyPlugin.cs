using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace Convoy
{
    [BepInPlugin("io.cebarks.convoy", "Convoy", VersionInfo.Version)]
    public class ConvoyPlugin : BaseUnityPlugin
    {
        private enum PluginState { Idle, Planning, AwaitingConfirmation, Executing, RestartRequired, Complete }

        private ConvoyConfig? _config;
        private SyncEngine? _engine;
        private ConvoyPanel? _panel;
        private PluginState _state = PluginState.Idle;

        // Planning phase (only used for panel-triggered re-plans)
        private SyncProgress? _planProgress;
        private Thread? _planThread;
        private volatile SyncPlan? _pendingPlan;

        // Confirmation UI state
        private SyncPlan? _plan;
        private Dictionary<string, bool> _groupCollapsed = new Dictionary<string, bool>();
        private Dictionary<int, bool> _modChecked = new Dictionary<int, bool>();
        private Vector2 _scrollPosition;

        // Execution phase
        private SyncProgress? _execProgress;
        private Thread? _execThread;

        // Status display
        private string? _statusText;
        private float _statusExpiry;
        private Color _statusColor;

        // Cached IMGUI resources
        private Texture2D? _overlayTex;
        private Texture2D? _panelBgTex;
        private GUIStyle? _modalWindowStyle;
        private GUIStyle? _panelStyle;
        private GUIStyle? _titleStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _modStyle;

        private const float PanelWidth = 500f;
        private const float PanelMaxHeight = 600f;
        private const float LineHeight = 26f;
        private const float Padding = 16f;
        private const float IndentWidth = 28f;
        private const int ModalId = 0x436F6E76; // unique window ID

        private void Awake()
        {
            _config = new ConvoyConfig(Config);
            _engine = new SyncEngine(Logger, _config);
            _panel = new ConvoyPanel(
                _config.PanelKeybind!,
                () => StartPlanning(),
                () => StartRedownload(bundlesOnly: true),
                () => StartRedownload(bundlesOnly: false),
                () => _state == PluginState.Planning || _state == PluginState.Executing,
                () => false // ponytail: in-raid detection, always allow for now — see spec
            );
            _panel.UpdateState(ConvoyState.Load());

            // Run plan synchronously to block game init until catalog check completes
            try
            {
                var plan = _engine.PlanSync();
                HandlePlanResult(plan, null);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Convoy plan failed: {ex.Message}");
                Logger.LogDebug(ex);
                _engine.SendReport("failed", null, ex.Message);
                HandlePlanResult(null, ex.Message);
            }
        }

        private void HandlePlanResult(SyncPlan? plan, string? error)
        {
            if (error != null)
            {
                var outcome = new SyncOutcome { Result = SyncResult.Failed, Error = error };
                if (plan != null)
                {
                    outcome.SptVersion = plan.Catalog.SptVersion;
                    outcome.QuartermasterVersion = plan.Catalog.QuartermasterVersion;
                    outcome.ServerUrl = plan.ServerUrl;
                }
                _panel?.UpdateOutcome(outcome);
                ShowStatus("Convoy: sync failed — check BepInEx log", Color.red, 15f);
                _state = PluginState.Complete;
            }
            else if (plan == null ||
                     (plan.Installs.Count == 0 && plan.Updates.Count == 0 && plan.Removals.Count == 0))
            {
                var outcome = new SyncOutcome { Result = SyncResult.UpToDate };
                if (plan != null)
                {
                    outcome.SptVersion = plan.Catalog.SptVersion;
                    outcome.QuartermasterVersion = plan.Catalog.QuartermasterVersion;
                    outcome.ServerUrl = plan.ServerUrl;
                    _panel?.UpdateCatalog(plan.Catalog);
                }
                _panel?.UpdateOutcome(outcome);
                _panel?.UpdateState(ConvoyState.Load());
                ShowStatus("Convoy: mods up to date", Color.green, 5f);
                _state = PluginState.Complete;
            }
            else
            {
                _plan = plan;
                _panel?.UpdateCatalog(plan.Catalog);
                _panel?.Close();
                InitConfirmationUI(plan);
                _state = PluginState.AwaitingConfirmation;
            }
        }

        private void StartPlanning()
        {
            var engine = _engine!;
            var progress = new SyncProgress();
            _planProgress = progress;
            _pendingPlan = null;

            _planThread = new Thread(() =>
            {
                try
                {
                    var plan = engine.PlanSync(progress);
                    _pendingPlan = plan;
                    progress.Complete(SyncResult.UpToDate);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Convoy plan failed: {ex.Message}");
                    Logger.LogDebug(ex);
                    engine.SendReport("failed", null, ex.Message);
                    progress.Complete(SyncResult.Failed, ex.Message);
                }
            })
            {
                Name = "ConvoyPlan",
                IsBackground = true
            };
            _planThread.Start();
            _state = PluginState.Planning;
        }

        private void Update()
        {
            // Suppress keybind while a modal overlay is showing
            if (_state != PluginState.AwaitingConfirmation && _state != PluginState.RestartRequired
                && _state != PluginState.Planning && _state != PluginState.Executing)
                _panel?.HandleKeybind();
            _panel?.FlushIfDirty();

            switch (_state)
            {
                case PluginState.Planning:
                    if (_planProgress != null && _planProgress.IsComplete)
                    {
                        var error = _planProgress.Result == SyncResult.Failed ? _planProgress.Error : null;
                        HandlePlanResult(_pendingPlan, error);
                        _planThread = null;
                        _planProgress = null;
                        _pendingPlan = null;
                    }
                    break;

                case PluginState.Executing:
                    if (_execProgress != null && _execProgress.IsComplete)
                    {
                        var outcome = new SyncOutcome
                        {
                            Result = _execProgress.Result ?? SyncResult.UpToDate,
                            Error = _execProgress.Error
                        };
                        if (_plan != null)
                        {
                            outcome.SptVersion = _plan.Catalog.SptVersion;
                            outcome.QuartermasterVersion = _plan.Catalog.QuartermasterVersion;
                            outcome.ServerUrl = _plan.ServerUrl;
                        }
                        _panel?.UpdateOutcome(outcome);
                        _panel?.UpdateState(ConvoyState.Load());

                        _execThread = null;
                        _execProgress = null;
                        _plan = null;

                        switch (outcome.Result)
                        {
                            case SyncResult.Failed:
                                ShowStatus("Convoy: sync failed — check BepInEx log", Color.red, 15f);
                                _state = PluginState.Complete;
                                break;
                            case SyncResult.RestartRequired:
                                _state = PluginState.RestartRequired;
                                break;
                            default:
                                ShowStatus("Convoy: mods up to date", Color.green, 5f);
                                _state = PluginState.Complete;
                                break;
                        }
                    }
                    break;
            }

        }

        private void InitConfirmationUI(SyncPlan plan)
        {
            _groupCollapsed.Clear();
            _modChecked.Clear();
            _scrollPosition = Vector2.zero;

            var slugs = plan.Installs.Concat(plan.Updates)
                .Select(m => m.GroupSlug).Distinct();
            foreach (var slug in slugs)
                _groupCollapsed[slug] = false;
            if (plan.Removals.Count > 0)
                _groupCollapsed["__removals"] = false;

            foreach (var mod in plan.Installs.Concat(plan.Updates).Where(m => !m.IsRequired))
                _modChecked[mod.Id] = true;
        }

        private void OnConfirm()
        {
            var plan = _plan!;
            var confirmedModIds = new List<int>();

            confirmedModIds.AddRange(plan.Installs.Where(m => m.IsRequired).Select(m => m.Id));
            confirmedModIds.AddRange(plan.Updates.Where(m => m.IsRequired).Select(m => m.Id));

            foreach (var kvp in _modChecked)
            {
                if (kvp.Value)
                    confirmedModIds.Add(kvp.Key);
            }

            var skippedModIds = new HashSet<int>(plan.State.SkippedMods);
            foreach (var kvp in _modChecked)
            {
                if (!kvp.Value)
                    skippedModIds.Add(kvp.Key);
            }

            StartExecution(confirmedModIds, skippedModIds);
        }

        private void OnSkipSync()
        {
            _engine!.SendReport("skipped", null, null);
            ShowStatus("Convoy: sync skipped", Color.yellow, 5f);
            _plan = null;
            _state = PluginState.Complete;
        }

        private void StartExecution(List<int> confirmedModIds, HashSet<int> skippedModIds)
        {
            var engine = _engine!;
            var plan = _plan!;
            var progress = new SyncProgress();
            _execProgress = progress;

            _execThread = new Thread(() =>
            {
                try
                {
                    var result = engine.ExecuteSync(plan, confirmedModIds, skippedModIds, progress);
                    progress.Complete(result);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Convoy sync failed: {ex.Message}");
                    Logger.LogDebug(ex);
                    engine.SendReport("failed", null, ex.Message);
                    progress.Complete(SyncResult.Failed, ex.Message);
                }
            })
            {
                Name = "ConvoyExec",
                IsBackground = true
            };
            _execThread.Start();
            _state = PluginState.Executing;
        }

        private void StartRedownload(bool bundlesOnly)
        {
            var engine = _engine!;
            var progress = new SyncProgress();
            _execProgress = progress;

            _execThread = new Thread(() =>
            {
                try
                {
                    progress.SetPhase("Fetching catalog...");
                    var catalog = engine.FetchCatalogPublic();
                    var result = engine.ExecuteRedownload(catalog, bundlesOnly, progress);
                    progress.Complete(result);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Convoy redownload failed: {ex.Message}");
                    Logger.LogDebug(ex);
                    engine.SendReport("failed", null, ex.Message);
                    progress.Complete(SyncResult.Failed, ex.Message);
                }
            })
            {
                Name = bundlesOnly ? "ConvoyBundleDL" : "ConvoyFullDL",
                IsBackground = true
            };
            _execThread.Start();
            _panel?.Close();
            _state = PluginState.Executing;
        }

        private void ShowStatus(string text, Color color, float duration)
        {
            _statusText = text;
            _statusColor = color;
            _statusExpiry = Time.realtimeSinceStartup + duration;
        }

        #region OnGUI

        private void EnsureStyles()
        {
            if (_overlayTex == null)
            {
                _overlayTex = new Texture2D(1, 1);
                _overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
                _overlayTex.Apply();
            }
            if (_panelBgTex == null)
            {
                _panelBgTex = new Texture2D(1, 1);
                _panelBgTex.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.95f));
                _panelBgTex.Apply();
            }
            if (_modalWindowStyle == null)
            {
                _modalWindowStyle = new GUIStyle { normal = { background = _overlayTex } };
            }
            if (_panelStyle == null)
            {
                _panelStyle = new GUIStyle { normal = { background = _panelBgTex } };
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20, fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14, fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) }
                };
                _modStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    normal = { textColor = new Color(0.8f, 0.8f, 0.8f, 1f) }
                };
            }
        }

        private void OnGUI()
        {
            if (_state == PluginState.RestartRequired ||
                _state == PluginState.AwaitingConfirmation ||
                _state == PluginState.Planning ||
                _state == PluginState.Executing)
            {
                EnsureStyles();
                GUI.ModalWindow(ModalId, new Rect(0, 0, Screen.width, Screen.height),
                    DrawModalContent, "", _modalWindowStyle);
                return;
            }

            // Draw the keybind panel (handles its own open/closed state)
            _panel?.DrawPanel();

            // Don't draw status overlay on top of the panel
            if (_panel != null && _panel.IsOpen) return;

            if (_statusText != null)
            {
                if (Time.realtimeSinceStartup > _statusExpiry)
                {
                    _statusText = null;
                    return;
                }

                var remaining = _statusExpiry - Time.realtimeSinceStartup;
                var alpha = remaining < 2f ? remaining / 2f : 1f;

                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    normal = { textColor = new Color(_statusColor.r, _statusColor.g, _statusColor.b, alpha) }
                };

                var rect = new Rect(12, 12, 800, 40);
                var shadowStyle = new GUIStyle(style)
                {
                    normal = { textColor = new Color(0, 0, 0, alpha * 0.8f) }
                };
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), _statusText, shadowStyle);
                GUI.Label(rect, _statusText, style);
            }
        }

        private void DrawModalContent(int windowId)
        {
            switch (_state)
            {
                case PluginState.Planning:
                case PluginState.Executing:
                    DrawSyncProgress();
                    break;
                case PluginState.AwaitingConfirmation:
                    if (_plan != null) DrawConfirmation();
                    break;
                case PluginState.RestartRequired:
                    DrawRestart();
                    break;
            }
        }

        private void DrawSyncProgress()
        {
            const float popupWidth = 400f;
            const float popupHeight = 120f;
            float popupX = (Screen.width - popupWidth) / 2f;
            float popupY = (Screen.height - popupHeight) / 2f;

            GUI.Box(new Rect(popupX, popupY, popupWidth, popupHeight), "", _panelStyle);
            GUI.Label(new Rect(popupX, popupY + Padding, popupWidth, 30f), "Convoy", _titleStyle);

            var msgStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 1f) }
            };

            var progress = _state == PluginState.Planning ? _planProgress : _execProgress;
            var progressText = progress != null ? FormatProgress(progress) : "Syncing...";
            GUI.Label(new Rect(popupX + Padding, popupY + 50f, popupWidth - Padding * 2, 50f),
                progressText, msgStyle);
        }

        private void DrawConfirmation()
        {
            var plan = _plan!;

            float contentHeight = CalculateContentHeight(plan);
            float titleHeight = 40f;
            float buttonHeight = 44f;
            float panelHeight = Math.Min(contentHeight + titleHeight + buttonHeight + Padding * 2, PanelMaxHeight);
            float panelX = (Screen.width - PanelWidth) / 2f;
            float panelY = (Screen.height - panelHeight) / 2f;
            var panelRect = new Rect(panelX, panelY, PanelWidth, panelHeight);

            GUI.Box(panelRect, "", _panelStyle);
            GUI.Label(new Rect(panelX, panelY + Padding, PanelWidth, 30f), "Convoy Sync", _titleStyle);

            float scrollTop = panelY + Padding + titleHeight;
            float scrollHeight = panelHeight - titleHeight - buttonHeight - Padding * 2;
            var scrollViewRect = new Rect(panelX + Padding, scrollTop, PanelWidth - Padding * 2, scrollHeight);
            float innerWidth = PanelWidth - Padding * 2 - 20f;
            var scrollContentRect = new Rect(0, 0, innerWidth, contentHeight);
            _scrollPosition = GUI.BeginScrollView(scrollViewRect, _scrollPosition, scrollContentRect);

            float y = 0;

            var allMods = plan.Installs.Concat(plan.Updates);
            var groupedMods = allMods
                .GroupBy(m => m.GroupSlug)
                .OrderBy(g => g.First().IsRequired ? 0 : 1)
                .ThenBy(g => g.First().GroupName);

            foreach (var group in groupedMods)
            {
                var first = group.First();
                var tierLabel = first.IsRequired ? "(required)" : "(optional)";
                var slug = first.GroupSlug;
                bool collapsed = _groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug];
                var arrow = collapsed ? "▸" : "▾";

                if (GUI.Button(new Rect(0, y, innerWidth, LineHeight),
                    $"{arrow}  {first.GroupName}  {tierLabel}", _headerStyle))
                {
                    _groupCollapsed[slug] = !collapsed;
                }
                y += LineHeight;

                if (!collapsed)
                {
                    foreach (var mod in group)
                    {
                        var action = mod.OldVersion != null
                            ? $"(update from {mod.OldVersion})"
                            : "(new)";

                        if (first.IsRequired)
                        {
                            GUI.Label(new Rect(IndentWidth, y, innerWidth - IndentWidth, LineHeight),
                                $"{mod.Name} {mod.Version}  {action}", _modStyle);
                        }
                        else
                        {
                            bool isChecked = _modChecked.ContainsKey(mod.Id) && _modChecked[mod.Id];
                            bool newChecked = GUI.Toggle(
                                new Rect(IndentWidth, y, innerWidth - IndentWidth, LineHeight),
                                isChecked, $" {mod.Name} {mod.Version}  {action}");
                            _modChecked[mod.Id] = newChecked;
                        }
                        y += LineHeight;
                    }
                }
                y += 4f;
            }

            if (plan.Removals.Count > 0)
            {
                bool collapsed = _groupCollapsed.ContainsKey("__removals") && _groupCollapsed["__removals"];
                var arrow = collapsed ? "▸" : "▾";

                if (GUI.Button(new Rect(0, y, innerWidth, LineHeight),
                    $"{arrow}  Removing", _headerStyle))
                {
                    _groupCollapsed["__removals"] = !collapsed;
                }
                y += LineHeight;

                if (!collapsed)
                {
                    foreach (var mod in plan.Removals)
                    {
                        GUI.Label(new Rect(IndentWidth, y, innerWidth - IndentWidth, LineHeight),
                            $"{mod.Name} {mod.Version}", _modStyle);
                        y += LineHeight;
                    }
                }
            }

            GUI.EndScrollView();

            float buttonY = panelY + panelHeight - buttonHeight;
            float buttonWidth = 120f;
            float buttonSpacing = 20f;
            float buttonsWidth = buttonWidth * 2 + buttonSpacing;
            float buttonStartX = panelX + (PanelWidth - buttonsWidth) / 2f;

            if (GUI.Button(new Rect(buttonStartX, buttonY, buttonWidth, 32f), "Confirm"))
                OnConfirm();

            if (GUI.Button(new Rect(buttonStartX + buttonWidth + buttonSpacing, buttonY, buttonWidth, 32f), "Skip Sync"))
                OnSkipSync();
        }

        private void DrawRestart()
        {
            const float popupWidth = 400f;
            const float popupHeight = 160f;
            float popupX = (Screen.width - popupWidth) / 2f;
            float popupY = (Screen.height - popupHeight) / 2f;

            GUI.Box(new Rect(popupX, popupY, popupWidth, popupHeight), "", _panelStyle);

            var msgStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 1f) }
            };

            GUI.Label(new Rect(popupX, popupY + Padding, popupWidth, 30f),
                "Restart Required", _titleStyle);
            GUI.Label(new Rect(popupX + Padding, popupY + 50f, popupWidth - Padding * 2, 50f),
                "Mods have been updated. Restart the game for changes to take effect.", msgStyle);

            const float btnWidth = 130f;
            const float btnSpacing = 20f;
            float btnY = popupY + popupHeight - 44f;
            float btnStartX = popupX + (popupWidth - btnWidth * 2 - btnSpacing) / 2f;

            if (GUI.Button(new Rect(btnStartX, btnY, btnWidth, 32f), "Restart Now"))
                Application.Quit();

            if (GUI.Button(new Rect(btnStartX + btnWidth + btnSpacing, btnY, btnWidth, 32f), "Continue Anyway"))
            {
                ShowStatus("Convoy: mods updated — restart required", Color.yellow, 20f);
                _state = PluginState.Complete;
            }
        }

        private float CalculateContentHeight(SyncPlan plan)
        {
            float height = 0;
            var allMods = plan.Installs.Concat(plan.Updates);
            foreach (var group in allMods.GroupBy(m => m.GroupSlug))
            {
                height += LineHeight;
                var slug = group.First().GroupSlug;
                if (!(_groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug]))
                    height += group.Count() * LineHeight;
                height += 4f;
            }
            if (plan.Removals.Count > 0)
            {
                height += LineHeight;
                if (!(_groupCollapsed.ContainsKey("__removals") && _groupCollapsed["__removals"]))
                    height += plan.Removals.Count * LineHeight;
            }
            return height;
        }

        #endregion

        private static string FormatProgress(SyncProgress progress)
        {
            var phase = progress.Phase;
            var total = progress.TotalBytes;
            var received = progress.BytesReceived;

            if (total > 0)
                return $"Convoy: {phase} {FormatBytes(received)} / {FormatBytes(total)}";

            return $"Convoy: {phase}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
