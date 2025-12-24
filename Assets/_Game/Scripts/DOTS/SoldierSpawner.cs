using UnityEngine;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

public class SoldierSpawner : MonoBehaviour
{
    public static SoldierSpawner Instance { get; private set; }

    [Header("士兵设置")]
    public Mesh soldierMesh;
    public Material soldierMaterial;
    public float moveSpeed = 6f;
    public float maxHealth = 150f;

    [Header("武器设置")]
    public float range = 18f;
    public float fireRate = 3f;
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

        _entityManager.SetComponentData(_projectilePrefab, LocalTransform.FromPosition(float3.zero).WithScale(0.2f));
        _entityManager.SetComponentData(_projectilePrefab, new Projectile { Damage = 20, Speed = 40 });
    }

    public void SpawnSoldier(float3 position)
    {
        var archetype = _entityManager.CreateArchetype(
            typeof(LocalTransform),
            typeof(LocalToWorld),
            typeof(RenderMeshArray),
            typeof(RenderBounds),
            typeof(SoldierTag),
            typeof(SoldierHealth),
            typeof(SoldierState),
            typeof(MoveSpeed),
            typeof(Turret),
            typeof(SightRange) // [新增] 加上视野组件
        );

        Entity soldier = _entityManager.CreateEntity(archetype);

        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { soldierMaterial }, new Mesh[] { soldierMesh });
        RenderMeshUtility.AddComponents(soldier, _entityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

        float3 spawnPos = new float3(position.x, 1.0f, position.z);
        _entityManager.SetComponentData(soldier, LocalTransform.FromPosition(spawnPos).WithScale(0.8f));

        _entityManager.SetComponentData(soldier, new MoveSpeed { Value = moveSpeed });

        _entityManager.SetComponentData(soldier, new SoldierHealth
        {
            Value = maxHealth,
            Max = maxHealth
        });

        _entityManager.SetComponentData(soldier, new SoldierState
        {
            TargetPosition = spawnPos,
            IsMoving = false,
            StopDistance = 0.5f,
            Command = SoldierCommand.Idle
        });

        _entityManager.SetComponentData(soldier, new Turret
        {
            Range = range,
            FireRate = fireRate,
            Cooldown = 0,
            ProjectilePrefab = _projectilePrefab,
            MuzzleOffset = new float3(0, 1.0f, 0)
        });

        // [新增] 士兵视野 15米
        _entityManager.SetComponentData(soldier, new SightRange { Value = 15f });
    }
}