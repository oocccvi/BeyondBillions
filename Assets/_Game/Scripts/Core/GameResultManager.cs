using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Entities;
using Unity.Collections;

public class GameResultManager : MonoBehaviour
{
    public static GameResultManager Instance { get; private set; }

    public bool isGameOver = false;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    public void TriggerGameOver()
    {
        if (isGameOver) return;

        isGameOver = true;
        Debug.LogError("=== 游戏结束！指挥中心被摧毁！ ===");
        Time.timeScale = 0f;
    }

    public void RetryGame()
    {
        Time.timeScale = 1f;
        ClearAllEntities();
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    private void ClearAllEntities()
    {
        EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;

        // 1. 销毁战斗单位
        em.DestroyEntity(em.CreateEntityQuery(typeof(Turret)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(SoldierTag)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(ZombieTag)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(Projectile)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(DeadBuildingTag)));

        // 2. [核心修复] 销毁迷雾数据实体
        // 这会告诉 FogOfWarSystem："旧游戏结束了，请重置数据"
        em.DestroyEntity(em.CreateEntityQuery(typeof(FogMapData)));

        // 3. 销毁流场数据实体 (局部流场)
        em.DestroyEntity(em.CreateEntityQuery(typeof(FlowFieldTag)));

        // 4. 清理 Mono 端数据
        if (MapGenerator.Instance != null && MapGenerator.Instance.BuildingMap.IsCreated)
        {
            MapGenerator.Instance.BuildingMap.Clear();
        }
    }

    void OnGUI()
    {
        if (!isGameOver) return;

        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");

        GUIStyle headerStyle = new GUIStyle();
        headerStyle.fontSize = 60;
        headerStyle.normal.textColor = Color.red;
        headerStyle.alignment = TextAnchor.MiddleCenter;
        headerStyle.fontStyle = FontStyle.Bold;

        GUIStyle subStyle = new GUIStyle();
        subStyle.fontSize = 25;
        subStyle.normal.textColor = Color.white;
        subStyle.alignment = TextAnchor.MiddleCenter;

        float centerX = Screen.width / 2;
        float centerY = Screen.height / 2;

        GUI.Label(new Rect(centerX - 300, centerY - 100, 600, 100), "GAME OVER", headerStyle);
        GUI.Label(new Rect(centerX - 300, centerY, 600, 50), " The Command Center has fallen.", subStyle);

        if (GUI.Button(new Rect(centerX - 100, centerY + 80, 200, 50), "RETRY (重试)"))
        {
            RetryGame();
        }
    }
}