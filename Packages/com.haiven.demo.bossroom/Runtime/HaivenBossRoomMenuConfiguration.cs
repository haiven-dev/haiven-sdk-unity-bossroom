#nullable enable
using System;
using UnityEngine;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Host discovery and retained prefab styling values for the Boss Room demo.
    /// The stock settings dialog remains game-only; Haiven uses these names only
    /// to align with its safe-area shell without adding controls to the host panel.
    /// Dialog colors are sampled from Boss Room v2.5.0 <c>ui_dialog</c>,
    /// <c>ui_btn_blank</c>, <c>inputfield_Blank</c>, and the settings-label gold.
    /// The fallback does not copy those textures.
    /// </summary>
    [Serializable]
    public sealed class HaivenBossRoomMenuConfiguration
    {
        // ui_dialog.png center fill (9, 6, 1). The old blue-black read as a different HUD.
        internal static readonly Color DialogFill = new Color(9f / 255f, 6f / 255f, 1f / 255f, 1f);
        // Same fill at the MOTD's accepted contrast over the host menu art.
        internal static readonly Color DialogScrim = new Color(9f / 255f, 6f / 255f, 1f / 255f, 0.94f);
        // ui_dialog.png top band (81, 48, 11) and side band (47, 28, 7).
        internal static readonly Color DialogBorder = new Color(81f / 255f, 48f / 255f, 11f / 255f, 1f);
        internal static readonly Color DialogBorderShade = new Color(47f / 255f, 28f / 255f, 7f / 255f, 1f);
        // ui_btn_blank.png navy well (20, 36, 52), side bronze (121, 68, 0), lit edge (152, 106, 15).
        internal static readonly Color ButtonFill = new Color(20f / 255f, 36f / 255f, 52f / 255f, 1f);
        internal static readonly Color ButtonBorder = new Color(121f / 255f, 68f / 255f, 0f / 255f, 1f);
        internal static readonly Color ButtonHighlight = new Color(152f / 255f, 106f / 255f, 15f / 255f, 1f);
        // inputfield_Blank.png well (2, 7, 14). Settings labels are (235, 209, 63).
        internal static readonly Color InputFill = new Color(2f / 255f, 7f / 255f, 14f / 255f, 1f);
        internal static readonly Color GoldText = new Color(235f / 255f, 209f / 255f, 63f / 255f, 1f);

        public bool IntegrateWithHostMenu = true;
        public string HostCanvasName = "SettingsPanelCanvas";
        public string HostPanelName = "SettingsPanel";
        public Vector2 Anchor = Vector2.one;
        public Vector2 Position = new Vector2(-690, -160);
        public Vector2 Size = new Vector2(640, 620);
        public Vector2 MotdPosition = new Vector2(20, -72);
        public Vector2 MotdSize = new Vector2(600, 230);
        public Vector2 RewardsPosition = new Vector2(20, -322);
        public Vector2 RewardsSize = new Vector2(600, 250);
        public Color PanelColor = DialogFill;
        public Color SurfaceColor = ButtonFill;
        public Color TextColor = new Color(1f, 1f, 1f, 1f);
        public Color AccentColor = GoldText;
        public Font? Font;
        public int HeadingFontSize = 24;
        public int BodyFontSize = 18;
        public bool UseHostFont = true;
        public bool ShowRewardsHandleWhenHostMenuMissing = false;

        public bool HasHostBindingNames =>
            !string.IsNullOrWhiteSpace(HostCanvasName) && !string.IsNullOrWhiteSpace(HostPanelName);
    }
}
