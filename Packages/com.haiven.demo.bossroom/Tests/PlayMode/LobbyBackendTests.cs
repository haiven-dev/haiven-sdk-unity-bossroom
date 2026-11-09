#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using ListedSession = Haiven.Sdk.Rest.ListedSession;
using Roster = Haiven.Sdk.Rest.Roster;
using Haiven.Sdk.Samples.Netcode;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>
    /// The lobby path against a running haiven-api with P2P sessions: two
    /// players sign in through the game login handoff, open game sockets, and
    /// the SDK's P2P controller lists, creates and joins a room, which is played
    /// over WebRTC with signaling and TURN credentials from the sockets.
    ///
    /// Needs a backend such as haiven-api's SDK demo stack with sessions turned
    /// on, named by HAIVEN_LOBBY_BASE_URL (for example http://127.0.0.1:18480/v0),
    /// HAIVEN_LOBBY_APP_ID and HAIVEN_LOBBY_INTEGRATION_ID; ignored without them.
    /// The sign-in page is the stack's development stand-in at /connect/game,
    /// which takes a subject so the two players are two accounts.
    /// </summary>
    public sealed class LobbyBackendTests
    {
        private const string Redirect = "http://127.0.0.1:47615/haiven/callback";
        private const float Timeout = 30f;

        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly List<IDisposable> disposables = new List<IDisposable>();

        [TearDown]
        public void Destroy()
        {
            foreach (var created in objects)
            {
                var manager = created.GetComponent<NetworkManager>();
                if (manager != null && manager.IsListening) manager.Shutdown();
                UnityEngine.Object.Destroy(created);
            }
            objects.Clear();
            foreach (var disposable in disposables) disposable.Dispose();
            disposables.Clear();
        }

        [UnityTest]
        public IEnumerator HostListsJoinsAndPlaysThroughTheSdkController()
        {
            if (!Configured(out var baseUrl, out var appId, out var integration)) yield break;
            Player? host = null, guest = null;
            yield return Run(async () =>
            {
                host = await Player.SignInAsync(baseUrl, appId, integration, "lobby-test-host-" + Short());
                guest = await Player.SignInAsync(baseUrl, appId, integration, "lobby-test-guest-" + Short());
            });
            disposables.Add(host!);
            disposables.Add(guest!);
            yield return Until(() => host!.Ready && guest!.Ready, "both game sockets authenticated", host!, guest!);

            HaivenSessionAdmission? created = null, joined = null;
            IReadOnlyList<ListedSession>? rooms = null;
            var name = "Lobby test " + Short();
            yield return Run(async () =>
            {
                created = await host.P2P.CreateAsync(new HaivenSessionCreateOptions(4)
                {
                    Metadata = new Dictionary<string, string> { ["name"] = name },
                });
                rooms = await guest.Sessions.ListAsync();
            });
            Assert.That(created!.RelayPolicy, Is.EqualTo("relay-only"), "a public room is always relayed");
            Assert.That(created.HostUserId, Is.Not.Empty, "the controller read the host's id from the roster");
            var listed = rooms!.SingleOrDefault(room => room.SessionId == created.SessionId);
            Assert.That(listed, Is.Not.Null, "the guest's list shows the host's room");
            Assert.That(listed!.Metadata!["name"], Is.EqualTo(name));
            Assert.That(listed.MemberCount, Is.EqualTo(1));

            var hostHeardJoin = false;
            string? left = null;
            host.P2P.MemberJoined += _ => hostHeardJoin = true;
            host.P2P.MemberLeft += (userId, _) => left = userId;
            yield return Run(async () => joined = await guest.P2P.JoinAsync(created.SessionId));
            Assert.That(joined!.HostUserId, Is.EqualTo(created.HostUserId));
            yield return Until(() => hostHeardJoin, "the host hears session.member.joined", host, guest);

            Assert.That(host.P2P.IceOptions!.Policy, Is.EqualTo(PeerIcePolicy.Relay));
            Assert.That(host.P2P.IceOptions.IceServers.SelectMany(server => server.Urls).Any(url => url.StartsWith("turn:")), Is.True);

            var hostManager = Manager("host", host, joined);
            var guestManager = Manager("guest", guest, joined);
            var hostTransport = (HaivenNetworkTransport)hostManager.NetworkConfig.NetworkTransport;

            Assert.That(hostManager.StartHost(), Is.True);
            Assert.That(guestManager.StartClient(), Is.True);
            yield return Until(() => guestManager.IsConnectedClient && hostManager.ConnectedClientsIds.Count == 2,
                "NGO connected over WebRTC with signaling through haiven-api", host, guest);
            yield return Until(() => hostTransport.GetPathType(1) != null, "the path type is measured", host, guest);
            Assert.That(hostTransport.GetPathType(1), Is.EqualTo("relay"));
            var guestUserId = hostTransport.GetUserId(1);

            // The guest leaves; session.member.left makes the host's controller drop it.
            yield return Run(() => guest.P2P.LeaveAsync());
            yield return Until(() => hostManager.ConnectedClientsIds.Count == 1,
                "the host dropped the guest after session.member.left", host, guest);
            Assert.That(left, Is.EqualTo(guestUserId));

            // The host leaving closes the room.
            yield return Run(() => host.P2P.LeaveAsync());
            IReadOnlyList<ListedSession>? after = null;
            yield return Run(async () => after = await guest.Sessions.ListAsync());
            Assert.That(after!.Any(room => room.SessionId == created.SessionId), Is.False, "the closed room is no longer listed");
        }

        /// <summary>
        /// A guest whose game socket drops resumes on a new one within the 30-second
        /// window: the controller moves the participation to the new connection
        /// and it stays on the roster.
        /// </summary>
        [UnityTest]
        public IEnumerator AGuestResumesOnANewSocketWithinTheWindow()
        {
            if (!Configured(out var baseUrl, out var appId, out var integration)) yield break;
            Player? host = null, guest = null;
            HaivenSessionAdmission? created = null, joined = null;
            yield return Run(async () =>
            {
                host = await Player.SignInAsync(baseUrl, appId, integration, "lobby-resume-host-" + Short());
                guest = await Player.SignInAsync(baseUrl, appId, integration, "lobby-resume-guest-" + Short());
            });
            disposables.Add(host!);
            disposables.Add(guest!);
            yield return Until(() => host!.Ready && guest!.Ready, "both game sockets authenticated", host!, guest!);
            yield return Run(async () =>
            {
                created = await host.P2P.CreateAsync(new HaivenSessionCreateOptions(4));
                joined = await guest.P2P.JoinAsync(created.SessionId);
            });
            var before = joined!.ConnectionId;
            var resumed = false;
            guest.P2P.Resumed += () => resumed = true;

            // The socket drops; the same signed-in player opens another.
            yield return Run(() => guest.ReconnectAsync());
            yield return Until(() => resumed, "the participation resumed on the new socket", host, guest);
            Assert.That(guest.P2P.State, Is.EqualTo(HaivenP2PState.InSession));
            Assert.That(joined.ConnectionId, Is.EqualTo(guest.P2P.Channel!.ConnectionId));
            Assert.That(joined.ConnectionId, Is.Not.EqualTo(before));

            Roster? roster = null;
            yield return Run(async () => roster = await host.Sessions.RosterAsync(created!.SessionId));
            Assert.That(roster!.Members, Has.Count.EqualTo(2), "the guest is still in the room");
            yield return Run(() => host.P2P.LeaveAsync());
        }

        /// <summary>
        /// The demo disposes its client when the credential lapses, without a
        /// close the controller could see. The controller treats the disposal as
        /// a drop, starts the resume window, and resumes on the next socket.
        /// </summary>
        [UnityTest]
        public IEnumerator AGuestWhoseClientIsDisposedResumesOnTheNextSocket()
        {
            if (!Configured(out var baseUrl, out var appId, out var integration)) yield break;
            Player? host = null, guest = null;
            HaivenSessionAdmission? created = null, joined = null;
            yield return Run(async () =>
            {
                host = await Player.SignInAsync(baseUrl, appId, integration, "lobby-disposed-host-" + Short());
                guest = await Player.SignInAsync(baseUrl, appId, integration, "lobby-disposed-guest-" + Short());
            });
            disposables.Add(host!);
            disposables.Add(guest!);
            yield return Until(() => host!.Ready && guest!.Ready, "both game sockets authenticated", host!, guest!);
            yield return Run(async () =>
            {
                created = await host.P2P.CreateAsync(new HaivenSessionCreateOptions(4));
                joined = await guest.P2P.JoinAsync(created.SessionId);
            });
            var before = joined!.ConnectionId;
            var lost = false;
            var resumed = false;
            guest.P2P.ConnectionLost += () => lost = true;
            guest.P2P.Resumed += () => resumed = true;

            // Disposed, not closed: no socket close reaches the controller.
            guest.CloseSocket();
            yield return Until(() => guest.P2P.State == HaivenP2PState.Resuming, "the guest treated the disposal as a drop", host, guest);
            Assert.That(lost, Is.True, "ConnectionLost was raised");

            yield return Run(() => guest.OpenSocketAsync());
            yield return Until(() => resumed, "the participation resumed on the new socket", host, guest);
            Assert.That(guest.P2P.State, Is.EqualTo(HaivenP2PState.InSession));
            Assert.That(joined.ConnectionId, Is.Not.EqualTo(before));
            yield return Run(() => host.P2P.LeaveAsync());
        }

        /// <summary>
        /// A guest whose socket stays gone loses the room when the window ends,
        /// and the server ends its participation too (connection_lost): the host
        /// drops it, from session.member.left or from its next roster read.
        /// </summary>
        [UnityTest, Timeout(120000)]
        public IEnumerator AGuestWithoutASocketLosesTheRoomAndTheHostDropsIt()
        {
            if (!Configured(out var baseUrl, out var appId, out var integration)) yield break;
            Player? host = null, guest = null;
            HaivenSessionAdmission? created = null;
            yield return Run(async () =>
            {
                host = await Player.SignInAsync(baseUrl, appId, integration, "lobby-late-host-" + Short());
                guest = await Player.SignInAsync(baseUrl, appId, integration, "lobby-late-guest-" + Short());
            });
            disposables.Add(host!);
            disposables.Add(guest!);
            yield return Until(() => host!.Ready && guest!.Ready, "both game sockets authenticated", host!, guest!);
            Roster? roster = null;
            yield return Run(async () =>
            {
                created = await host.P2P.CreateAsync(new HaivenSessionCreateOptions(4));
                await guest.P2P.JoinAsync(created.SessionId);
                roster = await host.Sessions.RosterAsync(created.SessionId);
            });
            var guestUserId = roster!.Members!.Select(member => member.UserId).Single(id => id != created!.HostUserId)!;
            var peers = new ConnectedPeers(guestUserId);
            host.P2P.Peers = peers;
            HaivenP2PEndReason? ended = null;
            guest.P2P.Ended += (reason, _) => ended = reason;

            // The socket drops and the player never opens another.
            guest.DropSocket();
            yield return Until(() => guest.P2P.State == HaivenP2PState.Resuming, "the guest noticed the drop", host, guest);
            // 30 seconds of window and up to one 10-second tick of the server's liveness worker.
            var waitUntil = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < waitUntil)
            {
                host.P2P.Tick();
                guest.P2P.Tick();
                yield return null;
            }

            Assert.That(ended, Is.EqualTo(HaivenP2PEndReason.ResumeWindowElapsed));
            Assert.That(peers.Dropped, Is.EqualTo(new[] { guestUserId }), "the host dropped the guest");
            yield return Run(async () => roster = await host.Sessions.RosterAsync(created!.SessionId));
            Assert.That(roster!.Members, Has.Count.EqualTo(1), "the server ended the guest's participation");
            yield return Run(() => host.P2P.LeaveAsync());
        }

        private static bool Configured(out string baseUrl, out string appId, out string integration)
        {
            baseUrl = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_BASE_URL") ?? "";
            appId = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_APP_ID") ?? "";
            integration = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_INTEGRATION_ID") ?? "";
            if (baseUrl.Length == 0 || appId.Length == 0 || integration.Length == 0)
                Assert.Ignore("HAIVEN_LOBBY_BASE_URL, HAIVEN_LOBBY_APP_ID and HAIVEN_LOBBY_INTEGRATION_ID are not set");
            return true;
        }

        private static string Short() => Guid.NewGuid().ToString("N").Substring(0, 6);

        // The lobby's wiring, as HaivenLobby.Install does it.
        private NetworkManager Manager(string name, Player player, HaivenSessionAdmission joined)
        {
            var created = new GameObject("lobby " + name);
            objects.Add(created);
            var session = player.P2P;
            var transport = created.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = session.Signaling;
            transport.HostUserId = session.Room!.Hosting ? null : joined.HostUserId;
            transport.Connection = session.IceOptions!;
            transport.PeerConnected += session.ReportPeerConnected;
            session.Peers = transport;
            var manager = created.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport, EnableSceneManagement = false };
            return manager;
        }

        private static IEnumerator Run(Func<Task> work)
        {
            var task = work();
            var deadline = Time.realtimeSinceStartup + Timeout;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out waiting for the backend");
                yield return null;
            }
            if (task.IsFaulted)
            {
                var error = task.Exception!.InnerException!;
                Assert.Fail(error is HaivenRestException rest ? $"HTTP {rest.StatusCode} {rest.Code}: {rest.Body}" : error.ToString());
            }
        }

        // Each frame ticks the players' controllers, as HaivenLobby.Update does.
        private static IEnumerator Until(Func<bool> condition, string what, params Player[] players)
        {
            var deadline = Time.realtimeSinceStartup + Timeout;
            while (true)
            {
                foreach (var player in players) player.P2P.Tick();
                if (condition()) yield break;
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out: " + what);
                yield return null;
            }
        }

        /// <summary>A host's peers for the roster check, without a transport.</summary>
        private sealed class ConnectedPeers : IHaivenPeerRoster
        {
            private readonly List<string> connected;

            public ConnectedPeers(params string[] users) => connected = users.ToList();

            public List<string> Dropped { get; } = new List<string>();

            public IEnumerable<string> ConnectedUsers() => connected.ToList();

            public bool DropUser(string userId)
            {
                if (!connected.Remove(userId)) return false;
                Dropped.Add(userId);
                return true;
            }
        }

        /// <summary>One signed-in player: a game credential, its game socket and the SDK's P2P controller.</summary>
        private sealed class Player : IDisposable
        {
            private readonly string baseUrl;
            private readonly string token;
            private readonly BearerRestTransport rest;
            private SystemNetSocket? socket;
            private HaivenSessionCore? session;
            private bool disposed;

            private Player(string baseUrl, string appId, string token)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                rest = new BearerRestTransport(baseUrl, appId, () => token);
                Sessions = new HaivenSessionsClient(rest, long.Parse(appId, CultureInfo.InvariantCulture));
                P2P = new HaivenP2PSession(Sessions, () => Time.realtimeSinceStartupAsDouble);
            }

            public HaivenSessionsClient Sessions { get; }

            public HaivenP2PSession P2P { get; }

            public bool Ready => P2P.Channel?.IsOpen == true;

            public static async Task<Player> SignInAsync(string baseUrl, string appId, string integration, string subject)
            {
                var player = new Player(baseUrl, appId, await HandoffAsync(baseUrl, integration, subject));
                await player.OpenSocketAsync();
                return player;
            }

            /// <summary>Drops this socket and opens another for the same signed-in player.</summary>
            public Task ReconnectAsync()
            {
                CloseSocket();
                return OpenSocketAsync();
            }

            /// <summary>Closes the socket as a dropped network would; the controller sees it close.</summary>
            public void DropSocket() => session?.Close();

            public void CloseSocket()
            {
                session?.Dispose();
                socket?.Dispose();
                session = null;
                socket = null;
            }

            public async Task OpenSocketAsync()
            {
                socket = new SystemNetSocket();
                session = new HaivenSessionCore(socket, () => token);
                P2P.Attach(session);
                var realtime = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/') + "/ws");
                await socket.ConnectAsync(realtime);
                session.Opened();
            }

            // The SDK's game login handoff by hand: PKCE S256 through the stack's
            // stand-in sign-in page, then the real exchange.
            private static async Task<string> HandoffAsync(string baseUrl, string integration, string subject)
            {
                var verifier = Base64Url(RandomBytes(32));
                string challenge;
                using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                var state = Base64Url(RandomBytes(16));
                var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
                var query = string.Join("&", new Dictionary<string, string>
                {
                    ["integration_id"] = integration,
                    ["completion"] = "redirect",
                    ["redirect_uri"] = Redirect,
                    ["state"] = state,
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256",
                    ["subject"] = subject,
                }.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));

                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var page = await http.GetAsync(origin + "/connect/game?" + query);
                var location = page.Headers.Location ?? throw new InvalidOperationException("no redirect from /connect/game: " + (int)page.StatusCode);
                var code = location.Query.TrimStart('?').Split('&').Select(part => part.Split('='))
                    .First(part => part[0] == "code")[1];
                using var exchange = await http.PostAsync(baseUrl.TrimEnd('/') + "/auth/handoff/exchange",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["code"] = Uri.UnescapeDataString(code),
                        ["code_verifier"] = verifier,
                        ["integration_id"] = integration,
                        ["redirect_uri"] = Redirect,
                    }));
                var body = await exchange.Content.ReadAsStringAsync();
                if (!exchange.IsSuccessStatusCode) throw new InvalidOperationException("handoff exchange: " + (int)exchange.StatusCode + " " + body);
                return HaivenJson.Parse(body).TryGetProperty("access_token", out var accessToken) ? accessToken.GetString() : "";
            }

            private static byte[] RandomBytes(int count)
            {
                var bytes = new byte[count];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                return bytes;
            }

            private static string Base64Url(byte[] bytes) =>
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                P2P.Dispose();
                CloseSocket();
                rest.Dispose();
            }
        }
    }
}
