using UnityEngine;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

public class UnitSpawner : MonoBehaviour
{
    public enum SpawnMode
    {
        RandomMap,   // 全图均匀随机分布 (适合游荡者)
        EdgeCluster  // 边缘集群分布 (适合尸潮)
    }

    [Header("--- 生成设置 ---")]
    [Tooltip("每次生成的数量")]
    public int spawnCount = 20; // 默认改为 20，避免误操作生成太多
    public SpawnMode spawnMode = SpawnMode.RandomMap; // 默认改为 RandomMap
    public Mesh unitMesh;
    public Material unitMaterial;
    public float zombieSpeed = 5f;
    public float zombieHealth = 100f;

    [Header("--- 自动化测试 (随着时间增加数量) ---")]
    [Tooltip("是否开启自动循环生成")]
    public bool enableAutoSpawn = false;
    [Tooltip("每次生成的间隔时间 (秒)")]
    public float spawnInterval = 10f;
    [Tooltip("每次生成后数量的倍率增长 (例如 1.2 代表增加 20%)")]
    public float difficultyMultiplier = 1.2f;
    [Tooltip("自动生成的最大数量限制，防止显卡爆炸")]
    public int maxAutoSpawnCount = 500;

    [Header("--- 行为设置 ---")]
    [Tooltip("生成的僵尸初始状态。建议设为 Rush 以测试流场移动。")]
    public ZombieBehavior initialBehavior = ZombieBehavior.Rush;

    private EntityManager _entityManager;
    private EntityArchetype _unitArchetype;
    // 使用 System.Random 来生成随机种子，确保每次运行都不一样
    private System.Random _sysRandom = new System.Random();

    // [新增] 用于保存初始生成数量，方便重置
    private int _originalSpawnCount;

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        _unitArchetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(ZombieTag),
            typeof(MoveSpeed),
            typeof(ZombieHealth),
            typeof(ZombieState)
        );

        // 保存初始值
        _originalSpawnCount = spawnCount;

        if (enableAutoSpawn)
        {
            StartCoroutine(AutoSpawnRoutine());
        }
    }

    System.Collections.IEnumerator AutoSpawnRoutine()
    {
        // 等待地图初始化
        yield return new WaitForSeconds(1.0f);

        // [核心修复] 开始自动生成前，重置数量为初始值
        // 这样每次点击运行游戏时，都会从 20 开始，而不是上次退出时的 1200
        spawnCount = _originalSpawnCount;

        while (enableAutoSpawn)
        {
            if (MapGenerator.Instance != null && MapGenerator.Instance.IsInitialized)
            {
                SpawnEntities();
                // 增加下一波的数量，但设置上限
                int nextCount = Mathf.CeilToInt(spawnCount * difficultyMultiplier);
                spawnCount = Mathf.Min(nextCount, maxAutoSpawnCount);
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    [ContextMenu("Spawn Zombies")]
    public void SpawnEntities()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized)
        {
            Debug.LogError("地图未初始化，无法生成单位！");
            return;
        }

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        var mapData = MapGenerator.Instance.MapData;

        // [核心修改] 使用动态种子，确保每次调用生成的集群位置都不同
        uint seed = (uint)_sysRandom.Next(1, int.MaxValue);
        Unity.Mathematics.Random random = new Unity.Mathematics.Random(seed);

        // 如果是 Rush 模式，生成前刷新流场目标
        if (FlowFieldController.Instance != null && initialBehavior == ZombieBehavior.Rush)
        {
            // 如果已有 HQ，FlowFieldController 会自己处理；如果没有，这里可能会重置为中心。
            // 为了安全，如果已有 HQ，我们不应该在这里强制重置为中心。
            if (!FlowFieldController.Instance.HasHQ)
            {
                FlowFieldController.Instance.UpdateTargetPosition(width / 2, height / 2);
            }
        }

        Debug.Log($"开始生成 {spawnCount} 个单位 (模式: {spawnMode}, 种子: {seed})...");

        // --- 准备集群点 (仅 EdgeCluster 模式) ---
        NativeList<int2> clusters = new NativeList<int2>(Allocator.Temp);
        if (spawnMode == SpawnMode.EdgeCluster)
        {
            int perimeter = (width + height) * 2;
            int count = Mathf.Clamp(perimeter / 150, 2, 8); // 自动计算点数

            int attempts = 0;
            while (clusters.Length < count && attempts < 50)
            {
                attempts++;
                int edge = random.NextInt(0, 4);
                int ex = 0, ey = 0;
                int margin = 2;
                switch (edge)
                {
                    case 0: ex = random.NextInt(0, width); ey = margin; break;
                    case 1: ex = random.NextInt(0, width); ey = height - margin - 1; break;
                    case 2: ex = margin; ey = random.NextInt(0, height); break;
                    case 3: ex = width - margin - 1; ey = random.NextInt(0, height); break;
                }

                int idx = ey * width + ex;
                if (idx >= 0 && idx < mapData.Length)
                {
                    byte t = mapData[idx].TerrainType;
                    // 避开水域和高山
                    if (t >= 2 && t != 6 && t != 7 && t != 9)
                    {
                        clusters.Add(new int2(ex, ey));
                    }
                }
            }
            if (clusters.Length == 0) clusters.Add(new int2(width / 2, height / 2)); // Fallback
        }


        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { unitMaterial }, new Mesh[] { unitMesh });

        NativeArray<Entity> entities = _entityManager.CreateEntity(_unitArchetype, spawnCount, Allocator.Temp);

        for (int i = 0; i < spawnCount; i++)
        {
            Entity entity = entities[i];
            RenderMeshUtility.AddComponents(entity, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            int2 randomPos = int2.zero;
            int maxAttempts = 10;
            float yPos = 1f;
            bool foundValid = false;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int x = 0, y = 0;

                if (spawnMode == SpawnMode.RandomMap)
                {
                    // [修改] 全图随机：每个僵尸独立随机坐标
                    x = random.NextInt(0, width);
                    y = random.NextInt(0, height);
                }
                else // EdgeCluster
                {
                    // 集群模式：围绕随机选出的几个点生成
                    if (clusters.Length > 0)
                    {
                        int2 c = clusters[random.NextInt(0, clusters.Length)];
                        x = c.x + random.NextInt(-15, 16);
                        y = c.y + random.NextInt(-15, 16);
                        x = Mathf.Clamp(x, 0, width - 1);
                        y = Mathf.Clamp(y, 0, height - 1);
                    }
                }

                int index = y * width + x;
                // 边界检查，防止 RandomMap 随机出的点越界（虽然 NextInt 不会）
                if (index < 0 || index >= mapData.Length) continue;

                byte t = mapData[index].TerrainType;

                // 统一地形检查：避开水(0,1)、山(6,7)、废墟(9)
                if (t >= 2 && t != 6 && t != 7 && t != 9)
                {
                    randomPos = new int2(x, y);
                    yPos = 1f;
                    foundValid = true;
                    break;
                }
            }

            if (!foundValid)
            {
                _entityManager.DestroyEntity(entity);
                continue;
            }

            float3 position = new float3(
                randomPos.x + random.NextFloat(0.1f, 0.9f),
                yPos,
                randomPos.y + random.NextFloat(0.1f, 0.9f)
            );

            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(position));
            _entityManager.SetComponentData(entity, new MoveSpeed { Value = zombieSpeed * random.NextFloat(0.8f, 1.2f) });
            _entityManager.SetComponentData(entity, new ZombieHealth { Value = zombieHealth, Max = zombieHealth });

            var state = new ZombieState
            {
                Behavior = initialBehavior,
                Timer = random.NextFloat(2f, 8f)
            };

            if (initialBehavior == ZombieBehavior.Wander)
            {
                float2 randDir = random.NextFloat2Direction();
                state.WanderDirection = new float3(randDir.x, 0, randDir.y);
            }

            _entityManager.SetComponentData(entity, state);
        }

        if (clusters.IsCreated) clusters.Dispose();
        entities.Dispose();
        Debug.Log($"<color=green>成功生成 {spawnCount} 个僵尸！</color>");
    }
}