using DG.Tweening;
using UnityEngine;

public class SkillEffect : MonoBehaviour
{
    public void Launch(Vector2 startpos, Vector2 endpos, Sprite skillUserImg)
    {
        GameObject skilluser = new GameObject("SkillUser", typeof(SpriteRenderer));
        var s = skilluser.GetComponent<SpriteRenderer>();
        s.sprite = skillUserImg;
        var t = skilluser.transform;

        // 시작 상태: startpos의 x만 사용(y는 목표 높이), +45도, 투명
        t.position = new Vector2(startpos.x, endpos.y);
        t.rotation = Quaternion.Euler(0f, 0f, -45f);
        Color c = s.color; c.a = 0f; s.color = c;

        // 화면 오른쪽 바깥 x (회전해도 안 걸리게 대각선 반지름만큼 여유)
        Camera cam = Camera.main;
        float rightEdge = cam.ViewportToWorldPoint(new Vector3(1f, 0.5f, Mathf.Abs(cam.transform.position.z))).x;
        float exitX = rightEdge + s.sprite.bounds.extents.magnitude;

        DOTween.Sequence()
            // 등장
            .Append(t.DOMoveX(endpos.x, 1f).SetEase(Ease.OutBack))
            .Join(t.DORotate(Vector3.zero, 1f).SetEase(Ease.OutBack))
            .Join(s.DOFade(1f, 0.4f))
            // 퇴장
            .Append(t.DOMoveX(exitX, 1f).SetEase(Ease.InExpo))
            .Join(t.DORotate(new Vector3(0f, 0f, -180f), 1f, RotateMode.FastBeyond360).SetEase(Ease.InExpo))
            .Join(s.DOFade(0f, 1f))
            .SetLink(skilluser)
            .OnComplete(() => Destroy(skilluser));
    }
}