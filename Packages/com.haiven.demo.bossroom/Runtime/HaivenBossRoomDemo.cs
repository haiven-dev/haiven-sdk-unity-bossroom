#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Boots the demo into whatever scene Boss Room loads first, without editing
    /// any Boss Room scene or prefab: a persistent object opens the socket and
    /// drives HaivenDemoClient. A host-supplied view prefab can own presentation;
    /// otherwise the package builds the original uGUI canvas in code.
    /// </summary>
    public sealed class HaivenBossRoomDemo : MonoBehaviour
    {
        /// <summary>
        /// Resources path for an optional host-owned prefab containing a
        /// <see cref="HaivenBossRoomView"/> implementation.
        /// </summary>
        public const string ViewResourcePath = "Haiven/HaivenBossRoomView";

        private const int MaxLines = 12;

        /// <summary>
        /// How often a running game re-reads the public Message of the Day. The
        /// backend sends a signed-in socket the current message only when it
        /// connects, and nothing when an admin posts or ends one, so without this
        /// a new message waited for the next Play. GET /v0/motd allows 60 reads a
        /// minute per IP, which leaves room for several local instances.
        /// </summary>
        internal static readonly TimeSpan MotdRefreshInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long quitting waits for queued analytics to send. A desktop player
        /// or the Editor delivers on background threads, so the main thread can
        /// wait for them; WebGL delivers from coroutines on the main thread, so a
        /// wait there could only stall, and the queue is dropped instead.
        /// </summary>
        internal static readonly TimeSpan AnalyticsShutdownTimeout =
#if UNITY_WEBGL && !UNITY_EDITOR
            TimeSpan.Zero;
#else
            DemoAnalytics.ShutdownFlushTimeout;
#endif

        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private DemoSettings? settings;
        private BearerRestTransport? publicRest;
        private SystemNetSocket? socket;
        private BearerRestTransport? rest;
        private HaivenDemoClient? client;
        private DemoMotdController? motd;
        private DemoAnalytics? analytics;
        private string connectionStatus = "";
        private string renderedAnalyticsCounts = "";
        private HaivenBossRoomView? view;
        private HaivenBossRoomUGuiView? offerView;
        private IHaivenChatView? chatView;
        private IHaivenRewardsView? rewardsView;
        private IHaivenServiceStatusView? statusView;
        private IHaivenRewardActionView? rewardActionView;
        private DemoSignIn? signIn;
        private IHandoffBrowser handoffBrowser = new UnityHandoffBrowser();
        private readonly IUnityExternalLinkBrowser externalLinks = new UnityExternalLinkBrowser();
        private string missingSettings = "";
        private Func<string, string?> readSetting = System.Environment.GetEnvironmentVariable;
        private string projectConfigurationJson = "";

        /// <summary>The running demo, for the lobby; null before boot and after it is destroyed.</summary>
        internal static HaivenBossRoomDemo? Current { get; private set; }

        internal DemoSettings? Settings => settings;
        private DateTime nextProjectConfigurationCheckUtc = DateTime.MinValue;
        private bool destroyed;
        private readonly DemoVictoryReward victory = new DemoVictoryReward();
        private DateTime postGameWatchUntilUtc = DateTime.MinValue;
        private DateTime nextPostGameReadUtc = DateTime.MinValue;
        // MaxValue until the first public MOTD load settles; focus and the timer
        // only refresh what that load started.
        private DateTime nextMotdRefreshUtc = DateTime.MaxValue;
        private bool motdRefreshing;

        /// <summary>The running client, once the socket is opened; null while the demo is off.</summary>
        public HaivenDemoClient? Client => client;

        /// <summary>Opens or closes chat from game-owned UI.</summary>
        public void SetChatOpen(bool isOpen) => view?.SetChatOpen(isOpen);

        /// <summary>Opens or closes live rewards from game-owned UI.</summary>
        public void SetRewardsOpen(bool isOpen) => view?.SetRewardsOpen(isOpen);

        /// <summary>The sign-in state, once settings are loaded; null while the demo is unconfigured.</summary>
        public DemoSignIn? SignInState => signIn;

        /// <summary>
        /// Opens Haiven's sign-in page. Defaults to the system browser
        /// (<see cref="UnityHandoffBrowser"/>); a host with an embedded browser, or
        /// a test, can replace it before the player signs in.
        /// </summary>
        public IHandoffBrowser HandoffBrowser
        {
            get => handoffBrowser;
            set
            {
                handoffBrowser = value ?? throw new ArgumentNullException(nameof(value));
                if (signIn != null) signIn.Browser = value;
            }
        }

        /// <summary>
        /// Signs in with Haiven from game-owned UI, or retries after a failure or
        /// expiry. Ignored while signed in, while a sign-in is pending, and when the
        /// HAIVEN_DEMO_ACCESS_TOKEN shortcut is in use.
        /// </summary>
        public async void SignIn()
        {
            if (signIn == null || destroyed || !signIn.Current.CanStart) return;
            Disconnect();
            if (await signIn.SignInAsync(lifetime.Token) && !destroyed) await ConnectAsync();
        }

        /// <summary>
        /// Chat that opened before sign-in existed (including chat that starts open)
        /// uses the same browser handoff as <see cref="SignIn"/>.
        /// </summary>
        private void StartChatOAuthIfOpen()
        {
            if (destroyed || signIn == null || view is not HaivenBossRoomUGuiView chat || !chat.IsChatOpen)
                return;
            if (!DemoChatSignIn.StartsBrowserOnChatOpen(signIn.Current.State, signIn.Current.CanStart))
                return;
            SignIn();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            var projectConfigurationAsset = Resources.Load<TextAsset>(
                DemoSettings.ProjectConfigurationResourcePath);
            var projectConfigurationJson = projectConfigurationAsset?.text ?? "";
            var projectConfiguration = ParseProjectConfiguration(projectConfigurationJson);
            var dotEnv = DemoSettings.ReadDotEnv(Path.Combine(projectRoot, ".env"));
            var readRuntimeOrBuildSetting = HaivenDemoBuildConfiguration.Reader(
                System.Environment.GetEnvironmentVariable);
            string? ReadSetting(string name) =>
                DemoSettings.ReadSetting(
                    name,
                    readRuntimeOrBuildSetting,
                    projectConfiguration,
                    dotEnv);
            var loaded = DemoSettings.FromEnvironment(ReadSetting, out var missing);
            var host = new GameObject("Haiven Demo");
            DontDestroyOnLoad(host);
            var demo = host.AddComponent<HaivenBossRoomDemo>();
            demo.settings = loaded;
            Current = demo;
            demo.readSetting = ReadSetting;
            demo.missingSettings = missing;
            demo.projectConfigurationJson = projectConfigurationJson;
            if (loaded == null)
            {
                Debug.LogWarning(
                    "Haiven Boss Room needs configuration: run haiven-bossroom dev, or set "
                    + missing + ".");
            }
        }

        private async void Start()
        {
            view = CreateView();
            chatView = view;
            rewardsView = view;
            statusView = view;
            rewardActionView = view;
            offerView = view as HaivenBossRoomUGuiView;
            chatView.Submitted += Submit;
            rewardActionView.RewardRequested += ClaimReward;
            if (offerView != null) offerView.RewardOfferRequested += ClaimRewardOffer;
            view.SignInRequested += SignIn;
            view.MotdDismissed += DismissMotd;
            view.MotdActionRequested += OpenMotdAction;
            SceneManager.sceneLoaded += WatchPostGame;
            view.ConfigureDrawers(
                settings?.ShowChatHandle != false,
                settings?.ShowRewardsHandle != false,
                settings?.ChatStartsOpen == true,
                settings?.RewardsStartOpen == true);
            rewardsView.ShowRewards("Haiven items: none yet");
            statusView.ShowModerationStatus("READY");
            rewardActionView.ShowRewardStatus("WAITING FOR SIGN-IN", false);
            if (settings == null)
            {
                view.ShowMotd(DemoMotdPresentation.Failure());
                statusView.ShowConnectionStatus("CONFIG REQUIRED");
                Show("Run haiven-bossroom dev, or set " + missingSettings + ".");
                return;
            }

            var environmentToken = readSetting(DemoSettings.AccessTokenVariable) ?? "";
            signIn = new DemoSignIn(
                settings,
                environmentToken,
                handoffBrowser,
                new UnityHandoffHttpTransport(),
                mainThread.Enqueue);
            signIn.Changed += PresentSignIn;
            view.ShowSignIn(signIn.Current);
            publicRest = BearerRestTransport.Anonymous(settings.BaseUrl, settings.AppId);
            // Analytics is opt-in (HAIVEN_DEMO_ANALYTICS). It posts anonymously: the
            // body never names a player, and a lapsed account token cannot refuse a
            // batch.
            analytics = DemoAnalytics.CreateIfOptedIn(
                new RestApi(publicRest),
                settings,
                CreateAnalyticsScheduler);
            motd = new DemoMotdController(new RestApi(publicRest), settings.IntegrationId);
            motd.Changed += PresentMotd;
            // Chat that is already open starts the same browser handoff as the
            // sign-in control. The public MOTD load stays in flight beside it.
            StartChatOAuthIfOpen();
            if (!await LoadPublicMotdAsync(motd, lifetime.Token) || destroyed) return;
            nextMotdRefreshUtc = DateTime.UtcNow + MotdRefreshInterval;

            // A sign-in that finished while the MOTD loaded has already connected.
            if (client != null) return;
            if (signIn.Current.State == DemoSignInState.SignedIn)
            {
                await ConnectAsync();
                return;
            }
            PresentSignIn(signIn.Current);
        }

        /// <summary>
        /// A dropped socket is opened again while the player stays signed in,
        /// so a Haiven room can resume within its 30-second window (ADR 0036
        /// point 7). The waits are 1, 2, 3, 4 and 5 seconds.
        /// </summary>
        internal const int ReconnectAttempts = 5;

        // 1008 credential_revoked is a sign-out, not a drop: the room must not resume.
        private const int CredentialRevoked = 1008;

        private async Task ReconnectAfterDropAsync(HaivenDemoClient dropped, int code)
        {
            if (code == CredentialRevoked || destroyed || client != dropped) return;
            if (signIn == null || signIn.Current.State != DemoSignInState.SignedIn) return;
            for (var attempt = 1; attempt <= ReconnectAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
                // Something else replaced or dropped the client meanwhile: a sign-out or a new sign-in.
                if (destroyed || client != dropped || signIn.Current.State != DemoSignInState.SignedIn) return;
                Debug.Log($"[haiven-demo] the Haiven socket closed ({code}); reconnecting, attempt {attempt}");
                Disconnect();
                if (await ConnectAsync()) return;
                if (client == null) return;
                dropped = client;
            }
        }

        private async Task<bool> ConnectAsync()
        {
            if (settings == null || signIn == null || motd == null || destroyed) return false;
            var current = signIn;
            rest = new BearerRestTransport(settings.BaseUrl, settings.AppId, () => current.AccessToken);
            socket = new SystemNetSocket();
            client = new HaivenDemoClient(
                settings,
                socket,
                rest,
                () => current.AccessToken,
                mainThread.Enqueue,
                motd);
            client.Log.Changed += Redraw;
            client.ItemsChanged += Redraw;
            client.PresentationChanged += Redraw;
            client.OperationFailed += ReportOperationFailed;
            var created = client;
            client.SocketClosed += code => _ = ReconnectAfterDropAsync(created, code);
            Redraw();
            var connecting = client;
            analytics?.Connecting();
            try
            {
                ShowConnectionStatus("CONNECTING · SOCKET");
                await socket.ConnectAsync(new Uri(settings.RealtimeUrl));
                if (destroyed || client != connecting) return false;
                analytics?.SessionStarted();
                ShowConnectionStatus("CONNECTED · AUTHENTICATING");
                client.Start();
                ClaimVictoryAfterSignIn(connecting);
                return true;
            }
            catch (Exception error)
            {
                if (!destroyed && client == connecting)
                {
                    analytics?.ConnectFailed();
                    ReportOperationFailed(DemoOperations.RealtimeConnect, error);
                    ShowConnectionStatus("CONNECTION FAILED");
                    Show(DemoChatLog.ConnectionUnavailable);
                }
                return false;
            }
        }

        /// <summary>Drops the signed-in session: the client, its socket, and its bearer transport.</summary>
        private void Disconnect()
        {
            analytics?.SessionEnded();
            if (client != null)
            {
                client.Log.Changed -= Redraw;
                client.ItemsChanged -= Redraw;
                client.PresentationChanged -= Redraw;
                client.OperationFailed -= ReportOperationFailed;
                // Close first, so the server ends the connection now rather than on its timeout.
                if (client.Session.State != HaivenSessionState.Closed) client.Session.Close();
                client.Dispose();
                client = null;
            }
            socket?.Dispose();
            socket = null;
            rest?.Dispose();
            rest = null;
            rewardActionView?.ShowRewardStatus("WAITING FOR SIGN-IN", false);
        }

        private void PresentSignIn(DemoSignInPresentation presentation)
        {
            view?.ShowSignIn(presentation);
            // Once a client exists it owns the connection line; sign-in owns it before.
            if (client != null) return;
            ShowConnectionStatus(presentation.Status);
            Show(presentation.Message);
        }

        private void Update()
        {
            while (mainThread.TryDequeue(out var action)) action();

            if (ProjectConfigurationChanged())
            {
                Debug.Log("Haiven Boss Room project configuration changed; reloading the demo.");
                EndAnalytics();
                Destroy(gameObject);
                Boot();
                return;
            }

            // A handoff credential is never refreshed in the background (SDK-HANDOFF-9):
            // when it lapses, the session ends and the player signs in again.
            if (signIn != null && signIn.CheckExpiry()) Disconnect();

            // Delivery runs off the main thread; show its counts once they move.
            if (analytics != null && connectionStatus.Length > 0
                && analytics.CountsText != renderedAnalyticsCounts)
                ShowConnectionStatus(connectionStatus);
            ReadPostGame();
            RefreshMotdWhenDue();

            // Boss Room uses the legacy Input Manager, so UnityEngine.Input is available.
            if (settings?.EnableChatHotkey != false
                && chatView != null
                && !chatView.IsInputFocused
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                SetChatOpen(true);
                chatView.FocusInput();
            }
        }

        private static DemoProjectConfiguration? ParseProjectConfiguration(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var configuration = JsonUtility.FromJson<DemoProjectConfiguration>(json);
                if (configuration != null && configuration.schemaVersion == 1) return configuration;
                Debug.LogWarning("Haiven Boss Room project configuration has an unsupported schema.");
            }
            catch (ArgumentException exception)
            {
                Debug.LogWarning("Haiven Boss Room project configuration is invalid: " + exception.Message);
            }
            return null;
        }

        private bool ProjectConfigurationChanged()
        {
            var now = DateTime.UtcNow;
            if (now < nextProjectConfigurationCheckUtc) return false;
            nextProjectConfigurationCheckUtc = now.AddMilliseconds(500);
            var asset = Resources.Load<TextAsset>(DemoSettings.ProjectConfigurationResourcePath);
            return !string.Equals(asset?.text ?? "", projectConfigurationJson, StringComparison.Ordinal);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Returning to the game is when a player looks for news: re-read now.
            if (hasFocus && nextMotdRefreshUtc != DateTime.MaxValue)
                nextMotdRefreshUtc = DateTime.MinValue;
        }

        private void RefreshMotdWhenDue()
        {
            if (motd == null || motdRefreshing || destroyed) return;
            if (DateTime.UtcNow < nextMotdRefreshUtc) return;
            RefreshMotd(motd);
        }

        private async void RefreshMotd(DemoMotdController controller)
        {
            motdRefreshing = true;
            try
            {
                await controller.RefreshPublicAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (destroyed || lifetime.IsCancellationRequested)
            {
                // The host is closing.
            }
            catch (ObjectDisposedException) when (destroyed)
            {
                // The controller was disposed with the host.
            }
            finally
            {
                motdRefreshing = false;
                nextMotdRefreshUtc = DateTime.UtcNow + MotdRefreshInterval;
            }
        }

        /// <summary>
        /// Quitting the player and leaving Play mode in the Editor arrive here
        /// before any OnDestroy. A teardown that skips it (the batchmode test
        /// runner's exit, a destroyed host) ends the session in OnDestroy.
        /// </summary>
        private void OnApplicationQuit() => EndAnalytics();

        private IAnalyticsScheduler? CreateAnalyticsScheduler()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL has no thread pool: coroutines on this object deliver.
            return gameObject.AddComponent<UnityAnalyticsScheduler>();
#else
            // Desktop and the Editor deliver on the thread pool (the batcher's
            // default scheduler). A component scheduler is cancelled by Unity's
            // teardown, in no guaranteed order relative to this OnDestroy, which
            // would drop the queue before the quit flush; EndAnalytics disposes
            // this one, so nothing outlives the demo.
            return null;
#endif
        }

        /// <summary>
        /// Sends <c>session.end</c> and <c>playtime</c> for a session still open,
        /// then flushes the queue within <see cref="AnalyticsShutdownTimeout"/>
        /// and disposes it. Runs once; nothing when opted out.
        /// </summary>
        private void EndAnalytics()
        {
            var ending = analytics;
            analytics = null;
            ending?.Shutdown(AnalyticsShutdownTimeout);
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            destroyed = true;
            lifetime.Cancel();
            SceneManager.sceneLoaded -= WatchPostGame;
            if (view != null)
            {
                view.MotdDismissed -= DismissMotd;
                view.MotdActionRequested -= OpenMotdAction;
            }
            if (chatView != null) chatView.Submitted -= Submit;
            if (rewardActionView != null) rewardActionView.RewardRequested -= ClaimReward;
            if (offerView != null) offerView.RewardOfferRequested -= ClaimRewardOffer;
            if (view != null) view.SignInRequested -= SignIn;
            if (signIn != null) signIn.Changed -= PresentSignIn;
            if (motd != null) motd.Changed -= PresentMotd;
            if (client != null) client.PresentationChanged -= Redraw;
            client?.Dispose();
            motd?.Dispose();
            EndAnalytics();
            socket?.Dispose();
            rest?.Dispose();
            publicRest?.Dispose();
            lifetime.Dispose();
        }

        internal static async Task<bool> LoadPublicMotdAsync(
            DemoMotdController controller,
            CancellationToken cancellationToken)
        {
            try
            {
                await controller.LoadPublicAsync(cancellationToken);
                return !cancellationToken.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        private void Submit(string text)
        {
            client?.Send(text);
            chatView?.ClearInput();
        }

        private void ClaimReward()
        {
            if (settings == null) return;
            ClaimRewardOffer(settings.RewardRef);
        }

        private async void ClaimRewardOffer(string rewardRef)
        {
            if (destroyed || string.IsNullOrEmpty(rewardRef)) return;
            if (client == null)
            {
                SignIn();
                return;
            }
            try
            {
                await client.ClaimRewardAsync(rewardRef, lifetime.Token);
            }
            catch (OperationCanceledException) when (destroyed || lifetime.IsCancellationRequested)
            {
                // The host is closing; no stale completion should update its view.
            }
        }

        /// <summary>The HAIVEN connection-status line, with the event counts beside it once opted in.</summary>
        private void ShowConnectionStatus(string status)
        {
            connectionStatus = status;
            if (analytics == null)
            {
                statusView?.ShowConnectionStatus(status);
                return;
            }
            renderedAnalyticsCounts = analytics.CountsText;
            statusView?.ShowConnectionStatus(status + " · " + renderedAnalyticsCounts);
        }

        private void ReportOperationFailed(string operation, Exception error) =>
            _ = analytics?.ReportErrorAsync(operation, error);
        /// <summary>Boss Room's post-game scene loaded: read its result for a few seconds.</summary>
        private void WatchPostGame(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != BossRoomPostGame.SceneName) return;
            postGameWatchUntilUtc = DateTime.UtcNow.AddSeconds(BossRoomPostGame.WatchSeconds);
            nextPostGameReadUtc = DateTime.MinValue;
        }

        private void ReadPostGame()
        {
            var now = DateTime.UtcNow;
            if (now > postGameWatchUntilUtc || now < nextPostGameReadUtc) return;
            nextPostGameReadUtc = now.AddSeconds(BossRoomPostGame.PollSeconds);
            var won = DemoVictoryReward.IsVictory(BossRoomPostGame.ReadWinState());
            if (won == null) return;
            postGameWatchUntilUtc = DateTime.MinValue;
            if (won == true) ClaimVictoryReward();
        }

        /// <summary>
        /// The boss was defeated: open live rewards on the existing claim row and
        /// inventory, then claim the configured reward once. Signed out, the
        /// drawer shows the existing sign-in gate and the claim waits for it.
        /// </summary>
        private async void ClaimVictoryReward()
        {
            if (destroyed || settings == null) return;
            if (settings.ShowRewardsHandle) SetRewardsOpen(true);
            try
            {
                var result = await victory.BossDefeatedAsync(client, lifetime.Token);
                if (result == DemoVictoryClaim.SignInRequired && !destroyed)
                    Show("Boss defeated. Sign in to claim your reward.");
            }
            catch (OperationCanceledException) when (destroyed || lifetime.IsCancellationRequested)
            {
                // The host is closing; no stale completion should update its view.
            }
        }

        private async void ClaimVictoryAfterSignIn(HaivenDemoClient connected)
        {
            try
            {
                await victory.SignedInAsync(connected, lifetime.Token);
            }
            catch (OperationCanceledException) when (destroyed || lifetime.IsCancellationRequested)
            {
                // The host is closing; no stale completion should update its view.
            }
        }

        private void Show(string line)
        {
            chatView?.ShowLog(line);
        }

        private void PresentMotd(DemoMotdPresentation presentation) =>
            mainThread.Enqueue(() => view?.ShowMotd(presentation));

        private void DismissMotd(DemoMotdPresentation rendered) => motd?.Dismiss(rendered);

        private void OpenMotdAction(DemoMotdPresentation rendered)
        {
            if (motd is not null && motd.TryGetAction(rendered, out var url) && url is not null)
                externalLinks.Open(url);
        }

        private void Redraw()
        {
            if (client == null) return;
            Render(chatView, rewardsView, client.Log.Lines, client.Items);
            ShowConnectionStatus(client.ConnectionStatus);
            statusView?.ShowModerationStatus(client.ModerationStatus);
            rewardActionView?.ShowRewardStatus(client.RewardStatus, client.CanClaimReward);
            view?.ShowRewardOffers(client.RewardOffers);
        }

        /// <summary>
        /// Backward-compatible adapter for callers that still pass the combined
        /// Boss Room view.
        /// </summary>
        internal static void Render(
            HaivenBossRoomView? target,
            IReadOnlyList<string> lines,
            IReadOnlyList<string> items)
            => Render(target, target, lines, items);

        internal static void Render(
            IHaivenChatView? chat,
            IHaivenRewardsView? rewards,
            IReadOnlyList<string> lines,
            IReadOnlyList<string> items)
        {
            if (chat == null || rewards == null) return;
            var start = Math.Max(0, lines.Count - MaxLines);
            chat.ShowLog(string.Join("\n", System.Linq.Enumerable.Skip(lines, start)));
            rewards.ShowRewards(items.Count == 0
                ? "Haiven items: none yet"
                : "Haiven items:\n- " + string.Join("\n- ", items));
        }

        private HaivenBossRoomView CreateView()
            => CreateView(transform, Resources.Load<GameObject>(ViewResourcePath));

        internal static HaivenBossRoomView CreateView(Transform parent, GameObject? prefab)
        {
            if (prefab != null)
            {
                var instance = Instantiate(prefab, parent, false);
                var supplied = instance.GetComponentInChildren<HaivenBossRoomView>(true);
                var problem = "must contain a HaivenBossRoomView";
                if (supplied != null)
                {
                    if (supplied.ValidateReadiness(out problem)) return supplied;
                }
                Debug.LogError("Haiven Boss Room view prefab is not ready: " + problem +
                    "; using the code-built fallback.");
                if (Application.isPlaying) Destroy(instance);
                else DestroyImmediate(instance);
            }

            return HaivenBossRoomUGuiView.CreateFallback(parent);
        }
    }
}
