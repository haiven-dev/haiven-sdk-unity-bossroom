#nullable enable
using System;

namespace Haiven.Sdk
{
    /// <summary>
    /// Minimal chat presentation boundary for a game-owned UI. It deliberately
    /// contains no uGUI, UI Toolkit, scene, or demo controller types.
    /// </summary>
    public interface IHaivenChatView
    {
        /// <summary>Raised only when the player deliberately submits chat text.</summary>
        event Action<string>? Submitted;

        /// <summary>Whether keyboard focus is already inside the chat input.</summary>
        bool IsInputFocused { get; }

        /// <summary>Moves keyboard focus to the chat input.</summary>
        void FocusInput();

        /// <summary>Clears and releases the chat input after submission.</summary>
        void ClearInput();

        /// <summary>Replaces the rendered bounded chat/status transcript.</summary>
        void ShowLog(string text);
    }

    /// <summary>
    /// Minimal rewards presentation boundary for a game-owned UI. Games decide
    /// whether the content appears as inventory, a toast, or a drawer.
    /// </summary>
    public interface IHaivenRewardsView
    {
        /// <summary>Replaces the rendered active-rewards summary.</summary>
        void ShowRewards(string text);
    }

    /// <summary>Optional status surface for connection and chat moderation state.</summary>
    public interface IHaivenServiceStatusView
    {
        void ShowConnectionStatus(string text);

        void ShowModerationStatus(string text);
    }

    /// <summary>Optional presenter action for claiming one configured live reward.</summary>
    public interface IHaivenRewardActionView
    {
        event Action? RewardRequested;

        void ShowRewardStatus(string text, bool canClaim);
    }

    /// <summary>Host policy applied when a drawer is first bound.</summary>
    public readonly struct HaivenDrawerConfiguration
    {
        public HaivenDrawerConfiguration(bool showHandle, bool startsOpen)
        {
            ShowHandle = showHandle;
            StartsOpen = startsOpen;
        }

        /// <summary>Whether Haiven should expose its own open/close handle.</summary>
        public bool ShowHandle { get; }

        /// <summary>The initial open state selected by the host game.</summary>
        public bool StartsOpen { get; }
    }

    /// <summary>Complete renderer-neutral state for one drawer.</summary>
    public readonly struct HaivenDrawerPresentation : IEquatable<HaivenDrawerPresentation>
    {
        public HaivenDrawerPresentation(bool isOpen, bool showHandle)
        {
            IsOpen = isOpen;
            ShowHandle = showHandle;
        }

        public bool IsOpen { get; }

        public bool ShowHandle { get; }

        public bool Equals(HaivenDrawerPresentation other) =>
            IsOpen == other.IsOpen && ShowHandle == other.ShowHandle;

        public override bool Equals(object? value) =>
            value is HaivenDrawerPresentation other && Equals(other);

        public override int GetHashCode() => (IsOpen ? 1 : 0) | (ShowHandle ? 2 : 0);

        public static bool operator ==(
            HaivenDrawerPresentation left,
            HaivenDrawerPresentation right) => left.Equals(right);

        public static bool operator !=(
            HaivenDrawerPresentation left,
            HaivenDrawerPresentation right) => !left.Equals(right);
    }

    /// <summary>
    /// State owner for one independently controlled drawer. A uGUI, UI Toolkit,
    /// or custom renderer applies <see cref="Current"/> and listens to
    /// <see cref="Changed"/>; chat and rewards can each use their own instance.
    /// </summary>
    public sealed class HaivenDrawerState
    {
        private HaivenDrawerPresentation current;

        public HaivenDrawerState()
            : this(new HaivenDrawerConfiguration(showHandle: true, startsOpen: false))
        {
        }

        public HaivenDrawerState(HaivenDrawerConfiguration configuration)
        {
            current = new HaivenDrawerPresentation(
                configuration.StartsOpen,
                configuration.ShowHandle);
        }

        /// <summary>Raised when either visibility or handle policy changes.</summary>
        public event Action<HaivenDrawerPresentation>? Changed;

        public HaivenDrawerPresentation Current => current;

        /// <summary>Reapplies host policy, including the configured initial state.</summary>
        public void Configure(HaivenDrawerConfiguration configuration) =>
            Apply(new HaivenDrawerPresentation(
                configuration.StartsOpen,
                configuration.ShowHandle));

        public void SetOpen(bool isOpen) =>
            Apply(new HaivenDrawerPresentation(isOpen, current.ShowHandle));

        public void Toggle() => SetOpen(!current.IsOpen);

        private void Apply(HaivenDrawerPresentation next)
        {
            if (next == current) return;
            current = next;
            Changed?.Invoke(next);
        }
    }
}
