#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.WebRtc;
using Unity.Netcode;
using UnityEngine;

namespace Haiven.Sdk.Samples.Netcode
{
    /// <summary>
    /// An NGO <see cref="NetworkTransport"/> over WebRTC data channels: one peer
    /// connection per host-guest pair, none between guests (ADR 0036 point 1).
    ///
    /// The guest offers and the host answers. Signaling goes through
    /// <see cref="Signaling"/>, so NGO client ids map to the user ids that
    /// signaling names; <see cref="GetUserId"/> returns them. Set
    /// <see cref="Signaling"/>, <see cref="Connection"/> and, on a guest,
    /// <see cref="HostUserId"/> before starting NGO; <see cref="HaivenNetcodeSession"/>
    /// does that from the P2P controller.
    ///
    /// A package sample, so the SDK itself needs no NGO: the game imports it
    /// and owns the copy.
    /// </summary>
    public sealed class HaivenNetworkTransport : NetworkTransport, IHaivenPeerRoster
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
        private HaivenPeerConnections? connections;

        public IHaivenPeerSignaling? Signaling { get; set; }

        /// <summary>ICE servers and policy for every connection this transport creates.</summary>
        private PeerConnectionOptions connection = new PeerConnectionOptions(Array.Empty<PeerIceServer>(), PeerIcePolicy.All);

        public PeerConnectionOptions Connection
        {
            get => connection;
            set
            {
                connection = value;
                if (connections != null) connections.Connection = value;
            }
        }

        /// <summary>The host's user id. Guests only.</summary>
        public string? HostUserId { get; set; }

        /// <summary>Creates the library connection. Tests replace it with a fake.</summary>
        public Func<PeerConnectionOptions, IHaivenPeerTransport> CreateTransport { get; set; } =
            options => new UnityWebRtcPeerTransport(options);

        public override ulong ServerClientId => HostClientId;

        /// <summary>The data channels to this user opened; raised once per connection, on the main thread.</summary>
        public event Action<string>? PeerConnected;

        /// <summary>The connection to this user ended, for any reason; raised once, on the main thread.</summary>
        public event Action<string>? PeerDisconnected;

        /// <summary>The users whose data channels are open, for the host's roster check.</summary>
        public IEnumerable<string> ConnectedUsers()
        {
            return connections?.ConnectedUsers() ?? Array.Empty<string>();
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

        /// <summary>The Haiven user id behind an NGO client id, or null when there is no such peer.</summary>
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
                    if (payload.Count > PeerFraming.MaxReliablePayload)
                    {
                        // The receiver refuses it, and the send queue could not hold it anyway.
                        Debug.LogWarning($"[haiven-p2p] reliable message of {payload.Count} bytes to {peer.UserId} is above {PeerFraming.MaxReliablePayload}; disconnecting");
                        Lose(peer, raiseEvent: true);
                        return;
                    }
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
            if (events.Count == 0 && started) connections?.Tick();
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
            started = false;
            foreach (var peer in new List<Peer>(peersByClient.Values)) Lose(peer, raiseEvent: false, sayGoodbye: true);
            connections?.Dispose();
            connections = null;
            events.Clear();
            nextClientId = 1;
        }

        private bool Begin(bool asServer)
        {
            if (started) return false;
            if (Signaling == null)
            {
                Debug.LogError("[haiven-p2p] Signaling is not set");
                return false;
            }
            isServer = asServer;
            connections = new HaivenPeerConnections(Signaling, CreateTransport, () => Time.realtimeSinceStartupAsDouble)
            {
                Connection = Connection,
            };
            connections.Started += AddPeer;
            connections.Connected += userId =>
            {
                if (!peersByUser.TryGetValue(userId, out var peer)) return;
                peer.Connected = true;
                PeerConnected?.Invoke(userId);
                Enqueue(NetworkEvent.Connect, peer.ClientId, default);
            };
            connections.Disconnected += (userId, reason) =>
            {
                Debug.Log($"[haiven-p2p] connection to {userId} closed: {reason}");
                if (peersByUser.TryGetValue(userId, out var peer)) Lose(peer, raiseEvent: true);
                else if (!isServer) Enqueue(NetworkEvent.Disconnect, HostClientId, default);
            };
            started = true;
            if (connections.Start(asServer, HostUserId)) return true;
            Shutdown();
            return false;
        }

        private void AddPeer(string userId, HaivenPeerLink link)
        {
            var clientId = isServer ? nextClientId++ : HostClientId;
            var peer = new Peer(userId, clientId, link);
            peersByClient[clientId] = peer;
            peersByUser[userId] = peer;
            link.Message += (message, reliable) => OnMessage(peer, message, reliable);
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
                    Debug.LogWarning($"[haiven-p2p] invalid reliable stream: {error.Message}");
                    Lose(peer, raiseEvent: true);
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
            connections?.Detach(peer.UserId);
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

            public string? LoggedPath { get; set; }

            public bool Connected { get; set; }

            public bool Lost { get; set; }

            public ushort NextSequence { get; set; }

            public bool HasSequence { get; set; }

            public ushort LastSequence { get; set; }
        }
    }
}
