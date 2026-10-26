using System;
using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 이어폰/블루투스 딜레이 측정기. Match와 같은 오브젝트(아무 데나 OK)에 붙인다.
// 일정한 박자의 "삑"에 맞춰 키를 누르면, 들린 소리와 실제 재생 시각의 차이를 재서
// Match.audioOffset에 넣고 PlayerPrefs에 저장한다. 반응 속도·입력 지연·치는 습관도 같이 보정된다.
// 대사 이벤트 "Calibrate"로 시작하고(설정 창은 Begin 직접 호출), 시작하면 "CalibrationStart", 끝나면(취소 포함) "CalibrationDone"을 보낸다.
//
// 화면: 게임처럼 노트가 오른쪽에서 와서 판정선을 지난다. 노트 위치는 게임과 같은 시계(dsp + audioOffset)라
// 보정값을 바꾸면 노트가 삑보다 앞당겨지거나 늦어진다. 키를 누르면 그 순간 노트가 있던 자리에 눈금이 찍힌다
// (판정선 오른쪽 = 일찍 침, 왼쪽 = 늦게 침). 10번 재면 자동 보정 → 이어서 -10/-1/+1/+10ms 버튼으로 손으로 맞추고 완료.
// 화면은 I'm GUI 리테인드: Begin에서 만들어 페이드인, 끝나면 페이드아웃 뒤 지운다
public class LatencyCalibrator : MonoBehaviour
{
    public const string PrefsKey = "audioOffset";

    public Match match;

    [SerializeField] private float interval = 0.5f;  // 삑 간격(초)
    [SerializeField] private int countIn = 3;        // 처음 몇 번은 박자 익히기용으로 무시
    [SerializeField] private int taps = 10;          // 측정할 입력 수
    [SerializeField] private float fadeTime = 0.35f; // 등장/퇴장 시간
    [SerializeField] private float noteSpeed = 0.35f; // 노트 속도 (초당 화면 폭의 비율)

    // 삑보다 이만큼(초) 빨리 누른 것까지 같은 삑으로 친다. 측정 범위 = -Early ~ interval-Early
    // 가장 가까운 삑(±interval/2)으로 잡으면 블루투스처럼 250ms 넘게 밀릴 때 다음 삑으로 넘어가 버린다.
    private const double Early = 0.1;
    private const float NoteSize = 36f;
    private const int NotePool = 8; // 화면에 동시에 보이는 노트 수보다 넉넉하게

    private AudioSource clickSource;
    private readonly List<double> deltas = new(); // 측정값만
    private double startDsp;
    private bool running;      // 측정 중 (입력이 보정값 계산에 들어감)
    private bool tuning;       // 측정 끝, 버튼으로 손보는 중 (삑·노트는 계속)
    private bool closing;
    private float lastPress = -1f;
    private GUIStyle big, small, buttonStyle;

    // 화면. 전부 root 그룹 아래라 root의 투명도 하나로 같이 나타나고 사라진다
    private GUIGroup root;
    private GUIImage lane, hitLine;
    private readonly GUIImage[] notes = new GUIImage[NotePool];
    private GUILabel title, info, offsetLabel, esc;
    private float hitX, laneY, speedPx;

    private void Awake()
    {
        if (match == null)
            match = FindFirstObjectByType<Match>();

        // 30ms짜리 1kHz 삑 + 나머지 무음 = 정확히 interval 길이. loop로 돌리면 삑이 dsp 기준 정확한 간격으로 난다.
        int rate = AudioSettings.outputSampleRate;
        var data = new float[Mathf.RoundToInt(interval * rate)];
        for (int i = 0; i < rate * 0.03f; i++)
            data[i] = Mathf.Sin(2 * Mathf.PI * 1000 * i / rate) * 0.5f;

        var clip = AudioClip.Create("Click", data.Length, 1, rate, false);
        clip.SetData(data, 0);

        clickSource = gameObject.AddComponent<AudioSource>();
        clickSource.clip = clip;
        clickSource.loop = true;
        clickSource.playOnAwake = false;
    }

    private void OnEnable() => DialogueManager.Message += OnMessage;
    private void OnDisable() => DialogueManager.Message -= OnMessage;
    private void OnDestroy() => GUIManager.Unregister(root);

    private void OnMessage(string e)
    {
        if (e == "Calibrate")
            Begin();
    }

    public void Begin()
    {
        // 게임 정지. Restart로 음악·예약된 타건음·노트를 다 치운 뒤 곡 시작 코루틴만 끈다
        match.Restart();
        match.StopAllCoroutines();
        match.enabled = false;
        // 튜토리얼이 다음 단계를 기다리던 중이면 그 대기(끝나면 match.Play)를 끄게 알린다
        DialogueManager.Send("CalibrationStart");

        deltas.Clear();
        lastPress = -1f;
        closing = false;
        tuning = false;
        Build();

        // UI가 다 뜬 뒤에 삑 시작
        startDsp = AudioSettings.dspTime + fadeTime + 0.2;
        clickSource.PlayScheduled(startDsp);
        running = true;
    }

    // 10번 다 재면 자동 보정값 저장 후 손보기 단계로. 삑·노트는 계속 돈다
    private void Measured()
    {
        running = false;
        tuning = true;
        double median = Median(); // 한두 번 크게 빗나간 입력에 휘둘리지 않게
        SetOffset((float)-median);
        title.Content.text = "자동 보정 완료. 노트가 판정선에 닿을 때 삑이 들리게 버튼으로 맞추세요";
        info.Content.text = $"측정 중앙값 {Ms(median)}";
        esc.Content.text = "Enter / 완료 버튼  끝내기";
    }

    // 퇴장. 취소(ESC)든 완료든 여기로. 손으로 바꾼 값은 버튼 누를 때마다 이미 저장돼 있다
    private void CloseScreen()
    {
        if (closing)
            return;
        closing = true;
        running = tuning = false;
        clickSource.Stop();
        lane.ScaleTo(new Vector2(0f, 1f), fadeTime, 0f, TweenHelper.EaseInQuad);
        root.FadeOut(fadeTime, 0f, TweenHelper.EaseInQuad, Close);
    }

    // 퇴장 애니메이션 끝
    private void Close()
    {
        GUIManager.Unregister(root);
        root = null;
        match.enabled = true;
        match.Restart();
        DialogueManager.Send("CalibrationDone");
    }

    private void SetOffset(float offset)
    {
        match.audioOffset = offset;
        PlayerPrefs.SetFloat(PrefsKey, offset);
        PlayerPrefs.Save();
        offsetLabel.Content.text = OffsetText();
    }

    // 버튼: 보정값을 ms 단위로 더하고 뺀다. +면 노트가 일찍 와서 일찍 쳐도 맞고, -면 늦게 쳐도 맞는다
    private void Nudge(int ms)
    {
        if (closing)
            return;
        SetOffset(match.audioOffset + ms / 1000f);
    }

    private string OffsetText() =>
        $"보정값 {Mathf.RoundToInt(match.audioOffset * 1000f):+0;-0;0}ms   (+ : 일찍 치는 사람,  - : 늦게 치는 사람)";

    private double Median()
    {
        var sorted = new List<double>(deltas);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static string Ms(double sec) =>
        $"{(sec >= 0 ? "+" : "")}{Mathf.RoundToInt((float)(sec * 1000))}ms";

    private void Update()
    {
        if (root == null)
            return;

        // 판정선은 입력 직후 잠깐 밝아짐
        float flash = Mathf.Clamp01(1f - (Time.unscaledTime - lastPress) / 0.15f);
        hitLine.Opacity = 0.6f + 0.4f * flash;
        hitLine.RenderScale = new Vector2(1f + 2f * flash, 1f);

        MoveNotes();

        if (closing)
            return;

        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            CloseScreen(); // 측정 중이면 취소(보정값 그대로), 손보는 중이면 끝내기
            return;
        }

        if (tuning && kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
        {
            CloseScreen();
            return;
        }

        bool pressed =
            (kb != null && kb.anyKey.wasPressedThisFrame) ||
            (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame);
        if (!pressed)
            return;

        double t = AudioSettings.dspTime - startDsp;
        lastPress = Time.unscaledTime;

        // 눈금: 누른 순간 가장 가까운 노트가 판정선에서 얼마나 떨어져 있었나 (게임 판정과 같은 시계)
        double vis = t + match.audioOffset;
        double judge = vis - Math.Round(vis / interval) * interval; // +면 늦게 침
        bool counted = running && Math.Floor((t + Early) / interval) >= countIn;
        Tick(hitX - (float)judge * speedPx, counted);
        if (!running)
        {
            info.Content.text = $"마지막 {(judge > 0 ? "늦음" : "빠름")} {Mathf.Abs(Mathf.RoundToInt((float)judge * 1000f))}ms";
            return;
        }

        // 측정: 삑과의 차이. +면 소리보다 늦게 누름(= 이어폰 딜레이 + 반응 지연 + 습관)
        long k = (long)Math.Floor((t + Early) / interval);
        double delta = t - k * interval;
        if (!counted)
            return;

        deltas.Add(delta);
        info.Content.text = $"{deltas.Count} / {taps}    마지막 {Ms(delta)}    중앙값 {Ms(Median())}";
        if (deltas.Count >= taps)
            Measured();
    }

    // 노트 k번째는 게임 시계(dsp - startDsp + audioOffset)가 k*interval일 때 판정선에 닿는다 = 게임 노트와 같은 식
    private void MoveNotes()
    {
        bool beeping = running || tuning;
        double vis = AudioSettings.dspTime - startDsp + match.audioOffset;
        long first = (long)Math.Floor(vis / interval) - 1; // 막 지나간 노트부터
        for (int i = 0; i < NotePool; i++)
        {
            long b = first + i;
            float x = hitX + (float)((b * interval - vis) * speedPx);
            GUIImage n = notes[i];
            n.isVisible = beeping && b >= 0 && x > hitX - 80f && x < Screen.width + NoteSize;
            n.Rect.x = x - NoteSize / 2f;
            n.Opacity = x < hitX ? 0.3f : 1f; // 지나간 노트는 흐리게
        }
    }

    // 화면 픽셀 좌표 그대로. 레이어 200 = 대사창·HUD보다 위
    private void Build()
    {
        GUIManager.Unregister(root); // 퇴장 도중 다시 시작하면 남은 것을 치운다

        if (big == null)
        {
            big = GUIStyleMaker.Label(fontSize: 28, alignment: TextAnchor.MiddleCenter);
            small = GUIStyleMaker.Label(fontSize: 18, alignment: TextAnchor.MiddleCenter);
            buttonStyle = GUIStyleMaker.Button(fontSize: 22);
        }

        var white = Texture2D.whiteTexture;
        float w = Screen.width, h = Screen.height;
        laneY = h / 2f;
        hitX = w * 0.25f;
        speedPx = w * noteSpeed;

        root = Widget.Window(GUIContent.none, new Rect(0, 0, w, h), "calibrator", GUIStyle.none);
        root.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        Widget.Image(root, GUIStyleMaker.Solid(Color.black), new Rect(0, 0, w, h)).Opacity = 0.6f;

        // 레인 + 판정선 + 노트
        lane = Widget.Image(root, white, new Rect(0, laneY - NoteSize, w, NoteSize * 2f));
        lane.Opacity = 0.12f;
        hitLine = Widget.Image(root, white, new Rect(hitX - 2f, laneY - NoteSize, 4f, NoteSize * 2f));
        for (int i = 0; i < NotePool; i++)
        {
            notes[i] = Widget.Image(root, white, new Rect(-NoteSize, laneY - NoteSize / 2f, NoteSize, NoteSize));
            notes[i].isVisible = false;
        }
        Widget.Label(root, "빠름 →", new Rect(hitX + 10f, laneY + NoteSize + 4f, 120f, 25f), small).Opacity = 0.5f;
        Widget.Label(root, "← 늦음", new Rect(hitX - 130f, laneY + NoteSize + 4f, 120f, 25f), small).Opacity = 0.5f;

        title = Widget.Label(root, "노트가 판정선에 닿을 때 삑 소리에 맞춰 아무 키나 누르세요", new Rect(0, laneY - 150f, w, 40f), big);
        info = Widget.Label(root, "박자 익히는 중...", new Rect(0, laneY + 70f, w, 30f), small);

        // 손 보정: -10 -1 +1 +10 완료. 측정 중에도 누를 수 있다
        offsetLabel = Widget.Label(root, "", new Rect(0, laneY + 120f, w, 30f), small);
        offsetLabel.Content.text = OffsetText();
        float bw = 90f, bh = 44f, gap = 12f;
        int[] steps = { -10, -1, 1, 10 };
        float bx = w / 2f - (bw * 5f + gap * 4f) / 2f, by = laneY + 160f;
        for (int i = 0; i < steps.Length; i++)
        {
            int ms = steps[i];
            Widget.Button(root, (ms > 0 ? "+" : "") + ms, new Rect(bx + i * (bw + gap), by, bw, bh), () => Nudge(ms), buttonStyle);
        }
        Widget.Button(root, "완료", new Rect(bx + 4 * (bw + gap), by, bw, bh), CloseScreen, buttonStyle);

        esc = Widget.Label(root, "ESC  취소 (자동 측정 안 하고 지금 값으로)", new Rect(0, h - 50f, w, 30f), small);

        root.SetLayer(200);
        root.isInteractable = true; // 버튼을 눌러야 해서 클릭을 받는다. 뒤 HUD(Restart 등)도 같이 막힘

        // 인트로: 전체 페이드인, 레인은 가운데서 양옆으로 펼쳐지고, 글은 살짝 떠오른다
        root.FadeIn(fadeTime, 0f, TweenHelper.EaseOutQuad);
        lane.RenderScale = new Vector2(0f, 1f);
        lane.ScaleTo(Vector2.one, fadeTime, 0f, TweenHelper.EaseOutQuad);
        GUITween.Wave(new GUIItem[] { title, info }, new Vector2(0f, 20f), fadeTime, 0.05f, TweenHelper.EaseOutQuad);
    }

    // 누른 자리 눈금. 레인 아래 짧은 세로선. 측정에 안 들어간 것(박자 익히기·손보기 중)은 0.5초 뒤 사라진다
    private void Tick(float x, bool counted)
    {
        GUIImage bar = Widget.Image(root, Texture2D.whiteTexture, new Rect(x - 1.5f, laneY + NoteSize - 14f, 3f, 28f));
        bar.RenderScale = new Vector2(1f, 1.8f);
        bar.ScaleTo(Vector2.one, 0.15f, 0f, TweenHelper.EaseOutQuad);
        if (!counted)
            bar.FadeOut(0.5f, 0f, linear => linear, () => GUIManager.Unregister(bar));
    }
}
