#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.P2P.Tests
{
    /// <summary>Signaling between transports in one process, keyed by user id.</summary>
    internal sealed class LoopbackSignalingHub
    {
        private readonly Dictionary<string, Endpoint> endpoints = new Dictionary<string, Endpoint>();

        public List<(string From, string To, PeerSignalKind Kind)> Sent { get; } =
            new List<(string, string, PeerSignalKind)>();

        public IHaivenPeerSignaling Join(string userId)
        {
            var endpoint = new Endpoint(this, userId);
            endpoints[userId] = endpoint;
            return endpoint;
        }

        public List<string> Candidates { get; } = new List<string>();

        private void Deliver(string from, string to, PeerSignal signal)
        {
            Sent.Add((from, to, signal.Kind));
            if (signal.Candidate != null) Candidates.Add(signal.Candidate.Candidate);
            if (endpoints.TryGetValue(to, out var endpoint)) endpoint.Raise(from, signal);
        }

        private sealed class Endpoint : IHaivenPeerSignaling
        {
            private readonly LoopbackSignalingHub hub;

            public Endpoint(LoopbackSignalingHub hub, string userId)
            {
                this.hub = hub;
                LocalUserId = userId;
            }

            public string LocalUserId { get; }

            public event Action<string, PeerSignal>? Received;

            public void Send(string toUserId, PeerSignal signal) => hub.Deliver(LocalUserId, toUserId, signal);

            public void Raise(string from, PeerSignal signal) => Received?.Invoke(from, signal);
        }
    }

    public sealed class PeerFramingTests
    {
        [Test]
        public void SequencedHeaderKeepsTheLargestNgoBatchUnderTheLimit()
        {
            // NGO rounds its MTU down to a multiple of 8.
            var ngoBatch = PeerFraming.MaxUnreliablePayload & ~7;
            Assert.That(ngoBatch, Is.EqualTo(1192));
            var message = PeerFraming.EncodeSequenced(new ArraySegment<byte>(new byte[PeerFraming.MaxUnreliablePayload]), 7);
            Assert.That(message.Length, Is.EqualTo(HaivenPeerLink.MaxUnreliableMessage));
            Assert.That(PeerFraming.EncodeUnreliable(new ArraySegment<byte>(new byte[ngoBatch])).Length, Is.EqualTo(1193));
        }

        [Test]
        public void SequenceNumbersSurviveWraparound()
        {
            Assert.That(PeerFraming.IsNewer(1, 0), Is.True);
            Assert.That(PeerFraming.IsNewer(0, 65535), Is.True, "0 follows 65535");
            Assert.That(PeerFraming.IsNewer(65535, 0), Is.False);
            Assert.That(PeerFraming.IsNewer(5, 5), Is.False, "a duplicate is not newer");
            Assert.That(PeerFraming.IsNewer(100, 40000), Is.True);
            Assert.That(PeerFraming.IsNewer(40000, 100), Is.False);
        }

        [Test]
        public void ReliableStreamSplitsAt64KiBAndJoinsBack()
        {
            var big = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
            var chunks = PeerFraming.EncodeReliable(new ArraySegment<byte>(big));
            Assert.That(chunks.All(c => c.Length <= HaivenPeerLink.MaxReliableMessage), Is.True);
            Assert.That(chunks.Count, Is.EqualTo(4));

            var small = PeerFraming.EncodeReliable(new ArraySegment<byte>(new byte[] { 9, 8, 7 }));
            var empty = PeerFraming.EncodeReliable(new ArraySegment<byte>(Array.Empty<byte>()));

            // Pieces are cut differently from how they were sent, as a stream may be.
            var stream = chunks.Concat(small).Concat(empty).SelectMany(c => c).ToArray();
            var assembler = new ReliableAssembler();
            var received = new List<byte[]>();
            for (var offset = 0; offset < stream.Length; offset += 777)
                received.AddRange(assembler.Add(stream.Skip(offset).Take(777).ToArray()));

            Assert.That(received.Count, Is.EqualTo(3));
            Assert.That(received[0], Is.EqualTo(big));
            Assert.That(received[1], Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(received[2], Is.Empty);
        }
    }

    /// <summary>
    /// <see cref="HaivenNetworkTransport"/> over two real com.unity.webrtc
    /// connections in one process, first on its own and then under two NGO
    /// NetworkManagers.
    /// </summary>
    public sealed class NetcodeTransportTests
    {
        private const float Timeout = 20f;
        private const string Host = "user-host";
        private const string Guest = "user-guest";

        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void Destroy()
        {
            foreach (var created in objects)
            {
                var manager = created.GetComponent<NetworkManager>();
                if (manager != null && manager.IsListening) manager.Shutdown();
                var transport = created.GetComponent<HaivenNetworkTransport>();
                if (transport != null) transport.Shutdown();
                UnityEngine.Object.Destroy(created);
            }
            objects.Clear();
        }

        [UnityTest]
        public IEnumerator GuestAndHostConnectAndCarryEveryDelivery()
        {
            var hub = new LoopbackSignalingHub();
            var host = Transport(hub, Host, null);
            var guest = Transport(hub, Guest, Host);
            Assert.That(host.StartServer(), Is.True);
            Assert.That(guest.StartClient(), Is.True);

            var hostEvents = new List<Polled>();
            var guestEvents = new List<Polled>();
            yield return Until(() => Poll(host, hostEvents).Any(e => e.Type == NetworkEvent.Connect)
                && Poll(guest, guestEvents).Any(e => e.Type == NetworkEvent.Connect));

            var guestId = hostEvents.Single(e => e.Type == NetworkEvent.Connect).ClientId;
            Assert.That(guestId, Is.Not.EqualTo(host.ServerClientId));
            Assert.That(host.GetUserId(guestId), Is.EqualTo(Guest));
            Assert.That(guestEvents.Single(e => e.Type == NetworkEvent.Connect).ClientId, Is.EqualTo(guest.ServerClientId));
            Assert.That(hub.Sent.First().Kind, Is.EqualTo(PeerSignalKind.Offer), "the guest offers first");
            Assert.That(hub.Sent.Where(s => s.Kind == PeerSignalKind.Offer).All(s => s.From == Guest), Is.True);
            var answerAt = hub.Sent.FindIndex(s => s.Kind == PeerSignalKind.Answer);
            Assert.That(hub.Sent.Take(answerAt).Where(s => s.Kind == PeerSignalKind.Candidate).All(s => s.From == Guest), Is.True,
                "the host sent a candidate before its answer");
            Assert.That(hub.Sent.Any(s => s.Kind == PeerSignalKind.Candidate && s.From == Guest), Is.True,
                "the guest's candidates reached the host");

            var big = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
            guest.Send(guest.ServerClientId, Segment(1, 1192), NetworkDelivery.Unreliable);
            guest.Send(guest.ServerClientId, Segment(2, 1192), NetworkDelivery.UnreliableSequenced);
            guest.Send(guest.ServerClientId, Segment(3, 10), NetworkDelivery.Reliable);
            guest.Send(guest.ServerClientId, Segment(4, 10), NetworkDelivery.ReliableSequenced);
            guest.Send(guest.ServerClientId, new ArraySegment<byte>(big), NetworkDelivery.ReliableFragmentedSequenced);
            host.Send(guestId, Segment(5, 64), NetworkDelivery.Reliable);

            yield return Until(() => Poll(host, hostEvents).Count(e => e.Type == NetworkEvent.Data) == 5
                && Poll(guest, guestEvents).Any(e => e.Type == NetworkEvent.Data));

            var data = hostEvents.Where(e => e.Type == NetworkEvent.Data).Select(e => e.Payload).ToList();
            Assert.That(data.Count(d => d.Length == 1192), Is.EqualTo(2));
            Assert.That(data.Any(d => d.SequenceEqual(Segment(3, 10))), Is.True);
            Assert.That(data.Any(d => d.SequenceEqual(Segment(4, 10))), Is.True);
            Assert.That(data.Single(d => d.Length == big.Length), Is.EqualTo(big));
            Assert.That(guestEvents.Single(e => e.Type == NetworkEvent.Data).Payload, Is.EqualTo(Segment(5, 64).ToArray()));
        }

        [UnityTest]
        public IEnumerator HostHearsWhenTheGuestLeaves()
        {
            var hub = new LoopbackSignalingHub();
            var host = Transport(hub, Host, null);
            var guest = Transport(hub, Guest, Host);
            host.StartServer();
            guest.StartClient();
            var hostEvents = new List<Polled>();
            var guestEvents = new List<Polled>();
            yield return Until(() => Poll(host, hostEvents).Any(e => e.Type == NetworkEvent.Connect)
                && Poll(guest, guestEvents).Any(e => e.Type == NetworkEvent.Connect));

            guest.DisconnectLocalClient();
            yield return Until(() => Poll(host, hostEvents).Any(e => e.Type == NetworkEvent.Disconnect));
            var guestId = hostEvents.First(e => e.Type == NetworkEvent.Connect).ClientId;
            Assert.That(hostEvents.Single(e => e.Type == NetworkEvent.Disconnect).ClientId, Is.EqualTo(guestId));
            Assert.That(host.GetUserId(guestId), Is.Null);
        }

        [UnityTest]
        public IEnumerator NgoClientConnectsToNgoHostAndReceivesALargeMessage()
        {
            var hub = new LoopbackSignalingHub();
            var hostManager = Manager(hub, Host, null);
            var guestManager = Manager(hub, Guest, Host);

            Assert.That(hostManager.StartHost(), Is.True);
            Assert.That(guestManager.StartClient(), Is.True);
            yield return Until(() => guestManager.IsConnectedClient && hostManager.ConnectedClientsIds.Count == 2);
            Assert.That(hostManager.NetworkConfig.NetworkTransport is HaivenNetworkTransport, Is.True);
            Assert.That(hostManager.MaximumTransmissionUnitSize, Is.EqualTo(1192));

            var guestClientId = hostManager.ConnectedClientsIds.Single(id => id != NetworkManager.ServerClientId);
            var hostTransport = (HaivenNetworkTransport)hostManager.NetworkConfig.NetworkTransport;
            // The first guest gets transport client id 1.
            Assert.That(hostTransport.GetUserId(1), Is.EqualTo(Guest));

            // A late-join snapshot is one large ReliableFragmentedSequenced
            // message; this one is bigger than four 64 KiB pieces.
            byte[]? received = null;
            guestManager.CustomMessagingManager.RegisterNamedMessageHandler("haiven-test-big", (_, reader) =>
            {
                reader.ReadValueSafe(out int length);
                received = new byte[length];
                reader.ReadBytesSafe(ref received, length);
            });
            var big = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 13)).ToArray();
            using (var writer = new FastBufferWriter(big.Length + 8, Allocator.Temp))
            {
                writer.WriteValueSafe(big.Length);
                writer.WriteBytesSafe(big);
                hostManager.CustomMessagingManager.SendNamedMessage("haiven-test-big", guestClientId, writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
            yield return Until(() => received != null);
            Assert.That(received, Is.EqualTo(big));

            guestManager.Shutdown();
            yield return Until(() => hostManager.ConnectedClientsIds.Count == 1);
        }

        [UnityTest]
        public IEnumerator NgoSessionRunsRelayOnlyThroughTurn()
        {
            // A session with a child in it: every path goes through coturn.
            var hub = new LoopbackSignalingHub();
            var hostManager = Manager(hub, Host, null, PeerTransportTests.TurnFromEnvironment());
            var guestManager = Manager(hub, Guest, Host, PeerTransportTests.TurnFromEnvironment());

            Assert.That(hostManager.StartHost(), Is.True);
            Assert.That(guestManager.StartClient(), Is.True);
            yield return Until(() => guestManager.IsConnectedClient && hostManager.ConnectedClientsIds.Count == 2);

            var candidates = hub.Candidates;
            Assert.That(candidates, Is.Not.Empty);
            Assert.That(candidates.Where(c => !c.Contains(" typ relay")), Is.Empty, "non-relay candidates were signaled");
        }

        private HaivenNetworkTransport Transport(LoopbackSignalingHub hub, string userId, string? hostUserId)
        {
            var created = new GameObject($"transport {userId}");
            objects.Add(created);
            var transport = created.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = hub.Join(userId);
            transport.HostUserId = hostUserId;
            transport.Initialize();
            return transport;
        }

        private NetworkManager Manager(
            LoopbackSignalingHub hub, string userId, string? hostUserId, PeerConnectionOptions? connection = null)
        {
            var created = new GameObject($"network manager {userId}");
            objects.Add(created);
            var transport = created.AddComponent<HaivenNetworkTransport>();
            transport.Signaling = hub.Join(userId);
            transport.HostUserId = hostUserId;
            if (connection != null) transport.Connection = connection;
            var manager = created.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                EnableSceneManagement = false,
            };
            return manager;
        }

        private static ArraySegment<byte> Segment(byte first, int length) =>
            new ArraySegment<byte>(Enumerable.Range(0, length).Select(i => (byte)(first + i)).ToArray());

        private static List<Polled> Poll(HaivenNetworkTransport transport, List<Polled> into)
        {
            NetworkEvent type;
            while ((type = transport.PollEvent(out var clientId, out var payload, out _)) != NetworkEvent.Nothing)
                into.Add(new Polled(type, clientId, payload.Array == null ? Array.Empty<byte>() : payload.ToArray()));
            return into;
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + Timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out");
                yield return null;
            }
        }

        private readonly struct Polled
        {
            public Polled(NetworkEvent type, ulong clientId, byte[] payload)
            {
                Type = type;
                ClientId = clientId;
                Payload = payload;
            }

            public NetworkEvent Type { get; }

            public ulong ClientId { get; }

            public byte[] Payload { get; }
        }
    }
}
