using System.Collections;
using Unity.Mathematics;
using UnityEngine;

public class BadApple : MonoBehaviour
{
    //CPU 고문 배드 애플. 세이더 쓰면 GPU라서 과장해서 몇천배 빠르긴 하는데, 역시 CPU보단 릴레이 인것 처럼 GPU보단 CPU가 맞다.
    //보니까 처리 속도가 무려 0.01ms가 나오더라, 0.2% 차지. ㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋ 역시 최적화는 즐겁다
    const int W = 64, H = 48;
    const int FrameBytes = W * H / 8;
    bool[] bitmap = new bool[W * H];

    byte[] data;
    int frames;
    public Sprite RefTexture;
    public float PixelSpace = 0.2f;
    public Vector3 wanted_Size = new Vector3(0.2f,0.2f,0);
    SpriteRenderer[] cells;

    //match에서 dsp 가져와서 음악이랑 싱크하는거임. 이건 걍 복붙
    Match match;
    public bool matchToPlayerTree;
    SpriteRenderer playerTree;

    //bad apple정도면 디렉터가 필요하지..
    void Awake()
    {
        if (SongSelect.SelectedSong != BadAppleDirector.SongId)
            enabled = false;
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        data = Resources.Load<TextAsset>("badapple").bytes;
        frames = data.Length / FrameBytes;
        Debug.Log("Bad Apple Ready.");

        playerTree = GameObject.Find("Player tree").GetComponent<SpriteRenderer>(); //아니 내가 원래 Gameobject.Find를 싫어하는데 왜캐 
        //ㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅁㅇㅁㄶㅇㄻㄴㅇㄹㄴ이ㅗㄹㅗㅗㅗㅗㅗㅗㅗㅗㅗㅗㅗㅗㅗㅗ

        cells = new SpriteRenderer[W * H]; //일종의 캐시다
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var go = new GameObject($"px {x},{y}: {y * W + x}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(x * PixelSpace, -y * PixelSpace, 10f);
                Vector2 size = RefTexture.bounds.size;// 스케일 1일 때 월드 크기
                go.transform.localScale = new Vector3(wanted_Size.x / size.x, wanted_Size.y / size.y, 1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = RefTexture;
                sr.enabled = false;
                sr.sortingOrder = -100;
                cells[y * W + x] = sr;
            }
        }
    }

    public void Init() //이니셜라이즈 뭔가 개간지 나지 않음?
    {
        match = FindAnyObjectByType<Match>();
    }

    int lastF = -1; //지난번에 그린 영상 프레임, 그러니까 같은 프레임 그렸으면 리턴시키는 거임. 부작용으로 나무 텍스쳐와 싱크가 33ms 정도 느려질 수 있음(??)

    void Update()
    {
        if (match == null || !match.Started) return; // Restart 직후 음악 예약 전: 시계가 아직 지난 판 값
        double t = match.SongTime;
        if (t < 0) return;//아직 영업 안해요
         int f = (int)(t * 30.0);
        if (f == lastF) return; //같은 영상 프레임이면 다시 그릴 필요가 업지용
        lastF = f;
        if (f < frames) ShowFrame(f);
    }

    //python 코드 유니티로 이식해온거임 대충 1차원 텍스트를 2차원 비트맵으로 바꾸는 코드

    public static Vector3 size; //제발되라 //O(1)을 재물로 O(N^2)을 막겠다는 아이디어 22222222222222222222
    //그러니까, SpriteRender에서 참조해오면 단순 계산으로 연산 2번하는데, 이러면 1번밖에 안한다 그런거임, ㅇㅋ?
    Sprite oldSprite;
    void ShowFrame(int f)
    {
        int start = f * FrameBytes;

        Sprite now = playerTree.sprite;
        bool textureUnChanged = now == oldSprite;

        if(!textureUnChanged) //순수한 싱글톤(??????????) 최적화 지랄, 그러니까 static이면 항상 메모리에 존재하니까 참조하는데 드는 비용도 줄어든다 그런거임
        { 
            size = RefTexture.bounds.size; 
            size = new Vector3(wanted_Size.x / size.x, wanted_Size.y / size.y, 1f);
        }

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x; //simple is better than complex -PEP 20-
                int bit = (data[start + i / 8] >> (7 - i & 7)) & 1;// 젠장 또 비트시프트야, 나는 찬양할 수 밖에 없다 그들을 프로그래밍의 미래가 어둡다

                bool on = bit != 0; //젠장 또 불리언이야, 나는 찬양해야해, 어레이 등호랑 숫자 등호는 같은 O(1)이라고? 어쩌라고 씨발 나는 cpu 아키텍쳐를 숭배한다

                if (bitmap[i] != on) //젠장 이번엔 어레이야, CPU에게 설렁탕은 개뿔 그냥 주스 먹이고 있는 꼴이잖아, 얼마나 작업을 더 쉽게 만들 생각인거야
                {
                    //달라졌을 때만 유니티 네이티브에 접근하는 코드인 거임, 그러니까 C# 메네지드로 지랄하는 게 Unity 네이티브 쓰는 것 보다 낫다는거임
                    cells[i].enabled = on;
                    bitmap[i] = on;
                }  
                if(!textureUnChanged)//스프라이트가 바뀌었을 때만 네이티브를 징벌하는 그야말로 솔로몬 성 베드로 페이커 lets go
                {
                    cells[i].sprite = now;
                    cells[i].gameObject.transform.localScale = size;
                }
            }
        }
        oldSprite = now;// O(1)을 재물로 O(N^2)을 막는다는 아이디어
    }
}