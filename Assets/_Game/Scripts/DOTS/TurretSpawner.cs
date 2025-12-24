using UnityEngine;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

public class TurretSpawner : MonoBehaviour
{
    public static TurretSpawner Instance { get; private set; }

    [Header("--- 塔的设置 ---")]
    public Mesh turretMesh;
    public Material turretMaterial;
    public float range = 20f;
    public float fireRate = 5f;
    public float maxHealth = 500f;

    [Header("--- 子弹设置 ---")]
    public Mesh projectileMesh;
    public Material projectileMaterial;

    private Entity _projectilePrefab;
    private EntityManager _entityManager;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        CreateProjectilePrefab();
    }

    void CreateProjectilePrefab()
    {
        var archetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(Projectile),
            typeof(Prefab)
        );

        _projectilePrefab = _entityManager.CreateEntity(archetype);

        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.Off, false);
        var renderMeshArray = new RenderMeshArray(new Material[] { projectileMaterial }, new Mesh[] { projectileMesh });
        RenderMeshUtility.AddComponents(_projectilePrefab, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

        _entityManager.SetComponentData(_projectilePrefab, LocalTransform.FromPosition(float3.zero).WithScale(0.3f));
        _entityManager.SetComponentData(_projectilePrefab, new Projectile { Damage = 35, Speed = 40 });
    }

    public void SpawnHQ(float3 position)
    {
        if (MapGenerator.Instance == null) return;

        _entityManager.CompleteAllTrackedJobs();

        int size = 4; // HQ 4x4
        int centerX = (int)position.x;
        int centerZ = (int)position.z;

        // [还原] 不做强制推土机修改，只生成实体

        Entity hq = SpawnTurret(position, true, size);

        // HQ 特有属性
        _entityManager.SetComponentData(hq, LocalTransform.FromPosition(new float3(position.x, 1f, position.z)).WithScale(4.0f));

        _entityManager.SetComponentData(hq, new BuildingHealth
        {
            Value = 5000,
            Max = 5000
        });

        _entityManager.AddComponent<MainBaseTag>(hq);
        _entityManager.SetComponentData(hq, new SightRange { Value = 30f });

        Debug.Log("指挥中心 (HQ) 已部署！");

        if (FlowFieldController.Instance != null)
        {
            FlowFieldController.Instance.UpdateTargetPosition(centerX, centerZ);
        }
    }

    public Entity SpawnTurret(float3 position, bool autoUpdateFlowField = true, int size = 2)
    {
        _entityManager.CompleteAllTrackedJobs();

        var archetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(Turret),
            typeof(BuildingHealth),
            typeof(SightRange)
        );

        Entity turret = _entityManager.CreateEntity(archetype);

        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { turretMaterial }, new Mesh[] { turretMesh });
        RenderMeshUtility.AddComponents(turret, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

        float3 spawnPos = new float3(position.x, 1.0f, position.z);
        _entityManager.SetComponentData(turret, LocalTransform.FromPosition(spawnPos).WithScale((float)size));

        _entityManager.SetComponentData(turret, new Turret
        {
            Range = range,
            FireRate = fireRate,
            Cooldown = 0,
            ProjectilePrefab = _projectilePrefab,
            MuzzleOffset = new float3(0, 2.0f, 0)
        });

        _entityManager.SetComponentData(turret, new BuildingHealth
        {
            Value = maxHealth,
            Max = maxHealth
        });

        _entityManager.SetComponentData(turret, new SightRange { Value = range + 2f });

        int centerX = (int)position.x;
        int centerZ = (int)position.z;
        int offset = size / 2;

        if (MapGenerator.Instance != null)
        {
            // 循环标记
            for (int x = centerX - offset; x < centerX - offset + size; x++)
            {
                for (int z = centerZ - offset; z < centerZ - offset + size; z++)
                {
                    MapGenerator.Instance.PlaceStructure(x, z, 1, true, turret);
                }
            }

            if (autoUpdateFlowField && FlowFieldController.Instance != null)
            {
                FlowFieldController.Instance.CalculateFlowField();
            }
        }

        return turret;
    }
}