using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Haiven.Sdk
{
    /// <summary>
    /// Hosts analytics work in Unity. A WebGL player has no managed thread pool or
    /// task timers, so that target starts work and completes delays from coroutines.
    /// </summary>
    public sealed class UnityAnalyticsScheduler : MonoBehaviour, IAnalyticsScheduler
    {
        private readonly object _lifetimeGate = new object();
        private UnityMainThreadContext _mainThread;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _destroyed;
#if UNITY_INCLUDE_TESTS
        internal Action BeforeLifetimeLinkForTesting { get; set; }
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
        private readonly List<Func<Task>> _pendingOperations = new List<Func<Task>>();
        private readonly HashSet<PendingDelay> _pendingDelays = new HashSet<PendingDelay>();
        private readonly List<Action> _pendingPosts = new List<Action>();
#endif

        private void Awake()
        {
            _mainThread = new UnityMainThreadContext(SynchronizationContext.Current);
        }

        private UnityMainThreadContext MainThread => _mainThread ?? throw new InvalidOperationException(
            "UnityAnalyticsScheduler has not initialized on Unity's main thread. " +
            "Create it with GameObject.AddComponent<UnityAnalyticsScheduler>() before constructing AnalyticsBatcher.");

        private void OnDestroy()
        {
            lock (_lifetimeGate)
            {
                if (_destroyed) return;
                _destroyed = true;
            }
            _lifetime.Cancel();
#if UNITY_WEBGL && !UNITY_EDITOR
            StopAllCoroutines();
            foreach (var pending in _pendingDelays)
            {
                pending.Complete();
                pending.Dispose();
            }
            _pendingDelays.Clear();

            // Unity stops this component's coroutines during destruction. Settle
            // every operation accepted before teardown so AnalyticsBatcher flushes
            // cannot remain pending, including before Run reaches its first frame.
            var operations = _pendingOperations.ToArray();
            _pendingOperations.Clear();
            foreach (var operation in operations) StartOperation(operation);

            var posts = _pendingPosts.ToArray();
            _pendingPosts.Clear();
            foreach (var callback in posts)
            {
                try { callback(); }
                catch (Exception error) { Debug.LogException(error); }
            }
#endif
            _mainThread?.Dispose();
            _lifetime.Dispose();
        }

        void IAnalyticsScheduler.Start(Func<Task> operation)
        {
            if (operation is null) throw new ArgumentNullException(nameof(operation));
            ThrowIfDestroyed();
#if UNITY_WEBGL && !UNITY_EDITOR
            MainThread.RequireCurrentThread();
            _pendingOperations.Add(operation);
            StartCoroutine(Run(operation));
#else
            _ = RunInBackground(operation);
#endif
        }

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            // A Start operation can be queued before destruction and begin
            // afterwards. Return cancellation so its async owner can unwind;
            // throwing synchronously here would strand that owner's completion.
#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsDestroyed()) return Task.CompletedTask;
            MainThread.RequireCurrentThread();
            var pending = new PendingDelay(cancellationToken);
            _pendingDelays.Add(pending);
            StartCoroutine(DelayCoroutine(duration, pending));
            return pending.Task;
#else
#if UNITY_INCLUDE_TESTS
            BeforeLifetimeLinkForTesting?.Invoke();
#endif
            CancellationTokenSource linked;
            lock (_lifetimeGate)
            {
                if (_destroyed) return Task.FromCanceled(new CancellationToken(true));
                // Create the linked source while holding the same gate OnDestroy
                // uses to publish destruction. Once linked, cancellation can
                // propagate without reading _lifetime after it is disposed.
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            }
            return DelayInBackground(duration, linked);
#endif
        }

        public void Post(Action callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
#if UNITY_WEBGL && !UNITY_EDITOR
            MainThread.RequireCurrentThread();
            if (IsDestroyed())
            {
                callback();
                return;
            }
            _pendingPosts.Add(callback);
            StartCoroutine(PostCoroutine(callback));
#else
            if (IsDestroyed()) return;
            MainThread.Post(callback);
#endif
        }

        private void ThrowIfDestroyed()
        {
            if (IsDestroyed()) throw new ObjectDisposedException(nameof(UnityAnalyticsScheduler));
        }

        private bool IsDestroyed()
        {
            lock (_lifetimeGate) return _destroyed;
        }

#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private async Task RunInBackground(Func<Task> operation)
        {
            try
            {
                await Task.Run(operation).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Post(() => Debug.LogException(error));
            }
        }

        private static async Task DelayInBackground(TimeSpan duration, CancellationTokenSource linked)
        {
            using (linked)
                await Task.Delay(duration, linked.Token).ConfigureAwait(false);
        }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        private IEnumerator Run(Func<Task> operation)
        {
            // Starting on the next frame keeps TryEnqueue synchronous and non-blocking.
            yield return null;
            if (!_pendingOperations.Remove(operation)) yield break;
            StartOperation(operation);
        }

        private void StartOperation(Func<Task> operation)
        {
            Task task;
            try
            {
                task = operation();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                return;
            }

            if (!IsDestroyed()) StartCoroutine(Observe(task));
            else if (task.IsFaulted && task.Exception is not null)
                Debug.LogException(task.Exception.GetBaseException());
        }

        private static IEnumerator Observe(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted && task.Exception is not null)
                Debug.LogException(task.Exception.GetBaseException());
        }

        private IEnumerator DelayCoroutine(TimeSpan duration, PendingDelay pending)
        {
            var seconds = Math.Max(0, Math.Min(duration.TotalSeconds, float.MaxValue));
            var deadline = Time.realtimeSinceStartup + (float)seconds;
            while (!pending.IsCanceled && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (pending.IsCanceled) pending.Cancel();
            else pending.Complete();
            _pendingDelays.Remove(pending);
            pending.Dispose();
        }

        private IEnumerator PostCoroutine(Action callback)
        {
            yield return null;
            if (!_pendingPosts.Remove(callback)) yield break;
            callback();
        }

        private sealed class PendingDelay : IDisposable
        {
            private readonly TaskCompletionSource<bool> _completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            internal PendingDelay(CancellationToken cancellationToken)
            {
                if (cancellationToken.CanBeCanceled)
                    _registration = cancellationToken.Register(Cancel);
            }

            internal Task Task => _completion.Task;
            internal bool IsCanceled => _completion.Task.IsCanceled;
            internal void Complete() => _completion.TrySetResult(true);
            internal void Cancel() => _completion.TrySetCanceled();
            public void Dispose() => _registration.Dispose();
        }
#endif
    }

    /// <summary>Captures Unity's synchronization context once and owns callback dispatch.</summary>
    internal sealed class UnityMainThreadContext : IDisposable
    {
        private readonly SynchronizationContext _context;
        private readonly int _threadId;
        private int _disposed;

        internal UnityMainThreadContext(SynchronizationContext context)
        {
            _context = context ?? throw new InvalidOperationException(
                "Unity did not install a SynchronizationContext before UnityAnalyticsScheduler.Awake. " +
                "The scheduler must be created on Unity's main thread.");
            _threadId = Environment.CurrentManagedThreadId;
        }

        internal void RequireCurrentThread()
        {
            if (Environment.CurrentManagedThreadId != _threadId)
            {
                throw new InvalidOperationException(
                    "UnityAnalyticsScheduler coroutine operations must be called on Unity's main thread.");
            }
        }

        internal void Post(Action callback)
        {
            _context.Post(state => ((PendingPost)state).Invoke(), new PendingPost(this, callback));
        }

        internal void RunOrPost(Action callback)
        {
            if (Environment.CurrentManagedThreadId == _threadId) callback();
            else Post(callback);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        private sealed class PendingPost
        {
            private readonly UnityMainThreadContext _owner;
            private readonly Action _callback;

            internal PendingPost(UnityMainThreadContext owner, Action callback)
            {
                _owner = owner;
                _callback = callback;
            }

            internal void Invoke()
            {
                if (Volatile.Read(ref _owner._disposed) == 0) _callback();
            }
        }
    }
}
