using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

[RequireComponent(typeof(SkillEffect))]
public class EffectScript : MonoBehaviour
{
    public GameObject slide;
    public GameObject tree;
    public GameObject leafEffectPrefab;
    public GameObject glowPrefeb;
    public GameObject magicPrefab;
    public Sprite fallback;
    public List<Sprite> skillTextures;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void Progression(int Size) {
        print("Effect");
        RectTransform gage = slide.GetComponent<RectTransform>();
        GameObject glow = Instantiate(glowPrefeb);
        // Overlay 캔버스라 코너가 곧 화면 픽셀. CanvasScaler 설정과 무관하게 게이지 오른쪽 끝을 잡는다.
        var corners = new Vector3[4];
        gage.GetWorldCorners(corners);
        Vector3 tip = (corners[2] + corners[3]) / 2f;
        var cam = Camera.main.ScreenToWorldPoint(new Vector3(tip.x, tip.y, +1f));
        glow.transform.position = cam;
        float Scale = 0.4f + Size * 0.1f;
        glow.transform.localScale = new Vector3(Scale, Scale, Scale);
        Destroy(glow, 3);
    }

    public void Level1()
    {
        print("Yo Stage1");
        GameObject leaf = Instantiate(leafEffectPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
    }

    public void Level2()
    {
        print("Yo Stage2");
        GameObject leaf = Instantiate(leafEffectPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
    }

    public void Level3()
    {
        print("Yo Stage3");
        StartCoroutine(SpawnMagic());
        //super mega ukulele
        //super mega ukulele do you know psy? do you know kimchi? 유노 싸이? 안녕하세요, 저는 오사완입니다.
    }

    IEnumerator SpawnMagic()
    {
        GameObject magic = Instantiate(magicPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        Destroy(magic, 2);
        magic.transform.position = tree.transform.position;
        yield return new WaitForSeconds(1f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 2), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        GameObject leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
        yield return new WaitForSeconds(1f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 1), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position + new Vector3(-1, 1, 0);
        yield return new WaitForSeconds(0.5f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, -1), skillTextures[Random.Range(0, skillTextures.Count)]??fallback);
        leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position + new Vector3(1, 2, 0);
    }
}
