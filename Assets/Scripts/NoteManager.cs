using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public enum NoteInputType
{
    A,
    B,
    C,
    D // 펌프 (F키). 펌프 모드에서만 쓰고 키보드 모드에서는 불러올 때 뺀다
}

[Serializable]
public struct Note
{
    public float interval;
    public NoteInputType input;
    // 비어 있지 않으면 이 노트의 박에 DialogueManager.Send(message)를 보낸다 (클라이맥스 이펙트, 보컬/드럼 예고 등)
    // 미리 알리고 싶으면 그만큼 앞 노트에 단다
    public string message;
    public static float NotesLength;
    public float noteTime;

    public Note(float interval, NoteInputType input)
    {
        this.interval = interval;
        this.input = input;
        this.message = null;
        this.noteTime = NotesLength;
        NotesLength += interval;
    }
}

public class NoteManager : MonoBehaviour
{
    [Serializable]
    private class JsonWrapper
    {
        public float bpm;
        public List<Note> noteList;
    }

    public struct NoteDataContainer
    {
        public readonly float bpm; // 0 = JSON에 BPM 없음
        public readonly List<Note> notes;

        public NoteDataContainer(List<Note> list, float bpm = 0f)
        {
            notes = list ?? new List<Note>();
            this.bpm = bpm;
        }
    }

    public static NoteManager Instance { get; private set; }

    public NoteDataContainer CurrentData { get; private set; }

    // 한 레인 노트를 뺀 새 목록 (키보드 모드에서 펌프 노트 빼기). interval은 앞 노트와의 간격이라
    // 그냥 지우면 뒤가 전부 당겨진다 → 뺀 노트의 간격을 다음 노트에 더해서 박 위치를 그대로 둔다
    public static List<Note> WithoutLane(List<Note> list, NoteInputType lane)
    {
        var result = new List<Note>(list.Count);
        float carry = 0f;
        foreach (Note n in list)
        {
            if (n.input == lane)
            {
                carry += n.interval;
                continue;
            }
            Note kept = n;
            kept.interval += carry;
            carry = 0f;
            result.Add(kept);
        }
        return result;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this; LoadNoteData(SongSelect.SelectedNote);
    }

    public void LoadNoteData(string fileName)
    {
        TextAsset jsonFile = Resources.Load<TextAsset>(fileName);

        if (jsonFile == null)
        {
            Debug.LogError(
                $"File not found: {fileName}.json",
                this
            );

            CurrentData = new NoteDataContainer(null);
            return;
        }

        try
        {
            JsonWrapper wrapper =
                JsonUtility.FromJson<JsonWrapper>(jsonFile.text);

            if (wrapper == null || wrapper.noteList == null)
            {
                Debug.LogError(
                    $"'{fileName}.json' is invalid. noteList could not be found.",
                    this
                );

                CurrentData = new NoteDataContainer(null);
                return;
            }

            CurrentData = new NoteDataContainer(wrapper.noteList, wrapper.bpm);

            Debug.Log(
                $"try to load: {fileName}.json / " +
                $"Note Count: {CurrentData.notes.Count} / BPM: {CurrentData.bpm}"
            );
        }
        catch (Exception e)
        {
            Debug.LogError(
                $"'{fileName}.json' �Ľ� �� ������ �߻��߽��ϴ�.\n{e}",
                this
            );

            CurrentData = new NoteDataContainer(null);
        }
    }
}
