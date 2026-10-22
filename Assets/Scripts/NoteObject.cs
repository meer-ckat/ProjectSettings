using UnityEngine;

public class NoteObject : MonoBehaviour
{
    public NoteInputType input;
    public float hitTime;

    private Match match;

    // �� ��Ʈ�� ���� ������ X ��ǥ
    private float laneX;

    public int index;

    public void Setup(
        NoteInputType input,
        float hitTime,
        Match match,
        float laneX,
        int index)
    {
        this.input = input;
        this.hitTime = hitTime;
        this.match = match;
        this.laneX = laneX;
        this.index = index;

        transform.position = new Vector3(
            laneX,
            match.SpawnPoint.position.y,
            transform.position.z
        );
    }

    void Update()
    {
        float remaining = hitTime - match.currentTime;

        float progress =
            1f - remaining / match.previewTime;

        // Unclamped: 판정선을 지나도 같은 속도로 계속 내려간다
        float y = Mathf.LerpUnclamped(
            match.SpawnPoint.position.y,
            match.HitPoint.position.y,
            progress
        );

        transform.position = new Vector3(
            laneX,
            y,
            transform.position.z
        );
    }

    public void Hit()
    {
        Destroy(gameObject);
    }
}
