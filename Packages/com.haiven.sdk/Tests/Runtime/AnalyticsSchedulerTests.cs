using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Sdk.Tests
{
    public class AnalyticsSchedulerTests
    {
        [UnityTest] public IEnumerator CallbackReturnsToTheUnityMainThread()
        {
            var unityThread = System.Environment.CurrentManagedThreadId;
            var callbackThread = -1;
            var host = new GameObject("analytics-scheduler-test");
            var scheduler = host.AddComponent<UnityAnalyticsScheduler>();

            ((IAnalyticsScheduler)scheduler).Start(async () =>
            {
                await Task.Delay(10).ConfigureAwait(false);
            scheduler.Post(() => callbackThread = System.Environment.CurrentManagedThreadId);
            });

            var deadline = Time.realtimeSinceStartup + 10;
            while (callbackThread < 0 && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(callbackThread, Is.EqualTo(unityThread));
            UnityEngine.Object.Destroy(host);
        }

        [UnityTest] public IEnumerator DestroyWhilePendingCancelsDelayAndDropsPost()
        {
            var host = new GameObject("analytics-scheduler-destroy-test");
            var scheduler = host.AddComponent<UnityAnalyticsScheduler>();
            var posted = false;
            var delay = scheduler.Delay(TimeSpan.FromHours(1), CancellationToken.None);
            scheduler.Post(() => posted = true);

            UnityEngine.Object.DestroyImmediate(host);
            var deadline = Time.realtimeSinceStartup + 1;
            while (!delay.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(delay.IsCompleted, Is.True, "destroy must not leave a pending delay hanging");
            Assert.That(delay.IsCanceled, Is.True, "destroy must settle a pending delay");
            Assert.That(posted, Is.False, "destroyed schedulers must drop queued callbacks");
        }

        [UnityTest] public IEnumerator DestroyedSchedulerReportsRefusedBatch()
        {
            var host = new GameObject("analytics-batcher-refused-start-test");
            var scheduler = host.AddComponent<UnityAnalyticsScheduler>();
            var batcher = new AnalyticsBatcher(
                new RestApi(new UnexpectedTransport()),
                new AnalyticsContext
                {
                    Environment = "test",
                    IntegrationId = "integration",
                    Platform = "unity",
                    Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                },
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    FlushInterval = TimeSpan.Zero,
                },
                scheduler: scheduler);
            AnalyticsDiagnostic refused = null;
            batcher.Diagnostic += diagnostic => refused = diagnostic;

            UnityEngine.Object.DestroyImmediate(host);
            Assert.Throws<ObjectDisposedException>(() => batcher.TryEnqueue(new AnalyticsEvent
            {
                EventId = "refused-event",
                Type = "scheduler.destroyed",
            }));

            Assert.That(batcher.FlushAsync().IsCompletedSuccessfully, Is.True);
            Assert.That(batcher.BufferedEventCount, Is.Zero);
            Assert.That(batcher.DroppedEventCount, Is.EqualTo(1));
            Assert.That(refused, Is.Not.Null, "the refused-start diagnostic must not use the destroyed scheduler");
            Assert.That(refused.EventIds, Is.EqualTo(new[] { "refused-event" }));
            Assert.That(refused.Exception, Is.TypeOf<ObjectDisposedException>());
            batcher.Dispose();
            yield break;
        }

        [UnityTest] public IEnumerator DisposeAndDestroySettleBatcherFlush()
        {
            var host = new GameObject("analytics-batcher-destroy-test");
            var scheduler = host.AddComponent<UnityAnalyticsScheduler>();
            var batcher = new AnalyticsBatcher(
                new RestApi(new UnexpectedTransport()),
                new AnalyticsContext
                {
                    Environment = "test",
                    IntegrationId = "integration",
                    Platform = "unity",
                    Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                },
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    FlushInterval = TimeSpan.FromHours(1),
                },
                scheduler: scheduler);

            Assert.That(batcher.TryEnqueue("destroy.pending"), Is.True);
            var flush = batcher.FlushAsync();
            yield return null;
            batcher.Dispose();
            UnityEngine.Object.DestroyImmediate(host);

            var deadline = Time.realtimeSinceStartup + 1;
            while (!flush.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(flush.IsCompletedSuccessfully, Is.True, "teardown must settle FlushAsync");
        }

        [UnityTest] public IEnumerator DestroyDuringDelayStartupCancelsAndSettlesBatcherFlush()
        {
            var host = new GameObject("analytics-scheduler-startup-race-test");
            var scheduler = host.AddComponent<UnityAnalyticsScheduler>();
            using var delayReachedLifetimeBoundary = new ManualResetEventSlim();
            using var allowDelayToContinue = new ManualResetEventSlim();
            scheduler.BeforeLifetimeLinkForTesting = () =>
            {
                delayReachedLifetimeBoundary.Set();
                allowDelayToContinue.Wait(TimeSpan.FromSeconds(10));
            };
            var batcher = new AnalyticsBatcher(
                new RestApi(new UnexpectedTransport()),
                new AnalyticsContext
                {
                    Environment = "test",
                    IntegrationId = "integration",
                    Platform = "unity",
                    Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                },
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    FlushInterval = TimeSpan.FromHours(1),
                },
                scheduler: scheduler);

            Assert.That(batcher.TryEnqueue("destroy.startup-race"), Is.True);
            var flush = batcher.FlushAsync();
            var deadline = Time.realtimeSinceStartup + 10;
            while (!delayReachedLifetimeBoundary.IsSet && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.That(delayReachedLifetimeBoundary.IsSet, Is.True, "pump must reach the delay lifetime boundary");
            UnityEngine.Object.DestroyImmediate(host);
            allowDelayToContinue.Set();

            deadline = Time.realtimeSinceStartup + 10;
            while (!flush.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(flush.IsCompletedSuccessfully, Is.True,
                "destroy racing delay startup must cancel the pump and settle FlushAsync");
            Assert.That(batcher.BufferedEventCount, Is.Zero);
            Assert.That(batcher.DroppedEventCount, Is.EqualTo(1));
            batcher.Dispose();
        }

        private sealed class UnexpectedTransport : IRestApiTransport
        {
            public Task<string> SendAsync(RestRequest request, CancellationToken cancellationToken) =>
                throw new AssertionException("flush interval should be canceled before transport");

            public Uri Url(string path) => new Uri("https://example.invalid" + path);
        }
    }
}
