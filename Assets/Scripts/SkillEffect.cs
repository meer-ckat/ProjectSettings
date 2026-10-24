using DG.Tweening;
using UnityEngine;

public class SkillEffect : MonoBehaviour
{
    [Tooltip("들어오는 시간(초)")] public float enterTime = 0.6f;
    [Tooltip("멈춰서 보여주는 시간(초). 0이면 들어오자마자 나감")] public float holdTime = 0.5f;
    [Tooltip("나가는 시간(초)")] public float exitTime = 0.7f;
    [Tooltip("도착할 때 커졌다 돌아오는 정도. 0이면 끔")] public float punch = 0.2f;
    [Tooltip("노트·판정보다 위에 그리기 (씬 스프라이트는 최대 3)")] public int sortingOrder = 10;

    public void Launch(Vector2 startpos, Vector2 endpos, Sprite skillUserImg)
    {
        GameObject skilluser = new GameObject("SkillUser", typeof(SpriteRenderer));
        var s = skilluser.GetComponent<SpriteRenderer>();
        s.sprite = skillUserImg;
        s.sortingOrder = sortingOrder;
        var t = skilluser.transform;

        // 시작 상태: startpos의 x만 사용(y는 목표 높이), +45도, 투명
        t.position = new Vector2(startpos.x, endpos.y);
        t.rotation = Quaternion.Euler(0f, 0f, -45f);
        Color c = s.color; c.a = 0f; s.color = c;

        // 화면 오른쪽 바깥 x (회전해도 안 걸리게 대각선 반지름만큼 여유)
        Camera cam = Camera.main;
        float rightEdge = cam.ViewportToWorldPoint(new Vector3(1f, 0.5f, Mathf.Abs(cam.transform.position.z))).x;
        float exitX = rightEdge + s.sprite.bounds.extents.magnitude;

        var seq = DOTween.Sequence()
            // 등장
            .Append(t.DOMoveX(endpos.x, enterTime).SetEase(Ease.OutBack))
            .Join(t.DORotate(Vector3.zero, enterTime).SetEase(Ease.OutBack))
            .Join(s.DOFade(1f, enterTime * 0.4f));

        // 도착 순간 한 번 튕겨서 "발동" 느낌. 멈춘 동안 살짝 위아래로 떠 있음
        if (punch > 0f)
            seq.Append(t.DOPunchScale(Vector3.one * punch, 0.3f, 6, 0.5f));
        seq.Append(t.DOMoveY(endpos.y + 0.15f, holdTime).SetEase(Ease.InOutSine));

        // 퇴장
        seq.Append(t.DOMoveX(exitX, exitTime).SetEase(Ease.InExpo))
            .Join(t.DORotate(new Vector3(0f, 0f, -180f), exitTime, RotateMode.FastBeyond360).SetEase(Ease.InExpo))
            .Join(s.DOFade(0f, exitTime))
            .SetLink(skilluser)
            .OnComplete(() => Destroy(skilluser));
    }
}
