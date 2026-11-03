#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// A stand-in for the game service's half of peer-connection rules signaling: it authenticates
    /// each socket, answers <c>p2p.signal</c> with success, and relays it to the
    /// recipient as <c>p2p.signal.received</c> with the sender's id set by the
    /// server; <c>p2p.signal.ack</c> goes back the same way. Deliveries can be
    /// dropped or doubled to exercise retries and duplicates.
    /// </summary>
    internal sealed class FakeSignalingServer
    {
        private readonly Dictionary<string, FakeSocket> sockets = new Dictionary<string, FakeSocket>();

        public string SessionId { get; } = Guid.NewGuid().ToString("D");

        /// <summary>Signal deliveries still to drop, counted down.</summary>
        public int DropSignals { get; set; }

        public bool DoubleSignals { get; set; }

        public List<string> Log { get; } = new List<string>();

        public (HaivenSessionCore Session, SessionChannel Channel) Connect(string userId)
        {
            var socket = new FakeSocket(this, userId);
            sockets[userId] = socket;
            var session = new HaivenSessionCore(socket, () => "token-" + userId);
            var channel = new SessionChannel(session);
            session.Opened();
            return (session, channel);
        }

        public void Receive(string from, string json)
        {
            var frame = HaivenJson.Parse(json);
            var type = SessionsApi.Text(frame, "type");
            var requestId = SessionsApi.Text(frame, "requestId");
            frame.TryGetProperty("payload", out var payload);
            switch (type)
            {
                case "auth":
                    sockets[from].Deliver("{\"type\":\"auth.success\",\"requestId\":\"" + requestId
                        + "\",\"success\":true,\"payload\":{\"connectionId\":\"conn-" + from + "\"}}");
                    break;
                case "p2p.signal":
                    sockets[from].Deliver("{\"type\":\"p2p.signal\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    var to = SessionsApi.Text(payload, "toUserId");
                    var seq = SessionsApi.Number(payload, "seq");
                    Log.Add($"signal {from}->{to} #{seq}");
                    if (DropSignals > 0)
                    {
                        DropSignals--;
                        Log.Add($"dropped #{seq}");
                        break;
                    }
                    var received = Event("p2p.signal.received", new Dictionary<string, object?>
                    {
                        ["sessionId"] = SessionsApi.Text(payload, "sessionId"),
                        ["fromUserId"] = from,
                        ["attemptId"] = SessionsApi.Text(payload, "attemptId"),
                        ["seq"] = seq,
                        ["data"] = SessionsApi.Text(payload, "data"),
                    });
                    sockets[to].Deliver(received);
                    if (DoubleSignals) sockets[to].Deliver(received);
                    break;
                case "p2p.signal.ack":
                    sockets[from].Deliver("{\"type\":\"p2p.signal.ack\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    sockets[SessionsApi.Text(payload, "toUserId")].Deliver(Event("p2p.signal.ack", new Dictionary<string, object?>
                    {
                        ["sessionId"] = SessionsApi.Text(payload, "sessionId"),
                        ["fromUserId"] = from,
                        ["attemptId"] = SessionsApi.Text(payload, "attemptId"),
                        ["seq"] = SessionsApi.Number(payload, "seq"),
                    }));
                    break;
                default:
                    sockets[from].Deliver("{\"type\":\"" + type + "\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    break;
            }
        }

        private static string Event(string type, Dictionary<string, object?> payload) =>
            HaivenJson.Serialize(new Dictionary<string, object?> { ["type"] = type, ["payload"] = payload });

        private sealed class FakeSocket : IHaivenSocket
        {
            private readonly FakeSignalingServer server;
            private readonly string userId;

            public FakeSocket(FakeSignalingServer server, string userId)
            {
                this.server = server;
                this.userId = userId;
            }

            public event Action<string>? Received;

            public event Action<int, string>? Closed;

            public void Send(string payload) => server.Receive(userId, payload);

            public void Close() => Closed?.Invoke(1000, "closed");

            public void Deliver(string json) => Received?.Invoke(json);
        }
    }

    public sealed class LobbySignalingTests
    {
        private const string Host = "host-user";
        private const string Guest = "guest-user";

        private readonly List<GameObject> objects = new List<GameObject>();

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
        }

        [Test]
        public void SignalsArriveOnceAndInOrderDespiteLossAndDuplicates()
        {
            var server = new FakeSignalingServer { DoubleSignals = true };
            var now = 0f;
            var (hostSocket, hostChannel) = server.Connect(Host);
            var (guestSocket, guestChannel) = server.Connect(Guest);
            Assert.That(hostChannel.ConnectionId, Is.EqualTo("conn-" + Host));
            var host = new SessionPeerSignaling(hostChannel, server.SessionId, Host, null, () => now);
            var guest = new SessionPeerSignaling(guestChannel, server.SessionId, Guest, Host, () => now);
            host.Start(asHost: true);
            guest.Start(asHost: false);
            var arrived = new List<string>();
            host.Received += (from, signal) => arrived.Add(from + ":" + signal.Kind + ":" + (signal.Sdp ?? signal.Candidate!.Candidate));

            // The second message is lost once; the first and third arrive twice.
            guest.Send(Host, PeerSignal.Offer("offer-sdp"));
            server.DropSignals = 1;
            guest.Send(Host, PeerSignal.ForCandidate(new PeerIceCandidate("candidate-1", "0", 0)));
            guest.Send(Host, PeerSignal.ForCandidate(new PeerIceCandidate("candidate-2", "0", 0)));
            host.Poll();
            guest.Poll();

            // Candidate 2 waits for candidate 1, which only a retry brings.
            Assert.That(arrived, Is.EqualTo(new[] { Guest + ":Offer:offer-sdp" }));
            Assert.That(guest.PendingAcknowledgements, Is.EqualTo(1), "only the lost message is unacknowledged");

            now = 1.1f;
            guest.Poll();
            host.Poll();
            guest.Poll();
            Assert.That(arrived, Is.EqualTo(new[]
            {
                Guest + ":Offer:offer-sdp", Guest + ":Candidate:candidate-1", Guest + ":Candidate:candidate-2",
            }));
            Assert.That(guest.PendingAcknowledgements, Is.Zero);

            hostSocket.Dispose();
            guestSocket.Dispose();
        }

        [Test]
        public void ANewOfferStartsANewAttemptAndTheOldOneIsIgnored()
        {
            var server = new FakeSignalingServer();
            var (_, hostChannel) = server.Connect(Host);
            var (_, guestChannel) = server.Connect(Guest);
            var host = new SessionPeerSignaling(hostChannel, server.SessionId, Host, null, () => 0f);
            var guest = new SessionPeerSignaling(guestChannel, server.SessionId, Guest, Host, () => 0f);
            host.Start(asHost: true);
            guest.Start(asHost: false);
            var atGuest = new List<string>();
            guest.Received += (_, signal) => atGuest.Add(signal.Kind + ":" + signal.Sdp);
            host.Received += (from, signal) =>
            {
                if (signal.Kind == PeerSignalKind.Offer) host.Send(from, PeerSignal.Answer("answer-to-" + signal.Sdp));
            };

            guest.Send(Host, PeerSignal.Offer("first"));
            host.Poll();
            guest.Poll();
            guest.Send(Host, PeerSignal.Offer("second"));
            host.Poll();
            guest.Poll();

            Assert.That(atGuest, Is.EqualTo(new[] { "Answer:answer-to-first", "Answer:answer-to-second" }));
            var attempts = server.Log.Count(line => line.StartsWith("signal " + Guest));
            Assert.That(attempts, Is.EqualTo(2));
        }

        [Test]
        public void SignalDataRoundTripsEveryKind()
        {
            var candidate = PeerSignal.ForCandidate(new PeerIceCandidate("candidate:1 1 udp 1 10.0.0.1 9 typ host", "0", 0));
            var decoded = SessionPeerSignaling.Decode(SessionPeerSignaling.Encode(candidate))!;
            Assert.That(decoded.Kind, Is.EqualTo(PeerSignalKind.Candidate));
            Assert.That(decoded.Candidate!.Candidate, Is.EqualTo(candidate.Candidate!.Candidate));
            Assert.That(decoded.Candidate.SdpMid, Is.EqualTo("0"));
            Assert.That(decoded.Candidate.SdpMLineIndex, Is.EqualTo(0));
            var offer = SessionPeerSignaling.Decode(SessionPeerSignaling.Encode(PeerSignal.Offer("v=0\r\na=x\r\n")))!;
            Assert.That(offer.Sdp, Is.EqualTo("v=0\r\na=x\r\n"));
        }

        [Test]
        public void EachPlayModePlayerSignsInAsItsOwnAccount()
        {
            const string url = "http://127.0.0.1:18480/connect/game?subject=__player__";
            Assert.That(DemoSettings.ExpandPlayer(url, new[] { "Unity.exe", "-name", "Player 2" }),
                Is.EqualTo("http://127.0.0.1:18480/connect/game?subject=player-2"));
            Assert.That(DemoSettings.ExpandPlayer(url, new[] { "Unity.exe", "-projectPath", "x" }),
                Is.EqualTo("http://127.0.0.1:18480/connect/game?subject=main"));
            Assert.That(DemoSettings.ExpandPlayer("http://127.0.0.1/connect/game", new[] { "-name", "Player 2" }),
                Is.EqualTo("http://127.0.0.1/connect/game"), "a URL without the placeholder is unchanged");
        }

        /// <summary>Two NGO NetworkManagers over real WebRTC, signaling through the socket protocol only.</summary>
        [UnityTest]
        public IEnumerator NgoConnectsWithSignalingOverTheGameSocket()
        {
            var server = new FakeSignalingServer { DoubleSignals = true };
            var (_, hostChannel) = server.Connect(Host);
            var (_, guestChannel) = server.Connect(Guest);
            var hostManager = Manager("host", new SessionPeerSignaling(hostChannel, server.SessionId, Host, null), null);
            var guestManager = Manager("guest", new SessionPeerSignaling(guestChannel, server.SessionId, Guest, Host), Host);
            var hostTransport = (HaivenNetworkTransport)hostManager.NetworkConfig.NetworkTransport;
            string? connected = null;
            hostTransport.PeerConnected += user => connected = user;

            Assert.That(hostManager.StartHost(), Is.True);
            Assert.That(guestManager.StartClient(), Is.True);
            var deadline = Time.realtimeSinceStartup + 20f;
            while (!(guestManager.IsConnectedClient && hostManager.ConnectedClientsIds.Count == 2))
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out; " + string.Join(", ", server.Log));
                yield return null;
            }
            Assert.That(connected, Is.EqualTo(Guest), "the host reports the guest by its Haiven user id");
            Assert.That(server.Log.Any(line => line.StartsWith("signal " + Host + "->" + Guest)), Is.True, "the host answered");

            // session.member.left on the host drops the guest at once.
            Assert.That(hostTransport.DropUser(Guest), Is.True);
            deadline = Time.realtimeSinceStartup + 10f;
            while (hostManager.ConnectedClientsIds.Count != 1)
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("the host kept the dropped guest");
                yield return null;
            }
        }

        private NetworkManager Manager(string name, SessionPeerSignaling signaling, string? hostUserId)
        {
            var created = new GameObject(name);
            objects.Add(created);
            var transport = created.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = signaling;
            transport.HostUserId = hostUserId;
            var manager = created.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport, EnableSceneManagement = false };
            return manager;
        }
    }
}
