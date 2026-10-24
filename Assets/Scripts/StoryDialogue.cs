using System;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 곡 시작 전 스토리. 화면 아래 RPG식 대화창(왼쪽 초상화 + 이름 + 글)
// Resources/Story/<곡 id>.json 이 있을 때만 Match가 만든다. 다 끝나야 음악이 시작된다
//   { "lines": [ { "speaker": "이름", "portrait": "Portraits/파일", "text": "대사" } ] }
//   portrait = Resources 안 이미지 경로(확장자 빼고). 비우거나 없으면 초상화 칸만 빈다
// 조작: 아무 키·클릭 = 글 다 보이기 → 다음 줄. ESC = 전부 건너뛰기
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

    public float charDelay = 0.03f; // 글자 하나 나오는 시간

    public bool Playing { get; private set; }
    public double EndedDsp { get; private set; } // 끝난 시각 (AudioSettings.dspTime). 음악 예약과 같은 시계

    const float RefH = 1080f;
    const float Margin = 60f, BoxH = 300f, Pad = 30f, PortraitSize = 240f;
    static readonly Color Obsidian = new(0x0B / 255f, 0x12 / 255f, 0x15 / 255f);

    Line[] lines;
    int cur = -1;
    float shownAt; // 지금 줄이 뜬 시각 (unscaled: 설정 창으로 멈춰도 글은 나온다)

    GUIGroup box;
    GUIImage portrait;
    GUILabel nameLabel, textLabel, nextMark;
    GUIItem[] parts;
    float k;

    // 붙잡아 둬야 씬 로드 직후 UnloadUnusedAssets가 안 지운다 (DialogueManager와 같은 이유)
    Texture2D boxTexture;
    Font font;

    // 곡에 스토리가 있으면 host에 붙여 시작하고 돌려준다. 없으면 null
    public static StoryDialogue TryStart(GameObject host, string songId)
    {
        TextAsset json = Resources.Load<TextAsset>("Story/" + songId);
        if (json == null)
            return null;

        Line[] lines = JsonUtility.FromJson<StoryFile>(json.text)?.lines;
        if (lines == null || lines.Length == 0)
            return null;

        var story = host.AddComponent<StoryDialogue>();
        story.lines = lines;
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
        GUIManager.Unregister(box);
        Destroy(boxTexture);
    }

    void Update()
    {
        if (!Playing || cur < 0)
            return;

        Line l = lines[cur];
        int shown = Mathf.Min((int)((Time.unscaledTime - shownAt) / charDelay), l.text.Length);
        bool typing = shown < l.text.Length;

        // 안 나온 글자도 투명하게 같이 그린다. 안 그러면 타이핑 중에 줄바꿈 위치가 바뀜
        textLabel.Content.text = l.text.Substring(0, shown) + "<color=#00000000>" + l.text.Substring(shown) + "</color>";
        nextMark.Opacity = typing ? 0f : Mathf.PingPong(Time.unscaledTime * 2f, 1f);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            Finish();
        else if (Pressed())
        {
            if (typing)
                shownAt = Time.unscaledTime - l.text.Length * charDelay; // 글 다 보이기
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

        string lastSpeaker = cur >= 0 ? lines[cur].speaker : null;
        Line l = lines[++cur];
        shownAt = Time.unscaledTime;
        nameLabel.Content.text = l.speaker;

        // 말하는 사람이 바뀔 때만 초상화를 갈아 끼우며 옆에서 밀어 넣는다
        if (cur > 0 && l.speaker == lastSpeaker)
            return;

        Texture2D face = string.IsNullOrEmpty(l.portrait) ? null : Resources.Load<Texture2D>(l.portrait);
        if (face == null && !string.IsNullOrEmpty(l.portrait))
            Debug.LogWarning($"StoryDialogue: 초상화 없음 Resources/{l.portrait}", this);
        portrait.SetTexture(face);
        portrait.isVisible = face != null;
        if (cur > 0)
        {
            portrait.FadeIn(0.2f);
            portrait.MoveIn(new Vector2(-30f * k, 0f), 0.25f, 0f, TweenHelper.EaseOutQuad);
            nameLabel.FadeIn(0.2f);
        }
    }

    // 아웃트로: 아래로 내려가며 사라진 뒤 지운다. 음악은 EndedDsp 기준으로 Match가 시작한다
    void Finish()
    {
        if (!Playing)
            return;
        Playing = false;
        EndedDsp = AudioSettings.dspTime;

        box.FadeOut(0.3f, onComplete: () => GUIManager.Unregister(box));
        GUITween.Wave(parts, new Vector2(0f, (BoxH + Margin) * k), 0.3f, 0f, TweenHelper.EaseInQuad, inward: false);
    }

    // 1080p 기준 값에 화면 높이 배율 k를 곱해 화면 픽셀로 둔다 (GUI.matrix로 키우면 글자가 흐려짐)
    void Build()
    {
        k = Screen.height / RefH;
        int Px(float size) => Mathf.RoundToInt(size * k);

        font = Resources.Load<Font>("Fonts/A2Z5"); // 없으면 기본 폰트
        boxTexture = DialogueManager.RoundedBox(Color.white, Obsidian);
        var boxStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        boxStyle.normal.background = boxTexture;
        var nameStyle = new GUIStyle { font = font, fontSize = Px(32), fontStyle = FontStyle.Bold };
        nameStyle.normal.textColor = Obsidian;
        var textStyle = new GUIStyle { font = font, fontSize = Px(34), wordWrap = true, richText = true };
        textStyle.normal.textColor = Obsidian;
        var smallStyle = new GUIStyle { font = font, fontSize = Px(22), alignment = TextAnchor.LowerRight };
        smallStyle.normal.textColor = Obsidian;

        var r = new Rect(Margin * k, Screen.height - (BoxH + Margin) * k, Screen.width - Margin * 2f * k, BoxH * k);
        float pad = Pad * k, face = PortraitSize * k;
        float textX = r.x + pad * 2f + face, textW = r.xMax - pad - textX;

        box = Widget.Window(GUIContent.none, r, "story", boxStyle);
        box.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        portrait = Widget.Image(box, (Texture)null, new Rect(r.x + pad, r.y + (r.height - face) / 2f, face, face), ScaleMode.ScaleToFit);
        nameLabel = Widget.Label(box, "", new Rect(textX, r.y + pad, textW, 44f * k), nameStyle);
        textLabel = Widget.Label(box, "", new Rect(textX, r.y + pad + 54f * k, textW, r.height - pad * 2f - 54f * k), textStyle);
        nextMark = Widget.Label(box, "▼", new Rect(r.xMax - pad - 40f * k, r.yMax - pad - 30f * k, 40f * k, 30f * k), smallStyle);
        Widget.Label(box, "ESC 건너뛰기", new Rect(r.xMax - pad - 300f * k, r.y + pad, 300f * k, 30f * k), smallStyle).Opacity = 0.5f;
        box.SetLayer(150);          // HUD·튜토리얼 대사창 위, 보정 화면 아래
        box.isInteractable = false; // 클릭은 Update에서 직접 읽는다
        parts = new GUIItem[box.Childrens.Count + 1];
        parts[0] = box;
        for (int i = 0; i < box.Childrens.Count; i++)
            parts[i + 1] = box.Childrens[i];

        // 인트로: 아래에서 튀어 올라온다
        box.FadeIn(0.3f);
        GUITween.Wave(parts, new Vector2(0f, (BoxH + Margin) * k), 0.45f, 0f, TweenHelper.EaseOutBack);
    }
}
