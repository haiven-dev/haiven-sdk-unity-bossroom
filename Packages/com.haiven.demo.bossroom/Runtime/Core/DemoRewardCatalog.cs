#nullable enable
using System.Collections.Generic;

namespace Haiven.Demo.BossRoom
{
    /// <summary>One demo reward the player can claim once this subscription period.</summary>
    public readonly struct DemoRewardSpec
    {
        public DemoRewardSpec(string rewardRef, string title)
        {
            RewardRef = rewardRef;
            Title = title;
        }

        public string RewardRef { get; }

        public string Title { get; }
    }

    /// <summary>The claim state of one demo reward, ready for a row in the dialog.</summary>
    public sealed class DemoRewardOffer
    {
        public DemoRewardOffer(string rewardRef, string title, string status, bool canClaim)
        {
            RewardRef = rewardRef;
            Title = title;
            Status = status;
            CanClaim = canClaim;
        }

        public string RewardRef { get; }

        public string Title { get; }

        public string Status { get; }

        public bool CanClaim { get; }
    }

    /// <summary>
    /// The demo's claim rows. The configured ref stays first so the original
    /// Claim button keeps its contract. The other refs are new each period, so
    /// a player who already claimed Friday Drop still has something to claim.
    /// A claim does not need a catalog row.
    /// </summary>
    public static class DemoRewardCatalog
    {
        public const string WeekendCacheRef = "bossroom/weekend-cache";
        public const string SupplyDropRef = "bossroom/supply-drop";

        public static IReadOnlyList<DemoRewardSpec> For(string configuredRef)
        {
            var specs = new List<DemoRewardSpec>(3);
            Add(specs, configuredRef, TitleFor(configuredRef));
            Add(specs, WeekendCacheRef, "Weekend Cache");
            Add(specs, SupplyDropRef, "Supply Drop");
            return specs;
        }

        public static string TitleFor(string rewardRef)
        {
            switch (rewardRef)
            {
                case "friday-drop":
                case "bossroom/friday-drop":
                    return "Friday Drop";
                case WeekendCacheRef:
                    return "Weekend Cache";
                case SupplyDropRef:
                    return "Supply Drop";
                default:
                    return rewardRef;
            }
        }

        private static void Add(List<DemoRewardSpec> specs, string rewardRef, string title)
        {
            if (rewardRef.Length == 0) return;
            for (var i = 0; i < specs.Count; i++)
            {
                if (specs[i].RewardRef == rewardRef) return;
            }
            specs.Add(new DemoRewardSpec(rewardRef, title));
        }
    }
}
