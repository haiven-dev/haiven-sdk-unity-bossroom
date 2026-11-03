#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Haiven.Demo.BossRoom.P2P;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using Unity.Netcode;
using UnityEngine;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>What the lobby is doing, for the view.</summary>
    public enum LobbyPhase
    {
        /// <summary>No signed-in game socket yet.</summary>
        SignedOut,

        Idle,

        Busy,

        /// <summary>In a room; Boss Room's own host or client flow is running.</summary>
        InRoom,
    }

    /// <summary>
    /// Haiven rooms for Boss Room: list, create and join through the game service's
    /// sessions, then play over the Haiven WebRTC transport with signaling and
    /// TURN credentials from the same game socket.
    ///
    /// The list is what the server lets this player join: it already filters by
    /// age group (adults see adults rooms, a child sees rooms of the same band),
    /// and rooms that mix ages are never listed; they are joined with the room
    /// code. The game never learns a player's age group.
    /// </summary>
    public sealed class HaivenLobby : MonoBehaviour
    {
        private const float ListInterval = 5f;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private SessionsApi? api;
        private SessionChannel? channel;
        private HaivenDemoClient? boundClient;
        private SessionAdmission? room;
        private bool hosting;
        private HaivenNetworkTransport? transport;
        private NetworkTransport? previousTransport;
        private SessionPeerSignaling? signaling;
        private DateTimeOffset? refreshIceAt;
        private float nextList;
        private bool listing;

        public LobbyPhase Phase { get; private set; } = LobbyPhase.SignedOut;

        public IReadOnlyList<SessionListing> Rooms { get; private set; } = Array.Empty<SessionListing>();

        /// <summary>One line for the player: what happened last.</summary>
        public string Status { get; private set; } = "";

        /// <summary>The room joined or hosted, while in one.</summary>
        public SessionAdmission? Room => room;

        public bool Hosting => hosting;

        /// <summary>What the host shares for a private or mixed room: the room id and its code.</summary>
        public string? ShareCode => room != null && hosting && !string.IsNullOrEmpty(room.JoinCode)
            ? room.SessionId + "/" + room.JoinCode
            : null;

        public event Action? Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            // Only inside Boss Room: a project without its connection manager has nowhere to start a game.
            if (Type.GetType(BossRoomConnection.ConnectionManagerType, false) == null) return;
            var host = new GameObject("Haiven Lobby");
            DontDestroyOnLoad(host);
            host.AddComponent<HaivenLobby>();
            host.AddComponent<HaivenLobbyView>();
        }

        public async void Refresh()
        {
            if (api == null || listing) return;
            listing = true;
            try
            {
                Rooms = await api.ListAsync();
                if (Phase == LobbyPhase.Idle && Status.StartsWith("Could not list")) Status = "";
            }
            catch (Exception error)
            {
                Status = "Could not list rooms: " + Describe(error);
            }
            finally
            {
                listing = false;
                nextList = Time.realtimeSinceStartup + ListInterval;
                Changed?.Invoke();
            }
        }

        public async void Create(string name, SessionVisibility visibility, int maxMembers, bool allowMixedAges)
        {
            if (!Ready(out var connectionId)) return;
            Begin("Creating the room…");
            try
            {
                var created = await api!.CreateAsync(connectionId, Name(name), visibility, maxMembers,
                    allowMixedAges && visibility != SessionVisibility.Public);
                created.JoinPayload = null;
                await EnterAsync(created, hosting: true);
            }
            catch (Exception error)
            {
                Fail("Could not create the room: " + Describe(error));
            }
        }

        /// <summary>Joins a listed room by id, or a private or mixed one with the host's share code.</summary>
        public async void Join(string sessionId, string? joinCode = null)
        {
            if (!Ready(out var connectionId)) return;
            Begin("Joining the room…");
            try
            {
                var joined = await api!.JoinAsync(connectionId, sessionId, joinCode);
                await EnterAsync(joined, hosting: false);
            }
            catch (Exception error)
            {
                Fail("Could not join: " + Describe(error));
            }
        }

        /// <summary>Takes "room-id/CODE", the share code the host's lobby shows.</summary>
        public void JoinWithCode(string shareCode)
        {
            var parts = (shareCode ?? "").Trim().Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length != 6)
            {
                Status = "A room code looks like room-id/ABC123, as the host's lobby shows it.";
                Changed?.Invoke();
                return;
            }
            Join(parts[0], parts[1].ToUpperInvariant());
        }

        public void Leave()
        {
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening) manager.Shutdown();
            else _ = EndRoomAsync("You left the room.");
        }

        private void Update()
        {
            Bind();
            channel?.Pump();
            if (Phase == LobbyPhase.Idle && Time.realtimeSinceStartup >= nextList) Refresh();
            if (room != null && refreshIceAt != null && DateTimeOffset.UtcNow >= refreshIceAt) _ = RefreshIceAsync();
        }

        private void OnDestroy()
        {
            if (room != null && api != null) _ = api.LeaveAsync(room.SessionId);
            Unbind();
        }

        // Follows the demo's sign-in: a new client means a new socket to bind to.
        private void Bind()
        {
            var client = HaivenBossRoomDemo.Current?.Client;
            var settings = HaivenBossRoomDemo.Current?.Settings;
            if (client == boundClient)
            {
                if (client != null && Phase == LobbyPhase.SignedOut && client.ConnectionId != null)
                {
                    Phase = LobbyPhase.Idle;
                    Status = "";
                    Refresh();
                }
                return;
            }

            Unbind();
            boundClient = client;
            if (client == null || settings == null)
            {
                Phase = LobbyPhase.SignedOut;
                Changed?.Invoke();
                return;
            }
            api = new SessionsApi(client.Rest, settings.AppId);
            channel = new SessionChannel(client.Session, client.ConnectionId);
            channel.Event += OnSessionEvent;
            Phase = client.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
            if (Phase == LobbyPhase.Idle) Refresh();
            Changed?.Invoke();
        }

        private void Unbind()
        {
            if (channel != null)
            {
                channel.Event -= OnSessionEvent;
                channel.Dispose();
            }
            channel = null;
            api = null;
            boundClient = null;
        }

        private bool Ready(out string connectionId)
        {
            connectionId = boundClient?.ConnectionId ?? "";
            if (api == null || channel == null || connectionId.Length == 0)
            {
                Status = boundClient == null
                    ? "Sign in with Haiven first (the Haiven panel, top right)."
                    : "Signed in, but the Haiven connection is not ready yet. Try again in a moment.";
                Changed?.Invoke();
                return false;
            }
            if (Phase != LobbyPhase.Idle) return false;
            if (!BossRoomConnection.IsAvailable)
            {
                Status = "Open the lobby from Boss Room's main menu.";
                Changed?.Invoke();
                return false;
            }
            return true;
        }

        private async Task EnterAsync(SessionAdmission admission, bool hosting)
        {
            room = admission;
            this.hosting = hosting;
            var connection = await IceAsync();
            Install(admission, connection);
            var started = hosting ? BossRoomConnection.StartHost("Host") : BossRoomConnection.StartClient("Guest");
            if (!started)
            {
                await EndRoomAsync("Boss Room's connection manager was not found.");
                return;
            }
            Phase = LobbyPhase.InRoom;
            Status = hosting
                ? (ShareCode != null ? "Room open. Share the code below to let players in." : "Room open. Waiting for players.")
                : "Joined. Connecting to the host…";
            Changed?.Invoke();
        }

        // the game service issues the servers for the caller's participation on this
        // socket. A credential is checked only when an allocation is made, so a
        // fresh one matters for the next guest, not for connections already open.
        private async Task<PeerConnectionOptions> IceAsync()
        {
            var reply = await channel!.RequestAsync("p2p.ice.servers", null, RequestTimeout);
            var options = IceOptions(reply, out refreshIceAt);
            Debug.Log($"[haiven-lobby] {options.IceServers.Count} ICE server(s), "
                + (options.Policy == PeerIcePolicy.Relay ? "relay-only" : "auto"));
            return options;
        }

        /// <summary>Reads a <c>p2p.ice.servers</c> reply into connection settings and the time to fetch again.</summary>
        internal static PeerConnectionOptions IceOptions(ServerFrame reply, out DateTimeOffset? refreshAt)
        {
            if (reply.Success != true || reply.Payload == null)
                throw new InvalidOperationException(reply.Error ?? "p2p.ice.servers failed");
            var payload = reply.Payload;
            var servers = new List<PeerIceServer>();
            if (payload.TryGetProperty("iceServers", out var list))
            {
                foreach (var server in list.EnumerateArray())
                {
                    var urls = server.TryGetProperty("urls", out var u)
                        ? u.EnumerateArray().Where(url => url.Kind == HaivenJsonKind.String).Select(url => url.GetString()).ToArray()
                        : Array.Empty<string>();
                    if (urls.Length == 0) continue;
                    servers.Add(new PeerIceServer(urls, SessionsApi.NullableText(server, "username"),
                        SessionsApi.NullableText(server, "credential")));
                }
            }
            var refreshAfter = SessionsApi.NullableText(payload, "refreshAfter");
            refreshAt = refreshAfter != null && DateTimeOffset.TryParse(refreshAfter, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
            var policy = SessionsApi.Text(payload, "relayPolicy") == "relay-only" ? PeerIcePolicy.Relay : PeerIcePolicy.All;
            return new PeerConnectionOptions(servers, policy);
        }

        private async Task RefreshIceAsync()
        {
            refreshIceAt = null;
            try
            {
                var connection = await IceAsync();
                if (transport != null) transport.Connection = connection;
            }
            catch (Exception error)
            {
                Debug.LogWarning("[haiven-lobby] could not refresh ICE servers: " + Describe(error));
                refreshIceAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        private void Install(SessionAdmission admission, PeerConnectionOptions connection)
        {
            var manager = NetworkManager.Singleton;
            transport = manager.GetComponent<HaivenNetworkTransport>() ?? manager.gameObject.AddComponent<HaivenNetworkTransport>();
            if (manager.NetworkConfig.NetworkTransport != transport)
            {
                previousTransport = manager.NetworkConfig.NetworkTransport;
                manager.NetworkConfig.NetworkTransport = transport;
            }
            signaling = new SessionPeerSignaling(channel!, admission.SessionId,
                hosting ? admission.HostUserId : "", hosting ? null : admission.HostUserId);
            transport.Signaling = signaling;
            transport.HostUserId = hosting ? null : admission.HostUserId;
            transport.Connection = connection;
            transport.CreateTransport = options => new UnityWebRtcPeerTransport(options);
            transport.PeerConnected += OnPeerConnected;
            transport.PeerDisconnected += OnPeerDisconnected;
            manager.OnClientStopped += OnStopped;
        }

        private void Uninstall()
        {
            var manager = NetworkManager.Singleton;
            if (manager != null)
            {
                manager.OnClientStopped -= OnStopped;
                // Direct IP keeps working afterwards: Unity Transport comes back
                // unless the P2P switch put the Haiven transport there for it.
                if (previousTransport != null && manager.NetworkConfig.NetworkTransport == transport)
                    manager.NetworkConfig.NetworkTransport = previousTransport;
            }
            if (transport != null)
            {
                transport.PeerConnected -= OnPeerConnected;
                transport.PeerDisconnected -= OnPeerDisconnected;
                if (previousTransport == null) HaivenP2PBoot.Reconfigure(transport);
            }
            previousTransport = null;
            transport = null;
            signaling = null;
        }

        // The host reports the open data channel; a guest that never gets here
        // loses its place after 60 seconds (peer-connection rules point 15).
        private void OnPeerConnected(string userId)
        {
            if (hosting && room != null)
                channel?.Send("session.peer.connected",
                    new Dictionary<string, object?> { ["sessionId"] = room.SessionId, ["userId"] = userId });
            if (!hosting) Status = "Connected to the host.";
            Changed?.Invoke();
        }

        private void OnPeerDisconnected(string userId) => Changed?.Invoke();

        private void OnSessionEvent(string type, IHaivenJsonNode payload)
        {
            if (room == null || SessionsApi.Text(payload, "sessionId") != room.SessionId) return;
            var manager = NetworkManager.Singleton;
            switch (type)
            {
                case "session.member.joined":
                    Status = "A player joined the room.";
                    break;
                case "session.member.left":
                    // The server ended this participation; close the peer at once
                    // rather than wait for the connection to fail (SDK-P2P-11).
                    var userId = SessionsApi.Text(payload, "userId");
                    if (hosting && transport != null && manager != null && manager.IsServer)
                    {
                        if (transport.DropUser(userId)) Status = "A player left the room.";
                    }
                    else if (!hosting && manager != null && manager.IsListening && userId != room.HostUserId)
                        Status = "A player left the room.";
                    break;
                case "session.closed":
                    Status = "The host closed the room.";
                    if (manager != null && manager.IsListening) manager.Shutdown();
                    else _ = EndRoomAsync(Status);
                    break;
            }
            Changed?.Invoke();
        }

        private void OnStopped(bool wasHost) => _ = EndRoomAsync(hosting ? "Room closed." : "You left the room.");

        private async Task EndRoomAsync(string status)
        {
            var ended = room;
            room = null;
            refreshIceAt = null;
            Uninstall();
            Phase = boundClient?.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
            Status = status;
            Changed?.Invoke();
            if (ended == null || api == null) return;
            try
            {
                await api.LeaveAsync(ended.SessionId);
            }
            catch (HaivenRestException error) when (error.Code == "session_not_found")
            {
                // Already over on the server: the host closed it, or it timed out.
            }
            catch (Exception error)
            {
                Debug.LogWarning("[haiven-lobby] leaving the room failed: " + Describe(error));
            }
            Refresh();
        }

        private void Begin(string status)
        {
            Phase = LobbyPhase.Busy;
            Status = status;
            Changed?.Invoke();
        }

        private void Fail(string status)
        {
            room = null;
            Uninstall();
            Phase = boundClient?.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
            Status = status;
            Changed?.Invoke();
        }

        private static string Name(string name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) trimmed = "Boss Room";
            return trimmed.Length > 64 ? trimmed.Substring(0, 64) : trimmed;
        }

        /// <summary>The player-facing reading of an API refusal; the code itself for anything else.</summary>
        internal static string Describe(Exception error)
        {
            var code = (error as HaivenRestException)?.Code ?? (error as InvalidOperationException)?.Message;
            return code switch
            {
                "session_full" => "the room is full.",
                "session_locked" => "the host locked the room.",
                "session_not_found" => "the room is gone, or you cannot join it.",
                "kicked_from_session" => "the host removed you from this room.",
                "already_in_session" => "you are already in a room for this game.",
                "connection_not_bound" => "the Haiven connection is not ready; sign in again.",
                "age_verification_required" => "Haiven needs to confirm your age first.",
                "parental_consent_required" => "a parent needs to allow online play first.",
                "game_access_required" => "you need this game on Haiven to join.",
                "relay_unavailable" => "Haiven's relay is not available right now.",
                "rate_limited" => "too many rooms at once; wait a minute.",
                "bad_request" => "the room settings were refused.",
                _ => code ?? error.Message,
            };
        }
    }
}
