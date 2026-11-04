using UnityEditor;

namespace Haiven.Demo.BossRoom.Editor
{
    /// <summary>
    /// Haiven > P2P Transport switches Boss Room's Direct IP flow onto the Haiven
    /// WebRTC transport. EditorPrefs are per user, so Multiplayer Play Mode
    /// players on this machine follow the same switch.
    /// </summary>
    internal static class HaivenP2PMenu
    {
        // Matches HaivenP2PBoot.EditorPrefsKey.
        private const string Key = "Haiven.Demo.BossRoom.P2P";
        private const string Item = "Haiven/P2P Transport";

        [MenuItem(Item)]
        private static void Toggle() => EditorPrefs.SetBool(Key, !EditorPrefs.GetBool(Key, false));

        [MenuItem(Item, true)]
        private static bool Validate()
        {
            Menu.SetChecked(Item, EditorPrefs.GetBool(Key, false));
            return true;
        }
    }
}
