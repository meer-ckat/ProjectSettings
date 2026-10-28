using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// 곡 선택 화면 (I'm GUI 리테인드 + 트윈)
// 카드 클릭 → 중앙으로 슬라이드 → 도착 후 PLAY 버튼 페이드인 → Pick → 난이도 창 → Game 씬
// 차트 JSON과 음원은 Resources에 같은 이름으로 둔다. songs의 id = 그 이름
// 곡 뒤에 CREDIT/EXIT 카드가 코드로 자동으로 붙는다 (인스펙터 songs에는 곡만 넣는다)
public class SongSelect : MonoBehaviour
{
    public static string SelectedNote = "stage1"; // Game 씬을 바로 Play해도 동작
    public static string SelectedSong = "stage1"; 
    public static float PreviewScale = 1f;    // 난이도. Match가 previewTime에 곱한다 (어려움 = 1)
    public static float JudgeScale = 1f;
    public static int SelectedDiff = -1; //0~3 = DifficultyNames 순서. -1 = 튜토리얼이나 바로 실행
    //펌프 모드는 발판 전용 채보라 기록을 따로 둔다 (키보드 키는 예전 그대로)
    public static string BestKey(string song, int diff) => "best_" + song + (PumpMode ? "_pump" : "") + "_" + diff;
    //엄격한 판정. 켜면 판정 범위 그대로 + 점수 그대로, 끄면 판정이 넓어지는 대신 점수가 깎임. 난이도 창에서 켜고 끔
    public static bool Strict = true;
    public static float ScoreScale = 1f; //Match가 최종 점수에 곱함 (난이도 배율 x 엄격 배율)
    public static string ScoreKey(string song) => "score_" + song + (PumpMode ? "_pump" : ""); //곡마다 최고 점수 하나 (난이도·엄격 상관없이)
    const string StrictKey = "strictJudge";
    //펌프 모드: 펌프 잇 업 발판(USB, 4칸 = A S D F 키). <곡>_pump_<easy|normal|hard> 발판 전용 채보를 쓴다. 메인 메뉴 SETTINGS에서 켬
    public static bool PumpMode = false;
    const string PumpKey = "pumpMode";

    //게임 중에는 설정대로 입력 기기를 고정한다 (Match가 부름). 펌프 모드면 발판 + 키보드(예비), 아니면 키보드만. 발판이 안 꽂혀 있으면 키보드
    //펌프 모드에도 키보드를 붙이는 이유: 레인 바인딩이 "USB Gamepad" 기종에만 묶여 있어서 다른 발판이면 발판 입력이 안 들어온다
    //자동 전환은 끈다 (켜두면 발판이 꽂혀 있을 때 그쪽으로 넘어감)
    public static void ApplyInputScheme(PlayerInput pi)
    {
        if (pi == null) return;
        pi.neverAutoSwitchControlSchemes = true;
        if (PumpMode && Joystick.current != null && Keyboard.current != null)
            pi.SwitchCurrentControlScheme("Keyboard&Mouse", Keyboard.current, Joystick.current); //메뉴와 같은 방식. 넘긴 기기는 스킴과 상관없이 다 붙음
        else if (PumpMode && Joystick.current != null)
            pi.SwitchCurrentControlScheme("Joystick", Joystick.current);
        else if (Keyboard.current != null)
            pi.SwitchCurrentControlScheme("Keyboard&Mouse", Keyboard.current);
    }
    public AudioClip swipeClip;
    public AudioClip enterClip;
    private AudioSource source;

    [System.Serializable]
    public class SongEntry
    {
        public string id;        // Resources의 차트/음원 이름
        public string title;
        public string artist;
        public Texture2D cover;  // 없어도 됨
        
        public string easy_id;
        public string normal_id;
        public string hard_id;

        //난이도 창 옆에 뜨는 난이도별 설명 (매우 쉬움은 easy_desc 같이씀)
        [TextArea] public string easy_desc;
        [TextArea] public string normal_desc;
        [TextArea] public string hard_desc;
    }

    public SongEntry[] songs;

    [TextArea(5, 20)]
    public string creditText = "만든 사람들\n\n(인스펙터의 Credit Text에서 수정)";

    // 특수 카드 id. Selected에는 절대 들어가지 않는다
    const string TutorialId = "__tutorial";
    const string CreditId = "__credit";
    const string ExitId = "__exit";
    const string SettingsId = "__settings";
    const string TutorialSong = "stage1"; // Match 초기화용으로만 읽음. 실제 차트·메트로놈은 TutorialCoach가 만든다

    [Header("레이아웃 (1920x1080 기준)")]
    public float cardW = 300f;
    public float cardH = 420f;
    public float gap = 40f;
    public float cardY = 220f;

    [Header("애니메이션")]
    public float smoothTime = 0.2f;  // 슬라이드 감도. 예전 SmoothDamp 값이라 실제 슬라이드 시간은 이것의 3배
    public float fadeSpeed = 4f;     // 버튼 페이드 속도 (초당)

    [Header("난이도 (미리보기 배율)")]
    public float[] difficultyScales_PreviewScale = { 1f, 1.8f, 2f, 2.5f };
    public float[] difficultyScales_judgeScale =  {0.5f, 1f, 1.5f, 2f};
    public float[] difficultyScales_score = { 1f, 0.8f, 0.6f, 0.4f }; //어려움 ~ 매우 쉬움 점수 배율
    public float lenientJudgeScale = 1.5f; //엄격 꺼짐이면 판정 범위에 더 곱함
    public float lenientScoreScale = 0.7f; //엄격 꺼짐이면 점수에 더 곱함
    public float windowTime = 0.2f;  // 난이도 창 열림/닫힘 시간
    static readonly string[] DifficultyNames = { "어려움", "중간", "쉬움", "매우 쉬움" };

    const float RefH = 1080f;

    // 레이어 = 그리는 순서이자 입력 우선순위. 위 레이어의 창이 덮은 자리에서는 아래 버튼이 안 눌린다
    const int CardLayer = 0, PlayLayer = 1, WindowLayer = 10, LoadLayer = 20;

    int selected = -1;
    float targetX;            // 가운데에 올 카드까지의 스크롤 (1080 기준)
    float loadProgress = -1f; // -1 = 로딩 중 아님
    bool creditOpen;
    bool diffOpen;            // 난이도 창 열림. false면 닫히는 중이거나 없음
    bool diffReady;           // 다 열린 뒤에만 클릭 받음
    int diffCursor;           // 난이도 창: 0~3 = 난이도, 4 = 엄격함, 5 = 나가기 / 설정 창: 0 = 입력 모드, 1 = 엄격함, 2~5 = 보정, 6 = 닫기
    int StrictRow => settingsOpen ? 1 : DifficultyNames.Length;
    //설정 창: 난이도 창 자리·모양 그대로 줄만 바꿔서 띄운다 (창·커서·설명창 코드를 같이 씀)
    bool settingsOpen;
    static readonly int[] OffsetSteps = { -10, -1, 1, 10 }; //설정 창 딜레이 보정 버튼(ms)
    int RowCount => settingsOpen ? 3 + OffsetSteps.Length : DifficultyNames.Length + 2;
    int ExitRow => RowCount - 1;
    string pendingSong;       // 난이도 창에서 고르는 중인 곡
    AudioSource sampleAudio;
    Dictionary<string, AudioClip> sampleClips = new(); //곡 id → 미리 읽어둔 음원. 카드 넘길때 안 끊기게

    // 위젯. 처음 한 번 만들고 트윈으로 움직인다. 창은 열 때 만들고 닫는 트윈이 끝나면 지운다
    GUIGroup[] cards;
    GUIButton play;
    GUIGroup creditPanel, creditView, diffPanel;
    GUIImage[] diffBars;
    GUILabel[] diffLabels;
    GUIImage loadFill;
    float loadFullW;
    GUIGroup infoPanel; //곡 위 정보창
    GUILabel infoLabel;
    GUIGroup descPanel; //가운데 카드 오른쪽 설명창. 난이도 창이랑 같이 열고 닫음
    GUILabel descLabel;
    readonly List<GUIItem> roots = new(); // 만든 루트 전부. 씬이 내려갈 때 지운다

    GUIStyle cardStyle, titleStyle, artistStyle, playStyle, creditStyle, creditTextStyle, diffHiStyle, hintStyle, infoStyle, infoBoxStyle, descStyle;

    float Step => cardW + gap;
    float SlideTime => smoothTime * 3f;

    // 1080p 기준 좌표 → 화면 픽셀. GUI.matrix로 키우면 글자가 흐려져서 좌표와 글자 크기에 직접 곱한다
    // ponytail: 화면 크기는 위젯을 만들 때 한 번 잰다. 창 크기를 바꾸면 다음에 여는 창부터 맞음
    float k = 1f, centerX;
    Rect R(float x, float y, float w, float h) => new Rect(x * k, y * k, w * k, h * k);
    T Root<T>(T item, int layer) where T : GUIItem
    {
        item.SetLayer(layer);
        roots.Add(item);
        return item;
    }

    public void Pick(string song)
    {
        source.PlayOneShot(enterClip);
        if (loadProgress >= 0f) return; // 연타로 중복 로드 방지
        if (song == CreditId) { OpenCredit(); return; }
        if (song == SettingsId) { settingsOpen = true; OpenDifficulty(); return; }
        if (song == ExitId) { Quit(); return; }
        //튜토리얼은 배율 1 고정. 안 정하면 직전 곡 난이도 배율(JudgeScale)이 그대로 넘어감
        if (song == TutorialId) { PreviewScale = 1f; JudgeScale = 1f; SelectedDiff = -1; Load(TutorialSong, TutorialSong, "Tutorial"); return; }
        // 일반 곡은 난이도 창을 먼저 띄운다. 고르면 ConfirmDifficulty → Load
        pendingSong = song;
        settingsOpen = false;
        OpenDifficulty();
    }

    void Load(string note, string song, string scene)
    {
        loadProgress = 0f;
        SelectedNote = note; // Game/Tutorial 씬의 NoteManager/Match가 이 값으로 차트·음원을 읽음
        SelectedSong = song;
        ShowLoading();
        StartCoroutine(LoadScene(scene));
    }

    void ConfirmDifficulty()
    {
        //엄격한 판정 줄은 창을 안 닫고 켜고 끄기만
        if (diffCursor == StrictRow)
        {
            Strict = !Strict;
            PlayerPrefs.SetInt(StrictKey, Strict ? 1 : 0);
            PlayerPrefs.Save();
            source.PlayOneShot(enterClip);
            diffLabels[StrictRow].Content.text = StrictLabel();
            UpdateCursor();
            return;
        }
        //설정 창: 입력 모드 켜고 끄기, 딜레이 보정값 더하고 빼기. 닫기 말고는 창을 안 닫는다
        if (settingsOpen)
        {
            if (diffCursor == ExitRow) { CloseDifficulty(); return; }
            source.PlayOneShot(enterClip);
            if (diffCursor == 0)
            {
                PumpMode = !PumpMode;
                PlayerPrefs.SetInt(PumpKey, PumpMode ? 1 : 0);
            }
            else
            {
                float offset = PlayerPrefs.GetFloat(LatencyCalibrator.PrefsKey, 0f) + OffsetSteps[diffCursor - 2] / 1000f;
                PlayerPrefs.SetFloat(LatencyCalibrator.PrefsKey, offset); //Game 씬 Match가 시작할 때 읽음
            }
            PlayerPrefs.Save();
            diffLabels[diffCursor].Content.text = RowLabel(diffCursor);
            UpdateCursor();
            return;
        }

        CloseDifficulty(); // 닫히는 애니메이션 동안 로딩 게이지가 찬다
        if (diffCursor == ExitRow) return; // 나가기
        source.PlayOneShot(enterClip);
        PreviewScale = difficultyScales_PreviewScale[Mathf.Min(diffCursor, difficultyScales_PreviewScale.Length - 1)];
        JudgeScale = difficultyScales_judgeScale[Mathf.Min(diffCursor, difficultyScales_judgeScale.Length - 1)] * (Strict ? 1f : lenientJudgeScale);
        ScoreScale = difficultyScales_score[Mathf.Min(diffCursor, difficultyScales_score.Length - 1)] * (Strict ? 1f : lenientScoreScale);

        //난이도별 노트변화
        //일단 먼저 pendingSong에 맞는 entry를 찾는다
        SongEntry song = songs.FirstOrDefault(x => x.id == pendingSong);
        if (song == null) return;

        SelectedDiff = diffCursor;
        Load(ChartId(song, diffCursor), pendingSong, "Game");
    }

    //실제로 불러올 채보. 펌프 모드면 발판 전용 <곡>_pump_<easy|normal|hard>, 없으면 키보드 채보(그땐 3레인)
    string ChartId(SongEntry song, int diff)
    {
        if (PumpMode)
        {
            string pid = song.id + "_pump_" + (diff == 0 ? "hard" : diff == 1 ? "normal" : "easy");
            if (Resources.Load<TextAsset>(pid) != null) return pid;
        }
        return DiffId(song, diff);
    }

    //난이도에 맞는 차트 id. 없으면 원본(song.id)
    //곡 위 정보창에서도 노트 수 셀때 씀
    string DiffId(SongEntry song, int diff)
    {
        switch (diff)
        {
            case 0:
                return string.IsNullOrEmpty(song.hard_id)? song.id : song.hard_id;
            case 1:
                return string.IsNullOrEmpty(song.normal_id)? song.id : song.normal_id;
            case 2:
                return string.IsNullOrEmpty(song.easy_id)? song.id : song.easy_id;
            case 3:
                return string.IsNullOrEmpty(song.easy_id)? song.id : song.easy_id;
        }
        return song.id;
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    public IEnumerator LoadScene(string scene)
    {
        Debug.Log("<color=green>Loading " + scene + " (" + SelectedNote + ")...</color>");
        AsyncOperation op = SceneManager.LoadSceneAsync(scene);

        // 2. 씬 로드가 완료되어도 자동으로 바로 전환되지 않도록 설정 (부드러운 연출용)
        op.allowSceneActivation = false;

        float timer = 0f;

        // 3. 로딩 진행 루프
        while (!op.isDone)
        {
            yield return null;

            // allowSceneActivation이 false일 때, progress는 최대 0.9f까지만 차오릅니다.
            if (op.progress < 0.9f)
            {
                // 실제 로딩 진행도를 슬라이더에 반영
                loadProgress = op.progress;
            }
            else
            {
                // 실제 로딩은 끝났지만 게이지가 끝까지 차오르는 연출 (최소 로딩 시간 확보)
                timer += Time.unscaledDeltaTime;
                loadProgress = Mathf.Lerp(0.9f, 1f, timer);

                // 게이지가 100% 채워지면 화면을 전환
                if (loadProgress >= 1f)
                {
                    op.allowSceneActivation = true;
                    yield break;
                }
            }
        }
    }

    bool wait = true;

    void a(){
        wait = false;
        source = GetComponent<AudioSource>();

        // 곡 뒤에 TUTORIAL/CREDIT/EXIT 카드를 붙인다 (플레이 중에만 바뀌고 씬에는 저장 안 됨)
        int n = songs.Length;
        System.Array.Resize(ref songs, n + 4);
        songs[n] = new SongEntry { id = TutorialId, title = "TUTORIAL", artist = "처음이라면" };
        songs[n + 1] = new SongEntry { id = SettingsId, title = "SETTINGS", artist = "입력 모드·판정·보정" };
        songs[n + 2] = new SongEntry { id = CreditId, title = "CREDIT", artist = "만든 사람들" };
        songs[n + 3] = new SongEntry { id = ExitId, title = "EXIT", artist = "게임 종료" };
        }

    void Start()
    {
        wait = true;
        Strict = PlayerPrefs.GetInt(StrictKey, 1) == 1; //저장 안 했으면 켜짐
        PumpMode = PlayerPrefs.GetInt(PumpKey, 0) == 1; //저장 안 했으면 키보드
        //메뉴는 입력 모드와 상관없이 키보드와 발판 둘 다 받는다 (넘긴 기기는 스킴과 상관없이 다 붙음)
        //ponytail: 메뉴 시작할 때 꽂혀 있던 발판만. 메뉴 도중에 꽂으면 씬을 다시 들어와야 함
        if (Keyboard.current != null && Joystick.current != null)
            GetComponent<PlayerInput>().SwitchCurrentControlScheme("Keyboard&Mouse", Keyboard.current, Joystick.current);
        Invoke(nameof(a), 2f);
        StartCoroutine(PreloadSamples());
    }

    //인트로 2초 동안 곡 음원을 하나씩 미리 읽어둔다
    //음원이 Decompress On Load라 LoadAudioData가 그 자리에서 압축을 풂 → 한 프레임에 한 곡씩
    //곡 전부 메모리에 풀어둠 (곡당 수십MB). 곡 많아지면 옆 카드만 읽게 바꾸기
    IEnumerator PreloadSamples()
    {
        foreach (SongEntry s in songs)
        {
            ResourceRequest req = Resources.LoadAsync<AudioClip>(s.id);
            yield return req;
            AudioClip clip = req.asset as AudioClip;
            if (clip == null) continue;
            clip.LoadAudioData();
            sampleClips[s.id] = clip;
            yield return null;
        }
    }

    void OnDestroy()
    {
        foreach (GUIItem item in roots)
            GUIManager.Unregister(item);
    }

    bool left, right, center;

    public void Onc(InputAction.CallbackContext c)
    {
        if(c.started)
        right = true;
    }
    public void Ona(InputAction.CallbackContext c)
    {
        if(c.started)
        left = true;
    }
    public void Onb(InputAction.CallbackContext c)
    {
        if(c.started)
        center = true;
    }

    void Update()
    {
        if (cards == null)
        {
            // 스타일은 GUIManager의 첫 OnGUI가 GUIStyleMaker를 초기화한 뒤에 만들 수 있다
            if (wait || !GUIStyleMaker.Initialized) return;
            Build();
        }

        // 창이 열려 있으면 뒤쪽 카드·버튼은 입력을 받지 않는다 (가운데 키는 창에서 씀)
        // 크레딧: 왼쪽=위로, 오른쪽=아래로, 가운데=닫기. 난이도: A=위, D=아래, S=결정
        int step = (right ? 1 : 0) - (left ? 1 : 0);
        if (creditOpen)
        {
            float max = creditView.ContentSize.y - creditView.Rect.height;
            creditView.ScrollPosition.y = Mathf.Clamp(creditView.ScrollPosition.y + step * 200f * k, 0f, Mathf.Max(0f, max));
            if (center) CloseCredit();
        }
        else if (diffOpen)
        {
            int rows = ExitRow + 1;
            if (step != 0) MoveCursor((diffCursor + step + rows) % rows); // 끝에서 반대쪽으로 돈다
            if (center) ConfirmDifficulty();
        }
        else
        {
            if (left)
                SelectCard(selected - 1);
            else if (right)
                SelectCard(selected + 1);

            // 거의 다 나타났을 때만 결정
            if (center && play.Opacity > 0.9f)
                Pick(songs[selected].id);
        }
        left = right = center = false;

        bool modal = creditOpen || diffOpen;
        foreach (GUIGroup card in cards)
            card.isEnabled = !modal;
        play.isEnabled = !modal;

        if (loadFill != null)
            loadFill.Rect.width = loadFullW * loadProgress;
    }

    // ---------------------------------------------------------------
    // 카드
    // ---------------------------------------------------------------

    void Build()
    {
        k = Screen.height / RefH;
        centerX = Screen.width / k * 0.5f;
        InitStyles();

        // 이전에 고른 곡이 목록에 있으면 처음부터 중앙에 둔다
        selected = Mathf.Max(0, System.Array.FindIndex(songs, s => s.id == pendingSong));
        targetX = selected * Step;

        cards = new GUIGroup[songs.Length];
        for (int i = 0; i < songs.Length; i++)
            cards[i] = MakeCard(i);

        play = Root(Widget.Button(PlayLabel(), R(centerX - 130f, cardY + cardH + 60f, 260f, 80f), OnPlayClicked, playStyle), PlayLayer);
        play.Opacity = 0f;

        //곡 위 정보창. 카드 위(cardY) 빈자리에 둔다
        Rect ip = R(centerX - 450f, 20f, 900f, 170f);
        infoPanel = Widget.Window(GUIContent.none, ip, "info", infoBoxStyle);
        infoPanel.Mask = false;
        infoLabel = Widget.Label(infoPanel, "", new Rect(ip.x + 20f * k, ip.y, ip.width - 40f * k, ip.height), infoStyle);
        Root(infoPanel, PlayLayer);
        infoPanel.Opacity = 0f;
        ShowInfo(0.5f);

        // 인트로: 가운데 카드부터 바깥으로 차례로 아래에서 튀어 오르며 나타난다
        for (int i = 0; i < cards.Length; i++)
        {
            float delay = Mathf.Abs(i - selected) * 0.07f;
            GUIGroup card = cards[i];
            foreach (GUIItem child in card.Childrens)
                child.MoveIn(new Vector2(0f, 240f * k), 0.55f, delay, TweenHelper.EaseOutBack);
            card.MoveIn(new Vector2(0f, 240f * k), 0.55f, delay, TweenHelper.EaseOutBack);
            card.Opacity = 0f;
            card.FadeTo(i == selected ? 1f : 0.55f, 0.4f, delay);
        }
        play.FadeIn(1f / fadeSpeed, 0.5f);
    }

    Vector2 CardPos(int i) => new Vector2(centerX - cardW * 0.5f + i * Step - targetX, cardY) * k;

    GUIGroup MakeCard(int i)
    {
        SongEntry s = songs[i];
        Rect r = new Rect(CardPos(i), new Vector2(cardW, cardH) * k);
        GUIGroup card = Widget.Window(GUIContent.none, r, "card" + i, cardStyle);
        card.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다

        float pad = 20f * k;
        float coverSize = r.width - pad * 2f;
        if (s.cover != null)
            Widget.Image(card, s.cover, new Rect(r.x + pad, r.y + pad, coverSize/4*4, coverSize/3*4), ScaleMode.ScaleAndCrop);

        float textY = r.y + pad + coverSize + 10f * k;
        //제목이 칸보다 길면 그 카드만 글자를 줄임 (Super Mario World Overworld가 잘렸음)
        GUIStyle ts = titleStyle;
        float need = titleStyle.CalcSize(new GUIContent(s.title)).x;
        if (need > coverSize)
        {
            ts = new GUIStyle(titleStyle);
            ts.fontSize = Mathf.FloorToInt(titleStyle.fontSize * coverSize / need * 0.95f);
        }
        Widget.BoxLabel(card, s.title, new Rect(r.x + pad, textY, coverSize, 40f * k), ts);
        Widget.BoxLabel(card, s.artist, new Rect(r.x + pad, textY + 40f * k, coverSize, 30f * k), artistStyle);

        // 투명 버튼으로 클릭만 받음
        Widget.Button(card, "", r, () => SelectCard(i), GUIStyle.none);
        return Root(card, CardLayer);
    }

    // 그룹을 옮겨도 자식은 안 따라오므로 자식마다 같은 트윈을 건다. 같이 출발해서 간격이 유지된다
    static void MoveCard(GUIGroup card, Vector2 to, float duration, System.Action onComplete = null)
    {
        foreach (GUIItem child in card.Childrens)
            child.MoveTo(to + child.Pos - card.Pos, duration, 0f, TweenHelper.EaseOutExpo);
        card.MoveTo(to, duration, 0f, TweenHelper.EaseOutExpo, onComplete);
    }

    void SelectCard(int i)
    {
        source.PlayOneShot(swipeClip);
        i = Mathf.Clamp(i, 0, songs.Length - 1);
        left = right = false; //특수기술: 기술부채 생성
        if (i == selected) return;

        cards[selected].ScaleTo(Vector2.one, 0.1f);
        selected = i;
        targetX = i * Step;

        // 새로 고르면 버튼 즉시 숨기고, 가운데 카드가 도착하면 다시 페이드인
        GUITween.Kill(play);
        play.Opacity = 0f;
        play.Content.text = PlayLabel();
        GUITween.Kill(infoPanel);
        infoPanel.Opacity = 0f;

        for (int j = 0; j < cards.Length; j++)
        {
            MoveCard(cards[j], CardPos(j), SlideTime, j == i ? () => { play.FadeIn(1f / fadeSpeed); ShowInfo(0f); } : null);
            cards[j].FadeTo(j == i ? 1f : 0.55f, 0.2f);
        }
        cards[i].RenderScale = new Vector2(1,1);
        cards[i].PunchScale(0.06f, 0.3f);

        if(sampleAudio == null)
        {
            sampleAudio = new GameObject("Sample Audio", typeof(AudioSource)).GetComponent<AudioSource>();
        }
        else
            if(sampleAudio.isPlaying)
                sampleAudio.Stop();
        //StopCoroutine(godamnasssAudioStopper())는 새 코루틴을 하나 더 만들 뿐이라 돌던 게 안 멈춤
        //→ 예전 카드의 코루틴이 남아서 20초 뒤에 지금 곡을 끊었음. StartCoroutine이 돌려준 걸로 멈춰야 함
        if (sampleStopper != null)
            StopCoroutine(sampleStopper);

        //미리 읽어둔게 있으면 그거, 아직 못 읽었으면 그냥 바로 읽음
        if(songs[selected].id.StartsWith("_")) //특수 기능 꺼지게 하는 특수 기능
            return;

        if (!sampleClips.TryGetValue(songs[selected].id, out var clip))
            clip = Resources.Load<AudioClip>(songs[selected].id);
        sampleAudio.volume = 1;
        sampleAudio.PlayOneShot(clip);
        sampleStopper = StartCoroutine(godamnasssAudioStopper());
    }

    Coroutine sampleStopper; //지금 도는 미리듣기 멈춤 코루틴

    IEnumerator godamnasssAudioStopper()
    {
        float end = Time.time + 20;

        while(Time.time < end && sampleAudio.isPlaying)
            yield return null;

        //1.5초 동안 소리 줄이고 멈춤
        //예전 1/300은 int끼리 나눠서 0이라 안 줄었음 + 프레임 수로 세면 fps마다 길이가 달라서 시간으로 셈
        for(float t = 0f; t < 1.5f; t += Time.deltaTime)
        {
            sampleAudio.volume = 1f - t / 1.5f;
            yield return null;
        }
        sampleAudio.Stop();
    }

    string PlayLabel()
    {
        string id = songs[selected].id;
        return id == CreditId ? "CREDIT" : id == ExitId ? "EXIT" : id == TutorialId ? "START" : id == SettingsId ? "SETTINGS" : "PLAY";
    }

    // ---------------------------------------------------------------
    // 곡 위 정보창: 난이도별 노트 수, 최고기록 (설명은 난이도 창 옆 descPanel)
    // ---------------------------------------------------------------

    [System.Serializable]
    class NoteList { public List<Note> noteList; } //노트 수만 셀거라 noteList만

    Dictionary<string, int> noteCounts = new(); //차트 id → 노트 수. 넘길때마다 json 다시 안읽게

    int NoteCount(string id)
    {
        if (noteCounts.ContainsKey(id)) return noteCounts[id];
        TextAsset json = Resources.Load<TextAsset>(id);
        int count = json == null ? 0 : JsonUtility.FromJson<NoteList>(json.text)?.noteList?.Count ?? 0;
        noteCounts[id] = count;
        return count;
    }

    string Desc(SongEntry song, int diff)
    {
        switch (diff)
        {
            case 0: return song.hard_desc;
            case 1: return song.normal_desc;
        }
        return song.easy_desc;
    }

    //가운데 카드 정보로 글자 바꾸고 페이드인. 특수카드(튜토리얼/크레딧/나가기)는 안띄움
    void ShowInfo(float delay)
    {
        SongEntry s = songs[selected];
        if (s.id == TutorialId || s.id == CreditId || s.id == ExitId || s.id == SettingsId) return;

        int bestPoints = PlayerPrefs.GetInt(ScoreKey(s.id), -1);
        string text = bestPoints < 0 ? "최고 점수  없음\n" : $"최고 점수  {bestPoints:N0}\n";
        for (int i = 0; i < DifficultyNames.Length; i++)
        {
            float best = PlayerPrefs.GetFloat(BestKey(s.id, i), -1f);
            string record = best < 0f ? "기록 없음" : $"{Match.RankName(best)}  {best * 100f:0.00}%"; //랭크 기준은 Match.RankAt 하나
            text += $"{DifficultyNames[i]}  |  노트 {NoteCount(ChartId(s, i))}  |  최고 {record}\n";
        }
        infoLabel.Content.text = text.TrimEnd('\n');
        infoPanel.FadeIn(1f / fadeSpeed, delay);
    }

    void OnPlayClicked()
    {
        if (play.Opacity > 0f && !creditOpen && !diffOpen)
            Pick(songs[selected].id);
    }

    // ---------------------------------------------------------------
    // 크레딧 창: 길면 스크롤 (휠·스크롤바·왼쪽/오른쪽 키). 창 아무 데나 클릭하거나 가운데 키로 닫힘
    // ---------------------------------------------------------------

    void OpenCredit()
    {
        creditOpen = true;

        Rect p = R(centerX - 500f, 140f, 1000f, 800f);
        creditPanel = Widget.Window(GUIContent.none, p, "credit", creditStyle);
        creditPanel.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다

        Rect view = new Rect(p.x + 30f * k, p.y + 30f * k, p.width - 60f * k, p.height - 60f * k);
        float textW = view.width - 20f; // 세로 스크롤바 자리. 스크롤바는 배율 없이 실제 픽셀 폭이라 k를 안 곱한다
        // 짧으면 창 높이에 맞춰 가운데 정렬, 길면 글 높이만큼 스크롤
        float textH = Mathf.Max(creditTextStyle.CalcHeight(new GUIContent(creditText), textW), view.height);

        creditView = Widget.Window(creditPanel, GUIContent.none, view, "credit.view", GUIStyle.none);
        creditView.Scrollable = true;
        creditView.PaddingLeft = creditView.PaddingRight = creditView.PaddingTop = creditView.PaddingBottom = 0f;
        creditView.ContentSize = new Vector2(textW, textH);
        Widget.Label(creditView, creditText, new Rect(view.x, view.y, textW, textH), creditTextStyle);

        // 그룹 자식은 넣은 순서대로 이벤트를 받는다. 스크롤바가 클릭을 먼저 가져가므로 스크롤바를 잡아도 닫히지 않음
        Widget.Button(creditPanel, "", p, CloseCredit, GUIStyle.none);
        Root(creditPanel, WindowLayer);

        // 인트로: 아래에서 떠오르며 나타난다 (그룹 투명도는 자식에도 곱해진다)
        creditPanel.FadeIn(0.2f);
        GUITween.Wave(Tree(creditPanel), new Vector2(0f, 50f * k), 0.35f, 0f, TweenHelper.EaseOutBack);
    }

    void CloseCredit()
    {
        if (!creditOpen) return;
        creditOpen = false;

        // 아웃트로: 아래로 가라앉으며 사라지고, 끝나면 지운다
        GUIGroup panel = creditPanel;
        panel.isInteractable = false; // 사라지는 동안 뒤 카드 클릭을 막지 않게
        panel.FadeOut(0.18f, onComplete: () => GUIManager.Unregister(panel));
        GUITween.Wave(Tree(panel), new Vector2(0f, 30f * k), 0.18f, 0f, TweenHelper.EaseInQuad, inward: false);
    }

    // 그룹과 그 아래 전부. 그룹은 자식을 안 끌고 다녀서 옮길 때는 전부에 트윈을 건다
    static List<GUIItem> Tree(GUIGroup group, List<GUIItem> into = null)
    {
        into ??= new List<GUIItem>();
        into.Add(group);
        foreach (GUIItem child in group.Childrens)
        {
            if (child is GUIGroup g) Tree(g, into);
            else into.Add(child);
        }
        return into;
    }

    // ---------------------------------------------------------------
    // 난이도 창: 가운데 카드 크기에서 조금 더 크게 펼쳐진다. A/D 또는 클릭으로 고르고 S(또는 커서 줄 다시 클릭)로 결정
    // ---------------------------------------------------------------

    void OpenDifficulty()
    {
        diffOpen = true;
        diffReady = false;
        diffCursor = ExitRow;

        float w = cardW + 60f, h = cardH + 60f;
        Rect p = new Rect(centerX - cardW * 1.1f - w * 0.5f, cardY + (cardH - h) * 0.5f, w, h);
        diffPanel = Widget.Window(GUIContent.none, R(p.x, p.y, p.width, p.height), "difficulty", creditStyle);
        diffPanel.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다

        int rows = RowCount;
        diffBars = new GUIImage[rows];
        diffLabels = new GUILabel[rows];
        float rowH = 60f, top = p.y + (p.height - rowH * rows) * 0.5f; // 엄격한 판정 줄은 예전에 나가기 앞 빈 줄 자리
        for (int i = 0; i < rows; i++)
        {
            bool exit = i == ExitRow;
            Rect row = R(p.x + 20f, top + i * rowH, p.width - 40f, rowH);

            // 줄마다 넷: 커서 바, 이름, 조작 안내, 투명 버튼. 그룹 안에서는 넣은 순서대로 그려진다
            diffBars[i] = Widget.Image(diffPanel, GUIStyleMaker.Solid(Color.black), row);
            diffBars[i].Opacity = 0.85f;
            diffLabels[i] = Widget.Label(diffPanel, RowLabel(i), row, creditTextStyle);
            Widget.Label(diffPanel, "\nA/D: 위, 아래 선택\nD: 결정", new Rect(row.x + 5f * k, row.y, row.width, row.height), hintStyle);
            int index = i;
            Widget.Button(diffPanel, "", row, () => ClickRow(index), GUIStyle.none);
        }
        Root(diffPanel, WindowLayer);

        //설명창: 가운데 카드 오른쪽, 난이도 창이랑 좌우대칭 (왼쪽에 두면 화면 밖으로 나감). 글자는 UpdateCursor가 커서 따라 바꿈
        Rect dp = R(centerX * 2f - p.xMax, p.y, p.width, p.height);
        descPanel = Widget.Window(GUIContent.none, dp, "difficulty.desc", infoBoxStyle);
        descPanel.Mask = false;
        descLabel = Widget.Label(descPanel, "", new Rect(dp.x + 20f * k, dp.y + 20f * k, dp.width - 40f * k, dp.height - 40f * k), descStyle);
        Root(descPanel, WindowLayer);
        descPanel.FadeIn(windowTime, 0f, TweenHelper.EaseOutQuad);
        descPanel.MoveIn(new Vector2(30f * k, 0f), 0.25f, 0f, TweenHelper.EaseOutQuad);
        descLabel.MoveIn(new Vector2(30f * k, 0f), 0.25f, 0f, TweenHelper.EaseOutQuad);

        UpdateCursor();

        // 인트로: 카드 크기에서 펼쳐지며 나타나고, 줄이 위에서부터 차례로 미끄러져 들어온다
        // RenderScale은 그룹 상자에만 걸린다(글자는 안 커짐)
        diffPanel.RenderScale = new Vector2(cardW / w, cardH / h);
        diffPanel.ScaleTo(Vector2.one, windowTime, 0f, TweenHelper.EaseOutQuad);
        diffPanel.FadeIn(windowTime, 0f, TweenHelper.EaseOutQuad, () => diffReady = true);
        IReadOnlyList<GUIItem> parts = diffPanel.Childrens;
        for (int j = 0; j < parts.Count; j++)
            parts[j].MoveIn(new Vector2(-30f * k, 0f), 0.25f, j / 4 * 0.04f, TweenHelper.EaseOutQuad);
    }

    void CloseDifficulty()
    {
        if (!diffOpen) return;
        diffOpen = false;

        // 아웃트로: 카드 크기로 접히며 사라지고, 끝나면 지운다
        GUIGroup panel = diffPanel;
        panel.isInteractable = false;
        panel.ScaleTo(new Vector2(cardW / (cardW + 60f), cardH / (cardH + 60f)), windowTime, 0f, TweenHelper.EaseInQuad);
        panel.FadeOut(windowTime, 0f, TweenHelper.EaseInQuad, () => GUIManager.Unregister(panel));

        GUIGroup desc = descPanel;
        desc.isInteractable = false;
        desc.FadeOut(windowTime, 0f, TweenHelper.EaseInQuad, () => GUIManager.Unregister(desc));
    }

    // 클릭: 다른 줄이면 커서 이동, 커서 줄이면 결정. 다 열린 뒤에만 받음
    void ClickRow(int i)
    {
        if (!diffOpen || !diffReady) return;
        if (i == diffCursor) ConfirmDifficulty();
        else MoveCursor(i);
    }

    void MoveCursor(int i)
    {
        diffCursor = i;
        source.PlayOneShot(swipeClip);
        UpdateCursor();
    }

    // 커서 줄: 검정 바 위 흰 글자
    void UpdateCursor()
    {
        for (int i = 0; i < diffBars.Length; i++)
        {
            bool hi = i == diffCursor;
            diffBars[i].isVisible = hi;
            diffLabels[i].Style = hi ? diffHiStyle : creditTextStyle;
        }

        //커서 난이도 설명. 나가기거나 설명 비어있으면 창 숨김
        string desc = diffCursor == ExitRow ? ""
            : diffCursor == StrictRow ? StrictDesc()
            : !settingsOpen ? Desc(songs[selected], diffCursor)
            : diffCursor == 0 ? PumpDesc()
            : OffsetDesc();
        descLabel.Content.text = desc;
        descPanel.isVisible = !string.IsNullOrEmpty(desc);
    }

    string RowLabel(int i)
    {
        if (i == ExitRow) return settingsOpen ? "닫기" : "나가기";
        if (i == StrictRow) return StrictLabel();
        if (!settingsOpen) return DifficultyNames[i];
        if (i == 0) return PumpLabel();
        int ms = OffsetSteps[i - 2];
        return $"보정 {(ms > 0 ? "+" : "")}{ms}ms";
    }

    string PumpLabel() => PumpMode ? "입력: 펌프" : "입력: 키보드";
    string PumpDesc() => PumpMode
        ? "펌프 모드\n\n펌프 발판(A S D F 4칸)으로\n발판 전용 채보를 밟아요.\n기록도 키보드와 따로예요."
        : "키보드 모드\n\nA S D 키 3레인으로 쳐요.\n(펌프 발판을 연결했으면\n결정 키로 바꾸세요)";

    string OffsetDesc()
    {
        int ms = Mathf.RoundToInt(PlayerPrefs.GetFloat(LatencyCalibrator.PrefsKey, 0f) * 1000f);
        return $"딜레이 보정값\n\n지금 {ms:+0;-0;0}ms\n\n+ : 일찍 치는 사람\n- : 늦게 치는 사람\n\n(게임 중 설정의 캘리브레이션으로\n자동 측정도 돼요)";
    }

    string StrictLabel() => Strict ? "엄격함 켬" : "엄격함 끔"; //길면 왼쪽 조작 안내랑 겹침

    //엄격한 판정 줄에 커서가 있을 때 설명창 글
    string StrictDesc() => Strict
        ? $"판정이 미치고 팔짝 뛰게 빡세집니다. 비교해보니까 어려움+엄격함은 정박에서 프로세카 Perfect보다 조금 빡빡하고, 투덱, osu!의 최상위 판정보다는 널널하답니다. 그리고 어려움+엄격함 풀콤보 기준 태고보다 빡빡하고 얼불춤 B-X (최대 600bpm) 보다 약간 널널해요 \n하지만 점수를 100% 받죠.\n\n(끄면 판정이 {lenientJudgeScale:0.#}배 넓어지고\n점수는 {lenientScoreScale * 100f:0}%만 받습니다)"
        : $"솔직히 엄격함을 켜야 재밌다고 생각해요. {lenientJudgeScale:0.#}배 넓습니다.\n점수는 {lenientScoreScale * 100f:0}%만 받고요.\n\n(켜면 판정이 진짜 더럽게 좁아지고\n점수를 100% 받ㅅㅂ니다.)";

    // ---------------------------------------------------------------
    // 로딩 게이지 (땜빵: 배경 박스 + 흰색 채움). 채움 폭은 Update가 loadProgress로 맞춘다
    // ---------------------------------------------------------------

    void ShowLoading()
    {
        Rect bar = R(centerX - 400f, RefH - 80f, 800f, 24f);
        GUIBoxLabel bg = Root(Widget.BoxLabel("", new Rect(bar.position, bar.size * 1.1f)), LoadLayer);
        loadFill = Root(Widget.Image(Texture2D.whiteTexture, bar), LoadLayer + 1);
        loadFullW = bar.width;
        loadFill.Rect.width = 0f;
        bg.FadeIn(0.2f);
        loadFill.FadeIn(0.2f);
    }

    // GUIStyleMaker는 GUIManager의 첫 OnGUI 뒤에야 쓸 수 있다 → Build에서 생성
    // 글자 크기는 1080p 기준값에 화면 배율 k를 곱한다
    void InitStyles()
    {
        int Px(int size) => Mathf.RoundToInt(size * k);

        // Pearl White(#F8F6F0, 투명도 20%) 배경, 글자는 검정
        var pearl = new Color(0xF8 / 255f, 0xF6 / 255f, 0xF0 / 255f, 0.8f);

        cardStyle = GUIStyleMaker.Box(pearl);

        titleStyle = new GUIStyle(cardStyle)
        {
            fontSize = Px(28),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
        };
        titleStyle.normal.textColor = Color.black;
        artistStyle = GUIStyleMaker.Label(Color.black, Px(20), TextAnchor.MiddleCenter);
        artistStyle.clipping = TextClipping.Clip;
        artistStyle.normal.background = GUIStyleMaker.Solid(pearl);
        playStyle = GUIStyleMaker.Button(fontSize: Px(32));
        // 카드용 펄은 반투명이라 뒤 카드가 비쳐 글자가 안 읽힘 → 크레딧 창은 불투명
        creditStyle = GUIStyleMaker.Box(new Color(pearl.r, pearl.g, pearl.b, 1f));
        creditTextStyle = GUIStyleMaker.Label(Color.black, Px(28), TextAnchor.MiddleCenter).Wrap();
        diffHiStyle = new GUIStyle(creditTextStyle).Text(Color.white);
        hintStyle = GUIStyleMaker.Label(fontSize: Px(16));
        //정보창은 반전: 검정 배경에 펄 글자
        infoBoxStyle = GUIStyleMaker.Box(pearl);
        infoStyle = GUIStyleMaker.Label(Color.black, Px(20), TextAnchor.MiddleLeft);
        infoStyle.clipping = TextClipping.Clip;
        infoStyle.richText = true;
        descStyle = new GUIStyle(infoStyle) { fontSize = Px(24), alignment = TextAnchor.MiddleCenter }.Wrap();
    }
}
