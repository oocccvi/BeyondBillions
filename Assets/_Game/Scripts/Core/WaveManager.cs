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
    public int initialPopulation = 50; // 初始游荡僵尸数量
    public float safeZoneRadius = 40f;
    public float cityAvoidanceRadius = 30f;

    [Header("Wave Settings")]
    public int currentWave = 0;
    public float timeToNextWave = 60f; // 波次间隔
    public bool isWaveActive = false;

    [Header("Difficulty Curve")]
    // [Modified] 简化的刷新规则
    public int zombiesPerPoint = 20; // 这里原本是每个点位的数量，现在可以理解为总强度的参考值
    public int maxSpawnPoints = 4;   // 这个参数现在用于计算总僵尸数 (Waves * Points * Zombies)

    [Header("Zombie Settings")]
    public Mesh zombieMesh;
    public Material zombieMaterial;   // 波次僵尸材质（默认红色/血腥）
    public Material wandererMaterial; // 游荡者材质（建议绿色/腐烂，用于区分）

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
        // 游荡者依然是随机生成
        SpawnZombies(initialPopulation, isWanderer: true);
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

        // [Fix] 核心修复：在波次开始前，确保流场是活跃的
        if (FlowFieldController.Instance != null)
        {
            // [Critical Fix] 强制完成所有 ECS Job，防止与 ZombieMoveSystem 发生读写冲突 (Race Condition)
            World.DefaultGameObjectInjectionWorld.EntityManager.CompleteAllTrackedJobs();

            if (FlowFieldController.Instance.HasHQ)
            {
                FlowFieldController.Instance.CalculateFlowField();
                Debug.Log("[WaveManager] 由于波次开始，强制刷新流场 (目标: HQ)");
            }
            else
            {
                int cx = MapGenerator.Instance.width / 2;
                int cy = MapGenerator.Instance.height / 2;
                FlowFieldController.Instance.UpdateTargetPosition(cx, cy);
                Debug.Log($"[WaveManager] 没有找到 HQ，临时将流场目标设为地图中心 ({cx},{cy})");
            }
        }

        // [Modified] 计算总僵尸数
        // 逻辑保持：强度随波次增加
        int multiplier = Mathf.Clamp(currentWave, 1, maxSpawnPoints);
        int totalZombieCount = multiplier * zombiesPerPoint;

        Debug.Log($"<color=red>=== WAVE {currentWave} STARTED ===</color>");
        Debug.Log($"Total Zombies: {totalZombieCount} (Spawning Randomly at Edges)");

        SpawnZombies(totalZombieCount, isWanderer: false);

        timeToNextWave = 60f;
        isWaveActive = false;
    }

    // [Modified] 移除了 spawnPointCount 参数，改为完全随机分布
    void SpawnZombies(int totalCount, bool isWanderer)
    {
        if (totalCount <= 0) return;

        // 区分材质
        Material targetMat = isWanderer ? (wandererMaterial != null ? wandererMaterial : zombieMaterial) : zombieMaterial;

        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { targetMat }, new Mesh[] { zombieMesh });

        NativeArray<Entity> entities = _entityManager.CreateEntity(_zombieArchetype, totalCount, Allocator.Temp);
        Unity.Mathematics.Random random = new Unity.Mathematics.Random((uint)System.DateTime.Now.Ticks);

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        var mapData = MapGenerator.Instance.MapData;
        float2 center = new float2(width / 2f, height / 2f);

        // 获取 FlowField 引用用于检查路径
        bool checkFlowField = !isWanderer && FlowFieldController.Instance != null && FlowFieldController.Instance.IntegrationField.IsCreated;
        NativeArray<ushort> integrationField = default;
        if (checkFlowField) integrationField = FlowFieldController.Instance.IntegrationField;

        // --- 生成循环 ---
        for (int i = 0; i < totalCount; i++)
        {
            Entity entity = entities[i];

            RenderMeshUtility.AddComponents(entity, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            int2 spawnPos = int2.zero;
            int attempts = 0;
            bool validPos = false;

            while (attempts < 30) // 增加尝试次数以确保能找到点
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
                    // [New] 波次僵尸：地图边缘随机
                    // 随机选择四条边中的一条，并在该边上随机选点
                    int edge = random.NextInt(0, 4);
                    int margin = 3; // 距离边缘的距离范围

                    switch (edge)
                    {
                        case 0: // 下边
                            x = random.NextInt(0, width);
                            y = random.NextInt(0, margin);
                            break;
                        case 1: // 上边
                            x = random.NextInt(0, width);
                            y = random.NextInt(height - margin, height);
                            break;
                        case 2: // 左边
                            x = random.NextInt(0, margin);
                            y = random.NextInt(0, height);
                            break;
                        case 3: // 右边
                            x = random.NextInt(width - margin, width);
                            y = random.NextInt(0, height);
                            break;
                    }

                    x = Mathf.Clamp(x, 0, width - 1);
                    y = Mathf.Clamp(y, 0, height - 1);
                }

                int index = y * width + x;
                byte t = mapData[index].TerrainType;

                bool isWalkable = (t >= 2 && t != 6 && t != 7 && t != 9);
                bool hasBuilding = mapData[index].BuildingType != 0;

                if (isWalkable && !hasBuilding)
                {
                    // 检查路径可达性
                    bool isReachable = true;
                    if (checkFlowField)
                    {
                        if (integrationField[index] == ushort.MaxValue)
                        {
                            isReachable = false;
                        }
                    }

                    if (isReachable)
                    {
                        float dist = math.distance(new float2(x, y), center);
                        // 游荡者需要避开安全区，波次僵尸不需要（因为就在边缘生成）
                        if (!isWanderer || (dist > safeZoneRadius && !MapGenerator.Instance.IsPositionNearCity(new float2(x, y), cityAvoidanceRadius)))
                        {
                            spawnPos = new int2(x, y);
                            validPos = true;
                            break;
                        }
                    }
                }
                attempts++;
            }

            if (!validPos)
            {
                _entityManager.DestroyEntity(entity);
                continue;
            }

            float3 pos = new float3(spawnPos.x, 1f, spawnPos.y);
            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(pos));

            float hp = zombieHealth + (isWanderer ? 0 : currentWave * 5);
            _entityManager.SetComponentData(entity, new ZombieHealth { Value = hp, Max = hp });

            float speedVal = isWanderer ? zombieSpeed * 0.6f : zombieSpeed;
            _entityManager.SetComponentData(entity, new MoveSpeed { Value = speedVal * random.NextFloat(0.8f, 1.2f) });

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