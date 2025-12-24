using UnityEngine;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;
using System.IO;
using System.Collections.Generic;

// --- 存档数据结构 ---
[System.Serializable]
public class SaveData
{
    public int mapSeed;
    public int currentGold;

    // [新增] 波次数据
    public int waveIndex;
    public float timeToNextWave;

    public List<BuildingData> buildings = new List<BuildingData>();
}

[System.Serializable]
public struct BuildingData
{
    public int x;
    public int z;
    public float health;
}

public class SaveManager : MonoBehaviour
{
    private string SavePath => Path.Combine(Application.persistentDataPath, "savegame.json");

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F5)) SaveGame();
        if (Input.GetKeyDown(KeyCode.F9)) LoadGame();
    }

    public void SaveGame()
    {
        Debug.Log("正在保存游戏...");

        SaveData data = new SaveData();

        // 1. 保存全局数据
        if (MapGenerator.Instance != null) data.mapSeed = MapGenerator.Instance.seed;
        if (ResourceManager.Instance != null) data.currentGold = ResourceManager.Instance.currentGold;

        // [新增] 保存波次
        if (WaveManager.Instance != null)
        {
            data.waveIndex = WaveManager.Instance.currentWave;
            // 我们不能直接访问 private _timer，但可以根据逻辑推算剩余时间，
            // 或者简单点，我们把 WaveManager 的 _timer 改为 public，或者这里只保存配置
            // 为了简单，我们只保存波数，倒计时重置
            data.timeToNextWave = 30f;
        }

        // 2. 保存防御塔
        EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;
        EntityQuery query = em.CreateEntityQuery(
            typeof(Turret),
            typeof(LocalTransform),
            typeof(BuildingHealth));

        NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        NativeArray<BuildingHealth> healths = query.ToComponentDataArray<BuildingHealth>(Allocator.Temp);

        for (int i = 0; i < transforms.Length; i++)
        {
            float3 pos = transforms[i].Position;
            BuildingData b = new BuildingData
            {
                x = (int)math.round(pos.x),
                z = (int)math.round(pos.z),
                health = healths[i].Value
            };
            data.buildings.Add(b);
        }

        transforms.Dispose();
        healths.Dispose();

        // 3. 写入
        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(SavePath, json);

        Debug.Log($"<color=green>游戏保存成功！路径: {SavePath}</color>");
    }

    public void LoadGame()
    {
        if (!File.Exists(SavePath))
        {
            Debug.LogError("找不到存档文件！");
            return;
        }

        Debug.Log("正在加载游戏...");
        string json = File.ReadAllText(SavePath);
        SaveData data = JsonUtility.FromJson<SaveData>(json);

        // 1. 清理
        ClearWorld();

        // 2. 恢复地图
        if (MapGenerator.Instance != null)
        {
            MapGenerator.Instance.seed = data.mapSeed;
            MapGenerator.Instance.GenerateWorld();
        }

        // 3. 恢复金币
        if (ResourceManager.Instance != null)
        {
            ResourceManager.Instance.currentGold = data.currentGold;
        }

        // [新增] 恢复波次
        if (WaveManager.Instance != null)
        {
            WaveManager.Instance.currentWave = data.waveIndex;
            // 读档后给玩家一点准备时间
            // 如果你想精确恢复倒计时，需要在 WaveManager 里把 timer 公开并赋值
        }

        // 4. 重建塔
        if (TurretSpawner.Instance != null)
        {
            EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;

            foreach (var b in data.buildings)
            {
                Entity turret = TurretSpawner.Instance.SpawnTurret(new float3(b.x, 0, b.z), false);
                if (em.HasComponent<BuildingHealth>(turret))
                {
                    var hp = em.GetComponentData<BuildingHealth>(turret);
                    hp.Value = b.health;
                    em.SetComponentData(turret, hp);
                }
            }
        }

        // 5. 重算流场
        if (FlowFieldController.Instance != null)
        {
            FlowFieldController.Instance.CalculateFlowField();
        }

        Debug.Log($"<color=green>游戏加载完毕！Wave: {data.waveIndex}</color>");
    }

    private void ClearWorld()
    {
        EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;
        em.DestroyEntity(em.CreateEntityQuery(typeof(Turret)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(ZombieTag)));
        em.DestroyEntity(em.CreateEntityQuery(typeof(Projectile)));

        if (MapGenerator.Instance != null && MapGenerator.Instance.BuildingMap.IsCreated)
        {
            MapGenerator.Instance.BuildingMap.Clear();
        }
    }
}