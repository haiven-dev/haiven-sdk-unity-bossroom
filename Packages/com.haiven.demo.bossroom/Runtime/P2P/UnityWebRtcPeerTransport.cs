#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Unity.WebRTC;

namespace Haiven.Demo.BossRoom.P2P
{
    /// <summary>
    /// <see cref="IHaivenPeerTransport"/> over <c>com.unity.webrtc</c>.
    ///
    /// Create and use it on the Unity main thread. The package delivers its
    /// callbacks there, and the async methods wait for its operations by
    /// yielding to the main thread's synchronization context each frame.
    /// </summary>
    public sealed class UnityWebRtcPeerTransport : IHaivenPeerTransport
    {
        private readonly RTCPeerConnection connection;
        private readonly RTCDataChannel reliable;
        private readonly RTCDataChannel unreliable;
        private bool closed;
        private bool measuring;

        public UnityWebRtcPeerTransport(PeerConnectionOptions options)
        {
            var configuration = new RTCConfiguration
            {
                iceServers = options.IceServers.Select(server => new RTCIceServer
                {
                    urls = server.Urls.ToArray(),
                    username = server.Username,
                    credential = server.Credential,
                    credentialType = RTCIceCredentialType.Password,
                }).ToArray(),
                iceTransportPolicy = options.Policy == PeerIcePolicy.Relay
                    ? RTCIceTransportPolicy.Relay
                    : RTCIceTransportPolicy.All,
            };
            connection = new RTCPeerConnection(ref configuration);
            connection.OnIceCandidate = candidate => LocalCandidate?.Invoke(
                new PeerIceCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex));
            connection.OnConnectionStateChange = state =>
            {
                if (state == RTCPeerConnectionState.Failed) Close("connection failed");
                else if (state == RTCPeerConnectionState.Closed) Close("connection closed");
            };

            // Negotiated with fixed ids, so neither side waits for an in-band
            // datachannel event and both create the same pair.
            reliable = Open(PeerChannel.Reliable, "haiven-reliable",
                new RTCDataChannelInit { negotiated = true, id = 0, ordered = true });
            unreliable = Open(PeerChannel.Unreliable, "haiven-unreliable",
                new RTCDataChannelInit { negotiated = true, id = 1, ordered = false, maxRetransmits = 0 });
        }

        public event Action<PeerIceCandidate>? LocalCandidate;

        public event Action<PeerChannel>? ChannelOpened;

        public event Action<PeerChannel, byte[]>? Received;

        public event Action<string>? Closed;

        public async Task<string> CreateOfferAsync()
        {
            var created = connection.CreateOffer();
            await Wait(created);
            if (created.IsError) throw Failure("create offer", created.Error);
            var offer = created.Desc;
            await SetLocal(offer);
            return offer.sdp;
        }

        public async Task<string> AcceptOfferAsync(string offerSdp)
        {
            await SetRemote(RTCSdpType.Offer, offerSdp);
            var created = connection.CreateAnswer();
            await Wait(created);
            if (created.IsError) throw Failure("create answer", created.Error);
            var answer = created.Desc;
            await SetLocal(answer);
            return answer.sdp;
        }

        public Task AcceptAnswerAsync(string answerSdp) => SetRemote(RTCSdpType.Answer, answerSdp);

        public void AddRemoteCandidate(PeerIceCandidate candidate)
        {
            // Signaling can still deliver a candidate after a failed handshake
            // closed the connection; the library throws on a disposed one.
            if (closed) return;
            connection.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex,
            }));
        }

        public bool Send(PeerChannel channel, byte[] payload)
        {
            if (closed) return false;
            var target = channel == PeerChannel.Reliable ? reliable : unreliable;
            if (target.ReadyState != RTCDataChannelState.Open) return false;
            try
            {
                target.Send(payload);
                return true;
            }
            catch (InvalidOperationException)
            {
                // The channel closed between the state check and the send.
                return false;
            }
        }

        public long BufferedAmount(PeerChannel channel) =>
            closed ? 0 : (long)(channel == PeerChannel.Reliable ? reliable : unreliable).BufferedAmount;

        public long RoundTripTimeMs { get; private set; } = -1;

        public async void RefreshRoundTripTime()
        {
            if (closed || measuring) return;
            measuring = true;
            try
            {
                var request = connection.GetStats();
                await Wait(request);
                if (closed || request.IsError || request.Value == null) return;
                using var report = request.Value;
                var seconds = SelectedPairRoundTrip(report);
                if (seconds != null) RoundTripTimeMs = (long)Math.Ceiling(seconds.Value * 1000);
            }
            finally
            {
                measuring = false;
            }
        }

        // The transport stats name the candidate pair in use; its
        // currentRoundTripTime comes from STUN consent checks, about one a second.
        private static double? SelectedPairRoundTrip(RTCStatsReport report)
        {
            var selected = report.Stats.Values.OfType<RTCTransportStats>()
                .Select(stats => stats.selectedCandidatePairId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            var pair = selected != null && report.Stats.TryGetValue(selected, out var named)
                ? named as RTCIceCandidatePairStats
                : report.Stats.Values.OfType<RTCIceCandidatePairStats>()
                    .FirstOrDefault(candidate => candidate.nominated && candidate.state == "succeeded");
            if (pair == null || !pair.Dict.ContainsKey("currentRoundTripTime")) return null;
            return pair.currentRoundTripTime;
        }

        public void Dispose() => Close("disposed");

        private RTCDataChannel Open(PeerChannel channel, string label, RTCDataChannelInit init)
        {
            var created = connection.CreateDataChannel(label, init);
            created.OnOpen = () => ChannelOpened?.Invoke(channel);
            created.OnMessage = bytes => Received?.Invoke(channel, bytes);
            created.OnClose = () => Close(label + " closed");
            return created;
        }

        private async Task SetLocal(RTCSessionDescription description)
        {
            var set = connection.SetLocalDescription(ref description);
            await Wait(set);
            if (set.IsError) throw Failure("set local description", set.Error);
        }

        private async Task SetRemote(RTCSdpType type, string sdp)
        {
            var description = new RTCSessionDescription { type = type, sdp = sdp };
            var set = connection.SetRemoteDescription(ref description);
            await Wait(set);
            if (set.IsError) throw Failure("set remote description", set.Error);
        }

        private static async Task Wait(AsyncOperationBase operation)
        {
            while (!operation.IsDone) await Task.Yield();
        }

        private static InvalidOperationException Failure(string step, RTCError error) =>
            new InvalidOperationException($"{step} failed: {error.errorType} {error.message}");

        private void Close(string reason)
        {
            if (closed) return;
            closed = true;
            reliable.Close();
            unreliable.Close();
            connection.Close();
            reliable.Dispose();
            unreliable.Dispose();
            connection.Dispose();
            Closed?.Invoke(reason);
        }
    }
}
