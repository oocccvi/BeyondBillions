using UnityEngine;
using System.Collections;

public class GameStarter : MonoBehaviour
{
    [Header("自动化流程开关")]
    public bool autoStart = true;          // 是否自动开始
    public float startupDelay = 0.5f;      // 启动延迟 (给地图生成留点时间)

    [Header("引用 (自动查找)")]
    public UnitSpawner unitSpawner;

    IEnumerator Start()
    {
        if (!autoStart) yield break;

        // 1. 等待一小会儿，确保 MapGenerator 的 Start() 已经跑完了
        // (因为 Unity 不同脚本的 Start 顺序是不确定的，等一帧或0.1秒最稳妥)
        yield return new WaitForSeconds(startupDelay);

        Debug.Log("<color=white>=== [GameStarter] 自动化流程开始 ===</color>");

        // 2. 确保地图已生成
        if (MapGenerator.Instance != null && MapGenerator.Instance.IsInitialized)
        {
            Debug.Log("[1/3] 地图检测完毕: OK");
        }
        else
        {
            Debug.LogError("地图生成失败或未初始化！流程终止。");
            yield break;
        }

        // 3. 自动计算流场
        if (FlowFieldController.Instance != null)
        {
            Debug.Log("[2/3] 正在计算流场...");
            FlowFieldController.Instance.CalculateFlowField();
        }
        else
        {
            Debug.LogError("找不到 FlowFieldController！");
        }

        // 4. 等一帧，确保流场数据写入内存
        yield return null;

        // 5. 自动生成僵尸
        // 如果 Inspector 没拖拽，尝试自动找一下
        if (unitSpawner == null) unitSpawner = FindObjectOfType<UnitSpawner>();

        if (unitSpawner != null)
        {
            Debug.Log("[3/3] 正在生成僵尸大军...");
            unitSpawner.SpawnEntities();
        }
        else
        {
            Debug.LogError("找不到 UnitSpawner，无法生成僵尸！");
        }

        Debug.Log("<color=green>=== [GameStarter] 游戏启动成功！ ===</color>");
    }
}