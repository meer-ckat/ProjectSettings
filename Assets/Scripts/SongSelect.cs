using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// 곡 선택 화면 (단독 OnGUI)
// 카드 클릭 → 중앙으로 슬라이드 → 도착 후 PLAY 버튼 페이드인 → Pick → 난이도 창 → Game 씬
// 차트 JSON과 음원은 Resources에 같은 이름으로 둔다. songs의 id = 그 이름
// 곡 뒤에 CREDIT/EXIT 카드가 코드로 자동으로 붙는다 (인스펙터 songs에는 곡만 넣는다)
public class SongSelect : MonoBehaviour
{
    public static string Selected = "stage1"; // Game 씬을 바로 Play해도 동작
    public static float PreviewScale = 1f;    // 난이도. Match가 previewTime에 곱한다 (어려움 = 1)
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
    public float smoothTime = 0.2f;  // 슬라이드 시간
    public float fadeSpeed = 4f;     // 버튼 페이드 속도 (초당)

    [Header("난이도 (미리보기 배율)")]
    public float[] difficultyScales = { 1f, 1.5f, 2f, 3f }; // 어려움(기본)·중간·쉬움·매우 쉬움
    public float windowTime = 0.2f;  // 난이도 창 열림/닫힘 시간
    static readonly string[] DifficultyNames = { "어려움", "중간", "쉬움", "매우 쉬움" };

    const float RefH = 1080f;

    int selected = -1;
    float scrollX, targetX, vel;
    float buttonAlpha;
    float loadProgress = -1f; // -1 = 로딩 중 아님
    bool creditOpen;
    Vector2 creditScroll;
    bool diffOpen;       // 난이도 창 열림. false면 닫히는 애니메이션
    float diffAnim;      // 0 = 카드 크기·투명, 1 = 다 열림
    int diffCursor;      // 0~3 = 난이도, 4 = 나가기
    string pendingSong;  // 난이도 창에서 고르는 중인 곡

    GUIStyle cardStyle, titleStyle, artistStyle, playStyle, creditStyle, creditTextStyle, diffHiStyle;

    float Step => cardW + gap;

    public void Pick(string song)
    {
        source.PlayOneShot(enterClip);
        if (loadProgress >= 0f) return; // 연타로 중복 로드 방지
        if (song == CreditId) { creditOpen = true; creditScroll = Vector2.zero; return; }
        if (song == ExitId) { Quit(); return; }
        if (song == TutorialId) { PreviewScale = 1f; Load(TutorialSong, "Tutorial"); return; }
        // 일반 곡은 난이도 창을 먼저 띄운다. 고르면 ConfirmDifficulty → Load
        pendingSong = song;
        diffCursor = 4;
        diffOpen = true;
    }

    void Load(string song, string scene)
    {
        loadProgress = 0f;
        Selected = song; // Game/Tutorial 씬의 NoteManager/Match가 이 값으로 차트·음원을 읽음
        StartCoroutine(LoadScene(scene));
    }

    void ConfirmDifficulty()
    {
        diffOpen = false; // 닫히는 애니메이션 동안 로딩 게이지가 찬다
        if (diffCursor >= DifficultyNames.Length) return; // 나가기
        source.PlayOneShot(enterClip);
        PreviewScale = difficultyScales[Mathf.Min(diffCursor, difficultyScales.Length - 1)];
        Load(pendingSong, "Game");
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
        Debug.Log("<color=green>Loading " + scene + " (" + Selected + ")...</color>");
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

        // 이전에 고른 곡이 목록에 있으면 처음부터 중앙에 둔다
        for (int i = 0; i < songs.Length; i++)
        {
            if (songs[i].id == Selected)
            {
                SelectCard(i);
                scrollX = targetX; // 슬라이드 없이 바로
                break;
            }
        }
        }

    void Start()
    {
        wait = true;
        Invoke(nameof(a), 2f);
    }

    // 시간 기반 계산은 전부 여기서 (OnGUI는 프레임당 여러 번 호출됨)
    void Update()
    {
        scrollX = Mathf.SmoothDamp(scrollX, targetX, ref vel, smoothTime);

        bool arrived = selected >= 0 && Mathf.Abs(scrollX - targetX) < 1f;
        buttonAlpha = Mathf.MoveTowards(buttonAlpha, arrived ? 1f : 0f, fadeSpeed * Time.deltaTime);
        diffAnim = Mathf.MoveTowards(diffAnim, diffOpen ? 1f : 0f, Time.deltaTime / windowTime);
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

    void OnGUI()
    {
        if(wait) return;
        InitStyles();

        // 1080p 기준 좌표로 그리고 화면 높이에 맞춰 확대/축소
        float scale = Screen.height / RefH;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float screenW = Screen.width / scale;
        float centerX = screenW * 0.5f;

        selected = Mathf.Clamp(selected, 0, songs.Length - 1);

        // 크레딧 창이 열려 있으면 뒤쪽 카드·버튼은 입력을 받지 않는다 (가운데 키는 창 닫기에 씀)
        bool closeCredit = creditOpen && center;
        int creditStep = creditOpen ? (right ? 1 : 0) - (left ? 1 : 0) : 0; // 왼쪽=위로, 오른쪽=아래로
        if (creditOpen) left = right = center = false;
        // 난이도 창도 같은 식. A=위, D=아래, S=결정
        bool diffConfirm = diffOpen && center;
        int diffStep = diffOpen ? (right ? 1 : 0) - (left ? 1 : 0) : 0;
        if (diffOpen) left = right = center = false;
        GUI.enabled = !creditOpen && !diffOpen;

        // 카드
        for (int i = 0; i < songs.Length; i++)
        {
            float x = centerX - cardW * 0.5f + i * Step - scrollX;
            if (x + cardW < 0f || x > screenW) continue; // 화면 밖 생략

            Rect r = new Rect(x, cardY, cardW, cardH);
            DrawCard(r, songs[i], i == selected);

            // 투명 버튼으로 클릭만 받음
            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                SelectCard(i);
            else if(left)
                SelectCard(selected - 1);
            else if(right)
                SelectCard(selected + 1);
        }

        // PLAY 버튼
        if (selected >= 0 && buttonAlpha > 0f)
        {
            GUI.color = new Color(1f, 1f, 1f, buttonAlpha);
            Rect b = new Rect(centerX - 130f, cardY + cardH + 60f, 260f, 80f);
            string id = songs[selected].id;
            string label = id == CreditId ? "CREDIT" : id == ExitId ? "EXIT" : id == TutorialId ? "START" : "PLAY";
            bool clicked = GUI.Button(b, label, playStyle);
            GUI.color = Color.white;

            // 거의 다 나타났을 때만 클릭 인정
            if (clicked || center && buttonAlpha > 0.9f)
                Pick(songs[selected].id);
        }
        left = right = center = false;
        GUI.enabled = true;

        // 크레딧 창: 길면 스크롤 (휠·스크롤바·왼쪽/오른쪽 키). 창 아무 데나 클릭하거나 가운데 키로 닫힘
        if (creditOpen)
        {
            Rect p = new Rect(centerX - 500f, 140f, 1000f, 800f);
            GUI.Box(p, GUIContent.none, creditStyle);

            Rect view = new Rect(p.x + 30f, p.y + 30f, p.width - 60f, p.height - 60f);
            float textW = view.width - 20f; // 세로 스크롤바 자리
            // 짧으면 창 높이에 맞춰 가운데 정렬, 길면 글 높이만큼 스크롤
            float textH = Mathf.Max(creditTextStyle.CalcHeight(new GUIContent(creditText), textW), view.height);
            creditScroll.y = Mathf.Clamp(creditScroll.y + creditStep * 200f, 0f, textH - view.height);

            creditScroll = GUI.BeginScrollView(view, creditScroll, new Rect(0f, 0f, textW, textH));
            GUI.Label(new Rect(0f, 0f, textW, textH), creditText, creditTextStyle);
            GUI.EndScrollView();

            // 스크롤바가 클릭을 먼저 가져가므로 스크롤바를 잡아도 닫히지 않음
            if (GUI.Button(p, GUIContent.none, GUIStyle.none) || closeCredit)
                creditOpen = false;
        }

        // 난이도 창: 가운데 카드에서 조금 더 크게 펼쳐진다. A/D 또는 클릭으로 고르고 S(또는 커서 줄 다시 클릭)로 결정
        if (diffAnim > 0f)
        {
            float e = 1f - (1f - diffAnim) * (1f - diffAnim); // ease-out
            float w = Mathf.Lerp(cardW, cardW + 60f, e), h = Mathf.Lerp(cardH, cardH + 60f, e);
            Rect p = new Rect(centerX - cardW * 1.1f - w * 0.5f, cardY + (cardH - h) * 0.5f, w, h);
            GUI.color = new Color(1f, 1f, 1f, e);
            GUI.Box(p, GUIContent.none, creditStyle);

            int rows = DifficultyNames.Length + 1; // + 나가기
            float rowH = 60f, top = p.y + (p.height - rowH * (rows + 1)) * 0.5f; // 나가기 앞에 한 줄 비움
            for (int i = 0; i < rows; i++)
            {
                bool exit = i == DifficultyNames.Length;
                Rect row = new Rect(p.x + 20f, top + (exit ? i + 1 : i) * rowH, p.width - 40f, rowH);
                bool hi = i == diffCursor;
                if (hi)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.85f * e);
                    GUI.DrawTexture(row, Texture2D.whiteTexture);
                    GUI.color = new Color(1f, 1f, 1f, e);
                }
                GUI.Label(row, exit ? "나가기" : DifficultyNames[i], hi ? diffHiStyle : creditTextStyle);
                GUI.Label(new Rect(row.position + new Vector2(5,0), row.size), "\nA/D: 위, 아래 선택\nD: 결정");

                // 클릭: 다른 줄이면 커서 이동, 커서 줄이면 결정. 다 열린 뒤에만 받음
                if (diffOpen && e > 0.9f && GUI.Button(row, GUIContent.none, GUIStyle.none))
                {
                    if (hi) diffConfirm = true;
                    else { diffCursor = i; source.PlayOneShot(swipeClip); }
                }
            }
            GUI.color = Color.white;

            if (diffStep != 0)
            {
                diffCursor = (diffCursor + diffStep + rows) % rows; // 끝에서 반대쪽으로 돈다
                source.PlayOneShot(swipeClip);
            }
            if (diffConfirm)
                ConfirmDifficulty();
        }

        // 로딩 게이지 (땜빵: 배경 박스 + 흰색 채움)
        if (loadProgress >= 0f)
        {
            Rect bar = new Rect(centerX - 400f, RefH - 80f, 800f, 24f);
            GUI.Box(new Rect(bar.position, bar.size * 1.1f), GUIContent.none);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * loadProgress, bar.height), Texture2D.whiteTexture);
        }
    }

    void DrawCard(Rect r, SongEntry s, bool isSelected)
    {
        GUI.color = isSelected ? Color.white : new Color(1f, 1f, 1f, 0.55f);

        GUI.Box(r, GUIContent.none, cardStyle);

        float pad = 20f;
        float coverSize = r.width - pad * 2f;
        if (s.cover != null)
            GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, coverSize/4*4, coverSize/3*4), s.cover, ScaleMode.ScaleAndCrop);

        float textY = r.y + pad + coverSize + 10f;
        GUI.Box(new Rect(r.x + pad, textY, coverSize, 40f), s.title, titleStyle);
        GUI.Box(new Rect(r.x + pad, textY + 40f, coverSize, 30f), s.artist, artistStyle);

        GUI.color = Color.white;
    }

    void SelectCard(int i)
    {
        source.PlayOneShot(swipeClip);
        i = Mathf.Clamp(i, 0, songs.Length - 1);
        left = right = false; //특수기술: 기술부채 생성
        if (i == selected) return;
        selected = i;
        targetX = i * Step;
        buttonAlpha = 0f; // 새로 고르면 버튼 즉시 숨김
    }

    // GUI.skin은 OnGUI 안에서만 접근 가능 → 첫 OnGUI에서 생성
    void InitStyles()
    {
        if (titleStyle != null) return;

        // 기본 skin.box는 반투명 회색이라 Pearl White(#F8F6F0, 투명도 20%) 텍스처로 바꾸고, 글자는 검정
        var pearl = new Texture2D(1, 1);
        pearl.SetPixel(0, 0, new Color(0xF8 / 255f, 0xF6 / 255f, 0xF0 / 255f, 0.8f));
        pearl.Apply();

        cardStyle = new GUIStyle(GUI.skin.box);
        cardStyle.normal.background = pearl;

        titleStyle = new GUIStyle(cardStyle)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
        };
        titleStyle.normal.textColor = Color.black;
        artistStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip
        };
        artistStyle.normal.textColor = Color.black;
        artistStyle.normal.background = pearl;
        playStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 32,
            fontStyle = FontStyle.Bold
        };
        // 카드용 펄은 반투명이라 뒤 카드가 비쳐 글자가 안 읽힘 → 크레딧 창은 불투명
        var solid = new Texture2D(1, 1);
        solid.SetPixel(0, 0, new Color(0xF8 / 255f, 0xF6 / 255f, 0xF0 / 255f, 1f));
        solid.Apply();
        creditStyle = new GUIStyle(cardStyle);
        creditStyle.normal.background = solid;
        creditTextStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        creditTextStyle.hover.textColor = Color.black;
        creditTextStyle.normal.textColor = Color.black;
        // 난이도 창 커서 줄: 검정 바 위 흰 글자
        diffHiStyle = new GUIStyle(creditTextStyle);
        diffHiStyle.normal.textColor = Color.white;
        diffHiStyle.hover.textColor = Color.white;
    }
}