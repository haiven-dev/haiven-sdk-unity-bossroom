#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Haiven.Sdk;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>
    /// The assembled Boss Room project, booted from its own Startup scene, with
    /// the demo pointed at sdks/unity/ci/fake-haiven.mjs and no access token: opening
    /// chat starts the browser handoff without the join dialog, the fake's sign-in
    /// page redirects to the demo's loopback callback, and the exchanged token connects
    /// chat. One send
    /// is accepted and one is held, the seeded item is in the inventory panel on
    /// connect, and a grant made while playing appears through the
    /// entitlements.updated push. Run it with demos/bossroom/run_playmode.py,
    /// which assembles the project, starts the fake, and sets HAIVEN_DEMO_* and
    /// HAIVEN_FAKE_ORIGIN for the editor.
    /// </summary>
    public class BossRoomSmokeTests
    {
        private const string StartupScene = "Startup";
        private const string SeededItem = "bossroom/starter-blade";
        private const string RewardItem = "bossroom/friday-drop";
        private const float Seconds = 60;

        [UnityTest]
        public IEnumerator ChatAndItemEntitlementInBossRoom()
        {
            var origin = Environment.GetEnvironmentVariable("HAIVEN_FAKE_ORIGIN");
            Assert.That(origin, Is.Not.Null.And.Not.Empty, "HAIVEN_FAKE_ORIGIN is unset; run demos/bossroom/run_playmode.py");

            // Boss Room's own boot reaches for Unity Gaming Services and relay, which
            // are offline here. Its errors are not this test's; the assertions are.
            LogAssert.ignoreFailingMessages = true;

            // The demo booted when play mode started ([RuntimeInitializeOnLoadMethod])
            // and survives scene loads, the way it rides along in a real session.
            yield return SceneManager.LoadSceneAsync(StartupScene, LoadSceneMode.Single);
            // Startup hands over to MainMenu by itself; either is Boss Room running.
            Assert.That(new[] { StartupScene, "MainMenu" }, Does.Contain(SceneManager.GetActiveScene().name), "Boss Room did not boot");

            var deadline = Time.realtimeSinceStartup + Seconds;
            HaivenBossRoomDemo? demo = null;
            while ((demo = UnityEngine.Object.FindObjectOfType<HaivenBossRoomDemo>()) == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(demo, Is.Not.Null, "the demo did not boot; are HAIVEN_DEMO_* set for the editor?");

            // Chat, Rewards & Inventory, and MOTD own independent rail handles.
            var chatHandle = GameObject.Find("Haiven Chat Handle")?.GetComponent<Button>();
            Assert.That(chatHandle, Is.Not.Null, "the independent chat handle was not built");
            Assert.That(GameObject.Find("Haiven Rewards and Inventory Handle"), Is.Not.Null,
                "the combined rewards and inventory handle was not built");
            Assert.That(GameObject.Find("Haiven MOTD Handle"), Is.Not.Null,
                "the independent MOTD handle was not built");
            Assert.That(GameObject.Find("Haiven Entitlements Handle"), Is.Null,
                "rewards and inventory must not create a second handle");
            Assert.That(GameObject.Find("Haiven Chat"), Is.Null, "chat should default closed");
            Assert.That(GameObject.Find("Haiven Rewards and Inventory"), Is.Null,
                "rewards and inventory should default closed");

            // No HAIVEN_DEMO_ACCESS_TOKEN: install the browser double before opening
            // chat, because that open starts the handoff immediately.
            while (demo!.SignInState == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            var signIn = demo!.SignInState;
            Assert.That(signIn, Is.Not.Null, "the demo did not load its settings");
            Assert.That(signIn!.UsesEnvironmentToken, Is.False, "HAIVEN_DEMO_ACCESS_TOKEN reached the editor");
            Assert.That(signIn.Current.State, Is.EqualTo(DemoSignInState.SignedOut));
            Assert.That(demo.Client, Is.Null, "the demo connected before signing in");
            var browser = new RedirectFollowingBrowser();
            demo.HandoffBrowser = browser;
            chatHandle!.onClick.Invoke();
            Assert.That(GameObject.Find("Haiven Chat Sign In Dialog"), Is.Null,
                "opening chat must not paint the join dialog");
            Assert.That(browser.Opened, Is.Not.Null, "opening chat did not start the browser sign-in");
            while (demo.Client == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(browser.Opened, Is.Not.Null, "sign-in never opened the browser");
            Assert.That(browser.Opened!.AbsolutePath, Is.EqualTo("/connect/game"));
            Assert.That(signIn.Current.State, Is.EqualTo(DemoSignInState.SignedIn),
                "sign-in did not complete: " + signIn.Current.Status);
            Assert.That(browser.Completed, Is.Not.Null);
            while (!browser.Completed!.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(browser.Completed.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(browser.Completed.Result, Is.EqualTo(200), "the loopback callback page did not answer");
            yield return null;
            Assert.That(GameObject.Find("Haiven Sign In"), Is.Null, "the sign-in control stays hidden once signed in");

            var client = demo.Client;
            Assert.That(client, Is.Not.Null, "the demo did not start its client");

            while (!client!.Log.Lines.Contains("Haiven chat connected. Press Enter to talk.") && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(client.Log.Lines, Does.Contain("Haiven chat connected. Press Enter to talk."), Describe(client));

            while (!client.Items.Contains(SeededItem) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(client.Items, Does.Contain(SeededItem), "the seeded item was not read on connect. " + Describe(client));

            client.Send("gg from boss room");
            client.Send("[hold] held from boss room");
            while (!(client.Log.Lines.Contains("you: gg from boss room")
                     && client.Log.Lines.Contains("you (held for review): [hold] held from boss room"))
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(client.Log.Lines, Does.Contain("you: gg from boss room"), "the send was not accepted. " + Describe(client));
            Assert.That(client.Log.Lines, Does.Contain("you (held for review): [hold] held from boss room"), Describe(client));
            Assert.That(client.Log.Lines.Count(line => line.EndsWith("gg from boss room")), Is.EqualTo(1),
                "the channel's echo of our own message was shown twice");

            Assert.That(GameObject.Find("Haiven Profile Button"), Is.Null,
                "the settings gear should not be duplicated by a standalone H handle");
            var settingsEntry = GameObject.Find(HaivenBossRoomHostSurface.SettingsButtonName)?.GetComponent<Button>();
            Assert.That(settingsEntry, Is.Not.Null, "the Boss Room settings gear was not available");
            settingsEntry!.onClick.Invoke();
            Assert.That(GameObject.Find("Game Tab"), Is.Null);
            Assert.That(GameObject.Find("Haiven Tab"), Is.Null,
                "the game-only settings dialog must not gain a Haiven tab");
            demo.SetRewardsOpen(true);
            yield return null;
            var rewardButton = GameObject.Find("Haiven Claim Reward")?.GetComponent<Button>();
            Assert.That(rewardButton, Is.Not.Null, "the combined rewards and inventory dialog was not built");
            Assert.That(GameObject.Find("Haiven Rewards and Inventory Handle"), Is.Not.Null,
                "opening the dialog must keep its only handle visible");
            rewardButton!.onClick.Invoke();
            while ((!client.Items.Contains(RewardItem) || client.RewardStatus != "CLAIMED")
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(client.Items, Does.Contain(RewardItem), "the claimed reward never reached inventory. " + Describe(client));
            Assert.That(client.RewardStatus, Is.EqualTo("CLAIMED"));

            var granted = "bossroom/" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var request = new UnityWebRequest(origin + "/_fake/entitlements", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{\"code\":\"" + granted + "\",\"kind\":\"item\"}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();
                Assert.That(request.result, Is.EqualTo(UnityWebRequest.Result.Success), request.error);
            }

            while (!client.Items.Contains(granted) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(client.Items, Does.Contain(granted), "a grant made while playing never reached the inventory. " + Describe(client));
            Assert.That(client.Log.Lines, Does.Contain("unlocked: " + granted));

            var inventory = GameObject.Find("Haiven Inventory Items")?.GetComponent<Text>();
            Assert.That(inventory, Is.Not.Null, "the combined dialog did not include inventory");
            Assert.That(inventory!.text, Does.Contain(SeededItem).And.Contain(RewardItem).And.Contain(granted));

            GameObject.Find("Haiven Rewards Close")!.GetComponent<Button>().onClick.Invoke();
            yield return null;
            Assert.That(GameObject.Find("Haiven Rewards and Inventory"), Is.Null);
            var rewardsHandle = GameObject.Find("Haiven Rewards and Inventory Handle")?.GetComponent<Button>();
            Assert.That(rewardsHandle, Is.Not.Null,
                "closing the combined dialog must retain its handle");
            rewardsHandle!.onClick.Invoke();
            yield return null;

            // Haiven owns independent rail handles; the gear remains exclusively
            // responsible for Boss Room's game settings.
            Assert.That(GameObject.Find("Haiven Chat Handle"), Is.Not.Null);
            Assert.That(GameObject.Find("Haiven Rewards and Inventory Handle"), Is.Not.Null);
            Assert.That(GameObject.Find("Haiven MOTD Handle"), Is.Not.Null);
            Assert.That(GameObject.Find("Haiven Entitlements Handle"), Is.Null);
            Assert.That(GameObject.Find("Haiven Chat"), Is.Not.Null, "chat should remain independently open");
            Assert.That(GameObject.Find("Haiven Rewards and Inventory"), Is.Not.Null,
                "the single handle must reopen both rewards and inventory");
            Assert.That(GameObject.Find("Haiven Section"), Is.Null);
            var canvas = GameObject.Find("Haiven Canvas");
            Assert.That(canvas, Is.Not.Null);
            Assert.That(
                canvas!.GetComponent<CanvasScaler>().referenceResolution,
                Is.EqualTo(new Vector2(1280, 720)),
                "the fallback UI should remain legible in the editor's default game view");
            Assert.That(
                canvas.GetComponentsInChildren<Text>(true)
                    .Where(text => text.gameObject.activeInHierarchy && text.color.a > 0f)
                    .Min(text => text.fontSize),
                Is.GreaterThanOrEqualTo(16),
                "the visible fallback UI contains tiny text");
        }

        [UnityTest]
        public IEnumerator ClaimAndInventoryRefreshAgainstConfiguredSdkDemo()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HAIVEN_DEMO_ACCESS_TOKEN")))
                Assert.Ignore("HAIVEN_DEMO_ACCESS_TOKEN is unset; this acceptance check needs sdkdemo configuration");

            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(StartupScene, LoadSceneMode.Single);

            var deadline = Time.realtimeSinceStartup + Seconds;
            HaivenBossRoomDemo? demo = null;
            while ((demo = UnityEngine.Object.FindObjectOfType<HaivenBossRoomDemo>()) == null
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(demo, Is.Not.Null, "the demo did not boot with sdkdemo configuration");

            while (demo!.Client == null && Time.realtimeSinceStartup < deadline) yield return null;
            var client = demo.Client;
            Assert.That(client, Is.Not.Null, "the sdkdemo token did not start the client");

            demo.SetRewardsOpen(true);
            yield return null;
            var rewardButton = GameObject.Find("Haiven Claim Reward")?.GetComponent<Button>();
            Assert.That(rewardButton, Is.Not.Null, "the rewards dialog did not expose Claim");
            rewardButton!.onClick.Invoke();

            while ((!client!.Items.Contains(RewardItem) || client.RewardStatus != "CLAIMED")
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.That(client.Items, Does.Contain(RewardItem),
                "the claimed reward never reached inventory through entitlements.updated. " + Describe(client));
            Assert.That(client.RewardStatus, Is.EqualTo("CLAIMED"));
            var inventory = GameObject.Find("Haiven Inventory Items")?.GetComponent<Text>();
            Assert.That(inventory, Is.Not.Null, "the combined rewards and inventory surface was not built");
            Assert.That(inventory!.text, Does.Contain(RewardItem));

            GameObject.Find("Haiven Rewards Close")!.GetComponent<Button>().onClick.Invoke();
            yield return null;
            var rewardsHandle = GameObject.Find("Haiven Rewards and Inventory Handle")?.GetComponent<Button>();
            Assert.That(rewardsHandle, Is.Not.Null, "the combined rewards and inventory handle was not built");
            rewardsHandle!.onClick.Invoke();
            yield return null;
            Assert.That(GameObject.Find("Haiven Inventory Items")?.GetComponent<Text>()?.text,
                Does.Contain(RewardItem));
        }

        /// <summary>A browser that opens the sign-in page and follows its redirect.</summary>
        private sealed class RedirectFollowingBrowser : IHandoffBrowser
        {
            public Uri? Opened { get; private set; }
            public Task<int>? Completed { get; private set; }

            public void Open(Uri uri)
            {
                Opened = uri;
                Completed = Task.Run(async () =>
                {
                    using var http = new HttpClient();
                    using var response = await http.GetAsync(uri);
                    return (int)response.StatusCode;
                });
            }
        }

        private static string Describe(HaivenDemoClient client) =>
            "Chat log: [" + string.Join(" | ", client.Log.Lines) + "]";
    }

    /// <summary>
    /// Route-level acceptance for the backend used by the interactive dev path.
    /// It deliberately submits an invalid claim so the test proves the route is
    /// installed without changing fixture state.
    /// </summary>
    public class BossRoomBackendRouteTests
    {
        [UnityTest]
        public IEnumerator MotdEntitlementsAndRewardClaimsExist()
        {
            var baseUrl = Required("HAIVEN_DEMO_BASE_URL").TrimEnd('/');
            var appId = Required("HAIVEN_DEMO_APP_ID");
            var integrationId = Required("HAIVEN_DEMO_INTEGRATION_ID");
            var token = Required("HAIVEN_DEMO_ACCESS_TOKEN");

            using (var motd = UnityWebRequest.Get(
                       baseUrl + "/motd?integration_id=" + UnityWebRequest.EscapeURL(integrationId)))
            {
                motd.SetRequestHeader("X-Haiven-App-ID", appId);
                yield return motd.SendWebRequest();
                Assert.That(motd.responseCode, Is.EqualTo(200), Failure("MOTD", motd));
            }

            using (var entitlements = UnityWebRequest.Get(baseUrl + "/users/me/entitlements"))
            {
                Authorize(entitlements, appId, token);
                yield return entitlements.SendWebRequest();
                Assert.That(
                    entitlements.responseCode,
                    Is.EqualTo(200),
                    Failure("entitlements", entitlements));
            }

            using (var claim = new UnityWebRequest(
                       baseUrl + "/users/me/rewards/claims",
                       UnityWebRequest.kHttpVerbPOST,
                       new DownloadHandlerBuffer(),
                       new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"))))
            {
                Authorize(claim, appId, token);
                claim.SetRequestHeader("Content-Type", "application/json");
                yield return claim.SendWebRequest();
                Assert.That(claim.responseCode, Is.EqualTo(400), Failure("reward claims", claim));
            }
        }

        private static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            Assert.That(value, Is.Not.Null.And.Not.Empty, name + " is unset");
            return value!;
        }

        private static void Authorize(UnityWebRequest request, string appId, string token)
        {
            request.SetRequestHeader("X-Haiven-App-ID", appId);
            request.SetRequestHeader("Authorization", "Bearer " + token);
        }

        private static string Failure(string route, UnityWebRequest request) =>
            route + " returned HTTP " + request.responseCode + ": " + request.downloadHandler.text;
    }
}
