#nullable enable
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    public sealed class BossRoomNativeHostSurfaceTests
    {
        private GameObject? host;
        private GameObject? settingsCanvas;

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.DestroyImmediate(host);
            if (settingsCanvas != null) Object.DestroyImmediate(settingsCanvas);
        }

        [Test]
        public void NativeSurfaceCentersAndClampsTheSingleHostShell()
        {
            var clamped = HaivenBossRoomHostSurface.ClampShellSize(new Vector2(520, 430));
            Assert.That(clamped, Is.EqualTo(new Vector2(472, 382)));
            var roomy = HaivenBossRoomHostSurface.ClampShellSize(new Vector2(1920, 1080));
            Assert.That(roomy, Is.EqualTo(new Vector2(640, 680)));

            var centered = HaivenBossRoomHostSurface.SafeAreaCenterOffset(
                new Rect(0, 0, 1920, 1080),
                new Vector2(1920, 1080),
                1f);
            Assert.That(centered, Is.EqualTo(Vector2.zero));

            var inset = HaivenBossRoomHostSurface.SafeAreaCenterOffset(
                new Rect(100, 20, 1800, 1000),
                new Vector2(2000, 1200),
                2f);
            Assert.That(inset, Is.EqualTo(new Vector2(0, -40)));
        }

        [Test]
        public void HostIntegrationLeavesGearGameOnlyAndKeepsHaivenRailIndependent()
        {
            var hostSettingsButton = CreateHostSettingsCanvas();
            host = new GameObject("Haiven native test host");
            var view = HaivenBossRoomUGuiView.CreateFallback(host.transform);
            view.ConfigureDrawers(true, true, false, false);

            Assert.That(view.TryAttachNativeHost(), Is.True);

            var safeArea = host.transform.Find("Haiven Canvas/Haiven Safe Area")!;
            var handleLayer = safeArea.Find("Haiven Handle Layer")!;
            var dialogLayer = safeArea.Find("Haiven Dialog Layer")!;
            var chatComponent = dialogLayer.Find("Haiven Chat Component")!;
            var rewardsComponent = dialogLayer.Find("Haiven Rewards Component")!;
            Assert.That(handleLayer.Find("Haiven Chat Handle")!.gameObject.activeSelf, Is.True);
            Assert.That(handleLayer.Find("Haiven Rewards and Inventory Handle")!.gameObject.activeSelf, Is.True);
            Assert.That(rewardsComponent.Find("Haiven Rewards and Inventory")!.gameObject.activeSelf, Is.False);
            Assert.That(handleLayer.Find("Haiven Entitlements Handle"), Is.Null,
                "the combined surface must not add a second rail handle");

            var shell = settingsCanvas.transform.Find(HaivenBossRoomHostSurface.PanelName)!.GetComponent<RectTransform>();
            Assert.That(shell.anchorMin, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(shell.anchorMax, Is.EqualTo(shell.anchorMin));
            Assert.That(shell.pivot, Is.EqualTo(shell.anchorMin));
            Assert.That(shell.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(shell.sizeDelta.x, Is.LessThanOrEqualTo(HaivenBossRoomHostSurface.MaxShellWidth));
            Assert.That(shell.sizeDelta.y, Is.LessThanOrEqualTo(HaivenBossRoomHostSurface.MaxShellHeight));
            Assert.That(shell.Find("Game Tab"), Is.Null);
            Assert.That(shell.Find("Haiven Tab"), Is.Null);
            Assert.That(shell.Find("Haiven Section"), Is.Null);
            Assert.That(shell.Find("SettingsLabel"), Is.Not.Null);
            Assert.That(shell.Find("MasterVolume"), Is.Not.Null);
            Assert.That(shell.Find("MusicVolume"), Is.Not.Null);
            hostSettingsButton.onClick.Invoke();
            Assert.That(handleLayer.Find("Haiven Chat Handle")!.gameObject.activeSelf, Is.True,
                "the settings gear must not take ownership of chat");
            Assert.That(handleLayer.Find("Haiven Rewards and Inventory Handle")!.gameObject.activeSelf, Is.True,
                "the settings gear must not take ownership of rewards and inventory");
        }

        private Button CreateHostSettingsCanvas()
        {
            settingsCanvas = new GameObject(
                HaivenBossRoomHostSurface.CanvasName,
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var settingsButton = Child(
                settingsCanvas.transform,
                HaivenBossRoomHostSurface.SettingsButtonName,
                typeof(Image), typeof(Button));
            settingsButton.anchorMin = settingsButton.anchorMax = settingsButton.pivot = Vector2.one;
            settingsButton.anchoredPosition = new Vector2(-50, -160);
            settingsButton.sizeDelta = new Vector2(100, 100);

            var panel = Child(settingsCanvas.transform, HaivenBossRoomHostSurface.PanelName, typeof(Image));
            panel.sizeDelta = new Vector2(500, 420);
            panel.gameObject.SetActive(false);
            Child(panel, "SettingsLabel", typeof(Text));
            Child(panel, "MasterVolume", typeof(Slider));
            Child(panel, "MusicVolume", typeof(Slider));

            var quit = Child(settingsCanvas.transform, "Quit Button", typeof(Image), typeof(Button));
            quit.anchorMin = quit.anchorMax = quit.pivot = Vector2.one;
            quit.anchoredPosition = new Vector2(-50, -32);
            quit.sizeDelta = new Vector2(100, 100);
            return settingsButton.GetComponent<Button>();
        }

        private static RectTransform Child(Transform parent, string name, params System.Type[] components)
        {
            var allComponents = new System.Type[components.Length + 1];
            allComponents[0] = typeof(RectTransform);
            components.CopyTo(allComponents, 1);
            var child = new GameObject(name, allComponents);
            child.transform.SetParent(parent, false);
            return child.GetComponent<RectTransform>();
        }
    }
}
