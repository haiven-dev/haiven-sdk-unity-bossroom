#nullable enable
using System;
using System.Collections.Generic;
using Haiven.Sdk;
using UnityEngine;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Presentation boundary for the Boss Room demo. Host projects can subclass
    /// this component and put it on the optional view prefab without changing
    /// the Haiven session, chat, or entitlement logic.
    /// </summary>
    public abstract class HaivenBossRoomView : MonoBehaviour,
        IHaivenChatView,
        IHaivenRewardsView,
        IHaivenServiceStatusView,
        IHaivenRewardActionView
    {
        /// <summary>Raised when the player deliberately submits chat text.</summary>
        public event Action<string>? Submitted;

        /// <summary>Raised when the player dismisses the currently presented MOTD.</summary>
        public event Action<DemoMotdPresentation>? MotdDismissed;

        /// <summary>Raised when the player requests the current MOTD HTTPS action.</summary>
        public event Action<DemoMotdPresentation>? MotdActionRequested;

        /// <summary>Raised when the player deliberately claims the configured live reward.</summary>
        public event Action? RewardRequested;

        /// <summary>
        /// Raised to start the browser handoff: chat opened while signed out,
        /// the player asked to sign in, or they retry after a stopped attempt.
        /// </summary>
        public event Action? SignInRequested;

        /// <summary>Whether keyboard focus is already inside the chat input.</summary>
        public abstract bool IsInputFocused { get; }

        /// <summary>
        /// Validates host-owned serialized state before the controller selects
        /// this view. Custom implementations are ready by default and can
        /// override this when they require their own bindings or resources.
        /// </summary>
        public virtual bool ValidateReadiness(out string error)
        {
            error = "";
            return true;
        }

        /// <summary>Moves keyboard focus to the chat input.</summary>
        public abstract void FocusInput();

        /// <summary>Clears and releases the chat input after submission.</summary>
        public abstract void ClearInput();

        /// <summary>
        /// Applies the demo drawer policy. Custom views can ignore the handle flags
        /// when the host game supplies its own controls, but should honor the initial
        /// open states.
        /// </summary>
        public virtual void ConfigureDrawers(
            bool showChatHandle,
            bool showRewardsHandle,
            bool chatStartsOpen,
            bool rewardsStartOpen)
        {
            SetChatOpen(chatStartsOpen);
            SetRewardsOpen(rewardsStartOpen);
        }

        /// <summary>Opens or closes the chat surface from game-owned UI.</summary>
        public virtual void SetChatOpen(bool isOpen) { }

        /// <summary>Opens or closes the live-rewards surface from game-owned UI.</summary>
        public virtual void SetRewardsOpen(bool isOpen) { }

        /// <summary>Renders the complete bounded chat/status transcript.</summary>
        public abstract void ShowLog(string text);

        /// <summary>Renders the complete active-item inventory summary.</summary>
        public abstract void ShowInventory(string text);

        /// <summary>
        /// Implements the reusable rewards boundary while preserving the original
        /// Boss Room inventory method for existing custom view subclasses.
        /// </summary>
        public virtual void ShowRewards(string text) => ShowInventory(text);

        /// <summary>Renders the connection/authentication state when the host exposes it.</summary>
        public virtual void ShowConnectionStatus(string text) { }

        /// <summary>Renders the latest moderation outcome when the host exposes it.</summary>
        public virtual void ShowModerationStatus(string text) { }

        /// <summary>Renders claim state and whether the configured reward can be claimed.</summary>
        public virtual void ShowRewardStatus(string text, bool canClaim) { }

        /// <summary>
        /// Renders one row per demo reward. A row whose
        /// <see cref="DemoRewardOffer.CanClaim"/> is false keeps its status visible
        /// and disables its button, except while the player is still signed out.
        /// </summary>
        public virtual void ShowRewardOffers(IReadOnlyList<DemoRewardOffer> offers) { }

        /// <summary>
        /// Renders the sign-in state: hide the control when
        /// <see cref="DemoSignInPresentation.ActionLabel"/> is null, and enable it
        /// only when <see cref="DemoSignInPresentation.CanStart"/>. Optional for
        /// custom views; a host can call <see cref="HaivenBossRoomDemo.SignIn"/>
        /// from its own menu instead.
        /// </summary>
        public virtual void ShowSignIn(DemoSignInPresentation presentation) { }

        /// <summary>Renders normalized MOTD state through host-owned controls.</summary>
        public abstract void ShowMotd(DemoMotdPresentation presentation);

        /// <summary>Notifies the controller of a designer-owned input action.</summary>
        protected void RaiseSubmitted(string text) => Submitted?.Invoke(text);

        /// <summary>Notifies the controller of a designer-owned dismiss action.</summary>
        protected void RaiseMotdDismissed(DemoMotdPresentation rendered) =>
            MotdDismissed?.Invoke(rendered);

        /// <summary>Delegates a designer-owned action button to the host controller.</summary>
        protected void RaiseMotdActionRequested(DemoMotdPresentation rendered) =>
            MotdActionRequested?.Invoke(rendered);

        /// <summary>Notifies the controller of a presenter-owned reward action.</summary>
        protected void RaiseRewardRequested() => RewardRequested?.Invoke();

        /// <summary>Notifies the controller that the player chose to sign in.</summary>
        protected void RaiseSignInRequested() => SignInRequested?.Invoke();
    }
}
