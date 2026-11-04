#nullable enable
using System;

namespace Haiven.Demo.BossRoom.P2P
{
    public enum PeerSignalKind
    {
        Offer,
        Answer,
        Candidate,
    }

    /// <summary>One signaling message between a guest and the host.</summary>
    public sealed class PeerSignal
    {
        private PeerSignal(PeerSignalKind kind, string? sdp, PeerIceCandidate? candidate)
        {
            Kind = kind;
            Sdp = sdp;
            Candidate = candidate;
        }

        public PeerSignalKind Kind { get; }

        public string? Sdp { get; }

        public PeerIceCandidate? Candidate { get; }

        public static PeerSignal Offer(string sdp) => new PeerSignal(PeerSignalKind.Offer, sdp, null);

        public static PeerSignal Answer(string sdp) => new PeerSignal(PeerSignalKind.Answer, sdp, null);

        public static PeerSignal ForCandidate(PeerIceCandidate candidate) =>
            new PeerSignal(PeerSignalKind.Candidate, null, candidate);
    }

    /// <summary>
    /// Where offers, answers and candidates travel. In a Haiven session this is
    /// <c>p2p.signal</c> over the game socket, and the server sets the sender's
    /// user id, so a peer cannot claim to be someone else. This experiment keeps
    /// it behind an interface so the transport runs before the SDK controller
    /// exists.
    /// </summary>
    public interface IHaivenPeerSignaling
    {
        string LocalUserId { get; }

        void Send(string toUserId, PeerSignal signal);

        /// <summary>Raised on the Unity main thread with the sender's user id.</summary>
        event Action<string, PeerSignal>? Received;
    }

    /// <summary>
    /// Signaling that the transport starts and stops with NGO and pumps every
    /// frame, for a channel that reads on another thread.
    /// </summary>
    public interface IHaivenPeerSignalingLifecycle
    {
        /// <summary>The host's user id once it is known on a guest; null before that and on the host.</summary>
        string? HostUserId { get; }

        bool Start(bool asHost);

        /// <summary>Raised on the main thread when a guest cannot reach the host or loses it.</summary>
        event Action<string>? HostUnreachable;

        /// <summary>Raises <see cref="IHaivenPeerSignaling.Received"/> for whatever arrived since the last call.</summary>
        void Poll();

        void Stop();
    }
}
