// Vendored from sdks/dotnet/Haiven.Sdk/Session.cs by scripts/vendor-unity.py.
// Edit or regenerate the source, then run `make vendor-unity`.
#nullable enable
using System;
using System.Collections.Generic;
using Haiven.Sdk.Json;

namespace Haiven.Sdk
{
    public enum HaivenSessionState
    {
        Pending,
        Authenticated,
        Expired,
        Closed,
    }

    /// <summary>A parsed server frame together with the exact text received.</summary>
    public sealed class ReceivedFrame
    {
        public ReceivedFrame(string json, ServerFrame frame)
        {
            Json = json;
            Frame = frame;
        }

        public string Json { get; }

        public ServerFrame Frame { get; }
    }

    /// <summary>
    /// Engine-neutral Haiven session state over an injected socket.
    ///
    /// The host owns connection establishment. Call <see cref="Opened"/> once
    /// the socket is writable; the session then performs the required first-frame
    /// authentication, queues application frames until it succeeds, and
    /// re-authenticates on the same socket after <c>auth.expired</c>.
    /// </summary>
    public sealed class HaivenSessionCore : IDisposable
    {
        private const string Auth = "auth";
        private const string AuthSuccess = "auth.success";
        private const string AuthExpired = "auth.expired";
        private const string AuthFailure = "auth.failure";
        private const string AuthExpiredDropped = "auth.expired.dropped";

        private readonly IHaivenSocket socket;
        private readonly Func<string> tokenSource;
        private readonly Func<string> nextRequestId;
        private readonly IHaivenJsonCodec codec;
        private readonly Queue<ClientFrame> pending = new Queue<ClientFrame>();

        // Written and not yet answered, oldest first. Only what the server names
        // as refused or dropped is resent from here: a request it accepted before
        // a lapse has its reply parked and replayed, so a blind resend duplicates
        // it (spec/client.md, SDK-RECOVERY-1..5).
        private readonly List<ClientFrame> inFlight = new List<ClientFrame>();

        // Refused or dropped requests, written again after the next auth.success.
        private readonly List<ClientFrame> awaitingResend = new List<ClientFrame>();
        private bool opened;
        private bool disposed;

        public HaivenSessionCore(
            IHaivenSocket socket,
            Func<string> tokenSource,
            Func<string>? nextRequestId = null,
            IHaivenJsonCodec? codec = null)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            this.tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
            this.nextRequestId = nextRequestId ?? DefaultRequestId;
            this.codec = codec ?? HaivenJson.Default;
            socket.Received += Receive;
            socket.Closed += SocketClosed;
        }

        public HaivenSessionState State { get; private set; } = HaivenSessionState.Pending;

        /// <summary>The JSON codec this session parses and writes frames with.</summary>
        public IHaivenJsonCodec Codec => codec;

        public event Action<ReceivedFrame>? FrameReceived;

        public event Action<ReceivedFrame>? ReplyReceived;

        public event Action<ReceivedFrame>? EventReceived;

        public event Action? Authenticated;

        public event Action? Expired;

        /// <summary>
        /// The server refused a re-auth credential (<c>auth.failure</c> on a live
        /// socket), with its code. Not an ordinary lapse: the session stops
        /// re-authenticating, and the caller must sign in again.
        /// </summary>
        public event Action<string?>? AuthenticationFailed;

        public event Action<int, string>? Closed;

        /// <summary>Starts the required first-frame authentication exactly once.</summary>
        public void Opened()
        {
            ThrowIfDisposed();
            if (State == HaivenSessionState.Closed)
            {
                throw new InvalidOperationException("haiven: cannot open a closed session");
            }

            if (opened) return;
            opened = true;
            Authenticate();
        }

        /// <summary>Sends a frame, or queues it until authentication succeeds.</summary>
        public ClientFrame Send(string type, object? payload = null)
        {
            ThrowIfDisposed();
            if (State == HaivenSessionState.Closed)
            {
                throw new InvalidOperationException("haiven: send on a closed session");
            }

            var frame = new ClientFrame
            {
                Type = type,
                RequestId = nextRequestId(),
                Payload = payload,
            };

            if (State == HaivenSessionState.Authenticated)
            {
                Write(frame);
            }
            else
            {
                pending.Enqueue(frame);
            }

            return frame;
        }

        public void Close()
        {
            ThrowIfDisposed();
            if (State != HaivenSessionState.Closed) socket.Close();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            socket.Received -= Receive;
            socket.Closed -= SocketClosed;
        }

        private void Authenticate()
        {
            Write(new ClientFrame
            {
                Type = Auth,
                RequestId = nextRequestId(),
                Payload = tokenSource(),
            });
        }

        private void Write(ClientFrame frame)
        {
            if (frame.Type != Auth) inFlight.Add(frame);
            socket.Send(codec.Write(writer => WriteFrame(writer, frame)));
        }

        // The envelope by hand, so no reflection reaches it: camelCase members, and
        // a null payload absent rather than null, as the reflective writer it
        // replaces spelled it.
        private void WriteFrame(IHaivenJsonWriter writer, ClientFrame frame)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("type");
            writer.WriteString(frame.Type);
            writer.WritePropertyName("requestId");
            writer.WriteString(frame.RequestId);
            if (frame.Payload != null)
            {
                writer.WritePropertyName("payload");
                HaivenJsonWrite.Value(writer, frame.Payload, codec);
            }

            writer.WriteEndObject();
        }

        private void Flush()
        {
            var resend = awaitingResend.ToArray();
            awaitingResend.Clear();
            foreach (var frame in resend) Write(frame);
            while (pending.Count > 0) Write(pending.Dequeue());
        }

        /// <summary>Removes and returns the unanswered request with this id, if any.</summary>
        private ClientFrame? TakeInFlight(string requestId)
        {
            var index = inFlight.FindIndex(frame => frame.RequestId == requestId);
            if (index < 0) return null;
            var frame = inFlight[index];
            inFlight.RemoveAt(index);
            return frame;
        }

        /// <summary>Sends recovered requests now if the session is usable, else after the next auth.success.</summary>
        private void Requeue(IEnumerable<ClientFrame> frames)
        {
            foreach (var frame in frames)
            {
                if (State == HaivenSessionState.Authenticated) Write(frame);
                else awaitingResend.Add(frame);
            }
        }

        private void HandleExpired(ServerFrame frame)
        {
            // A correlated notice names the one request the lapse refused.
            if (!string.IsNullOrEmpty(frame.RequestId) && TakeInFlight(frame.RequestId!) is { } refused)
            {
                awaitingResend.Add(refused);
            }

            // One re-auth per episode, on the same socket, however many notices it produced.
            if (State == HaivenSessionState.Expired) return;
            State = HaivenSessionState.Expired;
            Expired?.Invoke();
            Authenticate();
        }

        /// <summary>
        /// The overflow half of an expiry. It arrives after the auth.success that ended
        /// the episode, so what it names goes out at once rather than waiting.
        /// </summary>
        private void HandleDropped(ServerFrame frame)
        {
            var named = new List<ClientFrame>();
            var truncated = false;
            if (frame.Payload is { Kind: HaivenJsonKind.Object } report)
            {
                if (report.TryGetProperty("requestIds", out var ids) && ids.Kind == HaivenJsonKind.Array)
                {
                    foreach (var id in ids.EnumerateArray())
                    {
                        if (id.Kind == HaivenJsonKind.String && TakeInFlight(id.GetString()) is { } dropped)
                        {
                            named.Add(dropped);
                        }
                    }
                }

                truncated = report.TryGetProperty("truncated", out var flag) && flag.Kind == HaivenJsonKind.True;
            }

            // A truncated list is a sample: every request still unanswered is suspect.
            // Read before resending, because a resend is in flight again.
            var suspect = truncated ? inFlight.ToArray() : Array.Empty<ClientFrame>();
            if (truncated) inFlight.Clear();
            Requeue(named);
            Requeue(suspect);
        }

        private void Receive(string json)
        {
            var received = new ReceivedFrame(json, ServerFrame.Parse(json, codec));
            FrameReceived?.Invoke(received);

            if (received.Frame.Type == AuthSuccess)
            {
                // A replayed handshake reply on an authenticated session is a no-op.
                if (State == HaivenSessionState.Authenticated) return;
                State = HaivenSessionState.Authenticated;
                Authenticated?.Invoke();
                Flush();
                return;
            }

            if (received.Frame.Type == AuthExpired)
            {
                HandleExpired(received.Frame);
                return;
            }

            if (received.Frame.Type == AuthFailure)
            {
                // Stay expired and stop: a further auth.expired starts no re-auth.
                State = HaivenSessionState.Expired;
                AuthenticationFailed?.Invoke(received.Frame.Error);
                return;
            }

            if (received.Frame.Type == AuthExpiredDropped)
            {
                HandleDropped(received.Frame);
                return;
            }

            if (received.Frame.IsReply)
            {
                if (!string.IsNullOrEmpty(received.Frame.RequestId)) TakeInFlight(received.Frame.RequestId!);
                ReplyReceived?.Invoke(received);
            }
            else
            {
                EventReceived?.Invoke(received);
            }
        }

        private void SocketClosed(int code, string reason)
        {
            if (State == HaivenSessionState.Closed) return;
            State = HaivenSessionState.Closed;
            pending.Clear();
            inFlight.Clear();
            awaitingResend.Clear();
            Closed?.Invoke(code, reason);
        }

        private static string DefaultRequestId() => Guid.NewGuid().ToString("D");

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HaivenSessionCore));
        }
    }
}
