// Vendored from sdks/dotnet/Haiven.Sdk/PeerConnections.cs by scripts/vendor-unity.py.
// Edit or regenerate the source, then run `make vendor-unity`.
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Haiven.Sdk
{
    /// <summary>Guest-offered peer connections, independent of the game engine and networking stack.</summary>
    public sealed class HaivenPeerConnections : IHaivenPeerRoster, IDisposable
    {
        public const double ConnectTimeout = 20;
        public const int MaxPendingCandidates = 64;

        private readonly IHaivenPeerSignaling signaling;
        private readonly Func<PeerConnectionOptions, IHaivenPeerTransport> createTransport;
        private readonly Func<double> clock;
        private readonly Dictionary<string, Attempt> peers = new Dictionary<string, Attempt>();
        private bool running;
        private bool hosting;
        private string? hostUserId;

        public HaivenPeerConnections(IHaivenPeerSignaling signaling,
            Func<PeerConnectionOptions, IHaivenPeerTransport> createTransport, Func<double>? clock = null)
        {
            this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
            this.createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
            this.clock = clock ?? HaivenSessionSignaling.MonotonicSeconds();
        }

        /// <summary>Settings for the next connection; existing connections keep theirs.</summary>
        public PeerConnectionOptions Connection { get; set; } =
            new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

        /// <summary>A new attempt, before its asynchronous description exchange starts.</summary>
        public event Action<string, HaivenPeerLink>? Started;

        /// <summary>Both channels opened after the signaled remote description was applied.</summary>
        public event Action<string>? Connected;

        /// <summary>An attempt ended, including one that never connected. Reasons contain no SDP or library exception text.</summary>
        public event Action<string, string>? Disconnected;

        public bool Start(bool asHost, string? hostUserId = null)
        {
            if (running) return false;
            var lifecycle = signaling as IHaivenPeerSignalingLifecycle;
            if (lifecycle != null && !lifecycle.Start(asHost)) return false;
            var host = hostUserId ?? lifecycle?.HostUserId;
            if (!asHost && string.IsNullOrEmpty(host))
            {
                lifecycle?.Stop();
                return false;
            }
            hosting = asHost;
            this.hostUserId = host;
            running = true;
            signaling.Received += OnSignal;
            if (lifecycle != null) lifecycle.HostUnreachable += OnHostUnreachable;
            if (signaling is HaivenSessionSignaling session)
            {
                session.AttemptStarted += OnAttemptStarted;
                session.Refused += OnRefused;
                session.Abandoned += OnAbandoned;
            }
            if (!hosting)
            {
                var peer = Add(host!);
                if (peer != null) _ = Offer(peer);
            }
            return true;
        }

        public void Tick()
        {
            if (!running) return;
            if (signaling is IHaivenPeerSignalingLifecycle lifecycle) lifecycle.Poll();
            foreach (var peer in new List<Attempt>(peers.Values))
                if (!peer.Connected && clock() >= peer.Deadline) Drop(peer, "connect_timeout");
        }

        public IEnumerable<string> ConnectedUsers()
        {
            var users = new List<string>();
            // Include pending peers so a roster read can revoke them before they open.
            foreach (var peer in peers.Values) users.Add(peer.UserId);
            return users;
        }

        public bool DropUser(string userId)
        {
            if (!peers.TryGetValue(userId, out var peer)) return false;
            Drop(peer, "removed");
            return true;
        }

        /// <summary>Removes an attempt and transfers its link to the caller, which must dispose it after its goodbye drains.</summary>
        public HaivenPeerLink? Detach(string userId)
        {
            if (!peers.TryGetValue(userId, out var peer)) return null;
            peers.Remove(userId);
            peer.Lost = true;
            peer.PendingCandidates.Clear();
            peer.OutgoingCandidates.Clear();
            if (signaling is HaivenSessionSignaling session) session.ForgetPeer(userId);
            return peer.Link;
        }

        public void Dispose()
        {
            if (!running) return;
            running = false;
            signaling.Received -= OnSignal;
            if (signaling is IHaivenPeerSignalingLifecycle lifecycle)
            {
                lifecycle.HostUnreachable -= OnHostUnreachable;
                lifecycle.Stop();
            }
            if (signaling is HaivenSessionSignaling session)
            {
                session.AttemptStarted -= OnAttemptStarted;
                session.Refused -= OnRefused;
                session.Abandoned -= OnAbandoned;
            }
            foreach (var peer in new List<Attempt>(peers.Values)) Drop(peer, "disposed");
        }

        private void OnAttemptStarted(string userId)
        {
            if (!hosting) return;
            if (peers.TryGetValue(userId, out var old)) Drop(old, "replaced", forget: false);
            Add(userId);
        }

        private void OnSignal(string userId, PeerSignal signal)
        {
            if (!running || (!hosting && userId != hostUserId)) return;
            peers.TryGetValue(userId, out var peer);
            if (signal.Kind == PeerSignalKind.Offer)
            {
                if (!hosting) return;
                if (peer != null && peer.RemoteStarted)
                {
                    Drop(peer, "replaced", forget: false);
                    peer = null;
                }
                peer ??= Add(userId);
                if (peer == null) return;
                peer.RemoteStarted = true;
                _ = Answer(peer, signal.Sdp!);
            }
            else if (peer != null && signal.Kind == PeerSignalKind.Answer)
            {
                if (hosting || peer.RemoteStarted) return;
                peer.RemoteStarted = true;
                _ = ApplyAnswer(peer, signal.Sdp!);
            }
            else if (peer != null && signal.Candidate != null)
            {
                if (peer.RemoteApplied)
                {
                    try { peer.Link.Transport.AddRemoteCandidate(signal.Candidate); }
                    catch (Exception) { Drop(peer, "candidate_failed"); }
                }
                else Buffer(peer, peer.PendingCandidates, signal.Candidate);
            }
        }

        private Attempt? Add(string userId)
        {
            HaivenPeerLink link;
            try { link = new HaivenPeerLink(createTransport(Connection)); }
            catch (Exception)
            {
                if (signaling is HaivenSessionSignaling session) session.ForgetPeer(userId);
                Disconnected?.Invoke(userId, "transport_failed");
                return null;
            }
            var peer = new Attempt(userId, link, clock() + ConnectTimeout);
            peers[userId] = peer;
            link.Transport.LocalCandidate += candidate =>
            {
                if (peer.Lost) return;
                if (peer.LocalSent) signaling.Send(userId, PeerSignal.ForCandidate(candidate));
                else Buffer(peer, peer.OutgoingCandidates, candidate);
            };
            link.Opened += () => PublishConnected(peer);
            link.Closed += _ => Drop(peer, "transport_closed");
            Started?.Invoke(userId, link);
            return peer;
        }

        private void Buffer(Attempt peer, List<PeerIceCandidate> buffer, PeerIceCandidate candidate)
        {
            if (buffer.Count >= MaxPendingCandidates) Drop(peer, "candidate_buffer_full");
            else buffer.Add(candidate);
        }

        private async Task Offer(Attempt peer)
        {
            try
            {
                var offer = await peer.Link.Transport.CreateOfferAsync();
                if (peer.Lost) return;
                signaling.Send(peer.UserId, PeerSignal.Offer(offer));
                LocalSent(peer);
            }
            catch (Exception) { Drop(peer, "offer_failed"); }
        }

        private async Task Answer(Attempt peer, string sdp)
        {
            try
            {
                var answer = await peer.Link.Transport.AcceptOfferAsync(sdp);
                if (peer.Lost) return;
                RemoteApplied(peer);
                if (peer.Lost) return;
                signaling.Send(peer.UserId, PeerSignal.Answer(answer));
                LocalSent(peer);
            }
            catch (Exception) { Drop(peer, "answer_failed"); }
        }

        private async Task ApplyAnswer(Attempt peer, string sdp)
        {
            try
            {
                await peer.Link.Transport.AcceptAnswerAsync(sdp);
                if (!peer.Lost) RemoteApplied(peer);
            }
            catch (Exception) { Drop(peer, "answer_failed"); }
        }

        private void RemoteApplied(Attempt peer)
        {
            peer.RemoteApplied = true;
            foreach (var candidate in new List<PeerIceCandidate>(peer.PendingCandidates))
            {
                if (peer.Lost) return;
                peer.Link.Transport.AddRemoteCandidate(candidate);
            }
            peer.PendingCandidates.Clear();
            PublishConnected(peer);
        }

        private void LocalSent(Attempt peer)
        {
            if (peer.Lost) return;
            peer.LocalSent = true;
            foreach (var candidate in new List<PeerIceCandidate>(peer.OutgoingCandidates))
            {
                if (peer.Lost) return;
                signaling.Send(peer.UserId, PeerSignal.ForCandidate(candidate));
            }
            peer.OutgoingCandidates.Clear();
        }

        private void PublishConnected(Attempt peer)
        {
            // ChannelOpened promises DTLS matched the applied SDP (SDK-P2P-13).
            if (peer.Lost || peer.Connected || !peer.RemoteApplied || !peer.Link.IsOpen) return;
            if (clock() >= peer.Deadline)
            {
                Drop(peer, "connect_timeout");
                return;
            }
            peer.Connected = true;
            Connected?.Invoke(peer.UserId);
        }

        private void OnHostUnreachable(string reason)
        {
            if (!hosting && hostUserId != null && peers.TryGetValue(hostUserId, out var peer) && !peer.Connected)
                Drop(peer, "host_unreachable");
        }

        private void OnRefused(string userId, string reason)
        {
            if (peers.TryGetValue(userId, out var peer) && !peer.Connected) Drop(peer, reason);
        }

        private void OnAbandoned(string userId, int seq) => OnRefused(userId, "signal_timeout");

        private void Drop(Attempt peer, string reason, bool forget = true)
        {
            if (peer.Lost) return;
            if (forget) Detach(peer.UserId);
            else
            {
                peers.Remove(peer.UserId);
                peer.Lost = true;
            }
            peer.Link.Dispose();
            Disconnected?.Invoke(peer.UserId, reason);
        }

        private sealed class Attempt
        {
            public Attempt(string userId, HaivenPeerLink link, double deadline)
            {
                UserId = userId;
                Link = link;
                Deadline = deadline;
            }

            public string UserId { get; }
            public HaivenPeerLink Link { get; }
            public double Deadline { get; }
            public List<PeerIceCandidate> PendingCandidates { get; } = new List<PeerIceCandidate>();
            public List<PeerIceCandidate> OutgoingCandidates { get; } = new List<PeerIceCandidate>();
            public bool LocalSent { get; set; }
            public bool RemoteStarted { get; set; }
            public bool RemoteApplied { get; set; }
            public bool Connected { get; set; }
            public bool Lost { get; set; }
        }
    }
}
