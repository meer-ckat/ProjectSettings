using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class Setting : MonoBehaviour
{
    public Match match;
    public AudioClip Window;
    public AudioClip Button;
    public AudioClip Enter;

    public static bool CameraShakeOn = false;
    public static float defaultShakeStrength = 0.08f;
    public GameObject shakeButton;
    public List<GameObject> turnoffableshittyass;
    public Texture shakeOn;
    public Texture shakeOff;
    public bool isSettingsOpened;
    private TextMeshProUGUI shake_Text;
    private RawImage shake_image;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    AudioSource source;
    void Start()
    {
        source = GetComponent<AudioSource>();
        shake_image = shakeButton.transform.Find("Image").GetComponent<RawImage>();
        shake_Text = shakeButton.transform.Find("Text").GetComponent<TextMeshProUGUI>();
        UpdateShake();
    }

    public void SettingButtonClicked()
    {
        source.PlayOneShot(Window);
        isSettingsOpened = !isSettingsOpened;
        match.SetPaused(isSettingsOpened);
    }

    private float start;

    public void MusicVolSliderChanged(float per)
    {
        if(Time.unscaledTime - 0.25f > start)
        {
            start = Time.unscaledTime;
            source.PlayOneShot(Enter);
        }
        //어쩔 수 없는게, 중앙집중화 하면 관리 편하긴 한데 이거 어차피 한 번 만들고 버리는거잖아
        match.GetComponent<AudioSource>().volume = per;
    }

    public void Calib()
    {
        source.PlayOneShot(Window);
        GameObject.FindFirstObjectByType<LatencyCalibrator>().Begin();
        SettingButtonClicked();
    }

    private void UpdateShake()
    {
        source.PlayOneShot(Button);
        if (CameraShakeOn)
        {
            match.shakeStrength = defaultShakeStrength;
            
            shake_Text.text = "Camera Shake On";
            
            shake_image.texture = shakeOn;
        }
        else
        {
            match.shakeStrength = 0f;

            shake_Text.text = "Camera Shake Off";

            shake_image.texture = shakeOff;
        }
    }

    public void SetShake() {
        CameraShakeOn = !CameraShakeOn;
        UpdateShake() ;
    }

    // Update is called once per frame
    void Update()
    {
        // 스페이스(Jump) = 설정 창. 예전엔 이 오브젝트에 PlayerInput을 따로 뒀는데, Match 것과 키보드를 서로 가져가려 해서
        // 진 쪽 user가 무효가 되고 ApplyInputScheme이 "Invalid user"로 터졌다. 그래서 Match의 PlayerInput 하나만 쓴다
        // 펌프 모드면 그 PlayerInput이 발판만 잡아서 Jump가 안 들어온다 → 키보드가 안 붙어 있을 때만 스페이스를 직접 본다
        PlayerInput pi = match.GetComponent<PlayerInput>();
        Keyboard kb = Keyboard.current;
        bool keyboardPaired = kb != null && pi.devices.Contains(kb);
        if (pi.actions["Jump"].WasPressedThisFrame() || (!keyboardPaired && kb != null && kb.spaceKey.wasPressedThisFrame))
            SettingButtonClicked();

        shakeButton.SetActive(isSettingsOpened);
        //엄청난 기술부채다..
        //그러니까 gameobject 하나 부모로 두고 그것만 setactive하면 되는걸 이새기는 참
        //누군가 고쳐주겠지 ㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋ
        turnoffableshittyass[0].SetActive(isSettingsOpened);
        turnoffableshittyass[1].SetActive(isSettingsOpened);
        turnoffableshittyass[2].SetActive(isSettingsOpened);
        turnoffableshittyass[3].SetActive(isSettingsOpened);

        UpdatePadNav();
    }

    // 키보드·펌프 발판으로 UGUI 버튼 고르기. 설정 창이나 결과 창이 떠 있을 때만 (게임 중엔 A/S/D가 레인이라)
    // a = 이전, c = 다음, b = 누르기 (곡 선택 화면과 같음). Match의 PlayerInput을 써서 펌프 모드면 발판, 아니면 A/S/D
    // 창이 뜨면 첫 버튼을 골라 둔다. 안 골라 두면 EventSystem 기본 입력(화살표·Enter)도 안 먹는다
    // ponytail: 볼륨 슬라이더는 고르기만 되고 발판으로 값은 못 바꿈 (키보드 화살표 좌우로만)
    void UpdatePadNav()
    {
        Transform root = isSettingsOpened ? shakeButton.transform.parent
            : match.Result.gameObject.activeInHierarchy ? match.Result : null;
        EventSystem es = EventSystem.current;
        if (root == null || es == null)
            return;

        // 화면 위→아래, 왼→오른 순서로 돈다
        List<Selectable> items = root.GetComponentsInChildren<Selectable>()
            .Where(s => s.interactable)
            .OrderByDescending(s => s.transform.position.y).ThenBy(s => s.transform.position.x)
            .ToList();
        if (items.Count == 0)
            return;

        int i = items.FindIndex(s => s.gameObject == es.currentSelectedGameObject);
        if (i < 0)
        {
            i = 0;
            es.SetSelectedGameObject(items[0].gameObject);
        }

        InputActionAsset actions = match.GetComponent<PlayerInput>().actions;
        int step = (actions["c"].WasPressedThisFrame() ? 1 : 0) - (actions["a"].WasPressedThisFrame() ? 1 : 0);
        if (step != 0)
            es.SetSelectedGameObject(items[(i + step + items.Count) % items.Count].gameObject);
        else if (actions["b"].WasPressedThisFrame())
            ExecuteEvents.Execute(items[i].gameObject, new BaseEventData(es), ExecuteEvents.submitHandler);
    }
}
