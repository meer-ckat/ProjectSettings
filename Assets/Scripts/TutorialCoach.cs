using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 튜토리얼: 음악 없이 메트로놈만 깔고 박자 쪼개기를 단계별로 연습시킨다
// 한 단계에서 Good이나 Miss가 하나라도 나오면 그 단계를 처음부터 다시 (Perfect/Great만 통과)
// 차트와 메트로놈은 코드로 만들어 Match.Play로 넘긴다. 대사는 DialogueManager.Say
// 씬에 Match·DialogueManager·LatencyCalibrator가 있어야 한다
public class TutorialCoach : MonoBehaviour
{
    [Serializable]
    public class Stage
    {
        public string tip;
        public float bpm = 60f;
        [Tooltip("노트 사이 간격(박). 순서대로 쓰고 repeat만큼 반복. 마지막 간격은 안 쓰임")]
        public float[] steps = { 1f };
        public int repeat = 8;
    }

    const string Speaker = "박치";
    const int CountIn = 4;        // 첫 노트 전에 메트로놈만 나오는 박 수
    const   float NextDelay = 2f;   // 대사 띄우고 다음(또는 다시) 시작까지

    public Stage[] stages =
    {
        new() { tip = "1박: 삑 소리마다 한 번씩 쳐.", steps = new[] { 1f } },
        new() { tip = "1/2박: 삑과 삑 사이에 한 번 더.", steps = new[] { 1 / 2f } },
        new() { tip = "1/4박: 한 박을 넷으로 쪼개.", steps = new[] { 1 / 4f } },
        new() { tip = "1/8박: 한 박에 여덟 번!", steps = new[] { 1 / 8f } },
        new()
        {
            tip = "변박: 박마다 1, 2, 3, 4, 3, 2, 1개로 쪼갠다.",
            // 한 박을 n개로 = 1/n 간격을 n번
            steps = new[] { 1f, 1 / 2f, 1 / 2f, 1 / 3f, 1 / 3f, 1 / 3f, 1 / 4f, 1 / 4f, 1 / 4f, 1 / 4f, 1 / 3f, 1 / 3f, 1 / 3f, 1 / 2f, 1 / 2f, 1f },
            repeat = 1,
        },
    };

    Match match;
    DialogueManager dialogue;
    AudioClip[] clicks; // 단계별 메트로놈 (한 번 만들고 재사용)
    int stage;
    bool waiting;       // 다음/다시 시작 대기 중. 그 사이 판정은 무시

    IEnumerator Start()
    {
        match = FindAnyObjectByType<Match>();
        dialogue = FindAnyObjectByType<DialogueManager>();
        //펌프는 발로 밟아서 1박·1/2박 단계만 (1/3박 이하가 섞인 단계는 뺌)
        if (SongSelect.PumpMode)
            stages = Array.FindAll(stages, s => Array.TrueForAll(s.steps, x => x >= 0.5f));
        clicks = new AudioClip[stages.Length];

        yield return null; // Match.Start가 끝난 뒤에 캘리브레이션 (캘리브레이터가 match.Restart를 부름)
        dialogue.Say(Speaker, "어서 와! 먼저 보정부터 할거야. 박자에 맞춰 아무 키나 눌러.");
        DialogueManager.Send("Calibrate");
    }

    // Message는 static이라 씬이 바뀌어도 남는다 → 꼭 떼야 함
    void OnEnable() => DialogueManager.Message += OnMessage;
    void OnDisable() => DialogueManager.Message -= OnMessage;

    // Match.Resolve 안에서 불리므로 여기서 바로 Restart하면 안 됨 → 코루틴으로 미룸
    void OnMessage(string e)
    {
        // 캘리브레이션 중에 대기 코루틴이 끝나면 match.Play로 메트로놈이 깔린다 → 대기를 끄고 끝날 때까지 판정 무시
        if (e == "CalibrationStart")
        {
            StopAllCoroutines();
            waiting = true;
        }
        // 설정 창에서 다시 캘리브레이션해도 여기로 옴 → 하던 단계부터
        else if (e == "CalibrationDone")
            Next(stage, stages[stage].tip);
        else if (waiting)
            return;
        else if (e == "Good" || e == "Miss")
            Next(stage, $"{e}! Great 이상만 통과야. 다시.");
        else if (e == "Finish" && stage + 1 < stages.Length)
            Next(stage + 1, "좋아, 통과! 다음 단계.\n" + stages[stage + 1].tip);
        else if (e == "Finish")
            dialogue.Say(Speaker, "튜토리얼 끝! 이제 박자는 몸이 기억할 거야.");
    }

    void Next(int next, string line)
    {
        StopAllCoroutines(); // 대기 중에 캘리브레이션이 끼어들어도 하나만
        StartCoroutine(Go(next, line));
    }

    IEnumerator Go(int next, string line)
    {
        waiting = true;
        dialogue.Say(Speaker, line);

        // 틀린 즉시 멈춤: 노트·메트로놈·예약된 타건음을 치우고 곡 시작 코루틴도 끈다 (LatencyCalibrator와 같은 방식)
        yield return null;
        match.Restart();
        match.StopAllCoroutines();

        yield return new WaitForSeconds(NextDelay);

        stage = next;
        waiting = false;
        Stage s = stages[stage];

        int lanes = SongSelect.PumpMode ? 4 : 3;
        var notes = new List<Note>();
        float beats = CountIn; // 다음 노트까지 간격 (첫 노트는 카운트인 뒤)
        float last = 0f;       // 마지막 노트 박 위치
        for (int i = 0; i < s.steps.Length * s.repeat; i++)
        {
            notes.Add(new Note { interval = beats, input = (NoteInputType)(i % lanes) }); // 레인 A→B→C(→D) 순환
            last += beats;
            beats = s.steps[i % s.steps.Length];
        }

        if (clicks[stage] == null)
            clicks[stage] = Metronome(s.bpm, Mathf.CeilToInt(last) + 2);

        match.showResult = stage == stages.Length - 1;
        match.Play(notes, s.bpm, clicks[stage]);
    }

    // 박마다 30ms 삑, 마디(4박) 첫 박은 높게. 음악 대신 Match.Music으로 재생돼서 노트와 같은 시계로 맞는다
    static AudioClip Metronome(float bpm, int beats)
    {
        int rate = AudioSettings.outputSampleRate;
        float beatLen = 60f / bpm * rate;
        var data = new float[Mathf.RoundToInt(beats * beatLen)];

        for (int b = 0; b < beats; b++)
        {
            int start = Mathf.RoundToInt(b * beatLen); // 박마다 반올림해서 오차가 안 쌓임
            float freq = b % 4 == 0 ? 1500f : 1000f;
            for (int i = 0; i < rate * 0.03f; i++)
                data[start + i] = Mathf.Sin(2 * Mathf.PI * freq * i / rate) * 0.5f;
        }

        var clip = AudioClip.Create("Metronome", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
