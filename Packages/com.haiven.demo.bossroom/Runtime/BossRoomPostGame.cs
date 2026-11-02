#nullable enable
using System;
using System.Reflection;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Reads Boss Room's post-game result without a compile-time reference to
    /// Boss Room. ServerBossRoomState loads the <see cref="SceneName"/> scene
    /// after the boss dies (Win) or every player faints (Loss), and the scene's
    /// <c>NetworkPostGame.WinState</c> network variable carries which one. That
    /// scene load is the one success signal every peer sees; the pot pick-up
    /// runs only on the server.
    /// </summary>
    internal static class BossRoomPostGame
    {
        public const string SceneName = "PostGame";

        /// <summary>How long to wait for the network variable to leave Invalid.</summary>
        public const float WatchSeconds = 10f;

        /// <summary>Seconds between reads while watching.</summary>
        public const float PollSeconds = 0.25f;

        private const string NetworkPostGameType =
            "Unity.BossRoom.Gameplay.GameState.NetworkPostGame, Unity.BossRoom.Gameplay";

        private static Type? networkPostGame;
        private static bool resolved;

        /// <summary>
        /// The current <c>WinState</c> name ("Win", "Loss", "Invalid"), or null
        /// when no post-game object is loaded or Boss Room's shape differs.
        /// </summary>
        public static string? ReadWinState()
        {
            var type = Resolve();
            if (type == null) return null;
            var field = type.GetField("WinState", BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return null;
            foreach (var found in UnityEngine.Object.FindObjectsOfType(type))
            {
                var variable = field.GetValue(found);
                var value = variable?.GetType()
                    .GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(variable);
                if (value != null) return value.ToString();
            }
            return null;
        }

        private static Type? Resolve()
        {
            if (resolved) return networkPostGame;
            resolved = true;
            networkPostGame = Type.GetType(NetworkPostGameType, false);
            return networkPostGame;
        }
    }
}
