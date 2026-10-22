using IMGUI;
using UnityEngine;

// I'm GUI를 그리는 GUIManager를 게임 시작 때 하나 만들어 씬을 넘어 살린다. 씬마다 놓을 필요 없음
// 위젯을 만든 스크립트가 OnDestroy에서 Unregister한다. 안 하면 다음 씬에도 남아 그려진다
static class GUIHost
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Create()
    {
        var go = new GameObject("GUIManager");
        go.AddComponent<GUIManager>();
        Object.DontDestroyOnLoad(go);
    }
}
