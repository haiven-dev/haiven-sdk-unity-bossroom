#nullable enable
using System;
using System.Reflection;
using UnityEngine;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>
    /// Starts Boss Room's own host or client flow, the one its Direct IP window
    /// uses, so character select, the game and the post-game screen run
    /// unchanged. The demo package does not reference Boss Room's assemblies,
    /// so the connection manager is found by name, as BossRoomPostGame does.
    /// The address and port reach only Unity Transport's settings, which the
    /// Haiven transport ignores.
    /// </summary>
    internal static class BossRoomConnection
    {
        internal const string ConnectionManagerType =
            "Unity.BossRoom.ConnectionManagement.ConnectionManager, Unity.BossRoom.ConnectionManagement";

        internal static bool IsAvailable => Find(out _, out _);

        internal static bool StartHost(string playerName) => Start("StartHostIp", playerName);

        internal static bool StartClient(string playerName) => Start("StartClientIp", playerName);

        private static bool Start(string method, string playerName)
        {
            if (!Find(out var manager, out var type)) return false;
            var start = type!.GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(string), typeof(int) }, null);
            if (start == null)
            {
                Debug.LogWarning($"[haiven-lobby] Boss Room's ConnectionManager has no {method}(string, string, int)");
                return false;
            }
            start.Invoke(manager, new object[] { playerName, "127.0.0.1", 9998 });
            return true;
        }

        private static bool Find(out UnityEngine.Object? manager, out Type? type)
        {
            manager = null;
            type = Type.GetType(ConnectionManagerType, false);
            if (type == null) return false;
            manager = UnityEngine.Object.FindFirstObjectByType(type);
            return manager != null;
        }
    }
}
