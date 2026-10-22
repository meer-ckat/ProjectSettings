using System;
using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 이어폰/블루투스 딜레이 측정기. Match와 같은 오브젝트(아무 데나 OK)에 붙인다.
// 일정한 박자의 "삑"에 맞춰 키를 누르면, 들린 소리와 실제 재생 시각의 차이를 재서
// Match.audioOffset에 넣고 PlayerPrefs에 저장한다. 반응 속도·입력 지연도 같이 보정된다.
// 대사 이벤트 "Calibrate"로 시작하고, 끝나면(취소 포함) "CalibrationDone"을 보낸다.
//
// 화면: 가로 막대 = 시간축. 세로선 = 삑이 실제로 난 시각(0ms), 삑마다 튀어오른다.
// 키를 누르면 누른 시각 자리에 십자. 딜레이가 있으면 십자가 전부 세로선 오른쪽에 몰린다.
// 화면은 I'm GUI 리테인드: Begin에서 만들어 페이드인, 끝나면 페이드아웃 뒤 지운다
public class LatencyCalibrator : MonoBehaviour
{
    public const string PrefsKey = "audioOffset";

    public Match match;

    [SerializeField] private float interval = 0.5f;  // 삑 간격(초)
    [SerializeField] private int countIn = 3;        // 처음 몇 번은 박자 익히기용으로 무시
    [SerializeField] private int taps = 10;          // 측정할 입력 수
    [SerializeField] private float fadeTime = 0.35f; // 등장/퇴장 시간
    [SerializeField] private float resultHold = 1f;  // 끝나고 결과 보여주는 시간

    // 삑보다 이만큼(초) 빨리 누른 것까지 같은 삑으로 친다. 측정 범위 = -Early ~ interval-Early
    // 가장 가까운 삑(±interval/2)으로 잡으면 블루투스처럼 250ms 넘게 밀릴 때 다음 삑으로 넘어가 버린다.
    private const double Early = 0.1;

    private AudioSource clickSource;
    private readonly List<double> deltas = new(); // 측정값만
    private double startDsp;
    private bool running;      // 입력 받는 중
    private bool closePending; // closeAt 지나면 퇴장
    private float closeAt;
    private float lastPress = -1f;
    private GUIStyle big, small;

    // 화면. 전부 root 그룹 아래라 root의 투명도 하나로 같이 나타나고 사라진다
    private GUIGroup root;
    private GUIImage axis, zero;
    private GUILabel title, info, esc;
    private float axisW;

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

        deltas.Clear();
        lastPress = -1f;
        closePending = false;
        Build();

        // UI가 다 뜬 뒤에 삑 시작
        startDsp = AudioSettings.dspTime + fadeTime + 0.2;
        clickSource.PlayScheduled(startDsp);
        running = true;
    }

    private void Finish(bool apply)
    {
        clickSource.Stop();
        running = false;

        if (apply)
        {
            double median = Median(); // 한두 번 크게 빗나간 입력에 휘둘리지 않게
            SetOffset((float)-median);
            title.Content.text = $"딜레이 {Ms(median)} 저장됨";
        }
        else
            title.Content.text = "취소";

        info.isVisible = esc.isVisible = false;

        // 결과 잠깐 보여주고 퇴장. 퇴장 끝나면 Close()
        closeAt = Time.unscaledTime + (apply ? resultHold : 0f);
        closePending = true;
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
    }

    private double Median()
    {
        var sorted = new List<double>(deltas);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static string Ms(double sec) =>
        $"{(sec >= 0 ? "+" : "")}{Mathf.RoundToInt((float)(sec * 1000))}ms";

    private float ToX(double d) => (float)((d + Early) / interval) * axisW;

    private void Update()
    {
        if (root == null)
            return;

        // 퇴장: 시간축이 가운데로 접히고 전체가 사라진 뒤 지운다
        if (closePending && Time.unscaledTime >= closeAt)
        {
            closePending = false;
            axis.ScaleTo(new Vector2(0f, 1f), fadeTime, 0f, TweenHelper.EaseInQuad);
            root.FadeOut(fadeTime, 0f, TweenHelper.EaseInQuad, Close);
        }

        // 시간축은 입력 직후 잠깐 밝아짐
        float flash = Mathf.Clamp01(1f - (Time.unscaledTime - lastPress) / 0.15f);
        axis.Opacity = 0.25f + 0.4f * flash;

        // 삑 시각(0ms) 세로선. 삑마다 길어졌다 돌아옴 = 눈으로 보이는 진짜 박자.
        // 이어폰 딜레이가 있으면 이게 소리보다 먼저 튀는 게 정상
        double phase = (AudioSettings.dspTime - startDsp) % interval;
        float beat = running && phase >= 0 ? Mathf.Clamp01(1f - (float)phase / 0.15f) : 0f;
        zero.RenderScale = new Vector2(1f, 1f + 0.75f * beat); // 40 → 70
        zero.Opacity = 0.6f + 0.4f * beat;

        if (!running)
            return;

        bool pressed =
            (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) ||
            (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Finish(false);
            return;
        }

        if (!pressed)
            return;

        // 삑과의 차이. +면 소리보다 늦게 누름(= 이어폰 딜레이 + 반응 지연)
        double t = AudioSettings.dspTime - startDsp;
        long k = (long)System.Math.Floor((t + Early) / interval);
        double delta = t - k * interval;
        bool counted = k >= countIn;

        Cross(ToX(delta), Screen.height / 2f - 30f, counted);
        lastPress = Time.unscaledTime;
        if (!counted)
            return;

        deltas.Add(delta);
        info.Content.text = $"{deltas.Count} / {taps}    마지막 {Ms(delta)}    중앙값 {Ms(Median())}";
        if (deltas.Count >= taps)
            Finish(true);
    }

    // 화면 픽셀 좌표 그대로. 레이어 200 = 대사창·HUD보다 위
    private void Build()
    {
        GUIManager.Unregister(root); // 퇴장 도중 다시 시작하면 남은 것을 치운다

        if (big == null)
        {
            big = GUIStyleMaker.Label(fontSize: 28, alignment: TextAnchor.MiddleCenter);
            small = GUIStyleMaker.Label(fontSize: 18, alignment: TextAnchor.MiddleCenter);
        }

        var white = Texture2D.whiteTexture;
        float w = axisW = Screen.width, h = Screen.height, cy = h / 2f;

        root = Widget.Window(GUIContent.none, new Rect(0, 0, w, h), "calibrator", GUIStyle.none);
        root.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        Widget.Image(root, GUIStyleMaker.Solid(Color.black), new Rect(0, 0, w, h)).Opacity = 0.6f;
        axis = Widget.Image(root, white, new Rect(0, cy - 10f, w, 20f));
        zero = Widget.Image(root, white, new Rect(ToX(0) - 1f, cy - 20f, 2f, 40f));

        // 축 눈금
        Widget.Label(root, Ms(-Early), new Rect(0, cy + 12f, 100f, 25f), small).Opacity = 0.5f;
        Widget.Label(root, "0", new Rect(ToX(0) - 50f, cy + 12f, 100f, 25f), small).Opacity = 0.5f;
        Widget.Label(root, Ms(interval - Early), new Rect(w - 100f, cy + 12f, 100f, 25f), small).Opacity = 0.5f;

        title = Widget.Label(root, "삑 소리에 맞춰 아무 키나 누르세요", new Rect(0, cy - 120f, w, 40f), big);
        info = Widget.Label(root, "박자 익히는 중...", new Rect(0, cy + 45f, w, 30f), small);
        esc = Widget.Label(root, "ESC  취소", new Rect(0, h - 50f, w, 30f), small);

        root.SetLayer(200);
        root.isInteractable = false; // 화면을 덮지만 클릭은 안 막는다

        // 인트로: 전체 페이드인, 시간축은 가운데서 양옆으로 펼쳐지고, 글은 살짝 떠오른다
        root.FadeIn(fadeTime, 0f, TweenHelper.EaseOutQuad);
        axis.RenderScale = new Vector2(0f, 1f);
        axis.ScaleTo(Vector2.one, fadeTime, 0f, TweenHelper.EaseOutQuad);
        GUITween.Wave(new GUIItem[] { title, info }, new Vector2(0f, 20f), fadeTime, 0.05f, TweenHelper.EaseOutQuad);
    }

    // 십자. 크게 떴다가 제자리로. count-in 것은 0.5초 동안 사라진 뒤 지운다
    private void Cross(float x, float y, bool counted)
    {
        const float s = 12f, pop = 26f / s;
        var white = Texture2D.whiteTexture;
        GUIImage hBar = Widget.Image(root, white, new Rect(x - s / 2f, y - 1f, s, 2f));
        GUIImage vBar = Widget.Image(root, white, new Rect(x - 1f, y - s / 2f, 2f, s));
        hBar.RenderScale = new Vector2(pop, 1f);
        vBar.RenderScale = new Vector2(1f, pop);

        foreach (GUIImage bar in new[] { hBar, vBar })
        {
            bar.ScaleTo(Vector2.one, 0.15f, 0f, TweenHelper.EaseOutQuad);
            if (!counted)
                bar.FadeOut(0.5f, 0f, linear => linear, () => GUIManager.Unregister(bar));
        }
    }
}
