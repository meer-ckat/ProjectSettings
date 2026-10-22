using System;
using System.Collections.Generic;
using IMGUI;
using UnityEngine;

// 화면 위쪽 대사 팝업 (I'm GUI 리테인드 + 트윈). 게임은 멈추지 않는다
// Resources/<file>.json 의 대사를 곡 시간(at, 초)에 맞춰 띄운다. at이 커지는 순서로 적는다
// 코드에서 바로 띄우려면 Say (TutorialCoach)
// 다음 대사가 오거나, 다 나온 뒤 hold초가 지나면 사라진다
// 이벤트: 아무 데서나 DialogueManager.Send("이름")으로 보내고, Message로 받는다
//   sendEvent      = 이 대사가 뜰 때 보낼 이벤트
//   waitUntilEvent = 앞 대사가 뜬 뒤 이 이벤트가 와야 뜬다 (Match가 Perfect/Great/Good/Miss/Finish를 보냄)
public class DialogueManager : MonoBehaviour
{
    [Serializable]
    public class Line
    {
        public float at;
        public string speaker;
        public string text;
        public string sendEvent;
        public string waitUntilEvent;
    }

    [Serializable]
    class DialogueFile
    {
        public Line[] lines;
    }

    public string file = "Dialogue/tutorial";
    public float charDelay = 0.04f; // 글자 하나 나오는 시간
    public float hold = 2f;         // 다 나온 뒤 떠 있는 시간

    const float RefH = 1080f;
    const float BoxW = 1000f, BoxH = 150f, Top = 40f;
    static readonly Color Obsidian = new(0x0B / 255f, 0x12 / 255f, 0x15 / 255f);
    static readonly Color BrilliantWhite = Color.white;
    public static event Action<string> Message;
    public static void Send(string name) => Message?.Invoke(name);

    readonly HashSet<string> received = new(); // 지금 대사가 뜬 뒤 받은 이벤트
    Line[] lines;
    Match match;
    int cur = -1;
    Line current; // 지금 떠 있는 대사 (파일 대사 또는 Say)
    float shownAt;
    float lastNow;
    GUIStyle boxStyle, nameStyle, textStyle;
    // 필드로 꼭 붙잡는다. GUIStyle 안의 참조는 안 쳐줘서, 씬 로드 직후 UnloadUnusedAssets가
    // 이 텍스처·폰트를 지운다 → 대사창 배경이 사라지고 글꼴이 기본으로 돌아감
    Texture2D boxTexture;
    Font font;
    GUIGroup box;
    GUILabel nameLabel, textLabel;
    GUIItem[] parts; // 같이 움직이는 것 (그룹은 자식을 안 끌고 다님)
    bool showing;    // 떠 있거나 들어오는 중

    // Match가 없는 씬이면 씬 시작부터 잰다
    float Now => match ? match.currentTime : Time.timeSinceLevelLoad;

    void Start()
    {
        match = FindAnyObjectByType<Match>();

        var json = Resources.Load<TextAsset>(file);
        if (json == null)
        {
            Debug.LogError($"DialogueManager: {file}.json 없음", this);
            enabled = false;
            return;
        }
        lines = JsonUtility.FromJson<DialogueFile>(json.text).lines;
        InitStyles();
        BuildWidgets();
    }

    // Message는 static이라 씬이 바뀌어도 남는다 → 꼭 떼야 함
    void OnEnable() => Message += OnMessage;
    void OnDisable() => Message -= OnMessage;
    void OnMessage(string name) => received.Add(name);

    public void Say(string speaker, string text)
    {
        current = new Line { speaker = speaker, text = text };
        shownAt = Time.time;
    }

    bool Ready(Line l) => Now >= l.at && (string.IsNullOrEmpty(l.waitUntilEvent) || received.Contains(l.waitUntilEvent));

    void Update()
    {
        // Restart로 곡 시간이 되감기면 처음부터. 1초 여유는 오디오 시계 보정으로 살짝 뒤로 가는 것 무시용
        if (Now < lastNow - 1f)
        {
            cur = -1;
            received.Clear();
        }
        lastNow = Now;

        while (cur + 1 < lines.Length && Ready(lines[cur + 1]))
        {
            cur++;
            current = lines[cur];
            shownAt = Time.time;
            received.Clear();
            if (!string.IsNullOrEmpty(lines[cur].sendEvent))
                Send(lines[cur].sendEvent);
        }

        Line l = current;
        float t = Time.time - shownAt;
        if (l == null || t > l.text.Length * charDelay + hold)
        {
            if (showing) Hide();
            return;
        }

        if (!showing) Show();
        nameLabel.Content.text = l.speaker;

        // 안 나온 글자도 투명하게 같이 그린다. 안 그러면 타이핑 중에 줄바꿈 위치가 바뀜
        int shown = Mathf.Min((int)(t / charDelay), l.text.Length);
        textLabel.Content.text = l.text.Substring(0, shown) + "<color=#00000000>" + l.text.Substring(shown) + "</color>";
    }

    // 인트로: 위에서 튀어 내려오며 나타난다. 나갈 때마다 자리가 밀리므로 매번 처음 자리부터 다시 잡는다
    void Show()
    {
        showing = true;

        // 1080p 기준 값에 화면 높이 배율을 곱해 화면 픽셀로 둔다 (GUI.matrix로 키우면 글자가 흐려짐)
        float k = Screen.height / RefH;
        nameStyle.fontSize = Mathf.RoundToInt(28f * k);
        textStyle.fontSize = Mathf.RoundToInt(32f * k);
        var r = new Rect((Screen.width - BoxW * k) * 0.5f, Top * k, BoxW * k, BoxH * k);
        box.SetRect(r); // 그룹의 SetRect는 자식을 안 옮긴다 → 자식도 따로
        nameLabel.SetRect(new Rect(r.x + 36f * k, r.y + 18f * k, r.width - 72f * k, 36f * k));
        textLabel.SetRect(new Rect(r.x + 36f * k, r.y + 58f * k, r.width - 72f * k, r.height - 70f * k));

        box.isVisible = true;
        box.FadeIn(0.25f); // 그룹 투명도는 자식에도 곱해진다
        GUITween.Wave(parts, new Vector2(0f, -60f * k), 0.4f, 0f, TweenHelper.EaseOutBack);
    }

    // 아웃트로: 위로 빠지며 사라진다. 도중에 새 대사가 오면 Show의 FadeIn이 이 페이드를 끊는다
    void Hide()
    {
        showing = false;
        float k = Screen.height / RefH;
        box.FadeOut(0.25f, onComplete: () => box.isVisible = false);
        GUITween.Wave(parts, new Vector2(0f, -40f * k), 0.25f, 0f, TweenHelper.EaseInQuad, inward: false);
    }

    // 위젯은 한 번 만들어 두고 보이기만 켜고 끈다
    void BuildWidgets()
    {
        box = Widget.Window(GUIContent.none, Rect.zero, "dialogue", boxStyle);
        box.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        nameLabel = Widget.Label(box, "", Rect.zero, nameStyle);
        textLabel = Widget.Label(box, "", Rect.zero, textStyle);
        box.SetLayer(100);            // 게임 HUD 위
        box.isInteractable = false;   // 장식이라 밑의 버튼 클릭을 막지 않게
        box.isVisible = false;
        parts = new GUIItem[] { box, nameLabel, textLabel };
    }

    void OnDestroy()
    {
        GUIManager.Unregister(box);
        Destroy(boxTexture);
    }

    // GUI.skin을 안 쓰므로 OnGUI 밖(Start)에서 만들어도 된다
    void InitStyles()
    {
        font = Resources.Load<Font>("Fonts/A2Z5"); // 없으면 기본 폰트
        boxTexture = RoundedBox(BrilliantWhite, Obsidian);

        boxStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        boxStyle.normal.background = boxTexture;

        nameStyle = new GUIStyle { font = font, fontSize = 28 };
        nameStyle.normal.textColor = Obsidian;

        textStyle = new GUIStyle { font = font, fontSize = 32, wordWrap = true, richText = true };
        textStyle.normal.textColor = Obsidian;
    }

    // 둥근 모서리 + 테두리 3px. GUIStyle.border로 늘려 쓴다
    static Texture2D RoundedBox(Color fill, Color edge)
    {
        const int size = 32, radius = 14;
        var tex = new Texture2D(size, size) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0);
                float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0);
                float inside = radius - Mathf.Sqrt(dx * dx + dy * dy); // 바깥 테두리에서 안쪽으로 들어온 거리
                Color c = inside < 3f ? edge : fill;
                c.a = Mathf.Clamp01(inside + 0.5f);
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return tex;
    }
}
