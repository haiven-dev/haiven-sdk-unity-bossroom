#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Haiven.Demo.BossRoom.P2P;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.P2P.Tests
{
    /// <summary>
    /// Two real com.unity.webrtc connections in one process, wired to each other
    /// the way session signaling wires a guest to a host: the guest offers, the
    /// host answers, and candidates wait until the remote description is applied
    /// (SDK-P2P-8).
    ///
    /// The relay and send-queue tests need a TURN server with a REST shared
    /// secret, given as HAIVEN_TEST_TURN_URL and HAIVEN_TEST_TURN_SECRET, and
    /// are ignored without them. libwebrtc does not use the loopback interface,
    /// so the URL names a LAN address, for example turn:192.168.1.10:3478. The
    /// queue test needs the server to cap a session's rate (coturn
    /// --max-bps=262144) so that the queue fills.
    /// </summary>
    public sealed class PeerTransportTests
    {
        private const float Timeout = 20f;

        private readonly List<HaivenPeerLink> links = new List<HaivenPeerLink>();

        [TearDown]
        public void CloseLinks()
        {
            foreach (var link in links) link.Dispose();
            links.Clear();
        }

        [UnityTest]
        public IEnumerator BothChannelsOpenAndCarryMessagesBothWays()
        {
            var pair = Pair(Direct(), Direct());
            yield return pair.Connect();
            Assert.That(pair.Host.IsOpen && pair.Guest.IsOpen, Is.True, pair.Failure);

            Assert.That(pair.Guest.Send(Bytes(10, 1), reliable: true), Is.EqualTo(PeerSendResult.Sent));
            Assert.That(pair.Host.Send(Bytes(20, 2), reliable: false), Is.EqualTo(PeerSendResult.Sent));
            yield return Until(() => pair.HostReceived.Count == 1 && pair.GuestReceived.Count == 1);

            Assert.That(pair.HostReceived[0].Reliable, Is.True);
            Assert.That(pair.HostReceived[0].Payload, Is.EqualTo(Bytes(10, 1)));
            Assert.That(pair.GuestReceived[0].Reliable, Is.False);
            Assert.That(pair.GuestReceived[0].Payload, Is.EqualTo(Bytes(20, 2)));
        }

        [UnityTest]
        public IEnumerator MessageSizeLimits()
        {
            var pair = Pair(Direct(), Direct());
            yield return pair.Connect();
            Assert.That(pair.Guest.IsOpen, Is.True, pair.Failure);

            var guest = pair.Guest;
            Assert.That(guest.Send(Bytes(1200, 3), reliable: false), Is.EqualTo(PeerSendResult.Sent));
            Assert.That(guest.Send(Bytes(1201, 3), reliable: false), Is.EqualTo(PeerSendResult.TooLarge));
            Assert.That(guest.Send(Bytes(65536, 4), reliable: true), Is.EqualTo(PeerSendResult.Sent));
            Assert.That(guest.Send(Bytes(65537, 4), reliable: true), Is.EqualTo(PeerSendResult.TooLarge));

            yield return Until(() => pair.HostReceived.Any(m => m.Reliable));
            Assert.That(pair.HostReceived.Single(m => m.Reliable).Payload, Is.EqualTo(Bytes(65536, 4)));
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
        public IEnumerator PeerWithAnotherFingerprintIsRefused()
        {
            // The host's answer reaches the guest with a fingerprint that is not
            // the host's certificate: what a peer that did not come through
            // session signaling looks like (SDK-P2P-13, unadmitted_peer_refused).
            var pair = Pair(Direct(), Direct());
            pair.TamperAnswer = sdp => Regex.Replace(sdp, @"(a=fingerprint:sha-256 )([0-9A-F]{2})",
                match => match.Groups[1].Value + (match.Groups[2].Value == "00" ? "01" : "00"));
            yield return pair.Connect();

            Assert.That(pair.Host.IsOpen || pair.Guest.IsOpen, Is.False, "a channel opened to an unadmitted peer");
            Assert.That(pair.GuestClosed, Is.Not.Null, "the guest did not report the failed handshake");
            Debug.Log($"[haiven-webrtc] tampered fingerprint: guest closed with '{pair.GuestClosed}'");
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

        private static PeerConnectionOptions Direct() =>
            new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

        // The TURN REST scheme haiven-api issues: username "<expiry>:<id>",
        // password base64(HMAC-SHA1(secret, username)).
        internal static PeerConnectionOptions TurnFromEnvironment()
        {
            var url = Environment.GetEnvironmentVariable("HAIVEN_TEST_TURN_URL");
            var secret = Environment.GetEnvironmentVariable("HAIVEN_TEST_TURN_SECRET");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret))
                Assert.Ignore("HAIVEN_TEST_TURN_URL and HAIVEN_TEST_TURN_SECRET are not set");

            var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
            var username = $"{expiry}:{Guid.NewGuid()}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var password = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
            return new PeerConnectionOptions(
                new[] { new PeerIceServer(new[] { url! }, username, password) }, PeerIcePolicy.Relay);
        }

        private static byte[] Bytes(int length, byte first)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)(first + i);
            return bytes;
        }

        private static IEnumerator Until(Func<bool> condition, float timeout = Timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out");
                yield return null;
            }
        }

        private PeerPair Pair(PeerConnectionOptions host, PeerConnectionOptions guest)
        {
            var pair = new PeerPair(new UnityWebRtcPeerTransport(host), new UnityWebRtcPeerTransport(guest));
            links.Add(pair.Host);
            links.Add(pair.Guest);
            return pair;
        }

        private readonly struct Received
        {
            public Received(byte[] payload, bool reliable)
            {
                Payload = payload;
                Reliable = reliable;
            }

            public byte[] Payload { get; }

            public bool Reliable { get; }
        }

        private sealed class PeerPair
        {
            private readonly List<PeerIceCandidate> toHost = new List<PeerIceCandidate>();
            private readonly List<PeerIceCandidate> toGuest = new List<PeerIceCandidate>();
            private bool hostHasRemote;
            private bool guestHasRemote;

            public PeerPair(IHaivenPeerTransport host, IHaivenPeerTransport guest)
            {
                Host = new HaivenPeerLink(host);
                Guest = new HaivenPeerLink(guest);
                Host.Message += (payload, reliable) => HostReceived.Add(new Received(payload, reliable));
                Guest.Message += (payload, reliable) => GuestReceived.Add(new Received(payload, reliable));
                Host.Closed += reason => HostClosed = reason;
                Guest.Closed += reason => GuestClosed = reason;

                host.LocalCandidate += candidate =>
                {
                    HostCandidates.Add(candidate.Candidate);
                    Deliver(candidate, Guest, toGuest, guestHasRemote);
                };
                guest.LocalCandidate += candidate =>
                {
                    GuestCandidates.Add(candidate.Candidate);
                    Deliver(candidate, Host, toHost, hostHasRemote);
                };
            }

            public HaivenPeerLink Host { get; }

            public HaivenPeerLink Guest { get; }

            public Func<string, string> TamperAnswer { get; set; } = sdp => sdp;

            public List<Received> HostReceived { get; } = new List<Received>();

            public List<Received> GuestReceived { get; } = new List<Received>();

            public List<string> HostCandidates { get; } = new List<string>();

            public List<string> GuestCandidates { get; } = new List<string>();

            public string? HostClosed { get; private set; }

            public string? GuestClosed { get; private set; }

            public string Failure => $"host closed: {HostClosed ?? "no"}, guest closed: {GuestClosed ?? "no"}";

            /// <summary>Runs the exchange, then waits until both links open or either closes.</summary>
            public IEnumerator Connect()
            {
                var exchange = Exchange();
                var deadline = Time.realtimeSinceStartup + Timeout;
                while (!exchange.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (exchange.IsFaulted) Assert.Fail(exchange.Exception!.ToString());
                while (!(Host.IsOpen && Guest.IsOpen) && HostClosed == null && GuestClosed == null
                    && Time.realtimeSinceStartup < deadline)
                    yield return null;
            }

            private async Task Exchange()
            {
                var offer = await Guest.Transport.CreateOfferAsync();
                var answer = await Host.Transport.AcceptOfferAsync(offer);
                hostHasRemote = true;
                Flush(Host, toHost);
                await Guest.Transport.AcceptAnswerAsync(TamperAnswer(answer));
                guestHasRemote = true;
                Flush(Guest, toGuest);
            }

            private static void Deliver(
                PeerIceCandidate candidate, HaivenPeerLink to, List<PeerIceCandidate> waiting, bool ready)
            {
                if (ready) to.Transport.AddRemoteCandidate(candidate);
                else waiting.Add(candidate);
            }

            private static void Flush(HaivenPeerLink to, List<PeerIceCandidate> waiting)
            {
                foreach (var candidate in waiting) to.Transport.AddRemoteCandidate(candidate);
                waiting.Clear();
            }
        }
    }
}
