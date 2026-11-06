#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Haiven.Sdk;
using Haiven.Sdk.WebRtc;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Haiven.Sdk.WebRtc.Tests.PeerTesting;

namespace Haiven.Sdk.WebRtc.Tests
{
    /// <summary>
    /// The transport over direct connections in one process, with no server.
    /// The relay and send-queue tests are in <c>Tests/WebRtcRelay</c>, since
    /// they need a TURN server.
    /// </summary>
    public sealed class PeerTransportTests
    {
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

        private PeerPair Pair(PeerConnectionOptions host, PeerConnectionOptions guest)
        {
            var pair = new PeerPair(new UnityWebRtcPeerTransport(host), new UnityWebRtcPeerTransport(guest));
            links.Add(pair.Host);
            links.Add(pair.Guest);
            return pair;
        }

        [UnityTest]
        public IEnumerator HostRefusesGuestWithAnotherFingerprint()
        {
            var pair = Pair(Direct(), Direct());
            pair.TamperOffer = sdp => Regex.Replace(sdp, @"(a=fingerprint:sha-256 )([0-9A-F]{2})",
                match => match.Groups[1].Value + (match.Groups[2].Value == "00" ? "01" : "00"));
            yield return pair.Connect();
            Assert.That(pair.Host.IsOpen || pair.Guest.IsOpen, Is.False, "a channel opened to an unadmitted guest");
            Assert.That(pair.HostClosed, Is.Not.Null, "the host did not report the failed handshake");
        }
    }
}
