using System.Collections.Generic;
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

    [System.Serializable]
    public class Caption
    {
        public float from; // 곡 시간(초)
        public float to;
        [TextArea] public string text; // 첫 줄 = 이름, 다음 줄부터 설명
    }

    // 진짜 모드(영상이 나온 뒤)에서만 뜬다. 곡 시간 = 영상 시간
    // Match가 런타임에 붙이는 컴포넌트라, 인스펙터에서 채우려면 Game 씬 Match 오브젝트에 이 컴포넌트를 직접 붙여 둔다
    [Header("우하단 캐릭터 설명: 영상에 그 캐릭터가 나오는 동안 (곡 시간, 초)")]
    public Caption[] captions = { };
    public float captionFade = 0.3f;

    [Header("번쩍임: 이 구간(곡 시간, 초)의 동시치기마다 화면이 하얗게 번쩍인다")]
    public float flashFrom = 111.8f;
    public float flashTo = 118.7f;
    public float flashAlpha = 0.45f; // 완전한 흰색은 눈이 아프다
    public float flashTime = 0.15f;
    const float MinFlashGap = 0.34f; // 광과민성: 초당 3번 넘게 번쩍이지 않게 (WCAG 2.3.1). 줄이지 말 것

    // 카메라 샷: 편집 영상의 "3D 기울이기"처럼 찍힌 화면 전체를 납작한 카드로 보고 비스듬히 눕히고 돌린다
    // 카메라는 그대로 두고 투영 행렬만 바꾼다 → 깊이(z)가 달라도 다 같은 카드에 붙어 있어서 노트·덩굴·영상이 안 어긋난다
    // 치는 구간: 위쪽만 살짝 눕히고(노트가 멀리서 오는 모양이라 읽기 안 불편함) 거의 멈춘 채 느리게 흔들리고 조금씩 떠다닌다
    // 마지막 쉬는 구간(노트 하나만 남음)에서만 체스 편집처럼 마디마다 각도를 바꾼다
    // 각도가 바뀔 때는 마디 첫 박에 출발해 EaseOutCubic(박에 바로 움직이기 시작해 길게 풀리며 멈춤). 샷 앞뒤 정면에서 들어오고 나갈 때는 InOutCubic
    // 판정선 가운데를 축으로 기운다. 줌은 유치해서 안 씀. 레인을 대각선으로 만드는 y·z 각도는 치는 구간에선 아주 조금만
    // 각도 (x, y, z) °: x+ = 위쪽이 멀어짐(기타 히어로 길처럼), x- = 아래쪽이 멀어짐, y+ = 오른쪽이 멀어짐, z+ = 시계 반대로 기욺
    [System.Serializable]
    public class Shot
    {
        public float from, to;   // 곡 시간(초). 이 사이에 있는 마디 첫 박마다 다음 각도로 넘어간다
        public int beats = 8;    // 몇 박마다 넘어갈지. 8 = 두 마디. angles가 하나면 상관없음
        public Vector3[] angles; // 끝까지 가면 처음부터 다시
        public Vector3 sway;     // 각도에 더하는 느린 흔들림 폭 (°, 축마다)
        public Vector2 drift;    // 화면이 떠다니는 폭 (월드 단위. 화면 높이 = 10)
        public Vector3 orbitTo;  // 0이 아니면 궤도: from~to 동안 angles[0]에서 이 각도로 천천히 돈다
        public float blendSeconds; // 정면에서 들어오고 나가는 데 걸리는 초. 0이면 camMoveBeats
    }

    [Header("카메라 샷 (곡 시간, 초). 조용한 구간은 살짝 눕히기, 어려운 구간 몇 곳은 느린 궤도")]
    public Shot[] shots =
    {
        // 인트로 + 4분음표만 S로 치는 조용한 구간
        new() { from = 0.4f, to = 28.3f, angles = new Vector3[] { new(8f, 0f, 0f) }, sway = new(1.5f, 3f, 0.8f), drift = new(0.15f, 0.08f) },
        // 어려운 구간 궤도 3개: 아래에서 올려다본 채(x+) 한쪽 대각선에서 반대쪽으로 천천히 돈다. 5초에 걸쳐 들어오고 나간다
        // 오른쪽 대각선(오른쪽이 가까움, y-) → 왼쪽으로. 초당 3.7~4.2노트
        new() { from = 63.0f, to = 77.0f, angles = new Vector3[] { new(10f, -10f, 0f) }, orbitTo = new(10f, 10f, 0f), blendSeconds = 5f,
                sway = new(0.5f, 0f, 0.3f), drift = new(0.08f, 0.04f) },
        // 드럼 들어가기 전 숨 고르는 구간
        new() { from = 97.8f, to = 111.8f, angles = new Vector3[] { new(7f, 3f, 0f) }, sway = new(1.5f, 2.5f, 0.8f), drift = new(0.15f, 0.08f) },
        // 곡에서 가장 빽빽한 구간(초당 최대 5.2노트). 왼쪽 대각선 → 오른쪽으로
        new() { from = 125.6f, to = 139.6f, angles = new Vector3[] { new(9f, 10f, 0f) }, orbitTo = new(9f, -10f, 0f), blendSeconds = 5f,
                sway = new(0.5f, 0f, 0.3f), drift = new(0.08f, 0.04f) },
        // 초당 4.0~4.2노트. 오른쪽 대각선 → 왼쪽으로, 조금 더 크게
        new() { from = 160.4f, to = 174.4f, angles = new Vector3[] { new(11f, -12f, 0f) }, orbitTo = new(10f, 9f, 0f), blendSeconds = 5f,
                sway = new(0.5f, 0f, 0.3f), drift = new(0.08f, 0.04f) },
        // 클라이맥스 앞 S 위주 구간
        new() { from = 181.3f, to = 195.3f, angles = new Vector3[] { new(9f, -3f, 0f) }, sway = new(1.5f, 2.5f, 0.8f), drift = new(0.15f, 0.08f) },
        // 마지막 한 레인. UI는 이미 사라져서 여기서만 마디마다 각도를 바꾼다
        new() { from = 210.04f, to = 216.63f, beats = 4, angles = new Vector3[] { new(12f, 10f, -3f), new(-8f, -12f, 3f), new(14f, 12f, -4f), new(16f, -12f, 4f) },
                sway = new(1f, 1.5f, 0.5f), drift = new(0.1f, 0.05f) },
    };
    public float camFov = 60f;         // 카드를 보는 가상 렌즈. 넓을수록 기운 카드의 가까운 쪽이 더 커진다 (원근이 세짐)
    public float camMoveBeats = 3f;    // 다음 각도로 넘어가는 데 걸리는 박 (138BPM에서 1박 = 0.43초). Quint 2박은 너무 빠르고 덜 섞인다는 평
    public float camSwayBeats = 16f;   // 흔들림·떠다님 한 바퀴에 걸리는 박 (16 = 네 마디, 약 7초). 축마다 속도를 조금씩 달리해서 같은 모양이 반복돼 보이지 않게
    public float camBarOffset = 0.471f; // 첫 마디가 시작하는 곡 시간(초) = badapple_hard.json의 gridOffset (게임은 안 읽어서 여기 적어 둠)

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

    float lastSong; // 곡 시간이 뒤로 가면 Restart된 것

    GUIGroup captionRoot;
    GUILabel captionLabel;
    Caption shownCaption;
    float captionAlpha;

    List<float> flashTimes; // 번쩍일 곡 시간. 진짜 모드 시작할 때 채보에서 뽑는다
    int nextFlash;
    float flashStart = -1f;
    GUIImage flash;

    Camera cam;
    bool camOn;
    Vector3 camPos0;

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
        GUIManager.Unregister(captionRoot);
        GUIManager.Unregister(flash);
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

        bool live = revealed && match.Started;
        float song = match.SongTime;
        if (live && song < lastSong - 0.5f)
            nextFlash = 0; // Restart: 처음부터 다시 번쩍임
        lastSong = song;
        UpdateCaption(live, song);
        UpdateFlash(live, song);
    }

    // 카메라 연출은 투영 행렬만 바꾼다. Match.Shake(카메라 위치 흔들기)는 연출 중에도 그대로 먹는다
    void LateUpdate() => UpdateCamera(revealed && match.Started, match.SongTime);

    // ---------------------------------------------------------------
    // 우하단 캐릭터 설명
    // ---------------------------------------------------------------

    void UpdateCaption(bool live, float song)
    {
        Caption now = null;
        if (live)
            foreach (Caption c in captions)
                if (song >= c.from && song < c.to) { now = c; break; }

        // 새 설명이면 글을 바꾸고 처음부터 페이드인. 사라질 때는 글을 그대로 둔 채 페이드아웃
        if (now != null && now != shownCaption)
        {
            if (captionRoot == null)
                BuildCaption();
            shownCaption = now;
            captionLabel.Content.text = now.text;
            captionAlpha = 0f;
        }
        captionAlpha = Mathf.MoveTowards(captionAlpha, now != null ? 1f : 0f, Time.unscaledDeltaTime / captionFade);
        if (captionRoot == null)
            return;
        captionRoot.Opacity = captionAlpha;
        captionRoot.isVisible = captionAlpha > 0f;

        // 1080p 기준 크기. 창 크기가 바뀌어도 우하단에 붙어 있게 매 프레임 맞춘다
        float k = Screen.height / 1080f;
        var r = new Rect(Screen.width - 460f * k, Screen.height - 150f * k, 430f * k, 120f * k);
        captionRoot.Rect = r;
        captionLabel.Rect = new Rect(r.x + 18f * k, r.y + 10f * k, r.width - 36f * k, r.height - 20f * k);
    }

    void BuildCaption()
    {
        float k = Screen.height / 1080f;
        captionRoot = Widget.Window(GUIContent.none, Rect.zero, "ba_caption", GUIStyleMaker.Box(new Color(0f, 0f, 0f, 0.6f)));
        captionRoot.Mask = false; // Widget.Window 기본 Mask=true는 자식을 안쪽 여백 10px에서 잘라 버린다
        captionLabel = Widget.Label(captionRoot, "", Rect.zero,
            GUIStyleMaker.Label(Color.white, Mathf.RoundToInt(22f * k), TextAnchor.MiddleLeft));
        captionRoot.SetLayer(190); // 검은 화면(250)·보정 화면(200) 밑
        captionRoot.isInteractable = false;
    }

    // ---------------------------------------------------------------
    // 번쩍임
    // ---------------------------------------------------------------

    // 동시치기 = interval 0인 노트. 너무 붙은 것은 건너뛰어 MinFlashGap보다 자주 번쩍이지 않는다
    void BuildFlashTimes()
    {
        flashTimes = new List<float>();
        if (NoteManager.Instance == null)
            return;
        float t = 0f, spb = 60f / match.BPM; // Match가 hitTimes를 만드는 식과 같다
        foreach (Note n in NoteManager.Instance.CurrentData.notes)
        {
            t += n.interval * spb;
            if (n.interval == 0f && t >= flashFrom && t < flashTo &&
                (flashTimes.Count == 0 || t - flashTimes[^1] >= MinFlashGap))
                flashTimes.Add(t);
        }
    }

    void UpdateFlash(bool live, float song)
    {
        if (!live || flashTimes == null)
            return;
        // 한 프레임에 여러 개를 지나쳐도 한 번만 번쩍인다
        while (nextFlash < flashTimes.Count && song >= flashTimes[nextFlash])
        {
            nextFlash++;
            flashStart = Time.unscaledTime;
        }

        float a = flashStart < 0f ? 0f : flashAlpha * (1f - Mathf.Clamp01((Time.unscaledTime - flashStart) / flashTime));
        if (a <= 0f && flash == null)
            return;
        if (flash == null)
        {
            flash = Widget.Image(GUIStyleMaker.Solid(Color.white), Rect.zero);
            flash.SetLayer(240); // 검은 화면(250) 밑
            flash.isInteractable = false;
        }
        flash.Rect = new Rect(0f, 0f, Screen.width, Screen.height);
        flash.Opacity = a;
        flash.isVisible = a > 0f;
    }

    // ---------------------------------------------------------------
    // 마지막 구간 카메라
    // ---------------------------------------------------------------

    void UpdateCamera(bool live, float song)
    {
        // 지금 키(j). 키 시간(마디 첫 박)에 앞 키 각도에서 출발해 camMoveBeats 박(샷에 blendSeconds가 있으면 그 초) 동안 넘어온다
        int j = camKeys.FindLastIndex(k => k.time <= song);
        CamKey now = j >= 0 ? camKeys[j] : new CamKey(0f, null, null);
        float move = j >= 0 && now.shot.blendSeconds > 0f ? now.shot.blendSeconds : camMoveBeats * 60f / match.BPM;
        bool moving = j > 0 && song - now.time < move;
        if (!live || (now.angle == null && !(moving && camKeys[j - 1].angle != null)))
        {
            if (camOn)
                RestoreCamera();
            return;
        }

        if (!camOn)
        {
            cam = Camera.main;
            if (cam == null)
                return;
            camPos0 = cam.transform.position; // 축 기준. 매 프레임 재면 흔들기를 축 맞추기가 되돌려 버린다
            camOn = true;
        }

        // weight = 샷에 들어가 있는 정도 (정면 0 ~ 샷 1). 흔들림·떠다님도 이만큼만 걸어서 들어오고 나갈 때 안 튄다
        Vector3 angle = AngleOf(now, song);
        float weight = now.angle != null ? 1f : 0f;
        if (moving)
        {
            float x = (song - now.time) / move;
            CamKey last = camKeys[j - 1];
            // 정면에서 들어가고 나올 때는 InOutCubic: 멈춘 화면에서 OutCubic은 첫 프레임부터 최고 속도라 툭 끊겨 보인다
            // 샷 안에서 각도가 바뀔 때만 OutCubic (박에 맞춰 바로 움직이기 시작해 길게 풀리며 멈춤)
            float t = last.angle == null || now.angle == null
                ? (x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f)
                : 1f - Mathf.Pow(1f - x, 3f);
            angle = Vector3.Lerp(AngleOf(last, song), angle, t);
            weight = Mathf.Lerp(last.angle != null ? 1f : 0f, weight, t);
        }

        // 느린 흔들림·떠다님. 축마다 속도를 달리해 같은 모양이 되풀이돼 보이지 않게 한다
        float ph = song * 2f * Mathf.PI / (camSwayBeats * 60f / match.BPM);
        angle += Vector3.Scale(now.shot.sway, new Vector3(Mathf.Sin(ph), Mathf.Sin(ph * 0.71f + 1f), Mathf.Sin(ph * 0.53f + 2f))) * weight;
        Vector2 drift = Vector2.Scale(now.shot.drift, new Vector2(Mathf.Sin(ph * 0.61f + 0.5f), Mathf.Sin(ph * 0.83f + 1.5f))) * weight;

        // 축 = 판정선 가운데 (A~D 레인 가운데 x, 판정 박스 y)
        var pivot = new Vector2((match.GuideA.transform.position.x + match.GuideC.transform.position.x) * 0.5f,
            match.HitPoint.transform.position.y);
        float size = cam.orthographicSize, aspect = cam.aspect;
        Matrix4x4 ortho = Matrix4x4.Ortho(-size * aspect, size * aspect, -size, size, cam.nearClipPlane, cam.farClipPlane);
        cam.projectionMatrix = CardTilt(angle, pivot - (Vector2)camPos0, drift) * ortho;
        match.HideLaneFlash = true; // 화면에 곧게 선 레인 기둥은 기울어진 레인과 안 맞는다
    }

    // 키의 각도. 궤도 샷이면 샷 시간 동안 angles[0] → orbitTo로 천천히 (양 끝은 느리게) 돈다. 정면 키는 0
    static Vector3 AngleOf(CamKey key, float song)
    {
        if (key.angle == null)
            return Vector3.zero;
        Shot s = key.shot;
        if (s.orbitTo == Vector3.zero)
            return key.angle.Value;
        return Vector3.Lerp(key.angle.Value, s.orbitTo, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(s.from, s.to, song)));
    }

    // 카메라 키 = (이 시간에 출발, 이 각도로, 어느 샷). angle null = 정면으로 돌아감 (shot은 끝난 샷). 진짜 모드 시작할 때 BPM으로 한 번 만든다
    readonly struct CamKey
    {
        public readonly float time;
        public readonly Vector3? angle;
        public readonly Shot shot;
        public CamKey(float time, Vector3? angle, Shot shot) { this.time = time; this.angle = angle; this.shot = shot; }
    }
    readonly List<CamKey> camKeys = new();

    // 샷마다 from~to 사이 마디 첫 박에 각도 키를 찍고, 샷이 끝난 뒤 첫 박에 정면 키를 찍는다 (다음 샷이 그 박에 바로 시작하면 생략)
    void BuildCamKeys()
    {
        camKeys.Clear();
        foreach (Shot shot in shots)
        {
            if (shot.angles == null || shot.angles.Length == 0)
            {
                Debug.LogWarning($"BadAppleDirector: {shot.from}~{shot.to}초 샷에 angles가 비어 있어 건너뜀 (씬 인스펙터 값 확인)");
                continue;
            }
            float bar = shot.beats * 60f / match.BPM;
            float t = camBarOffset + Mathf.Ceil((shot.from - camBarOffset) / bar) * bar;
            if (camKeys.Count > 0 && camKeys[^1].angle == null && camKeys[^1].time >= t - 0.01f)
                camKeys.RemoveAt(camKeys.Count - 1);
            for (int k = 0; t < shot.to - 0.1f; k++, t += bar) // to가 마디 첫 박과 거의 같으면 그 박에 정면으로 돌아간다
                if (k < shot.angles.Length || shot.angles.Length > 1) // 각도가 하나면 첫 키만 (같은 각도로 다시 출발하지 않게)
                    camKeys.Add(new CamKey(t, shot.angles[k % shot.angles.Length], shot));
            camKeys.Add(new CamKey(t, null, shot));
        }
    }

    // 원래 orthographic 화면을 카드 한 장으로 보고 기울인 투영 행렬. 카메라 위치·각도는 안 건드린다 (흔들기도 그대로 먹음)
    // 화면 점(NDC x, y) → 축을 원점으로 → 카드를 angle만큼 돌림 → 가상 렌즈(camFov)로 원근 → 축을 원래 화면 자리 + drift로
    // 축 자리는 원근을 거쳐도 그대로라, 판정선 가운데는 drift만큼만 움직인다
    // 주의: 이 행렬에서는 ScreenToWorldPoint·ScreenPointToRay가 틀린 값을 낸다 (EffectScript는 역행렬로 직접 계산)
    Matrix4x4 CardTilt(Vector3 angle, Vector2 pivotWorld, Vector2 drift)
    {
        float size = cam.orthographicSize, aspect = cam.aspect;
        float d = 1f / Mathf.Tan(camFov * 0.5f * Mathf.Deg2Rad); // 카드에서 가상 렌즈까지 (카드 반 높이 = 1)
        var pivot = new Vector3(pivotWorld.x / (size * aspect), pivotWorld.y / size, 0f);
        var moved = pivot + new Vector3(drift.x / (size * aspect), drift.y / size, 0f);
        // x에 aspect를 곱해 정사각 좌표로 돌린다 (안 그러면 돌릴 때 찌그러짐). z는 0 = 깊이와 상관없이 다 카드 위
        var flat = Matrix4x4.Scale(new Vector3(aspect, 1f, 0f));
        var turn = Matrix4x4.Rotate(Quaternion.Euler(-angle.x, angle.y, angle.z));
        // 가상 렌즈: 가까워진 쪽(카드 z+)일수록 w가 작아져 커 보인다. x는 aspect로 되돌린다
        var lens = Matrix4x4.identity;
        lens.m00 = 1f / aspect;
        lens.m22 = 0f;
        lens.m32 = -1f / d;
        // 마지막 Translate는 w배로 더해져서(동차 좌표) 원근으로 나눈 뒤에도 축이 정확히 제자리로 간다
        Matrix4x4 h = Matrix4x4.Translate(moved) * lens * turn * flat * Matrix4x4.Translate(-pivot);
        // 깊이(z)는 카드와 따로 원래 값의 0.4배로 넘긴다. w가 1보다 작아지는 곳에서도 잘리지 않게 (앞뒤 순서는 그대로)
        // 그래서 정면 각도도 화면(x, y)만 원래와 같고 행렬 자체는 단위 행렬이 아니다. 화면에 보이는 차이는 없음
        h.m22 = 0.4f;
        return h;
    }

    void RestoreCamera()
    {
        camOn = false;
        match.HideLaneFlash = false;
        if (cam == null)
            return;
        cam.ResetProjectionMatrix();
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
        BuildFlashTimes();
        BuildCamKeys();
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
