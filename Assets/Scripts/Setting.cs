using System.Collections.Generic;
using TMPro;
using UnityEngine;
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
        shakeButton.SetActive(isSettingsOpened);
        //엄청난 기술부채다..
        //그러니까 gameobject 하나 부모로 두고 그것만 setactive하면 되는걸 이새기는 참
        //누군가 고쳐주겠지 ㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋ
        turnoffableshittyass[0].SetActive(isSettingsOpened);
        turnoffableshittyass[1].SetActive(isSettingsOpened);
        turnoffableshittyass[2].SetActive(isSettingsOpened);
        turnoffableshittyass[3].SetActive(isSettingsOpened);
    }
}
