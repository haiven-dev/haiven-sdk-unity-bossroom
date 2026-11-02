#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Observes Boss Room's stock settings shell so Haiven can share its safe-area
    /// geometry without taking ownership of the gear dialog. Chat, rewards, sign-in,
    /// and MOTD remain independent overlay components on the Haiven canvas.
    /// </summary>
    internal sealed class HaivenBossRoomHostSurface : IDisposable
    {
        internal const string CanvasName = "SettingsPanelCanvas";
        internal const string PanelName = "SettingsPanel";
        internal const string SettingsButtonName = "Settings Button";
        internal const float ShellMargin = 24f;
        internal const float MaxShellWidth = 640f;
        internal const float MaxShellHeight = 680f;

        private readonly Canvas canvas;
        private readonly RectTransform settingsPanel;
        private readonly Button settingsButton;

        private HaivenBossRoomHostSurface(
            Canvas canvas,
            RectTransform settingsPanel,
            Button settingsButton)
        {
            this.canvas = canvas;
            this.settingsPanel = settingsPanel;
            this.settingsButton = settingsButton;
            ApplySafeArea();
        }

        internal bool IsAvailable => canvas != null && settingsPanel != null && settingsButton != null;

        internal static HaivenBossRoomHostSurface? TryCreate(HaivenBossRoomMenuConfiguration menu)
        {
            if (!menu.IntegrateWithHostMenu || !menu.HasHostBindingNames) return null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (candidate == null || candidate.gameObject.name != menu.HostCanvasName
                    || !candidate.gameObject.scene.IsValid())
                    continue;
                var panel = candidate.transform.Find(menu.HostPanelName) as RectTransform;
                var settings = candidate.transform.Find(SettingsButtonName)?.GetComponent<Button>();
                if (panel == null || settings == null) continue;
                return new HaivenBossRoomHostSurface(candidate, panel, settings);
            }
            return null;
        }

        internal void Tick()
        {
            if (IsAvailable) ApplySafeArea();
        }

        private void ApplySafeArea()
        {
            var scale = canvas.scaleFactor <= 0 ? 1f : canvas.scaleFactor;
            var safe = Screen.safeArea;
            var safeSize = new Vector2(safe.width / scale, safe.height / scale);
            settingsPanel.anchorMin = settingsPanel.anchorMax = settingsPanel.pivot = new Vector2(0.5f, 0.5f);
            settingsPanel.sizeDelta = ClampShellSize(safeSize);
            settingsPanel.anchoredPosition = SafeAreaCenterOffset(
                safe,
                new Vector2(Screen.width, Screen.height),
                scale);
        }

        internal static Vector2 ClampShellSize(Vector2 safeSize) => new Vector2(
            Mathf.Max(0, Mathf.Min(MaxShellWidth, safeSize.x - ShellMargin * 2)),
            Mathf.Max(0, Mathf.Min(MaxShellHeight, safeSize.y - ShellMargin * 2)));

        internal static Vector2 SafeAreaCenterOffset(Rect safeArea, Vector2 screenSize, float scale)
        {
            var resolvedScale = scale <= 0 ? 1f : scale;
            return new Vector2(
                (safeArea.center.x - screenSize.x * 0.5f) / resolvedScale,
                (safeArea.center.y - screenSize.y * 0.5f) / resolvedScale);
        }

        public void Dispose()
        {
            // This adapter deliberately owns no host controls or listeners.
        }
    }
}
