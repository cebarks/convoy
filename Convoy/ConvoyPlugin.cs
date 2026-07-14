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
        private enum PluginState { Idle, Planning, AwaitingConfirmation, Executing, Complete }

        private ConvoyConfig? _config;
        private SyncEngine? _engine;
        private PluginState _state = PluginState.Idle;

        // Planning phase
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
        private GUIStyle? _panelStyle;
        private GUIStyle? _titleStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _modStyle;

        private const float PanelWidth = 500f;
        private const float PanelMaxHeight = 600f;
        private const float LineHeight = 26f;
        private const float Padding = 16f;
        private const float IndentWidth = 28f;

        private void Awake()
        {
            _config = new ConvoyConfig(Config);
            _config.RegisterDebugEntries();
            _engine = new SyncEngine(Logger, _config);
            StartPlanning();
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
            switch (_state)
            {
                case PluginState.Planning:
                    if (_planProgress != null && _planProgress.IsComplete)
                    {
                        if (_planProgress.Result == SyncResult.Failed)
                        {
                            UpdateDebugState(SyncResult.Failed, _planProgress.Error, _pendingPlan);
                            ShowStatus("Convoy: sync failed — check BepInEx log", Color.red, 15f);
                            _state = PluginState.Complete;
                        }
                        else if (_pendingPlan == null ||
                                 (_pendingPlan.Installs.Count == 0 && _pendingPlan.Updates.Count == 0 && _pendingPlan.Removals.Count == 0))
                        {
                            UpdateDebugState(SyncResult.UpToDate, null, _pendingPlan);
                            ShowStatus("Convoy: mods up to date", Color.green, 5f);
                            _state = PluginState.Complete;
                        }
                        else
                        {
                            _plan = _pendingPlan;
                            UpdateDebugState(SyncResult.UpToDate, null, _plan);
                            InitConfirmationUI(_plan);
                            _state = PluginState.AwaitingConfirmation;
                        }
                        _planThread = null;
                        _planProgress = null;
                        _pendingPlan = null;
                    }
                    break;

                case PluginState.Executing:
                    if (_execProgress != null && _execProgress.IsComplete)
                    {
                        switch (_execProgress.Result)
                        {
                            case SyncResult.Failed:
                                ShowStatus("Convoy: sync failed — check BepInEx log", Color.red, 15f);
                                break;
                            case SyncResult.RestartRequired:
                                ShowStatus("Convoy: mods updated — restart required", Color.yellow, 20f);
                                break;
                            default:
                                ShowStatus("Convoy: mods up to date", Color.green, 5f);
                                break;
                        }
                        UpdateDebugState(_execProgress.Result ?? SyncResult.UpToDate, _execProgress.Error, _plan);
                        _execThread = null;
                        _execProgress = null;
                        _plan = null;
                        _state = PluginState.Complete;
                    }
                    break;
            }

            if (_config?.SyncNow != null && _config.SyncNow.Value)
            {
                _config.SyncNow.Value = false;
                if (_state == PluginState.Planning || _state == PluginState.Executing)
                {
                    Logger.LogWarning("Convoy: sync already in progress, ignoring manual trigger");
                }
                else
                {
                    Logger.LogInfo("Convoy: manual sync triggered");
                    StartPlanning();
                }
            }
        }

        private void UpdateDebugState(SyncResult result, string? error = null, SyncPlan? plan = null)
        {
            if (_config == null) return;
            _config.UpdateDebugState(new SyncOutcome
            {
                Result = result,
                Error = error,
                SptVersion = plan?.Catalog.SptVersion,
                QuartermasterVersion = plan?.Catalog.QuartermasterVersion,
                ServerUrl = plan?.ServerUrl
            });
        }

        private void InitConfirmationUI(SyncPlan plan)
        {
            _groupCollapsed.Clear();
            _modChecked.Clear();
            _scrollPosition = Vector2.zero;

            var slugs = plan.Installs.Concat(plan.Updates).Concat(plan.Skipped)
                .Select(m => m.GroupSlug).Distinct();
            foreach (var slug in slugs)
                _groupCollapsed[slug] = false;
            if (plan.Removals.Count > 0)
                _groupCollapsed["__removals"] = false;

            foreach (var mod in plan.Installs.Concat(plan.Updates).Where(m => !m.IsRequired))
                _modChecked[mod.Id] = true;
            foreach (var mod in plan.Skipped)
                _modChecked[mod.Id] = false;
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

            // Preserve existing skips for mods not shown in UI, apply UI changes on top
            var skippedModIds = new HashSet<int>(plan.State.SkippedMods);
            foreach (var kvp in _modChecked)
            {
                if (kvp.Value)
                    skippedModIds.Remove(kvp.Key);
                else
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

        private void ShowStatus(string text, Color color, float duration)
        {
            _statusText = text;
            _statusColor = color;
            _statusExpiry = Time.realtimeSinceStartup + duration;
        }

        #region OnGUI

        private void OnGUI()
        {
            if (_state == PluginState.AwaitingConfirmation && _plan != null)
            {
                DrawConfirmationPanel();
                return;
            }

            string? text = null;
            Color color = Color.green;

            var progress = _state == PluginState.Planning ? _planProgress :
                           _state == PluginState.Executing ? _execProgress : null;

            if (progress != null && !progress.IsComplete)
            {
                text = FormatProgress(progress);
                color = Color.green;
            }
            else if (_statusText != null)
            {
                if (Time.realtimeSinceStartup > _statusExpiry)
                {
                    _statusText = null;
                    return;
                }
                text = _statusText;
                color = _statusColor;
            }

            if (text == null)
                return;

            var remaining = _statusText != null ? _statusExpiry - Time.realtimeSinceStartup : 999f;
            var alpha = remaining < 2f ? remaining / 2f : 1f;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(color.r, color.g, color.b, alpha) }
            };

            var rect = new Rect(12, 12, 800, 40);
            var shadowStyle = new GUIStyle(style)
            {
                normal = { textColor = new Color(0, 0, 0, alpha * 0.8f) }
            };
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, shadowStyle);
            GUI.Label(rect, text, style);
        }

        private void DrawConfirmationPanel()
        {
            var plan = _plan!;

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

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _overlayTex);

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

            var allMods = plan.Installs.Concat(plan.Updates).Concat(plan.Skipped);
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

        private float CalculateContentHeight(SyncPlan plan)
        {
            float height = 0;
            var allMods = plan.Installs.Concat(plan.Updates).Concat(plan.Skipped);
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
