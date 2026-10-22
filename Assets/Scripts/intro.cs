using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

public class intro : MonoBehaviour
{
    Image image;
    void Start()
    {
        image = GetComponent<Image>();

        Sequence seq = DOTween.Sequence();

        seq.Append(image.transform.DOPunchScale(new Vector3(0.1f,0.1f), 1f));
        seq.Append(image.transform.DOPunchScale(new Vector3(0.5f,0.5f), 1f));
        seq.Join(image.transform.DOMoveY(Screen.height * 10, 3f));
    }
}
