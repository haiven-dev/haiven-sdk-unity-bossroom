#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Haiven.Demo.BossRoom.P2P
{
    /// <summary>
    /// Puts <see cref="HaivenNetworkTransport"/> under Boss Room's NetworkManager
    /// when P2P is switched on, so Boss Room's "Direct IP" host and join run over
    /// WebRTC. The IP window's address and port then name the host's TCP
    /// signaling listener; game data goes over the data channels.
    ///
    /// Switched on by any of: the <c>-haiven-p2p</c> command-line argument,
    /// <c>HAIVEN_P2P=1</c>, or the editor menu Haiven > P2P Transport.
    /// <c>HAIVEN_P2P_TURN_URL</c> and <c>HAIVEN_P2P_TURN_SECRET</c> make every
    /// session <c>relay-only</c> through that TURN server, the way a session with
    /// a child runs. The credential is minted here only because this is a local
    /// test bed; in a Haiven session the game service issues it.
    /// The Multiplayer Services session flow keeps Unity Transport and does not
    /// work while P2P is on.
    /// </summary>
    public sealed class HaivenP2PBoot : MonoBehaviour
    {
        public const string EditorPrefsKey = "Haiven.Demo.BossRoom.P2P";

        public static bool Enabled
        {
            get
            {
                if (Environment.GetCommandLineArgs().Contains("-haiven-p2p")) return true;
                if (Environment.GetEnvironmentVariable("HAIVEN_P2P") == "1") return true;
#if UNITY_EDITOR
                if (UnityEditor.EditorPrefs.GetBool(EditorPrefsKey, false)) return true;
#endif
                return false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Enabled) return;
            var boot = new GameObject("Haiven P2P");
            DontDestroyOnLoad(boot);
            boot.AddComponent<HaivenP2PBoot>();
        }

        // The NetworkManager can come from a later scene, so wait for it.
        private void Update()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null) return;
            Install(manager);
            Destroy(gameObject);
        }

        public static HaivenNetworkTransport Install(NetworkManager manager)
        {
            var existing = manager.GetComponent<HaivenNetworkTransport>();
            if (existing != null) return existing;

            var unityTransport = manager.GetComponent<UnityTransport>();
            var transport = manager.gameObject.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = new TcpPeerSignaling(() => unityTransport != null
                ? (unityTransport.ConnectionData.Address, unityTransport.ConnectionData.Port)
                : ("127.0.0.1", (ushort)9998));
            transport.Connection = ConnectionFromEnvironment();
            // One TURN credential per connection: TURN server counts allocations per
            // username (user-quota), as it does for the game service's credentials.
            transport.CreateTransport = _ => new UnityWebRtcPeerTransport(ConnectionFromEnvironment());
            manager.NetworkConfig.NetworkTransport = transport;
            Debug.Log("[haiven-p2p] Boss Room runs over the Haiven WebRTC transport ("
                + (transport.Connection.Policy == PeerIcePolicy.Relay ? "relay-only" : "direct") + ")");
            return transport;
        }

        private static PeerConnectionOptions ConnectionFromEnvironment()
        {
            var url = Environment.GetEnvironmentVariable("HAIVEN_P2P_TURN_URL");
            var secret = Environment.GetEnvironmentVariable("HAIVEN_P2P_TURN_SECRET");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret))
                return new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

            // The TURN REST scheme the game service issues: username "<expiry>:<id>",
            // password base64(HMAC-SHA1(secret, username)).
            var username = $"{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}:{Guid.NewGuid()}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var password = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
            return new PeerConnectionOptions(
                new[] { new PeerIceServer(new[] { url! }, username, password) }, PeerIcePolicy.Relay);
        }
    }
}
