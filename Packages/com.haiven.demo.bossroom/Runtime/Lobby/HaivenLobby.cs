#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Demo.BossRoom.P2P;
using Haiven.Sdk;
using Haiven.Sdk.Rest;
using Haiven.Sdk.Samples.Netcode;
using Haiven.Sdk.WebRtc;
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

    /// <summary>One room in the list, as <c>GET /v0/games/{game_id}/sessions</c> returns it.</summary>
    public sealed class SessionListing
    {
        public string SessionId = "";
        public string HostUserId = "";
        public string Visibility = "";
        public int MaxMembers;
        public int MemberCount;
        public string RelayPolicy = "";
        public string State = "";
        public string Name = "";

        public bool IsFull => MemberCount >= MaxMembers;

        internal static SessionListing From(ListedSession listed) => new SessionListing
        {
            SessionId = listed.SessionId ?? "",
            HostUserId = listed.HostUserId ?? "",
            Visibility = listed.Visibility ?? "",
            MaxMembers = (int)(listed.MaxMembers ?? 0),
            MemberCount = (int)(listed.MemberCount ?? 0),
            RelayPolicy = listed.RelayPolicy ?? "",
            State = listed.State ?? "",
            Name = listed.Metadata != null && listed.Metadata.TryGetValue("name", out var name) ? name : "",
        };
    }

    /// <summary>
    /// Haiven rooms for Boss Room: list, create and join through haiven-api's
    /// sessions, then play over the Haiven WebRTC transport. The SDK's P2P
    /// controller, <see cref="HaivenP2PSession"/>, owns admission, signaling,
    /// ICE servers, resume and the host's roster check; this class connects it
    /// to the demo's sign-in, Boss Room's connection flow and the lobby view.
    ///
    /// The list is what the server lets this player join: it already filters by
    /// age group (adults see adults rooms, a child sees rooms of the same band),
    /// and rooms that mix ages are never listed; they are joined with the room
    /// code. The game never learns a player's age group.
    /// </summary>
    public sealed class HaivenLobby : MonoBehaviour
    {
        private const float ListInterval = 5f;

        private HaivenSessionsClient? sessions;
        private HaivenP2PSession? p2p;
        private HaivenDemoClient? boundClient;
        private bool entered;
        private bool hosting;
        private HaivenNetworkTransport? transport;
        private NetworkTransport? previousTransport;
        private float nextList;
        private bool listing;
        private string? pendingStatus;

        public LobbyPhase Phase { get; private set; } = LobbyPhase.SignedOut;

        public IReadOnlyList<SessionListing> Rooms { get; private set; } = Array.Empty<SessionListing>();

        /// <summary>One line for the player: what happened last.</summary>
        public string Status { get; private set; } = "";

        /// <summary>The room joined or hosted, while in one.</summary>
        public HaivenSessionAdmission? Room => p2p?.Room;

        public bool Hosting => hosting;

        /// <summary>What the host shares for a private or mixed room: the room id and its code.</summary>
        public string? ShareCode => Room is { Hosting: true } room && !string.IsNullOrEmpty(room.JoinCode)
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
            if (sessions == null || boundClient == null || listing) return;
            listing = true;
            try
            {
                Rooms = (await sessions.ListAsync()).Select(SessionListing.From).ToList();
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

        public async void Create(string name, HaivenSessionVisibility visibility, int maxMembers, bool allowMixedAges)
        {
            if (!Ready()) return;
            Begin("Creating the room…");
            try
            {
                var options = new HaivenSessionCreateOptions(maxMembers)
                {
                    Visibility = visibility,
                    AllowMixedAges = allowMixedAges && visibility != HaivenSessionVisibility.Public,
                    Metadata = new Dictionary<string, string> { ["name"] = Name(name) },
                };
                Enter(await p2p!.CreateAsync(options));
            }
            catch (Exception error)
            {
                Fail("Could not create the room: " + Describe(error));
            }
        }

        /// <summary>Joins a listed room by id, or a private or mixed one with the host's share code.</summary>
        public async void Join(string sessionId, string? joinCode = null)
        {
            if (!Ready()) return;
            Begin("Joining the room…");
            try
            {
                Enter(await p2p!.JoinAsync(sessionId, joinCode));
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

        /// <summary>
        /// Closes the game socket on purpose, as a dropped network would, so the
        /// resume can be tried by hand. The demo opens a new socket on its own.
        /// </summary>
        public void DropHaivenConnection() => boundClient?.Session.Close();

        private void Update()
        {
            Bind();
            p2p?.Tick();
            if (Phase == LobbyPhase.Idle && Time.realtimeSinceStartup >= nextList) Refresh();
        }

        private void OnDestroy()
        {
            if (p2p?.Room != null) _ = p2p.LeaveAsync();
            p2p?.Dispose();
        }

        // Follows the demo's sign-in: each new client is a new game socket. In a
        // room the controller resumes the participation on it within 30 seconds;
        // the peer connections keep running meanwhile (SDK-P2P-5).
        private void Bind()
        {
            var client = HaivenBossRoomDemo.Current?.Client;
            if (client == boundClient)
            {
                if (client != null && p2p != null && Phase == LobbyPhase.SignedOut && client.ConnectionId != null)
                {
                    Phase = LobbyPhase.Idle;
                    Status = "";
                    Refresh();
                }
                return;
            }

            boundClient = client;
            if (client == null || !Configure())
            {
                if (!entered) Phase = LobbyPhase.SignedOut;
                Changed?.Invoke();
                return;
            }
            p2p!.Attach(client.Session, client.ConnectionId);
            if (!entered)
            {
                Phase = client.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
                if (Phase == LobbyPhase.Idle) Refresh();
            }
            Changed?.Invoke();
        }

        // One controller for the lobby's lifetime. Its REST calls go through the
        // current client, since the demo opens a new transport with every socket.
        private bool Configure()
        {
            if (p2p != null) return true;
            var settings = HaivenBossRoomDemo.Current?.Settings;
            if (settings == null) return false;
            if (!long.TryParse(settings.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId))
            {
                Status = "HAIVEN_DEMO_APP_ID must be the game's numeric id for Haiven rooms.";
                return false;
            }
            sessions = new HaivenSessionsClient(new CurrentRest(() => boundClient?.Rest), gameId);
            p2p = new HaivenP2PSession(sessions, () => Time.realtimeSinceStartupAsDouble);
            p2p.IceOptionsChanged += options =>
            {
                if (transport != null) transport.Connection = options;
            };
            p2p.MemberJoined += _ => Say("A player joined the room.");
            p2p.MemberLeft += (userId, reason) =>
            {
                if (userId == Room?.HostUserId) return;
                Say(reason == "not_on_roster" ? "A player is no longer in the room." : "A player left the room.");
            };
            p2p.ConnectionLost += () =>
            {
                Debug.Log("[haiven-lobby] game socket lost; resuming within " + HaivenP2PSession.ResumeWindowSeconds + " s");
                Say("The Haiven connection dropped. Reconnecting...");
            };
            p2p.Resumed += () =>
            {
                Debug.Log("[haiven-lobby] resumed the participation on connection " + Room?.ConnectionId);
                Say("Reconnected to Haiven; the room carries on.");
            };
            p2p.Ended += OnEnded;
            p2p.Diagnostic += (what, error) => Debug.LogWarning("[haiven-lobby] " + what + ": " + Describe(error));
            return true;
        }

        private bool Ready()
        {
            if (p2p == null || boundClient?.ConnectionId == null)
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

        private void Enter(HaivenSessionAdmission admission)
        {
            entered = true;
            hosting = admission.Hosting;
            Install(admission);
            var started = hosting ? BossRoomConnection.StartHost("Host") : BossRoomConnection.StartClient("Guest");
            if (!started)
            {
                _ = EndRoomAsync("Boss Room's connection manager was not found.");
                return;
            }
            Phase = LobbyPhase.InRoom;
            Status = hosting
                ? (ShareCode != null ? "Room open. Share the code below to let players in." : "Room open. Waiting for players.")
                : "Joined. Connecting to the host…";
            Debug.Log($"[haiven-lobby] {p2p!.IceOptions!.IceServers.Count} ICE server(s), "
                + (p2p.IceOptions.Policy == PeerIcePolicy.Relay ? "relay-only" : "auto"));
            Changed?.Invoke();
        }

        private void Install(HaivenSessionAdmission admission)
        {
            var manager = NetworkManager.Singleton;
            transport = manager.GetComponent<HaivenNetworkTransport>() ?? manager.gameObject.AddComponent<HaivenNetworkTransport>();
            if (manager.NetworkConfig.NetworkTransport != transport)
            {
                previousTransport = manager.NetworkConfig.NetworkTransport;
                manager.NetworkConfig.NetworkTransport = transport;
            }
            transport.Signaling = p2p!.Signaling;
            transport.HostUserId = admission.Hosting ? null : admission.HostUserId;
            transport.Connection = p2p.IceOptions!;
            // The Direct IP switch may have left its own TURN settings here.
            transport.CreateTransport = options => new UnityWebRtcPeerTransport(options);
            transport.PeerConnected += OnPeerConnected;
            transport.PeerDisconnected += OnPeerDisconnected;
            p2p.Peers = transport;
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
            if (p2p != null) p2p.Peers = null;
            previousTransport = null;
            transport = null;
        }

        // The host reports the open data channel; a guest the host never reports
        // loses its place after 60 seconds (ADR 0036 point 15).
        private void OnPeerConnected(string userId)
        {
            p2p?.ReportPeerConnected(userId);
            if (!hosting) Status = "Connected to the host.";
            Changed?.Invoke();
        }

        private void OnPeerDisconnected(string userId) => Changed?.Invoke();

        // The controller already left the session; stop Boss Room's game, whose
        // stop callback closes the lobby's side.
        private void OnEnded(HaivenP2PEndReason reason, string? code)
        {
            if (reason == HaivenP2PEndReason.Left) return;
            var status = reason switch
            {
                HaivenP2PEndReason.Closed => hosting ? "The room is closed." : "The host closed the room.",
                HaivenP2PEndReason.Removed => "Haiven removed you from the room (" + (code ?? "removed") + ").",
                HaivenP2PEndReason.ResumeRefused => "Haiven ended your place in the room (" + (code ?? "refused") + ").",
                HaivenP2PEndReason.ResumeWindowElapsed => "The Haiven connection did not come back within 30 seconds, so the room ended for you.",
                HaivenP2PEndReason.SignedOut => "Haiven signed you out, so the room ended for you.",
                _ => "Haiven refused the connection (" + (code ?? "refused") + "), so the room ended for you.",
            };
            Debug.Log("[haiven-lobby] the session ended: " + reason + (code != null ? " (" + code + ")" : ""));
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
            {
                pendingStatus = status;
                manager.Shutdown();
            }
            else
            {
                _ = EndRoomAsync(status);
            }
        }

        private void OnStopped(bool wasHost)
        {
            var status = pendingStatus ?? (hosting ? "Room closed." : "You left the room.");
            pendingStatus = null;
            _ = EndRoomAsync(status);
        }

        private async Task EndRoomAsync(string status)
        {
            if (!entered) return;
            entered = false;
            Uninstall();
            Phase = boundClient?.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
            Status = status;
            Changed?.Invoke();
            if (p2p?.Room != null)
            {
                try
                {
                    await p2p.LeaveAsync();
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[haiven-lobby] leaving the room failed: " + Describe(error));
                }
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
            entered = false;
            Uninstall();
            // Admitted, but the game could not start on it.
            if (p2p?.Room != null) _ = p2p.LeaveAsync();
            Phase = boundClient?.ConnectionId != null ? LobbyPhase.Idle : LobbyPhase.SignedOut;
            Status = status;
            Changed?.Invoke();
        }

        private void Say(string status)
        {
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

        /// <summary>The signed-in client's REST transport, whichever client is current.</summary>
        private sealed class CurrentRest : IRestApiTransport
        {
            private readonly Func<IRestApiTransport?> current;

            public CurrentRest(Func<IRestApiTransport?> current) => this.current = current;

            public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken) =>
                Current().SendAsync(request, cancellationToken);

            public Uri Url(string path) => Current().Url(path);

            private IRestApiTransport Current() =>
                current() ?? throw new InvalidOperationException("haiven: not signed in");
        }
    }
}
