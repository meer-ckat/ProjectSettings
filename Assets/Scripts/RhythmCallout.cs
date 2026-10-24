using System;
using IMGUI;
using UnityEngine;

// 노트 메시지(DialogueManager.Send)를 받아 화면 위쪽에 "이제 보컬 리듬!" 같은 배너를 띄운다. Mote처럼 튀어나왔다 사라짐
// 채보 노트의 message에 아래 callouts의 message를 적으면 그 박에 뜬다 (노트 에디터 "메시지" 칸)
// 씬에 없으면 Match가 자동으로 붙인다. 글·색·아이콘을 바꾸려면 씬의 Match 오브젝트에 이 컴포넌트를 직접 붙여서 인스펙터로
public class RhythmCallout : MonoBehaviour
{
    [Serializable]
    public class Callout
    {
        public string message;   // 노트에 적는 이름
        public string text;      // 화면에 뜨는 글
        public Color color = new(0.1f, 0.1f, 0.1f, 0.85f); // 리본 색
        public Texture2D icon;   // 없어도 됨. 있으면 글 왼쪽에
        public float hold;       // 다 나온 뒤 떠 있는 시간(초). 0이면 아래 기본 hold
    }

    public Callout[] callouts =
    {
        new() { message = "vocal", text = "이제 보컬 리듬!", color = new Color(0.85f, 0.3f, 0.55f, 0.9f) },
        new() { message = "drum", text = "다시 드럼 리듬!", color = new Color(0.95f, 0.55f, 0.15f, 0.9f) },
        new() { message = "chorus", text = "코러스! 몰아친다", color = new Color(0.25f, 0.5f, 0.95f, 0.9f) },
        new() { message = "climax", text = "클라이맥스!", color = new Color(0.95f, 0.75f, 0.1f, 0.95f) },
        new() { message = "finale", text = "마지막까지!", color = new Color(0.55f, 0.3f, 0.85f, 0.9f) },
        // 곡을 만든 사람 크레딧. 마지막 노트에 달아서 곡이 끝나는 순간 뜬다
        new() { message = "credit_as", text = "또 하나의 계절 · 작곡 최준오", color = new Color(0.12f, 0.16f, 0.28f, 0.92f), hold = 4f },
    };

    public float hold = 1.2f; // 다 나온 뒤 떠 있는 시간(초)

    const float RefH = 1080f;
    const float W = 620f, H = 96f, Top = 230f; // 1080p 기준. 가운데 레인 위쪽

    Match match;
    GUIGroup current; // 지금 떠 있는 배너. 새 배너가 오면 바로 갈아치운다 (Mote와 같음)
    GUIStyle textStyle;
    Font font; // 필드로 붙잡아야 씬 로드 직후 UnloadUnusedAssets가 안 지운다

    void Awake() => match = GetComponent<Match>();
    void OnEnable() => DialogueManager.Message += OnMessage;
    void OnDisable() => DialogueManager.Message -= OnMessage;
    void OnDestroy() => GUIManager.Unregister(current);

    void OnMessage(string message)
    {
        foreach (Callout c in callouts)
        {
            if (c.message == message)
            {
                Show(c);
                return;
            }
        }
    }

    void Show(Callout c)
    {
        GUIManager.Unregister(current);

        float k = Screen.height / RefH;
        if (textStyle == null)
        {
            font = Resources.Load<Font>("Fonts/A2Z5"); // 없으면 기본 폰트
            textStyle = new GUIStyle { font = font, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            textStyle.normal.textColor = Color.white;
        }
        textStyle.fontSize = Mathf.RoundToInt(44f * k);

        // 가로 가운데 = 가운데 레인(GuideB). 없으면 화면 가운데
        float cx = Screen.width / 2f;
        if (match != null && match.GuideB != null && Camera.main != null)
            cx = Camera.main.WorldToScreenPoint(match.GuideB.transform.position).x;
        // 폭은 기본 W, 글이 길면 글 폭 + 양옆 여백만큼 늘린다 (크레딧처럼 긴 글이 리본 밖으로 안 나가게)
        float textW = textStyle.CalcSize(new GUIContent(c.text)).x + (c.icon != null ? H * k : 0f);
        float w = Mathf.Max(W * k, textW + 80f * k);
        var r = new Rect(cx - w / 2f, Top * k, w, H * k);

        GUIGroup root = Widget.Window(GUIContent.none, r, "callout", GUIStyle.none);
        root.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        GUIImage ribbon = Widget.Image(root, GUIStyleMaker.Solid(new Color(c.color.r, c.color.g, c.color.b)), r);
        ribbon.Opacity = c.color.a;

        float textX = r.x;
        GUIImage icon = null;
        if (c.icon != null)
        {
            float s = r.height * 0.8f;
            icon = Widget.Image(root, c.icon, new Rect(r.x + r.height * 0.2f, r.y + r.height * 0.1f, s, s), ScaleMode.ScaleToFit);
            textX = icon.Rect.xMax;
        }
        GUILabel label = Widget.Label(root, c.text, new Rect(textX, r.y, r.xMax - textX, r.height), textStyle);

        root.SetLayer(120);          // HUD·튜토리얼 대사창 위, 스토리 창 아래
        root.isInteractable = false; // 클릭은 안 막는다
        current = root;

        // 인트로: 리본이 가운데서 양옆으로 펼쳐지고, 글이 아래에서 튀어 오른다, 아이콘은 통 튄다
        // 아웃트로: hold초 뒤 리본이 가운데로 접히며 사라지고, 끝나면 지운다
        // 페이드·크기 트윈은 위젯당 하나씩이라 바로 걸면 인트로가 끊긴다 → 인트로가 끝난 뒤에 건다
        float stay = c.hold > 0f ? c.hold : hold;
        root.FadeIn(0.1f, onComplete: () =>
            root.FadeOut(0.3f, stay + 0.1f, TweenHelper.EaseInQuad, () =>
            {
                GUIManager.Unregister(root);
                if (current == root)
                    current = null;
            }));
        ribbon.RenderScale = new Vector2(0f, 1f);
        ribbon.ScaleTo(Vector2.one, 0.2f, 0f, TweenHelper.EaseOutBack, () =>
            ribbon.ScaleTo(new Vector2(0f, 1f), 0.3f, stay, TweenHelper.EaseInQuad));
        label.MoveIn(new Vector2(0f, 24f * k), 0.3f, 0.05f, TweenHelper.EaseOutBack);
        label.FadeIn(0.15f, 0.05f);
        if (icon != null)
            icon.PunchScale(0.3f, 0.35f);
    }
}
