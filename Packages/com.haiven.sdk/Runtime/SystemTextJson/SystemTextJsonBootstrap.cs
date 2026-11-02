// Selects the System.Text.Json codec at startup, when a project opts into it.
//
// The SDK's default JSON codec is its own dependency-free reader and writer, so
// the package needs no System.Text.Json and nothing here compiles by default:
// this assembly's asmdef carries the define constraint HAIVEN_SDK_SYSTEM_TEXT_JSON,
// and Unity skips an assembly whose constraint is unmet, references and all.
//
// To opt in: add HAIVEN_SDK_SYSTEM_TEXT_JSON to Player Settings > Scripting
// Define Symbols, and supply Haiven.Sdk.SystemTextJson.dll with System.Text.Json
// and its dependencies as plugins. Selecting it in code instead
// (HaivenJson.Default = SystemTextJsonCodec.Instance) works without the define.

using Haiven.Sdk.Json.SystemTextJson;
using UnityEngine;

namespace Haiven.Sdk.Json.SystemTextJson.Unity
{
    internal static class SystemTextJsonBootstrap
    {
        // SubsystemRegistration runs before any scene loads, so the codec is in
        // place before a game's own Awake can open a session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install() => SystemTextJsonCodec.Install();
    }
}
