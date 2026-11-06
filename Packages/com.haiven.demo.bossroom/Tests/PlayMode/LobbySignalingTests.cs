#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using Haiven.Sdk.Samples.Netcode;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>
    /// A stand-in for haiven-api's half of ADR 0036 signaling: it authenticates
    /// each socket, answers <c>p2p.signal</c> with success, and relays it to the
    /// recipient as <c>p2p.signal.received</c> with the sender's id set by the
    /// server; <c>p2p.signal.ack</c> goes back the same way. Deliveries can be
    /// doubled to exercise duplicates.
    /// </summary>
    internal sealed class FakeSignalingServer
    {
        private readonly Dictionary<string, FakeSocket> sockets = new Dictionary<string, FakeSocket>();

        public string SessionId { get; } = Guid.NewGuid().ToString("D");

        public bool DoubleSignals { get; set; }

        public List<string> Log { get; } = new List<string>();

        public (HaivenSessionCore Session, HaivenSessionChannel Channel) Connect(string userId)
        {
            var socket = new FakeSocket(this, userId);
            sockets[userId] = socket;
            var session = new HaivenSessionCore(socket, () => "token-" + userId);
            var channel = new HaivenSessionChannel(session);
            session.Opened();
            return (session, channel);
        }

        public void Receive(string from, string json)
        {
            var frame = HaivenJson.Parse(json);
            var type = Text(frame, "type");
            var requestId = Text(frame, "requestId");
            frame.TryGetProperty("payload", out var payload);
            switch (type)
            {
                case "auth":
                    sockets[from].Deliver("{\"type\":\"auth.success\",\"requestId\":\"" + requestId
                        + "\",\"success\":true,\"payload\":{\"connectionId\":\"conn-" + from + "\"}}");
                    break;
                case "p2p.signal":
                    sockets[from].Deliver("{\"type\":\"p2p.signal\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    var to = Text(payload, "toUserId");
                    var seq = Number(payload, "seq");
                    Log.Add($"signal {from}->{to} #{seq}");
                    var received = Event("p2p.signal.received", new Dictionary<string, object?>
                    {
                        ["sessionId"] = Text(payload, "sessionId"),
                        ["fromUserId"] = from,
                        ["attemptId"] = Text(payload, "attemptId"),
                        ["seq"] = seq,
                        ["data"] = Text(payload, "data"),
                    });
                    sockets[to].Deliver(received);
                    if (DoubleSignals) sockets[to].Deliver(received);
                    break;
                case "p2p.signal.ack":
                    sockets[from].Deliver("{\"type\":\"p2p.signal.ack\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    sockets[Text(payload, "toUserId")].Deliver(Event("p2p.signal.ack", new Dictionary<string, object?>
                    {
                        ["sessionId"] = Text(payload, "sessionId"),
                        ["fromUserId"] = from,
                        ["attemptId"] = Text(payload, "attemptId"),
                        ["seq"] = Number(payload, "seq"),
                    }));
                    break;
                default:
                    sockets[from].Deliver("{\"type\":\"" + type + "\",\"requestId\":\"" + requestId + "\",\"success\":true}");
                    break;
            }
        }

        private static string Text(IHaivenJsonNode? node, string name) =>
            node != null && node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.String
                ? value.GetString()
                : "";

        private static long Number(IHaivenJsonNode? node, string name) =>
            node != null && node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.Number
                ? long.Parse(value.GetNumberText(), System.Globalization.CultureInfo.InvariantCulture)
                : 0;

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

    /// <summary>
    /// The lobby's transport over the SDK's session signaling. The signaling
    /// rules themselves (acknowledgement, retries, duplicates, order, epochs)
    /// are proven by the SDK's gaming-p2p-client/v0 conformance suite.
    /// </summary>
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
            var hostManager = Manager("host", new HaivenSessionSignaling(hostChannel, server.SessionId, Host, null), null);
            var guestManager = Manager("guest",
                new HaivenSessionSignaling(guestChannel, server.SessionId, Guest, Host, participationId: "participation-guest"), Host);
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

        private NetworkManager Manager(string name, HaivenSessionSignaling signaling, string? hostUserId)
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
