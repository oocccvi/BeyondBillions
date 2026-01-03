using UnityEngine;
using System.Collections;

public class GameStarter : MonoBehaviour
{
    [Header("自动化流程开关")]
    public bool autoStart = true;          // 是否自动开始
    public float startupDelay = 1.0f;      // 增加启动延迟，确保地图完全生成

    [Header("引用 (自动查找)")]
    public UnitSpawner unitSpawner;

    private bool _hasStarted = false;

    IEnumerator Start()
    {
        if (!autoStart) yield break;

        Debug.Log($"<color=white>=== [GameStarter] 等待 {startupDelay}秒 准备启动... ===</color>");

        // 1. 等待地图生成 (MapGenerator 通常在 Start 中生成，给它一点时间)
        yield return new WaitForSeconds(startupDelay);

        // 2. 确保地图已生成
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized)
        {
            Debug.LogError("[GameStarter] 错误：地图未初始化！停止启动流程。");
            yield break;
        }

        int w = MapGenerator.Instance.width;
        int h = MapGenerator.Instance.height;
        Debug.Log($"[1/4] 地图检测完毕: {w}x{h}");

        // 3. 第一次设置流场目标 (尝试激活流场)
        if (FlowFieldController.Instance != null)
        {
            Debug.Log("[2/4] 初始化流场目标...");
            // 设置目标为地图中心
            int tx = w / 2;
            int ty = h / 2;
            FlowFieldController.Instance.UpdateTargetPosition(tx, ty);
        }
        else
        {
            Debug.LogError("[GameStarter] 找不到 FlowFieldController！请检查场景。");
        }

        // 等待一帧，让流场 Job 跑起来
        yield return null;

        // 4. 生成僵尸
        if (unitSpawner == null) unitSpawner = FindObjectOfType<UnitSpawner>();

        if (unitSpawner != null)
        {
            Debug.Log("[3/4] 生成僵尸大军...");
            unitSpawner.SpawnEntities();
        }
        else
        {
            Debug.LogError("[GameStarter] 找不到 UnitSpawner！");
        }

        // 5. [核心修复] 生成后再次强制刷新流场
        // 有时候生成过程会造成卡顿或数据竞争，再次刷新确保所有僵尸都能读到最新数据
        yield return new WaitForSeconds(0.5f);
        if (FlowFieldController.Instance != null)
        {
            Debug.Log("[4/4] <color=yellow>强制重算流场，确保单位激活...</color>");
            FlowFieldController.Instance.CalculateFlowField();
        }

        _hasStarted = true;
        Debug.Log("<color=green>=== [GameStarter] 游戏启动成功！ ===</color>");
    }

    // 可视化调试：在 Scene 视图画出目标点
    void OnDrawGizmos()
    {
        if (_hasStarted && FlowFieldController.Instance != null)
        {
            Vector2Int t = FlowFieldController.Instance.targetPosition;
            Vector3 pos = new Vector3(t.x, 2f, t.y);

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(pos, 2f);
            Gizmos.DrawLine(pos + Vector3.left * 5, pos + Vector3.right * 5);
            Gizmos.DrawLine(pos + Vector3.back * 5, pos + Vector3.forward * 5);
        }
    }
}