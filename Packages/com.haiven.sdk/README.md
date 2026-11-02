# Haiven SDK for Unity

`com.haiven.sdk` — the Haiven client SDK as a UPM package.

## Installing

Add it by git URL in the Package Manager, or in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.haiven.sdk": "https://github.com/haiven-dev/haiven-sdk.git?path=sdks/unity/com.haiven.sdk"
  }
}
```

The minimum supported editor is Unity `2022.3`. Release evidence is pinned to
Unity `2022.3.62f2` with its matching WebGL module. Older editors may accept the
package's `netstandard2.1` surface, but they are outside the supported range.

The package is self-contained: `Runtime/Core` carries the current portable C#
core as generated source, including `BuiltInJsonCodec`. No managed DLL or NuGet
restore is required for the default install.

For `AnalyticsBatcher`, add `UnityAnalyticsScheduler` to a long-lived game
object and pass it as the batcher's `scheduler`. It uses ordinary tasks in the
editor and desktop/mobile players, and Unity coroutines in a WebGL player so
enqueue, flush, retry delays, and diagnostics do not depend on a thread pool.

`UnityRestApiTransport`, `HaivenIdentity`, and `UnityIdentityModal` provide a
validated MOTD/account-method starter modal over the generated REST and realtime
contracts. Linking is disabled unless the game injects an
`IIdentityLinkBrowserContext` that performs popup creation, POST, cookie
handling, and redirect in the same browser context. The package intentionally
does not claim a system-browser `OpenURL` call is that adapter; live provider
linking remains host integration work. Authenticated Unity REST requests refuse
redirects instead of forwarding a bearer outside the validated origin.

## Game-owned chat and rewards UI

`IHaivenChatView` and `IHaivenRewardsView` are deliberately renderer-neutral.
They let a game bind Haiven data to uGUI, UI Toolkit, or its own presentation
layer without depending on a demo controller, scene, or prefab. The Boss Room
demo is the first adapter: its existing `HaivenBossRoomView` implements both
contracts, so existing subclasses remain source-compatible.

`HaivenDrawerState` owns only the behavior shared by those renderers: open vs.
closed and whether the SDK-provided handle is visible. Create one state object
per independently controlled surface, apply `Current` when binding, and listen
to `Changed` after that. Keeping drawer state separate from chat/rewards content
means a host can replace the handle with its own hotkey or navigation without
reimplementing the data-facing contracts.

## Choosing a socket

The SDK takes an `IHaivenSocket` rather than opening one. Unity's supported
targets do not agree that sockets exist, so this is the one thing a host has to
supply:

| target | implementation |
|---|---|
| WebGL player | `WebGlSocket` — the browser's WebSocket, over the bundled `.jslib` |
| desktop / mobile IL2CPP, and the editor | `UnitySocket` — `System.Net.WebSockets`, events on the main thread |

`UnitySocket` delivers `Received` and `Closed` on Unity's main thread by
default, so a chat handler can touch the scene directly. The underlying
`SystemNetSocket` receives on a background thread, and a handler that touched
the scene from there would throw. Create it on the main thread (in `Awake`,
`Start` or a coroutine); created anywhere else it throws instead of silently
delivering off-thread. Events are posted, so they arrive in order on a later
frame. A host that marshals for itself can opt out with
`new UnitySocket(HaivenDispatch.Direct)`.

```csharp
var socket = new UnitySocket();            // on the main thread
socket.Received += frame => chatLog.text += frame;   // safe: main thread
await socket.ConnectAsync(new Uri(realtimeUrl));
```

`WebGlSocket` compiles only under `UNITY_WEBGL && !UNITY_EDITOR`. The editor is
excluded on purpose: it runs Mono against the desktop socket stack even when the
build target is WebGL, so including it there would exercise the one path that is
never shipped.

## Game login handoff

`HandoffCoordinator` opens Haiven's first-party `/connect/game` page, validates
the returned state, exchanges the one-use code with S256 PKCE, and gives the
host an opaque access token plus its absolute expiry. The game owns every
platform-sensitive boundary:

```csharp
var tokens = new MemoryHandoffTokenStore();
var coordinator = new HandoffCoordinator(
    new HandoffOptions(
        new Uri("https://app.haiven.gg/connect/game"),
        new Uri("https://api.haiven.gg/v0/auth/handoff/exchange"),
        "your-public-integration-id",
        new Uri("your-game://haiven/callback")),
    new UnityHandoffBrowser(),
    new MemoryHandoffTransactionStore(),
    tokens,
    new UnityHandoffHttpTransport(),
    new UnityHandoffDispatcher());

var receiver = gameObject.AddComponent<HandoffDeepLinkReceiver>();
receiver.Configure(coordinator);
coordinator.Completed += credential => StartGameSession(
    credential.AccessToken,
    credential.ExpiresAt);
coordinator.Failed += error => ShowLoginError(error.Message);
coordinator.BeginLogin();
```

Create `UnityHandoffDispatcher` on Unity's main thread. The receiver subscribes
to `Application.deepLinkActivated`; if the game was launched from a deep link,
the host should also pass the initial callback URL to `receiver.Receive(...)`.
For WebGL, deliver the configured web callback the same way from the host page.
`UnityHandoffHttpTransport` uses an explicit form body and `UnityWebRequest`, so
the exchange does not depend on `HttpClient`, threads, or a native socket stack.

The included transaction store is deliberately memory-only: the verifier is
one-use, is removed before exchange, and expires locally after 15 minutes. The
included token store also keeps the credential only in memory. A game that
needs persistence supplies `IHandoffTokenStore` backed by its platform's secure
credential store; do not put the bearer in `PlayerPrefs`. The handoff has no
client secret and returns no refresh token. On expiry, begin login again.

### Desktop loopback redirect

A desktop game with no registered custom scheme can receive the redirect on a
loopback port instead (RFC 8252 section 7.3). Haiven matches redirects
exactly, so register a fixed port with an IP literal, for example
`http://127.0.0.1:47615/haiven/callback`, never `localhost`.
`LoopbackHandoffListener` (in the vendored core) binds that port before the
browser opens, answers the matching `GET` with a static page that echoes
nothing, and hands the unparsed callback back to the coordinator:

```csharp
using (var listener = LoopbackHandoffListener.Start(options.RedirectUri))
{
    // BeginLogin, wait for the redirect, then CompleteAsync, on the caller's context.
    var credential = await coordinator.LoginAsync(listener, cancellationToken);
}
```

Await it from Unity's main thread so `UnityHandoffHttpTransport` runs there.
A busy port throws `HandoffException` from `Start`; cancelling the wait stops
the listener. Not available on WebGL, which has no sockets. The Boss Room demo
uses this path (`demos/bossroom`, "Sign in with Haiven").

## JSON: built in, or System.Text.Json by opt-in

The SDK carries its own JSON reader and writer, and every wire type has a
generated read and write method, so the package needs no JSON library and
IL2CPP never has to preserve members for a reflective serializer. That is the
default; there is nothing to configure.

System.Text.Json is a source-checkout-only opt-in, not part of the published
package. Run `python3 scripts/vendor-unity.py --system-text-json` to stage the
pinned NuGet closure restored from
`Haiven.Sdk.Core.SystemTextJson.csproj` into a local package, then add
`HAIVEN_SDK_SYSTEM_TEXT_JSON` to **Player Settings > Scripting Define Symbols**.
The constrained `Haiven.Sdk.SystemTextJson.Unity` assembly then compiles the
vendored adapter source and selects that codec before the first scene loads.
The staging command also writes exact third-party notices. A git-URL install
continues to use the dependency-free built-in codec because it contains no DLLs.

**`UnityEngine.JsonUtility` is still not a safe reader here, and the reason is
protocol-level.** The two outbound frame shapes are told apart by the *presence*
of `success`, not its value. `JsonUtility` is documented to map an absent field
to the type's default, and `bool` defaults to `false` — which would make every
server-initiated event read as a *failed reply*. The built-in reader keeps
"absent" and "null" distinct, which is the property the SDK needs.

## Remaining engine evidence

The engine harness in `../host` runs package EditMode and PlayMode tests, builds
a High-stripping IL2CPP WebGL player, and requires real browser round trips for
the `.jslib`, handoff, and analytics paths. Static validation remains useful but
is not a substitute for that harness. The EditMode suite reads the shared
`gaming-client/v0` corpus and replays every unwaived scenario through the exact
package source compiled by Unity.

The prior aggregate tip passed the combined zero-mandatory-DLL package under
Unity 2022.3.62f2 in [run 38601804971](https://github.com/haiven-dev/haiven-sdk/actions/runs/38601804971),
but that run predates the shared client-corpus test. Three release claims remain
narrower than the current source evidence:

1. **The exact supported editor.** Release evidence for this aggregate must
   come from a successful same-repository pull-request run on the pinned Unity
   2022.3.62f2 runner; run 38601804971 proves only the older subset at its own
   tip. The repository ruleset does not currently require this workflow.
2. **`link.xml`.** Removal experiment done ([unity-editor.yml run 38113269264](https://github.com/haiven-dev/haiven-sdk/actions/runs/38113269264)):
   the stripped WebGL smoke passes without it. The smoke did not exercise typed
   payload decoding; see the scope recorded in `Runtime/link.xml`.
3. **The optional System.Text.Json codec under IL2CPP.** The built-in codec uses
   no reflection and runs in the WebGL harness. The local System.Text.Json
   staging path is compiled outside Unity but has not run in a player build.
