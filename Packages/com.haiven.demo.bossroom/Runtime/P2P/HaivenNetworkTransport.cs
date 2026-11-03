#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace Haiven.Demo.BossRoom.P2P
{
    /// <summary>
    /// An NGO <see cref="NetworkTransport"/> over WebRTC data channels: one peer
    /// connection per host-guest pair, none between guests (ADR 0036 point 1).
    ///
    /// The guest offers and the host answers. Signaling goes through
    /// <see cref="Signaling"/>, so NGO client ids map to the user ids that
    /// signaling names; <see cref="GetUserId"/> returns them. Set
    /// <see cref="Signaling"/>, <see cref="Connection"/> and, on a guest,
    /// <see cref="HostUserId"/> before starting NGO.
    ///
    /// This is the Boss Room experiment. It moves into the SDK once it works
    /// end to end.
    /// </summary>
    public sealed class HaivenNetworkTransport : NetworkTransport
    {
        private const ulong HostClientId = 0;

        private readonly Queue<PendingEvent> events = new Queue<PendingEvent>();
        private readonly Dictionary<ulong, Peer> peersByClient = new Dictionary<ulong, Peer>();
        private readonly Dictionary<string, Peer> peersByUser = new Dictionary<string, Peer>();
        private const float RoundTripInterval = 1f;

        private ulong nextClientId = 1;
        private float nextRoundTrip;
        private bool isServer;
        private bool started;

        public IHaivenPeerSignaling? Signaling { get; set; }

        /// <summary>ICE servers and policy for every connection this transport creates.</summary>
        public PeerConnectionOptions Connection { get; set; } =
            new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

        /// <summary>The host's user id. Guests only.</summary>
        public string? HostUserId { get; set; }

        /// <summary>Creates the library connection. Tests replace it with a fake.</summary>
        public Func<PeerConnectionOptions, IHaivenPeerTransport> CreateTransport { get; set; } =
            options => new UnityWebRtcPeerTransport(options);

        public override ulong ServerClientId => HostClientId;

        /// <summary>The Haiven user id behind an NGO client id, or null when there is no such peer.</summary>
        /// <summary>The data channels to this user opened; raised once per connection, on the main thread.</summary>
        public event Action<string>? PeerConnected;

        /// <summary>The connection to this user ended, for any reason; raised once, on the main thread.</summary>
        public event Action<string>? PeerDisconnected;

        /// <summary>The users whose data channels are open, for the host's roster check.</summary>
        public IReadOnlyList<string> ConnectedUsers()
        {
            var users = new List<string>();
            foreach (var peer in peersByClient.Values)
                if (peer.Connected && !peer.Lost) users.Add(peer.UserId);
            return users;
        }

        /// <summary>
        /// Closes the connection to a user the session no longer admits, and tells
        /// NGO the client left. The host calls it on <c>session.member.left</c>.
        /// </summary>
        public bool DropUser(string userId)
        {
            if (!peersByUser.TryGetValue(userId, out var peer) || peer.Lost) return false;
            Lose(peer, raiseEvent: true, sayGoodbye: true);
            return true;
        }

        public string? GetUserId(ulong clientId) =>
            peersByClient.TryGetValue(clientId, out var peer) ? peer.UserId : null;

        public override void Initialize(NetworkManager? networkManager = null)
        {
            // NGO batches unreliable messages up to this size. It rounds down to
            // a multiple of 8, so 1,197 becomes 1,192, which with the 3-byte
            // header stays under the 1,200-byte data channel limit.
            if (networkManager != null) networkManager.MaximumTransmissionUnitSize = PeerFraming.MaxUnreliablePayload;
        }

        public override bool StartServer() => Begin(asServer: true);

        public override bool StartClient()
        {
            if (!Begin(asServer: false)) return false;
            if (Signaling is IHaivenPeerSignalingLifecycle lifecycle && lifecycle.HostUserId != null)
                HostUserId = lifecycle.HostUserId;
            if (string.IsNullOrEmpty(HostUserId))
            {
                Debug.LogError("[haiven-p2p] HostUserId is not set");
                Shutdown();
                return false;
            }
            var peer = AddPeer(HostUserId!, HostClientId);
            _ = Offer(peer);
            return true;
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery)
        {
            if (!peersByClient.TryGetValue(clientId, out var peer) || !peer.Connected) return;

            switch (delivery)
            {
                case NetworkDelivery.Unreliable:
                    SendUnreliable(peer, PeerFraming.EncodeUnreliable(payload));
                    break;
                case NetworkDelivery.UnreliableSequenced:
                    SendUnreliable(peer, PeerFraming.EncodeSequenced(payload, peer.NextSequence++));
                    break;
                default:
                    foreach (var chunk in PeerFraming.EncodeReliable(payload))
                    {
                        var result = peer.Link.Send(chunk, reliable: true);
                        if (result == PeerSendResult.Sent) continue;
                        // A reliable stream with a piece missing cannot be read
                        // again, so the peer goes, as Unity Transport does on a
                        // full send queue.
                        Debug.LogWarning($"[haiven-p2p] reliable send to {peer.UserId} failed: {result}; disconnecting");
                        Lose(peer, raiseEvent: true);
                        return;
                    }
                    break;
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            if (events.Count == 0 && started && Signaling is IHaivenPeerSignalingLifecycle lifecycle) lifecycle.Poll();
            if (started && Time.realtimeSinceStartup >= nextRoundTrip)
            {
                nextRoundTrip = Time.realtimeSinceStartup + RoundTripInterval;
                foreach (var peer in peersByClient.Values)
                {
                    if (!peer.Connected) continue;
                    var path = peer.Link.Transport.PathType;
                    if (path != null && path != peer.LoggedPath)
                    {
                        peer.LoggedPath = path;
                        Debug.Log($"[haiven-p2p] connected to {peer.UserId} over a {path} path");
                    }
                    peer.Link.Transport.RefreshRoundTripTime();
                }
            }
            if (events.Count == 0)
            {
                clientId = 0;
                payload = default;
                receiveTime = Time.realtimeSinceStartup;
                return NetworkEvent.Nothing;
            }

            var next = events.Dequeue();
            clientId = next.ClientId;
            payload = next.Payload;
            receiveTime = next.ReceiveTime;
            return next.Type;
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (peersByClient.TryGetValue(clientId, out var peer)) Lose(peer, raiseEvent: false, sayGoodbye: true);
        }

        public override void DisconnectLocalClient()
        {
            if (peersByClient.TryGetValue(HostClientId, out var peer)) Lose(peer, raiseEvent: false, sayGoodbye: true);
        }

        /// <summary>The round-trip time to a peer, from the WebRTC stats read once a second.</summary>
        public bool TryGetRoundTripTime(ulong clientId, out long milliseconds)
        {
            milliseconds = peersByClient.TryGetValue(clientId, out var peer) ? peer.Link.Transport.RoundTripTimeMs : -1;
            return milliseconds >= 0;
        }

        /// <summary>The ICE type of the local candidate on the path to a peer, once measured.</summary>
        public string? GetPathType(ulong clientId) =>
            peersByClient.TryGetValue(clientId, out var peer) ? peer.Link.Transport.PathType : null;

        public override ulong GetCurrentRtt(ulong clientId) =>
            TryGetRoundTripTime(clientId, out var milliseconds) ? (ulong)milliseconds : 0;

        public override void Shutdown()
        {
            if (Signaling != null) Signaling.Received -= OnSignal;
            if (started && Signaling is IHaivenPeerSignalingLifecycle lifecycle)
            {
                lifecycle.HostUnreachable -= OnHostUnreachable;
                lifecycle.Stop();
            }
            foreach (var peer in new List<Peer>(peersByClient.Values)) Lose(peer, raiseEvent: false, sayGoodbye: true);
            events.Clear();
            nextClientId = 1;
            started = false;
        }

        private bool Begin(bool asServer)
        {
            if (started) return false;
            if (Signaling == null)
            {
                Debug.LogError("[haiven-p2p] Signaling is not set");
                return false;
            }
            if (Signaling is IHaivenPeerSignalingLifecycle lifecycle)
            {
                if (!lifecycle.Start(asServer)) return false;
                lifecycle.HostUnreachable += OnHostUnreachable;
            }
            isServer = asServer;
            started = true;
            Signaling.Received += OnSignal;
            return true;
        }

        // Before the data channels open, a guest without signaling cannot
        // connect at all, so NGO hears of it and stops waiting. Once they are
        // open the game goes on without signaling.
        private void OnHostUnreachable(string reason)
        {
            if (isServer || !peersByClient.TryGetValue(HostClientId, out var host) || host.Connected) return;
            Debug.LogWarning($"[haiven-p2p] {reason}");
            Lose(host, raiseEvent: true);
        }

        private void OnSignal(string fromUserId, PeerSignal signal)
        {
            if (!started) return;

            if (signal.Kind == PeerSignalKind.Offer)
            {
                if (!isServer) return;
                // A guest that offers again has started over; its old
                // connection is gone.
                if (peersByUser.TryGetValue(fromUserId, out var old)) Lose(old, raiseEvent: true);
                _ = Answer(AddPeer(fromUserId, nextClientId++), signal.Sdp!);
                return;
            }

            if (!peersByUser.TryGetValue(fromUserId, out var peer)) return;
            if (signal.Kind == PeerSignalKind.Answer)
            {
                if (!isServer) _ = ApplyAnswer(peer, signal.Sdp!);
            }
            else if (peer.RemoteApplied) peer.Link.Transport.AddRemoteCandidate(signal.Candidate!);
            else peer.PendingCandidates.Add(signal.Candidate!);
        }

        private Peer AddPeer(string userId, ulong clientId)
        {
            var peer = new Peer(userId, clientId, new HaivenPeerLink(CreateTransport(Connection)));
            peersByClient[clientId] = peer;
            peersByUser[userId] = peer;

            // libwebrtc starts gathering inside SetLocalDescription, so the first
            // candidates appear before the offer or answer is sent. The host
            // drops a candidate from a guest it has no offer from, so they wait.
            peer.Link.Transport.LocalCandidate += candidate =>
            {
                if (peer.LocalSent) Signaling?.Send(userId, PeerSignal.ForCandidate(candidate));
                else peer.OutgoingCandidates.Add(candidate);
            };
            peer.Link.Opened += () =>
            {
                if (peer.Lost) return;
                peer.Connected = true;
                PeerConnected?.Invoke(peer.UserId);
                Enqueue(NetworkEvent.Connect, clientId, default);
            };
            peer.Link.Message += (message, reliable) => OnMessage(peer, message, reliable);
            peer.Link.Closed += reason =>
            {
                if (peer.Lost) return;
                Debug.Log($"[haiven-p2p] connection to {userId} closed: {reason}");
                Lose(peer, raiseEvent: true);
            };
            return peer;
        }

        private async Task Offer(Peer peer)
        {
            try
            {
                var offer = await peer.Link.Transport.CreateOfferAsync();
                if (peer.Lost) return;
                Signaling?.Send(peer.UserId, PeerSignal.Offer(offer));
                LocalSent(peer);
            }
            catch (Exception error)
            {
                Fail(peer, "offer", error);
            }
        }

        private async Task Answer(Peer peer, string offerSdp)
        {
            try
            {
                var answer = await peer.Link.Transport.AcceptOfferAsync(offerSdp);
                if (peer.Lost) return;
                RemoteApplied(peer);
                Signaling?.Send(peer.UserId, PeerSignal.Answer(answer));
                LocalSent(peer);
            }
            catch (Exception error)
            {
                Fail(peer, "answer", error);
            }
        }

        private async Task ApplyAnswer(Peer peer, string answerSdp)
        {
            try
            {
                await peer.Link.Transport.AcceptAnswerAsync(answerSdp);
                if (!peer.Lost) RemoteApplied(peer);
            }
            catch (Exception error)
            {
                Fail(peer, "apply answer", error);
            }
        }

        // Candidates can arrive before the description they belong to is applied;
        // they wait until it is (SDK-P2P-8).
        private static void RemoteApplied(Peer peer)
        {
            peer.RemoteApplied = true;
            foreach (var candidate in peer.PendingCandidates) peer.Link.Transport.AddRemoteCandidate(candidate);
            peer.PendingCandidates.Clear();
        }

        private void LocalSent(Peer peer)
        {
            peer.LocalSent = true;
            foreach (var candidate in peer.OutgoingCandidates)
                Signaling?.Send(peer.UserId, PeerSignal.ForCandidate(candidate));
            peer.OutgoingCandidates.Clear();
        }

        private void Fail(Peer peer, string step, Exception error)
        {
            if (peer.Lost) return;
            Debug.LogWarning($"[haiven-p2p] {step} with {peer.UserId} failed: {error.Message}");
            Lose(peer, raiseEvent: true);
        }

        private void OnMessage(Peer peer, byte[] message, bool reliable)
        {
            if (peer.Lost) return;
            if (reliable)
            {
                List<byte[]> complete;
                try
                {
                    complete = peer.Assembler.Add(message);
                }
                catch (InvalidOperationException error)
                {
                    Fail(peer, "read reliable stream", error);
                    return;
                }
                foreach (var payload in complete) Enqueue(NetworkEvent.Data, peer.ClientId, new ArraySegment<byte>(payload));
                return;
            }

            if (message.Length < 1) return;
            if (message[0] == PeerFraming.Goodbye)
            {
                Debug.Log($"[haiven-p2p] {peer.UserId} closed the connection");
                Lose(peer, raiseEvent: true);
                return;
            }
            if (message[0] == PeerFraming.Unreliable)
            {
                Enqueue(NetworkEvent.Data, peer.ClientId, new ArraySegment<byte>(message, 1, message.Length - 1));
            }
            else if (message[0] == PeerFraming.UnreliableSequenced && message.Length >= PeerFraming.SequencedHeader)
            {
                var sequence = (ushort)((message[1] << 8) | message[2]);
                if (peer.HasSequence && !PeerFraming.IsNewer(sequence, peer.LastSequence)) return;
                peer.HasSequence = true;
                peer.LastSequence = sequence;
                Enqueue(NetworkEvent.Data, peer.ClientId,
                    new ArraySegment<byte>(message, PeerFraming.SequencedHeader, message.Length - PeerFraming.SequencedHeader));
            }
        }

        private static void SendUnreliable(Peer peer, byte[] message)
        {
            var result = peer.Link.Send(message, reliable: false);
            if (result == PeerSendResult.TooLarge)
                Debug.LogWarning($"[haiven-p2p] dropped an unreliable message of {message.Length} bytes; NGO's MTU is above the limit");
        }

        // A guest that loses the host always hears about it, so NGO stops
        // waiting; the host hears only about guests that had connected.
        private void Lose(Peer peer, bool raiseEvent, bool sayGoodbye = false)
        {
            if (peer.Lost) return;
            peer.Lost = true;
            peersByClient.Remove(peer.ClientId);
            if (peersByUser.TryGetValue(peer.UserId, out var current) && current == peer) peersByUser.Remove(peer.UserId);
            if (sayGoodbye && peer.Connected)
            {
                // Three copies, since the channel may drop one, and a short
                // wait so they leave before the connection and its allocation go.
                for (var i = 0; i < 3; i++) peer.Link.Send(new[] { PeerFraming.Goodbye }, reliable: false);
                _ = DisposeAfterGoodbye(peer.Link);
            }
            else
            {
                peer.Link.Dispose();
            }
            if (raiseEvent && (peer.Connected || !isServer)) Enqueue(NetworkEvent.Disconnect, peer.ClientId, default);
            if (peer.Connected) PeerDisconnected?.Invoke(peer.UserId);
        }

        private static async Task DisposeAfterGoodbye(HaivenPeerLink link)
        {
            await Task.Delay(200);
            link.Dispose();
        }

        private void Enqueue(NetworkEvent type, ulong clientId, ArraySegment<byte> payload) =>
            events.Enqueue(new PendingEvent(type, clientId, payload, Time.realtimeSinceStartup));

        private readonly struct PendingEvent
        {
            public PendingEvent(NetworkEvent type, ulong clientId, ArraySegment<byte> payload, float receiveTime)
            {
                Type = type;
                ClientId = clientId;
                Payload = payload;
                ReceiveTime = receiveTime;
            }

            public NetworkEvent Type { get; }

            public ulong ClientId { get; }

            public ArraySegment<byte> Payload { get; }

            public float ReceiveTime { get; }
        }

        private sealed class Peer
        {
            public Peer(string userId, ulong clientId, HaivenPeerLink link)
            {
                UserId = userId;
                ClientId = clientId;
                Link = link;
            }

            public string UserId { get; }

            public ulong ClientId { get; }

            public HaivenPeerLink Link { get; }

            public ReliableAssembler Assembler { get; } = new ReliableAssembler();

            public List<PeerIceCandidate> PendingCandidates { get; } = new List<PeerIceCandidate>();

            public List<PeerIceCandidate> OutgoingCandidates { get; } = new List<PeerIceCandidate>();

            public bool RemoteApplied { get; set; }

            public bool LocalSent { get; set; }

            public string? LoggedPath { get; set; }

            public bool Connected { get; set; }

            public bool Lost { get; set; }

            public ushort NextSequence { get; set; }

            public bool HasSequence { get; set; }

            public ushort LastSequence { get; set; }
        }
    }
}
