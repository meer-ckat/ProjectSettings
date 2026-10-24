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

    // 레벨 순서대로 고정: Level1=0번, Level2=1번, Level3=2번 (Level3 뒤 연타는 0→1→2 전원 등장). 순서는 인스펙터 리스트 순서
    Sprite Skill(int i) => i < skillTextures.Count && skillTextures[i] != null ? skillTextures[i] : fallback;

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

    public bool forceOff;

    public void Level1()
    {
        if(forceOff) return;
        print("Yo Stage1");
        GameObject leaf = Instantiate(leafEffectPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), Skill(0));
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
    }

    public void Level2()
    {
        if(forceOff) return;
        print("Yo Stage2");
        GameObject leaf = Instantiate(leafEffectPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), Skill(1));
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
    }

    public void Level3()
    {
        if(forceOff) return;
        print("Yo Stage3");
        StartCoroutine(SpawnMagic());
        //super mega ukulele
        //super mega ukulele do you know psy? do you know kimchi? 유노 싸이? 안녕하세요, 저는 오사완입니다.
    }

    IEnumerator SpawnMagic()
    {
        if(forceOff) yield break;
        GameObject magic = Instantiate(magicPrefab);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 0), Skill(2));
        Destroy(magic, 2);
        magic.transform.position = tree.transform.position;
        yield return new WaitForSeconds(1f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 2), Skill(0));
        GameObject leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position;
        yield return new WaitForSeconds(1f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, 1), Skill(1));
        leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position + new Vector3(-1, 1, 0);
        yield return new WaitForSeconds(0.5f);
        GetComponent<SkillEffect>().Launch(new Vector2(10,-3.5f), new Vector2(7.29f, -1), Skill(2));
        leaf = Instantiate(leafEffectPrefab);
        Destroy(leaf, 5);
        leaf.transform.position = tree.transform.position + new Vector3(1, 2, 0);
    }
}
