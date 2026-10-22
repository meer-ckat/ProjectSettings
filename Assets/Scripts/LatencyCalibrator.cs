using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// 이어폰/블루투스 딜레이 측정기. Match와 같은 오브젝트(아무 데나 OK)에 붙인다.
// 일정한 박자의 "삑"에 맞춰 키를 누르면, 들린 소리와 실제 재생 시각의 차이를 재서
// Match.audioOffset에 넣고 PlayerPrefs에 저장한다. 반응 속도·입력 지연도 같이 보정된다.
// 대사 이벤트 "Calibrate"로 시작하고, 끝나면(취소 포함) "CalibrationDone"을 보낸다.
//
// 화면: 가로 막대 = 시간축. 세로선 = 삑이 실제로 난 시각(0ms), 삑마다 튀어오른다.
// 키를 누르면 누른 시각 자리에 십자. 딜레이가 있으면 십자가 전부 세로선 오른쪽에 몰린다.
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

    private struct Hit
    {
        public double delta; // 삑과의 차이(초). +면 늦게 누름
        public float time;   // 누른 실시간(Time.unscaledTime). 애니메이션용
        public bool counted; // count-in 구간이면 false (찍혔다 사라지는 유령 십자)
    }

    private AudioSource clickSource;
    private readonly List<double> deltas = new(); // 측정값만
    private readonly List<Hit> hits = new();      // 화면에 찍을 것 전부
    private double startDsp;
    private bool running;      // 입력 받는 중
    private bool open;         // UI 켜짐. false면 퇴장 애니메이션
    private bool closePending; // 퇴장 끝나면 Close()
    private float closeAt;     // 이 시각 지나면 퇴장 시작
    private float vis;         // 0 = 안 보임, 1 = 다 보임
    private float lastPress = -1f;
    private string result;     // 끝났을 때 문구
    private GUIStyle big, small;

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
        hits.Clear();
        result = null;
        lastPress = -1f;
        open = true;
        closePending = false;

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
            result = $"딜레이 {Ms(median)} 저장됨";
        }
        else
            result = "취소";

        // 결과 잠깐 보여주고 퇴장. 퇴장 끝나면 Close()
        closeAt = Time.unscaledTime + (apply ? resultHold : 0f);
        closePending = true;
    }

    // 퇴장 애니메이션 끝
    private void Close()
    {
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

    private void Update()
    {
        // 등장/퇴장
        if (closePending && Time.unscaledTime >= closeAt)
            open = false;
        vis = Mathf.MoveTowards(vis, open ? 1f : 0f, Time.unscaledDeltaTime / fadeTime);
        if (closePending && !open && vis <= 0f)
        {
            closePending = false;
            Close();
        }

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

        hits.Add(new Hit { delta = delta, time = Time.unscaledTime, counted = counted });
        lastPress = Time.unscaledTime;
        if (!counted)
            return;

        deltas.Add(delta);
        if (deltas.Count >= taps)
            Finish(true);
    }

    private void OnGUI()
    {
        if (vis <= 0f)
            return;
        if (big == null)
        {
            big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 28 };
            small = new GUIStyle(big) { fontSize = 18 };
        }

        var tex = Texture2D.whiteTexture;
        float w = Screen.width, cy = Screen.height / 2f, now = Time.unscaledTime;
        float e = 1f - (1f - vis) * (1f - vis); // ease-out
        float ToX(double d) => (float)((d + Early) / interval) * w;

        // 배경
        GUI.color = new Color(0, 0, 0, 0.6f * e);
        GUI.DrawTexture(new Rect(0, 0, w, Screen.height), tex);

        // 시간축. 가운데서 양옆으로 펼쳐지고, 입력 직후 잠깐 밝아짐
        float flash = Mathf.Clamp01(1f - (now - lastPress) / 0.15f);
        GUI.color = new Color(1, 1, 1, (0.25f + 0.4f * flash) * e);
        GUI.DrawTexture(new Rect(w / 2f * (1f - e), cy - 10f, w * e, 20f), tex);

        // 삑 시각(0ms) 세로선. 삑마다 길어졌다 돌아옴 = 눈으로 보이는 진짜 박자.
        // 이어폰 딜레이가 있으면 이게 소리보다 먼저 튀는 게 정상
        double phase = (AudioSettings.dspTime - startDsp) % interval;
        float beat = running && phase >= 0 ? Mathf.Clamp01(1f - (float)phase / 0.15f) : 0f;
        float h = 40f + 30f * beat;
        GUI.color = new Color(1, 1, 1, (0.6f + 0.4f * beat) * e);
        GUI.DrawTexture(new Rect(ToX(0) - 1f, cy - h / 2f, 2f, h), tex);

        // 십자. 크게 떴다가 제자리로. count-in 것은 0.5초 뒤 사라짐
        foreach (var hit in hits)
        {
            float age = now - hit.time;
            float pop = 1f - Mathf.Pow(1f - Mathf.Clamp01(age / 0.15f), 2);
            float size = 12f + 14f * (1f - pop);
            float a = hit.counted ? 1f : 1f - Mathf.Clamp01(age / 0.5f);
            if (a <= 0f)
                continue;
            GUI.color = new Color(1, 1, 1, a * e);
            Cross(ToX(hit.delta), cy - 30f, size);
        }

        // 축 눈금
        GUI.color = new Color(1, 1, 1, 0.5f * e);
        GUI.Label(new Rect(0, cy + 12f, 100f, 25f), Ms(-Early), small);
        GUI.Label(new Rect(ToX(0) - 50f, cy + 12f, 100f, 25f), "0", small);
        GUI.Label(new Rect(w - 100f, cy + 12f, 100f, 25f), Ms(interval - Early), small);

        // 글. 등장할 때 살짝 떠오름
        float dy = (1f - e) * 20f;
        GUI.color = new Color(1, 1, 1, e);
        GUI.Label(new Rect(0, cy - 120f + dy, w, 40f), result ?? "삑 소리에 맞춰 아무 키나 누르세요", big);

        if (running)
        {
            string info = deltas.Count == 0
                ? "박자 익히는 중..."
                : $"{deltas.Count} / {taps}    마지막 {Ms(deltas[^1])}    중앙값 {Ms(Median())}";
            GUI.Label(new Rect(0, cy + 45f + dy, w, 30f), info, small);
            GUI.Label(new Rect(0, Screen.height - 50f, w, 30f), "ESC  취소", small);
        }

        GUI.color = Color.white;
    }

    private static void Cross(float x, float y, float s)
    {
        var tex = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(x - s / 2f, y - 1f, s, 2f), tex);
        GUI.DrawTexture(new Rect(x - 1f, y - s / 2f, 2f, s), tex);
    }
}