#nullable enable
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>
    /// The lobby on screen: a LOBBIES button in Boss Room's own main menu list,
    /// right under START SESSION, the room list with create and join, and a
    /// small bar while in a room. The button is a copy of START SESSION, so it
    /// looks like Boss Room's; the rest is built in code in the demo's colours.
    /// </summary>
    [RequireComponent(typeof(HaivenLobby))]
    public sealed class HaivenLobbyView : MonoBehaviour
    {
        internal const string CanvasName = "Haiven Lobby Canvas";
        internal const string SessionButtonName = "Session Start Button";
        internal const string LobbiesButtonName = "Lobbies Button";
        internal const string LobbiesCaption = "Lobbies";
        internal const string PanelName = "Haiven Rooms Panel";
        internal const int MinPlayers = 2;
        internal const int MaxPlayers = 8; // Boss Room's ConnectionManager admits eight.

        private static readonly Color Muted = new Color(0.78f, 0.74f, 0.66f, 1f);
        private static readonly Color RowFill = new Color(20f / 255f, 16f / 255f, 9f / 255f, 1f);

        private readonly List<GameObject> rows = new List<GameObject>();
        private HaivenLobby? lobby;
        private Font? font;
        private GameObject? lobbiesButton;
        private GameObject? panel;
        private GameObject? roomBar;
        private RectTransform? listContent;
        private Text? emptyText;
        private Text? statusText;
        private Text? roomText;
        private Button? copyButton;
        private InputField? nameInput;
        private InputField? codeInput;
        private Button? visibilityButton;
        private Text? visibilityText;
        private Text? playersText;
        private Button? mixedButton;
        private Text? mixedText;
        private Button? createButton;
        private Button? codeButton;
        private Button? refreshButton;
        private SessionVisibility visibility = SessionVisibility.Public;
        private int maxPlayers = 4;
        private bool allowMixedAges;
        private bool open;

        private void Awake()
        {
            lobby = GetComponent<HaivenLobby>();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Build();
            lobby.Changed += Render;
        }

        private void OnDestroy()
        {
            if (lobby != null) lobby.Changed -= Render;
        }

        private void Update()
        {
            // The main menu is where Boss Room starts a game; the button hides once
            // a game runs, and the room bar takes over.
            var manager = NetworkManager.Singleton;
            var inGame = manager != null && manager.IsListening;
            var menu = !inGame && BossRoomConnection.IsAvailable;
            if (menu) EnsureMenuButton();
            if (!menu) open = false;
            if (panel != null) panel.SetActive(menu && open);
            if (roomBar != null) roomBar.SetActive(lobby != null && lobby.Phase == LobbyPhase.InRoom);
        }

        private void Render()
        {
            if (lobby == null) return;
            // Signed out, the buttons stay pressable so a press can say why
            // nothing happens; they wait only while a request runs or in a room.
            var idle = lobby.Phase == LobbyPhase.Idle || lobby.Phase == LobbyPhase.SignedOut;
            statusText!.text = lobby.Phase == LobbyPhase.SignedOut && lobby.Status.Length == 0
                ? "Sign in with Haiven (top right) to see rooms."
                : lobby.Status;
            createButton!.interactable = idle;
            codeButton!.interactable = idle;
            refreshButton!.interactable = idle;
            visibilityText!.text = "Who can join: " + VisibilityCaption(visibility);
            playersText!.text = maxPlayers + " players";
            mixedButton!.interactable = idle && visibility != SessionVisibility.Public;
            mixedText!.text = (allowMixedAges && visibility != SessionVisibility.Public ? "[x]" : "[ ]") + " Allow mixed ages";

            foreach (var row in rows) Destroy(row);
            rows.Clear();
            foreach (var room in lobby.Rooms) rows.Add(Row(room, idle));
            emptyText!.gameObject.SetActive(lobby.Rooms.Count == 0);
            emptyText.text = lobby.Phase == LobbyPhase.SignedOut ? "" : "No open rooms yet. Create one below.";

            var current = lobby.Room;
            roomText!.text = current == null
                ? ""
                : (lobby.Hosting ? "Hosting" : "In") + " a Haiven room · " + lobby.Status
                  + (lobby.ShareCode != null ? "   Code: " + lobby.ShareCode : "");
            copyButton!.gameObject.SetActive(lobby.ShareCode != null);
        }

        private GameObject Row(SessionListing room, bool idle)
        {
            var row = new GameObject("Haiven Room " + room.SessionId, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(listContent, false);
            row.GetComponent<Image>().color = RowFill;
            row.GetComponent<LayoutElement>().preferredHeight = 52f;
            var rect = (RectTransform)row.transform;

            var name = Label(rect, "Name", room.Name.Length > 0 ? room.Name : "Boss Room", 18, FontStyle.Bold,
                new Vector2(14f, -6f), new Vector2(460f, 24f));
            name.color = HaivenBossRoomMenuConfiguration.GoldText;
            var details = room.MemberCount + "/" + room.MaxMembers + " players · " + room.Visibility
                + (room.RelayPolicy == "relay-only" ? " · through Haiven's relay" : " · direct when possible");
            Label(rect, "Details", details, 14, FontStyle.Normal, new Vector2(14f, -28f), new Vector2(460f, 20f)).color = Muted;

            var join = ButtonControl(rect, "Join", room.IsFull ? "Full" : "Join", new Vector2(-14f, -8f), new Vector2(110f, 36f));
            Anchor((RectTransform)join.transform, new Vector2(1f, 1f));
            join.interactable = idle && !room.IsFull;
            var id = room.SessionId;
            join.onClick.AddListener(() => lobby!.Join(id));
            return row;
        }

        private void Build()
        {
            var canvasObject = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var root = (RectTransform)canvasObject.transform;

            BuildPanel(root);
            BuildRoomBar(root);
            Render();
        }

        private void BuildPanel(RectTransform root)
        {
            panel = new GameObject(PanelName, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(root, false);
            panel.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.DialogFill;
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(820f, 700f);
            Border(rect, HaivenBossRoomMenuConfiguration.DialogBorder, 3f);

            Label(rect, "Title", "HAIVEN ROOMS", 28, FontStyle.Bold, new Vector2(28f, -22f), new Vector2(500f, 36f))
                .color = HaivenBossRoomMenuConfiguration.GoldText;
            Label(rect, "Explain",
                "Rooms you can join. Haiven lists only rooms for your age group, so the list is already filtered for you. "
                + "Rooms that mix ages are never listed: join them with the code the host shares.",
                14, FontStyle.Normal, new Vector2(28f, -62f), new Vector2(764f, 40f)).color = Muted;

            refreshButton = ButtonControl(rect, "Refresh", "Refresh", new Vector2(-82f, -22f), new Vector2(120f, 36f));
            Anchor((RectTransform)refreshButton.transform, new Vector2(1f, 1f));
            refreshButton.onClick.AddListener(() => lobby!.Refresh());
            var close = ButtonControl(rect, "Close", "X", new Vector2(-22f, -22f), new Vector2(44f, 36f));
            Anchor((RectTransform)close.transform, new Vector2(1f, 1f));
            close.onClick.AddListener(() => open = false);

            BuildList(rect);

            Label(rect, "Create Heading", "CREATE A ROOM", 18, FontStyle.Bold, new Vector2(28f, -428f), new Vector2(400f, 26f))
                .color = HaivenBossRoomMenuConfiguration.GoldText;
            nameInput = Input(rect, "Room Name", "Room name", new Vector2(28f, -462f), new Vector2(300f, 40f));
            visibilityButton = ButtonControl(rect, "Visibility", "", new Vector2(344f, -462f), new Vector2(260f, 40f));
            visibilityText = visibilityButton.GetComponentInChildren<Text>();
            visibilityButton.onClick.AddListener(() =>
            {
                visibility = visibility == SessionVisibility.Public ? SessionVisibility.Friends
                    : visibility == SessionVisibility.Friends ? SessionVisibility.Private : SessionVisibility.Public;
                if (visibility == SessionVisibility.Public) allowMixedAges = false;
                Render();
            });
            var fewer = ButtonControl(rect, "Fewer Players", "-", new Vector2(620f, -462f), new Vector2(40f, 40f));
            fewer.onClick.AddListener(() => { maxPlayers = Mathf.Max(MinPlayers, maxPlayers - 1); Render(); });
            playersText = Label(rect, "Players", "", 16, FontStyle.Normal, new Vector2(664f, -462f), new Vector2(84f, 40f));
            playersText.alignment = TextAnchor.MiddleCenter;
            var more = ButtonControl(rect, "More Players", "+", new Vector2(752f, -462f), new Vector2(40f, 40f));
            more.onClick.AddListener(() => { maxPlayers = Mathf.Min(MaxPlayers, maxPlayers + 1); Render(); });

            mixedButton = ButtonControl(rect, "Mixed Ages", "", new Vector2(28f, -512f), new Vector2(300f, 40f));
            mixedText = mixedButton.GetComponentInChildren<Text>();
            mixedButton.onClick.AddListener(() => { allowMixedAges = !allowMixedAges; Render(); });
            createButton = ButtonControl(rect, "Create", "Create room", new Vector2(612f, -512f), new Vector2(180f, 40f));
            createButton.onClick.AddListener(() =>
                lobby!.Create(nameInput!.text, visibility, maxPlayers, allowMixedAges));

            Label(rect, "Code Heading", "JOIN WITH A CODE", 18, FontStyle.Bold, new Vector2(28f, -570f), new Vector2(400f, 26f))
                .color = HaivenBossRoomMenuConfiguration.GoldText;
            codeInput = Input(rect, "Room Code", "room-id/ABC123", new Vector2(28f, -604f), new Vector2(564f, 40f));
            codeButton = ButtonControl(rect, "Join Code", "Join with code", new Vector2(612f, -604f), new Vector2(180f, 40f));
            codeButton.onClick.AddListener(() => lobby!.JoinWithCode(codeInput!.text));

            statusText = Label(rect, "Status", "", 15, FontStyle.Italic, new Vector2(28f, -654f), new Vector2(764f, 30f));
            statusText.color = Muted;
        }

        private void BuildList(RectTransform parent)
        {
            var view = new GameObject("Rooms", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            view.transform.SetParent(parent, false);
            view.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.InputFill;
            view.GetComponent<Mask>().showMaskGraphic = true;
            var viewRect = (RectTransform)view.transform;
            viewRect.anchorMin = viewRect.anchorMax = viewRect.pivot = new Vector2(0f, 1f);
            viewRect.anchoredPosition = new Vector2(28f, -112f);
            viewRect.sizeDelta = new Vector2(764f, 296f);

            var content = new GameObject("Room List", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(view.transform, false);
            listContent = (RectTransform)content.transform;
            listContent.anchorMin = new Vector2(0f, 1f);
            listContent.anchorMax = new Vector2(1f, 1f);
            listContent.pivot = new Vector2(0.5f, 1f);
            listContent.sizeDelta = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = view.GetComponent<ScrollRect>();
            scroll.content = listContent;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            emptyText = Label(viewRect, "Empty", "", 16, FontStyle.Italic, new Vector2(16f, -16f), new Vector2(700f, 30f));
            emptyText.color = Muted;
        }

        private void BuildRoomBar(RectTransform root)
        {
            roomBar = new GameObject("Haiven Room Bar", typeof(RectTransform), typeof(Image));
            roomBar.transform.SetParent(root, false);
            roomBar.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.DialogScrim;
            var rect = (RectTransform)roomBar.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 16f);
            rect.sizeDelta = new Vector2(1100f, 44f);
            roomText = Label(rect, "Room", "", 15, FontStyle.Normal, new Vector2(16f, -2f), new Vector2(940f, 40f));
            roomText.color = HaivenBossRoomMenuConfiguration.GoldText;
            copyButton = ButtonControl(rect, "Copy Code", "Copy code", new Vector2(-12f, -4f), new Vector2(120f, 36f));
            Anchor((RectTransform)copyButton.transform, new Vector2(1f, 1f));
            copyButton.onClick.AddListener(() =>
            {
                if (lobby?.ShareCode != null) GUIUtility.systemCopyBuffer = lobby.ShareCode;
            });
        }

        // Boss Room builds its main menu anew each time the scene loads, so the
        // button is added again whenever it is missing.
        private void EnsureMenuButton()
        {
            if (lobbiesButton != null) return;
            var session = FindMenuButton(SessionButtonName);
            if (session == null) return;
            lobbiesButton = AddUnder(session, LobbiesButtonName, LobbiesCaption, () =>
            {
                open = true;
                lobby!.Refresh();
            });
        }

        private static RectTransform? FindMenuButton(string name)
        {
            foreach (var button in FindObjectsByType<Button>(FindObjectsSortMode.None))
                if (button.gameObject.name == name && button.gameObject.activeInHierarchy)
                    return (RectTransform)button.transform;
            return null;
        }

        /// <summary>
        /// Copies a Boss Room menu button, places the copy where the next button
        /// was, and moves every button below it down by one row.
        /// </summary>
        internal static GameObject AddUnder(RectTransform above, string name, string caption, UnityEngine.Events.UnityAction onClick)
        {
            var parent = (RectTransform)above.parent;
            var step = above.rect.height + 16f;
            var below = new List<RectTransform>();
            foreach (Transform child in parent)
            {
                if (child == above || child is not RectTransform sibling || sibling.GetComponent<Button>() == null) continue;
                if (Mathf.Abs(sibling.anchoredPosition.x - above.anchoredPosition.x) > 1f) continue;
                if (sibling.anchoredPosition.y >= above.anchoredPosition.y) continue;
                below.Add(sibling);
            }
            if (below.Count > 0)
            {
                var nearest = float.MinValue;
                foreach (var sibling in below) nearest = Mathf.Max(nearest, sibling.anchoredPosition.y);
                step = above.anchoredPosition.y - nearest;
                foreach (var sibling in below) sibling.anchoredPosition -= new Vector2(0f, step);
            }

            var copy = Instantiate(above.gameObject, parent);
            copy.name = name;
            copy.transform.SetSiblingIndex(above.GetSiblingIndex() + 1);
            ((RectTransform)copy.transform).anchoredPosition = above.anchoredPosition - new Vector2(0f, step);
            // Boss Room's own scripts on the button (its tooltip, the session
            // flow's state) belong to the original; the copy keeps only UI.
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var space = behaviour.GetType().Namespace ?? "";
                if (!space.StartsWith("UnityEngine") && !space.StartsWith("TMPro")) Destroy(behaviour);
            }
            var button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(onClick);
            button.interactable = true;
            foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.text = caption;
            foreach (var label in copy.GetComponentsInChildren<Text>(true)) label.text = caption;
            return copy;
        }

        internal static string VisibilityCaption(SessionVisibility value) => value switch
        {
            SessionVisibility.Public => "everyone (listed)",
            SessionVisibility.Friends => "friends (listed)",
            _ => "with the code only",
        };

        private Text Label(RectTransform parent, string name, string caption, int size, FontStyle style, Vector2 position, Vector2 box)
        {
            var label = new GameObject("Haiven " + name, typeof(RectTransform), typeof(Text));
            label.transform.SetParent(parent, false);
            var rect = (RectTransform)label.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = box;
            var text = label.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.supportRichText = false;
            text.text = caption;
            return text;
        }

        private Button ButtonControl(RectTransform parent, string name, string caption, Vector2 position, Vector2 box)
        {
            var control = new GameObject("Haiven " + name, typeof(RectTransform), typeof(Image), typeof(Button));
            control.transform.SetParent(parent, false);
            var rect = (RectTransform)control.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = box;
            control.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.ButtonFill;
            Border(rect, HaivenBossRoomMenuConfiguration.ButtonBorder, 2f);
            var button = control.GetComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            button.colors = colors;
            var text = Label(rect, name + " Caption", caption, 17, FontStyle.Bold, Vector2.zero, box);
            text.alignment = TextAnchor.MiddleCenter;
            text.color = HaivenBossRoomMenuConfiguration.GoldText;
            return button;
        }

        private InputField Input(RectTransform parent, string name, string placeholder, Vector2 position, Vector2 box)
        {
            var field = new GameObject("Haiven " + name, typeof(RectTransform), typeof(Image), typeof(InputField));
            field.transform.SetParent(parent, false);
            var rect = (RectTransform)field.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = box;
            field.GetComponent<Image>().color = HaivenBossRoomMenuConfiguration.InputFill;
            Border(rect, HaivenBossRoomMenuConfiguration.DialogBorder, 2f);
            var text = Label(rect, name + " Text", "", 16, FontStyle.Normal, new Vector2(10f, 0f), box - new Vector2(20f, 0f));
            text.supportRichText = false;
            var hint = Label(rect, name + " Placeholder", placeholder, 16, FontStyle.Italic, new Vector2(10f, 0f), box - new Vector2(20f, 0f));
            hint.color = new Color(1f, 1f, 1f, 0.35f);
            var input = field.GetComponent<InputField>();
            input.textComponent = text;
            input.placeholder = hint;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 80;
            return input;
        }

        private static void Anchor(RectTransform rect, Vector2 corner)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
        }

        private static void Border(RectTransform parent, Color color, float thickness)
        {
            Edge(parent, color, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(thickness, 0f));
            Edge(parent, color, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(thickness, 0f));
            Edge(parent, color, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, thickness));
            Edge(parent, color, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, thickness));
        }

        private static void Edge(RectTransform parent, Color color, Vector2 min, Vector2 max, Vector2 size)
        {
            var edge = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            edge.transform.SetParent(parent, false);
            var image = edge.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = (RectTransform)edge.transform;
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }
    }
}
