using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class RhythmSpriteBounce : MonoBehaviour
{
    [Header("Rhythm")]
    [SerializeField] private float bpm = 120f;

    [Header("Bounce Size")]
    [Tooltip("X�� ��ĩ ũ��. ����/��� ����")]
    [SerializeField] private float bounceX = 0.1f;

    [Tooltip("Y�� ��ĩ ũ��. ����/��� ����")]
    [SerializeField] private float bounceY = -0.1f;

    [Range(0f, 1f)]
    [SerializeField] private float intensity = 1f;

    [Header("Animation")]
    [SerializeField] private bool playOnStart = true;

    private SpriteRenderer spriteRenderer;

    private Vector3 originalScale;
    private Vector3 originalPosition;

    public Sprite level1;
    public Sprite level2;
    public Sprite level3;
    public Sprite level4;

    // Sprite Pivot ���� Y ��ġ
    private float pivotOffsetY;

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();

        originalScale = transform.localScale;
        originalPosition = transform.localPosition;

        // Sprite�� Pivot�� �߽ɿ��� �󸶳� ������ �ִ��� ���
        pivotOffsetY = spriteRenderer.sprite.pivot.y / spriteRenderer.sprite.pixelsPerUnit
                       - spriteRenderer.sprite.bounds.min.y;
    }

    public void setSpriteLevel(int level)
    {
        switch (level)
        {
            case 0:
                spriteRenderer.sprite = level1;
                break;
            case 1:
                spriteRenderer.sprite = level2;
                break;
            case 2:
                spriteRenderer.sprite = level3;
                break;
            case 3:
                spriteRenderer.sprite = level4;
                break;
            default:
                Debug.LogWarning("Invalid sprite level: " + level);
                break;
        }
    }

    private void Start()
    {
        enabled = playOnStart;
    }

    private void Update()
    {
        if (bpm <= 0f)
            return;

        float beatLength = 60f / bpm;

        // 0 �� 1 �� 0
        float t = (Time.time % beatLength) / beatLength;
        float beat = Mathf.Sin(t * Mathf.PI) * intensity;

        // ���� Scale ���
        float scaleX = 1f + bounceX * beat;
        float scaleY = 1f + bounceY * beat;

        Vector3 newScale = new Vector3(
            originalScale.x * scaleX,
            originalScale.y * scaleY,
            originalScale.z
        );

        // Scale ����
        transform.localScale = newScale;

        // ------------------------------------------------
        // Position���� �ٴ� ��ġ ����
        // ------------------------------------------------

        // ���� Scale���� Pivot �� �ٴڱ����� �Ÿ�
        Vector3 newPosition = new Vector3(
            originalPosition.x,
            originalPosition.y + newScale.y,
            originalPosition.z
        );

        transform.localPosition = newPosition;
    }

    public void SetBounce(float x, float y)
    {
        bounceX = x;
        bounceY = y;
    }

    public void SetBounceX(float x)
    {
        bounceX = x;
    }

    public void SetBounceY(float y)
    {
        bounceY = y;
    }

    public void SetBPM(float newBPM)
    {
        bpm = newBPM;
    }

    public void ResetBounce()
    {
        transform.localScale = originalScale;
        transform.localPosition = originalPosition;
    }
}