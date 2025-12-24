using UnityEngine;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

public class UnitSpawner : MonoBehaviour
{
    [Header("--- 生成设置 ---")]
    public int spawnCount = 100000; // 目标生成数量：10万！
    public Mesh unitMesh;          // 单位的样子
    public Material unitMaterial;  // 单位的颜色
    public float zombieSpeed = 5f; // 僵尸移动速度
    public float zombieHealth = 100f; // [新增] 僵尸血量

    // 内部变量
    private EntityManager _entityManager;
    private EntityArchetype _unitArchetype;

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        // 修改点：我们在原型里加入了 ZombieTag, MoveSpeed 和 ZombieHealth
        _unitArchetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(ZombieTag),    // 标记：我是僵尸
            typeof(MoveSpeed),    // 数据：我有速度
            typeof(ZombieHealth)  // [新增] 数据：我有血条
        );
    }

    [ContextMenu("Spawn Zombies")]
    public void SpawnEntities()
    {
        // 使用单例访问地图，不用拖拽了
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized)
        {
            Debug.LogError("地图未初始化，无法生成单位！");
            return;
        }

        Debug.Log($"开始生成 {spawnCount} 个单位...");

        var desc = new RenderMeshDescription(
            UnityEngine.Rendering.ShadowCastingMode.On,
            true);

        var renderMeshArray = new RenderMeshArray(
            new Material[] { unitMaterial },
            new Mesh[] { unitMesh }
        );

        NativeArray<Entity> entities = _entityManager.CreateEntity(_unitArchetype, spawnCount, Allocator.Temp);
        Unity.Mathematics.Random random = new Unity.Mathematics.Random(1234);
        NativeArray<MapGenerator.CellData> mapData = MapGenerator.Instance.MapData;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;

        for (int i = 0; i < spawnCount; i++)
        {
            Entity entity = entities[i];

            RenderMeshUtility.AddComponents(
                entity,
                _entityManager,
                desc,
                renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            // --- 找落脚点 ---
            int2 randomPos = int2.zero;
            int maxAttempts = 10;
            float yPos = 1f;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int x = random.NextInt(0, width);
                int y = random.NextInt(0, height);
                int index = y * width + x;

                var cell = mapData[index];

                // 只要不是水 (Type 0) 且不是悬崖 (Type 2)
                if (cell.TerrainType != 0 && cell.TerrainType != 2)
                {
                    randomPos = new int2(x, y);
                    yPos = 1f; // 平原高度
                    break;
                }
            }

            // 设置坐标
            float3 position = new float3(
                randomPos.x + random.NextFloat(0.1f, 0.9f),
                yPos,
                randomPos.y + random.NextFloat(0.1f, 0.9f)
            );

            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(position));

            // 设置速度
            _entityManager.SetComponentData(entity, new MoveSpeed { Value = zombieSpeed * random.NextFloat(0.8f, 1.2f) });

            // [新增] 设置血量
            _entityManager.SetComponentData(entity, new ZombieHealth { Value = zombieHealth, Max = zombieHealth });
        }

        entities.Dispose();
        Debug.Log($"<color=green>成功生成 {spawnCount} 个僵尸 (HP: {zombieHealth})！准备冲锋！</color>");
    }
}