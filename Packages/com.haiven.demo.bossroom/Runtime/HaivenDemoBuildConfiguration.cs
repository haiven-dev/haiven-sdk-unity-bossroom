#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Haiven.Demo.BossRoom
{
    /// <summary>Public, build-time demo settings bundled by haiven-bossroom.</summary>
    internal static class HaivenDemoBuildConfiguration
    {
        internal const string ResourcePath = "Haiven/HaivenDemoConfiguration";

        internal static Func<string, string?> Reader(Func<string, string?> environment)
        {
            var bundled = Parse(Resources.Load<TextAsset>(ResourcePath)?.text ?? "");
            return name =>
            {
                var value = environment(name);
                return !string.IsNullOrWhiteSpace(value)
                    ? value
                    : bundled.TryGetValue(name, out var fallback) ? fallback : null;
            };
        }

        internal static IReadOnlyDictionary<string, string> Parse(string text)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = raw.IndexOf('=');
                if (separator <= 0) continue;
                found[raw.Substring(0, separator)] = raw.Substring(separator + 1);
            }
            return found;
        }
    }
}
