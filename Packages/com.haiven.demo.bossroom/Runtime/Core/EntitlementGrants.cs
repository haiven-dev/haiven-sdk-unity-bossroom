#nullable enable
using System.Collections.Generic;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    public static class EntitlementGrants
    {
        /// <summary>The entitlement kind that is an inventory item (haiven-api KindItem).</summary>
        public const string ItemKind = "item";

        /// <summary>The state of a held benefit (haiven-api StateActive).</summary>
        public const string ActiveState = "active";

        /// <summary>
        /// Active item entitlements not in <paramref name="alreadyGranted"/>, in
        /// response order and once each. <see cref="GrantedItem.ValidFrom"/> is
        /// haiven-api's grant time (min(granted_at)). Subscriptions and founder
        /// status are not items.
        /// </summary>
        public static List<GrantedItem> NewlyActiveItems(AccountEntitlementsResponse? response, ICollection<string> alreadyGranted)
        {
            var granted = new List<GrantedItem>();
            if (response?.Entitlements == null) return granted;
            foreach (var entitlement in response.Entitlements)
            {
                var code = entitlement.Code;
                if (string.IsNullOrEmpty(code) || entitlement.Kind != ItemKind || entitlement.State != ActiveState) continue;
                if (alreadyGranted.Contains(code!) || ContainsCode(granted, code!)) continue;
                granted.Add(new GrantedItem(code!, entitlement.ValidFrom));
            }
            return granted;
        }

        private static bool ContainsCode(List<GrantedItem> granted, string code)
        {
            foreach (var item in granted)
            {
                if (item.Code == code) return true;
            }
            return false;
        }
    }

    /// <summary>One newly active item and the grant time haiven-api exposes as validFrom.</summary>
    public readonly struct GrantedItem
    {
        public GrantedItem(string code, string? validFrom)
        {
            Code = code;
            ValidFrom = validFrom;
        }

        public string Code { get; }

        public string? ValidFrom { get; }
    }
}
