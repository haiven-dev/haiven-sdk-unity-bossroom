#nullable enable
using System;
using System.Collections.Generic;

namespace Haiven.Demo.BossRoom
{
    internal readonly struct LootCardModel
    {
        public LootCardModel(string code, string title)
        {
            Code = code;
            Title = title;
        }

        public string Code { get; }
        public string Title { get; }
    }

    /// <summary>
    /// Sample cards for the Boss Room inventory summary. Titles are display
    /// copy; the entitlement code stays on the card.
    /// Colors are the Park Dark tokens the current rewards card mock uses
    /// (surface, border, accent swatch, muted text, accent mark).
    /// </summary>
    internal static class LootInventoryCards
    {
        public const int SurfaceColor = 0x100F11;
        public const int BorderColor = 0x403E43;
        public const int SwatchColor = 0x313131;
        public const int MutedColor = 0xA09FA2;
        public const int MarkColor = 0xB4B4B4;
        public const string EmptyTitle = "No rewards yet";
        public const string EmptyBody =
            "When a game makes a reward available or confirms a claim, it will appear here.";
        public const string OwnedLabel = "In inventory";

        public static IReadOnlyList<LootCardModel> FromSummary(string? summary)
        {
            var cards = new List<LootCardModel>();
            if (string.IsNullOrEmpty(summary)) return cards;
            var lines = summary.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
                var code = line.Substring(2).Trim();
                if (code.Length == 0) continue;
                cards.Add(new LootCardModel(code, TitleFromCode(code)));
            }
            return cards;
        }

        private static string TitleFromCode(string code)
        {
            var slash = code.LastIndexOf('/');
            var segment = slash >= 0 && slash < code.Length - 1 ? code.Substring(slash + 1) : code;
            var words = segment.Replace('-', ' ').Replace('_', ' ').Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0) continue;
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            }
            var title = string.Join(" ", words).Trim();
            return title.Length == 0 ? code : title;
        }
    }
}
