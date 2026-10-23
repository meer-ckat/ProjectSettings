using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    }

    public SongEntry[] songs;

    [TextArea(5, 20)]
    public string creditText = "만든 사람들\n\n(인스펙터의 Credit Text에서 수정)";

    // 특수 카드 id. Selected에는 절대 들어가지 않는다
    const string TutorialId = "__tutorial";
    const string CreditId = "__credit";
    const string ExitId = "__exit";
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
    public float[] difficultyScales_PreviewScale = { 1f, 1.5f, 2f, 3f };
    public float[] difficultyScales_judgeScale =  {0.7f, 1f, 1.5f, 2f};
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
    int diffCursor;           // 0~3 = 난이도, 4 = 나가기
    string pendingSong;       // 난이도 창에서 고르는 중인 곡

    // 위젯. 처음 한 번 만들고 트윈으로 움직인다. 창은 열 때 만들고 닫는 트윈이 끝나면 지운다
    GUIGroup[] cards;
    GUIButton play;
    GUIGroup creditPanel, creditView, diffPanel;
    GUIImage[] diffBars;
    GUILabel[] diffLabels;
    GUIImage loadFill;
    float loadFullW;
    readonly List<GUIItem> roots = new(); // 만든 루트 전부. 씬이 내려갈 때 지운다

    GUIStyle cardStyle, titleStyle, artistStyle, playStyle, creditStyle, creditTextStyle, diffHiStyle, hintStyle;

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
        if (song == ExitId) { Quit(); return; }
        if (song == TutorialId) { PreviewScale = 1f; Load(TutorialSong, TutorialSong, "Tutorial"); return; }
        // 일반 곡은 난이도 창을 먼저 띄운다. 고르면 ConfirmDifficulty → Load
        pendingSong = song;
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
        CloseDifficulty(); // 닫히는 애니메이션 동안 로딩 게이지가 찬다
        if (diffCursor >= DifficultyNames.Length) return; // 나가기
        source.PlayOneShot(enterClip);
        PreviewScale = difficultyScales_PreviewScale[Mathf.Min(diffCursor, difficultyScales_PreviewScale.Length - 1)];
        JudgeScale = difficultyScales_judgeScale[Mathf.Min(diffCursor, difficultyScales_judgeScale.Length - 1)];

        //난이도별 노트변화
        //일단 먼저 pendingSong에 맞는 entry를 찾는다
        SongEntry song = songs.FirstOrDefault(x => x.id == pendingSong);
        if (song == null) return;

        string DifficultySong = "";

        switch (diffCursor)
        {
            case 0:
                DifficultySong = string.IsNullOrEmpty(song.hard_id)? song.id : song.hard_id;
                break;
            case 1:
                DifficultySong = string.IsNullOrEmpty(song.normal_id)? song.id : song.normal_id;
                break;
            case 2:
                DifficultySong = string.IsNullOrEmpty(song.easy_id)? song.id : song.easy_id;
                break;
            case 3:
                DifficultySong = string.IsNullOrEmpty(song.easy_id)? song.id : song.easy_id;
                break;
        }

        Load(DifficultySong, pendingSong, "Game");
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
        System.Array.Resize(ref songs, n + 3);
        songs[n] = new SongEntry { id = TutorialId, title = "TUTORIAL", artist = "처음이라면" };
        songs[n + 1] = new SongEntry { id = CreditId, title = "CREDIT", artist = "만든 사람들" };
        songs[n + 2] = new SongEntry { id = ExitId, title = "EXIT", artist = "게임 종료" };
        }

    void Start()
    {
        wait = true;
        Invoke(nameof(a), 2f);
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
            int rows = DifficultyNames.Length + 1;
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
        Widget.BoxLabel(card, s.title, new Rect(r.x + pad, textY, coverSize, 40f * k), titleStyle);
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
        selected = i;
        targetX = i * Step;

        // 새로 고르면 버튼 즉시 숨기고, 가운데 카드가 도착하면 다시 페이드인
        GUITween.Kill(play);
        play.Opacity = 0f;
        play.Content.text = PlayLabel();

        for (int j = 0; j < cards.Length; j++)
        {
            MoveCard(cards[j], CardPos(j), SlideTime, j == i ? () => play.FadeIn(1f / fadeSpeed) : null);
            cards[j].FadeTo(j == i ? 1f : 0.55f, 0.2f);
        }
        cards[i].PunchScale(0.06f, 0.3f);
    }

    string PlayLabel()
    {
        string id = songs[selected].id;
        return id == CreditId ? "CREDIT" : id == ExitId ? "EXIT" : id == TutorialId ? "START" : "PLAY";
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
        diffCursor = DifficultyNames.Length;

        float w = cardW + 60f, h = cardH + 60f;
        Rect p = new Rect(centerX - cardW * 1.1f - w * 0.5f, cardY + (cardH - h) * 0.5f, w, h);
        diffPanel = Widget.Window(GUIContent.none, R(p.x, p.y, p.width, p.height), "difficulty", creditStyle);
        diffPanel.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다

        int rows = DifficultyNames.Length + 1; // + 나가기
        diffBars = new GUIImage[rows];
        diffLabels = new GUILabel[rows];
        float rowH = 60f, top = p.y + (p.height - rowH * (rows + 1)) * 0.5f; // 나가기 앞에 한 줄 비움
        for (int i = 0; i < rows; i++)
        {
            bool exit = i == DifficultyNames.Length;
            Rect row = R(p.x + 20f, top + (exit ? i + 1 : i) * rowH, p.width - 40f, rowH);

            // 줄마다 넷: 커서 바, 이름, 조작 안내, 투명 버튼. 그룹 안에서는 넣은 순서대로 그려진다
            diffBars[i] = Widget.Image(diffPanel, GUIStyleMaker.Solid(Color.black), row);
            diffBars[i].Opacity = 0.85f;
            diffLabels[i] = Widget.Label(diffPanel, exit ? "나가기" : DifficultyNames[i], row, creditTextStyle);
            Widget.Label(diffPanel, "\nA/D: 위, 아래 선택\nD: 결정", new Rect(row.x + 5f * k, row.y, row.width, row.height), hintStyle);
            int index = i;
            Widget.Button(diffPanel, "", row, () => ClickRow(index), GUIStyle.none);
        }
        Root(diffPanel, WindowLayer);
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
    }

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
    }
}
