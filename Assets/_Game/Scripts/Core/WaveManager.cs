using UnityEngine;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Rendering;

public class WaveManager : MonoBehaviour
{
    public static WaveManager Instance { get; private set; }

    [Header("Basic Settings")]
    public int initialPopulation = 50; // 初始游荡僵尸数量 (降低以减轻开局压力)
    public float safeZoneRadius = 40f;
    public float cityAvoidanceRadius = 30f;

    [Header("Wave Settings")]
    public int currentWave = 0;
    public float timeToNextWave = 60f; // 波次间隔
    public bool isWaveActive = false;

    [Header("Difficulty Curve")]
    public int baseWaveCount = 20; // 第一波僵尸数量
    public float countMultiplier = 1.2f; // 每波数量倍率 (1.2 = 增加20%)
    public int maxSpawnPoints = 4; // 最大同时刷新点数量

    [Header("Zombie Settings")]
    public Mesh zombieMesh;
    public Material zombieMaterial;
    public float zombieSpeed = 2f;
    public float zombieHealth = 100f;

    private EntityManager _entityManager;
    private EntityArchetype _zombieArchetype;
    private float _timer;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        _zombieArchetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(ZombieTag),
            typeof(MoveSpeed),
            typeof(ZombieHealth),
            typeof(ZombieState)
        );

        StartCoroutine(SpawnInitialPopulation());
    }

    System.Collections.IEnumerator SpawnInitialPopulation()
    {
        yield return new WaitForSeconds(0.5f);

        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) yield break;

        Debug.Log($"<color=orange>Spawning initial wanderers: {initialPopulation}...</color>");
        SpawnZombies(initialPopulation, isWanderer: true, spawnPointCount: 1); // 游荡者全图随机，这里 spawnPointCount 参数对它无效
    }

    void Update()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        if (!isWaveActive)
        {
            _timer += Time.deltaTime;
            if (_timer >= timeToNextWave)
            {
                StartNextWave();
                _timer = 0;
            }
        }
    }

    void StartNextWave()
    {
        currentWave++;
        isWaveActive = true;

        // 1. 计算本波次总僵尸数量
        int totalZombieCount = Mathf.CeilToInt(baseWaveCount * Mathf.Pow(countMultiplier, currentWave - 1));

        // 2. 计算本波次激活的刷新点数量 (随着波次增加，刷新点从 1 增加到 maxSpawnPoints)
        // 例如：第1-3波: 1个点; 第4-6波: 2个点...
        int activeSpawnPoints = Mathf.Clamp((currentWave + 2) / 3, 1, maxSpawnPoints);

        Debug.Log($"<color=red>=== WAVE {currentWave} STARTED ===</color>");
        Debug.Log($"Total Zombies: {totalZombieCount}, Spawn Points: {activeSpawnPoints}");

        SpawnZombies(totalZombieCount, isWanderer: false, spawnPointCount: activeSpawnPoints);

        timeToNextWave = 60f; // 重置下一波倒计时
        isWaveActive = false;
    }

    void SpawnZombies(int totalCount, bool isWanderer, int spawnPointCount)
    {
        if (totalCount <= 0) return;

        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { zombieMaterial }, new Mesh[] { zombieMesh });

        NativeArray<Entity> entities = _entityManager.CreateEntity(_zombieArchetype, totalCount, Allocator.Temp);
        Unity.Mathematics.Random random = new Unity.Mathematics.Random((uint)System.DateTime.Now.Ticks);

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        var mapData = MapGenerator.Instance.MapData;
        float2 center = new float2(width / 2f, height / 2f);

        // --- 准备刷新点 (仅针对波次僵尸) ---
        NativeList<int2> spawnCenters = new NativeList<int2>(Allocator.Temp);
        if (!isWanderer)
        {
            int attempts = 0;
            // 尝试寻找有效刷新点
            while (spawnCenters.Length < spawnPointCount && attempts < 50)
            {
                attempts++;
                int edge = random.NextInt(0, 4);
                int ex = 0, ey = 0;
                int margin = 2;

                switch (edge)
                {
                    case 0: ex = random.NextInt(0, width); ey = margin; break; // Bottom
                    case 1: ex = random.NextInt(0, width); ey = height - margin - 1; break; // Top
                    case 2: ex = margin; ey = random.NextInt(0, height); break; // Left
                    case 3: ex = width - margin - 1; ey = random.NextInt(0, height); break; // Right
                }

                // 检查地形：避开水域(0,1)、高山(6,7)、废墟(9)
                int idx = ey * width + ex;
                if (idx >= 0 && idx < mapData.Length)
                {
                    byte t = mapData[idx].TerrainType;
                    if (t >= 2 && t != 6 && t != 7 && t != 9)
                    {
                        spawnCenters.Add(new int2(ex, ey));
                    }
                }
            }
            // 如果找不到，回退到地图中心（极少发生）
            if (spawnCenters.Length == 0) spawnCenters.Add(new int2(width / 2, height / 2));
        }

        // --- 生成僵尸 ---
        // 如果是波次僵尸，我们需要把 totalCount 均匀分配给 spawnCenters
        // 为了简单，我们每次循环随机选一个点，或者按顺序分配

        for (int i = 0; i < totalCount; i++)
        {
            Entity entity = entities[i];
            RenderMeshUtility.AddComponents(entity, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            int2 spawnPos = int2.zero;
            int attempts = 0;
            bool validPos = false;

            // 确定这只僵尸归属的刷新中心
            int2 myCenter = int2.zero;
            if (!isWanderer)
            {
                // 均匀分配策略：i % spawnCenters.Length
                myCenter = spawnCenters[i % spawnCenters.Length];
            }

            while (attempts < 20)
            {
                int x = 0, y = 0;

                if (isWanderer)
                {
                    // 游荡者：全图随机
                    x = random.NextInt(0, width);
                    y = random.NextInt(0, height);
                }
                else
                {
                    // 波次僵尸：在归属点周围随机偏移 (30x30区域)
                    x = myCenter.x + random.NextInt(-15, 16);
                    y = myCenter.y + random.NextInt(-15, 16);
                    x = Mathf.Clamp(x, 0, width - 1);
                    y = Mathf.Clamp(y, 0, height - 1);
                }

                int index = y * width + x;
                byte t = mapData[index].TerrainType;

                // 统一地形检查
                bool isWalkable = (t >= 2 && t != 6 && t != 7 && t != 9);
                bool hasBuilding = mapData[index].BuildingType != 0;

                if (isWalkable && !hasBuilding)
                {
                    float dist = math.distance(new float2(x, y), center);
                    // 游荡者避开城市和出生点
                    if (!isWanderer || (dist > safeZoneRadius && !MapGenerator.Instance.IsPositionNearCity(new float2(x, y), cityAvoidanceRadius)))
                    {
                        spawnPos = new int2(x, y);
                        validPos = true;
                        break;
                    }
                }
                attempts++;
            }

            if (!validPos)
            {
                _entityManager.DestroyEntity(entity);
                continue;
            }

            // 设置组件
            float3 pos = new float3(spawnPos.x, 1f, spawnPos.y);
            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(pos));

            // 血量随波次增加 (如果是游荡者则不加)
            float hp = zombieHealth + (isWanderer ? 0 : currentWave * 5);
            _entityManager.SetComponentData(entity, new ZombieHealth { Value = hp, Max = hp });

            // 速度微调
            float speedVal = isWanderer ? zombieSpeed * 0.6f : zombieSpeed;
            _entityManager.SetComponentData(entity, new MoveSpeed { Value = speedVal * random.NextFloat(0.8f, 1.2f) });

            // 状态
            if (isWanderer)
            {
                float2 randDir = random.NextFloat2Direction();
                _entityManager.SetComponentData(entity, new ZombieState
                {
                    Behavior = ZombieBehavior.Wander,
                    Timer = random.NextFloat(1f, 5f),
                    WanderDirection = new float3(randDir.x, 0, randDir.y)
                });
            }
            else
            {
                _entityManager.SetComponentData(entity, new ZombieState { Behavior = ZombieBehavior.Rush });
            }
        }

        if (spawnCenters.IsCreated) spawnCenters.Dispose();
        entities.Dispose();
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle();
        style.fontSize = 25;
        style.normal.textColor = Color.red;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.UpperCenter;

        float timeLeft = Mathf.Max(0, timeToNextWave - _timer);
        string text = isWaveActive ? $"WAVE {currentWave} SPAWNING..." : $"WAVE {currentWave + 1} IN: {timeLeft:F1}s";

        GUI.Label(new Rect(Screen.width / 2 - 150, 20, 300, 50), text, style);
    }
}