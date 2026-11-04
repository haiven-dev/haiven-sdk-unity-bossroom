#nullable enable
using System.Collections;
using Haiven.Demo.BossRoom.Lobby;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>A Boss Room script on a menu button, which the copy must not keep.</summary>
    internal sealed class FakeBossRoomTooltip : MonoBehaviour
    {
    }

    public sealed class LobbyMenuButtonTests
    {
        private GameObject? canvas;

        [TearDown]
        public void Destroy()
        {
            if (canvas != null) Object.Destroy(canvas);
        }

        [UnityTest]
        public IEnumerator LobbiesSitsUnderStartSessionAndPushesTheRestDown()
        {
            canvas = new GameObject("Menu", typeof(RectTransform), typeof(Canvas));
            var root = (RectTransform)canvas.transform;
            var session = MenuButton(root, "Session Start Button", "Start Session", -80f);
            session.gameObject.AddComponent<FakeBossRoomTooltip>();
            var clicked = false;
            session.GetComponent<Button>().onClick.AddListener(() => Assert.Fail("the original session flow ran"));
            var ip = MenuButton(root, "IP Start Button", "Direct IP", -203f);

            var copy = HaivenLobbyView.AddUnder(session, "Lobbies Button", "Lobbies", () => clicked = true);
            yield return null;

            var rect = (RectTransform)copy.transform;
            Assert.That(rect.anchoredPosition.y, Is.EqualTo(-203f).Within(0.01f), "LOBBIES takes the next row");
            Assert.That(ip.anchoredPosition.y, Is.EqualTo(-326f).Within(0.01f), "Direct IP moves down one row");
            Assert.That(rect.GetSiblingIndex(), Is.EqualTo(session.GetSiblingIndex() + 1));
            Assert.That(copy.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Lobbies"));
            Assert.That(copy.GetComponent<FakeBossRoomTooltip>(), Is.Null, "Boss Room's scripts stay on the original");
            copy.GetComponent<Button>().onClick.Invoke();
            Assert.That(clicked, Is.True);
        }

        private static RectTransform MenuButton(RectTransform parent, string name, string caption, float y)
        {
            var button = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            button.transform.SetParent(parent, false);
            var rect = (RectTransform)button.transform;
            rect.sizeDelta = new Vector2(400f, 110f);
            rect.anchoredPosition = new Vector2(0f, y);
            var label = new GameObject("Text (TMP)", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(rect, false);
            label.GetComponent<TextMeshProUGUI>().text = caption;
            return rect;
        }
    }
}
