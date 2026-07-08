using System.Threading;
using BepInEx;
using UnityEngine;

namespace Convoy
{
    [BepInPlugin("io.cebarks.convoy", "Convoy", VersionInfo.Version)]
    public class ConvoyPlugin : BaseUnityPlugin
    {
        private SyncProgress? _progress;
        private Thread? _syncThread;

        private string? _statusText;
        private float _statusExpiry;
        private Color _statusColor;

        private void Awake()
        {
            var config = new ConvoyConfig(Config);
            var engine = new SyncEngine(Logger, config);
            var progress = new SyncProgress();
            _progress = progress;

            _syncThread = new Thread(() => engine.Run(progress))
            {
                Name = "ConvoySync",
                IsBackground = true
            };
            _syncThread.Start();
        }

        private void Update()
        {
            if (_progress == null || !_progress.IsComplete)
                return;

            switch (_progress.Result)
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

            _syncThread = null;
            _progress = null;
        }

        private void ShowStatus(string text, Color color, float duration)
        {
            _statusText = text;
            _statusColor = color;
            _statusExpiry = Time.realtimeSinceStartup + duration;
        }

        private void OnGUI()
        {
            string? text = null;
            Color color = Color.green;

            var progress = _progress;
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
