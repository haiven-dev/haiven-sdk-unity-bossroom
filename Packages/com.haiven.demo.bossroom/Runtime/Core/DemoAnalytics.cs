#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// The finite operation names the demo reports to <c>POST /sdk/telemetry/errors</c>.
    /// </summary>
    public static class DemoOperations
    {
        public const string RealtimeConnect = "realtime.connect";
        public const string ChatHistory = "chat.history";
        public const string ChatJoin = "chat.join";
        public const string EntitlementsRead = "entitlements.read";
        public const string RewardClaimsRead = "rewards.claims.read";
        public const string RewardClaim = "rewards.claim";
    }

    /// <summary>
    /// The demo's opt-in telemetry over the SDK's <see cref="AnalyticsBatcher"/>:
    /// <c>game.launch</c> once per run, <c>session.start</c> and <c>sdk.health</c>
    /// when the realtime socket opens, <c>session.end</c> plus one bounded
    /// <c>playtime</c> delta when the signed-in session drops, and
    /// <c>chat.message.sent</c> and <c>reward.claimed</c> from the existing send and
    /// claim paths. <see cref="Shutdown"/> ends the run the same way when the game
    /// quits, then gives the queue a bounded chance to send. SDK failures go to <c>POST /sdk/telemetry/errors</c> with the
    /// message omitted. Event names are the contract's finite
    /// <see cref="EventType"/> values, and nothing sent carries a player name, chat
    /// text, or credential. Enqueue never waits on the network and never throws
    /// into gameplay.
    /// </summary>
    public sealed class DemoAnalytics : IDisposable
    {
        /// <summary>The analytics API accepts a playtime delta in (0, 86400] seconds.</summary>
        public const double MaxPlaytimeSeconds = 86400;

        /// <summary>Error reports sent per run, so a failing loop cannot flood the endpoint.</summary>
        public const int MaxErrorReports = 16;

        /// <summary>Telemetry names and codes are capped at 64 bytes.</summary>
        private const int MaxTelemetryKey = 64;

        public const string PlatformName = "unity";
        public const string EnvironmentName = "development";
        public const string SdkName = "haiven-unity";
        public const string SdkVersion = "0.0.0";

        /// <summary>
        /// The longest <see cref="Shutdown"/> waits for queued events: the batcher's
        /// default one-second collection window plus one request. A quit on a dead
        /// network waits this long and no longer.
        /// </summary>
        public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(2);

        private readonly AnalyticsBatcher batcher;
        private readonly RestApi api;
        private readonly AnalyticsContext context;
        private readonly string analyticsSessionId;
        private readonly Func<DateTimeOffset> utcNow;
        private bool launched;
        private DateTimeOffset? sessionStartedAt;
        private long enqueued;
        private long refused;
        private int errorReports;
        private int shutDown;

        private DemoAnalytics(
            RestApi api,
            AnalyticsContext context,
            string analyticsSessionId,
            IAnalyticsScheduler? scheduler,
            AnalyticsBatcherOptions? options,
            Func<DateTimeOffset>? utcNow)
        {
            this.api = api;
            this.context = context;
            this.analyticsSessionId = analyticsSessionId;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            batcher = new AnalyticsBatcher(api, context, options, analyticsSessionId, scheduler);
        }

        /// <summary>
        /// Telemetry posting to <c>POST /sdk/analytics/events</c> and
        /// <c>POST /sdk/telemetry/errors</c> through <paramref name="api"/>.
        /// </summary>
        public static DemoAnalytics Create(
            RestApi api,
            DemoSettings settings,
            IAnalyticsScheduler? scheduler = null,
            AnalyticsBatcherOptions? options = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            if (api is null) throw new ArgumentNullException(nameof(api));
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            return new DemoAnalytics(
                api,
                ContextFor(settings),
                Guid.NewGuid().ToString("D"),
                scheduler,
                options,
                utcNow);
        }

        /// <summary>
        /// The demo's telemetry when the presenter opted in (HAIVEN_DEMO_ANALYTICS),
        /// otherwise null: an opted-out run creates no queue and no scheduler, so it
        /// sends nothing, at quit included.
        /// </summary>
        public static DemoAnalytics? CreateIfOptedIn(
            RestApi api,
            DemoSettings settings,
            Func<IAnalyticsScheduler?> scheduler,
            AnalyticsBatcherOptions? options = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            if (scheduler is null) throw new ArgumentNullException(nameof(scheduler));
            if (!settings.AnalyticsOptIn) return null;
            return Create(api, settings, scheduler(), options, utcNow);
        }

        /// <summary>The runtime context every batch carries: integration, platform, environment, SDK.</summary>
        public static AnalyticsContext ContextFor(DemoSettings settings) => new AnalyticsContext
        {
            Environment = EnvironmentName,
            IntegrationId = settings.IntegrationId,
            Platform = PlatformName,
            Sdk = new AnalyticsSDK { Name = SdkName, Version = SdkVersion },
        };

        /// <summary>The underlying queue, for counts and diagnostics.</summary>
        public AnalyticsBatcher Batcher => batcher;

        /// <summary>The client-generated analytics session id shared by events and error reports.</summary>
        public string AnalyticsSessionId => analyticsSessionId;

        /// <summary>A session is open between <see cref="SessionStarted"/> and <see cref="SessionEnded"/>.</summary>
        public bool InSession => sessionStartedAt.HasValue;

        /// <summary>Events accepted into the queue and not yet sent, retried, or dropped.</summary>
        public long QueuedCount => batcher.BufferedEventCount;

        /// <summary>Events accepted into the queue that a request has delivered.</summary>
        public long FlushedCount
        {
            get
            {
                var queued = batcher.BufferedEventCount;
                var lost = Math.Max(0, batcher.DroppedEventCount - Interlocked.Read(ref refused));
                return Math.Max(0, Interlocked.Read(ref enqueued) - queued - lost);
            }
        }

        /// <summary>The counts shown beside the HAIVEN connection-status line.</summary>
        public string CountsText => "EVENTS " + QueuedCount + " QUEUED · " + FlushedCount + " FLUSHED";

        /// <summary>Enqueues <c>game.launch</c> the first time a connect begins in this run.</summary>
        public void Connecting()
        {
            if (launched) return;
            launched = true;
            Enqueue(EventType.EventGameLaunch, null, utcNow());
        }

        /// <summary>The socket opened: <c>session.start</c>, once per session, then <c>sdk.health</c> ok.</summary>
        public void SessionStarted()
        {
            var now = utcNow();
            if (!sessionStartedAt.HasValue)
            {
                sessionStartedAt = now;
                Enqueue(EventType.EventSessionStart, null, now);
            }
            Health(true, now);
        }

        /// <summary>The socket could not open: <c>sdk.health</c> error.</summary>
        public void ConnectFailed() => Health(false, utcNow());

        /// <summary>
        /// Ends an open session: <c>session.end</c> and one <c>playtime</c> delta ending
        /// at the same instant. A second call, or a call with no session, sends nothing.
        /// </summary>
        public void SessionEnded()
        {
            if (!sessionStartedAt.HasValue) return;
            var started = sessionStartedAt.Value;
            sessionStartedAt = null;
            var now = utcNow();
            Enqueue(EventType.EventSessionEnd, null, now);
            var seconds = PlaytimeSeconds(started, now);
            if (seconds > 0)
            {
                Enqueue(
                    EventType.EventPlaytime,
                    new Dictionary<string, object?> { ["seconds"] = seconds },
                    now);
            }
        }

        /// <summary>
        /// The player sent a chat message. The event has no properties: the text,
        /// channel, and sender stay out of analytics.
        /// </summary>
        public void ChatMessageSent() => Enqueue(EventType.EventChatMessageSent, null, utcNow());

        /// <summary>A reward claim was recorded; the event carries only the public reward ref.</summary>
        public void RewardClaimed(string rewardRef)
        {
            if (string.IsNullOrEmpty(rewardRef)) return;
            Enqueue(
                EventType.EventRewardClaimed,
                new Dictionary<string, object?> { ["rewardRef"] = rewardRef },
                utcNow());
        }

        /// <summary>The session's length in seconds, clamped to [0, <see cref="MaxPlaytimeSeconds"/>].</summary>
        public static double PlaytimeSeconds(DateTimeOffset started, DateTimeOffset ended)
        {
            var seconds = Math.Round((ended - started).TotalSeconds, 3);
            if (seconds <= 0) return 0;
            return Math.Min(seconds, MaxPlaytimeSeconds);
        }

        /// <summary>
        /// Reports one SDK failure through the generated
        /// <see cref="RestApi.PostSdkTelemetryErrorsAsync"/> request, at most
        /// <see cref="MaxErrorReports"/> per run. The returned task never faults: a
        /// telemetry failure is never allowed to replace the original one.
        /// </summary>
        public Task<bool> ReportErrorAsync(string operation, Exception error)
        {
            if (string.IsNullOrEmpty(operation) || error is null) return Task.FromResult(false);
            if (Interlocked.Increment(ref errorReports) > MaxErrorReports) return Task.FromResult(false);
            return SendErrorAsync(ErrorReport(
                operation,
                error,
                context,
                analyticsSessionId,
                Guid.NewGuid().ToString("D"),
                utcNow()));
        }

        /// <summary>
        /// One bounded error report. The exception message, stack, and response body
        /// are never copied: only the operation, the exception type, and the API's
        /// finite error code when it has one.
        /// </summary>
        public static ReportTelemetryErrorRequest ErrorReport(
            string operation,
            Exception error,
            AnalyticsContext context,
            string sessionId,
            string eventId,
            DateTimeOffset occurredAt) => new ReportTelemetryErrorRequest
            {
                EventId = eventId,
                OccurredAt = occurredAt.ToUniversalTime().ToString("O"),
                Runtime = context,
                SessionId = sessionId,
                Error = new TelemetryError
                {
                    Code = FiniteCode((error as HaivenRestException)?.Code),
                    Operation = FiniteCode(operation),
                    Type = FiniteCode(error.GetType().Name),
                },
            };

        /// <summary>
        /// Ends the run when the game quits or the demo is torn down: enqueues
        /// <c>session.end</c> and its <c>playtime</c> for a session still open (none
        /// when <see cref="SessionEnded"/> already ran), waits at most
        /// <paramref name="timeout"/> for the queue to send, then disposes, which
        /// drops anything still queued and cancels a request in flight. Only the
        /// first call does anything. Returns true when every queued event was sent
        /// or settled within the bound; never throws.
        /// </summary>
        public bool Shutdown(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref shutDown, 1) != 0) return false;
            var drained = false;
            try
            {
                SessionEnded();
                var flushed = batcher.FlushAsync();
                drained = timeout > TimeSpan.Zero ? flushed.Wait(timeout) : flushed.IsCompleted;
            }
            catch (Exception)
            {
                // Quitting must never fail on analytics.
            }
            finally
            {
                batcher.Dispose();
            }
            return drained;
        }

        public void Dispose() => batcher.Dispose();

        private async Task<bool> SendErrorAsync(ReportTelemetryErrorRequest request)
        {
            try
            {
                await api.PostSdkTelemetryErrorsAsync(request).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Health(bool healthy, DateTimeOffset now) =>
            Enqueue(
                EventType.EventSDKHealth,
                new Dictionary<string, object?> { ["status"] = healthy ? "ok" : "error" },
                now);

        private bool Enqueue(string type, IReadOnlyDictionary<string, object?>? properties, DateTimeOffset now)
        {
            try
            {
                if (batcher.TryEnqueue(type, properties, now))
                {
                    Interlocked.Increment(ref enqueued);
                    return true;
                }
                Interlocked.Increment(ref refused);
                return false;
            }
            catch (ObjectDisposedException)
            {
                // The demo is shutting down; analytics never reaches gameplay.
                return false;
            }
        }

        /// <summary>The value when it is an ASCII identifier of at most 64 characters, otherwise null.</summary>
        private static string? FiniteCode(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > MaxTelemetryKey) return null;
            foreach (var character in value)
            {
                var allowed = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '_' || character == '.' || character == '-';
                if (!allowed) return null;
            }
            return value;
        }
    }
}
