using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Standards;
using UnityEditor;
using UnityEngine;

// Tools > MIDI to Stage (또는 .mid 우클릭 > MIDI > Export Stage JSON)
// 채널마다 A/B/C/제외 를 골라 Assets/Resources/<midi이름>.json 생성 (NoteManager가 읽는 noteList 포맷)
public class MidiToStage : EditorWindow
{
    [Serializable]
    private class StageNote
    {
        public float interval; // 이전 노트와의 간격 (박자 단위, Match.BPM 기준)
        public int input;      // 0=A, 1=B, 2=C
    }

    [Serializable]
    private class Stage
    {
        public float bpm;
        public float gridOffset; // 노트 에디터 그리드 시작 시각(초). 게임은 안 씀
        public List<StageNote> noteList = new();
    }

    private static readonly string[] LaneNames = { "제외", "A", "B", "C" };

    private DefaultAsset midiAsset;
    private MidiFile midi;
    private (int channel, int count, string instrument)[] channels = Array.Empty<(int, int, string)>();
    private readonly int[] lanes = new int[16]; // 채널(0~15)별 선택: -1=제외, 0=A, 1=B, 2=C

    // 같은 레인 노트 사이 최소 간격(박). 촘촘하면 올리고(1 = 한 박에 하나), 성기면 내린다.
    private float minGapBeats = 0.25f;

    // MIDI 앞부분에 음원에 없는 카운트인/빈 마디가 있으면 그만큼 잘라낸다 (4분음표 박 수, MIDI 템포 기준).
    private int trimBeats;

    [MenuItem("Tools/MIDI to Stage")]
    private static void Open() => GetWindow<MidiToStage>("MIDI to Stage");

    [MenuItem("Assets/MIDI/Export Stage JSON", true)]
    private static bool ValidateFromAsset() => IsMidi(Selection.activeObject);

    [MenuItem("Assets/MIDI/Export Stage JSON")]
    private static void OpenFromAsset()
    {
        var window = GetWindow<MidiToStage>("MIDI to Stage");
        window.Load((DefaultAsset)Selection.activeObject);
    }

    private static bool IsMidi(UnityEngine.Object obj) =>
        obj is DefaultAsset && AssetDatabase.GetAssetPath(obj).EndsWith(".mid", StringComparison.OrdinalIgnoreCase);

    private void Load(DefaultAsset asset)
    {
        midiAsset = asset;
        midi = null;
        channels = Array.Empty<(int, int, string)>();
        if (!IsMidi(asset))
            return;

        midi = MidiFile.Read(AssetDatabase.GetAssetPath(asset));
        channels = Channels(midi);

        // 기본값: 노트 많은 드럼 외 채널 3개를 A/B/C에
        Array.Fill(lanes, -1);
        var top = channels.Where(c => c.channel != 9).OrderByDescending(c => c.count).Take(3).ToArray();
        for (int i = 0; i < top.Length; i++)
            lanes[top[i].channel] = i;
    }

    // 노트가 있는 채널(0~15)마다 노트 수와 악기 이름
    internal static (int channel, int count, string instrument)[] Channels(MidiFile midi)
    {
        var programs = midi.GetTimedEvents().Select(e => e.Event).OfType<ProgramChangeEvent>()
                           .GroupBy(e => (int)e.Channel)
                           .ToDictionary(g => g.Key, g => ((GeneralMidiProgram)(byte)g.First().ProgramNumber).ToString());

        return midi.GetNotes().GroupBy(n => (int)n.Channel).OrderBy(g => g.Key)
                   .Select(g => (g.Key, g.Count(),
                                 g.Key == 9 ? "Drums" : programs.TryGetValue(g.Key, out var p) ? p : "AcousticGrandPiano"))
                   .ToArray();
    }

    private void OnGUI()
    {
        var picked = (DefaultAsset)EditorGUILayout.ObjectField("MIDI 파일", midiAsset, typeof(DefaultAsset), false);
        if (picked != midiAsset)
            Load(picked);

        if (midi == null)
        {
            EditorGUILayout.HelpBox(".mid 파일을 넣으세요.", MessageType.Info);
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("채널별 레인", EditorStyles.boldLabel);
        foreach (var (channel, count, instrument) in channels)
        {
            lanes[channel] = EditorGUILayout.Popup(
                $"ch{channel + 1}  {instrument}  ({count}개)", lanes[channel] + 1, LaneNames) - 1;
        }

        EditorGUILayout.Space();
        minGapBeats = EditorGUILayout.FloatField("레인당 최소 간격(박)", Mathf.Max(0f, minGapBeats));
        trimBeats = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("앞에서 자를 박 수",
            "노트가 음악보다 N박 늦게 나오면 N. 이 지점이 음악 0초가 된다."), trimBeats));

        EditorGUILayout.Space();
        if (GUILayout.Button("Export JSON", GUILayout.Height(30)))
        {
            Convert(midi, AssetDatabase.GetAssetPath(midiAsset), lanes, minGapBeats, trimBeats);
            AssetDatabase.Refresh();
        }
    }

    // 곡에서 가장 오래(초 기준) 유지되는 템포와 그 구간이 시작하는 시각(초). 시작 템포는 인트로에만 쓰이는 MIDI가 있음
    // (예: Counting Stars는 앞 48박만 107.6, 나머지는 122). 템포 변화는 초 단위 변환으로 반영됨.
    internal static (double bpm, double startSeconds) MainTempo(MidiFile midi)
    {
        var tempoMap = midi.GetTempoMap();
        long end = midi.GetNotes().Select(n => n.EndTime).DefaultIfEmpty(0).Max();
        var times = tempoMap.GetTempoChanges().Select(c => c.Time).Where(t => t > 0 && t < end)
                            .Prepend(0L).Append(end).ToList();
        double Seconds(long t) => TimeConverter.ConvertTo<MetricTimeSpan>(t, tempoMap).TotalSeconds;
        var main = Enumerable.Range(0, times.Count - 1)
            .GroupBy(i => Math.Round(tempoMap.GetTempoAtTime(new MidiTimeSpan(times[i])).BeatsPerMinute, 2))
            .OrderByDescending(g => g.Sum(i => Seconds(times[i + 1]) - Seconds(times[i])))
            .First();
        return (main.Key, Seconds(times[main.Min()]));
    }

    private static void Convert(MidiFile midi, string midiPath, int[] lanes, double minGapBeats, int trimBeats)
    {
        var tempoMap = midi.GetTempoMap();
        var (bpm, mainStart) = MainTempo(midi);

        double trimSeconds = TimeConverter.ConvertTo<MetricTimeSpan>(new MusicalTimeSpan(trimBeats, 4), tempoMap).TotalSeconds;

        // 노트 에디터 그리드 시작점: 주 템포 구간 시작을 마디(4박) 단위로 0초 쪽으로 당긴 시각
        double barSeconds = 4 * 60.0 / bpm;
        double gridOffset = ((mainStart - trimSeconds) % barSeconds + barSeconds) % barSeconds;

        // 선택한 채널만, 레인마다 minGapBeats보다 가까운 노트(화음 포함)는 버림
        var lastBeat = new[] { double.MinValue, double.MinValue, double.MinValue };
        var stage = new Stage { bpm = (float)Math.Round(bpm, 4), gridOffset = (float)Math.Round(gridOffset, 4) };
        double prevBeat = 0;

        foreach (var n in midi.GetNotes().OrderBy(n => n.Time))
        {
            int lane = lanes[n.Channel];
            if (lane < 0)
                continue;

            double seconds = n.TimeAs<MetricTimeSpan>(tempoMap).TotalSeconds - trimSeconds;
            if (seconds < 0)
                continue;

            double beat = seconds * bpm / 60.0;
            if (beat - lastBeat[lane] < minGapBeats - 1e-6) // 오차로 정확히 minGap인 노트가 버려지지 않게
                continue;

            lastBeat[lane] = beat;
            stage.noteList.Add(new StageNote
            {
                interval = (float)Math.Round(beat - prevBeat, 4),
                input = lane
            });
            prevBeat = beat;
        }

        if (stage.noteList.Count == 0)
        {
            Debug.LogError($"선택한 채널에 노트 없음: {midiPath}");
            return;
        }

        Directory.CreateDirectory("Assets/Resources");
        string outPath = $"Assets/Resources/{Path.GetFileNameWithoutExtension(midiPath)}.json";
        File.WriteAllText(outPath, JsonUtility.ToJson(stage, true));
        Debug.Log($"{outPath} 생성: 노트 {stage.noteList.Count}개 / BPM {bpm:0.##}");
    }
}
