using System;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// 디버그 메뉴: ↑↑↓↓←→←→BA 로 열고 닫는다 (닫기 버튼도 있음)
// 에디터·Development Build에서만 켜진다. 씬을 넘어 살아서 메뉴·게임 어디서나 된다 (GUIHost와 같은 방식)
// 스토리 도중에 열면 방향키·B·A·클릭이 대사를 넘기기도 한다 (StoryDialogue가 입력을 직접 읽음)
// 항목 추가 = Build()의 Items에 (글, 할 일) 한 줄. 할 일이 돌려준 글이 아래 상태 줄에 뜬다
class DebugKonami : MonoBehaviour
{
    static readonly Key[] Code =
    {
        Key.UpArrow, Key.UpArrow, Key.DownArrow, Key.DownArrow,
        Key.LeftArrow, Key.RightArrow, Key.LeftArrow, Key.RightArrow, Key.B, Key.A,
    };

    const float RefH = 1080f, W = 520f, Pad = 24f, RowH = 56f, RowGap = 10f;

    int next; // 다음에 와야 할 Code 칸
    GUIGroup panel;
    GUILabel status;
    GUIBoxLabel autoBadge; // 자동 퍼펙트 켜진 동안 왼쪽 위에 계속 뜬다 (켠 걸 잊고 치지 않게)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (!Debug.isDebugBuild)
            return;
        var go = new GameObject("DebugKonami");
        go.AddComponent<DebugKonami>();
        DontDestroyOnLoad(go);
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || !kb.anyKey.wasPressedThisFrame)
            return;

        foreach (KeyControl key in kb.allKeys)
        {
            if (!key.wasPressedThisFrame)
                continue;
            // 틀리면 처음부터. 틀린 키가 ↑면 ↑ 하나(↑↑ 뒤 ↑이면 ↑↑)는 이미 친 걸로 센다
            if (key.keyCode == Code[next])
                next++;
            else
                next = key.keyCode != Key.UpArrow ? 0 : next == 2 ? 2 : 1;

            if (next == Code.Length)
            {
                next = 0;
                if (panel == null)
                    Open();
                else
                    Close();
            }
        }
    }

    void OnDestroy()
    {
        Close();
        GUIManager.Unregister(autoBadge);
    }

    void Open()
    {
        if (!GUIStyleMaker.Initialized)
            return; // 첫 OnGUI 전. 거의 없는 경우라 다시 치면 된다

        float k = Screen.height / RefH;
        (string label, Func<string> run)[] items =
        {
            ("스토리 기록 지우기", ResetStories),
            ("치트: 자동 퍼펙트 켜기/끄기", ToggleAutoPlay),
            ("닫기", () => { Close(); return null; }),
        };

        float w = W * k, pad = Pad * k, rowH = RowH * k, gap = RowGap * k;
        float titleH = 50f * k, statusH = 40f * k;
        float h = pad * 2f + titleH + items.Length * (rowH + gap) + statusH;
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

        panel = Widget.Window(GUIContent.none, r, "debug", GUIStyleMaker.Box(new Color(0.06f, 0.07f, 0.08f, 0.92f)));
        panel.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        Widget.Label(panel, "DEBUG", new Rect(r.x + pad, r.y + pad, w - pad * 2f, titleH),
            GUIStyleMaker.Label(Color.yellow, Mathf.RoundToInt(34f * k), TextAnchor.MiddleLeft));

        float y = r.y + pad + titleH;
        GUIStyle button = GUIStyleMaker.Button(fontSize: Mathf.RoundToInt(24f * k));
        foreach (var (label, run) in items)
        {
            Widget.Button(panel, label, new Rect(r.x + pad, y, w - pad * 2f, rowH), () =>
            {
                string msg = run();
                if (msg != null && status != null)
                    status.Content.text = msg;
            }, button);
            y += rowH + gap;
        }
        status = Widget.Label(panel, "", new Rect(r.x + pad, y, w - pad * 2f, statusH),
            GUIStyleMaker.Label(Color.white, Mathf.RoundToInt(20f * k), TextAnchor.MiddleLeft));

        panel.SetLayer(300); // 스토리(150)·설정 창 위
        panel.FadeIn(0.15f);
    }

    void Close()
    {
        GUIManager.Unregister(panel);
        panel = null;
        status = null;
    }

    // 켜진 판은 최고기록·점수에 안 들어간다 (Match.FinishGame)
    string ToggleAutoPlay()
    {
        Match.AutoPlay = !Match.AutoPlay;
        GUIManager.Unregister(autoBadge);
        autoBadge = null;
        if (Match.AutoPlay)
        {
            float k = Screen.height / RefH;
            autoBadge = Widget.BoxLabel("AUTO PLAY (기록 안 남음)", new Rect(20f * k, 20f * k, 360f * k, 44f * k),
                GUIStyleMaker.Background(GUIStyleMaker.Label(Color.yellow, Mathf.RoundToInt(22f * k), TextAnchor.MiddleCenter),
                    new Color(0f, 0f, 0f, 0.6f), Color.yellow));
            autoBadge.SetLayer(300);
        }
        return Match.AutoPlay ? "자동 퍼펙트 켬. 이 상태로 친 판은 기록에 안 들어간다" : "자동 퍼펙트 끔";
    }

    static string ResetStories()
    {
        int n = 0;
        foreach (TextAsset story in Resources.LoadAll<TextAsset>("Story"))
        {
            string key = "story_seen_" + story.name;
            if (PlayerPrefs.HasKey(key))
                n++;
            PlayerPrefs.DeleteKey(key);
        }
        PlayerPrefs.Save();
        Debug.Log($"DebugKonami: 본 스토리 기록 {n}개 지움");
        return $"스토리 기록 {n}개 지움. 다음에 곡에 들어갈 때부터 다시 나온다";
    }
}
