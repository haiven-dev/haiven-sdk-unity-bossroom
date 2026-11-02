#nullable enable
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    public sealed class BossRoomDrawerVisualTests
    {
        private const string OfflineTitle = "Boss Room briefing";
        private const string OfflineBody =
            "Haiven live updates are offline. You can still play Boss Room; reconnect to see the latest message.";

        private GameObject? host;

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.DestroyImmediate(host);
            host = null;
        }

        [UnityTest]
        public IEnumerator CombinedRewardsAndInventoryDialogUsesOnePersistentHandle()
        {
            var view = CreateView();
            view.ConfigureDrawers(true, true, false, false);

            var handleLayer = Child(view, "Haiven Handle Layer");
            var dialogLayer = Child(view, "Haiven Dialog Layer");
            Assert.That(dialogLayer.transform.GetSiblingIndex(),
                Is.GreaterThan(handleLayer.transform.GetSiblingIndex()),
                "dialogs must render above the persistent handle rail");
            var chatComponent = Child(view, "Haiven Chat Component");
            var chat = Child(chatComponent, "Haiven Chat");
            var chatHandle = Child(handleLayer, "Haiven Chat Handle");
            Assert.That(Child(chatHandle, "Chat Bubble"), Is.Not.Null);
            Assert.That(Child(chatHandle, "Chat Bubble Fill"), Is.Not.Null);
            Assert.That(Child(chatHandle, "Chat Bubble Tail"), Is.Not.Null);
            var hiddenChatLabel = Child(chatHandle, "Chat Handle Label").GetComponent<Text>();
            Assert.That(hiddenChatLabel.color.a, Is.Zero,
                "the accessible label must not render wrapped text over the chat icon");
            Assert.That(hiddenChatLabel.fontSize, Is.EqualTo(1));
            var conversation = Child(chat, "Haiven Chat Conversation");
            var accountGate = Child(chat, "Haiven Chat Sign In Dialog");
            Assert.That(chat.transform.parent, Is.EqualTo(chatComponent.transform));
            Assert.That(chatHandle.transform.parent, Is.EqualTo(handleLayer.transform));
            var rewardsComponent = Child(view, "Haiven Rewards Component");
            var rewards = Child(rewardsComponent, "Haiven Rewards and Inventory");
            var rewardsHandle = Child(handleLayer, "Haiven Rewards and Inventory Handle");
            Assert.That(rewards.transform.parent, Is.EqualTo(rewardsComponent.transform),
                "rewards need an independent component root");
            var rewardsPanelRect = rewards.GetComponent<RectTransform>();
            Assert.That(rewardsPanelRect.anchorMin, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(rewardsPanelRect.anchorMax, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(rewardsPanelRect.anchoredPosition.x, Is.Zero.Within(0.0001f),
                "the combined dialog should use the MOTD's upper-center placement");
            Assert.That(rewardsHandle.transform.parent, Is.EqualTo(handleLayer.transform),
                "the combined handle must stay on the persistent rail");
            var objectNames = view.GetComponentsInChildren<Transform>(true).Select(item => item.name);
            Assert.That(objectNames, Does.Not.Contain("Haiven Entitlements Component"));
            Assert.That(objectNames, Does.Not.Contain("Haiven Entitlements Handle"));
            Assert.That(objectNames, Does.Not.Contain("Haiven Chat Sign In Accent"));
            Assert.That(objectNames, Does.Not.Contain("Haiven Rewards Accent"));
            Assert.That(objectNames, Does.Not.Contain("Haiven MOTD Accent"));
            AssertDialogChrome(chat, "Haiven Chat");
            AssertDialogChrome(accountGate, "Haiven Chat Sign In Dialog");
            AssertDialogChrome(rewards, "Haiven Rewards and Inventory");
            var signInTitle = Child(accountGate, "Haiven Chat Sign In Title").GetComponent<Text>();
            Assert.That(signInTitle.color, Is.EqualTo(Color.white),
                "dialog titles should use Boss Room's white popup type, not cyan");
            Assert.That(signInTitle.fontStyle, Is.EqualTo(FontStyle.Bold));
            AssertBossRoomColor(
                Child(conversation, "Haiven Connection Status").GetComponent<Text>().color,
                235f / 255f, 209f / 255f, 63f / 255f,
                "status type should use Boss Room's gold settings label");
            var inputField = Child(conversation, "Input");
            AssertBossRoomColor(inputField.GetComponent<Image>().color, 2f / 255f, 7f / 255f, 14f / 255f,
                "the chat field should use Boss Room's input well");
            AssertBossRoomColor(
                Child(inputField, "Input Border Top").GetComponent<Image>().color,
                152f / 255f, 106f / 255f, 15f / 255f,
                "the chat field should use Boss Room's bronze input rim");
            AssertBossRoomColor(
                inputField.GetComponent<InputField>().textComponent.color,
                235f / 255f, 209f / 255f, 63f / 255f,
                "typed chat should use Boss Room's gold input type");
            Assert.That(inputField.GetComponent<InputField>().textComponent.fontStyle, Is.EqualTo(FontStyle.Bold));
            Assert.That(inputField.GetComponent<InputField>().textComponent.alignment,
                Is.EqualTo(TextAnchor.MiddleLeft),
                "typed chat should sit vertically centred in its field, not on the top rim");
            AssertBossRoomButton(Child(accountGate, "Haiven Sign In"));
            var style = new HaivenBossRoomMenuConfiguration();
            AssertBossRoomColor(style.PanelColor, 9f / 255f, 6f / 255f, 1f / 255f,
                "retained panel styling should match the dialog fill");
            AssertBossRoomColor(style.SurfaceColor, 20f / 255f, 36f / 255f, 52f / 255f,
                "retained surface styling should match Boss Room's button well");
            Assert.That(style.TextColor, Is.EqualTo(Color.white));
            AssertBossRoomColor(style.AccentColor, 235f / 255f, 209f / 255f, 63f / 255f,
                "retained accent styling should be Boss Room's settings gold");
            AssertText(rewards, "REWARDS & INVENTORY");
            AssertText(rewards, "YOUR INVENTORY");
            var rewardsTitle = Child(rewards, "Haiven Rewards Heading").GetComponent<Text>();
            Assert.That(rewardsTitle.color, Is.EqualTo(Color.white),
                "the rewards title should use Boss Room's white popup type");
            Assert.That(rewardsTitle.fontStyle, Is.EqualTo(FontStyle.Bold));
            AssertBossRoomColor(Child(rewards, "Haiven Inventory Heading").GetComponent<Text>().color,
                235f / 255f, 209f / 255f, 63f / 255f,
                "section labels should use Boss Room's gold settings type");
            Assert.That(Child(rewards, "Haiven Inventory Items"), Is.Not.Null,
                "inventory must share the Rewards surface");
            Assert.That(Child(accountGate, "Haiven Sign In").transform.parent,
                Is.EqualTo(accountGate.transform),
                "the chat sign-in control stays inside its dialog");

            var chatSignIns = 0;
            view.SignInRequested += () => chatSignIns++;
            view.ShowSignIn(DemoSignIn.SignedOut);
            Assert.That(accountGate.activeSelf, Is.False,
                "signed-out chat must not show the join dialog before it opens");
            chatHandle.GetComponent<Button>().onClick.Invoke();
            yield return null;

            AssertOpaque(chat);
            Assert.That(chat.activeSelf, Is.True);
            Assert.That(chatComponent.transform.GetSiblingIndex(),
                Is.EqualTo(chatComponent.transform.parent.childCount - 1),
                "the open chat component must render above persistent edge controls");
            Assert.That(chatHandle.activeSelf, Is.True,
                "opening chat from its rail handle must keep that handle available");
            Assert.That(accountGate.activeSelf, Is.False,
                "opening chat while signed out starts OAuth without the join dialog");
            Assert.That(chatSignIns, Is.EqualTo(1),
                "opening chat while signed out starts the same sign-in as the button");
            Assert.That(conversation.activeSelf, Is.False,
                "conversation controls must remain unavailable while signed out");
            Assert.That(view.IsInputFocused, Is.False,
                "the signed-out chat path must not focus the hidden message input");
            view.ShowSignIn(DemoSignIn.Waiting);
            Assert.That(accountGate.activeSelf, Is.False,
                "signing in must not flash the join dialog");
            view.ShowSignIn(DemoSignIn.Failed);
            yield return null;
            Assert.That(accountGate.activeSelf, Is.True,
                "a stopped sign-in still shows the existing retry dialog");
            var signIn = Child(accountGate, "Haiven Sign In");
            Assert.That(signIn.GetComponentInChildren<Text>(true).text, Is.EqualTo("Try again"));
            signIn.GetComponent<Button>().onClick.Invoke();
            Assert.That(accountGate.activeSelf, Is.False,
                "the sign-in click must go straight to the browser without an intermediate dialog");
            Assert.That(chatSignIns, Is.EqualTo(2));
            view.ShowSignIn(DemoSignIn.SignedIn);
            Assert.That(EventSystem.current!.currentSelectedGameObject, Is.EqualTo(Child(conversation, "Input")),
                "a sign-in that finishes with chat open must hand selection to the message input");
            yield return null;
            Assert.That(accountGate.activeSelf, Is.False);
            Assert.That(conversation.activeSelf, Is.True,
                "conversation controls must return after sign-in");
            Assert.That(view.IsInputFocused, Is.True,
                "the first chat open of a session signs in first; typing must then reach the input");
            var chatClose = Child(chat, "Haiven Chat Close").GetComponent<Button>();
            Assert.That(chatClose, Is.Not.Null, "chat drawer needs an explicit close-button affordance");
            var chatCloseButton = chatClose!;
            AssertBossRoomButton(chatCloseButton.gameObject);
            Assert.That(chatCloseButton.GetComponentInChildren<Text>(true).text, Is.EqualTo("Close"));
            Assert.That(chatCloseButton.gameObject.activeInHierarchy, Is.True);
            chatCloseButton.onClick.Invoke();
            yield return null;
            Assert.That(chat.activeSelf, Is.False, "chat close button did not dismiss its drawer");
            Assert.That(Child(view, "Haiven Chat Handle").activeSelf, Is.True);

            chatHandle.GetComponent<Button>().onClick.Invoke();
            var messageInput = Child(conversation, "Input");
            Assert.That(EventSystem.current!.currentSelectedGameObject, Is.EqualTo(messageInput),
                "opening chat from its message handle must select the input, not Close");
            yield return null;
            Assert.That(chat.activeSelf, Is.True);
            yield return null;
            Assert.That(view.IsInputFocused, Is.True,
                "opening chat from its message handle must focus the input");
            // Re-applying the open drawer (a host-menu attach, a drawer reconfigure)
            // must not move selection to Close or the rewards handle and drop the
            // player's typing.
            view.ConfigureDrawers(true, true, true, false);
            yield return null;
            Assert.That(EventSystem.current!.currentSelectedGameObject, Is.EqualTo(messageInput));
            Assert.That(view.IsInputFocused, Is.True,
                "re-applying the open chat drawer must keep the message input focused");
            chatCloseButton.onClick.Invoke();
            yield return null;

            view.ShowSignIn(DemoSignIn.SignedOut);
            view.SetRewardsOpen(true);
            yield return null;

            AssertOpaque(rewards);
            Assert.That(rewards.activeSelf, Is.True);
            var rewardsGate = Child(rewards, "Haiven Rewards Sign In Dialog");
            Assert.That(rewardsGate.activeInHierarchy, Is.True,
                "a signed-out rewards drawer shows the Haiven account gate");
            Assert.That(Child(rewards, "Haiven Rewards Content").activeInHierarchy, Is.False,
                "reward names and inventory stay hidden until sign-in");
            Assert.That(Child(rewardsGate, "Haiven Rewards Sign In Title").GetComponent<Text>().text,
                Is.EqualTo("CLAIM HAIVEN REWARDS"));
            Assert.That(Child(rewardsGate, "Haiven Rewards Sign In Message").GetComponent<Text>().text,
                Does.Contain("already claimed"));
            var rewardsSignIn = Child(rewardsGate, "Haiven Rewards Sign In").GetComponent<Button>();
            Assert.That(rewardsSignIn.GetComponentInChildren<Text>(true).text, Is.EqualTo("Sign in or register"));
            var signIns = 0;
            view.SignInRequested += () => signIns++;
            rewardsSignIn.onClick.Invoke();
            view.ShowSignIn(DemoSignIn.Waiting);
            Assert.That(signIns, Is.EqualTo(1), "the rewards gate starts the same Haiven sign-in as chat");
            Assert.That(accountGate.activeSelf, Is.False,
                "the sign-in click must not flash the chat join dialog");
            Assert.That(rewardsGate.activeSelf, Is.True,
                "the rewards gate stays put while the browser opens");
            Assert.That(Child(rewardsGate, "Haiven Rewards Sign In Title").GetComponent<Text>().text,
                Is.EqualTo("CLAIM HAIVEN REWARDS"),
                "the sign-in click must not flash an intermediate waiting screen");
            view.ShowSignIn(DemoSignIn.SignedIn);
            yield return null;
            Assert.That(view.IsInputFocused, Is.False,
                "a sign-in from the rewards drawer must not pull focus into closed chat");
            Assert.That(rewardsGate.activeInHierarchy, Is.False);
            Assert.That(Child(rewards, "Haiven Rewards Content").activeInHierarchy, Is.True,
                "reward rows return after sign-in");

            AssertOpaque(rewards);
            Assert.That(rewards.activeSelf, Is.True);
            Assert.That(rewardsComponent.transform.GetSiblingIndex(),
                Is.EqualTo(rewardsComponent.transform.parent.childCount - 1),
                "the open rewards component must render above the MOTD and persistent edge controls");
            Assert.That(rewardsHandle.activeSelf, Is.True,
                "opening Rewards and Inventory must keep its single handle available");
            Assert.That(rewards.GetComponentsInChildren<Button>(true)
                .Select(button => button.gameObject.name), Does.Not.Contain("Haiven Sign In"));
            var rewardsClose = Child(rewards, "Haiven Rewards Close").GetComponent<Button>();
            Assert.That(rewardsClose, Is.Not.Null, "rewards drawer needs an explicit close-button affordance");
            var rewardsCloseButton = rewardsClose!;
            AssertBossRoomButton(rewardsCloseButton.gameObject);
            var claimButton = Child(rewards, "Haiven Claim Reward");
            AssertBossRoomButton(claimButton);
            var claimRect = claimButton.GetComponent<RectTransform>();
            var closeRect = rewardsCloseButton.GetComponent<RectTransform>();
            Assert.That(closeRect.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeRect.anchorMax, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeRect.pivot, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeRect.anchoredPosition.x, Is.EqualTo(claimRect.anchoredPosition.x).Within(0.01f),
                "Close should share the claim reward button's right edge");
            Assert.That(closeRect.sizeDelta, Is.EqualTo(claimRect.sizeDelta),
                "Close should use the claim reward button's row size");
            Assert.That(closeRect.anchoredPosition.y - closeRect.sizeDelta.y - claimRect.anchoredPosition.y,
                Is.EqualTo(8f).Within(0.01f),
                "Close should keep the claim column's 8px row gap");
            var primaryClaim = claimButton.GetComponent<Button>();
            var weekend = Child(rewards, "Haiven Claim Reward Weekend Cache");
            var supply = Child(rewards, "Haiven Claim Reward Supply Drop");
            Assert.That(weekend, Is.Not.Null, "the dialog needs an unclaimed weekend reward");
            Assert.That(supply, Is.Not.Null, "the dialog needs an unclaimed supply reward");
            Assert.That(primaryClaim.interactable, Is.True);
            Assert.That(weekend.GetComponent<Button>().interactable, Is.True);
            Assert.That(supply.GetComponent<Button>().interactable, Is.True);
            string? requestedRef = null;
            view.RewardOfferRequested += rewardRef => requestedRef = rewardRef;
            view.ShowRewardOffers(new[]
            {
                new DemoRewardOffer("bossroom/friday-drop", "Friday Drop", "ALREADY CLAIMED", false),
                new DemoRewardOffer(DemoRewardCatalog.WeekendCacheRef, "Weekend Cache", "READY TO CLAIM", true),
                new DemoRewardOffer(DemoRewardCatalog.SupplyDropRef, "Supply Drop", "CLAIMED", false),
            });
            Assert.That(Child(rewards, "Haiven Reward Status").GetComponent<Text>().text,
                Is.EqualTo("Friday Drop · ALREADY CLAIMED"));
            Assert.That(Child(rewards, "Haiven Claim Reward").activeSelf, Is.False,
                "a claimed reward has no button");
            Assert.That(Child(rewards, "Haiven Claimed Label").activeInHierarchy, Is.True);
            Assert.That(Child(rewards, "Haiven Claimed Label").GetComponent<Text>().text, Is.EqualTo("Claimed"));
            Assert.That(weekend.activeSelf, Is.True, "an unclaimed reward keeps its button");
            Assert.That(weekend.GetComponent<Button>().interactable, Is.True);
            Assert.That(weekend.GetComponentInChildren<Text>(true).text, Is.EqualTo("Claim reward"));
            weekend.GetComponent<Button>().onClick.Invoke();
            Assert.That(signIns, Is.EqualTo(1), "a signed-in claim does not start sign-in again");
            Assert.That(requestedRef, Is.EqualTo(DemoRewardCatalog.WeekendCacheRef));
            Assert.That(Child(rewards, "Haiven Claimed Label Weekend Cache").activeSelf, Is.False);
            Assert.That(Child(rewards, "Haiven Reward Status Weekend Cache").GetComponent<Text>().text,
                Is.EqualTo("Weekend Cache · READY TO CLAIM"));
            Assert.That(supply.activeSelf, Is.False, "a claimed extra reward has no button");
            Assert.That(Child(rewards, "Haiven Claimed Label Supply Drop").activeInHierarchy, Is.True);
            Assert.That(Child(rewards, "Haiven Claimed Label Supply Drop").GetComponent<Text>().text,
                Is.EqualTo("Claimed"));
            var unclaimedMark = Child(rewardsHandle, "Rewards Unclaimed Subicon");
            Assert.That(unclaimedMark.activeSelf, Is.False,
                "the unclaimed mark stays off while the rewards drawer is open");
            Assert.That(rewardsCloseButton.GetComponentInChildren<Text>(true).text, Is.EqualTo("Close"));
            Assert.That(rewardsCloseButton.gameObject.activeInHierarchy, Is.True);
            rewardsCloseButton.onClick.Invoke();
            yield return null;
            Assert.That(rewards.activeSelf, Is.False, "rewards close button did not dismiss its drawer");
            Assert.That(rewardsHandle.activeSelf, Is.True);
            Assert.That(unclaimedMark.activeSelf, Is.True,
                "a closed drawer shows the unclaimed mark while Weekend Cache can be claimed");
            view.ShowRewardOffers(new[]
            {
                new DemoRewardOffer("bossroom/friday-drop", "Friday Drop", "CLAIMED", false),
                new DemoRewardOffer(DemoRewardCatalog.WeekendCacheRef, "Weekend Cache", "CLAIMED", false),
                new DemoRewardOffer(DemoRewardCatalog.SupplyDropRef, "Supply Drop", "CLAIMED", false),
            });
            Assert.That(unclaimedMark.activeSelf, Is.False,
                "the unclaimed mark hides when every reward is claimed");

            var rewardsRect = rewardsHandle.GetComponent<RectTransform>();
            var motdRect = Child(view, "Haiven MOTD Handle").GetComponent<RectTransform>();
            var chatRect = Child(view, "Haiven Chat Handle").GetComponent<RectTransform>();
            view.ShowMotd(DemoMotdPresentation.Failure());
            yield return null;
            Assert.That(rewardsRect.anchoredPosition.y, Is.EqualTo(-520f / 3f).Within(0.0001f));
            Assert.That(motdRect.anchoredPosition.y,
                Is.EqualTo(rewardsRect.anchoredPosition.y - rewardsRect.sizeDelta.y).Within(0.0001f));
            Assert.That(chatRect.anchoredPosition.y,
                Is.EqualTo(motdRect.anchoredPosition.y - motdRect.sizeDelta.y).Within(0.0001f),
                "the three independent handles must pack without a dead rail slot");
        }

        [UnityTest]
        public IEnumerator ReturnThatFocusesChatSubmitsToTheInputNotTheCloseButton()
        {
            var view = CreateView();
            view.ConfigureDrawers(true, true, false, false);
            view.ShowSignIn(DemoSignIn.SignedIn);
            yield return null;

            var chat = Child(Child(view, "Haiven Chat Component"), "Haiven Chat");
            var input = Child(chat, "Haiven Chat Conversation").GetComponentInChildren<InputField>(true);
            var eventSystem = EventSystem.current;
            Assert.That(eventSystem, Is.Not.Null);

            // The Return hotkey opens and focuses chat. Return is also the input
            // module's Submit button, which it delivers to the selected object
            // later in the same frame.
            view.FocusInput();
            Assert.That(chat.activeSelf, Is.True);
            Assert.That(eventSystem!.currentSelectedGameObject, Is.EqualTo(input.gameObject),
                "focusing chat must leave the message input selected, not the Close button");
            ExecuteEvents.Execute(
                eventSystem.currentSelectedGameObject,
                new BaseEventData(eventSystem),
                ExecuteEvents.submitHandler);
            Assert.That(chat.activeSelf, Is.True,
                "the Return that opened chat must not also press its Close button");
            yield return null;
            Assert.That(view.IsInputFocused, Is.True,
                "the message input must accept typing after the Return hotkey");
        }

        [UnityTest]
        public IEnumerator ChatDraftKeepsItsTextWhenFocusReturnsFromRewards()
        {
            var view = CreateView();
            view.ConfigureDrawers(true, true, false, false);
            view.ShowSignIn(DemoSignIn.SignedIn);
            yield return null;

            var chat = Child(Child(view, "Haiven Chat Component"), "Haiven Chat");
            var input = Child(chat, "Haiven Chat Conversation").GetComponentInChildren<InputField>(true);
            view.FocusInput();
            yield return null;
            yield return null;
            Assert.That(view.IsInputFocused, Is.True);
            const string draft = "hey first";
            input.text = draft;

            view.SetRewardsOpen(true);
            yield return null;
            Assert.That(view.IsInputFocused, Is.False, "the open rewards drawer takes selection");
            Child(Child(view, "Haiven Rewards Component"), "Haiven Rewards Close")
                .GetComponent<Button>().onClick.Invoke();
            yield return null;
            yield return null;
            Assert.That(view.IsInputFocused, Is.True, "closing rewards hands focus back to open chat");
            Assert.That(input.text, Is.EqualTo(draft), "returning focus must keep the unsent draft");
            Assert.That(input.selectionAnchorPosition, Is.EqualTo(input.selectionFocusPosition),
                "returning focus must not select the draft, or the next keystroke overwrites it");
            Assert.That(input.caretPosition, Is.EqualTo(draft.Length),
                "returning focus puts the caret at the end of the draft");

            view.SetChatOpen(false);
            yield return null;
            view.FocusInput();
            yield return null;
            yield return null;
            Assert.That(view.IsInputFocused, Is.True, "reopening chat focuses the input");
            Assert.That(input.text, Is.EqualTo(draft));
            Assert.That(input.selectionAnchorPosition, Is.EqualTo(input.selectionFocusPosition),
                "reopening chat must not select the draft either");
            Assert.That(input.caretPosition, Is.EqualTo(draft.Length));
        }

        [UnityTest]
        public IEnumerator ChatWindowFitsASenderNameAndMessageOnOneLine()
        {
            var view = CreateView();
            view.ConfigureDrawers(true, true, true, false);
            view.ShowSignIn(DemoSignIn.SignedIn);
            yield return null;

            var chat = Child(Child(view, "Haiven Chat Component"), "Haiven Chat");
            var chatRect = chat.GetComponent<RectTransform>();
            Assert.That(chatRect.sizeDelta, Is.EqualTo(new Vector2(680, 380)),
                "the chat window should be wide enough for a name and message");
            AssertOpaque(chat);
            // The frame is the shared bronze dialog rim (AddDialogChrome), which
            // replaced the per-panel accent bars; there is no separate accent child.
            AssertDialogChrome(chat, "Haiven Chat");
            var log = Child(chat, "Haiven Chat Log").GetComponent<Text>();
            Assert.That(log.fontSize, Is.EqualTo(16),
                "chat body text should stay at the readable floor instead of overflowing the window");
            Assert.That(log.horizontalOverflow, Is.EqualTo(HorizontalWrapMode.Wrap));
            Assert.That(log.verticalOverflow, Is.EqualTo(VerticalWrapMode.Truncate));
            Assert.That(log.supportRichText, Is.False,
                "player text must not be able to resize itself with rich-text tags");
            log.text = "ArchmageThunderhold: the boss dropped a legendary sword, meet at the gate";
            Canvas.ForceUpdateCanvases();
            yield return null;
            Assert.That(log.preferredWidth, Is.GreaterThan(400f),
                "the sample line should measure against the built-in font");
            Assert.That(log.preferredWidth, Is.LessThanOrEqualTo(log.rectTransform.rect.width + 0.5f),
                "the front of a chat line (sender name and message) must fit without wrapping");
        }

        [UnityTest]
        public IEnumerator MotdStartsCollapsedAndExpandsWithoutOverlappingRewards()
        {
            var view = CreateView();
            view.ConfigureDrawers(true, true, false, false);
            var motd = Child(view, "Haiven Message of the Day");
            var motdHandle = Child(view, "Haiven MOTD Handle");
            var motdLabel = Child(motdHandle, "MOTD Handle Label").GetComponent<Text>();
            var unreadIndicator = Child(motdHandle, "MOTD Unread Subicon");
            var frameGold = Child(motdHandle, "MOTD Frame Gold Horizontal");
            var frameFill = Child(motdHandle, "MOTD Frame Fill Horizontal");
            var unreadFill = Child(unreadIndicator, "MOTD Unread Fill");
            var rewardsComponent = Child(view, "Haiven Rewards Component");
            var rewards = Child(rewardsComponent, "Haiven Rewards and Inventory");
            Assert.That(rewards.transform.parent, Is.EqualTo(rewardsComponent.transform),
                "the visual regression needs the production component hierarchy");
            Assert.That(rewardsComponent.transform.parent, Is.EqualTo(motd.transform.parent),
                "the rewards component and MOTD must compete at the same render layer");
            Assert.That(motdHandle.activeSelf, Is.False,
                "the MOTD handle should remain hidden until a presentation exists");
            var chatHandleRect = Child(view, "Haiven Chat Handle").GetComponent<RectTransform>();
            Assert.That(chatHandleRect.anchoredPosition.x, Is.EqualTo(-100f / 3f).Within(0.0001f));
            Assert.That(chatHandleRect.anchoredPosition.y,
                Is.EqualTo(-240f).Within(0.0001f),
                "without a MOTD, Chat should pack below Rewards and Inventory");

            var presentation = DemoMotdPresentation.Present(
                1,
                "friday-demo",
                "Friday briefing",
                "Double rewards are live.",
                "https://haiven.ai/news",
                null);
            DemoMotdPresentation? actionRequested = null;
            view.MotdActionRequested += requested => actionRequested = requested;
            view.ShowMotd(presentation);
            Canvas.ForceUpdateCanvases();
            yield return null;

            Assert.That(motd.activeSelf, Is.False, "MOTD content must not cover the game by default");
            Assert.That(motdHandle.activeSelf, Is.True, "a collapsed MOTD needs an edge affordance");
            Assert.That(unreadIndicator.activeSelf, Is.True, "a new MOTD needs an unread indicator");
            Assert.That(motdHandle.GetComponentsInChildren<Text>(true).Select(label => label.text),
                Does.Contain("MOTD"), "the collapsed affordance must not look like the chat control");
            Assert.That(motdLabel, Is.Not.Null);
            Assert.That(motdLabel!.alignment, Is.EqualTo(TextAnchor.MiddleCenter));
            Assert.That(unreadIndicator.GetComponent<Image>(), Is.Not.Null,
                "the unread state should be a visual subicon rather than text");
            Assert.That(motdHandle.GetComponent<Image>().color.a, Is.LessThanOrEqualTo(0.01f),
                "the handle should use clipped frame layers instead of a flat rectangular backing");
            AssertBossRoomColor(frameGold.GetComponent<Image>().color, 0.898f, 0.741f, 0.161f,
                "the handle frame should use Boss Room's muted sprite-sampled gold edge");
            AssertBossRoomColor(frameFill.GetComponent<Image>().color, 0.071f, 0.208f, 0.341f,
                "the handle frame should use Boss Room's sprite-sampled navy fill");
            Assert.That(motdLabel.color, Is.EqualTo(frameGold.GetComponent<Image>().color),
                "the MOTD label should use the same gold as the frame");
            AssertBossRoomColor(unreadFill.GetComponent<Image>().color, 0.94f, 0.27f, 0.08f,
                "the unread state should remain distinct inside the gold badge");
            Assert.That(frameGold.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(58, 52)),
                "the crossing frame bars should leave clipped Boss Room-style corners");
            Assert.That(frameFill.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(52, 44)),
                "the blue fill should remain inset from the heavy frame");
            Assert.That(unreadIndicator.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(14, 14)),
                "the unread badge should be legible without overtaking the handle");
            var handleRect = motdHandle.GetComponent<RectTransform>();
            Assert.That(handleRect.anchorMin, Is.EqualTo(Vector2.one));
            Assert.That(handleRect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(handleRect.pivot, Is.EqualTo(Vector2.one));
            var canvasScaler = view.GetComponentInParent<CanvasScaler>();
            Assert.That(canvasScaler.referenceResolution, Is.EqualTo(new Vector2(1280, 720)));
            Assert.That(canvasScaler.matchWidthOrHeight, Is.EqualTo(0.506f).Within(0.0001f),
                "the Haiven rail must use the stock canvas' scaling interpolation");
            Assert.That(handleRect.anchoredPosition.x, Is.EqualTo(-100f / 3f).Within(0.0001f));
            Assert.That(handleRect.anchoredPosition.y, Is.EqualTo(-240f).Within(0.0001f),
                "MOTD should pack below Rewards and Inventory");
            Assert.That(handleRect.sizeDelta.x, Is.EqualTo(200f / 3f).Within(0.0001f));
            Assert.That(handleRect.sizeDelta.y, Is.EqualTo(200f / 3f).Within(0.0001f),
                "the scaled MOTD hit target should render at the stock controls' 100-pixel size");
            Assert.That(chatHandleRect.anchorMin, Is.EqualTo(Vector2.one));
            Assert.That(chatHandleRect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(chatHandleRect.pivot, Is.EqualTo(Vector2.one));
            Assert.That(chatHandleRect.anchoredPosition.x, Is.EqualTo(handleRect.anchoredPosition.x));
            Assert.That(chatHandleRect.anchoredPosition.x, Is.EqualTo(-100f / 3f).Within(0.0001f));
            Assert.That(chatHandleRect.anchoredPosition.y,
                Is.EqualTo(-920f / 3f).Within(0.0001f),
                "the independent chat handle should occupy the rail slot below MOTD");
            Assert.That(chatHandleRect.sizeDelta, Is.EqualTo(handleRect.sizeDelta));
            var actualRailGap = Mathf.Abs(
                chatHandleRect.anchoredPosition.y - handleRect.anchoredPosition.y) - handleRect.sizeDelta.y;
            Assert.That(actualRailGap, Is.EqualTo(0f).Within(0.0001f),
                "MOTD and Chat should pack without a dead rail slot");

            motdHandle.GetComponent<Button>().onClick.Invoke();
            Canvas.ForceUpdateCanvases();
            yield return null;

            var motdImage = motd.GetComponent<Image>();
            Assert.That(motdImage, Is.Not.Null, "MOTD needs a readable background surface");
            Assert.That(motdImage!.color.a, Is.EqualTo(0.94f).Within(0.001f),
                "MOTD surface must preserve contrast over the host menu art");
            Assert.That(motdImage.color.r, Is.EqualTo(9f / 255f).Within(0.001f),
                "MOTD fill should use Boss Room's dialog brown, not a blue scrim");
            Assert.That(motdImage.color.g, Is.EqualTo(6f / 255f).Within(0.001f));
            Assert.That(motdImage.color.b, Is.EqualTo(1f / 255f).Within(0.001f));
            AssertDialogChrome(motd, "Haiven Message of the Day", opaqueFill: false);
            var motdTitle = motd.GetComponentsInChildren<Text>(true)
                .Single(label => label.text == "Friday briefing");
            Assert.That(motdTitle.color, Is.EqualTo(Color.white),
                "MOTD titles should use Boss Room's white popup type, not cyan");
            Assert.That(motdTitle.fontStyle, Is.EqualTo(FontStyle.Bold));
            AssertBossRoomButton(Child(motd, "MOTD Action"));
            Assert.That(motd.activeSelf, Is.True);
            Assert.That(motdHandle.activeSelf, Is.True,
                "opening the MOTD from its rail handle must keep that handle available");
            Assert.That(unreadIndicator.activeSelf, Is.False, "opening the MOTD marks it as read");
            var motdRect = motd.GetComponent<RectTransform>();
            Assert.That(motdRect.anchorMin, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(motdRect.anchorMax, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(motdRect.sizeDelta.x, Is.LessThanOrEqualTo(520));
            Assert.That(motdRect.sizeDelta.y, Is.LessThanOrEqualTo(144),
                "the compact briefing must not descend into the Boss Room title");
            AssertText(motd, "Friday briefing");
            AssertText(motd, "Double rewards are live.");
            var text = motd.GetComponentsInChildren<Text>(true);
            Assert.That(text.Single(label => label.text == "Friday briefing").fontSize, Is.LessThanOrEqualTo(22));
            Assert.That(text.Single(label => label.text == "Double rewards are live.").fontSize,
                Is.LessThanOrEqualTo(18));
            Assert.That(Child(motd, "MOTD Action").activeSelf, Is.True);
            Assert.That(Child(motd, "MOTD Dismiss").activeSelf, Is.True);
            Child(motd, "MOTD Action").GetComponent<Button>().onClick.Invoke();
            Assert.That(actionRequested, Is.SameAs(presentation),
                "the expanded action must preserve the rendered presentation");

            Child(motd, "MOTD Collapse").GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(motd.activeSelf, Is.False);
            Assert.That(motdHandle.activeSelf, Is.True);
            Assert.That(unreadIndicator.activeSelf, Is.False,
                "collapsing a read MOTD must not restore the NEW indicator");

            view.ShowMotd(presentation);
            yield return null;
            Assert.That(unreadIndicator.activeSelf, Is.False,
                "rerendering the same message must not mark read content as fresh");

            view.ShowMotd(DemoMotdPresentation.Absent());
            yield return null;
            Assert.That(motd.activeSelf, Is.False);
            Assert.That(motdHandle.activeSelf, Is.False,
                "a true no-message absent state must hide the MOTD handle");
            Assert.That(unreadIndicator.activeSelf, Is.False);
            Assert.That(chatHandleRect.anchoredPosition.x, Is.EqualTo(-100f / 3f).Within(0.0001f));
            Assert.That(chatHandleRect.anchoredPosition.y,
                Is.EqualTo(-240f).Within(0.0001f),
                "Chat should reclaim the slot below Rewards and Inventory when the MOTD is absent");

            var dismissed = DemoMotdPresentation.Restore(
                2,
                DemoMotdPresentation.AbsentState,
                null,
                null,
                null,
                null,
                null,
                "friday-demo");
            view.ShowMotd(dismissed);
            yield return null;
            Assert.That(motdHandle.activeSelf, Is.True,
                "a dismissed absent state must retain the stable right-rail affordance");
            Assert.That(unreadIndicator.activeSelf, Is.False,
                "dismissed content must not look fresh or unread");
            Assert.That(chatHandleRect.anchoredPosition.x, Is.EqualTo(-100f / 3f).Within(0.0001f));
            Assert.That(chatHandleRect.anchoredPosition.y, Is.EqualTo(-920f / 3f).Within(0.0001f));
            motdHandle.GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(motd.activeSelf, Is.True);
            AssertText(motd, DemoMotdPresentation.FallbackTitle);
            AssertText(motd, "There is no message right now.");
            Child(motd, "MOTD Collapse").GetComponent<Button>().onClick.Invoke();
            yield return null;

            view.ShowMotd(DemoMotdPresentation.Present(
                2,
                "saturday-demo",
                "Saturday briefing",
                "A fresh message is available.",
                null,
                null));
            yield return null;
            Assert.That(unreadIndicator.activeSelf, Is.True,
                "a new message id should light the fresh-content indicator");

            motdHandle.GetComponent<Button>().onClick.Invoke();
            yield return null;

            view.SetChatOpen(true);
            view.SetRewardsOpen(true);
            yield return null;
            Assert.That(Child(view, "Haiven Chat").activeSelf, Is.True,
                "chat may remain open while the player uses rewards");
            Assert.That(rewards.activeSelf, Is.True);
            Assert.That(rewardsComponent.transform.GetSiblingIndex(),
                Is.EqualTo(rewardsComponent.transform.parent.childCount - 1),
                "an open rewards component must render above the persistent MOTD and right-rail controls");
            Assert.That(motd.activeSelf, Is.False, "MOTD and rewards must not overlap");

            view.ShowMotd(DemoMotdPresentation.Failure());
            yield return null;
            Assert.That(motd.activeSelf, Is.False, "an in-flight MOTD update must not cover open rewards");
            Assert.That(motdHandle.activeSelf, Is.True,
                "a non-blocking handle should keep fallback status available");
            Assert.That(unreadIndicator.activeSelf, Is.False,
                "loading failures are status updates, not unread announcements");

            view.SetRewardsOpen(false);
            Canvas.ForceUpdateCanvases();
            yield return null;
            Assert.That(rewards.activeSelf, Is.False);
            Assert.That(motd.activeSelf, Is.False,
                "closing rewards must not expand the latest MOTD over the game");
            Assert.That(Child(view, "Haiven Chat").activeSelf, Is.True,
                "updating the MOTD must not unexpectedly close chat");
            motdHandle.GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(motd.activeSelf, Is.True);
            AssertText(motd, OfflineTitle);
            AssertText(motd, OfflineBody);
            Assert.That(Child(motd, "MOTD Action").activeSelf, Is.False);
            Assert.That(Child(motd, "MOTD Dismiss").activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator InventorySummaryRendersLootCardsInsteadOfAList()
        {
            var view = CreateView();
            view.SetRewardsOpen(true);
            yield return null;

            var rewards = Child(Child(view, "Haiven Rewards Component"), "Haiven Rewards and Inventory");
            var inventory = Child(rewards, "Haiven Inventory Items").GetComponent<Text>();
            Assert.That(inventory.text, Is.EqualTo("Haiven items: none yet"));
            Assert.That(inventory.color.a, Is.EqualTo(0f),
                "the old bullet summary stays for the card parser and is not drawn");
            Assert.That(inventory.fontSize, Is.GreaterThanOrEqualTo(16));
            Assert.That(Child(rewards, "Haiven Loot Current Label").GetComponent<Text>().text, Is.EqualTo("Current"));
            Assert.That(rewards.GetComponentsInChildren<Transform>(true)
                .Any(item => item.name == "Haiven Loot Previous Label"), Is.False);
            Assert.That(Child(rewards, "Haiven Loot Empty"), Is.Not.Null);
            AssertText(Child(rewards, "Haiven Loot Empty"), "No rewards yet");
            Assert.That(rewards.GetComponentsInChildren<Transform>(true)
                .Count(item => item.name == "Haiven Loot Card"), Is.EqualTo(0));

            const string summary =
                "Haiven items:\n- bossroom/starter-blade\n- bossroom/friday-drop\n- skin/gold";
            view.ShowInventory(summary);
            yield return null;

            Assert.That(inventory.text, Is.EqualTo(summary),
                "the hidden summary still feeds the cards");
            Assert.That(rewards.GetComponentsInChildren<Transform>(true)
                .Any(item => item.name == "Haiven Loot Empty"), Is.False);
            var cards = rewards.GetComponentsInChildren<Transform>(true)
                .Where(item => item.name == "Haiven Loot Card")
                .Select(item => item.gameObject)
                .ToArray();
            Assert.That(cards.Length, Is.EqualTo(3));
            Assert.That(cards[0].GetComponentsInChildren<Text>(true).Select(label => label.text),
                Does.Contain("Starter Blade").And.Contain("bossroom/starter-blade").And.Contain("In inventory"));
            Assert.That(cards[1].GetComponentsInChildren<Text>(true).Select(label => label.text),
                Does.Contain("Friday Drop").And.Contain("bossroom/friday-drop"));
            Assert.That(cards[2].GetComponentsInChildren<Text>(true).Select(label => label.text),
                Does.Contain("Gold").And.Contain("skin/gold"));
            Assert.That(cards[0].GetComponentsInChildren<Text>(true).All(label => label.fontSize >= 16),
                Is.True, "loot card text must stay at the fallback minimum size");
            Assert.That(Child(rewards, "Haiven Claim Reward"), Is.Not.Null,
                "the card grid must not replace the claim action");
        }

        private HaivenBossRoomUGuiView CreateView()
        {
            host = new GameObject("Boss Room drawer visual test");
            if (EventSystem.current == null)
            {
                var eventSystem = new GameObject("Event System", typeof(EventSystem), typeof(StandaloneInputModule));
                eventSystem.transform.SetParent(host.transform, false);
            }
            var view = HaivenBossRoomUGuiView.CreateFallback(host.transform);
            var menu = (HaivenBossRoomMenuConfiguration)view.GetType()
                .GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view);
            menu.IntegrateWithHostMenu = false;
            return view;
        }

        private static GameObject Child(Component parent, string name) => Child(parent.gameObject, name);

        private static GameObject Child(GameObject parent, string name)
        {
            var child = parent.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(candidate => candidate.name == name);
            Assert.That(child, Is.Not.Null, $"missing child '{name}' under '{parent.name}'");
            return child!.gameObject;
        }

        private static void AssertDialogChrome(GameObject panel, string name, bool opaqueFill = true)
        {
            if (opaqueFill)
            {
                AssertBossRoomColor(panel.GetComponent<Image>().color, 9f / 255f, 6f / 255f, 1f / 255f,
                    name + " should use Boss Room's brown-black dialog fill, not a blue panel");
            }
            AssertBossRoomColor(Child(panel, name + " Border Top").GetComponent<Image>().color,
                81f / 255f, 48f / 255f, 11f / 255f,
                name + " should use Boss Room's bronze dialog rim");
            AssertBossRoomColor(Child(panel, name + " Border Left").GetComponent<Image>().color,
                47f / 255f, 28f / 255f, 7f / 255f,
                name + " side rim should use the dialog sprite's darker bronze");
            Assert.That(Child(panel, name + " Border Top").GetComponent<RectTransform>().sizeDelta.y,
                Is.EqualTo(6f).Within(0.001f),
                name + " rim should stay inside the existing content inset");
        }

        private static void AssertBossRoomButton(GameObject button)
        {
            AssertBossRoomColor(button.GetComponent<Image>().color, 20f / 255f, 36f / 255f, 52f / 255f,
                button.name + " should use Boss Room's navy button well");
            AssertBossRoomColor(Child(button, button.name + " Border Top").GetComponent<Image>().color,
                152f / 255f, 106f / 255f, 15f / 255f,
                button.name + " should use Boss Room's lit bronze button edge");
            AssertBossRoomColor(Child(button, button.name + " Border Left").GetComponent<Image>().color,
                121f / 255f, 68f / 255f, 0f / 255f,
                button.name + " should use Boss Room's darker bronze button side");
            var label = button.GetComponentInChildren<Text>(true);
            Assert.That(label, Is.Not.Null, button.name + " needs a caption");
            Assert.That(label!.color, Is.EqualTo(Color.white),
                button.name + " caption should be white, matching Boss Room buttons");
            Assert.That(label.fontStyle, Is.EqualTo(FontStyle.Bold),
                button.name + " caption should use Boss Room's heavy button type");
        }

        private static void AssertOpaque(GameObject panel)
        {
            var image = panel.GetComponent<Image>();
            Assert.That(image, Is.Not.Null, $"{panel.name} has no background image");
            Assert.That(image!.color.a, Is.EqualTo(1f).Within(0.001f),
                $"{panel.name} must stay readable over the game world");
        }

        private static void AssertBossRoomColor(Color actual, float red, float green, float blue, string message)
        {
            Assert.That(actual.r, Is.EqualTo(red).Within(0.001f), message);
            Assert.That(actual.g, Is.EqualTo(green).Within(0.001f), message);
            Assert.That(actual.b, Is.EqualTo(blue).Within(0.001f), message);
            Assert.That(actual.a, Is.EqualTo(1f).Within(0.001f), message);
        }

        private static void AssertText(GameObject root, string expected)
        {
            var labels = root.GetComponentsInChildren<Text>(true).Select(label => label.text);
            Assert.That(labels, Does.Contain(expected), $"{root.name} did not render '{expected}'");
        }
    }
}
