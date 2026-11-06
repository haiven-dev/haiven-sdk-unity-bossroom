#nullable enable
using System;
using System.Threading.Tasks;
using Haiven.Sdk;
using Unity.Netcode;
using UnityEngine;

namespace Haiven.Sdk.Samples.Netcode
{
    /// <summary>
    /// Runs Netcode for GameObjects over a Haiven P2P session: creates or joins
    /// it through <see cref="HaivenP2PSession"/>, puts
    /// <see cref="HaivenNetworkTransport"/> on the <see cref="NetworkManager"/>,
    /// and starts NGO as host or client.
    ///
    /// Give it the game socket with <see cref="Attach"/> after sign-in and
    /// again after every reconnect; the session resumes on the new socket.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class HaivenNetcodeSession : MonoBehaviour
    {
        private NetworkManager? manager;
        private HaivenNetworkTransport? transport;

        /// <summary>The controller; null until <see cref="Configure"/>.</summary>
        public HaivenP2PSession? Session { get; private set; }

        /// <summary>NGO stopped because the Haiven session ended: why, and the server's code when it gave one.</summary>
        public event Action<HaivenP2PEndReason, string?>? Ended;

        /// <summary>Sets the game this component creates and joins sessions for.</summary>
        public void Configure(HaivenSessionsClient sessions)
        {
            if (Session != null) throw new InvalidOperationException("haiven: already configured");
            Session = new HaivenP2PSession(sessions, () => Time.realtimeSinceStartupAsDouble);
            Session.Ended += OnEnded;
            Session.IceOptionsChanged += options =>
            {
                if (transport != null) transport.Connection = options;
            };
            Session.Diagnostic += (what, error) => Debug.LogWarning($"[haiven-p2p] {what}: {error.Message}");
        }

        /// <summary>Uses a signed-in game socket, opened with the game credential.</summary>
        public void Attach(HaivenSessionCore socket, string? connectionId = null) => Required().Attach(socket, connectionId);

        /// <summary>Creates a session and starts NGO as its host.</summary>
        public async Task<HaivenSessionAdmission> HostAsync(HaivenSessionCreateOptions options)
        {
            var admission = await Required().CreateAsync(options);
            Begin(admission);
            return admission;
        }

        /// <summary>Joins a session and starts NGO as a client of its host.</summary>
        public async Task<HaivenSessionAdmission> JoinAsync(string sessionId, string? joinCode = null)
        {
            var admission = await Required().JoinAsync(sessionId, joinCode);
            Begin(admission);
            return admission;
        }

        /// <summary>Stops NGO and leaves the session; the host leaving closes it.</summary>
        public Task LeaveAsync()
        {
            if (manager != null && manager.IsListening) manager.Shutdown();
            return Session?.LeaveAsync() ?? Task.CompletedTask;
        }

        private void Awake() => manager = GetComponent<NetworkManager>();

        private void Update() => Session?.Tick();

        private void OnDestroy()
        {
            if (Session?.Room != null) _ = Session.LeaveAsync();
            Session?.Dispose();
        }

        private void Begin(HaivenSessionAdmission admission)
        {
            var session = Required();
            transport = manager!.GetComponent<HaivenNetworkTransport>() ?? manager.gameObject.AddComponent<HaivenNetworkTransport>();
            manager.NetworkConfig.NetworkTransport = transport;
            transport.Signaling = session.Signaling;
            transport.Connection = session.IceOptions!;
            transport.HostUserId = admission.Hosting ? null : admission.HostUserId;
            transport.PeerConnected -= session.ReportPeerConnected;
            transport.PeerConnected += session.ReportPeerConnected;
            session.Peers = transport;
            var started = admission.Hosting ? manager.StartHost() : manager.StartClient();
            if (!started) _ = LeaveAsync();
        }

        private void OnEnded(HaivenP2PEndReason reason, string? code)
        {
            if (manager != null && manager.IsListening) manager.Shutdown();
            Ended?.Invoke(reason, code);
        }

        private HaivenP2PSession Required() =>
            Session ?? throw new InvalidOperationException("haiven: call Configure first");
    }
}
