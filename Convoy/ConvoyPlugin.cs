using BepInEx;
using UnityEngine;

namespace Convoy
{
    [BepInPlugin("io.cebarks.convoy", "Convoy", VersionInfo.Version)]
    public class ConvoyPlugin : BaseUnityPlugin
    {
        private string? _statusText;
        private float _statusExpiry;
        private Color _statusColor;

        private void Awake()
        {
            var config = new ConvoyConfig(Config);
            var engine = new SyncEngine(Logger, config);
            var result = engine.Run();

            switch (result)
            {
                case SyncResult.Failed:
                    Logger.LogError("Convoy sync failed — check log above for details");
                    ShowStatus("Convoy: sync failed — check BepInEx log", Color.red, 15f);
                    break;
                case SyncResult.RestartRequired:
                    ShowStatus("Convoy: mods updated — restart required", Color.yellow, 20f);
                    break;
                default:
                    ShowStatus("Convoy: mods up to date", Color.green, 5f);
                    break;
            }
        }

        private void ShowStatus(string text, Color color, float duration)
        {
            _statusText = text;
            _statusColor = color;
            _statusExpiry = Time.realtimeSinceStartup + duration;
        }

        private void OnGUI()
        {
            if (_statusText == null || Time.realtimeSinceStartup > _statusExpiry)
            {
                _statusText = null;
                return;
            }

            var remaining = _statusExpiry - Time.realtimeSinceStartup;
            var alpha = remaining < 2f ? remaining / 2f : 1f; // ponytail: fade out over last 2s

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(_statusColor.r, _statusColor.g, _statusColor.b, alpha) }
            };

            // drop shadow for readability
            var rect = new Rect(12, 12, 600, 40);
            var shadowStyle = new GUIStyle(style)
            {
                normal = { textColor = new Color(0, 0, 0, alpha * 0.8f) }
            };
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), _statusText, shadowStyle);
            GUI.Label(rect, _statusText, style);
        }
    }
}
