#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Haiven.Sdk
{
    /// <summary>Opens an ordinary external HTTPS link, such as a MOTD call to action.</summary>
    public interface IUnityExternalLinkBrowser
    {
        void Open(Uri uri);
    }

    public sealed class UnityExternalLinkBrowser : IUnityExternalLinkBrowser
    {
        public void Open(Uri uri)
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Only HTTPS links may leave the game.", nameof(uri));
            Application.OpenURL(uri.AbsoluteUri);
        }
    }

    /// <summary>
    /// Dependency-free Unity modal for MOTD and account-link settings. Configure
    /// it with <see cref="HaivenIdentity"/> after creating the game's REST and
    /// realtime clients.
    /// </summary>
    /// <remarks>
    /// The modal deliberately has no built-in account-link browser adapter.
    /// Linking is enabled only when the facade was constructed with an
    /// <see cref="IIdentityLinkBrowserContext"/> that owns a same-browser popup,
    /// cookie and redirect flow. Opening a URL with <see cref="Application.OpenURL"/>
    /// after a game-side POST would not preserve that browser context.
    /// </remarks>
    public sealed class UnityIdentityModal : MonoBehaviour
    {
        [SerializeField] private bool visible = true;
        [SerializeField] private string windowTitle = "Haiven account";
        [SerializeField] private Rect windowRect = new Rect(24, 24, 420, 420);

        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object accountLifetimeSync = new object();
        private readonly List<CancellationTokenSource> retiredAccountLifetimes =
            new List<CancellationTokenSource>();
        private CancellationTokenSource accountLifetime = new CancellationTokenSource();
        private HaivenIdentity? identity;
        private IUnityExternalLinkBrowser externalLinks = new UnityExternalLinkBrowser();
        private UnityMainThreadContext? mainThread;
        private bool authenticated;
        private int busy;
        private int destroyed;
        private int configurationGeneration;
        private int accountLifetimeGeneration;
        private long statusRevision;
        private string? statusCode;
        private Action<HaivenMotd?>? motdHandler;
        private Action<HaivenSignInMethods?>? methodsHandler;
        private Action<HaivenIdentityMethod>? linkedHandler;
        private Action<IdentityLinkFailure>? failedHandler;
        private Action<string>? diagnosticHandler;

        public event Action? StateChanged;

        public bool Visible
        {
            get => visible;
            set
            {
                if (visible == value) return;
                visible = value;
                StateChanged?.Invoke();
            }
        }

        public bool Authenticated => authenticated;
        public bool IsBusy => Volatile.Read(ref busy) != 0;
        public string? StatusCode => statusCode;
        public HaivenMotd? Motd => identity?.CurrentMotd;
        public HaivenSignInMethods? SignInMethods => identity?.CurrentSignInMethods;
        public bool CanBeginLink => authenticated && identity?.CanBeginLink == true;

        public void Configure(
            HaivenIdentity value,
            bool isAuthenticated,
            IUnityExternalLinkBrowser? externalLinkBrowser = null)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            ThrowIfDestroyed();
            if (IsBusy) throw new InvalidOperationException("Cannot replace identity state during an operation.");
            var dispatcher = new UnityMainThreadContext(
                SynchronizationContext.Current
                ?? throw new InvalidOperationException(
                    "Configure UnityIdentityModal on Unity's main thread."));
            var generation = Interlocked.Increment(ref configurationGeneration);
            Unsubscribe();
            RotateAccountLifetime();
            mainThread?.Dispose();
            identity = value;
            authenticated = isAuthenticated;
            externalLinks = externalLinkBrowser ?? new UnityExternalLinkBrowser();
            mainThread = dispatcher;
            Subscribe(
                generation,
                Volatile.Read(ref accountLifetimeGeneration),
                Volatile.Read(ref authenticated));
            SetStatus(null, false);
            StateChanged?.Invoke();
        }

        public void SetAuthenticated(bool value)
        {
            RequireIdentity();
            if (authenticated == value) return;
            Unsubscribe();
            RotateAccountLifetime();
            authenticated = value;
            if (!value) identity!.ClearAccount();
            Subscribe(
                Volatile.Read(ref configurationGeneration),
                Volatile.Read(ref accountLifetimeGeneration),
                value);
            SetStatus(null, false);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Loads public MOTD before sign-in, or account sign-in methods after
        /// sign-in. Signed-in MOTD updates arrive through the attached router.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            var current = RequireIdentity();
            EnterOperation();
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token, AccountLifetimeToken()))
                {
                    if (authenticated)
                        await current.LoadSignInMethodsAsync(linked.Token);
                    else
                        await current.LoadMotdAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                SetStatus("ready");
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Opens a provider flow. Completion is intentionally not inferred from
        /// this task; the account.identity.linked/link.failed event is authoritative.
        /// </summary>
        public async Task<IdentityLinkLaunch> BeginLinkAsync(
            string provider,
            CancellationToken cancellationToken = default)
        {
            var current = RequireIdentity();
            if (!authenticated) throw new InvalidOperationException("Sign in before linking an identity.");
            EnterOperation();
            try
            {
                var pendingRevision = SetStatus("link_opening");
                IdentityLinkLaunch launch;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token, AccountLifetimeToken()))
                {
                    launch = await current.BeginLinkAsync(provider, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                TrySetStatus(
                    pendingRevision,
                    launch.Succeeded ? "link_opened_waiting_for_realtime" : launch.ErrorCode);
                return launch;
            }
            finally
            {
                ExitOperation();
            }
        }

        public async Task UnlinkAsync(string provider, CancellationToken cancellationToken = default)
        {
            var current = RequireIdentity();
            if (!authenticated) throw new InvalidOperationException("Sign in before unlinking an identity.");
            EnterOperation();
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token, AccountLifetimeToken()))
                {
                    await current.UnlinkAsync(provider, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                SetStatus("identity_unlinked");
            }
            finally
            {
                ExitOperation();
            }
        }

        public void OpenMotdLink()
        {
            var url = RequireIdentity().CurrentMotd?.Url
                ?? throw new InvalidOperationException("The current MOTD has no HTTPS link.");
            externalLinks.Open(url);
        }

        public void DismissMotd() => RequireIdentity().DismissMotd();

        private void OnGUI()
        {
            if (!visible || identity is null) return;
            windowRect = GUI.ModalWindow(GetInstanceID(), windowRect, DrawWindow, windowTitle);
        }

        private void DrawWindow(int id)
        {
            var motd = Motd;
            if (motd is not null)
            {
                if (!string.IsNullOrEmpty(motd.Title)) GUILayout.Label(motd.Title);
                GUILayout.Label(motd.Body);
                GUILayout.BeginHorizontal();
                if (motd.Url is not null && GUILayout.Button("Learn more")) OpenMotdLink();
                if (GUILayout.Button("Dismiss")) DismissMotd();
                GUILayout.EndHorizontal();
                GUILayout.Space(12);
            }

            if (authenticated)
            {
                GUILayout.Label("Sign-in methods");
                var methods = SignInMethods;
                if (methods is null)
                {
                    GUILayout.Label("Refresh to load account methods.");
                }
                else
                {
                    GUILayout.Label(methods.HasPassword ? "Password enabled" : "No password sign-in");
                    foreach (var method in methods.Identities)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(method.Provider + ": " + method.Email);
                        GUI.enabled = !IsBusy;
                        if (GUILayout.Button("Unlink")) RunUi(() => UnlinkAsync(method.Provider));
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                }

                GUI.enabled = !IsBusy && CanBeginLink;
                if (GUILayout.Button("Link Google")) RunLinkUi(() => BeginLinkAsync("google"));
                if (GUILayout.Button("Link Apple")) RunLinkUi(() => BeginLinkAsync("apple"));
                GUI.enabled = true;
                if (!CanBeginLink)
                    GUILayout.Label("Account linking needs a same-browser context adapter.");
            }

            GUI.enabled = !IsBusy;
            if (GUILayout.Button("Refresh")) RunUi(() => RefreshAsync());
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(statusCode)) GUILayout.Label("Status: " + statusCode);
            GUI.DragWindow();
        }

        private async void RunUi(Func<Task> operation)
        {
            var generation = Volatile.Read(ref accountLifetimeGeneration);
            try { await operation(); }
            catch (OperationCanceledException)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus("cancelled");
            }
            catch (HaivenIdentityException error)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus(error.Code);
            }
            catch (Exception error)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus("operation_failed");
                Debug.LogException(error);
            }
        }

        private async void RunLinkUi(Func<Task<IdentityLinkLaunch>> operation)
        {
            var generation = Volatile.Read(ref accountLifetimeGeneration);
            try { await operation(); }
            catch (OperationCanceledException)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus("cancelled");
            }
            catch (HaivenIdentityException error)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus(error.Code);
            }
            catch (Exception error)
            {
                if (generation == Volatile.Read(ref accountLifetimeGeneration))
                    SetStatus("operation_failed");
                Debug.LogException(error);
            }
        }

        private void Subscribe(int configuration, int account, bool expectedAuthenticated)
        {
            if (identity is null) return;
            motdHandler = _ => Publish(
                configuration, account, expectedAuthenticated, () => StateChanged?.Invoke());
            identity.MotdChanged += motdHandler;
            if (expectedAuthenticated)
            {
                methodsHandler = _ => Publish(
                    configuration, account, true, () => StateChanged?.Invoke());
                linkedHandler = _ => Publish(
                    configuration, account, true, () => SetStatus("identity_linked"));
                failedHandler = value => Publish(
                    configuration, account, true, () => SetStatus(value.Code));
                diagnosticHandler = code => Publish(
                    configuration, account, true, () => SetStatus(code));
                identity.SignInMethodsChanged += methodsHandler;
                identity.IdentityLinked += linkedHandler;
                identity.IdentityLinkFailed += failedHandler;
                identity.Diagnostic += diagnosticHandler;
            }
        }

        private void Unsubscribe()
        {
            if (identity is null) return;
            if (motdHandler is not null) identity.MotdChanged -= motdHandler;
            if (methodsHandler is not null) identity.SignInMethodsChanged -= methodsHandler;
            if (linkedHandler is not null) identity.IdentityLinked -= linkedHandler;
            if (failedHandler is not null) identity.IdentityLinkFailed -= failedHandler;
            if (diagnosticHandler is not null) identity.Diagnostic -= diagnosticHandler;
            motdHandler = null;
            methodsHandler = null;
            linkedHandler = null;
            failedHandler = null;
            diagnosticHandler = null;
        }

        private void Publish(
            int configuration,
            int account,
            bool expectedAuthenticated,
            Action callback)
        {
            if (!IsCurrent(configuration, account, expectedAuthenticated)) return;
            var dispatcher = mainThread;
            if (dispatcher is null) return;
            dispatcher.RunOrPost(() =>
            {
                if (Volatile.Read(ref destroyed) == 0
                    && IsCurrent(configuration, account, expectedAuthenticated))
                    callback();
            });
        }

        private bool IsCurrent(int configuration, int account, bool expectedAuthenticated) =>
            configuration == Volatile.Read(ref configurationGeneration)
            && account == Volatile.Read(ref accountLifetimeGeneration)
            && expectedAuthenticated == Volatile.Read(ref authenticated);

        private long SetStatus(string? value, bool notify = true)
        {
            var revision = Interlocked.Increment(ref statusRevision);
            statusCode = value;
            if (notify) StateChanged?.Invoke();
            return revision;
        }

        private bool TrySetStatus(long expectedRevision, string? value)
        {
            if (Interlocked.CompareExchange(
                    ref statusRevision, expectedRevision + 1, expectedRevision) != expectedRevision)
                return false;
            statusCode = value;
            StateChanged?.Invoke();
            return true;
        }

        private CancellationToken AccountLifetimeToken()
        {
            lock (accountLifetimeSync) return accountLifetime.Token;
        }

        private void RotateAccountLifetime()
        {
            CancellationTokenSource previous;
            Interlocked.Increment(ref accountLifetimeGeneration);
            lock (accountLifetimeSync)
            {
                previous = accountLifetime;
                accountLifetime = new CancellationTokenSource();
                retiredAccountLifetimes.Add(previous);
            }
            previous.Cancel();
        }

        private HaivenIdentity RequireIdentity()
        {
            ThrowIfDestroyed();
            return identity
                ?? throw new InvalidOperationException("Configure the identity modal before using it.");
        }

        private void ThrowIfDestroyed()
        {
            if (Volatile.Read(ref destroyed) != 0)
                throw new ObjectDisposedException(nameof(UnityIdentityModal));
        }

        private void EnterOperation()
        {
            ThrowIfDestroyed();
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new InvalidOperationException("Another identity operation is already running.");
            try
            {
                StateChanged?.Invoke();
            }
            catch
            {
                // Callers enter their try/finally only after this returns, so
                // ExitOperation would never run. Release the operation slot here
                // or one throwing subscriber wedges every later operation.
                Volatile.Write(ref busy, 0);
                throw;
            }
        }

        private void ExitOperation()
        {
            Volatile.Write(ref busy, 0);
            if (Volatile.Read(ref destroyed) == 0) StateChanged?.Invoke();
        }

        private void OnDestroy()
        {
            if (Interlocked.Exchange(ref destroyed, 1) != 0) return;
            Interlocked.Increment(ref configurationGeneration);
            lifetime.Cancel();
            RotateAccountLifetime();
            Unsubscribe();
            mainThread?.Dispose();
            mainThread = null;
            lock (accountLifetimeSync)
            {
                accountLifetime.Cancel();
                accountLifetime.Dispose();
                foreach (var retired in retiredAccountLifetimes) retired.Dispose();
                retiredAccountLifetimes.Clear();
            }
            lifetime.Dispose();
        }
    }
}
