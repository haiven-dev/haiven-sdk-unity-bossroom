#nullable enable

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// When chat opens signed out, the existing browser handoff starts at once.
    /// The join dialog is not an intermediate screen on that path. It remains
    /// only after a handoff has already stopped and the player needs the retry.
    /// </summary>
    internal static class DemoChatSignIn
    {
        public static bool StartsBrowserOnChatOpen(DemoSignInState state, bool canStart) =>
            state == DemoSignInState.SignedOut && canStart;

        public static bool ShowsJoinDialog(DemoSignInState state) =>
            state == DemoSignInState.Failed || state == DemoSignInState.Expired;
    }
}
