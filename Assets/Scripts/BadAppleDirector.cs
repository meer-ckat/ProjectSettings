using IMGUI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// Bad Apple!! 전용 연출. 평범한 곡인 척 시작했다가 곡 시간 5초에 "Real BadApple":
//   입력 차단 + 프리징(Time.timeScale 0, 검은 줄무늬가 멈춘 채로, 음악은 0.1초 구간 되풀이) → 검은 화면 페이드 인
//   → 까만 동안 처음부터 다시 준비(노트는 판정 없이 치움), 배경을 검게
//   → 2초에 페이드 아웃 + 짧은 글리치 → 곡·노트가 0초부터 다시, BadApple(픽셀 영상) 켜기
// Bad Apple 곡일 때만 Match가 붙인다. 두 번째 판에서는 다시 안 일어난다
public class BadAppleDirector : MonoBehaviour
{
    public const string SongId = "badapple_hard";

    public float triggerAt = 5f;   // 곡 시간(초)
    public float fadeIn = 0.5f;
    public float blackUntil = 4f;  // 메시지부터 페이드 아웃 시작까지
    public float fadeOut = 0.01f;
    public float glitchIn = 0.5f;  // 메시지 순간 글리치 길이
    public float glitchOut = 0.25f; // 화면이 걷힐 때 글리치 길이
    public float stutterLen = 0.1f; // 음악 글리치: 이 길이 구간을 글리치 동안 되풀이 (튀는 CD처럼)

    [Header("마지막 쉬는 구간: UI·덩굴 페이드 아웃 (영상만 남긴다). 어려움에서만")]
    public float uiFadeAt = 209.6f;  // 곡 시간(초). 어려움 기준 쉼 209.2~216.7초
    public float uiFadeTime = 1.5f;

    Match match;
    BadApple badApple;
    bool fired;
    GUIImage black;
    readonly GUIImage[] bars = new GUIImage[8];
    readonly Rect[] barSpots = new Rect[8]; // 줄 자리를 화면 비율(0~1)로. 창 크기가 바뀌어도 같은 자리에 멈춰 있게
    float glitchUntil;
    float revealGlitchAt = -1f;
    float stutterFrom = -1f; // 되풀이할 구간 시작 (음악 위치, 초)
    float stutterUntil;
    bool revealed, uiFaded;
    float uiFadeStart = -1f;
    CanvasGroup[] uiGroups;
    TMPro.TMP_Text combo;
    SpriteRenderer[] vines;

    void Awake()
    {
        // 씬에 직접 붙여 둬도 Bad Apple 곡이 아니면 아무것도 안 한다 (안 그러면 모든 곡에서 5초에 전환된다)
        if (SongSelect.SelectedNote != SongId)
        {
            enabled = false;
            return;
        }

        match = GetComponent<Match>();
        badApple = FindAnyObjectByType<BadApple>(FindObjectsInactive.Include);
        if (badApple != null)
            badApple.enabled = false; // 진짜 모드 전까지 꺼 둔다
    }

    void OnDestroy()
    {
        if (black != null && Time.timeScale == 0f)
            Time.timeScale = 1f; // 얼어 있는 도중에 씬을 나가면 다음 씬까지 멈춰 있지 않게
        GUIManager.Unregister(black);
        foreach (GUIImage bar in bars)
            GUIManager.Unregister(bar);
    }

    void Update()
    {
        if (!fired && match.currentTime >= triggerAt)
            Fire();
        Stutter();
        Glitch();
        FitToScreen();

        // 어려움만: 어려움은 쉼 뒤에 마지막 노트가 한 번 더 와서, 영상만 남은 화면에서 그 한 방으로 끝난다
        if (revealed && !uiFaded && match.Started && match.SongTime >= uiFadeAt
            && SongSelect.SelectedNote == SongId)
            StartUiFade();
        FadeUi();
    }

    // 마지막 쉬는 구간: 판정 패널·게이지·설정 버튼·콤보를 서서히 지워 Bad Apple 영상만 남긴다
    // 결과창(Result)이 같은 Rank 캔버스에 있어서 캔버스째로는 안 끄고 항목마다 CanvasGroup으로 지운다
    void StartUiFade()
    {
        uiFaded = true;
        uiFadeStart = Time.unscaledTime;
        match.FadeOutHud(uiFadeTime);

        var groups = new System.Collections.Generic.List<CanvasGroup>();
        GameObject rank = GameObject.Find("Rank");
        if (rank != null)
        {
            foreach (string name in new[] { "Horizontal Progress Bar", "Settings" })
            {
                Transform t = rank.transform.Find(name);
                if (t == null)
                    continue;
                CanvasGroup g = t.GetComponent<CanvasGroup>();
                if (g == null)
                    g = t.gameObject.AddComponent<CanvasGroup>();
                groups.Add(g);
            }
        }
        uiGroups = groups.ToArray();
        if (match.Combo != null)
            combo = match.Combo.GetComponent<TMPro.TMP_Text>();

        // 덩굴 = 레인 가이드 아래 키 큰 스프라이트. 작은 판정 박스는 마지막 노트를 쳐야 하니 남긴다
        var tall = new System.Collections.Generic.List<SpriteRenderer>();
        foreach (GameObject guide in new[] { match.GuideA, match.GuideB, match.GuideC, match.GuideD })
        {
            if (guide == null)
                continue;
            foreach (SpriteRenderer sr in guide.GetComponentsInChildren<SpriteRenderer>())
                if (sr.bounds.size.y > 2f)
                    tall.Add(sr);
        }
        vines = tall.ToArray();
    }

    void FadeUi()
    {
        if (uiFadeStart < 0f)
            return;
        float a = 1f - Mathf.Clamp01((Time.unscaledTime - uiFadeStart) / uiFadeTime);
        foreach (CanvasGroup g in uiGroups)
        {
            g.alpha = a;
            g.interactable = g.blocksRaycasts = a > 0f;
        }
        if (combo != null)
            combo.alpha = a;
        foreach (SpriteRenderer vine in vines)
        {
            Color c = vine.color;
            c.a = a;
            vine.color = c;
        }
        if (a <= 0f)
            uiFadeStart = -1f;
    }

    // 음악 글리치: 구간 끝을 지나면 시작으로 되감는다. 프레임 단위라 길이가 조금씩 흔들리는데, 그게 더 글리치 같다
    // 끝나면 그냥 흘러가게 두고, 화면이 까매질 때 Restart가 음악을 멈춘다
    void Stutter()
    {
        AudioSource music = match.Music;
        if (stutterFrom < 0f || music == null || !music.isPlaying)
            return;
        if (Time.unscaledTime >= stutterUntil)
        {
            stutterFrom = -1f;
            return;
        }
        if (music.time >= stutterFrom + stutterLen)
            music.time = stutterFrom;
    }

    void Fire()
    {
        fired = true;
        DialogueManager.Send("Real BadApple");
        match.InputBlocked = true;
        Time.timeScale = 0f; // 프리징: 덩굴·파티클까지 그 자리에 언다. 화면이 까매지면 OnBlack에서 1로
        StartGlitch(glitchIn);
        if (match.Music != null)
        {
            stutterFrom = match.Music.time;
            stutterUntil = Time.unscaledTime + glitchIn;
        }

        black = Widget.Image(GUIStyleMaker.Solid(Color.black), new Rect(0, 0, Screen.width, Screen.height));
        black.SetLayer(250);          // 대사·스토리·보정 화면보다 위
        black.isInteractable = false; // 클릭은 안 막는다(입력은 Match에서 막음)
        black.FadeIn(fadeIn, 0.5f, onComplete: OnBlack);
    }

    // 다 까매진 동안: 처음부터 다시 준비하고 배경을 검게
    void OnBlack()
    {
        Time.timeScale = 1f;
        match.HoldStart = true; // 음악 예약을 화면이 걷힐 때까지 미룬다
        match.Restart();        // 음악 멈춤, 남은 노트는 판정 없이 치움, 점수·콤보 초기화

        // 배경 = Background 캔버스의 RawImage(VideoPlayer로 도는 폴리곤 영상)
        GameObject bg = GameObject.Find("Background");
        if (bg != null)
        {
            foreach (VideoPlayer v in bg.GetComponentsInChildren<VideoPlayer>())
                v.Pause();
            foreach (RawImage raw in bg.GetComponentsInChildren<RawImage>())
                raw.color = Color.black;
        }
        if (Camera.main != null)
            Camera.main.backgroundColor = Color.black;

        float wait = Mathf.Max(0f, blackUntil - fadeIn);
        revealGlitchAt = Time.unscaledTime + wait;
        black.FadeOut(fadeOut, wait, onComplete: OnReveal);
    }

    // 화면이 걷힘: 곡·노트가 0초부터 다시(0.1초 뒤), 진짜 Bad Apple 시작
    void OnReveal()
    {
        GUIManager.Unregister(black);
        black = null;
        match.InputBlocked = false;
        match.HoldStart = false;
        revealed = true;
        if (badApple != null)
        {
            badApple.enabled = true;
            badApple.Init();
        }
    }

    // 완전 검은 가로 줄무늬. 시작할 때 한 번만 자리를 정하고 끝날 때까지 그대로 둔다 (프리징 걸린 화면처럼)
    void StartGlitch(float duration)
    {
        glitchUntil = Time.unscaledTime + duration;
        for (int i = 0; i < bars.Length; i++)
        {
            if (bars[i] == null)
            {
                bars[i] = Widget.Image(GUIStyleMaker.Solid(Color.black), Rect.zero);
                bars[i].SetLayer(260); // 검은 화면 위
                bars[i].isInteractable = false;
            }

            bars[i].isVisible = Random.value < 0.6f; // 줄 개수도 매번 조금 다르게
            float h = Random.Range(0.01f, 0.06f);
            barSpots[i] = new Rect(Random.Range(-0.15f, 0.15f), Random.Range(0f, 1f - h), 1f, h);
        }
    }

    // 검은 화면과 줄은 매 프레임 지금 화면 크기에 맞춘다 (연출 도중 창 크기가 바뀌어도 화면 전체를 덮게)
    void FitToScreen()
    {
        float w = Screen.width, h = Screen.height;
        if (black != null)
            black.Rect = new Rect(0f, 0f, w, h);
        for (int i = 0; i < bars.Length; i++)
        {
            Rect s = barSpots[i];
            if (bars[i] != null)
                bars[i].Rect = new Rect(s.x * w, s.y * h, s.width * w, s.height * h);
        }
    }

    void Glitch()
    {
        if (revealGlitchAt > 0f && Time.unscaledTime >= revealGlitchAt)
        {
            revealGlitchAt = -1f;
            StartGlitch(glitchOut);
        }

        if (Time.unscaledTime < glitchUntil)
            return;
        foreach (GUIImage bar in bars)
            if (bar != null)
                bar.isVisible = false;
    }
}
