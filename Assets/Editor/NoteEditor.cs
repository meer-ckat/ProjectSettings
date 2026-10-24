using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Interaction;
using UnityEditor;
using UnityEngine;

// Tools > Note Editor
// 레인 빈 곳 클릭 또는 A/S/D(재생 바 위치): 노트 추가 / Space: 재생·정지 / 왼쪽 숫자 칸·가이드 패널 클릭: 재생 바 이동
// 노트 드래그: 이동(좌우로 끌면 레인 변경, 선택된 노트는 묶음째) / 우클릭 또는 Delete: 삭제
// Shift+클릭·Shift+끌기: 여러 개 선택 / Ctrl+C·X·V: 복사·잘라내기·재생 바 자리에 붙여넣기 / Ctrl+A: 전체 선택
// 휠: 스크롤 / Ctrl+휠: 확대·축소 / Ctrl+Z: 되돌리기
// 가이드 MIDI를 넣으면 레인 오른쪽 패널에 음이 피아노 롤로 나온다 (노트는 직접 찍음)
public class NoteEditor : EditorWindow
{
    [Serializable]
    private class EditorNote
    {
        public float beat; // 곡 시작부터의 박 (BPM 기준)
        public int lane;   // 0=A, 1=B, 2=C
        public string message; // 이 박에 게임이 DialogueManager.Send로 보낼 메시지. 비면 안 보냄
    }

    // NoteManager가 읽는 포맷 (MidiToStage와 같음)
    [Serializable]
    private class StageNote
    {
        public float interval;
        public int input;
        public string message;
    }

    [Serializable]
    private class Stage
    {
        public float bpm;
        public float gridOffset; // 첫 박이 나오는 시각(초). 에디터 그리드용, 게임은 안 씀
        public List<StageNote> noteList = new();
    }

    private const float Gutter = 48f;
    private const float NoteHeight = 12f;
    private const float HitFlash = 0.2f; // 재생 중 친 노트 강조 시간(초)

    private static readonly KeyCode[] LaneKeys = { KeyCode.A, KeyCode.S, KeyCode.D };
    private static readonly string[] LaneNames = { "A", "B", "C" };
    private static readonly Color[] LaneColors =
    {
        new(1f, 0.45f, 0.45f),
        new(0.45f, 1f, 0.55f),
        new(0.45f, 0.65f, 1f)
    };

    private static readonly int[] Divisions = { 1, 2, 3, 4, 6, 8, 12, 16 };
    private static readonly string[] DivisionNames = Divisions.Select(d => $"1/{d} 박").ToArray();

    [SerializeField] private TextAsset json;
    [SerializeField] private AudioClip music;
    [SerializeField] private float bpm = 120f;
    [SerializeField] private float gridOffset;
    [SerializeField] private bool grid = true;
    [SerializeField] private int division = 4;
    [SerializeField] private float pxPerBeat = 80f;
    [SerializeField] private float speed = 1f; // 재생 속도. pitch로 바꿔서 음높이도 같이 바뀜
    [SerializeField] private List<EditorNote> notes = new();
    [SerializeField] private DefaultAsset midi;
    [SerializeField] private float midiShift; // 가이드를 음악에 맞추는 값(초). MIDI 앞에 카운트인이 있으면 -

    [SerializeField] private bool midiSnap = true;
    // 스냅할 MIDI 채널 비트(채널 0~15). 기본은 드럼(ch10) 뺌: 드럼은 16분음표마다 있어서 다 붙어 버림
    [SerializeField] private int snapChannels = ~(1 << 9);
    private int[] guideChannels = Array.Empty<int>();
    private string[] guideChannelNames = Array.Empty<string>();

    private const float GuideWidth = 160f;
    private const float MidiSnapPx = 10f; // 이 거리(화면 픽셀) 안에 MIDI 음 시작이 있으면 그리드 대신 거기에 붙는다
    private readonly List<(float start, float end, int pitch, int channel)> guide = new(); // 초 단위
    private float[] guideStarts = Array.Empty<float>(); // 스냅 채널의 정렬된 음 시작 시각(초), 스냅 검색용
    private int minPitch, maxPitch;
    private float midiBpm, midiStart; // MIDI 주 템포와 그 구간이 시작하는 시각(초)

    private float playheadBeat;
    private float scroll; // 타임라인 맨 위가 가리키는 픽셀 위치
    private Rect timeline;

    private AudioSource source;
    private bool playing;

    private readonly HashSet<EditorNote> selected = new();
    private EditorNote dragging; // 끌고 있는 노트. 선택된 나머지도 같이 움직인다
    private float dragOffset;
    private Vector2? boxStart; // Shift+끌기 선택 상자
    private Vector2 boxEnd;

    // static이라 다른 곡 JSON을 열어도 남아서 곡끼리 복사할 수 있다. beat는 맨 앞 노트 기준
    private static readonly List<EditorNote> clipboard = new();

    private readonly HashSet<KeyCode> held = new(); // 키 꾹 누를 때 반복 입력 막기

    [MenuItem("Tools/Note Editor")]
    private static void Open() => GetWindow<NoteEditor>("Note Editor");

    private void OnEnable()
    {
        Undo.undoRedoPerformed += OnUndoRedo;
        LoadMidi(); // 가이드 목록은 직렬화 안 되니 스크립트 리로드 후 다시 읽는다
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        if (source != null)
            DestroyImmediate(source.gameObject);
    }

    private void OnLostFocus() => held.Clear(); // 포커스 밖에서 뗀 키는 KeyUp이 안 옴

    private void OnUndoRedo()
    {
        selected.Clear(); // 되돌리면 리스트 객체가 새로 만들어짐
        dragging = null;
        Repaint();
    }

    private float BeatToSeconds(float beat) => beat * 60f / bpm;
    private float SecondsToBeat(float seconds) => seconds * bpm / 60f;
    private float OffsetBeats => SecondsToBeat(gridOffset);

    // 이어폰 딜레이: 게임(Match.audioOffset)과 같은 값·같은 부호. 재생 바 = 귀에 들리는 시각
    private static float AudioOffset
    {
        get => PlayerPrefs.GetFloat(LatencyCalibrator.PrefsKey, 0f);
        set => PlayerPrefs.SetFloat(LatencyCalibrator.PrefsKey, value);
    }

    // 딜레이는 실제 시간이라 느리게 틀면 음악 시간으로는 그만큼 짧아진다
    private float MusicOffset => AudioOffset * speed;

    // MIDI 스냅이 켜져 있으면 가까운 MIDI 음 시작에 먼저 붙고, 없으면 그리드 선(OffsetBeats + k/division)에 붙는다
    private float Snap(float beat)
    {
        if (midiSnap && guideStarts.Length > 0)
        {
            float sec = BeatToSeconds(beat) - midiShift;
            int i = Array.BinarySearch(guideStarts, sec);
            if (i < 0)
                i = ~i;

            float best = float.MaxValue;
            for (int j = Mathf.Max(0, i - 1); j <= Mathf.Min(guideStarts.Length - 1, i); j++)
                if (Mathf.Abs(guideStarts[j] - sec) < Mathf.Abs(best - sec))
                    best = guideStarts[j];

            float snapped = SecondsToBeat(best + midiShift);
            if (Mathf.Abs(snapped - beat) * pxPerBeat <= MidiSnapPx)
                return Mathf.Max(0f, snapped);
        }

        return Mathf.Max(0f, grid ? Mathf.Round((beat - OffsetBeats) * division) / division + OffsetBeats : beat);
    }
    private float Y(float beat) => beat * pxPerBeat - scroll;
    private float BeatAt(float y) => (y + scroll) / pxPerBeat;
    private float LaneWidth => Mathf.Min(120f, (timeline.width - Gutter - (guide.Count > 0 ? GuideWidth : 0f)) / 3f);

    private Rect BoxRect => Rect.MinMaxRect(Mathf.Min(boxStart.Value.x, boxEnd.x), Mathf.Min(boxStart.Value.y, boxEnd.y),
                                            Mathf.Max(boxStart.Value.x, boxEnd.x), Mathf.Max(boxStart.Value.y, boxEnd.y));

    private Rect NoteRect(EditorNote n) =>
        new(Gutter + n.lane * LaneWidth + 4f, Y(n.beat) - NoteHeight / 2f, LaneWidth - 8f, NoteHeight);

    private void Update()
    {
        if (calibrating)
        {
            if (EditorApplication.timeSinceStartup > calibrationEnd)
                FinishCalibration(false); // 삑이 다 끝날 때까지 다 못 누름
            Repaint();
        }

        if (!playing)
            return;

        if (source == null)
        {
            playing = false;
            return;
        }

        // Play() 직후엔 isPlaying이 한동안 false일 수 있어서, 곡 끝에 닿았을 때만 끝난 걸로 본다
        if (!source.isPlaying)
        {
            if (BeatToSeconds(playheadBeat) - MusicOffset >= music.length - 0.05f)
            {
                playing = false;
                Repaint();
            }
            return;
        }

        playheadBeat = Mathf.Max(0f, SecondsToBeat(source.timeSamples / (float)music.frequency + MusicOffset));
        Repaint();
    }

    private void Play()
    {
        if (music == null)
        {
            ShowNotification(new GUIContent("레퍼런스 음악을 넣으세요"));
            return;
        }

        if (calibrating)
            FinishCalibration(false);
        EnsureSource();

        if (BeatToSeconds(playheadBeat) >= music.length)
            playheadBeat = 0f;

        // 재생 바 위치에서 이어서 재생 (Play 앞뒤로 위치를 지정해야 에디터에서 확실히 먹힘)
        source.clip = music;
        source.pitch = speed;
        Seek(playheadBeat);
        source.Play();
        Seek(playheadBeat);
        playing = true;
    }

    private void EnsureSource()
    {
        if (source != null)
            return;

        source = EditorUtility.CreateGameObjectWithHideFlags(
            "NoteEditorAudio", HideFlags.HideAndDontSave, typeof(AudioSource)).GetComponent<AudioSource>();
        source.playOnAwake = false;
    }

    // 노트는 음악 시각(초)에 고정하고 그리드만 바꾼다
    private void SetBpm(float newBpm)
    {
        if (Mathf.Approximately(newBpm, bpm))
            return;

        Undo.RecordObject(this, "Change BPM");
        float scale = newBpm / bpm;
        foreach (EditorNote n in notes)
            n.beat *= scale;
        playheadBeat *= scale;
        scroll *= scale;
        bpm = newBpm;
    }

    private void AutoBpm()
    {
        // MIDI엔 정확한 템포가 들어 있으니 음악 소리 분석(추정)보다 그쪽을 쓴다
        if (guide.Count > 0)
        {
            SetBpm((float)Math.Round(midiBpm, 4));
            float bar = 4f * 60f / bpm; // ponytail: 4/4 고정, 그리드와 같음
            gridOffset = (float)Math.Round(((midiStart + midiShift) % bar + bar) % bar, 4);
            ShowNotification(new GUIContent($"MIDI 기준 BPM {bpm:0.##} / 첫 박 {gridOffset:0.000}초"));
            return;
        }

        float[] mono = ReadMono();
        if (mono == null)
            return;

        var (detectedBpm, offset) = DetectTempo(mono, music.frequency);
        SetBpm(detectedBpm);
        gridOffset = (float)Math.Round(offset, 4);
        ShowNotification(new GUIContent($"BPM {detectedBpm:0.##} / 첫 박 {offset:0.000}초"));
    }

    // 음악은 게임 기준(0초부터 재생)이라 못 옮긴다. 대신 MIDI를 음악에 맞는 밀기 값을 ±10초 범위에서 찾는다.
    // 음 시작↔어택만 맞추면 박마다 비슷하게 겹쳐서 한 박씩 틀리기 쉽다. 그래서
    // 1) 1초로 뭉갠 밀도(조용한 구간·터지는 구간 같은 곡 구조)로 대략 위치 → 2) 그 근처 ±0.2초에서 어택에 정밀하게
    private void AlignMidi()
    {
        float[] mono = ReadMono();
        if (mono == null)
            return;

        var (onset, fps) = Onsets(mono, music.frequency);
        int n = onset.Length, maxLag = (int)(10 * fps), win = (int)fps;
        float[] starts = guide.Select(g => g.start).Distinct().ToArray(); // 스냅 채널과 상관없이 전부(드럼이 맞추기엔 제일 좋음)

        var midiFrames = new double[n];
        foreach (float start in starts)
        {
            int i = (int)Math.Round(start * fps);
            if (i >= 0 && i < n)
                midiFrames[i] += 1;
        }

        double[] a = Smooth(onset, win), m = Smooth(midiFrames, win);
        int coarse = BestLag(-maxLag, maxLag, 5, lag =>
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < n && i + lag < n; i++)
                s += m[i] * a[i + lag];
            return s;
        });

        int fineRange = (int)(0.2 * fps);
        int best = BestLag(coarse - fineRange, coarse + fineRange, 1, lag =>
        {
            double s = 0;
            foreach (float start in starts)
            {
                int i = (int)Math.Round(start * fps) + lag;
                if (i >= 0 && i < n)
                    s += onset[i];
            }
            return s;
        });

        Undo.RecordObject(this, "Align MIDI");
        midiShift = (float)Math.Round(best / fps, 3);
        ShowNotification(new GUIContent($"MIDI 밀기 {midiShift:0.000}초"));
    }

    private static int BestLag(int from, int to, int step, Func<int, double> score)
    {
        int best = from;
        double bestScore = double.MinValue;
        for (int lag = from; lag <= to; lag += step)
        {
            double s = score(lag);
            if (s > bestScore)
            {
                bestScore = s;
                best = lag;
            }
        }
        return best;
    }

    // win 프레임 이동 평균에서 전체 평균을 뺀 것 (상관을 구할 때 소리 큰 곡이 무조건 이기지 않게)
    private static double[] Smooth(double[] x, int win)
    {
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++)
            prefix[i + 1] = prefix[i] + x[i];

        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int lo = Math.Max(0, i - win / 2), hi = Math.Min(x.Length, i + win / 2 + 1);
            y[i] = (prefix[hi] - prefix[lo]) / (hi - lo);
        }

        double mean = y.Average();
        for (int i = 0; i < y.Length; i++)
            y[i] -= mean;
        return y;
    }

    // 레퍼런스 음악을 모노 샘플로. 못 읽으면 이유를 알리고 null
    private float[] ReadMono()
    {
        if (music == null)
        {
            ShowNotification(new GUIContent("레퍼런스 음악을 넣으세요"));
            return null;
        }

        music.LoadAudioData();
        var data = new float[music.samples * music.channels];
        if (!music.GetData(data, 0))
        {
            ShowNotification(new GUIContent("음악 Import 설정에서 Load Type을 Decompress On Load로 바꾸세요"));
            return null;
        }

        var mono = new float[music.samples];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < music.channels; c++)
                sum += data[i * music.channels + c];
            mono[i] = sum / music.channels;
        }

        return mono;
    }

    // 박 검출: 소리가 커지는 순간(onset) → 자기상관으로 대략 BPM → 박 위상에 onset이 가장 몰리는 BPM으로 정밀화.
    // ponytail: 템포 고정 곡 전제. 템포가 바뀌는 곡은 가장 강한 템포 하나만 나옴
    private static (float bpm, float offset) DetectTempo(float[] mono, int rate)
    {
        var (onset, fps) = Onsets(mono, rate);
        int frames = onset.Length;

        // 1) 자기상관, 60~200 BPM. 120 근처를 선호해서 절반/두 배 BPM 오검출을 줄인다 (그래도 틀리면 ÷2 ×2 버튼)
        double mean = onset.Average();
        int minLag = (int)Math.Floor(fps * 60 / 200), maxLag = (int)Math.Ceiling(fps * 60 / 60);
        var ac = new double[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
            for (int i = 0; i + lag < frames; i++)
                ac[lag] += (onset[i] - mean) * (onset[i + lag] - mean);

        int best = minLag;
        double bestScore = double.MinValue;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double octaves = Math.Log(60 * fps / lag / 120, 2) / 0.7;
            double score = ac[lag] * Math.Exp(-0.5 * octaves * octaves);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        double a = ac[best - 1], b = ac[best], c = ac[best + 1], denom = a - 2 * b + c;
        double coarse = 60 * fps / (best + (denom < 0 ? 0.5 * (a - c) / denom : 0));

        // 2) coarse ±3%를 0.01 BPM 단위로 훑는다
        double bestBpm = coarse, bestStrength = -1;
        for (double cand = coarse * 0.97; cand <= coarse * 1.03; cand += 0.01)
        {
            double s = PhaseStrength(onset, fps, cand, out _);
            if (s > bestStrength)
            {
                bestStrength = s;
                bestBpm = cand;
            }
        }

        // 녹음된 곡은 대부분 정수 BPM
        if (Math.Abs(Math.Round(bestBpm) - bestBpm) < 0.05)
            bestBpm = Math.Round(bestBpm);
        else
            bestBpm = Math.Round(bestBpm, 2);

        PhaseStrength(onset, fps, bestBpm, out double phase);
        double period = 60 / bestBpm;
        double offset = phase / (2 * Math.PI) * period + 0.5 / fps; // 어택은 프레임 중간쯤
        offset = (offset % period + period) % period;
        return ((float)bestBpm, (float)offset);
    }

    // 10ms 프레임마다 소리가 커진 양(onset). 크면 그 순간에 음이 새로 시작됐다는 뜻
    private static (double[] onset, double fps) Onsets(float[] mono, int rate)
    {
        int hop = rate / 100; // 10ms
        int frames = mono.Length / hop;

        var onset = new double[frames];
        double prevEnergy = 0, prevSample = 0;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * hop; i < (f + 1) * hop; i++)
            {
                double d = mono[i] - prevSample; // 고역 강조: 드럼 어택이 잘 잡힘
                prevSample = mono[i];
                sum += d * d;
            }

            double energy = Math.Log(1 + 1000 * sum / hop);
            onset[f] = Math.Max(0, energy - prevEnergy);
            prevEnergy = energy;
        }

        return (onset, rate / (double)hop);
    }

    // onset을 박 주기로 감았을 때 한 방향으로 얼마나 몰리는지(|Σ onset·e^{iωt}|²)와 그 방향(=박 위상)
    private static double PhaseStrength(double[] onset, double fps, double bpm, out double phase)
    {
        double w = 2 * Math.PI * bpm / 60 / fps, cos = Math.Cos(w), sin = Math.Sin(w);
        double zr = 1, zi = 0, sr = 0, si = 0;
        foreach (double o in onset)
        {
            sr += o * zr;
            si += o * zi;
            (zr, zi) = (zr * cos - zi * sin, zr * sin + zi * cos);
        }

        phase = Math.Atan2(si, sr);
        return sr * sr + si * si;
    }

    // 딜레이 측정 (게임의 LatencyCalibrator와 같은 방식): 일정 간격 삑에 맞춰 키를 누르면
    // 들린 시각과 재생 시각의 차이 중앙값을 오프셋으로 저장한다
    private const double ClickInterval = 0.5;
    private const int CalibrationCountIn = 3;
    private const int CalibrationTaps = 10;

    private bool calibrating;
    private double calibrationEnd;
    private int clickRate;
    private int clickSamples; // 삑 간격(샘플)
    private readonly List<double> deltas = new();

    private void BeginCalibration()
    {
        Stop();
        EnsureSource();

        clickRate = AudioSettings.outputSampleRate;
        clickSamples = (int)Math.Round(ClickInterval * clickRate);
        int beeps = CalibrationCountIn + CalibrationTaps * 2; // 몇 번 놓쳐도 되게 여유
        var data = new float[clickSamples * beeps];
        for (int b = 0; b < beeps; b++)
            for (int i = 0; i < clickRate * 0.03f; i++)
                data[b * clickSamples + i] = Mathf.Sin(2 * Mathf.PI * 1000 * i / clickRate) * 0.5f;

        var clip = AudioClip.Create("NoteEditorClick", data.Length, 1, clickRate, false);
        clip.SetData(data, 0);

        source.clip = clip;
        source.pitch = 1f; // 삑 간격이 정확해야 함
        source.timeSamples = 0;
        source.Play();

        deltas.Clear();
        calibrationEnd = EditorApplication.timeSinceStartup + clip.length + 0.5;
        calibrating = true;
    }

    private void CalibrationTap()
    {
        // 가장 가까운 삑과의 차이. +면 소리보다 늦게 누름(= 이어폰 딜레이 + 반응 지연)
        double t = source.timeSamples / (double)clickRate;
        double interval = clickSamples / (double)clickRate;
        long k = (long)Math.Round(t / interval);
        if (k < CalibrationCountIn)
            return;

        deltas.Add(t - k * interval);
        if (deltas.Count >= CalibrationTaps)
            FinishCalibration(true);
    }

    private void FinishCalibration(bool apply)
    {
        calibrating = false;
        if (source != null)
        {
            source.Stop();
            source.clip = music;
        }

        if (!apply)
            return;

        // 중앙값: 한두 번 크게 빗나간 입력에 휘둘리지 않게
        deltas.Sort();
        AudioOffset = (float)-deltas[deltas.Count / 2];
        PlayerPrefs.Save();
        ShowNotification(new GUIContent($"이어폰 딜레이: {AudioOffset * 1000f:0} ms"));
    }

    // 일시정지: 재생 바는 멈춘 자리에 남고, 다시 재생하면 거기서 이어진다
    private void Stop()
    {
        if (source != null)
            source.Pause();
        playing = false;
    }

    private void Seek(float beat)
    {
        playheadBeat = Mathf.Max(0f, beat);
        if (source != null && source.clip != null)
            source.timeSamples = Mathf.Clamp((int)((BeatToSeconds(playheadBeat) - MusicOffset) * source.clip.frequency),
                                             0, source.clip.samples - 1);
    }

    private EditorNote AddNote(int lane, float beat)
    {
        EditorNote existing = notes.FirstOrDefault(n => n.lane == lane && Mathf.Abs(n.beat - beat) < 1e-4f);
        if (existing != null)
            return existing;

        Undo.RecordObject(this, "Add Note");
        var note = new EditorNote { beat = beat, lane = lane };
        notes.Add(note);
        return note;
    }

    private void LoadMidi()
    {
        guide.Clear();
        guideStarts = Array.Empty<float>();
        guideChannels = Array.Empty<int>();
        guideChannelNames = Array.Empty<string>();
        if (midi == null)
            return;

        string path = AssetDatabase.GetAssetPath(midi);
        if (!path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))
        {
            midi = null;
            ShowNotification(new GUIContent("MIDI(.mid) 파일만 넣을 수 있어요"));
            return;
        }

        Melanchall.DryWetMidi.Core.MidiFile file;
        try
        {
            file = Melanchall.DryWetMidi.Core.MidiFile.Read(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"MIDI 읽기 실패: {path}\n{e}");
            midi = null;
            return;
        }

        // 템포가 바뀌는 MIDI도 초로 바꿔 두면 지금 BPM 그리드에 그대로 얹힌다
        TempoMap tempoMap = file.GetTempoMap();
        foreach (Melanchall.DryWetMidi.Interaction.Note n in file.GetNotes()) // 게임 쪽 Note와 이름이 겹침
        {
            float start = (float)n.TimeAs<MetricTimeSpan>(tempoMap).TotalSeconds;
            float end = (float)TimeConverter.ConvertTo<MetricTimeSpan>(n.EndTime, tempoMap).TotalSeconds;
            guide.Add((start, end, (int)n.NoteNumber, (int)n.Channel));
        }

        if (guide.Count == 0)
            return;

        minPitch = guide.Min(g => g.pitch);
        maxPitch = guide.Max(g => g.pitch);
        var channels = MidiToStage.Channels(file);
        guideChannels = channels.Select(c => c.channel).ToArray();
        guideChannelNames = channels.Select(c => $"ch{c.channel + 1} {c.instrument} ({c.count})").ToArray();
        RebuildSnapStarts();

        var (tempo, tempoStart) = MidiToStage.MainTempo(file);
        midiBpm = (float)tempo;
        midiStart = (float)tempoStart;
    }

    private bool IsSnapChannel(int channel) => (snapChannels >> channel & 1) != 0;

    private void RebuildSnapStarts() =>
        guideStarts = guide.Where(g => IsSnapChannel(g.channel)).Select(g => g.start).Distinct().OrderBy(s => s).ToArray();

    private void Delete(EditorNote note)
    {
        Undo.RecordObject(this, "Delete Note");
        notes.Remove(note);
        selected.Remove(note);
    }

    private void DeleteSelected()
    {
        Undo.RecordObject(this, "Delete Notes");
        notes.RemoveAll(selected.Contains);
        selected.Clear();
    }

    private void Copy()
    {
        float first = selected.Min(n => n.beat);
        clipboard.Clear();
        clipboard.AddRange(selected.Select(n => new EditorNote { beat = n.beat - first, lane = n.lane, message = n.message }));
        ShowNotification(new GUIContent($"노트 {clipboard.Count}개 복사"));
    }

    // 맨 앞 노트가 재생 바 자리(스냅)에 오고, 나머지 간격은 복사한 그대로
    private void Paste()
    {
        float at = Snap(playheadBeat);
        Undo.RecordObject(this, "Paste Notes");
        selected.Clear();
        foreach (EditorNote c in clipboard)
        {
            EditorNote pasted = AddNote(c.lane, at + c.beat);
            pasted.message = c.message;
            selected.Add(pasted);
        }
    }

    // Ctrl+C/X/V/A는 KeyDown이 아니라 Validate→Execute 명령으로 온다. Validate를 Use해야 Execute가 옴
    private void HandleCommands()
    {
        Event e = Event.current;
        if (e.type != EventType.ValidateCommand && e.type != EventType.ExecuteCommand || EditorGUIUtility.editingTextField)
            return;

        bool run = e.type == EventType.ExecuteCommand;
        switch (e.commandName)
        {
            case "Copy" when selected.Count > 0:
                if (run)
                    Copy();
                break;
            case "Cut" when selected.Count > 0:
                if (run)
                {
                    Copy();
                    DeleteSelected();
                }
                break;
            case "Paste" when clipboard.Count > 0:
                if (run)
                    Paste();
                break;
            case "SelectAll":
                if (run)
                    selected.UnionWith(notes);
                break;
            default:
                return;
        }

        e.Use();
    }

    private void OnGUI()
    {
        HandleCommands();
        HandleKeys();
        DrawToolbar();

        timeline = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (Event.current.type == EventType.Layout)
            return;

        GUI.BeginClip(timeline); // 아래부터 좌표는 타임라인 기준
        HandleMouse();
        if (Event.current.type == EventType.Repaint)
            DrawTimeline();
        GUI.EndClip();
    }

    private void HandleKeys()
    {
        Event e = Event.current;

        if (e.type == EventType.KeyUp)
            held.Remove(e.keyCode);

        if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField || e.control || e.command)
            return;

        if (calibrating)
        {
            if (e.keyCode == KeyCode.Escape)
                FinishCalibration(false);
            else if (e.keyCode != KeyCode.None && held.Add(e.keyCode))
                CalibrationTap();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Space)
        {
            if (held.Add(KeyCode.Space))
            {
                if (playing)
                    Stop();
                else
                    Play();
            }
            e.Use();
        }
        else if (Array.IndexOf(LaneKeys, e.keyCode) is var lane and >= 0)
        {
            if (held.Add(e.keyCode))
                AddNote(lane, Snap(playheadBeat));
            e.Use();
        }
        else if (e.keyCode == KeyCode.Home)
        {
            Seek(0f);
            e.Use();
        }
        else if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && selected.Count > 0)
        {
            DeleteSelected();
            e.Use();
        }
    }

    private void DrawToolbar()
    {
        EditorGUI.BeginChangeCheck();
        json = (TextAsset)EditorGUILayout.ObjectField("노트 JSON", json, typeof(TextAsset), false);
        if (EditorGUI.EndChangeCheck() && json != null)
            Load(json);

        EditorGUI.BeginChangeCheck();
        music = (AudioClip)EditorGUILayout.ObjectField("레퍼런스 음악", music, typeof(AudioClip), false);
        if (EditorGUI.EndChangeCheck())
            Stop();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            midi = (DefaultAsset)EditorGUILayout.ObjectField(new GUIContent("가이드 MIDI",
                "넣으면 레인 오른쪽 패널에 음이 나온다. 보고 따라 찍으면 됨"), midi, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
                LoadMidi();

            using (new EditorGUI.DisabledScope(midi == null))
            {
                midiShift = EditorGUILayout.FloatField(new GUIContent("MIDI 밀기(초)",
                    "MIDI 가이드를 옮긴다. 음악은 게임이 0초부터 트는 기준이라 안 움직임. 가이드가 늦으면 -, 빠르면 +"),
                    midiShift, GUILayout.Width(200));
                if (GUILayout.Button(new GUIContent("음악에 맞추기", "MIDI 음이 음악 소리와 가장 잘 겹치는 밀기 값을 자동으로 찾는다"),
                                     GUILayout.Width(90)))
                    AlignMidi();
                midiSnap = EditorGUILayout.ToggleLeft(new GUIContent("MIDI 스냅",
                    "노트가 가까운 MIDI 음 시작에 붙는다. 근처에 음이 없으면 그리드에 붙음"), midiSnap, GUILayout.Width(80));
            }

            // 스냅할 채널. 목록 순서(i)와 실제 채널 번호가 달라서 비트를 옮겨 담는다.
            // MIDI가 없으면 아예 안 그린다: MaskField는 빈 목록이면 예외를 던져서 창 전체가 멈춤
            if (guideChannels.Length > 0)
            using (new EditorGUI.DisabledScope(!midiSnap))
            {
                int shown = 0;
                for (int i = 0; i < guideChannels.Length; i++)
                    if (IsSnapChannel(guideChannels[i]))
                        shown |= 1 << i;

                EditorGUI.BeginChangeCheck();
                shown = EditorGUILayout.MaskField(shown, guideChannelNames, GUILayout.Width(170));
                if (EditorGUI.EndChangeCheck())
                {
                    for (int i = 0; i < guideChannels.Length; i++)
                        if ((shown >> i & 1) != 0)
                            snapChannels |= 1 << guideChannels[i];
                        else
                            snapChannels &= ~(1 << guideChannels[i]);
                    RebuildSnapStarts();
                }
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            SetBpm(Mathf.Max(1f, EditorGUILayout.FloatField("BPM", bpm)));
            if (GUILayout.Button("÷2", GUILayout.Width(28)))
                SetBpm(bpm / 2f);
            if (GUILayout.Button("×2", GUILayout.Width(28)))
                SetBpm(bpm * 2f);
            if (GUILayout.Button(new GUIContent("자동", "BPM과 첫 박(오프셋)을 찾는다. 가이드 MIDI가 있으면 MIDI 템포를, 없으면 음악을 분석해서 씀"), GUILayout.Width(40)))
                AutoBpm();

        }

        using (new EditorGUILayout.HorizontalScope())
        {
            gridOffset = EditorGUILayout.FloatField(new GUIContent("오프셋(초)",
                "곡의 첫 박(마디 시작)이 나오는 시각. 그리드가 이 지점부터 그어진다"), gridOffset);
            grid = EditorGUILayout.ToggleLeft(new GUIContent("그리드 스냅", "노트 찍기·드래그·재생 바 이동이 그리드 칸에 붙는다"),
                                              grid, GUILayout.Width(90));
            using (new EditorGUI.DisabledScope(!grid))
                division = Divisions[EditorGUILayout.Popup(Mathf.Max(0, Array.IndexOf(Divisions, division)), DivisionNames)];
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            float delayMs = EditorGUILayout.FloatField(new GUIContent("이어폰 딜레이(ms)",
                "게임의 LatencyCalibrator 값과 공유. 노트가 음악보다 늦게 찍히면 -, 빠르면 +"), AudioOffset * 1000f);
            if (EditorGUI.EndChangeCheck())
                AudioOffset = delayMs / 1000f;

            if (GUILayout.Button(calibrating ? $"삑 소리에 맞춰 아무 키 ({deltas.Count}/{CalibrationTaps}, Esc 취소)" : "딜레이 측정",
                                 GUILayout.Width(calibrating ? 260 : 80)))
            {
                if (calibrating)
                    FinishCalibration(false);
                else
                    BeginCalibration();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("⏮", GUILayout.Width(28)))
                Seek(0f);

            if (GUILayout.Button(playing ? "❚❚ 일시정지 (Space)" : "▶ 재생 (Space)", GUILayout.Width(130)))
            {
                if (playing)
                    Stop();
                else
                    Play();
            }

            EditorGUI.BeginChangeCheck();
            speed = EditorGUILayout.Slider(new GUIContent("속도", "재생 속도. 느리게 틀고 노트 찍을 때. 음높이도 같이 바뀜"),
                                           speed, 0.25f, 2f, GUILayout.Width(220));
            if (GUILayout.Button("1×", GUILayout.Width(28)))
                speed = 1f;
            if (EditorGUI.EndChangeCheck() && playing)
            {
                source.pitch = speed;
                Seek(playheadBeat); // 오프셋 길이가 바뀌어서 들리는 위치를 다시 맞춤
            }

            if (GUILayout.Button("저장", GUILayout.Width(50)))
                Save(json != null ? AssetDatabase.GetAssetPath(json) : null);
            if (GUILayout.Button("다른 이름으로", GUILayout.Width(90)))
                Save(null);

            if (GUILayout.Button("전부 지우기", GUILayout.Width(80)) &&
                EditorUtility.DisplayDialog("Note Editor", "노트를 전부 지울까요?", "지우기", "취소"))
            {
                Undo.RecordObject(this, "Clear Notes");
                notes.Clear();
                selected.Clear();
            }

            float seconds = BeatToSeconds(playheadBeat);
            GUILayout.Label($"{(int)seconds / 60}:{seconds % 60:00.00}  /  {playheadBeat:0.00}박  /  노트 {notes.Count}개");
        }

        // 선택한 노트의 메시지. 여러 개 골랐으면 한 번에 같은 값으로
        using (new EditorGUI.DisabledScope(selected.Count == 0))
        {
            string first = selected.Count > 0 ? selected.First().message ?? "" : "";
            EditorGUI.showMixedValue = selected.Any(n => (n.message ?? "") != first);
            EditorGUI.BeginChangeCheck();
            string message = EditorGUILayout.DelayedTextField(new GUIContent("메시지",
                "노트의 박이 지나갈 때 게임이 DialogueManager.Send로 보낸다. 미리 알리려면 앞 노트에 달 것. 비우면 안 보냄"),
                first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "Note Message");
                foreach (EditorNote n in selected)
                    n.message = message;
            }
        }

        GUILayout.Label("레인 빈 곳 클릭: 노트 추가 · 노트 끌기: 이동 · 우클릭: 삭제 · 왼쪽 숫자 칸 클릭: 재생 바 이동 · " +
                        "Shift+클릭/Shift+끌기: 여러 개 선택 · Ctrl+C/X/V: 복사/잘라내기/재생 바 자리에 붙여넣기 · Ctrl+A: 전체 선택 · " +
                        "Space: 재생 · A/S/D: 재생 바 자리에 노트 · Ctrl+휠: 확대 · Ctrl+Z: 되돌리기",
                        EditorStyles.wordWrappedMiniLabel);
    }

    private void HandleMouse()
    {
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        bool inside = new Rect(0, 0, timeline.width, timeline.height).Contains(e.mousePosition);

        switch (e.type)
        {
            case EventType.MouseDown when inside:
            {
                GUIUtility.keyboardControl = 0; // 텍스트 필드에서 포커스 빼서 A/S/D가 먹히게
                EditorNote hit = notes.LastOrDefault(n => NoteRect(n).Contains(e.mousePosition));

                if (e.button == 1 && hit != null)
                    Delete(hit);
                else if (e.button == 0 && hit != null && e.shift)
                {
                    if (!selected.Remove(hit)) // Shift+클릭: 선택에 넣었다 뺐다
                        selected.Add(hit);
                }
                else if (e.button == 0 && hit != null)
                {
                    if (!selected.Contains(hit)) // 이미 선택된 노트를 잡으면 선택 묶음째로 끈다
                    {
                        selected.Clear();
                        selected.Add(hit);
                    }

                    Undo.RecordObject(this, "Move Note");
                    dragging = hit;
                    dragOffset = BeatAt(e.mousePosition.y) - hit.beat;
                    GUIUtility.hotControl = id;
                }
                else if (e.button == 0 && e.shift)
                {
                    boxStart = boxEnd = e.mousePosition;
                    GUIUtility.hotControl = id;
                }
                else if (e.button == 0)
                {
                    // 레인 안이면 노트 추가, 왼쪽 숫자 칸이나 가이드 패널이면 재생 바 이동
                    float beat = Snap(BeatAt(e.mousePosition.y));
                    int lane = Mathf.FloorToInt((e.mousePosition.x - Gutter) / LaneWidth);
                    selected.Clear();
                    if (e.mousePosition.x >= Gutter && lane < 3)
                        selected.Add(AddNote(lane, beat));
                    else
                        Seek(beat);
                }

                e.Use();
                break;
            }

            case EventType.MouseDrag when dragging != null:
            {
                // 잡은 노트 기준으로 스냅하고, 같은 만큼 묶음 전체를 옮긴다 (레인 밖·0박 앞으로는 안 나감)
                float delta = Snap(BeatAt(e.mousePosition.y) - dragOffset) - dragging.beat;
                int laneDelta = Mathf.Clamp(Mathf.FloorToInt((e.mousePosition.x - Gutter) / LaneWidth), 0, 2) - dragging.lane;
                laneDelta = Mathf.Clamp(laneDelta, -selected.Min(n => n.lane), 2 - selected.Max(n => n.lane));
                delta = Mathf.Max(delta, -selected.Min(n => n.beat));
                foreach (EditorNote n in selected)
                {
                    n.beat += delta;
                    n.lane += laneDelta;
                }
                e.Use();
                break;
            }

            case EventType.MouseDrag when boxStart != null:
                boxEnd = e.mousePosition;
                e.Use();
                break;

            case EventType.MouseUp when dragging != null:
                dragging = null;
                GUIUtility.hotControl = 0;
                e.Use();
                break;

            case EventType.MouseUp when boxStart != null:
                selected.UnionWith(notes.Where(n => NoteRect(n).Overlaps(BoxRect)));
                boxStart = null;
                GUIUtility.hotControl = 0;
                e.Use();
                break;

            case EventType.ScrollWheel when inside:
                if (e.control || e.command)
                {
                    float anchor = BeatAt(e.mousePosition.y); // 마우스 아래 박을 고정한 채 확대
                    pxPerBeat = Mathf.Clamp(pxPerBeat * (e.delta.y < 0 ? 1.1f : 1f / 1.1f), 10f, 1000f);
                    scroll = anchor * pxPerBeat - e.mousePosition.y;
                }
                else
                    scroll += e.delta.y * 20f;

                e.Use();
                break;
        }
    }

    private void DrawTimeline()
    {
        float h = timeline.height;
        float laneW = LaneWidth;

        // 재생 중엔 재생 바가 위에서 30% 지점에 머물도록 따라간다
        if (playing)
            scroll = playheadBeat * pxPerBeat - h * 0.3f;
        scroll = Mathf.Max(-NoteHeight, scroll);

        EditorGUI.DrawRect(new Rect(0, 0, timeline.width, h), new Color(0.12f, 0.12f, 0.12f));
        for (int lane = 0; lane < 3; lane++)
        {
            Color c = LaneColors[lane];
            EditorGUI.DrawRect(new Rect(Gutter + lane * laneW, 0, laneW - 1f, h), new Color(c.r, c.g, c.b, 0.06f));
        }

        // 곡 끝 표시
        if (music != null)
        {
            float end = Y(SecondsToBeat(music.length));
            if (end < h)
                EditorGUI.DrawRect(new Rect(0, end, timeline.width, h - end), new Color(0f, 0f, 0f, 0.4f));
        }

        // 그리드: 마디(4박, ponytail: 4/4 고정) > 박 > 분할선
        int div = grid && pxPerBeat / division >= 4f ? division : 1;
        float origin = OffsetBeats;
        int first = Mathf.FloorToInt((BeatAt(0f) - origin) * div);
        int last = Mathf.CeilToInt((BeatAt(h) - origin) * div);
        float right = Gutter + laneW * 3f;
        float lineRight = guide.Count > 0 ? timeline.width : right; // 가이드 패널까지 그리드를 이어서 같은 박을 맞춰 보게

        DrawGuide(right + 8f, timeline.width - 4f, h);

        for (int k = first; k <= last; k++)
        {
            float beat = origin + k / (float)div;
            if (beat < 0f)
                continue;

            float y = Y(beat);
            bool isBeat = k % div == 0;
            bool isBar = k % (div * 4) == 0;

            float alpha = isBar ? 0.5f : isBeat ? 0.22f : 0.08f;
            EditorGUI.DrawRect(new Rect(Gutter, y, lineRight - Gutter, 1f), new Color(1f, 1f, 1f, alpha));

            if (isBeat)
                GUI.Label(new Rect(4f, y - 8f, Gutter - 8f, 16f), (k / div).ToString(),
                          isBar ? EditorStyles.boldLabel : EditorStyles.miniLabel);
        }

        foreach (EditorNote n in notes)
        {
            Rect r = NoteRect(n);
            if (r.yMax < 0f || r.y > h)
                continue;

            // 재생 바가 막 지나간 노트 = 지금 치는 노트: 레인을 밝히고 노트를 키웠다가 줄인다 (실제 시간 HitFlash초)
            float hitAgo = BeatToSeconds(playheadBeat - n.beat) / speed;
            if (playing && hitAgo >= 0f && hitAgo < HitFlash)
            {
                float k = 1f - hitAgo / HitFlash;
                Color c = LaneColors[n.lane];
                EditorGUI.DrawRect(new Rect(Gutter + n.lane * laneW, 0, laneW - 1f, h), new Color(c.r, c.g, c.b, 0.2f * k));
                float grow = 2f + 6f * k;
                EditorGUI.DrawRect(new Rect(r.x - grow, r.y - grow, r.width + grow * 2f, r.height + grow * 2f), new Color(1f, 1f, 1f, k));
            }
            else if (selected.Contains(n))
                EditorGUI.DrawRect(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), Color.white);
            EditorGUI.DrawRect(r, LaneColors[n.lane]);

            // 메시지 달린 노트: 노트 오른쪽에 이름을 띄운다
            if (!string.IsNullOrEmpty(n.message))
                GUI.Label(new Rect(r.xMax + 2f, r.center.y - 8f, laneW, 16f), "✉ " + n.message, EditorStyles.miniBoldLabel);
        }

        if (boxStart != null)
            EditorGUI.DrawRect(BoxRect, new Color(1f, 1f, 1f, 0.12f));

        // 재생 바
        EditorGUI.DrawRect(new Rect(0, Y(playheadBeat) - 1f, lineRight, 2f), new Color(1f, 0.85f, 0.2f));

        // 레인 이름
        for (int lane = 0; lane < 3; lane++)
            GUI.Label(new Rect(Gutter + lane * laneW, 0, laneW, 18f),
                      $"{LaneNames[lane]} ({LaneKeys[lane]})", EditorStyles.centeredGreyMiniLabel);
    }

    // 피아노 롤: 가로 = 음높이(왼쪽 낮음), 세로 = 시간. 색 = MIDI 채널
    private void DrawGuide(float x0, float x1, float h)
    {
        if (guide.Count == 0 || x1 - x0 < 20f)
            return;

        float w = x1 - x0;
        float keyW = w / (maxPitch - minPitch + 1);
        EditorGUI.DrawRect(new Rect(x0, 0, w, h), new Color(1f, 1f, 1f, 0.03f));

        foreach (var (start, end, pitch, channel) in guide)
        {
            float y0 = Y(SecondsToBeat(start + midiShift));
            float y1 = Y(SecondsToBeat(end + midiShift));
            if (y1 < 0f || y0 > h)
                continue;

            // 스냅 안 하는 채널은 흐리게: 어디에 붙는지 보이게
            Color c = Color.HSVToRGB(channel / 16f, 0.5f, 0.9f);
            if (midiSnap && !IsSnapChannel(channel))
                c.a = 0.2f;
            EditorGUI.DrawRect(new Rect(x0 + (pitch - minPitch) * keyW, y0, Mathf.Max(2f, keyW - 1f), Mathf.Max(2f, y1 - y0)), c);
        }

        GUI.Label(new Rect(x0, 0, w, 18f), "MIDI 가이드", EditorStyles.centeredGreyMiniLabel);
    }

    private void Load(TextAsset asset)
    {
        Stage stage;
        try
        {
            stage = JsonUtility.FromJson<Stage>(asset.text);
        }
        catch (Exception e)
        {
            Debug.LogError($"노트 JSON 읽기 실패: {asset.name}\n{e}");
            return;
        }

        Undo.RecordObject(this, "Load Notes");
        notes.Clear();
        selected.Clear();
        dragging = null;

        float beat = 0f;
        foreach (StageNote n in stage?.noteList ?? new List<StageNote>())
        {
            beat += n.interval;
            notes.Add(new EditorNote { beat = beat, lane = Mathf.Clamp(n.input, 0, 2), message = n.message });
        }

        if (stage != null && stage.bpm > 0f)
            bpm = stage.bpm;
        gridOffset = stage?.gridOffset ?? 0f;

        playheadBeat = 0f;
        scroll = 0f;
    }

    private void Save(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Directory.CreateDirectory("Assets/Resources");
            path = EditorUtility.SaveFilePanelInProject("노트 JSON 저장", "stage", "json", "", "Assets/Resources");
            if (string.IsNullOrEmpty(path))
                return;
        }

        // 누적 박을 먼저 반올림하고 차이를 간격으로 → 노트가 많아도 반올림 오차가 쌓이지 않음
        var stage = new Stage { bpm = bpm, gridOffset = gridOffset };
        double prev = 0;
        foreach (EditorNote n in notes.OrderBy(n => n.beat).ThenBy(n => n.lane))
        {
            double beat = Math.Round(n.beat, 4);
            stage.noteList.Add(new StageNote { interval = (float)Math.Round(beat - prev, 4), input = n.lane, message = n.message });
            prev = beat;
        }

        File.WriteAllText(path, JsonUtility.ToJson(stage, true));
        AssetDatabase.ImportAsset(path);
        json = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        ShowNotification(new GUIContent($"저장: {Path.GetFileName(path)} ({stage.noteList.Count}개)"));
    }
}
