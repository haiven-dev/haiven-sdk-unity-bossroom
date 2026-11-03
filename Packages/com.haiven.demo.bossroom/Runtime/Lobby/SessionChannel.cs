#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Json;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>
    /// The game socket as the lobby uses it: correlated requests, and the
    /// session events handed to the main thread.
    ///
    /// <see cref="HaivenSessionCore"/> raises frames on the socket's thread.
    /// Replies complete their task there, so an <c>await</c> resumes on the
    /// caller's context; events wait in a queue until <see cref="Pump"/> runs
    /// them on the main thread, in the order they arrived.
    /// </summary>
    public sealed class SessionChannel : IDisposable
    {
        private const string AuthSuccess = "auth.success";

        private readonly HaivenSessionCore session;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerFrame>> waiting =
            new ConcurrentDictionary<string, TaskCompletionSource<ServerFrame>>();
        private readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();

        // The session core picks the request id inside Send, so a fast reply can
        // land before its waiter is registered. It is parked here and claimed.
        private readonly ConcurrentDictionary<string, ServerFrame> early = new ConcurrentDictionary<string, ServerFrame>();
        private volatile string? connectionId;

        public SessionChannel(HaivenSessionCore session, string? connectionId = null)
        {
            this.session = session;
            this.connectionId = connectionId;
            session.FrameReceived += OnFrame;
            session.ReplyReceived += OnReply;
            session.EventReceived += OnEvent;
            session.Closed += OnClosed;
        }

        /// <summary>The server's id for this socket, from <c>auth.success</c>; null until then.</summary>
        public string? ConnectionId => connectionId;

        public bool IsOpen => session.State == HaivenSessionState.Authenticated && connectionId != null;

        /// <summary>A server event, on the main thread: its type and payload.</summary>
        public event Action<string, IHaivenJsonNode>? Event;

        /// <summary>The socket closed, on the main thread.</summary>
        public event Action? Lost;

        /// <summary>Sends one operation and waits for its correlated reply.</summary>
        public async Task<ServerFrame> RequestAsync(string type, object? payload, TimeSpan timeout)
        {
            var sent = session.Send(type, payload);
            var reply = Wait(sent.RequestId);
            var finished = await Task.WhenAny(reply.Task, Task.Delay(timeout)).ConfigureAwait(true);
            waiting.TryRemove(sent.RequestId, out _);
            if (finished != reply.Task) throw new TimeoutException("haiven: no reply to " + type);
            return reply.Task.Result;
        }

        /// <summary>Sends one operation without waiting; a refusal comes back through <paramref name="onReply"/>.</summary>
        public void Send(string type, object? payload, Action<ServerFrame>? onReply = null)
        {
            var sent = session.Send(type, payload);
            // Claimed even when nobody reads it, so its reply is not parked forever.
            var reply = Wait(sent.RequestId);
            if (onReply == null) return;
            reply.Task.ContinueWith(done => inbox.Enqueue(() => onReply(done.Result)),
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private TaskCompletionSource<ServerFrame> Wait(string requestId)
        {
            var reply = new TaskCompletionSource<ServerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting[requestId] = reply;
            if (early.TryRemove(requestId, out var arrived) && waiting.TryRemove(requestId, out _))
                reply.TrySetResult(arrived);
            return reply;
        }

        /// <summary>Runs queued events on the calling (main) thread.</summary>
        public void Pump()
        {
            while (inbox.TryDequeue(out var action)) action();
        }

        public void Dispose()
        {
            session.FrameReceived -= OnFrame;
            session.ReplyReceived -= OnReply;
            session.EventReceived -= OnEvent;
            session.Closed -= OnClosed;
            foreach (var pending in waiting.Values) pending.TrySetCanceled();
            waiting.Clear();
        }

        private void OnFrame(ReceivedFrame received)
        {
            if (received.Frame.Type != AuthSuccess || received.Frame.Payload == null) return;
            var id = SessionsApi.NullableText(received.Frame.Payload, "connectionId");
            if (!string.IsNullOrEmpty(id)) connectionId = id;
        }

        private void OnReply(ReceivedFrame received)
        {
            var id = received.Frame.RequestId;
            if (string.IsNullOrEmpty(id)) return;
            if (waiting.TryRemove(id!, out var reply)) reply.TrySetResult(received.Frame);
            else early[id!] = received.Frame;
        }

        private void OnEvent(ReceivedFrame received)
        {
            var type = received.Frame.Type;
            var payload = received.Frame.Payload ?? HaivenJson.Parse("{}");
            inbox.Enqueue(() => Event?.Invoke(type, payload));
        }

        private void OnClosed(int code, string reason)
        {
            connectionId = null;
            inbox.Enqueue(() => Lost?.Invoke());
        }
    }
}
