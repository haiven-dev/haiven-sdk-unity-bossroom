#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Haiven.Sdk;
using Haiven.Sdk.WebRtc;
using NUnit.Framework;
using UnityEngine;
using Haiven.Sdk.WebRtc.Tests;
using UnityEngine.TestTools;
using static Haiven.Sdk.WebRtc.Tests.PeerTesting;

namespace Haiven.Sdk.WebRtc.RelayTests
{
    /// <summary>
    /// The transport through a TURN relay. They need a TURN server with a REST
    /// shared secret, given as HAIVEN_TEST_TURN_URL and HAIVEN_TEST_TURN_SECRET,
    /// and are ignored without them. libwebrtc does not use the loopback
    /// interface, so the URL names a LAN address, for example
    /// turn:192.168.1.10:3478. The queue test needs the server to cap a
    /// session's rate (coturn --max-bps=262144) so that the queue fills.
    ///
    /// A separate assembly, so the strict engine harness, which selects
    /// <c>Haiven.Sdk.Tests.*</c> and fails on any ignored test, leaves them out.
    /// </summary>
    public sealed class RelayTransportTests
    {
        private readonly List<HaivenPeerLink> links = new List<HaivenPeerLink>();

        [TearDown]
        public void CloseLinks()
        {
            foreach (var link in links) link.Dispose();
            links.Clear();
        }

        [UnityTest]
        public IEnumerator FullSendQueueRefusesReliableAndDropsUnreliable()
        {
            // In one process over loopback the library hands each message to the
            // peer inside Send, so nothing ever queues. A TURN server that caps
            // the session's rate (coturn --max-bps) gives the queue a slow link.
            var pair = Pair(TurnFromEnvironment(), TurnFromEnvironment());
            yield return pair.Connect();
            Assert.That(pair.Guest.IsOpen, Is.True, pair.Failure);

            // Sent within one frame, so the link cannot drain much between
            // sends: 1 MiB is sixteen 64 KiB messages.
            var sent = 0;
            long deepest = 0;
            PeerSendResult result;
            while ((result = pair.Guest.Send(Bytes(65536, (byte)sent), reliable: true)) == PeerSendResult.Sent)
            {
                sent++;
                deepest = Math.Max(deepest, pair.Guest.Transport.BufferedAmount(PeerChannel.Reliable));
                if (sent > 1000) Assert.Fail($"the send queue never filled; deepest buffered amount {deepest}");
            }
            Assert.That(result, Is.EqualTo(PeerSendResult.QueueFull));
            Assert.That(sent, Is.InRange(16, 64), "reliable messages accepted before the queue filled");
            Assert.That(pair.Guest.Send(Bytes(1200, 5), reliable: false), Is.EqualTo(PeerSendResult.Dropped));
            Debug.Log($"[haiven-webrtc] reliable 64 KiB messages accepted before send_queue_full: {sent}");

            // Everything the queue accepted arrives, in order. About 3.3 MiB
            // through a 256 KB/s relay takes 13 to 20 seconds.
            yield return Until(() => pair.HostReceived.Count(m => m.Reliable) == sent, 60f);
            var reliable = pair.HostReceived.Where(m => m.Reliable).ToList();
            for (var i = 0; i < sent; i++) Assert.That(reliable[i].Payload[0], Is.EqualTo((byte)i));
            Assert.That(pair.Guest.Send(Bytes(65536, 6), reliable: true), Is.EqualTo(PeerSendResult.Sent));
        }

        [UnityTest]
        public IEnumerator RelayOnlyConnectsThroughTurnWithRelayCandidatesOnly()
        {
            var pair = Pair(TurnFromEnvironment(), TurnFromEnvironment());
            yield return pair.Connect();
            Assert.That(pair.Host.IsOpen && pair.Guest.IsOpen, Is.True, pair.Failure);

            var candidates = pair.HostCandidates.Concat(pair.GuestCandidates).ToList();
            Assert.That(candidates, Is.Not.Empty);
            Assert.That(candidates.Where(c => !c.Contains(" typ relay")), Is.Empty, "non-relay candidates gathered");

            Assert.That(pair.Guest.Send(Bytes(1200, 7), reliable: false), Is.EqualTo(PeerSendResult.Sent));
            Assert.That(pair.Guest.Send(Bytes(65536, 8), reliable: true), Is.EqualTo(PeerSendResult.Sent));
            yield return Until(() => pair.HostReceived.Count == 2);
            Assert.That(pair.HostReceived.Single(m => m.Reliable).Payload, Is.EqualTo(Bytes(65536, 8)));
        }

        [UnityTest, Explicit("holds a relayed connection for twelve minutes"), Timeout(900000)]
        public IEnumerator RelayedConnectionOutlivesItsCredential()
        {
            // haiven-api issues a 10-minute credential and says to fetch a new one
            // after 8. This shows what happens to an open connection when the
            // credential it allocated with expires and is never replaced: libwebrtc
            // refreshes permissions after 4 minutes and the allocation before its
            // 10-minute lifetime, each time with the same username.
            const int lifetime = 120;
            // Past the allocation refresh (lifetime 600 s, refreshed a minute
            // early) and the channel binding refresh at 10 minutes.
            const float hold = 720f;
            var pair = Pair(TurnFromEnvironment(lifetime), TurnFromEnvironment(lifetime));
            yield return pair.Connect();
            Assert.That(pair.Host.IsOpen && pair.Guest.IsOpen, Is.True, pair.Failure);

            var start = Time.realtimeSinceStartup;
            var nextSend = start;
            var lastArrival = start;
            var seen = 0;
            while (Time.realtimeSinceStartup - start < hold && pair.HostClosed == null && pair.GuestClosed == null)
            {
                if (Time.realtimeSinceStartup >= nextSend)
                {
                    pair.Guest.Send(Bytes(16, 9), reliable: false);
                    nextSend += 1f;
                }
                if (pair.HostReceived.Count > seen)
                {
                    seen = pair.HostReceived.Count;
                    lastArrival = Time.realtimeSinceStartup;
                }
                yield return null;
            }
            var report = $"credential expired at {lifetime} s; {seen} messages arrived, the last at "
                + $"{lastArrival - start:F0} s; held {Time.realtimeSinceStartup - start:F0} s; {pair.Failure}";
            Debug.Log("[haiven-webrtc] " + report);
            Assert.That(lastArrival - start, Is.GreaterThan(hold - 5), report);
        }

        private PeerPair Pair(PeerConnectionOptions host, PeerConnectionOptions guest)
        {
            var pair = new PeerPair(new UnityWebRtcPeerTransport(host), new UnityWebRtcPeerTransport(guest));
            links.Add(pair.Host);
            links.Add(pair.Guest);
            return pair;
        }
    }
}
