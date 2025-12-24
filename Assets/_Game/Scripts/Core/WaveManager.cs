using UnityEngine;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Rendering;

public class WaveManager : MonoBehaviour
{
    public static WaveManager Instance { get; private set; }

    [Header("地图人口设置")]
    public int initialPopulation = 5000;
    public float safeZoneRadius = 40f;
    [Tooltip("游荡僵尸避开城市的半径")]
    public float cityAvoidanceRadius = 30f; // [新增] 避开城市

    [Header("波次设置")]
    public int currentWave = 0;
    public float timeToNextWave = 60f;
    public bool isWaveActive = false;

    [Header("难度曲线")]
    public int baseCount = 50;
    public float difficultyMultiplier = 1.5f;

    [Header("僵尸设置")]
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

        Debug.Log($"<color=orange>正在生成地图游荡人口: {initialPopulation} 只...</color>");

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

        int count = Mathf.FloorToInt(baseCount * Mathf.Pow(difficultyMultiplier, currentWave - 1));

        Debug.Log($"<color=red>=== 第 {currentWave} 波尸潮开始！数量: {count} ===</color>");

        SpawnZombies(count, isWanderer: false);

        timeToNextWave = 60f;
        isWaveActive = false;
    }

    void SpawnZombies(int count, bool isWanderer)
    {
        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { zombieMaterial }, new Mesh[] { zombieMesh });

        NativeArray<Entity> entities = _entityManager.CreateEntity(_zombieArchetype, count, Allocator.Temp);
        Unity.Mathematics.Random random = new Unity.Mathematics.Random((uint)System.DateTime.Now.Ticks);

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        var mapData = MapGenerator.Instance.MapData;
        float2 center = new float2(width / 2f, height / 2f);

        for (int i = 0; i < count; i++)
        {
            Entity entity = entities[i];
            RenderMeshUtility.AddComponents(entity, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            int2 spawnPos = int2.zero;
            int attempts = 0;
            bool validPos = false;

            while (attempts < 20)
            {
                int x = 0, y = 0;

                if (isWanderer)
                {
                    x = random.NextInt(0, width);
                    y = random.NextInt(0, height);
                }
                else
                {
                    int edge = random.NextInt(0, 4);
                    switch (edge)
                    {
                        case 0: x = random.NextInt(0, width); y = 1; break;
                        case 1: x = random.NextInt(0, width); y = height - 2; break;
                        case 2: x = 1; y = random.NextInt(0, height); break;
                        case 3: x = width - 2; y = random.NextInt(0, height); break;
                    }
                }

                int index = y * width + x;

                // [检查 1] 必须是平原 (Type 1)
                if (mapData[index].TerrainType == 1)
                {
                    float dist = math.distance(new float2(x, y), center);

                    // [检查 2] 避开出生点安全区
                    if (!isWanderer || dist > safeZoneRadius)
                    {
                        // [检查 3] 游荡者必须避开城市/村庄
                        if (isWanderer && MapGenerator.Instance.IsPositionNearCity(new float2(x, y), cityAvoidanceRadius))
                        {
                            attempts++;
                            continue; // 离城市太近，重试
                        }

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
        string text = $"WAVE {currentWave + 1} IN: {timeLeft:F1}s";

        GUI.Label(new Rect(Screen.width / 2 - 100, 20, 200, 50), text, style);
    }
}