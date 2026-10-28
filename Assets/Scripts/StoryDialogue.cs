using System;
using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 곡 시작 전 스토리. RPG식 대화창(초상화 + 이름 + 글)이 대사마다 하나씩 생겨 아래에서 쌓이고, 앞 창은 위로 밀려 올라간다 (카톡처럼)
// Resources/Story/<곡 id>.json 이 있을 때만 Match가 만든다. 다 끝나야 음악이 시작된다
//   { "lines": [ { "speaker": "이름", "portrait": "Portraits/파일", "text": "대사" } ] }
//   portrait = Resources 안 이미지 경로(확장자 빼고). 비우거나 없으면 초상화 칸만 빈다
//   자리: 처음 나온 인물 = 왼쪽, 두 번째 = 오른쪽, 세 번째 = 다시 왼쪽… 끝까지 안 바뀜
//         speaker가 "나레이션"이거나 비면 가운데, 초상화·이름 없음
// 조작: 아무 키·클릭 = 글 다 보이기 → 다음 줄. ESC = 전부 건너뛰기
// 끝까지 보거나 ESC로 넘기면 PlayerPrefs "story_seen_<곡 id>" = 1, 다음부터 안 뜬다 (도중에 나가면 다시 뜸)
//   다시 보려면 그 키를 지우거나 Edit > Clear All PlayerPrefs
// 튜토리얼의 위쪽 대사창(DialogueManager)과는 별개
public class StoryDialogue : MonoBehaviour
{
    [Serializable]
    public class Line
    {
        public string speaker;
        public string portrait;
        public string text;
    }

    [Serializable]
    class StoryFile
    {
        public Line[] lines;
    }

    // 대사창 하나. 그룹은 자식을 안 끌고 다녀서 parts를 같이 옮긴다
    class Box
    {
        public GUIGroup group;
        public GUIItem[] parts;
        public float[] relY; // 각 part의 y - 창의 y
        public float y;      // 창이 가야 할 y. 연타해도 안 겹치게 "지금 위치" 말고 이 값에서 뺀다
        public GUILabel text, mark;
        public Line line;
    }

    enum Side { Left, Right, Mid }

    public float charDelay = 0.03f; // 글자 하나 나오는 시간

    public bool Playing { get; private set; }
    public double EndedDsp { get; private set; } // 끝난 시각 (AudioSettings.dspTime). 음악 예약과 같은 시계

    const float RefH = 1080f;
    const float Margin = 60f, BoxH = 230f, Gap = 16f, Pad = 26f, PortraitSize = 178f, NameH = 44f;
    const float BoxWidth = 0.76f; // 화면 폭 대비
    const float OldOpacity = 0.6f; // 지난 창
    static readonly Color Obsidian = new(0x0B / 255f, 0x12 / 255f, 0x15 / 255f);

    Line[] lines;
    string seenKey;
    int cur = -1;
    float shownAt; // 지금 줄이 뜬 시각 (unscaled: 설정 창으로 멈춰도 글은 나온다)

    readonly List<Box> boxes = new();
    readonly Dictionary<string, int> seats = new(); // 인물 → 처음 나온 순서
    Box current;
    GUILabel escLabel;
    float k;
    GUIStyle boxStyle, nameLeft, nameRight, textLeft, textRight, textMid, markLeft, markRight;

    // 붙잡아 둬야 씬 로드 직후 UnloadUnusedAssets가 안 지운다 (DialogueManager와 같은 이유)
    Texture2D boxTexture;
    Font font;

    // 곡에 스토리가 있으면 host에 붙여 시작하고 돌려준다. 없으면 null
    public static StoryDialogue TryStart(GameObject host, string songId)
    {
        string seenKey = "story_seen_" + songId;
        if (PlayerPrefs.GetInt(seenKey, 0) == 1)
            return null;

        TextAsset json = Resources.Load<TextAsset>("Story/" + songId);
        if (json == null)
            return null;

        Line[] lines = JsonUtility.FromJson<StoryFile>(json.text)?.lines;
        if (lines == null || lines.Length == 0)
            return null;

        var story = host.AddComponent<StoryDialogue>();
        story.lines = lines;
        story.seenKey = seenKey;
        story.Playing = true;
        return story;
    }

    void Start()
    {
        Build();
        Next();
    }

    void OnDestroy()
    {
        foreach (Box b in boxes)
            GUIManager.Unregister(b.group);
        GUIManager.Unregister(escLabel);
        Destroy(boxTexture);
    }

    void Update()
    {
        if (!Playing || current == null)
            return;

        string t = current.line.text;
        int shown = Mathf.Min((int)((Time.unscaledTime - shownAt) / charDelay), t.Length);
        bool typing = shown < t.Length;

        // 안 나온 글자도 투명하게 같이 그린다. 안 그러면 타이핑 중에 줄바꿈 위치가 바뀜
        current.text.Content.text = t.Substring(0, shown) + "<color=#00000000>" + t.Substring(shown) + "</color>";
        current.mark.Opacity = typing ? 0f : Mathf.PingPong(Time.unscaledTime * 2f, 1f);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            Finish();
        else if (Pressed())
        {
            if (typing)
                shownAt = Time.unscaledTime - t.Length * charDelay; // 글 다 보이기
            else
                Next();
        }
    }

    static bool Pressed() =>
        (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) ||
        (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) ||
        (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame);

    void Next()
    {
        if (cur + 1 >= lines.Length)
        {
            Finish();
            return;
        }

        Line l = lines[++cur];
        shownAt = Time.unscaledTime;
        if (current != null)
            current.text.Content.text = current.line.text; // 지난 창은 Update가 더 안 건드리니 글 전체로 고정

        // 떠 있던 창은 한 칸씩 위로, 흐리게. 화면 위로 다 나간 창은 지운다
        float step = (BoxH + Gap) * k;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            Box b = boxes[i];
            b.y -= step;
            if (b.y + BoxH * k < 0f)
            {
                GUIManager.Unregister(b.group);
                boxes.RemoveAt(i);
                continue;
            }
            for (int p = 0; p < b.parts.Length; p++)
                b.parts[p].MoveTo(new Vector2(b.parts[p].Pos.x, b.y + b.relY[p]), 0.25f, 0f, TweenHelper.EaseOutQuad);
            b.group.FadeTo(OldOpacity, 0.25f);
            b.mark.Opacity = 0f;
        }

        current = MakeBox(l, SideOf(l.speaker));
        boxes.Add(current);

        // 인트로: 아래에서 튀어 올라온다
        current.group.FadeIn(0.3f);
        GUITween.Wave(current.parts, new Vector2(0f, (BoxH + Margin) * k), 0.45f, 0f, TweenHelper.EaseOutBack);
    }

    Side SideOf(string speaker)
    {
        if (string.IsNullOrEmpty(speaker) || speaker == "나레이션")
            return Side.Mid;
        if (!seats.TryGetValue(speaker, out int seat))
            seats[speaker] = seat = seats.Count;
        return seat % 2 == 0 ? Side.Left : Side.Right;
    }

    // 아웃트로: 남은 창이 전부 아래로 내려가며 사라진 뒤 지운다. 음악은 EndedDsp 기준으로 Match가 시작한다
    void Finish()
    {
        if (!Playing)
            return;
        Playing = false;
        EndedDsp = AudioSettings.dspTime;
        PlayerPrefs.SetInt(seenKey, 1);
        PlayerPrefs.Save();

        foreach (Box b in boxes)
        {
            GUIGroup g = b.group;
            g.FadeOut(0.3f, onComplete: () => GUIManager.Unregister(g));
            GUITween.Wave(b.parts, new Vector2(0f, (BoxH + Margin) * k), 0.3f, 0f, TweenHelper.EaseInQuad, inward: false);
        }
        boxes.Clear();
        current = null;
        escLabel.FadeOut(0.3f, onComplete: () => GUIManager.Unregister(escLabel));
    }

    // 1080p 기준 값에 화면 높이 배율 k를 곱해 화면 픽셀로 둔다 (GUI.matrix로 키우면 글자가 흐려짐)
    void Build()
    {
        k = Screen.height / RefH;
        int Px(float size) => Mathf.RoundToInt(size * k);
        GUIStyle Aligned(GUIStyle s, TextAnchor a) => new(s) { alignment = a };

        font = Resources.Load<Font>("Fonts/A2Z5"); // 없으면 기본 폰트
        boxTexture = DialogueManager.RoundedBox(Color.white, Obsidian);
        boxStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        boxStyle.normal.background = boxTexture;
        nameLeft = new GUIStyle { font = font, fontSize = Px(33), fontStyle = FontStyle.Bold };
        nameLeft.normal.textColor = Obsidian;
        nameRight = Aligned(nameLeft, TextAnchor.UpperRight);
        textLeft = new GUIStyle { font = font, fontSize = Px(38), wordWrap = true, richText = true };
        textLeft.normal.textColor = Obsidian;
        textRight = Aligned(textLeft, TextAnchor.UpperRight);
        textMid = Aligned(textLeft, TextAnchor.MiddleCenter);
        markRight = new GUIStyle { font = font, fontSize = Px(22), alignment = TextAnchor.LowerRight };
        markRight.normal.textColor = Obsidian;
        markLeft = Aligned(markRight, TextAnchor.LowerLeft);

        var escStyle = Aligned(markRight, TextAnchor.UpperRight);
        escStyle.normal.textColor = Color.white;
        escLabel = Widget.Label("ESC 건너뛰기", new Rect(Screen.width - (Margin + 300f) * k, Margin * 0.5f * k, 300f * k, 30f * k), escStyle);
        escLabel.SetLayer(150);
        escLabel.Opacity = 0.5f;
    }

    Box MakeBox(Line l, Side side)
    {
        float w = Screen.width * BoxWidth, h = BoxH * k;
        float x = side == Side.Left ? Margin * k
                : side == Side.Right ? Screen.width - Margin * k - w
                : (Screen.width - w) / 2f;
        var r = new Rect(x, Screen.height - (BoxH + Margin) * k, w, h);
        float pad = Pad * k, face = PortraitSize * k;

        var b = new Box { line = l, y = r.y };
        b.group = Widget.Window(GUIContent.none, r, "story", boxStyle);
        b.group.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다

        if (side == Side.Mid)
        {
            b.text = Widget.Label(b.group, "", new Rect(r.x + pad, r.y + pad, r.width - pad * 2f, r.height - pad * 2f), textMid);
            b.mark = Widget.Label(b.group, "▼", new Rect(r.xMax - pad - 40f * k, r.yMax - pad - 30f * k, 40f * k, 30f * k), markRight);
        }
        else
        {
            // 초상화는 바깥쪽 끝 (왼쪽 창은 왼쪽, 오른쪽 창은 오른쪽). 이름·글·▼도 그쪽으로 붙인다
            bool left = side == Side.Left;
            float faceX = left ? r.x + pad : r.xMax - pad - face;
            float textX = left ? faceX + face + pad : r.x + pad;
            float textW = left ? r.xMax - pad - textX : faceX - pad - textX;

            Texture2D tex = string.IsNullOrEmpty(l.portrait) ? null : Resources.Load<Texture2D>(l.portrait);
            if (tex == null && !string.IsNullOrEmpty(l.portrait))
                Debug.LogWarning($"StoryDialogue: 초상화 없음 Resources/{l.portrait}", this);
            GUIImage portrait = Widget.Image(b.group, (Texture)null, new Rect(faceX, r.y + (r.height - face) / 2f, face, face), ScaleMode.ScaleToFit);
            portrait.SetTexture(tex);
            portrait.isVisible = tex != null;

            Widget.Label(b.group, l.speaker, new Rect(textX, r.y + pad, textW, NameH * k), left ? nameLeft : nameRight);
            b.text = Widget.Label(b.group, "", new Rect(textX, r.y + pad + (NameH + 8f) * k, textW, r.height - pad * 2f - (NameH + 8f) * k), left ? textLeft : textRight);
            float markX = left ? r.xMax - pad - 40f * k : r.x + pad;
            b.mark = Widget.Label(b.group, "▼", new Rect(markX, r.yMax - pad - 30f * k, 40f * k, 30f * k), left ? markRight : markLeft);
        }

        b.group.SetLayer(150);          // HUD·튜토리얼 대사창 위, 보정 화면 아래
        b.group.isInteractable = false; // 클릭은 Update에서 직접 읽는다
        b.parts = new GUIItem[b.group.Childrens.Count + 1];
        b.relY = new float[b.parts.Length];
        b.parts[0] = b.group;
        for (int i = 0; i < b.group.Childrens.Count; i++)
            b.parts[i + 1] = b.group.Childrens[i];
        for (int i = 0; i < b.parts.Length; i++)
            b.relY[i] = b.parts[i].Pos.y - r.y;
        b.mark.Opacity = 0f;
        return b;
    }
}
