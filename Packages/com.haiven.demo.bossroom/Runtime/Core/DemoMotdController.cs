#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    /// <summary>The closed message value nested inside a present MOTD projection.</summary>
    public sealed class DemoMotdMessage
    {
        internal DemoMotdMessage(string id, string title, string body, string? url)
        {
            Id = id;
            Title = title;
            Body = body;
            Url = url;
        }

        public string Id { get; }
        public string Title { get; }
        public string Body { get; }
        public string? Url { get; }

        internal bool HasSameMessage(DemoMotdMessage other) =>
            string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Body, other.Body, StringComparison.Ordinal)
            && string.Equals(Url, other.Url, StringComparison.Ordinal);
    }

    /// <summary>Normalized Message of the Day state consumed by the host-owned view.</summary>
    public sealed class DemoMotdPresentation
    {
        public const string LoadingState = "loading";
        public const string AbsentState = "absent";
        public const string PresentState = "present";
        public const string FailureState = "failure";
        public const string FallbackTitle = "Message of the day";
        public const string FailureCopy = "Unable to load message of the day.";

        private DemoMotdPresentation(
            long revision,
            string status,
            DemoMotdMessage? message,
            string? error,
            string? dismissedId)
        {
            Revision = revision;
            Status = status;
            Message = message;
            Error = error;
            DismissedId = dismissedId;
        }

        public long Revision { get; }
        public string Status { get; }
        public bool Visible => Status != AbsentState;
        public DemoMotdMessage? Message { get; }
        public string? Error { get; }
        public string? DismissedId { get; }

        public static DemoMotdPresentation Loading() => Loading(0, null);

        public static DemoMotdPresentation Absent() => Absent(0, null);

        public static DemoMotdPresentation Failure() => Failure(0, null);

        internal static DemoMotdPresentation Loading(long revision, string? dismissedId) =>
            new DemoMotdPresentation(
                revision,
                LoadingState,
                null,
                null,
                dismissedId);

        internal static DemoMotdPresentation Absent(long revision, string? dismissedId) =>
            new DemoMotdPresentation(
                revision,
                AbsentState,
                null,
                null,
                dismissedId);

        internal static DemoMotdPresentation Present(
            long revision,
            string id,
            string? title,
            string body,
            string? actionUrl,
            string? dismissedId) =>
            new DemoMotdPresentation(
                revision,
                PresentState,
                new DemoMotdMessage(
                    id,
                    string.IsNullOrWhiteSpace(title) ? FallbackTitle : title!,
                    body,
                    SafeAction(actionUrl)),
                null,
                dismissedId);

        internal static DemoMotdPresentation Failure(long revision, string? dismissedId) =>
            new DemoMotdPresentation(
                revision,
                FailureState,
                null,
                FailureCopy,
                dismissedId);

        internal static DemoMotdPresentation Restore(
            long revision,
            string state,
            string? messageId,
            string? title,
            string? body,
            string? actionUrl,
            string? error,
            string? dismissedId)
        {
            switch (state)
            {
                case LoadingState:
                    return Loading(revision, dismissedId);
                case AbsentState:
                    return Absent(revision, dismissedId);
                case PresentState:
                    if (messageId is null || body is null)
                        throw new ArgumentException("A present MOTD requires an id and body.");
                    return Present(revision, messageId, title, body, actionUrl, dismissedId);
                case FailureState:
                    if (!string.Equals(error, FailureCopy, StringComparison.Ordinal))
                        throw new ArgumentException("A failure MOTD requires the public failure copy.");
                    return Failure(revision, dismissedId);
                default:
                    throw new ArgumentException("Unknown MOTD presentation state.", nameof(state));
            }
        }

        internal bool HasSameProjection(DemoMotdPresentation other) =>
            string.Equals(Status, other.Status, StringComparison.Ordinal)
            && (Message is null
                ? other.Message is null
                : other.Message is not null && Message.HasSameMessage(other.Message))
            && string.Equals(Error, other.Error, StringComparison.Ordinal)
            && string.Equals(DismissedId, other.DismissedId, StringComparison.Ordinal);

        private static string? SafeAction(string? value)
        {
            if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return null;
            return string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parsed.Host)
                && string.IsNullOrEmpty(parsed.UserInfo)
                ? parsed.AbsoluteUri
                : null;
        }
    }

    /// <summary>
    /// Boss Room-owned copy for MOTD states that do not carry service content.
    /// Present messages always win; transport failures receive a stable, useful
    /// briefing without exposing endpoint or credential details.
    /// </summary>
    public static class DemoMotdDisplayCopy
    {
        public const string OfflineTitle = "Boss Room briefing";
        public const string OfflineBody =
            "Haiven live updates are offline. You can still play Boss Room; reconnect to see the latest message.";

        public static string Title(DemoMotdPresentation presentation)
        {
            if (presentation is null) throw new ArgumentNullException(nameof(presentation));
            if (presentation.Status == DemoMotdPresentation.PresentState)
                return presentation.Message!.Title;
            return presentation.Status == DemoMotdPresentation.FailureState
                ? OfflineTitle
                : DemoMotdPresentation.FallbackTitle;
        }

        public static string Body(DemoMotdPresentation presentation)
        {
            if (presentation is null) throw new ArgumentNullException(nameof(presentation));
            if (presentation.Status == DemoMotdPresentation.PresentState)
                return presentation.Message!.Body;
            if (presentation.Status == DemoMotdPresentation.LoadingState)
                return "Loading message…";
            return presentation.Status == DemoMotdPresentation.FailureState
                ? OfflineBody
                : "There is no message right now.";
        }
    }

    /// <summary>
    /// The finite MOTD projection shared by the public REST load, realtime events,
    /// dismissal, and the host-owned Unity view.
    /// </summary>
    internal sealed class DemoMotdProjection
    {
        private DemoMotdPresentation current;
        private string? activeRequest;

        internal DemoMotdProjection()
            : this(DemoMotdPresentation.Absent(0, null))
        {
        }

        internal DemoMotdProjection(DemoMotdPresentation initial, string? activeRequest = null)
        {
            current = initial ?? throw new ArgumentNullException(nameof(initial));
            this.activeRequest = activeRequest;
        }

        internal DemoMotdPresentation Current => current;

        internal bool LoadStarted(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("A request id is required.", nameof(requestId));
            activeRequest = requestId;
            current = DemoMotdPresentation.Loading(current.Revision + 1, current.DismissedId);
            return true;
        }

        /// <summary>
        /// A background re-read of the public message. Unlike
        /// <see cref="LoadStarted"/> it leaves the shown state in place, so a
        /// running game picks up a new or ended message without a loading flash.
        /// </summary>
        internal bool RefreshStarted(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("A request id is required.", nameof(requestId));
            activeRequest = requestId;
            return false;
        }

        /// <summary>
        /// A failed background re-read keeps the message already shown. It only
        /// settles a load it superseded, which would otherwise stay loading.
        /// </summary>
        internal bool RefreshFailed(string requestId)
        {
            if (!Consume(requestId)) return false;
            if (current.Status != DemoMotdPresentation.LoadingState) return false;
            return Change(DemoMotdPresentation.Failure(current.Revision + 1, current.DismissedId));
        }

        internal bool RestAbsent(string requestId)
        {
            if (!Consume(requestId)) return false;
            return Change(DemoMotdPresentation.Absent(current.Revision + 1, current.DismissedId));
        }

        internal bool RestPresent(
            string requestId,
            string id,
            string? title,
            string body,
            string? actionUrl)
        {
            if (!Consume(requestId)) return false;
            if (string.Equals(id, current.DismissedId, StringComparison.Ordinal))
                return Change(DemoMotdPresentation.Absent(current.Revision + 1, current.DismissedId));
            return Present(id, title, body, actionUrl);
        }

        internal bool RestFailure(string requestId)
        {
            if (!Consume(requestId)) return false;
            return Change(DemoMotdPresentation.Failure(current.Revision + 1, current.DismissedId));
        }

        internal bool RealtimePresent(string id, string? title, string body, string? actionUrl)
        {
            var changed = Present(id, title, body, actionUrl);
            if (changed) activeRequest = null;
            return changed;
        }

        internal bool RealtimeClear()
        {
            activeRequest = null;
            return Change(DemoMotdPresentation.Absent(current.Revision + 1, current.DismissedId));
        }

        internal bool Dismiss()
        {
            if (current.Status != DemoMotdPresentation.PresentState || current.Message is null) return false;
            activeRequest = null;
            current = DemoMotdPresentation.Absent(current.Revision + 1, current.Message.Id);
            return true;
        }

        internal bool Dismiss(long expectedRevision, string expectedMessageId)
        {
            if (current.Revision != expectedRevision
                || current.Message is null
                || !string.Equals(current.Message.Id, expectedMessageId, StringComparison.Ordinal))
                return false;
            return Dismiss();
        }

        private bool Present(string id, string? title, string body, string? actionUrl)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A MOTD id is required.", nameof(id));
            if (body is null) throw new ArgumentNullException(nameof(body));
            if (string.Equals(id, current.DismissedId, StringComparison.Ordinal)) return false;
            if (current.Status == DemoMotdPresentation.PresentState
                && string.Equals(id, current.Message?.Id, StringComparison.Ordinal))
                return false;
            return Change(DemoMotdPresentation.Present(
                current.Revision + 1,
                id,
                title,
                body,
                actionUrl,
                current.DismissedId));
        }

        private bool Consume(string requestId)
        {
            if (!string.Equals(requestId, activeRequest, StringComparison.Ordinal)) return false;
            activeRequest = null;
            return true;
        }

        private bool Change(DemoMotdPresentation next)
        {
            if (current.HasSameProjection(next)) return false;
            current = next;
            return true;
        }
    }

    /// <summary>
    /// Loads public MOTD without an account token, then follows authenticated
    /// realtime updates through the same identity facade.
    /// </summary>
    public sealed class DemoMotdController : IDisposable
    {
        private readonly object sync = new object();
        private readonly HaivenIdentity identity;
        private readonly DemoMotdProjection projection = new DemoMotdProjection();
        private readonly AsyncLocal<string?> restRequest = new AsyncLocal<string?>();
        private long nextLoad;
        private bool disposed;

        public DemoMotdController(RestApi rest, string integrationId)
        {
            identity = new HaivenIdentity(rest, integrationId);
            identity.MotdChanged += OnMotdChanged;
        }

        public DemoMotdPresentation Current
        {
            get { lock (sync) return projection.Current; }
        }

        public event Action<DemoMotdPresentation>? Changed;

        public Task LoadPublicAsync(CancellationToken cancellationToken = default) =>
            ReadPublicAsync(background: false, cancellationToken);

        /// <summary>
        /// Re-reads the public message in the background. The backend greets a
        /// signed-in socket with the current message only when it connects, and
        /// sends nothing when an admin posts or ends one, so a running game
        /// calls this to keep the panel current. The shown state stays in place
        /// while it runs, and a failure keeps the last message.
        /// </summary>
        public Task RefreshPublicAsync(CancellationToken cancellationToken = default) =>
            ReadPublicAsync(background: true, cancellationToken);

        private async Task ReadPublicAsync(bool background, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var requestId = Interlocked.Increment(ref nextLoad).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (background) Apply(model => model.RefreshStarted(requestId));
            else Apply(model => model.LoadStarted(requestId));
            var previousRequest = restRequest.Value;
            restRequest.Value = requestId;
            try
            {
                var value = await identity.LoadMotdAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ApplyRestValue(requestId, value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HaivenIdentityException error) when (error.Code == "superseded")
            {
                // The realtime callback already advanced the same projection.
            }
            catch (Exception)
            {
                // Presentation copy is deliberately finite and never contains
                // transport, endpoint, or credential details.
                if (background) Apply(model => model.RefreshFailed(requestId));
                else Apply(model => model.RestFailure(requestId));
            }
            finally
            {
                restRequest.Value = previousRequest;
            }
        }

        public void Attach(WsEventRouter router)
        {
            ThrowIfDisposed();
            identity.Attach(router);
        }

        /// <summary>
        /// Dismisses only the immutable projection the player actually acted on.
        /// A queued newer realtime message is left untouched.
        /// </summary>
        public bool Dismiss(DemoMotdPresentation rendered)
        {
            if (rendered is null) throw new ArgumentNullException(nameof(rendered));
            ThrowIfDisposed();
            var messageId = rendered.Message?.Id;
            if (messageId is null) return false;
            var accepted = Apply(model => model.Dismiss(rendered.Revision, messageId));
            if (!accepted) return false;
            identity.DismissMotd(messageId);
            return true;
        }

        /// <summary>Dismisses the current projection for non-view callers.</summary>
        public bool Dismiss()
        {
            DemoMotdPresentation rendered;
            lock (sync)
            {
                ThrowIfDisposed();
                rendered = projection.Current;
            }
            return Dismiss(rendered);
        }

        /// <summary>
        /// Resolves an action only while the supplied rendered projection remains
        /// current, so a queued replacement cannot redirect an older button.
        /// </summary>
        public bool TryGetAction(DemoMotdPresentation rendered, out Uri? action)
        {
            if (rendered is null) throw new ArgumentNullException(nameof(rendered));
            lock (sync)
            {
                ThrowIfDisposed();
                var current = projection.Current;
                var url = current.Message?.Url;
                if (current.Revision != rendered.Revision
                    || current.Status != DemoMotdPresentation.PresentState
                    || !string.Equals(current.Message?.Id, rendered.Message?.Id, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(url)
                    || !Uri.TryCreate(url, UriKind.Absolute, out action))
                {
                    action = null;
                    return false;
                }
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            identity.MotdChanged -= OnMotdChanged;
            identity.Dispose();
        }

        private void OnMotdChanged(HaivenMotd? value)
        {
            var requestId = restRequest.Value;
            if (requestId is not null)
            {
                ApplyRestValue(requestId, value);
                return;
            }
            if (value is null)
            {
                Apply(model => model.RealtimeClear());
                return;
            }
            Apply(model => model.RealtimePresent(
                value.Id,
                value.Title,
                value.Body,
                value.Url?.OriginalString));
        }

        private void ApplyRestValue(string requestId, HaivenMotd? value)
        {
            if (value is null)
            {
                Apply(model => model.RestAbsent(requestId));
                return;
            }
            Apply(model => model.RestPresent(
                requestId,
                value.Id,
                value.Title,
                value.Body,
                value.Url?.OriginalString));
        }

        private bool Apply(Func<DemoMotdProjection, bool> transition)
        {
            DemoMotdPresentation? changed = null;
            lock (sync)
            {
                ThrowIfDisposed();
                if (transition(projection)) changed = projection.Current;
            }
            if (changed is not null) Changed?.Invoke(changed);
            return changed is not null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(DemoMotdController));
        }
    }
}
