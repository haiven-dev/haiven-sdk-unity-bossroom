#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Haiven.Demo.BossRoom
{
    /// <summary>What one boss defeat did with the configured reward.</summary>
    public enum DemoVictoryClaim
    {
        /// <summary>This session already handled a victory, or none is waiting for sign-in.</summary>
        None,

        /// <summary>Signed out: no claim. The claim waits for the player to sign in.</summary>
        SignInRequired,

        /// <summary>The reward was already claimed, or a claim is in flight: no second request.</summary>
        AlreadyClaimed,

        /// <summary>The reward claim was sent and inventory was refreshed.</summary>
        Claimed,
    }

    /// <summary>
    /// Claims the configured reward once per session when Boss Room's boss is
    /// defeated. The claim, its status, and the inventory refresh are
    /// <see cref="HaivenDemoClient"/>'s existing reward path; this only decides
    /// whether to call it. Call from the game's main thread.
    /// </summary>
    public sealed class DemoVictoryReward
    {
        /// <summary>Boss Room's <c>WinState</c> value for a defeated boss.</summary>
        public const string WinStateWin = "Win";

        /// <summary>Boss Room's <c>WinState</c> value when every player fainted.</summary>
        public const string WinStateLoss = "Loss";

        private bool handled;

        /// <summary>A victory happened while signed out and claims on the next sign-in.</summary>
        public bool AwaitingSignIn { get; private set; }

        /// <summary>
        /// Boss Room's post-game <c>WinState</c> by name: true for Win, false for
        /// Loss, null while it is still Invalid (not yet synchronised).
        /// </summary>
        public static bool? IsVictory(string? winState)
        {
            if (winState == WinStateWin) return true;
            if (winState == WinStateLoss) return false;
            return null;
        }

        /// <summary>
        /// The boss was defeated. Signed out (<paramref name="client"/> null),
        /// nothing is claimed until <see cref="SignedInAsync"/>. Otherwise the
        /// configured reward is claimed once for this session.
        /// </summary>
        public Task<DemoVictoryClaim> BossDefeatedAsync(
            HaivenDemoClient? client,
            CancellationToken cancellationToken = default)
        {
            if (handled) return Task.FromResult(DemoVictoryClaim.None);
            if (client == null)
            {
                AwaitingSignIn = true;
                return Task.FromResult(DemoVictoryClaim.SignInRequired);
            }
            return ClaimAsync(client, cancellationToken);
        }

        /// <summary>A session started. Claims a victory that happened while signed out.</summary>
        public Task<DemoVictoryClaim> SignedInAsync(
            HaivenDemoClient client,
            CancellationToken cancellationToken = default)
        {
            if (handled || !AwaitingSignIn) return Task.FromResult(DemoVictoryClaim.None);
            return ClaimAsync(client, cancellationToken);
        }

        private async Task<DemoVictoryClaim> ClaimAsync(
            HaivenDemoClient client,
            CancellationToken cancellationToken)
        {
            handled = true;
            AwaitingSignIn = false;
            // RewardOffers lists the configured ref first.
            var offers = client.RewardOffers;
            var title = offers.Count > 0 ? offers[0].Title : "reward";
            if (!client.CanClaimReward)
            {
                // Seeded as claimed this period, a finished claim, or one in flight.
                client.Log.Note("Boss defeated: " + title + " " + client.RewardStatus.ToLowerInvariant());
                return DemoVictoryClaim.AlreadyClaimed;
            }

            client.Log.Note("Boss defeated: claiming " + title);
            await client.ClaimRewardAsync(cancellationToken).ConfigureAwait(false);
            // entitlements.updated normally delivers the grant; one read covers a
            // missed push. Items already shown are not added twice.
            await client.RefreshEntitlementsAsync().ConfigureAwait(false);
            return DemoVictoryClaim.Claimed;
        }
    }
}
