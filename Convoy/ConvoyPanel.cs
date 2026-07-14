using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Convoy
{
    public class ConvoyPanel
    {
        private enum Tab { Status, Mods }

        private readonly ConfigEntry<KeyboardShortcut> _keybind;
        private readonly Action _onSyncRequested;
        private readonly Func<bool> _isSyncInProgress;
        private readonly Func<bool> _isInRaid;

        private bool _open;
        private Tab _activeTab = Tab.Status;

        private ConvoyState _state = new ConvoyState();
        private SyncOutcome? _outcome;
        private Catalog? _catalog;

        // Group collapse state (shared across tabs)
        private readonly Dictionary<string, bool> _groupCollapsed = new Dictionary<string, bool>();

        // Mods tab: per-mod checkbox state
        private readonly Dictionary<int, bool> _modChecked = new Dictionary<int, bool>();
        private bool _modsDirty;
        private bool _stateDirty;

        // Scroll positions per tab
        private Vector2 _statusScroll;
        private Vector2 _modsScroll;

        // Cached IMGUI resources
        private Texture2D? _overlayTex;
        private Texture2D? _panelBgTex;
        private Texture2D? _tabActiveTex;
        private GUIStyle? _panelStyle;
        private GUIStyle? _titleStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _modStyle;
        private GUIStyle? _tabStyle;
        private GUIStyle? _tabActiveStyle;
        private GUIStyle? _infoStyle;
        private GUIStyle? _infoValueStyle;

        private const float PanelWidth = 500f;
        private const float PanelMaxHeight = 600f;
        private const float LineHeight = 26f;
        private const float Padding = 16f;
        private const float IndentWidth = 28f;
        private const float TabHeight = 32f;

        public bool IsOpen => _open;

        public ConvoyPanel(
            ConfigEntry<KeyboardShortcut> keybind,
            Action onSyncRequested,
            Func<bool> isSyncInProgress,
            Func<bool> isInRaid)
        {
            _keybind = keybind;
            _onSyncRequested = onSyncRequested;
            _isSyncInProgress = isSyncInProgress;
            _isInRaid = isInRaid;
        }

        public void UpdateState(ConvoyState state)
        {
            _state = state;
            RebuildModChecks();
        }

        public void UpdateOutcome(SyncOutcome outcome) => _outcome = outcome;

        public void UpdateCatalog(Catalog? catalog) => _catalog = catalog;

        public void Close() => _open = false;

        public void HandleKeybind()
        {
            if (_keybind.Value.IsDown())
                _open = !_open;
        }

        // Called from Update(), not OnGUI() — avoids disk writes on every IMGUI repaint
        public void FlushIfDirty()
        {
            if (!_stateDirty) return;
            _stateDirty = false;
            _state.Save();
        }

        private void RebuildModChecks()
        {
            _modChecked.Clear();
            if (_catalog == null) return;
            foreach (var group in _catalog.Groups.Where(g => g.Tier == "optional"))
            {
                if (!_state.EnabledGroups.Contains(group.Slug)) continue;
                foreach (var mod in group.Mods)
                    _modChecked[mod.Id] = !_state.SkippedMods.Contains(mod.Id);
            }
        }

        private void EnsureStyles()
        {
            if (_panelStyle != null) return;

            _overlayTex = new Texture2D(1, 1);
            _overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
            _overlayTex.Apply();

            _panelBgTex = new Texture2D(1, 1);
            _panelBgTex.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.95f));
            _panelBgTex.Apply();

            _tabActiveTex = new Texture2D(1, 1);
            _tabActiveTex.SetPixel(0, 0, new Color(0.25f, 0.25f, 0.25f, 1f));
            _tabActiveTex.Apply();

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
            _tabStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 1f) }
            };
            _tabActiveStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white, background = _tabActiveTex }
            };
            _infoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 1f) }
            };
            _infoValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) }
            };
        }

        public void DrawPanel()
        {
            if (!_open) return;
            EnsureStyles();

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _overlayTex!);

            float panelHeight = PanelMaxHeight;
            float panelX = (Screen.width - PanelWidth) / 2f;
            float panelY = (Screen.height - panelHeight) / 2f;
            var panelRect = new Rect(panelX, panelY, PanelWidth, panelHeight);

            GUI.Box(panelRect, "", _panelStyle!);

            // Title
            float y = panelY + Padding;
            GUI.Label(new Rect(panelX, y, PanelWidth, 30f), "Convoy", _titleStyle!);

            // Close button
            if (GUI.Button(new Rect(panelX + PanelWidth - 36, y, 24, 24), "X"))
                _open = false;

            y += 36f;

            // Tab bar
            float tabWidth = (PanelWidth - Padding * 2) / 2f;
            float tabX = panelX + Padding;
            if (GUI.Button(new Rect(tabX, y, tabWidth, TabHeight), "Status",
                _activeTab == Tab.Status ? _tabActiveStyle! : _tabStyle!))
                _activeTab = Tab.Status;
            if (GUI.Button(new Rect(tabX + tabWidth, y, tabWidth, TabHeight), "Mods",
                _activeTab == Tab.Mods ? _tabActiveStyle! : _tabStyle!))
                _activeTab = Tab.Mods;

            y += TabHeight + 8f;

            float contentTop = y;
            float buttonAreaHeight = _activeTab == Tab.Mods ? 48f : 0f;
            float contentHeight = panelY + panelHeight - contentTop - Padding - buttonAreaHeight;

            switch (_activeTab)
            {
                case Tab.Status:
                    DrawStatusTab(panelX, contentTop, contentHeight);
                    break;
                case Tab.Mods:
                    DrawModsTab(panelX, contentTop, contentHeight);
                    DrawSyncButton(panelX, contentTop + contentHeight);
                    break;
            }
        }

        private void DrawStatusTab(float panelX, float top, float height)
        {
            float innerWidth = PanelWidth - Padding * 2;
            float y = 0f;

            // Header info
            float headerHeight = 0f;
            var lines = new List<(string label, string value, Color color)>();

            lines.Add(("Convoy", VersionInfo.Version, Color.white));
            if (_outcome?.QuartermasterVersion != null)
                lines.Add(("Quartermaster", _outcome.QuartermasterVersion, Color.white));
            if (_outcome?.SptVersion != null)
                lines.Add(("SPT", _outcome.SptVersion, Color.white));
            if (_outcome?.ServerUrl != null)
                lines.Add(("Server", _outcome.ServerUrl, Color.white));

            if (_outcome != null)
            {
                var (label, color) = _outcome.Result switch
                {
                    SyncResult.UpToDate => ("Up to date", Color.green),
                    SyncResult.RestartRequired => ("Updated — restart required", Color.yellow),
                    SyncResult.Failed => ("Failed", Color.red),
                    _ => ("Unknown", Color.white)
                };
                lines.Add(("Last Sync", label, color));
                if (_outcome.Result == SyncResult.Failed && !string.IsNullOrEmpty(_outcome.Error))
                    lines.Add(("Error", _outcome.Error!, Color.red));
            }

            headerHeight = lines.Count * LineHeight + 8f;

            // Mod listing
            float modListHeight = CalculateModListHeight();
            float totalContent = headerHeight + modListHeight;

            var scrollRect = new Rect(panelX + Padding, top, innerWidth, height);
            var contentRect = new Rect(0, 0, innerWidth - 20f, totalContent);
            _statusScroll = GUI.BeginScrollView(scrollRect, _statusScroll, contentRect);

            float labelWidth = 120f;
            foreach (var (label, value, color) in lines)
            {
                GUI.Label(new Rect(0, y, labelWidth, LineHeight), label, _infoStyle!);
                var valueStyle = new GUIStyle(_infoValueStyle!) { normal = { textColor = color } };
                GUI.Label(new Rect(labelWidth, y, innerWidth - labelWidth - 20f, LineHeight), value, valueStyle);
                y += LineHeight;
            }
            y += 8f;

            // Installed mods grouped
            DrawModList(y, innerWidth - 20f, readOnly: true);

            GUI.EndScrollView();
        }

        private void DrawModsTab(float panelX, float top, float height)
        {
            float innerWidth = PanelWidth - Padding * 2;
            bool inRaid = _isInRaid();

            if (inRaid)
            {
                GUI.Label(new Rect(panelX + Padding, top, innerWidth, LineHeight),
                    "(read-only in raid)", _infoStyle!);
                top += LineHeight;
                height -= LineHeight;
            }

            if (_modsDirty)
            {
                var dirtyStyle = new GUIStyle(_infoStyle!) { normal = { textColor = Color.yellow } };
                GUI.Label(new Rect(panelX + Padding, top, innerWidth, LineHeight),
                    "Changes pending — sync to apply", dirtyStyle);
                top += LineHeight;
                height -= LineHeight;
            }

            float contentHeight = CalculateModsTabHeight();
            var scrollRect = new Rect(panelX + Padding, top, innerWidth, height);
            var contentRect = new Rect(0, 0, innerWidth - 20f, contentHeight);
            _modsScroll = GUI.BeginScrollView(scrollRect, _modsScroll, contentRect);

            DrawModsTabContent(innerWidth - 20f, inRaid);

            GUI.EndScrollView();
        }

        private void DrawModList(float startY, float width, bool readOnly)
        {
            if (_catalog == null) return;
            float y = startY;

            var installedIds = new HashSet<int>(_state.Mods.Select(m => m.Id));
            var installedVersions = _state.Mods.ToDictionary(m => m.Id);

            foreach (var group in _catalog.Groups.OrderBy(g => g.Tier == "required" ? 0 : 1).ThenBy(g => g.Name))
            {
                var modsInGroup = group.Mods.Where(m => installedIds.Contains(m.Id)).ToList();
                if (modsInGroup.Count == 0) continue;

                var slug = group.Slug;
                var tierLabel = group.Tier == "required" ? "(required)" : "(optional)";
                bool collapsed = _groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug];
                var arrow = collapsed ? "▸" : "▾";

                if (GUI.Button(new Rect(0, y, width, LineHeight),
                    $"{arrow}  {group.Name}  {tierLabel} — {modsInGroup.Count} mods", _headerStyle!))
                    _groupCollapsed[slug] = !collapsed;
                y += LineHeight;

                if (!collapsed)
                {
                    foreach (var mod in modsInGroup)
                    {
                        var fileCount = installedVersions.TryGetValue(mod.Id, out var ms) ? ms.Files.Count : 0;
                        GUI.Label(new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                            $"{mod.Name} v{mod.Version} — {fileCount} files", _modStyle!);
                        y += LineHeight;
                    }
                }
                y += 4f;
            }
        }

        private void DrawModsTabContent(float width, bool inRaid)
        {
            if (_catalog == null) return;
            float y = 0f;

            foreach (var group in _catalog.Groups.OrderBy(g => g.Tier == "required" ? 0 : 1).ThenBy(g => g.Name))
            {
                bool isRequired = group.Tier == "required";
                var slug = group.Slug;
                bool collapsed = _groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug];
                var arrow = collapsed ? "▸" : "▾";
                var tierLabel = isRequired ? "(required)" : "(optional)";

                // Collapse arrow (clickable for all group types)
                if (GUI.Button(new Rect(0, y, IndentWidth, LineHeight), arrow, _headerStyle!))
                    _groupCollapsed[slug] = !collapsed;

                if (isRequired)
                {
                    GUI.Label(new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                        $"{group.Name}  {tierLabel}", _headerStyle!);
                }
                else
                {
                    bool groupEnabled = _state.EnabledGroups.Contains(slug);
                    bool newGroupEnabled = inRaid ? groupEnabled :
                        GUI.Toggle(new Rect(IndentWidth, y, width - IndentWidth, LineHeight), groupEnabled,
                            $" {group.Name}  {tierLabel}");
                    if (newGroupEnabled != groupEnabled && !inRaid)
                    {
                        if (newGroupEnabled)
                            _state.EnabledGroups.Add(slug);
                        else
                            _state.EnabledGroups.Remove(slug);
                        RebuildModChecks();
                        _modsDirty = true;
                        _stateDirty = true;
                    }
                }
                y += LineHeight;

                if (!collapsed)
                {
                    foreach (var mod in group.Mods)
                    {
                        if (isRequired)
                        {
                            GUI.Label(new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                                $"{mod.Name} v{mod.Version}", _modStyle!);
                        }
                        else
                        {
                            bool isChecked = _modChecked.ContainsKey(mod.Id) && _modChecked[mod.Id];
                            bool enabled = _state.EnabledGroups.Contains(slug);
                            if (!enabled)
                            {
                                GUI.Label(new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                                    $"{mod.Name} v{mod.Version}", _modStyle!);
                            }
                            else if (inRaid)
                            {
                                GUI.Toggle(new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                                    isChecked, $" {mod.Name} v{mod.Version}");
                            }
                            else
                            {
                                bool newChecked = GUI.Toggle(
                                    new Rect(IndentWidth, y, width - IndentWidth, LineHeight),
                                    isChecked, $" {mod.Name} v{mod.Version}");
                                if (newChecked != isChecked)
                                {
                                    _modChecked[mod.Id] = newChecked;
                                    if (newChecked)
                                        _state.SkippedMods.Remove(mod.Id);
                                    else
                                        _state.SkippedMods.Add(mod.Id);
                                    _modsDirty = true;
                                    _stateDirty = true;
                                }
                            }
                        }
                        y += LineHeight;
                    }
                }
                y += 4f;
            }
        }

        private void DrawSyncButton(float panelX, float top)
        {
            float buttonWidth = 140f;
            float buttonX = panelX + (PanelWidth - buttonWidth) / 2f;
            bool inRaid = _isInRaid();
            bool syncing = _isSyncInProgress();

            if (inRaid) return;

            var label = syncing ? "Syncing..." : "Sync Now";
            if (!syncing && GUI.Button(new Rect(buttonX, top + 8f, buttonWidth, 32f), label))
            {
                _onSyncRequested();
                _modsDirty = false;
            }

            if (syncing)
                GUI.Label(new Rect(buttonX, top + 8f, buttonWidth, 32f), label, _tabStyle!);
        }

        private float CalculateModListHeight()
        {
            if (_catalog == null) return 0f;
            float height = 0f;
            var installedIds = new HashSet<int>(_state.Mods.Select(m => m.Id));

            foreach (var group in _catalog.Groups)
            {
                var modsInGroup = group.Mods.Where(m => installedIds.Contains(m.Id)).ToList();
                if (modsInGroup.Count == 0) continue;
                height += LineHeight;
                var slug = group.Slug;
                if (!(_groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug]))
                    height += modsInGroup.Count * LineHeight;
                height += 4f;
            }
            return height;
        }

        private float CalculateModsTabHeight()
        {
            if (_catalog == null) return 0f;
            float height = 0f;
            foreach (var group in _catalog.Groups)
            {
                height += LineHeight;
                var slug = group.Slug;
                if (!(_groupCollapsed.ContainsKey(slug) && _groupCollapsed[slug]))
                    height += group.Mods.Count * LineHeight;
                height += 4f;
            }
            return height;
        }
    }
}
