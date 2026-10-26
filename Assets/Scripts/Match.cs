using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using IMGUI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public enum Accuracy
{
    Perfect,
    Great,
    Good,
    Miss,
    Oops
}

public class Match : MonoBehaviour
{
    public float BPM = 260;

    public float nt(float x)
    {
        return 60f / BPM * x;
    }

    public GameObject Glow;
    public GameObject Shine;

    public GameObject Particle;

    public GameObject Lit;
    public GameObject Click;

    public GameObject Mote;
    public GameObject GuideA;
    public GameObject GuideB;
    public GameObject GuideC;
    [Tooltip("펌프 레인(F키). 비워 두면 펌프 모드일 때 GuideC를 복제해서 오른쪽에 만든다")]
    public GameObject GuideD;
    public GameObject NotePrefab;

    public Transform SpawnPoint;
    public Transform HitPoint;
    public RectTransform SliderUI;
    public RectTransform Result;

    public EffectScript effectScript;

    [Min(0.01f)]
    public float previewTime = 1f;

    [Header("Music sync")]
    public AudioSource Music;

    [Tooltip("노트가 음악보다 늦으면 +, 빠르면 - (초)")]
    public float audioOffset = 0f;

    [Header("Hit feel")]
    [Tooltip("비우면 기본 '틱' 소리를 만들어 쓴다")]
    public AudioClip HitSound;

    [Range(0f, 1f)]
    public float hitVolume = 0.8f;
    
    public float shakeStrength = 0.08f;
    public float shakeDuration = 0.08f;

    // PlayScheduled는 소스당 예약 1개라서 여러 개 돌려 쓴다
    private readonly AudioSource[] hitSources = new AudioSource[16];
    private int nextHitSource;
    private Transform cameraTransform;

    private int nextSoundIndex;

    public RhythmSpriteBounce rhythmSpriteBounce;

    private GUIBoxLabel guideAText;
    private GUIBoxLabel guideBText;
    private GUIBoxLabel guideCText;
    private GUIBoxLabel guideDText; // 펌프 모드에서만

    // 노트를 치면 그 레인 기둥이 레인 색으로 번쩍했다 사라진다 (노트 에디터 재생 강조와 같은 색·시간)
    private static readonly Color[] LaneColors =
    {
        new(1f, 0.45f, 0.45f),
        new(0.45f, 1f, 0.55f),
        new(0.45f, 0.65f, 1f),
        new(1f, 0.8f, 0.3f) // 펌프
    };
    private const float LaneFlashTime = 0.2f;
    private readonly GUIImage[] laneFlash = new GUIImage[4];

    // 펌프 모드: 곡 선택에서 켜고, 튜토리얼은 항상 키보드(3레인)
    bool pumpLane;

    // 타건음은 키 입력이 아니라 노트에 붙어 있다: 각 노트의 정박에 맞춰 미리 예약 재생한다.
    // 음악과 같은 시계(dsp)·같은 출력 경로라서 이어폰 딜레이가 있어도 음악과 정확히 겹친다.
    // (누른 뒤에 재생하면 딜레이만큼 무조건 늦게 들림.) 대신 놓친 노트도 소리는 난다.
    private void ScheduleNoteSounds()
    {
        if (hitSources[0] == null)
        {
            for (int i = 0; i < hitSources.Length; i++)
            {
                hitSources[i] = gameObject.AddComponent<AudioSource>();
                hitSources[i].playOnAwake = false;
            }

            if (HitSound == null)
            {
                // 40ms 노이즈가 빠르게 줄어드는 '틱'
                int rate = AudioSettings.outputSampleRate;
                var data = new float[(int)(rate * 0.04f)];
                for (int i = 0; i < data.Length; i++)
                    data[i] = Random.Range(-1f, 1f) * Mathf.Exp(-i / (rate * 0.006f));

                HitSound = AudioClip.Create("HitTick", data.Length, 1, rate, false);
                HitSound.SetData(data, 0);
            }
        }

        // 0.1초 앞까지 예약 (오디오 버퍼·프레임보다 충분히 앞, 소스 16개로 모자라지 않을 만큼 짧게)
        double now = AudioSettings.dspTime;
        while (nextSoundIndex < hitTimes.Length && startDsp + hitTimes[nextSoundIndex] <= now + 0.1)
        {
            double at = startDsp + hitTimes[nextSoundIndex++];
            if (at < now)
                continue; // 이미 지난 노트

            AudioSource source = hitSources[nextHitSource];
            nextHitSource = (nextHitSource + 1) % hitSources.Length;
            source.clip = HitSound;
            source.volume = hitVolume;
            source.PlayScheduled(at);
        }
    }

    // 노트의 박이 지나가는 순간 그 노트의 message를 보낸다. 플레이어가 쳤는지와 상관없이 곡 시간 기준
    // 받는 쪽: DialogueManager.Message += ... (대사 waitUntilEvent로도 쓸 수 있다)
    // all = 박이 안 와도 남은 것 전부 (곡이 끝나면 Update가 멈춰서, 마지막 노트를 일찍 친 경우 그 메시지가 영영 안 나감)
    private void SendNoteMessages(bool all = false)
    {
        while (nextMessageIndex < notes.Count && (all || hitTimes[nextMessageIndex] <= currentTime))
        {
            string message = notes[nextMessageIndex++].message;
            if (!string.IsNullOrEmpty(message))
                DialogueManager.Send(message);
        }
    }

    // 노트를 맞히면 카메라 살짝 흔들기
    private void Shake()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (cameraTransform != null)
        {
            // 연타해도 위치가 밀리지 않게 이전 흔들림을 끝내고(원위치) 새로 시작
            cameraTransform.DOComplete();
            cameraTransform.DOShakePosition(shakeDuration, shakeStrength, 30);
        }
    }

    [Header("Judgment windows (seconds)")]
    [Min(0f)]
    public float perfectWindow = 0.05f;

    [Min(0f)]
    public float greatWindow = 0.1f;

    [Min(0f)]
    public float goodWindow = 0.15f;

    // 연타 방지. Good 범위보다 이만큼 더 일찍 누르면 그 노트를 Miss로 먹는다.
    // 사람이 연타하는 간격(약 0.06~0.1초)보다 넓어야 연타가 이 구간을 못 건너뛴다. 난이도 배율(JudgeScale)은 안 곱한다
    [Min(0f)]
    public float earlyMissWindow = 0.1f;

    // 레인마다 마지막으로 Miss 처리된 노트의 정박 시각 (지나가서 놓침 + 얼리 미스).
    // 그 노트에 더 가까운 입력은 "그 노트를 늦게 친 것"이라 다음 노트를 얼리 미스로 먹지 않는다 (아래 CheckAccuracyAndForward)
    private readonly float[] lastMissTime = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

    public bool InputA;
    public bool InputB;
    public bool InputC;
    public bool InputD; // 펌프

    public float currentTime;

    public GameObject Combo;

    private List<Note> notes;
    private float[] hitTimes;

    // 현재 화면에 보이는(스폰됐고 아직 판정되지 않은) 노트. 판정은 이 목록만 대상으로 한다.
    private readonly List<NoteObject> activeNotes = new();

    private int nextSpawnIndex;
    private int nextMessageIndex; // 다음에 메시지를 확인할 노트
    private int judged;
    private int perfect;
    private int great;
    private int good;
    private int miss;
    private int combo;
    private int bestCombo;

    private bool ready;
    private bool finished;
    private bool started;
    private double startDsp;
    private const float MusicDelay = 3f; // 씬 진입(스토리가 있으면 스토리 끝) → 음악 시작(초). 그동안 A/B/C 안내가 떴다 사라진다
    private StoryDialogue story;          // 곡 전 스토리. 없으면 null
    private double enteredDsp;            // 씬에 들어온 시각 (dspTime)
    private bool guidesFlashed;

    // 연출용 스위치 (BadAppleDirector가 쓴다)
    // InputBlocked: 입력을 버리고 곡 시계도 멈춘다 → 노트가 그 자리에 얼고, 판정·Miss도 안 난다
    // HoldStart: Restart 뒤 음악 예약을 미룬다 (화면이 까만 동안 처음부터 다시 시작할 준비만)
    [HideInInspector] public bool InputBlocked;
    [HideInInspector] public bool HoldStart;
    public bool Started => started; // 음악이 예약돼서 currentTime이 이번 판 기준으로 돌고 있음 (BadApple이 본다)

    // 음악 위치(초)를 오디오 시계로 바로 계산한 값. currentTime과 달리 채보가 끝나도(finished) 계속 간다.
    // 정지 중에는 정지한 순간 값에 멈춘다 (풀 때 SetPaused가 startDsp를 미뤄서 이어진다). 이어폰 딜레이 보정 포함
    public float SongTime => (float)((paused ? pauseDsp : AudioSettings.dspTime) - startDsp) + audioOffset;

    // 오디오 로드 + 로딩 끊김이 가라앉을 때까지(10프레임 연속 50ms 미만) 기다린 뒤
    // 음악을 dsp 시계 기준 정확한 시각에 예약 재생한다.
    private IEnumerator StartWhenReady()
    {
        if (Music != null && Music.clip != null)
        {
            Music.clip.LoadAudioData();
            while (Music.clip.loadState == AudioDataLoadState.Loading)
                yield return null;
        }

        int stable = 0;
        while (stable < 10)
        {
            yield return null;
            stable = Time.unscaledDeltaTime < 0.05f ? stable + 1 : 0;
        }

        // 스토리가 끝나고 설정 창도 닫혀 있을 때까지 음악 예약을 미룬다. 한 조건으로 같이 본다:
        // 따로 기다리면 스토리 도중 설정 창을 연 경우 멈춘 채로 예약되고, 풀 때 노트 시계만 밀린다
        while (paused || HoldStart || (story != null && story.Playing))
            yield return null;

        // 씬에 들어온 뒤(스토리가 있었으면 끝난 뒤) MusicDelay초에 맞춰 시작.
        // 로딩이 그보다 길었거나 씬 중간의 Restart면 바로(0.1초 뒤)
        // 기준 시각도 dsp로 잰다: 게임 시간(timeSinceLevelLoad)은 프레임이 끊기면 오디오 시계와 벌어진다
        double from = story != null ? story.EndedDsp : enteredDsp;
        startDsp = System.Math.Max(AudioSettings.dspTime + 0.1, from + MusicDelay);

        if (Music != null)
            Music.PlayScheduled(startDsp);

        currentTime = (float)(AudioSettings.dspTime - startDsp) + audioOffset;
        started = true;
    }

    private string feedback = "Ready";
    private float feedbackUntil;
    private Color feedbackColor = Color.white;

    private GUIStyle labelStyle, feedbackStyle;
    private GUIGroup hudPanel;
    private GUILabel notesLabel, scoreLabel, countsLabel, nextLabel, feedbackLabel;
    private bool feedbackShown; // 판정 문구가 떠 있음. 사라지는 페이드를 한 번만 걸려고

    private TextMeshPro comboText;
    private readonly List<ParticleSystem> comboGlows = new();

    private GameObject moteFolder;
    private GameObject currentMote;
    private Vector3 motePos;
    public float MiracleProgress;


    //Pooling 이벤트 핸들러
    public void OnA(InputAction.CallbackContext c)
    {
        if (c.performed)
        {
            var g = Instantiate(Click);
            g.transform.position = GuideA.transform.position;
            Destroy(g, 1);
            InputA = true;
        }
    }

    public void OnB(InputAction.CallbackContext c)
    {
        if (c.performed)
        {
            var g = Instantiate(Click);
            g.transform.position = GuideB.transform.position;
            Destroy(g, 1);
            InputB = true;
        }
    }

    public void OnC(InputAction.CallbackContext c)
    {
        if (c.performed)
        {
            var g = Instantiate(Click);
            g.transform.position = GuideC.transform.position;
            Destroy(g, 1);
            InputC = true;
        }
    }

    // 펌프(F키). 씬 PlayerInput 이벤트 대신 Start에서 "d" 액션에 직접 붙인다 (씬 수정 없이)
    public void OnD(InputAction.CallbackContext c)
    {
        if (c.performed && pumpLane)
        {
            var g = Instantiate(Click);
            g.transform.position = GuideD.transform.position;
            Destroy(g, 1);
            InputD = true;
        }
    }

    InputAction pumpAction;

    // 펌프 모드 준비. 키보드 모드면 채보에서 펌프 노트를 빼고, 펌프 모드면 C 레인 오른쪽에 D 레인을 만든다
    private void SetupPumpLane()
    {
        pumpLane = SongSelect.PumpMode && FindAnyObjectByType<TutorialCoach>() == null;

        if (!pumpLane)
        {
            notes = NoteManager.WithoutLane(notes, NoteInputType.D);
            if (GuideD != null)
                GuideD.SetActive(false);
            return;
        }

        if (GuideD == null)
        {
            // C 레인을 레인 간격만큼 오른쪽에 복제 (판정 칸·덩굴 그림이 자식이라 같이 온다)
            Vector3 step = GuideC.transform.localPosition - GuideB.transform.localPosition;
            GuideD = Instantiate(GuideC, GuideC.transform.parent);
            GuideD.name = "GuideD (pump)";
            GuideD.transform.localPosition = GuideC.transform.localPosition + step;
            // 레인 4개를 가운데로: 전체를 반 칸 왼쪽으로 (오른쪽 나무·게이지와 안 겹치게)
            GuideC.transform.parent.position -= GuideC.transform.parent.TransformVector(step) * 0.5f;
        }
        GuideD.SetActive(true);

        var playerInput = FindAnyObjectByType<PlayerInput>();
        pumpAction = playerInput != null ? playerInput.actions.FindAction("d") : null;
        if (pumpAction != null)
            pumpAction.performed += OnD;
        else
            Debug.LogWarning("Match: 입력 액션 'd'(F키)를 못 찾음. 펌프 입력이 안 들어온다", this);
    }

    private void Start()
    {
        enteredDsp = AudioSettings.dspTime;

        // 노트 메시지 배너("이제 보컬 리듬!"). 씬에 직접 붙여 뒀으면 그걸 쓴다
        if (GetComponent<RhythmCallout>() == null)
            gameObject.AddComponent<RhythmCallout>();

        // Bad Apple!! 곡이면 5초 "Real BadApple" 연출
        if (SongSelect.SelectedNote == BadAppleDirector.SongId && GetComponent<BadAppleDirector>() == null)
            gameObject.AddComponent<BadAppleDirector>();

        if (!GuideA || !GuideB || !GuideC ||
            !NotePrefab || !SpawnPoint || !HitPoint)
        {
            Debug.LogError(
                "Match: GuideA, GuideB, GuideC, NotePrefab, SpawnPoint, HitPoint",
                this
            );

            enabled = false;
            return;
        }

        if (!NotePrefab.GetComponent<NoteObject>())
        {
            Debug.LogError(
                "Match: NotePrefab�� NoteObject�� �ʿ��մϴ�.",
                this
            );

            enabled = false;
            return;
        }

        if (Combo != null)
        {
            comboText = Combo.GetComponent<TextMeshPro>();

            for (int i = 0; i < Combo.transform.childCount; i++)
            {
                ParticleSystem particle =
                    Combo.transform.GetChild(i).GetComponent<ParticleSystem>();

                if (particle != null)
                    comboGlows.Add(particle);
            }
        }

        motePos = GuideB.transform.position + new Vector3(0, 1, 0);

        moteFolder = GameObject.Find("Motes");

        if (moteFolder != null)
            moteFolder.transform.position = motePos;

        if (NoteManager.Instance == null)
        {
            Debug.LogError(
                "Match: NoteManager.Instance�� �����ϴ�.",
                this
            );

            enabled = false;
            return;
        }

        notes = NoteManager.Instance.CurrentData.notes;
        SetupPumpLane();

        if (NoteManager.Instance.CurrentData.bpm > 0f)
            BPM = NoteManager.Instance.CurrentData.bpm;

        rhythmSpriteBounce.SetBPM(BPM);

        if (notes == null || notes.Count == 0)
        {
            Debug.LogError(
                "Match: JSON���� ä���� ���� ���߽��ϴ�.",
                this
            );

            enabled = false;
            return;
        }

        // LatencyCalibrator로 측정·저장한 이어폰 딜레이
        audioOffset = PlayerPrefs.GetFloat(LatencyCalibrator.PrefsKey, audioOffset);

        // 곡 선택 창에서 고른 난이도. 노트가 보이는 시간을 늘린다 (어려움 = 1배)
        previewTime *= SongSelect.PreviewScale;
        perfectWindow *= SongSelect.JudgeScale;
        greatWindow *= SongSelect.JudgeScale;
        goodWindow *= SongSelect.JudgeScale;

        // 메뉴에서 고른 곡 음원 (Resources에 차트와 같은 이름). 없으면 Inspector의 clip 유지
        var clip = Resources.Load<AudioClip>(SongSelect.SelectedSong);
        if (clip != null && Music != null)
            Music.clip = clip;
        Debug.Log($"Music: song={SongSelect.SelectedSong} clip={(Music != null && Music.clip != null ? Music.clip.name : "NONE")}", this);

        // 곡 전 스토리 (Resources/Story/<곡 id>.json 있을 때). 튜토리얼은 자기 대사창이 따로 있어서 안 띄운다
        if (FindAnyObjectByType<TutorialCoach>() == null)
            story = StoryDialogue.TryStart(gameObject, SongSelect.SelectedSong);

        ready = true;

        Restart();
    }
    
    private TextMeshProUGUI Tip => Result.Find("Tip").GetComponent<TextMeshProUGUI>();
    private TextMeshProUGUI Score => Result.Find("Score").GetComponent<TextMeshProUGUI>();
    public void Restart()
    {
        Result.gameObject.SetActive(false);
        Result.anchoredPosition = new Vector2(Result.anchoredPosition.x, -Result.rect.height * 3);
        if (!ready || notes == null)
            return;

        foreach (NoteObject note in activeNotes)
        {
            if (note != null)
                Destroy(note.gameObject);
        }

        activeNotes.Clear();

        perfectWindow = Mathf.Max(0f, perfectWindow);
        greatWindow = Mathf.Max(perfectWindow, greatWindow);
        goodWindow = Mathf.Max(greatWindow, goodWindow);
        previewTime = Mathf.Max(0.01f, previewTime);
        hitTimes = new float[notes.Count];

        float time = 0f;
        float beatToSecond = 60f / BPM;

        for (int i = 0; i < notes.Count; i++)
        {
            time += notes[i].interval * beatToSecond;
            hitTimes[i] = time;
        }

        nextSpawnIndex = 0;
        nextSoundIndex = 0;
        for (int i = 0; i < lastMissTime.Length; i++)
            lastMissTime[i] = float.NegativeInfinity;
        nextMessageIndex = 0;

        if (Music != null)
            Music.Stop();

        foreach (AudioSource source in hitSources)
        {
            if (source != null)
                source.Stop(); // 예약된 타건음 취소
        }

        started = false;
        StopAllCoroutines();
        // 꺼져 있으면(캘리브레이션 중) 판만 치우고 음악은 예약 안 함. 코루틴은 꺼진 컴포넌트에서도 돌아서 음악만 나온다
        // 캘리브레이션이 끝나면 LatencyCalibrator.Close가 켜고 다시 Restart한다
        if (enabled)
            StartCoroutine(StartWhenReady());

        progressLevel = 1;
        judged = 0;
        perfect = 0;
        great = 0;
        good = 0;
        miss = 0;
        combo = 0;
        bestCombo = 0;
        MiracleProgress = 0f;

        finished = false;

        ClearInput();

        rhythmSpriteBounce.ResetBounce();
        rhythmSpriteBounce.setSpriteLevel(0);
        ShowFeedback("Ready", Color.white);

        // 결과창(또는 BadApple 마지막 구간)에서 치운 판정 패널을 다시 보이게
        if (hudPanel != null)
        {
            GUITween.Kill(hudPanel);
            hudPanel.Opacity = 1f;
            hudPanel.isInteractable = true;
        }
    }

    private void Update()
    {
        bool a = InputA;
        bool b = InputB;
        bool c = InputC;
        bool d = InputD;

        ClearInput();

        if (!ready || !started || finished || InputBlocked || Time.timeScale <= 0f)
            return;

        // 오디오 하드웨어 시계(dspTime)가 기준. 프레임마다 deltaTime으로 부드럽게 진행하고,
        // dspTime과 30ms 넘게 벌어지면 맞춘다 (dspTime은 오디오 버퍼 단위로 뚝뚝 끊겨서 그대로 쓰면 노트가 떨림).
        float audioClock = (float)(AudioSettings.dspTime - startDsp) + audioOffset;
        currentTime += Time.deltaTime;
        if (Mathf.Abs(audioClock - currentTime) > 0.03f)
            currentTime = audioClock;

        ScheduleNoteSounds();
        SendNoteMessages();
        ExpireNotes();
        SpawnUpcomingNotes();

        if (a)
            CheckAccuracyAndForward(NoteInputType.A);

        if (b)
            CheckAccuracyAndForward(NoteInputType.B);

        if (c)
            CheckAccuracyAndForward(NoteInputType.C);

        if (d)
            CheckAccuracyAndForward(NoteInputType.D);

        if (judged == notes.Count)
        {
            FinishGame();
        }

        UpdateComboUI();
        UpdateSliderUI();
    }

    private bool paused;
    private double pauseDsp;

    // 설정 창 열고 닫을 때. 노트 시계가 dspTime 기준이라 timeScale만 0으로는 안 멈춘다:
    // 멈춘 동안 흐른 dsp 시간만큼 startDsp를 미루고, 예약해 둔 타건음은 취소 후 다시 예약한다.
    public void SetPaused(bool pause)
    {
        if (pause == paused)
            return;

        paused = pause;
        Time.timeScale = pause ? 0f : 1f;

        if (pause)
        {
            pauseDsp = AudioSettings.dspTime;

            if (Music != null)
                Music.Pause();

            foreach (AudioSource source in hitSources)
            {
                if (source != null)
                    source.Stop();
            }
        }
        else
        {
            if (started)
                startDsp += AudioSettings.dspTime - pauseDsp;
            nextSoundIndex = 0; // 지난 노트는 ScheduleNoteSounds가 건너뜀

            if (Music != null)
                Music.UnPause();
        }
    }

    // Exit 버튼 OnClick에 연결
    public void Exit()
    {
        Time.timeScale = 1f; // 설정 창 연 채로 나가도 메뉴가 멈춰 있지 않게
        DOTween.KillAll(); // 씬 넘어가며 파괴될 오브젝트의 트윈 정리
        SceneManager.LoadScene("Menu");
    }

    // 코드로 만든 차트 치기 (TutorialCoach). music = 음악 대신 깔 소리 (메트로놈)
    public void Play(List<Note> chart, float bpm, AudioClip music)
    {
        notes = chart;
        BPM = bpm;
        rhythmSpriteBounce.SetBPM(bpm);

        if (Music != null)
            Music.clip = music;

        Restart();
    }

    [Tooltip("끝나면 결과 창 띄우기. 튜토리얼은 마지막 단계에서만 켠다")]
    public bool showResult = true;

    public Sprite S;
    public Sprite A;
    public Sprite B;
    public Sprite C;

    public void FinishGame()
    {
        finished = true;
        SendNoteMessages(all: true);
        DialogueManager.Send("Finish");
        ShowFeedback("Finished", Color.white);

        // 점수 = 정확도(MiracleProgress) x 100만 x 난이도·엄격 배율. 어려움+엄격 올 Perfect = 1,000,000
        // 랭크는 정확도만 보고, 점수는 어떤 설정으로 쳤는지까지 반영한다
        int points = Points;

        //최고기록 저장 (튜토리얼이나 Game씬 바로 실행은 SelectedDiff = -1이라 저장 안함)
        if (SongSelect.SelectedDiff >= 0)
        {
            string key = SongSelect.BestKey(SongSelect.SelectedSong, SongSelect.SelectedDiff);
            if (MiracleProgress > PlayerPrefs.GetFloat(key, -1f))
                PlayerPrefs.SetFloat(key, MiracleProgress);

            string scoreKey = SongSelect.ScoreKey(SongSelect.SelectedSong);
            if (points > PlayerPrefs.GetInt(scoreKey, -1))
                PlayerPrefs.SetInt(scoreKey, points);

            PlayerPrefs.Save();
        }

        if (!showResult)
            return;

        Result.gameObject.SetActive(true);
        Result.DOAnchorPosY(0f, 1f).SetEase(Ease.OutQuad);
        LayoutResult();
        FadeOutHud(0.3f); // 결과창이 왼쪽 위 판정 패널과 겹쳐서 치운다. Restart가 다시 켠다
        int rank = RankIndex(MiracleProgress); // 0=C, 1=B, 2=A, 3=S
        Tip.text = new[] { "Keep practicing!", "Good", "Great!", "Miracle!" }[rank];

        Score.text = $"Score: {points:N0}  ({MiracleProgress * 100f:0.00}%)\n" +
                     $"Perfect: {perfect}\n" +
                     $"Great: {great}\n" +
                     $"Good: {good}\n" +
                     $"Miss: {miss}\n" +
                     $"Best Combo: {bestCombo}";

        Result.Find("Image").GetComponent<Image>().sprite = new[] { C, B, A, S }[rank];
    }

    // 결과창 배치 (Result 패널 기준, 패널은 약 296x395). 씬 기본 배치는 점수 글 칸이 75x72로 작아서
    // 글이 왼쪽 밖으로 넘치고 랭크 그림과 겹친다 → 위에서부터 문구 / 랭크 그림 / 점수 / 버튼 순으로 잡는다
    // ponytail: 씬에서 옮겨도 여기서 덮어씀. 배치를 인스펙터로 만지고 싶어지면 씬 값으로 옮기고 이 함수를 지울 것
    GameObject scoreBack;
    private void LayoutResult()
    {
        static void Place(RectTransform r, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(0f, y);
            r.sizeDelta = new Vector2(w, h);
        }

        Place(Tip.rectTransform, 138f, 260f, 50f);

        var rankImage = Result.Find("Image").GetComponent<Image>();
        rankImage.preserveAspect = true;
        Place(rankImage.rectTransform, 70f, 90f, 90f);

        // 점수 글: 밝은 배경 위라 반투명 검정 판을 깔고 가운데 정렬, 칸에 맞게 글자 크기 자동
        if (scoreBack == null)
        {
            scoreBack = new GameObject("ScoreBack", typeof(RectTransform), typeof(Image));
            scoreBack.transform.SetParent(Result, false);
            scoreBack.transform.SetSiblingIndex(Score.transform.GetSiblingIndex()); // 점수 글 바로 밑에 그려지게
            var img = scoreBack.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.35f);
            img.raycastTarget = false;
        }
        Place((RectTransform)scoreBack.transform, -50f, 250f, 150f);
        Place(Score.rectTransform, -50f, 236f, 140f);
        Score.alignment = TextAlignmentOptions.Center;
        Score.enableAutoSizing = true;
        Score.fontSizeMin = 10f;
        Score.fontSizeMax = 22f;
    }

    private void ExpireNotes()
    {
        for (int i = activeNotes.Count - 1; i >= 0; i--)
        {
            NoteObject note = activeNotes[i];

            if (currentTime - note.hitTime > goodWindow)
                Resolve(note, Accuracy.Miss);
        }
    }

    private RectTransform SliderParent;

    public int noteCount;
    int progressLevel = 1;
    // 랭크 기준 (0~1, B·A·S 순). 나무 단계·게이지 눈금·결과창·곡 선택 최고기록이 전부 이것 하나를 본다
    public static readonly float[] RankAt = { 0.3f, 0.6f, 0.9f };
    public const int MaxPoints = 1000000; // 어려움 + 엄격한 판정으로 전부 Perfect일 때 점수
    // 지금까지 점수. 판정이 날 때마다 MiracleProgress가 쌓여서 같이 오른다 (HUD·결과창)
    int Points => Mathf.RoundToInt(MiracleProgress * MaxPoints * SongSelect.ScoreScale);
    static readonly string[] StageNames = { "B", "A", "S" };
    public static string RankName(float p) => new[] { "C", "B", "A", "S" }[RankIndex(p)];
    public static int RankIndex(float p) // 0=C, 1=B, 2=A, 3=S
    {
        int r = 0;
        while (r < RankAt.Length && p >= RankAt[r])
            r++;
        return r;
    }
    void stage3(){rhythmSpriteBounce.setSpriteLevel(3);}

    private void UpdateSliderUI()
    {
        if (SliderParent == null)
        {
            SliderParent = SliderUI?.parent as RectTransform;
            if (SliderParent != null)
                MakeStageMarks();
        }

        if (SliderUI == null || SliderParent == null || notes.Count == 0)
            return;

        if (progressLevel <= RankAt.Length && MiracleProgress >= RankAt[progressLevel - 1]) //valid 판정
        {
            if (progressLevel == 1){effectScript.Level1(); rhythmSpriteBounce.setSpriteLevel(1);}
            else if (progressLevel == 2){effectScript.Level2(); rhythmSpriteBounce.setSpriteLevel(2);}
            else if (progressLevel == 3)
            {
                effectScript.Level3(); 
                Invoke(nameof(stage3), 2.5f);
            }
            else{Debug.LogWarning("Invalid progress level: " + progressLevel);}
            progressLevel++;
        }

        SliderUI.offsetMax = new Vector2(-SliderParent.rect.width + MiracleProgress * SliderParent.rect.width - SliderUI.offsetMin.x, SliderUI.offsetMax.y);
    }

    // 게이지 위 단계 눈금: RankAt 자리에 세로선 + 랭크 글자. 앵커를 비율로 걸어서 게이지 크기가 바뀌어도 따라간다
    // SliderParent(Border Mask)가 마스크라 게이지 밖은 잘림 → 글자도 게이지 안쪽, 선 오른쪽에 둔다
    private void MakeStageMarks()
    {
        for (int i = 0; i < RankAt.Length; i++)
        {
            var mark = new GameObject("Stage " + StageNames[Mathf.Min(i, StageNames.Length - 1)], typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            mark.SetParent(SliderParent, false);
            mark.anchorMin = new Vector2(RankAt[i], 0f);
            mark.anchorMax = new Vector2(RankAt[i], 1f);
            mark.sizeDelta = new Vector2(3f, 0f);
            mark.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.8f);
            mark.GetComponent<Image>().raycastTarget = false;

            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            var rt = label.rectTransform;
            rt.SetParent(mark, false);
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(4f, 0f);
            rt.sizeDelta = new Vector2(30f, 0f);
            label.text = StageNames[Mathf.Min(i, StageNames.Length - 1)];
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = 28f;
            label.raycastTarget = false;
        }
    }

    private void ClearInput()
    {
        InputA = false;
        InputB = false;
        InputC = false;
        InputD = false;
    }

    private void OnDisable()
    {
        ClearInput();

        if (hudPanel != null)
            hudPanel.isVisible = false;
    }

    private void OnApplicationFocus(bool focus)
    {
        if (!focus)
            ClearInput();
    }

    public Accuracy CheckAccuracyAndForward(NoteInputType input)
    {
        if (!ready ||
            finished ||
            !isActiveAndEnabled ||
            Time.timeScale <= 0f)
        {
            return Accuracy.Miss;
        }

        NoteObject candidate = null;
        NoteObject tooEarly = null; // Good 범위 바로 앞(earlyMissWindow 안)에 있는 노트
        float closest = float.PositiveInfinity;
        int bestGrade = int.MaxValue; // 0 Perfect, 1 Great, 2 Good
        bool upcoming = false;

        foreach (NoteObject note in activeNotes)
        {
            if (note.input != input)
                continue;

            upcoming = true;

            float distance =
                Mathf.Abs(currentTime - note.hitTime);

            if (distance > goodWindow)
            {
                float ahead = note.hitTime - currentTime;
                if (ahead > 0f && ahead <= goodWindow + earlyMissWindow &&
                    (tooEarly == null || note.hitTime < tooEarly.hitTime))
                    tooEarly = note;
                continue;
            }

            // 판정을 가장 후하게 받는 노트를 친다. 판정이 같으면 앞 노트
            // (가장 가까운 노트만 보면 앞 노트를 Great로 칠 수 있는데 뒤 노트를 Great로 뺏어서 앞 노트가 Miss가 된다)
            int grade = distance <= perfectWindow ? 0 : distance <= greatWindow ? 1 : 2;
            if (grade < bestGrade || (grade == bestGrade && note.hitTime < candidate.hitTime))
            {
                candidate = note;
                closest = distance;
                bestGrade = grade;
            }
        }

        // 칠 노트는 없는데 너무 일찍 누른 노트가 있으면 그 노트를 Miss로 먹는다 (얼리 미스)
        // 없으면 연타가 공짜라서 계속 누르기만 해도 모든 노트가 범위 안에서 잡힌다.
        // 단, 방금 Miss 난 노트에 더 가까운 입력은 그 노트를 치려던 것 → 벌 없이 넘긴다.
        // 이게 없으면 빠른 같은 레인 연타에서 한 번 밀린 뒤 입력마다 다음 노트를 얼리 미스로 먹어서 끝까지 Miss가 이어졌다
        if (candidate == null && tooEarly != null &&
            currentTime - lastMissTime[(int)input] <= tooEarly.hitTime - currentTime)
        {
            ShowFeedback(input + " - Late", Color.gray);
            return Accuracy.Miss;
        }

        if (candidate == null && tooEarly != null)
        {
            Resolve(tooEarly, Accuracy.Miss);
            ShowFeedback(input + " - Too early (Miss)", JudgmentColor(Accuracy.Miss));
            return Accuracy.Miss;
        }

        if (candidate == null)
        {

            ShowFeedback(
                input + (upcoming ? " - Too early" : " - No note"),
                Color.gray
            );

            return Accuracy.Miss;
        }

        Accuracy result =
            closest <= perfectWindow
                ? Accuracy.Perfect
                : closest <= greatWindow
                    ? Accuracy.Great
                    : Accuracy.Good;

        string timing =
            currentTime < candidate.hitTime
                ? "Early"
                : "Late";

        Resolve(candidate, result);

        ShowFeedback(
            input + " - " + result +
            (result == Accuracy.Perfect
                ? ""
                : " / " + timing),
            JudgmentColor(result)
        );

        return result;
    }

    private void Resolve(NoteObject note, Accuracy result)
    {
        if (!activeNotes.Remove(note))
            return;

        judged++;
        DialogueManager.Send(result.ToString()); // Perfect/Great/Good/Miss (아래에서 Oops로 바뀌기 전)

        bool missed = result == Accuracy.Miss; // 아래에서 result가 Oops로 바뀔 수 있어서 미리 저장
        if (missed)
            lastMissTime[(int)note.input] = Mathf.Max(lastMissTime[(int)note.input], note.hitTime);

        if (!missed)
        {
            Shake();
            FlashLane(note.input, result);
        }

        // 곡 완성도(총 정확도): 노트마다 판정 가중치 / 전체 노트 수를 더한다. 전부 Perfect면 1.
        float weight = 0f;

        if (result == Accuracy.Perfect)
        {
            weight = 1f;
            perfect++;
        }
        else if (result == Accuracy.Great)
        {
            weight = 0.8f;
            great++;
        }
        else if (result == Accuracy.Good)
        {
            weight = 0.5f;
            good++;
        }
        else if (result == Accuracy.Miss)
            miss++;

        noteCount = notes.Count;

        MiracleProgress = Mathf.Clamp01(MiracleProgress + weight / noteCount);

        int lastCombo = combo;

        combo =
            result == Accuracy.Miss ||
            result == Accuracy.Good
                ? 0
                : combo + 1;

        if (lastCombo > combo)
            result = Accuracy.Oops;

        bestCombo = Mathf.Max(bestCombo, combo);

        // 친 노트는 즉시 제거, 놓친 노트는 그대로 지나가서 화면 밖으로 나간 뒤 제거
        Destroy(note.gameObject, missed ? previewTime : 0f);

        ShowFeedback(
            note.input + " - " + result,
            JudgmentColor(result)
        );

        //print("Mote!");
        CreateMote(note.input, result);
    }

    private void SpawnUpcomingNotes()
    {
        while (nextSpawnIndex < notes.Count &&
               hitTimes[nextSpawnIndex] - currentTime <= previewTime)
        {
            int i = nextSpawnIndex++;

            NoteObject note =
                Instantiate(NotePrefab).GetComponent<NoteObject>();

            note.Setup(
                notes[i].input,
                hitTimes[i],
                this,
                GetGuide(notes[i].input).transform.position.x,
                i
            );

            activeNotes.Add(note);
        }
    }

    // 다음에 쳐야 할 노트: 보이는 노트 중 가장 이른 것, 없으면 다음 스폰 예정 노트
    private bool TryGetNextNote(out NoteInputType input, out float hitTime)
    {
        NoteObject earliest = null;

        foreach (NoteObject note in activeNotes)
        {
            if (earliest == null || note.hitTime < earliest.hitTime)
                earliest = note;
        }

        if (earliest != null)
        {
            input = earliest.input;
            hitTime = earliest.hitTime;
            return true;
        }

        if (nextSpawnIndex < notes.Count)
        {
            input = notes[nextSpawnIndex].input;
            hitTime = hitTimes[nextSpawnIndex];
            return true;
        }

        input = default;
        hitTime = 0f;
        return false;
    }

    private GameObject GetGuide(NoteInputType input)
    {
        return input == NoteInputType.A
            ? GuideA
            : input == NoteInputType.B
                ? GuideB
                : input == NoteInputType.C
                    ? GuideC
                    : GuideD;
    }

    private Color JudgmentColor(Accuracy result)
    {
        return result == Accuracy.Perfect
            ? Color.yellow
            : result == Accuracy.Great
                ? Color.green
                : result == Accuracy.Good
                    ? new Color(1f, 0.5f, 0f)
                    : Color.red;
    }

    private void CreateMote(
        NoteInputType input,
        Accuracy accuracy)
    {
        if (moteFolder == null)
            return;

        if (currentMote != null)
            currentMote.SetActive(false);

        if(accuracy != Accuracy.Oops && accuracy != Accuracy.Miss)
        {
            
            if (Particle != null)
            {
                GameObject particle = Instantiate(Particle);

                particle.transform.position =
                    GetGuide(input).transform.position;

                Destroy(particle, 3f);
            }
            
            if (Lit != null)
            {
                GameObject lit = Instantiate(Lit);

                lit.transform.position =
                    GetGuide(input).transform.position;

                Destroy(lit, 3f);
            }
        }
            

        switch (accuracy)
        {
            case Accuracy.Perfect:
                CreateMoteInternal(
                    "Perfect",
                    motePos
                );

                effectScript.Progression(3);
                if (Shine != null)
                {
                    GameObject shine = Instantiate(Shine);

                    shine.transform.position =
                        motePos + new Vector3(0, 0, 1);

                    Destroy(shine, 3f);
                }

                break;

            case Accuracy.Great:
                CreateMoteInternal(
                    "Great",
                    motePos
                );
                effectScript.Progression(2);
                break;

            case Accuracy.Good:
                CreateMoteInternal(
                    "Good",
                    motePos
                );
                effectScript.Progression(1);
                break;

            case Accuracy.Miss:
                CreateMoteInternal(
                    "Miss",
                    motePos
                );
                break;

            case Accuracy.Oops:
                CreateMoteInternal(
                    "Oops",
                    motePos
                );
                break;
        }
    }

    private void CreateMoteInternal(
        string name,
        Vector3 pos)
    {
        Transform moteTransform =
            moteFolder.transform.Find(name);

        if (moteTransform == null)
            return;

        GameObject mote = moteTransform.gameObject;

        mote.SetActive(true);
        currentMote = mote;

        mote.transform.position = pos;
        mote.transform.localScale = new Vector3(10f,10f,10f);
        mote.transform.DOKill();

        if (name == "Perfect")
        {
            mote.transform
                .DOScale(10f * 1.2f, 0.05f)
                .SetEase(Ease.OutQuad)
                .OnComplete(() =>
                {
                    mote.transform
                        .DOScale(10f, 0.1f)
                        .SetEase(Ease.InQuad);
                });
        }
        else if (name == "Great")
        {
            mote.transform
                .DOScale(10f * 1.1f, 0.05f)
                .SetEase(Ease.OutQuad)
                .OnComplete(() =>
                {
                    mote.transform
                        .DOScale(10f, 0.1f)
                        .SetEase(Ease.InQuad);
                });
        }

        mote.transform.DOScale(Vector3.zero, 5f);
    }

    private void UpdateComboUI()
    {
        if (comboText == null)
            return;

        if (combo > 0)
        {
            string text = $"Combo {combo}";

            if (comboText.text != text)
            {
                if (combo == 1 && Combo != null)
                {
                    Combo.transform.DOKill();
                    Combo.transform.DOPunchPosition(
                        Vector3.up * 0.5f,
                        0.5f
                    );
                }

                comboText.text = text;
            }

            foreach (ParticleSystem p in comboGlows)
            {
                if (p == null)
                    continue;

                var emission = p.emission;
                emission.enabled = combo > 9;
            }
        }
        else
        {
            comboText.text = "";

            foreach (ParticleSystem p in comboGlows)
            {
                if (p == null)
                    continue;

                var emission = p.emission;
                emission.enabled = false;
            }
        }
    }

    private void ShowFeedback(
        string message,
        Color color)
    {
        feedback = message;
        feedbackColor = color;
        feedbackUntil = currentTime + 0.7f;

        // 판정 문구는 번쩍 나타났다가 feedbackUntil 지나면 LateUpdate가 흐리게 지운다
        if (feedbackLabel != null)
        {
            feedbackLabel.Content.text = message;
            feedbackStyle.normal.textColor = color;
            feedbackLabel.FadeIn(0.08f);
            feedbackShown = true;
        }
    }

    // 왼쪽 위 판정 패널 (I'm GUI 리테인드). 처음 한 번 만들어 왼쪽에서 밀어 넣고, 글만 매 프레임 바꾼다
    // Update는 곡이 안 돌 때 일찍 빠지므로 여기서
    private void LateUpdate()
    {
        if (!ready || !GUIStyleMaker.Initialized)
            return;

        if (hudPanel == null)
            BuildHud();

        // 음악 시작 전 MusicDelay초 동안 키 안내. 스토리가 있으면 끝난 다음부터
        if (!guidesFlashed && (story == null || !story.Playing))
        {
            guidesFlashed = true;
            FlashGuide(guideAText);
            FlashGuide(guideBText);
            FlashGuide(guideCText);
            if (guideDText != null)
                FlashGuide(guideDText);
        }

        notesLabel.Content.text = finished ? "RESULT" : $"NOTES  {judged} / {notes.Count}";
        scoreLabel.Content.text = Points.ToString("N0");
        countsLabel.Content.text = $"Perfect {perfect}   Great {great}\nGood {good}   Miss {miss}";

        var aPos = Camera.main.WorldToScreenPoint(GuideA.transform.position);
        aPos = new Vector2(aPos.x, GUIManager.ScreenSize.y - aPos.y);
        var bPos = Camera.main.WorldToScreenPoint(GuideB.transform.position);
        bPos = new Vector2(bPos.x, GUIManager.ScreenSize.y - bPos.y);
        var cPos = Camera.main.WorldToScreenPoint(GuideC.transform.position);
        cPos = new Vector2(cPos.x, GUIManager.ScreenSize.y - cPos.y);

        // 레인 기둥: 가이드 x를 가운데로 레인 간격만큼 폭, 화면 위아래 전체. 카메라가 흔들려도 따라가게 매 프레임
        float laneW = Mathf.Abs(bPos.x - aPos.x);
        for (int i = 0; i < laneFlash.Length; i++)
        {
            if (laneFlash[i] == null)
                continue; // 키보드 모드면 펌프 레인 기둥은 안 만든다
            float laneX = Camera.main.WorldToScreenPoint(GetGuide((NoteInputType)i).transform.position).x;
            laneFlash[i].SetRect(new Rect(laneX - laneW / 2f, 0f, laneW, Screen.height));
        }

        guideAText.SetRect(new Rect(aPos - new Vector3(50, -200), new Vector2(100, 100)));
        guideBText.SetRect(new Rect(bPos - new Vector3(50, -200), new Vector2(100, 100)));
        guideCText.SetRect(new Rect(cPos - new Vector3(50, -200), new Vector2(100, 100)));
        if (guideDText != null)
        {
            var dPos = Camera.main.WorldToScreenPoint(GuideD.transform.position);
            dPos = new Vector2(dPos.x, GUIManager.ScreenSize.y - dPos.y);
            guideDText.SetRect(new Rect(dPos - new Vector3(50, -200), new Vector2(100, 100)));
        }

        nextLabel.isVisible = false;
        if (!finished && TryGetNextNote(out NoteInputType nextInput, out float nextHitTime))
        {
            nextLabel.isVisible = true;
            nextLabel.Content.text = $"Next: {nextInput} / {Mathf.Max(0f, nextHitTime - currentTime):0.00}s";
        }

        if (feedbackShown && !finished && currentTime > feedbackUntil)
        {
            feedbackShown = false;
            feedbackLabel.FadeOut(0.25f);
        }
    }

    // 줄 높이는 GUILayout 대신 고정 칸. Next가 없을 때(결과 화면)는 그 칸이 빈다
    private void BuildHud()
    {
        var tutorsytle = GUIStyleMaker.Label(fontSize: 30);
        tutorsytle = GUIStyleMaker.Align(tutorsytle, TextAnchor.UpperCenter);
        tutorsytle = GUIStyleMaker.Background(tutorsytle, new Color(0.0f, 0.498f,1.0f,0.2f), Color.azure);

        guideAText = Widget.BoxLabel("키보드 A", new Rect(Camera.main.WorldToScreenPoint(new Vector3(GuideA.transform.position.x,HitPoint.transform.position.y,0)) - new Vector3(50, -50), new Vector2(100, 100)), tutorsytle);
        guideBText = Widget.BoxLabel("키보드 S", new Rect(Camera.main.WorldToScreenPoint(new Vector3(GuideB.transform.position.x,HitPoint.transform.position.y,0)) - new Vector3(50, -50), new Vector2(100, 100)), tutorsytle);
        guideCText = Widget.BoxLabel("키보드 D", new Rect(Camera.main.WorldToScreenPoint(new Vector3(GuideC.transform.position.x,HitPoint.transform.position.y,0)) - new Vector3(50, -50), new Vector2(100, 100)), tutorsytle);
        // 안내는 스토리가 끝난 뒤 LateUpdate가 띄운다
        guideAText.Opacity = guideBText.Opacity = guideCText.Opacity = 0f;
        if (pumpLane)
        {
            guideDText = Widget.BoxLabel("펌프 F", new Rect(Camera.main.WorldToScreenPoint(new Vector3(GuideD.transform.position.x,HitPoint.transform.position.y,0)) - new Vector3(50, -50), new Vector2(100, 100)), tutorsytle);
            guideDText.Opacity = 0f;
        }

        // 레인 기둥은 평소 투명. 레이어 -1 = 다른 UI 밑, 클릭은 안 막음
        for (int i = 0; i < laneFlash.Length; i++)
        {
            if (i == (int)NoteInputType.D && !pumpLane)
                continue;
            laneFlash[i] = Widget.Image(GUIStyleMaker.Solid(LaneColors[i]), Rect.zero);
            laneFlash[i].SetLayer(-1);
            laneFlash[i].isInteractable = false;
            laneFlash[i].Opacity = 0f;
        }

        // 곡 선택 화면 정보창과 같은 톤: 어두운 반투명 상자 + 펄색 글자. 점수만 크고 굵게
        var pearl = new Color(0xF8 / 255f, 0xF6 / 255f, 0xF0 / 255f, 1f);
        var panelStyle = GUIStyleMaker.Box(new Color(0.1667f, 0.2137f, 0.2333f, 0.7f));
        labelStyle = GUIStyleMaker.Label(pearl, 18, TextAnchor.UpperLeft).Wrap();
        var scoreStyle = new GUIStyle(labelStyle) { fontSize = 30, fontStyle = FontStyle.Bold };
        feedbackStyle = new GUIStyle(labelStyle);
        feedbackStyle.normal.textColor = feedbackColor;

        const float x = 32f, w = 298f;
        float line = labelStyle.CalcHeight(new GUIContent("가"), w);
        float y = 30f;
        Rect Row(float height)
        {
            var r = new Rect(x, y, w, height);
            y += height + 4f;
            return r;
        }

        hudPanel = Widget.Window(GUIContent.none, new Rect(16, 16, 330, 280), "match", panelStyle);
        hudPanel.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        scoreLabel = Widget.Label(hudPanel, "", Row(scoreStyle.CalcHeight(new GUIContent("0"), w)), scoreStyle);
        notesLabel = Widget.Label(hudPanel, "", Row(line), labelStyle);
        countsLabel = Widget.Label(hudPanel, "", Row(line * 2f), labelStyle);
        nextLabel = Widget.Label(hudPanel, "", Row(line), labelStyle);
        feedbackLabel = Widget.Label(hudPanel, feedback, Row(line), feedbackStyle);
        Widget.Button(hudPanel, "Restart", new Rect(x, y + 4f, w, 34f), Restart, GUIStyleMaker.Button(fontSize: 18));
        hudPanel.Rect.height = y + 4f + 34f + 14f - 16f; // 줄 수에 맞춰 상자 높이 (위 16px은 창 y)
        feedbackShown = true;

        // 인트로: 패널과 줄이 왼쪽에서 차례로 튀어 들어온다 (그룹은 자식을 안 끌고 다녀서 전부에 건다)
        var parts = new List<GUIItem> { hudPanel };
        parts.AddRange(hudPanel.Childrens);
        GUITween.Wave(parts, new Vector2(-360f, 0f), 0.45f, 0.04f, TweenHelper.EaseOutBack);
        hudPanel.FadeIn(0.3f);
    }

    // 음악 시작 전 MusicDelay초 동안: 0.5초 나타나고, 머물다가, 0.5초 사라진 뒤 지운다
    // 페이드는 위젯당 하나라 FadeOut을 바로 걸면 FadeIn이 끊긴다 → FadeIn이 끝난 뒤에 건다
    private static void FlashGuide(GUIItem guide)
    {
        guide.FadeIn(0.5f, onComplete: () =>
            guide.FadeOut(0.5f, MusicDelay - 1f, onComplete: () => GUIManager.Unregister(guide)));
    }

    // 판정이 좋을수록 밝게 번쩍. 연타하면 페이드가 처음부터 다시 걸린다
    private void FlashLane(NoteInputType lane, Accuracy result)
    {
        GUIImage flash = laneFlash[(int)lane];
        if (flash == null)
            return; // HUD가 아직 안 만들어짐

        flash.Opacity = result == Accuracy.Perfect ? 0.45f : result == Accuracy.Great ? 0.3f : 0.18f;
        flash.FadeTo(0f, LaneFlashTime, 0f, TweenHelper.EaseOutQuad);
    }

    // 왼쪽 위 판정 패널을 서서히 지운다 (BadAppleDirector: 마지막 쉬는 구간). 그룹 투명도라 줄·버튼도 같이 사라진다
    public void FadeOutHud(float duration)
    {
        if (hudPanel == null)
            return;
        hudPanel.FadeOut(duration);
        hudPanel.isInteractable = false; // 사라진 Restart 버튼이 눌리지 않게
    }

    // 꺼져 있는 동안(보정 중)은 패널도 숨긴다. 예전 OnGUI도 꺼지면 안 불렸다
    private void OnEnable()
    {
        if (hudPanel != null)
            hudPanel.isVisible = true;
    }

    private void OnDestroy()
    {
        GUIManager.Unregister(hudPanel);
        GUIManager.Unregister(guideAText); // 3초 안에 씬을 떠나면 아직 남아 있다
        GUIManager.Unregister(guideBText);
        GUIManager.Unregister(guideCText);
        GUIManager.Unregister(guideDText);
        if (pumpAction != null)
            pumpAction.performed -= OnD;
        foreach (GUIImage flash in laneFlash)
            GUIManager.Unregister(flash);
    }
}
