#nullable enable
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;

namespace Haiven.Demo.BossRoom
{
    public enum DemoSignInState
    {
        SignedOut,
        SigningIn,
        SignedIn,
        Failed,
        Expired,
    }

    /// <summary>
    /// What the view shows for sign-in: a short connection-status line, the
    /// product copy from docs/game-auth-handoff.md, and the one action, if any.
    /// Never carries the token, the code, or an exception message.
    /// </summary>
    public sealed class DemoSignInPresentation
    {
        internal DemoSignInPresentation(
            DemoSignInState state,
            string status,
            string message,
            string? actionLabel,
            bool canStart)
        {
            State = state;
            Status = status;
            Message = message;
            ActionLabel = actionLabel;
            CanStart = canStart;
        }

        public DemoSignInState State { get; }

        /// <summary>Upper-case connection status, for example <c>SIGNED OUT</c>.</summary>
        public string Status { get; }

        /// <summary>The sentence shown to the player.</summary>
        public string Message { get; }

        /// <summary>The sign-in control's caption; null hides the control.</summary>
        public string? ActionLabel { get; }

        /// <summary>Whether the control starts a sign-in now.</summary>
        public bool CanStart { get; }
    }

    /// <summary>
    /// "Sign in with Haiven" for the desktop demo: the SDK's PKCE handoff
    /// (<see cref="HandoffCoordinator"/>) over a fixed loopback redirect
    /// (<see cref="LoopbackHandoffListener"/>). The access token is held in
    /// memory only and is never logged, shown, or persisted. A non-empty
    /// HAIVEN_DEMO_ACCESS_TOKEN is the shortcut: the demo starts signed in and
    /// never opens the browser.
    /// </summary>
    public sealed class DemoSignIn
    {
        // Match the web handoff window so first-time profile or age setup does not
        // strand the player after the game has already stopped listening.
        public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(30);

        internal static readonly DemoSignInPresentation SignedOut = new DemoSignInPresentation(
            DemoSignInState.SignedOut, "SIGNED OUT",
            "Sign in or register with Haiven to join chat and claim rewards.", "Sign in or register", true);
        internal static readonly DemoSignInPresentation Waiting = new DemoSignInPresentation(
            DemoSignInState.SigningIn, "SIGNING IN · WAITING FOR HAIVEN",
            "Check your browser to finish signing in. If needed, choose Try again to open a fresh sign-in.",
            "Try again", true);
        internal static readonly DemoSignInPresentation SignedIn = new DemoSignInPresentation(
            DemoSignInState.SignedIn, "SIGNED IN", "Signed in with Haiven.", null, false);
        internal static readonly DemoSignInPresentation SignedInWithToken = new DemoSignInPresentation(
            DemoSignInState.SignedIn, "SIGNED IN · ACCESS TOKEN",
            "Using the access token from " + DemoSettings.AccessTokenVariable + ".", null, false);
        internal static readonly DemoSignInPresentation NotConfigured = new DemoSignInPresentation(
            DemoSignInState.Failed, "SIGN-IN NOT CONFIGURED",
            "Set " + DemoSettings.AccessTokenVariable + ", or a valid " + DemoSettings.SignInUrlVariable
            + " and " + DemoSettings.RedirectUriVariable + ".", null, false);
        internal static readonly DemoSignInPresentation Cancelled = new DemoSignInPresentation(
            DemoSignInState.Failed, "SIGN-IN CANCELLED",
            "Sign-in was cancelled. No account was connected.", "Try again", true);
        internal static readonly DemoSignInPresentation PortBusy = new DemoSignInPresentation(
            DemoSignInState.Failed, "SIGN-IN FAILED",
            "The sign-in callback port is in use. Close the other copy of the game and try again.", "Try again", true);
        internal static readonly DemoSignInPresentation Unavailable = new DemoSignInPresentation(
            DemoSignInState.Failed, "SIGN-IN UNAVAILABLE",
            "Haiven sign-in is not available on this server yet.", "Try again", true);
        internal static readonly DemoSignInPresentation Failed = new DemoSignInPresentation(
            DemoSignInState.Failed, "SIGN-IN FAILED", "Haiven sign-in failed. Try again.", "Try again", true);
        internal static readonly DemoSignInPresentation LinkExpired = new DemoSignInPresentation(
            DemoSignInState.Expired, "SIGN-IN EXPIRED",
            "This sign-in link expired. Start again to continue.", "Start again", true);
        internal static readonly DemoSignInPresentation SessionExpired = new DemoSignInPresentation(
            DemoSignInState.Expired, "SESSION EXPIRED",
            "Your Haiven session expired. Sign in again to continue.", "Sign in again", true);

        private readonly object gate = new object();
        private readonly HandoffOptions? options;
        private readonly IHandoffHttpTransport exchange;
        private readonly Action<Action> post;
        private readonly IHandoffClock clock;
        private readonly string environmentToken;
        private readonly SemaphoreSlim attemptGate = new SemaphoreSlim(1, 1);
        private HandoffCredential? credential;
        private DemoSignInPresentation current;
        private CancellationTokenSource? activeAttempt;
        private int requestedAttempt;

        /// <param name="environmentToken">HAIVEN_DEMO_ACCESS_TOKEN; null or empty means sign in with Haiven.</param>
        /// <param name="browser">Opens the sign-in page; replaceable through <see cref="Browser"/>.</param>
        /// <param name="exchange">The handoff exchange transport (UnityHandoffHttpTransport in Unity).</param>
        /// <param name="post">Runs <see cref="Changed"/> on the game's thread.</param>
        public DemoSignIn(
            DemoSettings settings,
            string? environmentToken,
            IHandoffBrowser browser,
            IHandoffHttpTransport exchange,
            Action<Action> post,
            IHandoffClock? clock = null)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            Browser = browser ?? throw new ArgumentNullException(nameof(browser));
            this.exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
            this.post = post ?? throw new ArgumentNullException(nameof(post));
            this.clock = clock ?? new SystemHandoffClock();
            this.environmentToken = environmentToken ?? "";
            options = TryOptions(settings);
            current = this.environmentToken.Length > 0
                ? SignedInWithToken
                : options is null ? NotConfigured : SignedOut;
        }

        /// <summary>Opens the sign-in page. A host or test can replace it before <see cref="SignInAsync"/>.</summary>
        public IHandoffBrowser Browser { get; set; }

        /// <summary>How long to wait for the browser to come back before the link counts as expired.</summary>
        public TimeSpan WaitTimeout { get; set; } = DefaultWaitTimeout;

        public DemoSignInPresentation Current
        {
            get { lock (gate) return current; }
        }

        /// <summary>Whether the token came from HAIVEN_DEMO_ACCESS_TOKEN rather than a sign-in.</summary>
        public bool UsesEnvironmentToken => environmentToken.Length > 0;

        /// <summary>
        /// The bearer for REST and the socket handshake while signed in, otherwise
        /// empty. Read it at use time; never log it.
        /// </summary>
        public string AccessToken
        {
            get
            {
                lock (gate)
                {
                    if (current.State != DemoSignInState.SignedIn) return "";
                    return credential?.AccessToken ?? environmentToken;
                }
            }
        }

        /// <summary>Raised through the <c>post</c> dispatcher after every state change.</summary>
        public event Action<DemoSignInPresentation>? Changed;

        /// <summary>
        /// Opens Haiven's sign-in page and waits for the loopback redirect, then
        /// exchanges the one-use code. Calling again while the browser is pending
        /// cancels that attempt before opening a fresh PKCE handoff. True once
        /// signed in; false while already signed in or when superseded by a newer
        /// attempt. Never throws for a sign-in failure; the outcome is in
        /// <see cref="Current"/>.
        /// </summary>
        public async Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        {
            if (options is null || UsesEnvironmentToken) return false;
            int request;
            lock (gate)
            {
                if (current.State == DemoSignInState.SignedIn) return false;
                request = ++requestedAttempt;
                activeAttempt?.Cancel();
            }

            await attemptGate.WaitAsync();
            CancellationTokenSource? attempt = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Publish(SignedOut);
                    return false;
                }
                lock (gate)
                {
                    if (request != requestedAttempt || current.State == DemoSignInState.SignedIn)
                        return false;
                    credential = null;
                    attempt = new CancellationTokenSource();
                    activeAttempt = attempt;
                }
                Publish(Waiting);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, attempt!.Token);
                timeout.CancelAfter(WaitTimeout);
                var coordinator = new HandoffCoordinator(
                    options,
                    Browser,
                    new MemoryHandoffTransactionStore(),
                    new MemoryHandoffTokenStore(),
                    exchange,
                    new ImmediateHandoffDispatcher(),
                    clock);
                HandoffCredential received;
                // Bound before the browser opens, so a busy port fails here, not after the player signs in.
                using (var listener = LoopbackHandoffListener.Start(options.RedirectUri))
                    received = await coordinator.LoginAsync(listener, timeout.Token);
                if (received.ExpiresAt <= clock.UtcNow)
                {
                    Publish(SessionExpired);
                    return false;
                }
                lock (gate) credential = received;
                Publish(SignedIn);
                return true;
            }
            catch (OperationCanceledException) when (attempt?.IsCancellationRequested == true)
            {
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Publish(SignedOut);
                return false;
            }
            catch (OperationCanceledException)
            {
                Publish(LinkExpired);
                return false;
            }
            catch (Exception error)
            {
                Publish(Classify(error));
                return false;
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeAttempt, attempt)) activeAttempt = null;
                }
                attempt?.Dispose();
                attemptGate.Release();
            }
        }

        /// <summary>
        /// Moves a signed-in handoff credential to <see cref="DemoSignInState.Expired"/>
        /// once its expiry passes. True only on the call that expired it. The
        /// HAIVEN_DEMO_ACCESS_TOKEN shortcut has no known expiry and is left alone.
        /// </summary>
        public bool CheckExpiry()
        {
            lock (gate)
            {
                if (current.State != DemoSignInState.SignedIn || credential is null) return false;
                if (clock.UtcNow < credential.ExpiresAt) return false;
                credential = null;
            }
            Publish(SessionExpired);
            return true;
        }

        /// <summary>
        /// The finite outcome for a sign-in failure. Reads the SDK's exception
        /// category, never shows its text.
        /// </summary>
        internal static DemoSignInPresentation Classify(Exception error)
        {
            if (error is HandoffException handoff)
            {
                if (handoff.InnerException is SocketException) return PortBusy;
                var message = handoff.Message;
                if (message.StartsWith("The handoff callback failed", StringComparison.Ordinal)) return Cancelled;
                if (message.Contains("HTTP 503")) return Unavailable;
                if (message.Contains("expired") || message.Contains("HTTP 400") || message.Contains("HTTP 401"))
                    return LinkExpired;
            }
            return Failed;
        }

        private static HandoffOptions? TryOptions(DemoSettings settings)
        {
            try
            {
                var options = new HandoffOptions(
                    new Uri(DemoSettings.ExpandPlayer(settings.SignInUrl, Environment.GetCommandLineArgs())),
                    new Uri(settings.ExchangeUrl),
                    settings.IntegrationId,
                    new Uri(settings.RedirectUri));
                LoopbackHandoffListener.LoopbackAddress(options.RedirectUri);
                return options;
            }
            catch (Exception error) when (error is ArgumentException || error is UriFormatException)
            {
                return null;
            }
        }

        private void Publish(DemoSignInPresentation presentation)
        {
            lock (gate) current = presentation;
            post(() => Changed?.Invoke(presentation));
        }
    }
}
