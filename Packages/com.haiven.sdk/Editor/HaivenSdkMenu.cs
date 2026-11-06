#nullable enable
using UnityEditor;
using UnityEngine;

namespace Haiven.Sdk.Editor
{
    /// <summary>
    /// Editor entry points for the Haiven SDK. Settings, diagnostics and the fake
    /// server land here as they are built; today it links the package documentation.
    /// </summary>
    public static class HaivenSdkMenu
    {
        /// <summary>The package README, the same page as package.json's documentationUrl.</summary>
        public const string DocumentationUrl =
            "https://github.com/haiven-dev/haiven-sdk-unity-bossroom/blob/cadcd898508410f1753d370f9d8f27d8e2a3f9e4/Packages/com.haiven.sdk/README.md";

        [MenuItem("Help/Haiven SDK/Documentation")]
        public static void OpenDocumentation() => Application.OpenURL(DocumentationUrl);
    }
}
