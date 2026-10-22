using System;
using System.Collections.Generic;
using UnityEngine;

// 화면 위쪽 대사 팝업 (단독 OnGUI). 게임은 멈추지 않는다
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
    }

    void OnGUI()
    {
        if (current == null) return;

        Line l = current;
        float t = Time.time - shownAt;
        if (t > l.text.Length * charDelay + hold) return;

        InitStyles();

        // 1080p 기준 좌표로 그리고 화면 높이에 맞춰 확대/축소
        float scale = Screen.height / RefH;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        var box = new Rect((Screen.width / scale - BoxW) * 0.5f, Top, BoxW, BoxH);

        GUI.Box(box, GUIContent.none, boxStyle);
        GUI.Label(new Rect(box.x + 36f, box.y + 18f, box.width - 72f, 36f), l.speaker, nameStyle);

        // 안 나온 글자도 투명하게 같이 그린다. 안 그러면 타이핑 중에 줄바꿈 위치가 바뀜
        int shown = Mathf.Min((int)(t / charDelay), l.text.Length);
        string text = l.text.Substring(0, shown) + "<color=#00000000>" + l.text.Substring(shown) + "</color>";
        GUI.Label(new Rect(box.x + 36f, box.y + 58f, box.width - 72f, box.height - 70f), text, textStyle);
    }

    // GUI.skin은 OnGUI 안에서만 접근 가능 → 첫 OnGUI에서 생성
    void InitStyles()
    {
        if (boxStyle != null) return;

        var font = Resources.Load<Font>("Fonts/A2Z5"); // 없으면 기본 폰트

        boxStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        boxStyle.normal.background = RoundedBox(BrilliantWhite, Obsidian);

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
