#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Haiven.Demo.BossRoom.Lobby;
using Haiven.Demo.BossRoom.P2P;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>
    /// The lobby path against a running game service with P2P sessions: two
    /// players sign in through the game login handoff, open game sockets, and a
    /// host's room is listed, joined and played over WebRTC with signaling and
    /// TURN credentials from the sockets.
    ///
    /// Needs a backend such as the game service's SDK demo stack with sessions turned
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
        public IEnumerator HostListsJoinsAndPlaysThroughTheSessionsApi()
        {
            var baseUrl = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_BASE_URL");
            var appId = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_APP_ID");
            var integration = Environment.GetEnvironmentVariable("HAIVEN_LOBBY_INTEGRATION_ID");
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(integration))
                Assert.Ignore("HAIVEN_LOBBY_BASE_URL, HAIVEN_LOBBY_APP_ID and HAIVEN_LOBBY_INTEGRATION_ID are not set");

            Player? host = null, guest = null;
            yield return Run(async () =>
            {
                host = await Player.SignInAsync(baseUrl!, appId!, integration!, "lobby-test-host-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                guest = await Player.SignInAsync(baseUrl!, appId!, integration!, "lobby-test-guest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            });
            disposables.Add(host!);
            disposables.Add(guest!);
            yield return Until(() => host!.Channel.IsOpen && guest!.Channel.IsOpen, "both game sockets authenticated");

            SessionAdmission? created = null, joined = null;
            IReadOnlyList<SessionListing>? rooms = null;
            var name = "Lobby test " + Guid.NewGuid().ToString("N").Substring(0, 6);
            yield return Run(async () =>
            {
                created = await host!.Api.CreateAsync(host.Channel.ConnectionId!, name, SessionVisibility.Public, 4, false);
                rooms = await guest!.Api.ListAsync();
            });
            Assert.That(created!.RelayPolicy, Is.EqualTo("relay-only"), "a public room is always relayed");
            var listed = rooms!.SingleOrDefault(room => room.SessionId == created.SessionId);
            Assert.That(listed, Is.Not.Null, "the guest's list shows the host's room");
            Assert.That(listed!.Name, Is.EqualTo(name));
            Assert.That(listed.MemberCount, Is.EqualTo(1));

            var hostEvents = new List<string>();
            host!.Channel.Event += (type, _) => hostEvents.Add(type);
            yield return Run(async () => joined = await guest!.Api.JoinAsync(guest.Channel.ConnectionId!, created.SessionId, null));
            Assert.That(joined!.HostUserId, Is.Not.Empty);
            yield return Until(() => { host.Channel.Pump(); return hostEvents.Contains("session.member.joined"); },
                "the host hears session.member.joined");

            PeerConnectionOptions? hostIce = null, guestIce = null;
            yield return Run(async () =>
            {
                hostIce = HaivenLobby.IceOptions(await host.Channel.RequestAsync("p2p.ice.servers", null, TimeSpan.FromSeconds(10)), out _);
                guestIce = HaivenLobby.IceOptions(await guest!.Channel.RequestAsync("p2p.ice.servers", null, TimeSpan.FromSeconds(10)), out _);
            });
            Assert.That(hostIce!.Policy, Is.EqualTo(PeerIcePolicy.Relay));
            Assert.That(hostIce.IceServers.SelectMany(server => server.Urls).Any(url => url.StartsWith("turn:")), Is.True);

            var hostManager = Manager("host", new SessionPeerSignaling(host.Channel, created.SessionId, joined.HostUserId, null), null, hostIce);
            var guestManager = Manager("guest", new SessionPeerSignaling(guest!.Channel, created.SessionId, "", joined.HostUserId),
                joined.HostUserId, guestIce!);
            var hostTransport = (HaivenNetworkTransport)hostManager.NetworkConfig.NetworkTransport;
            var reported = false;
            hostTransport.PeerConnected += userId => host.Channel.Send("session.peer.connected",
                new Dictionary<string, object?> { ["sessionId"] = created.SessionId, ["userId"] = userId },
                reply => reported = reply.Success == true);

            Assert.That(hostManager.StartHost(), Is.True);
            Assert.That(guestManager.StartClient(), Is.True);
            yield return Until(() => guestManager.IsConnectedClient && hostManager.ConnectedClientsIds.Count == 2,
                "NGO connected over WebRTC with signaling through the game service");
            yield return Until(() => { host.Channel.Pump(); return reported; }, "the game service accepted session.peer.connected");
            yield return Until(() => hostTransport.GetPathType(1) != null, "the path type is measured");
            Assert.That(hostTransport.GetPathType(1), Is.EqualTo("relay"));

            // The guest leaves through the API; the host is told and drops it.
            yield return Run(() => guest.Api.LeaveAsync(created.SessionId));
            yield return Until(() =>
            {
                host.Channel.Pump();
                if (hostEvents.Contains("session.member.left") && hostTransport.GetUserId(1) != null)
                    hostTransport.DropUser(hostTransport.GetUserId(1)!);
                return hostManager.ConnectedClientsIds.Count == 1;
            }, "the host dropped the guest after session.member.left");

            // The host leaving closes the room.
            yield return Run(() => host.Api.LeaveAsync(created.SessionId));
            IReadOnlyList<SessionListing>? after = null;
            yield return Run(async () => after = await guest.Api.ListAsync());
            Assert.That(after!.Any(room => room.SessionId == created.SessionId), Is.False, "the closed room is no longer listed");
        }

        private NetworkManager Manager(string name, SessionPeerSignaling signaling, string? hostUserId, PeerConnectionOptions ice)
        {
            var created = new GameObject("lobby " + name);
            objects.Add(created);
            var transport = created.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = signaling;
            transport.HostUserId = hostUserId;
            transport.Connection = ice;
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

        private static IEnumerator Until(Func<bool> condition, string what)
        {
            var deadline = Time.realtimeSinceStartup + Timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out: " + what);
                yield return null;
            }
        }

        /// <summary>One signed-in player: a game credential, its game socket and the sessions API.</summary>
        private sealed class Player : IDisposable
        {
            private readonly SystemNetSocket socket;
            private readonly HaivenSessionCore session;
            private readonly BearerRestTransport rest;

            private Player(SystemNetSocket socket, HaivenSessionCore session, SessionChannel channel, BearerRestTransport rest, SessionsApi api)
            {
                this.socket = socket;
                this.session = session;
                this.rest = rest;
                Channel = channel;
                Api = api;
            }

            public SessionChannel Channel { get; }

            public SessionsApi Api { get; }

            public static async Task<Player> SignInAsync(string baseUrl, string appId, string integration, string subject)
            {
                var token = await HandoffAsync(baseUrl, integration, subject);
                var rest = new BearerRestTransport(baseUrl, appId, () => token);
                var socket = new SystemNetSocket();
                var session = new HaivenSessionCore(socket, () => token);
                var channel = new SessionChannel(session);
                var realtime = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/') + "/ws");
                await socket.ConnectAsync(realtime);
                session.Opened();
                return new Player(socket, session, channel, rest, new SessionsApi(rest, appId));
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
                return SessionsApi.Text(HaivenJson.Parse(body), "access_token");
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
                Channel.Dispose();
                session.Dispose();
                socket.Dispose();
                rest.Dispose();
            }
        }
    }
}
