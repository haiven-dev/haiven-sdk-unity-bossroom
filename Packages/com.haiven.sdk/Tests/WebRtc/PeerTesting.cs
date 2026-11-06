#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.WebRtc;
using NUnit.Framework;
using UnityEngine;

namespace Haiven.Sdk.WebRtc.Tests
{
    /// <summary>
    /// Two real com.unity.webrtc connections in one process, wired to each other
    /// the way session signaling wires a guest to a host: the guest offers, the
    /// host answers, and candidates wait until the remote description is applied
    /// (SDK-P2P-8). Shared by the direct tests and the relay tests.
    /// </summary>
    public static class PeerTesting
    {
        public const float Timeout = 20f;

        public static PeerConnectionOptions Direct() =>
            new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

        // The TURN REST scheme haiven-api issues: username "<expiry>:<id>",
        // password base64(HMAC-SHA1(secret, username)).
        public static PeerConnectionOptions TurnFromEnvironment(int lifetimeSeconds = 600)
        {
            var url = Environment.GetEnvironmentVariable("HAIVEN_TEST_TURN_URL");
            var secret = Environment.GetEnvironmentVariable("HAIVEN_TEST_TURN_SECRET");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret))
                Assert.Ignore("HAIVEN_TEST_TURN_URL and HAIVEN_TEST_TURN_SECRET are not set");

            var expiry = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds();
            var username = $"{expiry}:{Guid.NewGuid()}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var password = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
            return new PeerConnectionOptions(
                new[] { new PeerIceServer(new[] { url! }, username, password) }, PeerIcePolicy.Relay);
        }

        public static byte[] Bytes(int length, byte first)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)(first + i);
            return bytes;
        }

        public static IEnumerator Until(Func<bool> condition, float timeout = Timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out");
                yield return null;
            }
        }


        public readonly struct Received
        {
            public Received(byte[] payload, bool reliable)
            {
                Payload = payload;
                Reliable = reliable;
            }

            public byte[] Payload { get; }

            public bool Reliable { get; }
        }

        public sealed class PeerPair
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

            public Func<string, string> TamperOffer { get; set; } = sdp => sdp;

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
                var answer = await Host.Transport.AcceptOfferAsync(TamperOffer(offer));
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
