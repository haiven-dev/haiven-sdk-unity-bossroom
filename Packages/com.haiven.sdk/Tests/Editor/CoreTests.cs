using System;
using System.Threading;
using Haiven.Sdk.Json;
using NUnit.Framework;

namespace Haiven.Sdk.Tests
{
    /// <summary>
    /// The core as a game receives it: compiled by Unity from the package's own
    /// source, with no hand-published DLL. Runs wherever the package is listed in
    /// the project manifest's <c>testables</c>.
    /// </summary>
    public class ProtocolTests
    {
        [Test] public void RefusedReplyPreservesPresence()
        {
            var frame = ServerFrame.Parse("{\"type\":\"ping\",\"success\":false,\"requestId\":\"\"}");
            Assert.That(frame.IsReply, Is.True);
            Assert.That(frame.Success, Is.False);
            Assert.That(frame.RequestId, Is.EqualTo(""));
        }

        [Test] public void EventHasNoSuccessOrRequestId()
        {
            var frame = ServerFrame.Parse("{\"type\":\"chat.message\",\"payload\":{\"id\":\"m1\"}}");
            Assert.That(frame.IsReply, Is.False);
            Assert.That(frame.Success, Is.Null);
            Assert.That(frame.RequestId, Is.Null);
        }

        [Test] public void HandoffDispatcherRunsOnUnityThread()
        {
            var unityThread = Environment.CurrentManagedThreadId;
            var callbackThread = -1;
            new UnityHandoffDispatcher()
                .RunAsync(() => callbackThread = Environment.CurrentManagedThreadId, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.That(callbackThread, Is.EqualTo(unityThread));
        }
    }

    public class PackagingTests
    {
        [Test] public void CoreIsCompiledFromPackageSource()
        {
            // The vendored Runtime/Core asmdef, not a Haiven.Sdk.Core.dll plugin.
            var core = typeof(ServerFrame).Assembly;
            Assert.That(core.GetName().Name, Is.EqualTo("Haiven.Sdk.Core"));
            Assert.That(core.Location, Does.Contain("ScriptAssemblies"));
        }

        [Test] public void BuiltInJsonIsTheDependencyFreeDefault()
        {
            Assert.That(HaivenJson.Default, Is.TypeOf<BuiltInJsonCodec>());
        }
    }

    public class PresentationTests
    {
        [Test] public void DrawersStartCollapsedAndPublishOnlyRealChanges()
        {
            var drawer = new HaivenDrawerState();
            HaivenDrawerPresentation? observed = null;
            var changes = 0;
            drawer.Changed += value =>
            {
                observed = value;
                changes++;
            };

            Assert.That(drawer.Current.IsOpen, Is.False);
            Assert.That(drawer.Current.ShowHandle, Is.True);

            drawer.SetOpen(false);
            Assert.That(changes, Is.Zero);

            drawer.Toggle();
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(observed?.IsOpen, Is.True);

            drawer.Configure(new HaivenDrawerConfiguration(
                showHandle: false,
                startsOpen: false));
            Assert.That(drawer.Current.IsOpen, Is.False);
            Assert.That(drawer.Current.ShowHandle, Is.False);
            Assert.That(changes, Is.EqualTo(2));
        }
    }

    public class AnalyticsSchedulerInitializationTests
    {
        [Test] public void MissingUnityContextFailsImmediately()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new UnityMainThreadContext(null));
            Assert.That(error.Message, Does.Contain("main thread"));
        }

        [Test] public void PostUsesTheCapturedContext()
        {
            var context = new RecordingContext();
            var schedulerContext = new UnityMainThreadContext(context);
            var called = false;

            schedulerContext.Post(() => called = true);

            Assert.That(called, Is.False, "Post must queue rather than invoke inline");
            Assert.That(context.Callback, Is.Not.Null);
            context.Callback(context.State);
            Assert.That(called, Is.True);
        }

        [Test] public void DisposedContextDropsQueuedCallbacks()
        {
            var context = new RecordingContext();
            var schedulerContext = new UnityMainThreadContext(context);
            var called = false;
            schedulerContext.Post(() => called = true);

            schedulerContext.Dispose();
            context.Callback(context.State);

            Assert.That(called, Is.False);
        }

        private sealed class RecordingContext : SynchronizationContext
        {
            internal SendOrPostCallback Callback { get; private set; }
            internal object State { get; private set; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                Callback = callback;
                State = state;
            }
        }
    }
}
