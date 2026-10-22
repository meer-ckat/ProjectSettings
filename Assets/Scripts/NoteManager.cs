using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public enum NoteInputType
{
    A,
    B,
    C
}

[Serializable]
public struct Note
{
    public float interval;
    public NoteInputType input;
    public static float NotesLength;
    public float noteTime;

    public Note(float interval, NoteInputType input)
    {
        this.interval = interval;
        this.input = input;
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

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this; LoadNoteData(SongSelect.Selected);
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
