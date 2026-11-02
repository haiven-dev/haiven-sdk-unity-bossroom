#nullable enable
using System;
using System.Collections.Generic;
using Haiven.Sdk;
using UnityEngine;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Designer-bindable uGUI view. Assign the serialized controls on a
    /// prefab, or let <see cref="CreateFallback"/> build the original plain UI.
    /// </summary>
    public sealed class HaivenBossRoomUGuiView : HaivenBossRoomView
    {
        private static readonly Vector2 ChatPanelOffset = new Vector2(24, 24);
        // 16px body text on a 680-wide panel keeps a sender name and message on
        // one line. The right edge stays clear of Boss Room's bottom-right
        // action bar on 16:9 and 16:10; only a 4:3 frame would meet it.
        private static readonly Vector2 ChatPanelSize = new Vector2(680, 380);
        private const int ChatBodyFontSize = 16;
        private static readonly Vector2 RewardsPanelOffset = new Vector2(0, -24);
        private static readonly Vector2 RewardsPanelSize = new Vector2(520, 520);
        private static readonly Vector2 MotdPanelOffset = new Vector2(0, -24);
        private static readonly Vector2 MotdPanelSize = new Vector2(520, 144);
        // Boss Room's stock 1920x1080 rail uses 100x100 hit targets at
        // (-50, -32) and (-50, -160): a 128-pixel cadence and a 28-pixel gap.
        // This overlay uses a proportional 1280x720 reference resolution, so
        // scaling every stock measurement by 2/3 keeps both added controls on
        // exactly the same visual grid.
        private const float BossRoomCanvasScale = 2f / 3f;
        private const float BossRoomCanvasMatch = 0.506f;
        private const float BossRoomRailX = -50f * BossRoomCanvasScale;
        private const float BossRoomRailSize = 100f * BossRoomCanvasScale;
        private static readonly Vector2 RailHandleSize =
            new Vector2(BossRoomRailSize, BossRoomRailSize);
        // The stock settings button ends at y=-520/3 on this proportional
        // canvas. Enabled Haiven handles pack immediately below that edge.
        private static readonly Vector2 FirstRailHandleOffset =
            new Vector2(BossRoomRailX, -520f / 3f);
        // Code-native stand-in for ui_dialog's rim. Thick enough to read on the
        // 1280 reference canvas without crossing the 12px content inset.
        private const float DialogBorderThickness = 6f;
        private const float ButtonBorderThickness = 3f;

        [SerializeField] private Text? log;
        [SerializeField] private Text? inventory;
        [SerializeField] private InputField? input;
        [SerializeField] private Text? connectionStatus;
        [SerializeField] private Text? moderationStatus;
        [SerializeField] private Text? rewardStatus;
        [SerializeField] private Button? rewardAction;
        private Text? rewardClaimedLabel;
        [SerializeField] private GameObject? chatDrawer;
        [SerializeField] private GameObject? chatConversation;
        [SerializeField] private GameObject? signInDialog;
        [SerializeField] private Text? signInTitle;
        [SerializeField] private Text? signInMessage;
        [SerializeField] private GameObject? rewardsDrawer;
        [SerializeField] private Button? chatHandle;
        [SerializeField] private Text? chatHandleLabel;
        [SerializeField] private Button? rewardsHandle;
        [SerializeField] private Text? rewardsHandleLabel;
        [SerializeField] private GameObject? rewardsUnclaimedIndicator;
        [SerializeField] private Button? chatClose;
        [SerializeField] private Button? rewardsClose;
        [SerializeField] private GameObject? motdRoot;
        [SerializeField] private Text? motdTitle;
        [SerializeField] private Text? motdBody;
        [SerializeField] private Button? motdAction;
        [SerializeField] private Button? motdDismiss;
        [SerializeField] private Button? motdCollapse;
        [SerializeField] private Button? motdHandle;
        [SerializeField] private GameObject? motdUnreadIndicator;
        // Optional: a prefab without a sign-in control still validates, and its host
        // can drive HaivenBossRoomDemo.SignIn from its own UI.
        [SerializeField] private Button? signInAction;
        [SerializeField] private Text? signInLabel;
        // Code-built rewards surface. A prefab without these still shows its rows.
        private GameObject? rewardsContent;
        private GameObject? rewardsAccountGate;
        private Button? rewardsSignInAction;
        private Text? rewardsSignInLabel;
        private Text? rewardsSignInTitle;
        private Text? rewardsSignInMessage;
        [SerializeField] private HaivenBossRoomMenuConfiguration menu = new HaivenBossRoomMenuConfiguration();
        private bool bound;
        private bool drawersBound;
        private bool motdBound;
        private readonly HaivenDrawerState chatState = new HaivenDrawerState();
        private readonly HaivenDrawerState rewardsState = new HaivenDrawerState();
        private bool rewardsHaveUnclaimed;
        private DemoMotdPresentation? renderedMotd;
        private string? lastPresentedMotdId;
        private bool motdExpanded;
        private bool motdUnread;
        // Frames left to make sure a handle click ends with the message input
        // focused: a later drawer re-apply or a same-frame selection change would
        // otherwise leave it deselected, and the first typing would be lost.
        private int chatFocusFramesLeft;
        private const int ChatFocusFrames = 3;
        private HaivenBossRoomHostSurface? hostSurface;
        private string renderedLog = "";
        private string renderedInventory = "Haiven items: none yet";
        private RectTransform? lootGrid;
        private ScrollRect? lootScroll;
        private string? lootGridSummary;
        private readonly List<GameObject> lootCards = new List<GameObject>();
        private const float LootCardHeight = 74f;
        private const float LootCardGap = 8f;
        private string renderedConnectionStatus = "STARTING";
        private string renderedModerationStatus = "READY";
        private string renderedRewardStatus = "WAITING FOR SIGN-IN";
        private bool renderedRewardCanClaim;
        private readonly List<RewardOfferRow> extraRewardRows = new List<RewardOfferRow>();

        /// <summary>Raised when the player claims one of the extra demo rewards.</summary>
        public event Action<string>? RewardOfferRequested;
        private DemoSignInState renderedSignInState = DemoSignInState.SignedOut;
        private bool renderedCanStart = true;

        public override bool IsInputFocused => input != null && input.isFocused;

        public override bool ValidateReadiness(out string error)
        {
            var missing = "";
            if (log == null) missing = "log";
            if (inventory == null) missing += (missing.Length == 0 ? "" : ", ") + "inventory";
            if (input == null) missing += (missing.Length == 0 ? "" : ", ") + "input";
            if (connectionStatus == null) missing += (missing.Length == 0 ? "" : ", ") + "connectionStatus";
            if (moderationStatus == null) missing += (missing.Length == 0 ? "" : ", ") + "moderationStatus";
            if (rewardStatus == null) missing += (missing.Length == 0 ? "" : ", ") + "rewardStatus";
            if (rewardAction == null) missing += (missing.Length == 0 ? "" : ", ") + "rewardAction";
            if (chatDrawer == null) missing += (missing.Length == 0 ? "" : ", ") + "chatDrawer";
            if (rewardsDrawer == null) missing += (missing.Length == 0 ? "" : ", ") + "rewardsDrawer";
            if (chatClose == null) missing += (missing.Length == 0 ? "" : ", ") + "chatClose";
            if (rewardsClose == null) missing += (missing.Length == 0 ? "" : ", ") + "rewardsClose";
            if (motdRoot == null) missing += (missing.Length == 0 ? "" : ", ") + "motdRoot";
            if (motdTitle == null) missing += (missing.Length == 0 ? "" : ", ") + "motdTitle";
            if (motdBody == null) missing += (missing.Length == 0 ? "" : ", ") + "motdBody";
            if (motdAction == null) missing += (missing.Length == 0 ? "" : ", ") + "motdAction";
            if (motdDismiss == null) missing += (missing.Length == 0 ? "" : ", ") + "motdDismiss";
            if (motdCollapse == null) missing += (missing.Length == 0 ? "" : ", ") + "motdCollapse";
            if (motdHandle == null) missing += (missing.Length == 0 ? "" : ", ") + "motdHandle";
            if (motdUnreadIndicator == null) missing += (missing.Length == 0 ? "" : ", ") + "motdUnreadIndicator";
            error = missing.Length == 0 ? "" : "missing serialized bindings: " + missing;
            return missing.Length == 0;
        }

        private void Awake()
        {
            BindInput();
            BindDrawerActions();
            BindMotdActions();
            ApplyMotdVisibility();
        }

        private void LateUpdate()
        {
            if (hostSurface != null && !hostSurface.IsAvailable)
            {
                hostSurface.Dispose();
                hostSurface = null;
                ApplyChatDrawer(chatState.Current);
                ApplyRewardsDrawer(rewardsState.Current);
            }

            if (hostSurface == null)
            {
                hostSurface = HaivenBossRoomHostSurface.TryCreate(menu);
                if (hostSurface != null)
                {
                    ApplyChatDrawer(chatState.Current);
                    ApplyRewardsDrawer(rewardsState.Current);
                }
            }

            if (hostSurface != null) hostSurface.Tick();
        }

        private void OnDestroy()
        {
            UnbindInput();
            UnbindDrawerActions();
            UnbindMotdActions();
            hostSurface?.Dispose();
        }

        private void Update()
        {
            if (chatFocusFramesLeft > 0)
            {
                chatFocusFramesLeft--;
                if (!chatState.Current.IsOpen || IsInputFocused) chatFocusFramesLeft = 0;
                else FocusInput();
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.JoystickButton1))
            {
                DismissOpenDrawers();
            }
        }

        public override void FocusInput()
        {
            SetChatOpen(true);
            if (renderedSignInState != DemoSignInState.SignedIn)
            {
                SelectDrawerControl(signInAction);
                return;
            }
            if (input == null) return;
            // Opening the drawer selects Close for controller navigation. Return is
            // also the EventSystem's Submit button, and the input module can run
            // after this in the same frame: left on Close, the Return that opened
            // chat (or the next one after a send) would press Close instead.
            if (input.gameObject.activeInHierarchy) input.Select();
            input.ActivateInputField();
        }

        public override void ClearInput()
        {
            if (input == null) return;
            input.text = "";
            input.DeactivateInputField();
        }

        public bool IsChatOpen => chatState.Current.IsOpen;

        public override void ConfigureDrawers(
            bool showChatHandle,
            bool showRewardsHandle,
            bool chatStartsOpen,
            bool rewardsStartOpen)
        {
            var opening = chatStartsOpen && !chatState.Current.IsOpen;
            if (opening && !DemoChatSignIn.ShowsJoinDialog(renderedSignInState))
                signInDialog?.SetActive(false);
            chatState.Configure(new HaivenDrawerConfiguration(showChatHandle, chatStartsOpen));
            rewardsState.Configure(new HaivenDrawerConfiguration(showRewardsHandle, rewardsStartOpen));
            ApplyChatDrawer(chatState.Current);
            ApplyRewardsDrawer(rewardsState.Current);
            if (opening) BeginChatOAuth();
        }

        public override void SetChatOpen(bool isOpen)
        {
            var opening = isOpen && !chatState.Current.IsOpen;
            // Deactivate before the drawer itself, or the join card paints for a frame.
            if (opening && !DemoChatSignIn.ShowsJoinDialog(renderedSignInState))
                signInDialog?.SetActive(false);
            chatState.SetOpen(isOpen);
            ApplyChatDrawer(chatState.Current);
            if (opening) BeginChatOAuth();
        }

        private void BeginChatOAuth()
        {
            if (!DemoChatSignIn.StartsBrowserOnChatOpen(renderedSignInState, renderedCanStart))
                return;
            signInDialog?.SetActive(false);
            RaiseSignInRequested();
        }

        private void ApplyChatDrawer(HaivenDrawerPresentation state)
        {
            if (chatHandle != null) chatHandle.gameObject.SetActive(state.ShowHandle);
            if (chatClose != null) chatClose.gameObject.SetActive(state.IsOpen);
            if (!state.IsOpen && input != null && input.isFocused) input.DeactivateInputField();
            if (chatDrawer != null)
            {
                chatDrawer.SetActive(state.IsOpen);
                if (state.IsOpen) DrawerLayerRoot(chatDrawer, chatHandle).SetAsLastSibling();
            }
            // Open and signed in, chat is for typing: select the message input, not
            // Close. Selecting Close opened chat with the input unselected until a
            // deferred focus caught up, and any later re-apply of the open drawer
            // deselected it again, so the first typing could be lost. Return still
            // cannot press Close, which is not selected while the input is shown.
            // Signed out, Close stays the controller target.
            if (state.IsOpen && ChatInputShown()) SelectChatInput();
            else SelectDrawerControl(state.IsOpen ? chatClose : chatHandle);
            ApplyHandleRailLayout(HasMotdPresentation());
        }

        private bool ChatInputShown() => input != null
            && renderedSignInState == DemoSignInState.SignedIn
            && input.gameObject.activeInHierarchy;

        private void SelectChatInput()
        {
            if (input == null) return;
            input.Select();
            input.ActivateInputField();
        }

        public override void SetRewardsOpen(bool isOpen)
        {
            if (isOpen) motdExpanded = false;
            rewardsState.SetOpen(isOpen);
            ApplyRewardsDrawer(rewardsState.Current);
        }

        private void ApplyRewardsDrawer(HaivenDrawerPresentation state)
        {
            if (rewardsHandle != null)
                rewardsHandle.gameObject.SetActive(state.ShowHandle);
            if (rewardsClose != null) rewardsClose.gameObject.SetActive(state.IsOpen);
            if (rewardsDrawer != null)
            {
                rewardsDrawer.SetActive(state.IsOpen);
                if (state.IsOpen) DrawerLayerRoot(rewardsDrawer, rewardsHandle).SetAsLastSibling();
            }
            // A closed rewards drawer hands selection back to open chat rather than
            // pulling it onto the rewards handle, which would drop the player's typing.
            if (state.IsOpen || !chatState.Current.IsOpen)
                SelectDrawerControl(state.IsOpen ? rewardsClose : rewardsHandle);
            else if (ChatInputShown()) SelectChatInput();
            else SelectDrawerControl(chatClose);
            ApplyRewardsUnclaimedIndicator(state.IsOpen && state.ShowHandle);
            ApplyMotdVisibility();
        }

        private void ApplyRewardsUnclaimedIndicator(bool drawerOpen)
        {
            if (rewardsUnclaimedIndicator == null) return;
            rewardsUnclaimedIndicator.SetActive(rewardsHaveUnclaimed && !drawerOpen);
        }

        private Transform DrawerLayerRoot(GameObject drawerObject, Button? handle)
        {
            var drawer = drawerObject.transform;
            var component = drawer.parent;
            if (component != null &&
                handle != null &&
                ((handle.transform.parent == component &&
                    (motdRoot == null || motdRoot.transform.parent != component)) ||
                 (motdRoot != null && motdRoot.transform.parent == component.parent)))
            {
                return component;
            }

            return drawer;
        }

        internal bool DismissOpenDrawers()
        {
            var dismissed = chatState.Current.IsOpen
                || rewardsState.Current.IsOpen;
            if (chatState.Current.IsOpen) SetChatOpen(false);
            if (rewardsState.Current.IsOpen) SetRewardsOpen(false);
            return dismissed;
        }

        private static void SelectDrawerControl(Button? button)
        {
            if (button == null) return;
            if (button.gameObject.activeInHierarchy) button.Select();
        }

        public override void ShowLog(string text)
        {
            renderedLog = text;
            if (log != null) log.text = text;
        }

        public override void ShowInventory(string text)
        {
            renderedInventory = text;
            if (inventory != null) inventory.text = text;
            if (lootGrid != null && lootGridSummary == text) return;
            RenderLootGrid(text);
        }

        public override void ShowConnectionStatus(string text)
        {
            renderedConnectionStatus = text;
            if (connectionStatus != null) connectionStatus.text = "HAIVEN · " + text;
        }

        public override void ShowModerationStatus(string text)
        {
            renderedModerationStatus = text;
            if (moderationStatus != null) moderationStatus.text = "MODERATION · " + text;
        }

        public override void ShowRewardStatus(string text, bool canClaim)
        {
            renderedRewardStatus = text;
            renderedRewardCanClaim = canClaim;
            if (rewardStatus != null) rewardStatus.text = "REWARD · " + text;
            if (rewardAction != null)
                ApplyClaimPresentation(rewardAction, rewardClaimedLabel, text, canClaim);
            rewardsHaveUnclaimed = canClaim;
            ApplyRewardsUnclaimedIndicator(rewardsState.Current.IsOpen);
            if (text != "WAITING FOR SIGN-IN") return;
            foreach (var row in extraRewardRows)
            {
                row.RewardRef = "";
                row.Status.text = row.Title + " · WAITING FOR SIGN-IN";
                ApplyClaimPresentation(row.Button, row.Claimed, text, false);
            }
        }

        public override void ShowRewardOffers(IReadOnlyList<DemoRewardOffer> offers)
        {
            if (offers.Count == 0) return;
            EnsureExtraRewardRows();
            EnsurePrimaryClaimedLabel();
            var primary = offers[0];
            renderedRewardStatus = primary.Status;
            renderedRewardCanClaim = primary.CanClaim;
            rewardsHaveUnclaimed = primary.CanClaim;
            if (rewardStatus != null) rewardStatus.text = primary.Title + " · " + primary.Status;
            if (rewardAction != null)
                ApplyClaimPresentation(rewardAction, rewardClaimedLabel, primary.Status, primary.CanClaim);
            for (var i = 0; i < extraRewardRows.Count; i++)
            {
                var row = extraRewardRows[i];
                var visible = i + 1 < offers.Count;
                row.Status.gameObject.SetActive(visible);
                if (!visible)
                {
                    row.Button.gameObject.SetActive(false);
                    row.Claimed.gameObject.SetActive(false);
                    continue;
                }
                var offer = offers[i + 1];
                row.RewardRef = offer.RewardRef;
                row.Title = offer.Title;
                row.Status.text = offer.Title + " · " + offer.Status;
                ApplyClaimPresentation(row.Button, row.Claimed, offer.Status, offer.CanClaim);
                if (offer.CanClaim) rewardsHaveUnclaimed = true;
            }
            ApplyRewardsUnclaimedIndicator(rewardsState.Current.IsOpen);
        }

        /// <summary>
        /// A claimed reward has no button. The same slot shows the text Claimed.
        /// Waiting for sign-in hides this row behind the rewards account gate.
        /// Any other state keeps the Claim reward button.
        /// </summary>
        private static void ApplyClaimPresentation(Button button, Text? claimed, string status, bool canClaim)
        {
            var claimedState = status == "CLAIMED" || status == "ALREADY CLAIMED";
            button.gameObject.SetActive(!claimedState);
            if (claimed != null) claimed.gameObject.SetActive(claimedState);
            if (claimedState) return;
            var caption = button.GetComponentInChildren<Text>(true);
            if (caption != null) caption.text = "Claim reward";
            button.interactable = canClaim || status == "WAITING FOR SIGN-IN";
        }

        private void EnsurePrimaryClaimedLabel()
        {
            if (rewardClaimedLabel != null || rewardAction == null) return;
            var parent = rewardAction.transform.parent as RectTransform;
            var slot = rewardAction.GetComponent<RectTransform>();
            var caption = rewardAction.GetComponentInChildren<Text>(true);
            if (parent == null || slot == null || caption == null || caption.font == null) return;
            rewardClaimedLabel = ClaimedLabel(parent, caption.font, "Haiven Claimed Label", slot);
        }

        internal void SubmitFromHostInput(string text)
        {
            if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;
            RaiseSubmitted(text);
        }

        internal void RequestRewardFromHost() => RaiseRewardRequested();

        internal bool TryAttachNativeHost()
        {
            if (hostSurface != null) return true;
            hostSurface = HaivenBossRoomHostSurface.TryCreate(menu);
            if (hostSurface == null) return false;
            ApplyChatDrawer(chatState.Current);
            ApplyRewardsDrawer(rewardsState.Current);
            return true;
        }

        public override void ShowSignIn(DemoSignInPresentation presentation)
        {
            if (presentation is null) throw new System.ArgumentNullException(nameof(presentation));
            var becameSignedIn = presentation.State == DemoSignInState.SignedIn
                && renderedSignInState != DemoSignInState.SignedIn;
            renderedSignInState = presentation.State;
            renderedCanStart = presentation.CanStart;
            // The browser is the next screen. Do not swap a visible gate into the
            // waiting copy, or the click flashes an intermediate dialog.
            if (presentation.State == DemoSignInState.SigningIn)
            {
                if (signInDialog != null) signInDialog.SetActive(false);
                return;
            }
            var signedIn = presentation.State == DemoSignInState.SignedIn;
            if (chatConversation != null) chatConversation.SetActive(signedIn);
            if (signInDialog != null)
                signInDialog.SetActive(DemoChatSignIn.ShowsJoinDialog(presentation.State));
            if (signInTitle != null) signInTitle.text = presentation.Status;
            if (signInMessage != null) signInMessage.text = presentation.Message;
            if (signInAction != null)
            {
                signInAction.gameObject.SetActive(!signedIn && presentation.ActionLabel is not null);
                signInAction.interactable = !signedIn && presentation.CanStart;
            }
            if (signInLabel != null) signInLabel.text = presentation.ActionLabel ?? "";
            if (rewardsSignInTitle != null)
            {
                rewardsSignInTitle.text = presentation.State == DemoSignInState.SignedOut
                    ? "CLAIM HAIVEN REWARDS"
                    : presentation.Status;
            }
            if (rewardsSignInMessage != null)
            {
                rewardsSignInMessage.text = presentation.State == DemoSignInState.SignedOut
                    ? "Sign in or register with Haiven to see your rewards. A reward you already claimed stays on your account."
                    : presentation.Message;
            }
            if (rewardsSignInAction != null)
            {
                rewardsSignInAction.gameObject.SetActive(!signedIn && presentation.ActionLabel is not null);
                rewardsSignInAction.interactable = !signedIn && presentation.CanStart;
            }
            if (rewardsSignInLabel != null) rewardsSignInLabel.text = presentation.ActionLabel ?? "";
            ApplyRewardsAccountGate();
            // The first chat open of a session usually happens signed out: it starts
            // the browser sign-in and, with the input still hidden, selects Close. The
            // sign-in finishes after the open's focus frames have run out, so without
            // this the input appeared unselected and typing went to the game. Chat was
            // opened to type, so hand it the input now, unless rewards is in front.
            if (becameSignedIn && chatState.Current.IsOpen && !rewardsState.Current.IsOpen
                && ChatInputShown())
                SelectChatInput();
        }

        /// <summary>
        /// Reward names and inventory stay hidden until sign-in. The server is
        /// the only source of an existing claim, so a signed-out rewards drawer
        /// keeps its account gate. Chat does not: opening it starts the browser.
        /// </summary>
        private void ApplyRewardsAccountGate()
        {
            var signedIn = renderedSignInState == DemoSignInState.SignedIn;
            if (rewardsContent != null) rewardsContent.SetActive(signedIn);
            if (rewardsAccountGate != null) rewardsAccountGate.SetActive(!signedIn);
        }

        public override void ShowMotd(DemoMotdPresentation presentation)
        {
            if (presentation is null) throw new System.ArgumentNullException(nameof(presentation));
            if (motdRoot == null || motdTitle == null || motdBody == null
                || motdAction == null || motdDismiss == null) return;
            renderedMotd = presentation;
            motdTitle.supportRichText = false;
            motdBody.supportRichText = false;
            var present = presentation.Status == DemoMotdPresentation.PresentState;
            var freshContent = present
                && !string.Equals(
                    lastPresentedMotdId,
                    presentation.Message?.Id,
                    System.StringComparison.Ordinal);
            if (present) lastPresentedMotdId = presentation.Message?.Id;
            motdTitle.text = DemoMotdDisplayCopy.Title(presentation);
            motdBody.text = DemoMotdDisplayCopy.Body(presentation);
            motdAction.gameObject.SetActive(present && presentation.Message?.Url is not null);
            motdDismiss.gameObject.SetActive(present);
            motdExpanded = false;
            motdUnread = freshContent;
            ApplyMotdVisibility();
        }

        private void ApplyMotdVisibility()
        {
            var hasPresentation = HasMotdPresentation();
            ApplyHandleRailLayout(hasPresentation);
            if (motdRoot == null || motdHandle == null || motdUnreadIndicator == null) return;

            motdRoot.SetActive(hasPresentation
                && motdExpanded
                && !rewardsState.Current.IsOpen);
            motdHandle.gameObject.SetActive(hasPresentation);
            motdUnreadIndicator.SetActive(hasPresentation && !motdExpanded && motdUnread);
        }

        private bool HasMotdPresentation() => renderedMotd is not null
            && (renderedMotd.Status != DemoMotdPresentation.AbsentState
                || renderedMotd.DismissedId is not null);

        private void ApplyHandleRailLayout(bool hasMotdHandle)
        {
            var position = FirstRailHandleOffset;
            if (rewardsState.Current.ShowHandle)
            {
                LayoutRailHandle(rewardsHandle, position);
                position = new Vector2(position.x, position.y - RailHandleSize.y);
            }

            LayoutRailHandle(motdHandle, position);
            if (hasMotdHandle)
                position = new Vector2(position.x, position.y - RailHandleSize.y);
            LayoutRailHandle(chatHandle, position);
        }

        private static void LayoutRailHandle(Button? handle, Vector2 position)
        {
            var rect = handle?.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = position;
            rect.sizeDelta = RailHandleSize;
        }

        private void OnEndEdit(string text)
        {
            if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;
            RaiseSubmitted(text);
        }

        private void BindInput()
        {
            if (bound || input == null) return;
            input.onEndEdit.AddListener(OnEndEdit);
            bound = true;
        }

        private void UnbindInput()
        {
            if (!bound || input == null) return;
            input.onEndEdit.RemoveListener(OnEndEdit);
            bound = false;
        }

        private void BindDrawerActions()
        {
            if (drawersBound || (chatHandle == null && rewardsHandle == null
                && chatClose == null && rewardsClose == null
                && rewardAction == null && signInAction == null
                && rewardsSignInAction == null)) return;
            chatHandle?.onClick.AddListener(ToggleChat);
            rewardsHandle?.onClick.AddListener(ToggleRewards);
            chatClose?.onClick.AddListener(CloseChat);
            rewardsClose?.onClick.AddListener(CloseRewards);
            rewardAction?.onClick.AddListener(OnRewardRequested);
            signInAction?.onClick.AddListener(OnSignInRequested);
            rewardsSignInAction?.onClick.AddListener(OnSignInRequested);
            drawersBound = true;
        }

        private void UnbindDrawerActions()
        {
            if (!drawersBound) return;
            chatHandle?.onClick.RemoveListener(ToggleChat);
            rewardsHandle?.onClick.RemoveListener(ToggleRewards);
            chatClose?.onClick.RemoveListener(CloseChat);
            rewardsClose?.onClick.RemoveListener(CloseRewards);
            rewardAction?.onClick.RemoveListener(OnRewardRequested);
            signInAction?.onClick.RemoveListener(OnSignInRequested);
            rewardsSignInAction?.onClick.RemoveListener(OnSignInRequested);
            drawersBound = false;
        }

        private void ToggleChat()
        {
            var isOpening = !chatState.Current.IsOpen;
            SetChatOpen(isOpening);
            chatFocusFramesLeft = isOpening ? ChatFocusFrames : 0;
        }

        private void ToggleRewards() => SetRewardsOpen(!rewardsState.Current.IsOpen);

        private void CloseChat() => SetChatOpen(false);

        private void CloseRewards() => SetRewardsOpen(false);

        private void OnRewardRequested()
        {
            if (ClaimStartsSignIn())
            {
                RaiseSignInRequested();
                return;
            }
            RaiseRewardRequested();
        }

        private void OnRewardOfferRequested(RewardOfferRow row)
        {
            if (ClaimStartsSignIn())
            {
                RaiseSignInRequested();
                return;
            }
            if (row.RewardRef.Length == 0) return;
            RewardOfferRequested?.Invoke(row.RewardRef);
        }

        /// <summary>
        /// A signed-out claim click still starts Haiven sign-in. The rewards
        /// drawer hides those buttons until <see cref="DemoSignInState.SignedIn"/>.
        /// </summary>
        private bool ClaimStartsSignIn() => renderedSignInState != DemoSignInState.SignedIn;

        private void EnsureExtraRewardRows()
        {
            if (extraRewardRows.Count > 0 || rewardStatus == null) return;
            var parent = rewardStatus.rectTransform.parent as RectTransform;
            var font = rewardStatus.font;
            if (parent == null || font == null) return;
            CreateRewardOfferRow(
                parent, font, "Weekend Cache",
                "Haiven Reward Status Weekend Cache", "Haiven Claim Reward Weekend Cache",
                "Haiven Claimed Label Weekend Cache", -92f);
            CreateRewardOfferRow(
                parent, font, "Supply Drop",
                "Haiven Reward Status Supply Drop", "Haiven Claim Reward Supply Drop",
                "Haiven Claimed Label Supply Drop", -136f);
        }

        private void CreateRewardOfferRow(
            RectTransform parent,
            Font font,
            string title,
            string statusName,
            string buttonName,
            string claimedName,
            float y)
        {
            var status = StatusLabel(
                parent, font, statusName, title + " · WAITING FOR SIGN-IN",
                new Vector2(20f, y), new Vector2(320f, 32f));
            status.fontSize = 16;
            var button = ButtonControl(
                parent, font, buttonName, "Claim reward", new Vector2(352f, 244f), new Vector2(148f, 36f));
            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-20f, y);
            rect.sizeDelta = new Vector2(148f, 36f);
            button.interactable = true;
            var claimed = ClaimedLabel(parent, font, claimedName, rect);
            var row = new RewardOfferRow(title, status, button, claimed);
            button.onClick.AddListener(() => OnRewardOfferRequested(row));
            extraRewardRows.Add(row);
        }

        private void OnSignInRequested()
        {
            // Hide before the handoff starts so this frame cannot paint the join card.
            signInDialog?.SetActive(false);
            RaiseSignInRequested();
        }

        private void BindMotdActions()
        {
            if (motdBound || motdAction == null || motdDismiss == null
                || motdCollapse == null || motdHandle == null) return;
            motdAction.onClick.AddListener(OnMotdActionRequested);
            motdDismiss.onClick.AddListener(OnMotdDismissed);
            motdCollapse.onClick.AddListener(CollapseMotd);
            motdHandle.onClick.AddListener(ToggleMotd);
            motdBound = true;
        }

        private void UnbindMotdActions()
        {
            if (!motdBound || motdAction == null || motdDismiss == null
                || motdCollapse == null || motdHandle == null) return;
            motdAction.onClick.RemoveListener(OnMotdActionRequested);
            motdDismiss.onClick.RemoveListener(OnMotdDismissed);
            motdCollapse.onClick.RemoveListener(CollapseMotd);
            motdHandle.onClick.RemoveListener(ToggleMotd);
            motdBound = false;
        }

        private void ToggleMotd()
        {
            motdExpanded = !motdExpanded;
            if (motdExpanded) motdUnread = false;
            ApplyMotdVisibility();
        }

        private void CollapseMotd()
        {
            motdExpanded = false;
            ApplyMotdVisibility();
        }

        private void OnMotdActionRequested()
        {
            if (renderedMotd is not null) RaiseMotdActionRequested(renderedMotd);
        }

        private void OnMotdDismissed()
        {
            if (renderedMotd is not null) RaiseMotdDismissed(renderedMotd);
        }

        private void Configure(
            Text logText,
            Text inventoryText,
            InputField inputField,
            Text connectionStatusText,
            Text moderationStatusText,
            Text rewardStatusText,
            Button rewardButton,
            GameObject chatRoot,
            GameObject chatConversationRoot,
            GameObject chatSignInDialog,
            Text chatSignInTitle,
            Text chatSignInMessage,
            GameObject rewardsRoot,
            Button chatToggle,
            Text chatToggleLabel,
            Button rewardsToggle,
            Text rewardsToggleLabel,
            GameObject rewardsUnclaimedMark,
            Button chatCloseButton,
            Button rewardsCloseButton,
            GameObject messageRoot,
            Text messageTitle,
            Text messageBody,
            Button messageAction,
            Button messageDismiss,
            Button messageCollapse,
            Button messageToggle,
            GameObject messageUnreadIndicator,
            Button signInButton,
            Text signInButtonLabel)
        {
            UnbindInput();
            UnbindDrawerActions();
            UnbindMotdActions();
            log = logText;
            inventory = inventoryText;
            input = inputField;
            connectionStatus = connectionStatusText;
            moderationStatus = moderationStatusText;
            rewardStatus = rewardStatusText;
            rewardAction = rewardButton;
            chatDrawer = chatRoot;
            chatConversation = chatConversationRoot;
            signInDialog = chatSignInDialog;
            signInTitle = chatSignInTitle;
            signInMessage = chatSignInMessage;
            rewardsDrawer = rewardsRoot;
            chatHandle = chatToggle;
            chatHandleLabel = chatToggleLabel;
            rewardsHandle = rewardsToggle;
            rewardsHandleLabel = rewardsToggleLabel;
            rewardsUnclaimedIndicator = rewardsUnclaimedMark;
            chatClose = chatCloseButton;
            rewardsClose = rewardsCloseButton;
            motdRoot = messageRoot;
            motdTitle = messageTitle;
            motdBody = messageBody;
            motdAction = messageAction;
            motdDismiss = messageDismiss;
            motdCollapse = messageCollapse;
            motdHandle = messageToggle;
            motdUnreadIndicator = messageUnreadIndicator;
            signInAction = signInButton;
            signInLabel = signInButtonLabel;
            BindInput();
            BindDrawerActions();
            BindMotdActions();
            ApplyMotdVisibility();
        }

        internal static HaivenBossRoomUGuiView CreateFallback(Transform parent)
        {
            // Boss Room titles are Bungee and body copy is Roboto Bold. Those faces
            // stay in the host project; LegacyRuntime carries the hierarchy by weight.
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasObject = new GameObject(
                "Haiven Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(parent, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            // Match the stock canvas' width/height interpolation as well as its
            // aspect ratio. Without this, non-16:9 Game views scale the Haiven
            // rail differently from Boss Room's X and gear controls.
            scaler.matchWidthOrHeight = BossRoomCanvasMatch;

            // Keep every fallback surface inside notches and platform-reserved
            // screen edges without asking the host game to special-case Haiven UI.
            var safeAreaObject = new GameObject(
                "Haiven Safe Area", typeof(RectTransform), typeof(HaivenSafeArea));
            safeAreaObject.transform.SetParent(canvasObject.transform, false);
            var safeArea = safeAreaObject.GetComponent<RectTransform>();
            safeArea.anchorMin = Vector2.zero;
            safeArea.anchorMax = Vector2.one;
            safeArea.offsetMin = Vector2.zero;
            safeArea.offsetMax = Vector2.zero;

            // Persistent rail controls render below dialogs so an open surface
            // cannot be obscured by a handle. Both transforms are layout-only.
            var handleLayer = OverlayComponent(safeArea, "Haiven Handle Layer");
            var dialogLayer = OverlayComponent(safeArea, "Haiven Dialog Layer");

            // Chat owns its panel independently from rewards. Its sign-in dialog is
            // only the retry after a stopped handoff; a signed-out open starts the browser.
            var chatComponent = OverlayComponent(dialogLayer, "Haiven Chat Component");
            var chat = Panel(
                chatComponent, "Haiven Chat", new Vector2(0, 0),
                ChatPanelOffset, ChatPanelSize);
            AddDialogChrome(chat);
            var conversation = OverlayComponent(chat, "Haiven Chat Conversation");
            // Each status gets its own row. Side by side, the longer states
            // ("CONNECTED · AUTHENTICATING", "WAITING FOR CONNECTION") ran under
            // the close button and out of the panel.
            var connectionStatusText = StatusLabel(
                conversation, font, "Haiven Connection Status", "HAIVEN · STARTING",
                new Vector2(16, -8), new Vector2(508, 24));
            var moderationStatusText = StatusLabel(
                conversation, font, "Haiven Moderation Status", "MODERATION · READY",
                new Vector2(16, -40), new Vector2(608, 24));
            // The log shares the input field's 12px column (still clear of the 6px
            // rim). At 16px it would be 648 wide, a few pixels short of a typical
            // sender name and message; the panel cannot widen past the action bar.
            var logText = Label(conversation, font, new Vector2(12, 52), new Vector2(-12, -72));
            logText.gameObject.name = "Haiven Chat Log";
            logText.fontSize = ChatBodyFontSize;
            logText.color = new Color(1f, 0.94f, 0.82f, 1f);
            logText.supportRichText = false;
            logText.horizontalOverflow = HorizontalWrapMode.Wrap;
            logText.verticalOverflow = VerticalWrapMode.Truncate;
            var inputField = Field(conversation, font);

            var accountGate = Panel(
                chat, "Haiven Chat Sign In Dialog", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(500, 228));
            AddDialogChrome(accountGate);
            var accountTitle = Label(accountGate, font, new Vector2(24, 158), new Vector2(-24, -18));
            accountTitle.gameObject.name = "Haiven Chat Sign In Title";
            accountTitle.text = "";
            accountTitle.fontSize = 24;
            accountTitle.fontStyle = FontStyle.Bold;
            accountTitle.color = Color.white;
            accountTitle.alignment = TextAnchor.MiddleLeft;
            var accountMessage = Label(accountGate, font, new Vector2(24, 72), new Vector2(-24, -68));
            accountMessage.gameObject.name = "Haiven Chat Sign In Message";
            accountMessage.text = "Sign in or register with Haiven to join chat and claim rewards.";
            accountMessage.fontSize = 19;
            accountMessage.alignment = TextAnchor.UpperLeft;
            accountMessage.supportRichText = false;
            accountMessage.resizeTextForBestFit = true;
            accountMessage.resizeTextMinSize = 16;
            accountMessage.resizeTextMaxSize = 19;
            var signIn = ButtonControl(
                accountGate, font, "Haiven Sign In", "Sign in or register", new Vector2(24, 20), new Vector2(224, 40));
            var signInText = signIn.GetComponentInChildren<Text>(true)!;
            conversation.gameObject.SetActive(false);
            // Stay inactive until a stopped handoff needs the existing retry copy.
            // Signed-out chat starts the browser instead of painting this card.
            accountGate.gameObject.SetActive(false);
            var chatCloseButton = DrawerCloseButton(
                chat, font, "Haiven Chat Close", "Close", new Vector2(-12, -8), new Vector2(96, 28));

            // One upper-center surface groups the closely related reward action
            // and resulting inventory. A single persistent handle controls it.
            var rewardsComponent = OverlayComponent(dialogLayer, "Haiven Rewards Component");
            var items = Panel(
                rewardsComponent, "Haiven Rewards and Inventory", new Vector2(0.5f, 1),
                RewardsPanelOffset, RewardsPanelSize);
            AddDialogChrome(items);
            // Reward names, claim controls, and inventory stay in this layer so a
            // signed-out player cannot see a claim that may already exist.
            var rewardsBody = OverlayComponent(items, "Haiven Rewards Content");
            var rewardsHeading = StatusLabel(
                rewardsBody, font, "Haiven Rewards Heading", "REWARDS & INVENTORY",
                new Vector2(20, -12), new Vector2(300, 32));
            rewardsHeading.fontStyle = FontStyle.Bold;
            rewardsHeading.color = Color.white;
            var rewardStatusText = StatusLabel(
                rewardsBody, font, "Haiven Reward Status", "REWARD · WAITING FOR SIGN-IN",
                new Vector2(20, -48), new Vector2(320, 32));
            rewardStatusText.fontSize = 16;
            var claimReward = ButtonControl(
                rewardsBody, font, "Haiven Claim Reward", "Claim reward", new Vector2(352, 244), new Vector2(148, 40));
            var claimRect = claimReward.GetComponent<RectTransform>();
            claimRect.anchorMin = claimRect.anchorMax = claimRect.pivot = new Vector2(1f, 1f);
            claimRect.anchoredPosition = new Vector2(-20f, -48f);
            claimRect.sizeDelta = new Vector2(148f, 36f);
            claimReward.interactable = true;
            var primaryClaimed = ClaimedLabel(rewardsBody, font, "Haiven Claimed Label", claimRect);
            var inventoryHeading = StatusLabel(
                rewardsBody, font, "Haiven Inventory Heading", "YOUR INVENTORY",
                new Vector2(20, -180), new Vector2(260, 28));
            inventoryHeading.fontStyle = FontStyle.Bold;
            // The summary string stays on this Text so cards and tests can read
            // it. The card grid is the only visible inventory.
            var inventoryText = Label(rewardsBody, font, new Vector2(20, 12), new Vector2(-20, -400));
            inventoryText.gameObject.name = "Haiven Inventory Items";
            inventoryText.alignment = TextAnchor.UpperLeft;
            inventoryText.fontSize = 16;
            inventoryText.supportRichText = false;
            // Same right edge and row size as Claim reward, 8px above it.
            var rewardsCloseButton = DrawerCloseButton(
                items, font, "Haiven Rewards Close", "Close",
                new Vector2(
                    claimRect.anchoredPosition.x,
                    claimRect.anchoredPosition.y + 8f + claimRect.sizeDelta.y),
                claimRect.sizeDelta);
            var rewardsGate = Panel(
                items, "Haiven Rewards Sign In Dialog", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(460, 228));
            AddDialogChrome(rewardsGate);
            var rewardsGateTitle = Label(rewardsGate, font, new Vector2(24, 158), new Vector2(-24, -18));
            rewardsGateTitle.gameObject.name = "Haiven Rewards Sign In Title";
            rewardsGateTitle.text = "CLAIM HAIVEN REWARDS";
            rewardsGateTitle.fontSize = 24;
            rewardsGateTitle.fontStyle = FontStyle.Bold;
            rewardsGateTitle.color = Color.white;
            rewardsGateTitle.alignment = TextAnchor.MiddleLeft;
            var rewardsGateMessage = Label(rewardsGate, font, new Vector2(24, 72), new Vector2(-24, -68));
            rewardsGateMessage.gameObject.name = "Haiven Rewards Sign In Message";
            rewardsGateMessage.text =
                "Sign in or register with Haiven to see your rewards. A reward you already claimed stays on your account.";
            rewardsGateMessage.fontSize = 19;
            rewardsGateMessage.alignment = TextAnchor.UpperLeft;
            rewardsGateMessage.supportRichText = false;
            rewardsGateMessage.resizeTextForBestFit = true;
            rewardsGateMessage.resizeTextMinSize = 16;
            rewardsGateMessage.resizeTextMaxSize = 19;
            var rewardsSignIn = ButtonControl(
                rewardsGate, font, "Haiven Rewards Sign In", "Sign in or register",
                new Vector2(24, 20), new Vector2(224, 40));
            var rewardsSignInText = rewardsSignIn.GetComponentInChildren<Text>(true)!;

            var message = Panel(
                dialogLayer, "Haiven Message of the Day", new Vector2(0.5f, 1),
                MotdPanelOffset, MotdPanelSize, HaivenBossRoomMenuConfiguration.DialogScrim);
            AddDialogChrome(message);
            var messageTitle = Label(message, font, new Vector2(20, 96), new Vector2(-16, -12));
            messageTitle.fontSize = 22;
            messageTitle.fontStyle = FontStyle.Bold;
            messageTitle.color = Color.white;
            messageTitle.alignment = TextAnchor.MiddleLeft;
            messageTitle.resizeTextForBestFit = true;
            messageTitle.resizeTextMinSize = 18;
            messageTitle.resizeTextMaxSize = 22;
            var messageBody = Label(message, font, new Vector2(20, 42), new Vector2(-16, -48));
            messageBody.fontSize = 18;
            messageBody.alignment = TextAnchor.UpperLeft;
            messageBody.resizeTextForBestFit = true;
            messageBody.resizeTextMinSize = 15;
            messageBody.resizeTextMaxSize = 18;
            var messageAction = ButtonControl(
                message, font, "MOTD Action", "Learn more", new Vector2(20, 8), new Vector2(132, 28));
            var messageDismiss = ButtonControl(
                message, font, "MOTD Dismiss", "Dismiss", new Vector2(164, 8), new Vector2(112, 28));
            var messageCollapse = ButtonControl(
                message, font, "MOTD Collapse", "Collapse", new Vector2(388, 8), new Vector2(112, 28));

            var chatToggle = ChatHandle(
                handleLayer,
                font,
                Vector2.one,
                FirstRailHandleOffset,
                RailHandleSize,
                out var chatToggleLabel);
            var rewardsToggle = RewardsHandle(
                handleLayer,
                font,
                Vector2.one,
                FirstRailHandleOffset,
                RailHandleSize,
                out var rewardsToggleLabel,
                out var rewardsUnclaimedMark);
            var messageToggle = MotdHandle(
                handleLayer,
                font,
                Vector2.one,
                FirstRailHandleOffset,
                RailHandleSize,
                out var messageUnreadIndicator);

            var view = canvasObject.AddComponent<HaivenBossRoomUGuiView>();
            view.rewardClaimedLabel = primaryClaimed;
            view.rewardsContent = rewardsBody.gameObject;
            view.rewardsAccountGate = rewardsGate.gameObject;
            view.rewardsSignInAction = rewardsSignIn;
            view.rewardsSignInLabel = rewardsSignInText;
            view.rewardsSignInTitle = rewardsGateTitle;
            view.rewardsSignInMessage = rewardsGateMessage;
            view.CreateRewardOfferRow(
                rewardsBody, font, "Weekend Cache",
                "Haiven Reward Status Weekend Cache", "Haiven Claim Reward Weekend Cache",
                "Haiven Claimed Label Weekend Cache", -92f);
            view.CreateRewardOfferRow(
                rewardsBody, font, "Supply Drop",
                "Haiven Reward Status Supply Drop", "Haiven Claim Reward Supply Drop",
                "Haiven Claimed Label Supply Drop", -136f);
            view.Configure(
                logText,
                inventoryText,
                inputField,
                connectionStatusText,
                moderationStatusText,
                rewardStatusText,
                claimReward,
                chat.gameObject,
                conversation.gameObject,
                accountGate.gameObject,
                accountTitle,
                accountMessage,
                items.gameObject,
                chatToggle,
                chatToggleLabel,
                rewardsToggle,
                rewardsToggleLabel,
                rewardsUnclaimedMark,
                chatCloseButton,
                rewardsCloseButton,
                message.gameObject,
                messageTitle,
                messageBody,
                messageAction,
                messageDismiss,
                messageCollapse,
                messageToggle,
                messageUnreadIndicator,
                signIn,
                signInText);
            view.ApplyRewardsAccountGate();
            view.ShowInventory(view.renderedInventory);
            return view;
        }

        private void RenderLootGrid(string summary)
        {
            EnsureLootGrid();
            if (lootGrid == null) return;
            ClearLootCards();
            var font = inventory != null ? inventory.font : null;
            var cards = LootInventoryCards.FromSummary(summary);
            if (cards.Count == 0)
            {
                lootCards.Add(CreateEmptyLootCard(lootGrid, font));
                SetLootContentHeight(LootCardHeight + 28f, scroll: false);
            }
            else
            {
                for (var i = 0; i < cards.Count; i++)
                    lootCards.Add(CreateLootCard(lootGrid, font, cards[i], i));
                var contentHeight = cards.Count * (LootCardHeight + LootCardGap) - LootCardGap;
                SetLootContentHeight(contentHeight, scroll: cards.Count > 2);
            }
            lootGridSummary = summary;
        }

        private void EnsureLootGrid()
        {
            if (lootGrid != null || inventory == null) return;
            var host = inventory.transform.parent as RectTransform;
            if (host == null) return;

            var font = inventory.font;
            Caption(host, font, "Haiven Loot Current Label", "Current", new Vector2(20f, -212f), top: true);
            var list = inventory.rectTransform;
            list.anchorMin = list.anchorMax = list.pivot = new Vector2(0f, 1f);
            list.anchoredPosition = new Vector2(20f, -212f);
            list.sizeDelta = Vector2.zero;
            inventory.fontSize = 16;
            inventory.color = new Color(0f, 0f, 0f, 0f);
            inventory.alignment = TextAnchor.UpperLeft;
            inventory.supportRichText = false;
            inventory.raycastTarget = false;

            var gridObject = new GameObject(
                "Haiven Inventory Grid", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            gridObject.SetActive(false);
            gridObject.transform.SetParent(host, false);
            var viewport = gridObject.GetComponent<RectTransform>();
            viewport.anchorMin = new Vector2(0f, 1f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(0.5f, 1f);
            viewport.offsetMin = new Vector2(20f, -504f);
            viewport.offsetMax = new Vector2(-20f, -240f);
            gridObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

            var contentObject = new GameObject("Haiven Inventory Cards", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            var content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, LootCardHeight);

            var scroll = gridObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = false;
            scroll.inertia = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            lootScroll = scroll;
            lootGrid = content;
            gridObject.SetActive(true);
        }

        private static void Caption(
            RectTransform parent, Font? font, string name, string caption, Vector2 position, bool top)
        {
            var label = new GameObject(name, typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var rect = label.GetComponent<RectTransform>();
            var anchor = top ? new Vector2(0f, 1f) : new Vector2(0f, 0f);
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(240f, 24f);
            var text = label.GetComponent<Text>();
            text.font = font;
            text.text = caption;
            text.fontSize = 16;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.color = top ? Color.white : ThemeColor(LootInventoryCards.MutedColor);
        }

        private void SetLootContentHeight(float height, bool scroll)
        {
            if (lootGrid == null) return;
            lootGrid.sizeDelta = new Vector2(0f, height);
            if (lootScroll == null) return;
            lootScroll.vertical = scroll;
            lootScroll.verticalNormalizedPosition = 1f;
        }

        private void ClearLootCards()
        {
            for (var i = 0; i < lootCards.Count; i++)
            {
                var card = lootCards[i];
                if (card == null) continue;
                card.SetActive(false);
                UnityEngine.Object.Destroy(card);
            }
            lootCards.Clear();
        }

        private static GameObject CreateEmptyLootCard(RectTransform parent, Font? font)
        {
            var card = CreateCardShell(parent, "Haiven Loot Empty", LootInventoryCards.BorderColor);
            var rect = card.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -(LootCardHeight + 28f));
            rect.offsetMax = Vector2.zero;
            var title = CardLabel(
                card.GetComponent<RectTransform>(), font, "Haiven Loot Title",
                LootInventoryCards.EmptyTitle, 18, FontStyle.Bold);
            title.color = Color.white;
            title.alignment = TextAnchor.MiddleLeft;
            StretchTop(title.rectTransform, 16f, 48f, 12f);
            var body = CardLabel(
                card.GetComponent<RectTransform>(), font, "Haiven Loot Detail",
                LootInventoryCards.EmptyBody, 16, FontStyle.Normal);
            body.color = ThemeColor(LootInventoryCards.MutedColor);
            body.alignment = TextAnchor.UpperLeft;
            StretchTop(body.rectTransform, 16f, 96f, 48f);
            return card;
        }

        private static GameObject CreateLootCard(RectTransform parent, Font? font, LootCardModel model, int row)
        {
            var card = CreateCardShell(parent, "Haiven Loot Card", LootInventoryCards.BorderColor);
            var rect = card.GetComponent<RectTransform>();
            var top = -row * (LootCardHeight + LootCardGap);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, top - LootCardHeight);
            rect.offsetMax = new Vector2(0f, top);

            var swatch = new GameObject("Haiven Loot Swatch", typeof(RectTransform), typeof(Image));
            swatch.transform.SetParent(rect, false);
            swatch.GetComponent<Image>().color = ThemeColor(LootInventoryCards.SwatchColor);
            var swatchRect = swatch.GetComponent<RectTransform>();
            swatchRect.anchorMin = swatchRect.anchorMax = swatchRect.pivot = new Vector2(0f, 1f);
            swatchRect.anchoredPosition = new Vector2(14f, -15f);
            swatchRect.sizeDelta = new Vector2(44f, 44f);
            var mark = CardLabel(swatchRect, font, "Haiven Loot Mark", Mark(model.Title), 20, FontStyle.Bold);
            mark.color = ThemeColor(LootInventoryCards.MarkColor);
            mark.alignment = TextAnchor.MiddleCenter;
            mark.rectTransform.anchorMin = Vector2.zero;
            mark.rectTransform.anchorMax = Vector2.one;
            mark.rectTransform.offsetMin = Vector2.zero;
            mark.rectTransform.offsetMax = Vector2.zero;

            var pill = new GameObject("Haiven Loot Status", typeof(RectTransform), typeof(Image));
            pill.transform.SetParent(rect, false);
            pill.GetComponent<Image>().color = ThemeColor(LootInventoryCards.SwatchColor);
            var pillRect = pill.GetComponent<RectTransform>();
            pillRect.anchorMin = pillRect.anchorMax = pillRect.pivot = new Vector2(1f, 1f);
            pillRect.anchoredPosition = new Vector2(-8f, -8f);
            pillRect.sizeDelta = new Vector2(132f, 28f);
            var pillLabel = CardLabel(
                pillRect, font, "Haiven Loot Status Label", LootInventoryCards.OwnedLabel, 16, FontStyle.Bold);
            pillLabel.color = Color.white;
            pillLabel.alignment = TextAnchor.MiddleCenter;
            pillLabel.rectTransform.anchorMin = Vector2.zero;
            pillLabel.rectTransform.anchorMax = Vector2.one;
            pillLabel.rectTransform.offsetMin = Vector2.zero;
            pillLabel.rectTransform.offsetMax = Vector2.zero;

            var title = CardLabel(rect, font, "Haiven Loot Title", model.Title, 18, FontStyle.Bold);
            title.color = Color.white;
            title.alignment = TextAnchor.MiddleLeft;
            StretchTop(title.rectTransform, 68f, 36f, 8f);
            title.rectTransform.offsetMax = new Vector2(-148f, title.rectTransform.offsetMax.y);
            var detail = CardLabel(rect, font, "Haiven Loot Detail", model.Code, 16, FontStyle.Normal);
            detail.color = ThemeColor(LootInventoryCards.MutedColor);
            detail.alignment = TextAnchor.MiddleLeft;
            StretchTop(detail.rectTransform, 68f, 66f, 36f);
            return card;
        }

        private static GameObject CreateCardShell(RectTransform parent, string name, int borderColor)
        {
            var card = new GameObject(name, typeof(RectTransform), typeof(Image));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = ThemeColor(borderColor);
            var fill = new GameObject("Haiven Loot Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(card.transform, false);
            fill.GetComponent<Image>().color = ThemeColor(LootInventoryCards.SurfaceColor);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(1f, 1f);
            fillRect.offsetMax = new Vector2(-1f, -1f);
            return card;
        }

        private static Text CardLabel(
            RectTransform parent, Font? font, string name, string caption, int size, FontStyle style)
        {
            var label = new GameObject(name, typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var text = label.GetComponent<Text>();
            text.font = font;
            text.text = caption;
            text.fontSize = size;
            text.fontStyle = style;
            text.supportRichText = false;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static void StretchTop(RectTransform rect, float left, float bottom, float top)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(left, -bottom);
            rect.offsetMax = new Vector2(-8f, -top);
        }

        private static string Mark(string title)
        {
            for (var i = 0; i < title.Length; i++)
            {
                if (!char.IsLetterOrDigit(title[i])) continue;
                return char.ToUpperInvariant(title[i]).ToString();
            }
            return "?";
        }

        private static Color ThemeColor(int rgb) => new Color(
            ((rgb >> 16) & 255) / 255f,
            ((rgb >> 8) & 255) / 255f,
            (rgb & 255) / 255f,
            1f);

        private static Text StatusLabel(
            RectTransform parent,
            Font font,
            string name,
            string caption,
            Vector2 position,
            Vector2 size)
        {
            var label = new GameObject(name, typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var text = label.GetComponent<Text>();
            text.font = font;
            text.fontSize = 20;
            text.color = HaivenBossRoomMenuConfiguration.GoldText;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            text.text = caption;
            return text;
        }

        private static RectTransform OverlayComponent(Transform parent, string name)
        {
            var component = new GameObject(name, typeof(RectTransform));
            component.transform.SetParent(parent, false);
            var rect = component.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static void AddDialogChrome(RectTransform panel)
        {
            var name = panel.gameObject.name;
            // Sides first so the lit top and bottom bands stay continuous, matching
            // ui_dialog.png. The bars stay inside the content inset.
            AddEdge(panel, name + " Border Left", Vector2.zero, new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(DialogBorderThickness, 0f), HaivenBossRoomMenuConfiguration.DialogBorderShade);
            AddEdge(panel, name + " Border Right", new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f),
                new Vector2(DialogBorderThickness, 0f), HaivenBossRoomMenuConfiguration.DialogBorderShade);
            AddEdge(panel, name + " Border Bottom", Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, DialogBorderThickness), HaivenBossRoomMenuConfiguration.DialogBorder);
            AddEdge(panel, name + " Border Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, DialogBorderThickness), HaivenBossRoomMenuConfiguration.DialogBorder);
        }

        private static void AddButtonChrome(RectTransform button)
        {
            var name = button.gameObject.name;
            AddEdge(button, name + " Border Left", Vector2.zero, new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(ButtonBorderThickness, 0f), HaivenBossRoomMenuConfiguration.ButtonBorder);
            AddEdge(button, name + " Border Right", new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f),
                new Vector2(ButtonBorderThickness, 0f), HaivenBossRoomMenuConfiguration.ButtonBorder);
            AddEdge(button, name + " Border Bottom", Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, ButtonBorderThickness), HaivenBossRoomMenuConfiguration.ButtonHighlight);
            AddEdge(button, name + " Border Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, ButtonBorderThickness), HaivenBossRoomMenuConfiguration.ButtonHighlight);
        }

        private static void AddEdge(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 size,
            Color color)
        {
            var edge = new GameObject(name, typeof(RectTransform), typeof(Image));
            edge.transform.SetParent(parent, false);
            edge.GetComponent<Image>().color = color;
            var rect = edge.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        private static RectTransform Panel(
            Transform parent,
            string name,
            Vector2 corner,
            Vector2 offset,
            Vector2 size,
            Color? fill = null)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);
            panel.GetComponent<Image>().color = fill ?? HaivenBossRoomMenuConfiguration.DialogFill;
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
            return rect;
        }

        private static Text Label(RectTransform parent, Font font, Vector2 min, Vector2 max)
        {
            var label = new GameObject("Text", typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = min;
            rect.offsetMax = max;
            var text = label.GetComponent<Text>();
            text.font = font;
            text.fontSize = 24;
            text.color = Color.white;
            text.alignment = TextAnchor.LowerLeft;
            return text;
        }

        private static InputField Field(RectTransform parent, Font font)
        {
            var field = new GameObject(
                "Input", typeof(RectTransform), typeof(Image), typeof(HaivenChatInputField), typeof(Mask));
            field.transform.SetParent(parent, false);
            field.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.InputFill;
            field.GetComponent<Mask>().showMaskGraphic = true;
            var rect = field.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(0.5f, 0);
            rect.offsetMin = new Vector2(12, 8);
            rect.offsetMax = new Vector2(-12, 44);
            AddButtonChrome(rect);

            var text = new GameObject("Text", typeof(RectTransform), typeof(Text));
            text.transform.SetParent(field.transform, false);
            var textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8, 4);
            textRect.offsetMax = new Vector2(-8, -4);
            var label = text.GetComponent<Text>();
            label.font = font;
            label.fontSize = ChatBodyFontSize;
            label.fontStyle = FontStyle.Bold;
            label.color = HaivenBossRoomMenuConfiguration.GoldText;
            label.supportRichText = false;
            // Centre the typed line (and the caret, which follows the text's
            // alignment) vertically in the field; the default UpperLeft pins it
            // to the top border.
            label.alignment = TextAnchor.MiddleLeft;
            // A single-line composer scrolls inside the field instead of wrapping
            // over the transcript. The parent Mask keeps the overflow clipped.
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;

            var inputField = field.GetComponent<InputField>();
            inputField.textComponent = label;
            inputField.lineType = InputField.LineType.SingleLine;
            return inputField;
        }

        private static Button ChatHandle(
            Transform parent,
            Font font,
            Vector2 corner,
            Vector2 position,
            Vector2 size,
            out Text label)
        {
            var button = BossRoomRailHandle(
                parent, "Haiven Chat Handle", "Chat", corner, position, size,
                out var rect, out var gold, out var blue);

            // A compact speech bubble with a small tail is distinct from the MOTD
            // bulletin label while remaining legible at the stock 100px hit size.
            IconBlock(rect, "Chat Bubble", new Vector2(0, 2), new Vector2(34, 22), gold);
            IconBlock(rect, "Chat Bubble Fill", new Vector2(0, 2), new Vector2(28, 16), blue);
            IconBlock(rect, "Chat Bubble Tail", new Vector2(-9, -12), new Vector2(8, 8), gold);
            label = HiddenHandleLabel(rect, font, "Chat Handle Label", "Chat");
            return button;
        }

        private static Button RewardsHandle(
            Transform parent,
            Font font,
            Vector2 corner,
            Vector2 position,
            Vector2 size,
            out Text label,
            out GameObject unclaimedIndicator)
        {
            var button = BossRoomRailHandle(
                parent, "Haiven Rewards and Inventory Handle", "Rewards", corner, position, size,
                out var rect, out var gold, out var blue);

            // Code-native present silhouette: lid, box, crossing ribbon, and bow.
            // The gold silhouette and blue inset match Boss Room's utility sprites.
            IconBlock(rect, "Rewards Gift Box", new Vector2(0, -5), new Vector2(34, 25), gold);
            IconBlock(rect, "Rewards Gift Box Fill", new Vector2(0, -5), new Vector2(28, 19), blue);
            IconBlock(rect, "Rewards Gift Lid", new Vector2(0, 8), new Vector2(40, 7), gold);
            IconBlock(rect, "Rewards Gift Ribbon Vertical", new Vector2(0, -2), new Vector2(6, 34), gold);
            IconBlock(rect, "Rewards Gift Ribbon Horizontal", new Vector2(0, -1), new Vector2(34, 5), gold);
            IconBlock(rect, "Rewards Gift Bow Left", new Vector2(-7, 15), new Vector2(12, 8), gold);
            IconBlock(rect, "Rewards Gift Bow Right", new Vector2(7, 15), new Vector2(12, 8), gold);
            label = HiddenHandleLabel(
                rect, font, "Rewards and Inventory Handle Label", "Rewards and Inventory");
            unclaimedIndicator = new GameObject(
                "Rewards Unclaimed Subicon", typeof(RectTransform), typeof(Image));
            unclaimedIndicator.transform.SetParent(rect, false);
            unclaimedIndicator.GetComponent<Image>().color = gold;
            var unclaimedRect = unclaimedIndicator.GetComponent<RectTransform>();
            unclaimedRect.anchorMin = unclaimedRect.anchorMax = unclaimedRect.pivot = Vector2.one;
            unclaimedRect.anchoredPosition = new Vector2(-2, -2);
            unclaimedRect.sizeDelta = new Vector2(14, 14);
            IconBlock(
                unclaimedRect,
                "Rewards Unclaimed Fill",
                Vector2.zero,
                new Vector2(8, 8),
                new Color(0.94f, 0.27f, 0.08f, 1f));
            unclaimedIndicator.SetActive(false);
            return button;
        }

        private static Button BossRoomRailHandle(
            Transform parent,
            string name,
            string prefix,
            Vector2 corner,
            Vector2 position,
            Vector2 size,
            out RectTransform rect,
            out Color gold,
            out Color blue)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var shadow = new Color(0.055f, 0.137f, 0.212f, 1f);
            var bronze = new Color(0.608f, 0.341f, 0f, 1f);
            gold = new Color(0.898f, 0.741f, 0.161f, 1f);
            var navy = new Color(0.071f, 0.208f, 0.341f, 1f);
            blue = new Color(0.153f, 0.38f, 0.596f, 1f);
            IconBlock(rect, prefix + " Frame Shadow Vertical", new Vector2(0, -1), new Vector2(58, 64), shadow);
            IconBlock(rect, prefix + " Frame Shadow Horizontal", new Vector2(0, -1), new Vector2(64, 58), shadow);
            IconBlock(rect, prefix + " Frame Bronze Vertical", Vector2.zero, new Vector2(56, 62), bronze);
            IconBlock(rect, prefix + " Frame Bronze Horizontal", Vector2.zero, new Vector2(62, 56), bronze);
            IconBlock(rect, prefix + " Frame Gold Vertical", Vector2.zero, new Vector2(52, 58), gold);
            IconBlock(rect, prefix + " Frame Gold Horizontal", Vector2.zero, new Vector2(58, 52), gold);
            IconBlock(rect, prefix + " Frame Fill Vertical", Vector2.zero, new Vector2(44, 52), navy);
            IconBlock(rect, prefix + " Frame Fill Horizontal", Vector2.zero, new Vector2(52, 44), navy);
            IconBlock(rect, prefix + " Frame Highlight", new Vector2(0, 21), new Vector2(40, 2), blue);
            return buttonObject.GetComponent<Button>();
        }

        private static Text HiddenHandleLabel(RectTransform parent, Font font, string name, string caption)
        {
            var label = Label(parent, font, Vector2.zero, Vector2.zero);
            label.gameObject.name = name;
            label.text = caption;
            label.color = new Color(0, 0, 0, 0);
            label.fontSize = 1;
            return label;
        }

        private static Button MotdHandle(
            Transform parent,
            Font font,
            Vector2 corner,
            Vector2 position,
            Vector2 size,
            out GameObject unreadSubicon)
        {
            var button = BossRoomRailHandle(
                parent, "Haiven MOTD Handle", "MOTD", corner, position, size,
                out var rect, out var gold, out var blue);

            // Use an explicit bulletin label instead of a speech bubble so this
            // control cannot be mistaken for the independent chat handle below it.
            var label = Label(rect, font, new Vector2(6, 14), new Vector2(-6, -14));
            label.gameObject.name = "MOTD Handle Label";
            label.text = "MOTD";
            label.fontSize = 16;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = gold;

            unreadSubicon = new GameObject("MOTD Unread Subicon", typeof(RectTransform), typeof(Image));
            unreadSubicon.transform.SetParent(rect, false);
            unreadSubicon.GetComponent<Image>().color = gold;
            var unreadRect = unreadSubicon.GetComponent<RectTransform>();
            unreadRect.anchorMin = unreadRect.anchorMax = unreadRect.pivot = Vector2.one;
            unreadRect.anchoredPosition = new Vector2(-2, -2);
            unreadRect.sizeDelta = new Vector2(14, 14);
            IconBlock(
                unreadRect,
                "MOTD Unread Fill",
                Vector2.zero,
                new Vector2(8, 8),
                new Color(0.94f, 0.27f, 0.08f, 1f));

            return button;
        }

        private static void IconBlock(
            RectTransform parent,
            string name,
            Vector2 position,
            Vector2 size,
            Color color)
        {
            var block = new GameObject(name, typeof(RectTransform), typeof(Image));
            block.transform.SetParent(parent, false);
            block.GetComponent<Image>().color = color;
            var rect = block.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Button DrawerCloseButton(
            RectTransform parent,
            Font font,
            string name,
            string caption,
            Vector2? position = null,
            Vector2? size = null)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.ButtonFill;
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = position ?? new Vector2(-12, -12);
            rect.sizeDelta = size ?? new Vector2(112, 40);
            AddButtonChrome(rect);
            var label = Label(rect, font, new Vector2(8, 4), new Vector2(-8, -4));
            label.text = caption;
            label.fontSize = size.HasValue ? 16 : 18;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleCenter;
            return buttonObject.GetComponent<Button>();
        }

        private static Button ButtonControl(
            RectTransform parent,
            Font font,
            string name,
            string caption,
            Vector2 position,
            Vector2? size = null)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.ButtonFill;
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = position;
            rect.sizeDelta = size ?? new Vector2(148, 36);
            AddButtonChrome(rect);
            var label = Label(rect, font, new Vector2(6, 4), new Vector2(-6, -4));
            label.text = caption;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            if (size.HasValue) label.fontSize = 16;
            label.alignment = TextAnchor.MiddleCenter;
            return buttonObject.GetComponent<Button>();
        }

        private static Text ClaimedLabel(RectTransform parent, Font font, string name, RectTransform slot)
        {
            var label = new GameObject(name, typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = slot.anchorMin;
            rect.anchorMax = slot.anchorMax;
            rect.pivot = slot.pivot;
            rect.anchoredPosition = slot.anchoredPosition;
            rect.sizeDelta = slot.sizeDelta;
            var text = label.GetComponent<Text>();
            text.font = font;
            text.text = "Claimed";
            text.fontSize = 16;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            label.SetActive(false);
            return text;
        }

        private sealed class RewardOfferRow
        {
            public RewardOfferRow(string title, Text status, Button button, Text claimed)
            {
                Title = title;
                Status = status;
                Button = button;
                Claimed = claimed;
            }

            public string Title;
            public string RewardRef = "";
            public readonly Text Status;
            public readonly Button Button;
            public readonly Text Claimed;
        }
    }

    /// <summary>
    /// The chat composer. Focus returns to it with a draft in place after the
    /// rewards drawer closes, chat reopens, or sign-in finishes, and stock uGUI
    /// selects the whole text on every activation, so the next keystroke would
    /// overwrite the draft. Put the caret at the end instead.
    /// </summary>
    internal sealed class HaivenChatInputField : InputField
    {
        protected override void LateUpdate()
        {
            // ActivateInputField only defers: the activation, and the select-all
            // OnFocus does inside it, happen in this LateUpdate. Moving the caret
            // straight after it, in the same call, leaves no frame in which a
            // keystroke could land on the selection.
            var wasFocused = isFocused;
            base.LateUpdate();
            if (!wasFocused && isFocused) MoveTextEnd(false);
        }
    }

    [DisallowMultipleComponent]
    internal sealed class HaivenSafeArea : MonoBehaviour
    {
        private Rect lastSafeArea;
        private Vector2Int lastScreenSize;

        private void OnEnable() => Apply();

        private void LateUpdate()
        {
            var screenSize = new Vector2Int(Screen.width, Screen.height);
            if (Screen.safeArea == lastSafeArea && screenSize == lastScreenSize) return;
            Apply();
        }

        private void Apply()
        {
            var rect = GetComponent<RectTransform>();
            var screenSize = new Vector2Int(Screen.width, Screen.height);
            var safeArea = Screen.safeArea;
            if (screenSize.x <= 0 || screenSize.y <= 0) return;

            rect.anchorMin = new Vector2(safeArea.xMin / screenSize.x, safeArea.yMin / screenSize.y);
            rect.anchorMax = new Vector2(safeArea.xMax / screenSize.x, safeArea.yMax / screenSize.y);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            lastSafeArea = safeArea;
            lastScreenSize = screenSize;
        }
    }
}
