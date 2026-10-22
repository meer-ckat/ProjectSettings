
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public class BackgroundGradient : MonoBehaviour
{
    [Header("Texture")]
    [SerializeField, Min(16)] private int width = 512;
    [SerializeField, Min(16)] private int height = 340;

    [Header("Perlin Distortion")]
    [SerializeField, Range(0f, 0.2f)]
    private float warpStrength = 0.045f;

    [SerializeField, Range(0.1f, 10f)]
    private float noiseScale = 2.3f;

    // 5 x 4 색상 격자
    // 아래쪽 행부터 위쪽 행 순서
    [Header("Color Palette")]
    [SerializeField] private Color[] palette =
    {
        // Bottom
        new Color32(193, 165, 180, 255),
        new Color32(229, 129, 141, 255),
        new Color32(189, 131, 146, 255),
        new Color32(115, 148, 225, 255),
        new Color32(117, 160, 254, 255),

        // Lower Middle
        new Color32(216, 152, 166, 255),
        new Color32(244, 120, 128, 255),
        new Color32(210, 166, 189, 255),
        new Color32(188, 165, 245, 255),
        new Color32(221, 167, 253, 255),

        // Upper Middle
        new Color32(195, 211, 226, 255),
        new Color32(218, 175, 195, 255),
        new Color32(186, 151, 215, 255),
        new Color32(202, 157, 248, 255),
        new Color32(240, 166, 253, 255),

        // Top
        new Color32(245, 244, 250, 255),
        new Color32(225, 223, 245, 255),
        new Color32(185, 177, 250, 255),
        new Color32(201, 172, 254, 255),
        new Color32(238, 163, 254, 255)
    };

    private const int Columns = 5;
    private const int Rows = 4;

    private Texture2D texture;
    private Sprite sprite;

    private void Start()
    {
        Generate();
    }

    [ContextMenu("Generate Gradient")]
    public void Generate()
    {
        if (palette == null || palette.Length != Columns * Rows)
        {
            Debug.LogError("Palette must contain exactly 20 colors.");
            return;
        }

        int w = Mathf.Max(16, width);
        int h = Mathf.Max(16, height);

        Texture2D newTexture = new Texture2D(
            w, h,
            TextureFormat.RGBA32,
            false
        );

        newTexture.filterMode = FilterMode.Bilinear;
        newTexture.wrapMode = TextureWrapMode.Clamp;

        Color32[] pixels = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / (w - 1);
                float v = (float)y / (h - 1);

                // Perlin Noise로 좌표를 부드럽게 왜곡
                float noiseX = Mathf.PerlinNoise(
                    u * noiseScale + 13.7f,
                    v * noiseScale + 42.1f
                ) * 2f - 1f;

                float noiseY = Mathf.PerlinNoise(
                    u * noiseScale + 87.3f,
                    v * noiseScale + 19.6f
                ) * 2f - 1f;

                float warpedU = Mathf.Clamp01(
                    u + noiseX * warpStrength
                );

                float warpedV = Mathf.Clamp01(
                    v + noiseY * warpStrength
                );

                pixels[y * w + x] = SampleColor(
                    warpedU, warpedV
                );
            }
        }

        newTexture.SetPixels32(pixels);
        newTexture.Apply(false, true);

        Sprite newSprite = Sprite.Create(
            newTexture,
            new Rect(0, 0, w, h),
            new Vector2(0.5f, 0.5f)
        );

        Image image = GetComponent<Image>();
        image.sprite = newSprite;
        image.color = Color.white;
        image.type = Image.Type.Simple;
        image.preserveAspect = false;
        image.raycastTarget = false;

        Release();

        texture = newTexture;
        sprite = newSprite;
    }

    private Color SampleColor(float u, float v)
    {
        float gx = Mathf.Clamp01(u) * (Columns - 1);
        float gy = Mathf.Clamp01(v) * (Rows - 1);

        int x0 = Mathf.Min(Mathf.FloorToInt(gx), Columns - 2);
        int y0 = Mathf.Min(Mathf.FloorToInt(gy), Rows - 2);

        float tx = gx - x0;
        float ty = gy - y0;

        // Smoothstep: 경계의 기울기를 부드럽게
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        Color bottomLeft = palette[y0 * Columns + x0];
        Color bottomRight = palette[y0 * Columns + x0 + 1];

        Color topLeft = palette[(y0 + 1) * Columns + x0];
        Color topRight = palette[(y0 + 1) * Columns + x0 + 1];

        Color bottom = Color.Lerp(
            bottomLeft, bottomRight, tx
        );

        Color top = Color.Lerp(
            topLeft, topRight, tx
        );

        return Color.Lerp(bottom, top, ty);
    }

    private void Release()
    {
        if (Application.isPlaying)
        {
            if (sprite != null) Destroy(sprite);
            if (texture != null) Destroy(texture);
        }
        else
        {
            if (sprite != null) DestroyImmediate(sprite);
            if (texture != null) DestroyImmediate(texture);
        }

        sprite = null;
        texture = null;
    }

    private void OnDestroy()
    {
        Release();
    }
}
