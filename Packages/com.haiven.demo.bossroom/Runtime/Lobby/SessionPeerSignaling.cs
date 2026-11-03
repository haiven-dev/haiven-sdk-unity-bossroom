#nullable enable
using System;
using System.Collections.Generic;
using Haiven.Demo.BossRoom.P2P;
using Haiven.Sdk.Json;
using UnityEngine;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>
    /// WebRTC signaling over the Haiven game socket, as ADR 0036 points 14 and
    /// 15 describe: <c>p2p.signal</c> to one member, relayed by haiven-api with
    /// the sender's user id set by the server, acknowledged with
    /// <c>p2p.signal.ack</c>.
    ///
    /// The server only accepts a message for delivery, so reliability is here.
    /// An unacknowledged message is sent again after 1, 2 and 4 seconds. The
    /// receiver acknowledges every copy, drops duplicates by attempt and
    /// sequence, hands messages on in sequence order, and ignores an older
    /// attempt. One attempt is one connection: the guest starts it with its
    /// offer, and the host answers within it.
    /// </summary>
    public sealed class SessionPeerSignaling : IHaivenPeerSignaling, IHaivenPeerSignalingLifecycle
    {
        private static readonly float[] RetryDelays = { 1f, 2f, 4f };

        private SessionChannel channel;
        private readonly string sessionId;
        private readonly Func<float> clock;
        private readonly Dictionary<string, Outgoing> outgoing = new Dictionary<string, Outgoing>();
        private readonly Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
        private readonly List<Unacknowledged> unacknowledged = new List<Unacknowledged>();
        private bool running;

        public SessionPeerSignaling(
            SessionChannel channel, string sessionId, string localUserId, string? hostUserId, Func<float>? clock = null)
        {
            this.channel = channel;
            this.sessionId = sessionId;
            LocalUserId = localUserId;
            HostUserId = hostUserId;
            this.clock = clock ?? (() => Time.realtimeSinceStartup);
        }

        public string LocalUserId { get; }

        public string? HostUserId { get; }

        public event Action<string, PeerSignal>? Received;

        public event Action<string>? HostUnreachable;

        /// <summary>Messages sent and not yet acknowledged, for tests and logs.</summary>
        public int PendingAcknowledgements => unacknowledged.Count;

        public bool Start(bool asHost)
        {
            if (running) return false;
            running = true;
            channel.Event += OnEvent;
            channel.Lost += OnLost;
            return true;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            channel.Event -= OnEvent;
            channel.Lost -= OnLost;
            outgoing.Clear();
            incoming.Clear();
            unacknowledged.Clear();
        }

        /// <summary>
        /// Moves to the socket the session resumed on (SDK-P2P-5). Messages not yet
        /// acknowledged go out again on it, since the old socket may have lost them.
        /// </summary>
        public void Rebind(SessionChannel resumed)
        {
            if (resumed == channel) return;
            if (running)
            {
                channel.Event -= OnEvent;
                channel.Lost -= OnLost;
                resumed.Event += OnEvent;
                resumed.Lost += OnLost;
            }
            channel = resumed;
            var now = clock();
            foreach (var message in unacknowledged)
            {
                message.NextTry = now;
                message.Retries = Math.Min(message.Retries, RetryDelays.Length - 1);
            }
        }

        public void Poll()
        {
            channel.Pump();
            var now = clock();
            for (var i = unacknowledged.Count - 1; i >= 0; i--)
            {
                var message = unacknowledged[i];
                if (now < message.NextTry) continue;
                if (message.Retries >= RetryDelays.Length)
                {
                    unacknowledged.RemoveAt(i);
                    Debug.LogWarning($"[haiven-p2p] signal {message.Seq} to {message.ToUserId} was never acknowledged");
                    continue;
                }
                message.NextTry = now + RetryDelays[message.Retries];
                message.Retries++;
                Write(message);
            }
        }

        public void Send(string toUserId, PeerSignal signal)
        {
            if (!running) return;

            // An offer starts a new attempt: one per connection, including a
            // guest that offers again. The host's answer and candidates use the
            // attempt of the offer it answers.
            if (!outgoing.TryGetValue(toUserId, out var attempt) || signal.Kind == PeerSignalKind.Offer)
            {
                var attemptId = signal.Kind == PeerSignalKind.Offer || !incoming.TryGetValue(toUserId, out var theirs)
                    ? Guid.NewGuid().ToString("D")
                    : theirs.AttemptId;
                attempt = new Outgoing(attemptId);
                outgoing[toUserId] = attempt;
                unacknowledged.RemoveAll(message => message.ToUserId == toUserId);
            }

            var sent = new Unacknowledged(toUserId, attempt.AttemptId, ++attempt.LastSeq, Encode(signal))
            {
                NextTry = clock() + RetryDelays[0],
                Retries = 1,
            };
            unacknowledged.Add(sent);
            Write(sent);
        }

        private void Write(Unacknowledged message)
        {
            channel.Send("p2p.signal", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["toUserId"] = message.ToUserId,
                ["attemptId"] = message.AttemptId,
                ["seq"] = message.Seq,
                ["data"] = message.Data,
            }, reply =>
            {
                // peer_not_in_session: the member left, so there is nobody to retry for.
                if (reply.Success == false && reply.Error == "peer_not_in_session")
                    unacknowledged.Remove(message);
            });
        }

        private void OnEvent(string type, IHaivenJsonNode payload)
        {
            if (!running || SessionsApi.Text(payload, "sessionId") != sessionId) return;
            if (type == "p2p.signal.ack") OnAck(payload);
            else if (type == "p2p.signal.received") OnSignal(payload);
        }

        private void OnAck(IHaivenJsonNode payload)
        {
            var from = SessionsApi.Text(payload, "fromUserId");
            var attemptId = SessionsApi.Text(payload, "attemptId");
            var seq = SessionsApi.Number(payload, "seq");
            unacknowledged.RemoveAll(message =>
                message.ToUserId == from && message.AttemptId == attemptId && message.Seq == seq);
        }

        private void OnSignal(IHaivenJsonNode payload)
        {
            var from = SessionsApi.Text(payload, "fromUserId");
            var attemptId = SessionsApi.Text(payload, "attemptId");
            var seq = SessionsApi.Number(payload, "seq");
            var data = SessionsApi.Text(payload, "data");
            if (from.Length == 0 || attemptId.Length == 0 || seq < 1) return;

            // Every copy is acknowledged, since the first acknowledgement may be the one lost.
            channel.Send("p2p.signal.ack", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["toUserId"] = from,
                ["attemptId"] = attemptId,
                ["seq"] = seq,
            });

            if (!incoming.TryGetValue(from, out var attempt) || attempt.AttemptId != attemptId)
            {
                // A new attempt is either the peer's offer, or the host answering
                // the attempt this side started. Anything else is from an older one.
                PeerSignal? first;
                try { first = Decode(data); }
                catch (HaivenJsonException) { return; }
                if (first == null) return;
                var ours = outgoing.TryGetValue(from, out var mine) && mine.AttemptId == attemptId;
                if (first.Kind != PeerSignalKind.Offer && !ours) return;
                if (first.Kind == PeerSignalKind.Offer && !ours)
                {
                    // The guest started over: what this side sent in the old attempt is void.
                    outgoing.Remove(from);
                    unacknowledged.RemoveAll(message => message.ToUserId == from);
                }
                attempt = new Incoming(attemptId);
                incoming[from] = attempt;
            }

            if (seq < attempt.NextSeq || attempt.Held.ContainsKey(seq)) return;
            attempt.Held[seq] = data;
            while (attempt.Held.TryGetValue(attempt.NextSeq, out var next))
            {
                attempt.Held.Remove(attempt.NextSeq);
                attempt.NextSeq++;
                PeerSignal? signal;
                try { signal = Decode(next); }
                catch (HaivenJsonException) { continue; }
                if (signal != null) Received?.Invoke(from, signal);
            }
        }

        private void OnLost() => HostUnreachable?.Invoke("the Haiven game socket closed");

        internal static string Encode(PeerSignal signal)
        {
            var data = new Dictionary<string, object?>();
            switch (signal.Kind)
            {
                case PeerSignalKind.Offer:
                    data["kind"] = "offer";
                    data["sdp"] = signal.Sdp;
                    break;
                case PeerSignalKind.Answer:
                    data["kind"] = "answer";
                    data["sdp"] = signal.Sdp;
                    break;
                default:
                    data["kind"] = "candidate";
                    data["candidate"] = signal.Candidate!.Candidate;
                    data["sdpMid"] = signal.Candidate.SdpMid;
                    data["sdpMLineIndex"] = signal.Candidate.SdpMLineIndex;
                    break;
            }
            return HaivenJson.Serialize(data);
        }

        internal static PeerSignal? Decode(string data)
        {
            var node = HaivenJson.Parse(data);
            switch (SessionsApi.Text(node, "kind"))
            {
                case "offer": return PeerSignal.Offer(SessionsApi.Text(node, "sdp"));
                case "answer": return PeerSignal.Answer(SessionsApi.Text(node, "sdp"));
                case "candidate":
                    int? index = node.TryGetProperty("sdpMLineIndex", out var line) && line.Kind == HaivenJsonKind.Number
                        ? SessionsApi.Number(node, "sdpMLineIndex")
                        : (int?)null;
                    return PeerSignal.ForCandidate(new PeerIceCandidate(
                        SessionsApi.Text(node, "candidate"), SessionsApi.NullableText(node, "sdpMid"), index));
                default: return null;
            }
        }

        private sealed class Outgoing
        {
            public Outgoing(string attemptId) => AttemptId = attemptId;

            public string AttemptId { get; }

            public int LastSeq { get; set; }
        }

        private sealed class Incoming
        {
            public Incoming(string attemptId) => AttemptId = attemptId;

            public string AttemptId { get; }

            public int NextSeq { get; set; } = 1;

            public Dictionary<int, string> Held { get; } = new Dictionary<int, string>();
        }

        private sealed class Unacknowledged
        {
            public Unacknowledged(string toUserId, string attemptId, int seq, string data)
            {
                ToUserId = toUserId;
                AttemptId = attemptId;
                Seq = seq;
                Data = data;
            }

            public string ToUserId { get; }

            public string AttemptId { get; }

            public int Seq { get; }

            public string Data { get; }

            public float NextTry { get; set; }

            public int Retries { get; set; }
        }
    }
}
