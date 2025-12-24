using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

[UpdateBefore(typeof(ZombieMoveSystem))]
public partial struct ZombieAttackSystem : ISystem
{
    // [关键修复] 必须加上 OnDestroy，否则停止游戏时会报 InvalidOperationException
    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null ||
            !MapGenerator.Instance.BuildingMap.IsCreated ||
            !MapGenerator.Instance.IsInitialized) return;

        var buildingMap = MapGenerator.Instance.BuildingMap;
        var mapData = MapGenerator.Instance.MapData;

        NativeParallelMultiHashMap<int, Entity> soldierMap = default;
        if (SystemAPI.HasSingleton<SoldierSpatialMap>())
        {
            soldierMap = SystemAPI.GetSingleton<SoldierSpatialMap>().Map;
        }

        var width = MapGenerator.Instance.width;
        var height = MapGenerator.Instance.height;
        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();

        float dt = SystemAPI.Time.DeltaTime;

        new ZombieAttackJob
        {
            DeltaTime = dt,
            BuildingMap = buildingMap,
            SoldierMap = soldierMap,
            MapData = mapData,
            Width = width,
            Height = height,
            BuildingHealthLookup = SystemAPI.GetComponentLookup<BuildingHealth>(false),
            SoldierHealthLookup = SystemAPI.GetComponentLookup<SoldierHealth>(false),
            // DeadTagLookup 移除了，因为 CombatComponents 可能没定义，直接用 HasComponent 判断即可
            // 或者如果定义了，可以加回来。为了稳妥，这里先不传 DeadTagLookup，直接用 HasComponent
            // 如果您确定 CombatComponents 里有 DeadBuildingTag，可以保留下行：
            DeadTagLookup = SystemAPI.GetComponentLookup<DeadBuildingTag>(true),
            MainBaseLookup = SystemAPI.GetComponentLookup<MainBaseTag>(true),
            TransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true),
            Ecb = ecb
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct ZombieAttackJob : IJobEntity
{
    public float DeltaTime;
    [ReadOnly] public NativeParallelHashMap<int, Entity> BuildingMap;
    [ReadOnly] public NativeParallelMultiHashMap<int, Entity> SoldierMap;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
    public int Width;
    public int Height;

    [NativeDisableParallelForRestriction]
    public ComponentLookup<BuildingHealth> BuildingHealthLookup;

    [NativeDisableParallelForRestriction]
    public ComponentLookup<SoldierHealth> SoldierHealthLookup;

    [ReadOnly] public ComponentLookup<DeadBuildingTag> DeadTagLookup;
    [ReadOnly] public ComponentLookup<MainBaseTag> MainBaseLookup;

    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public ComponentLookup<LocalTransform> TransformLookup;

    public EntityCommandBuffer.ParallelWriter Ecb;

    enum TargetType { None, Building, Soldier }

    public void Execute(Entity zombieEntity, [EntityIndexInQuery] int sortKey, ref LocalTransform transform, ref MoveSpeed speed, in ZombieHealth zombieHealth)
    {
        int x = (int)math.floor(transform.Position.x);
        int z = (int)math.floor(transform.Position.z);

        Entity bestTarget = Entity.Null;
        TargetType targetType = TargetType.None;
        float minTargetDistSq = float.MaxValue;
        float3 bestTargetPos = float3.zero;

        // 1. 感知阶段
        int searchRange = 4;
        if (zombieHealth.Value < zombieHealth.Max) searchRange = 10;

        for (int xOffset = -searchRange; xOffset <= searchRange; xOffset++)
        {
            for (int zOffset = -searchRange; zOffset <= searchRange; zOffset++)
            {
                int nx = x + xOffset;
                int nz = z + zOffset;

                if (nx < 0 || nx >= Width || nz < 0 || nz >= Height) continue;

                int index = nz * Width + nx;

                // --- A. 检查建筑 ---
                if (BuildingMap.TryGetValue(index, out Entity buildingEntity))
                {
                    if (BuildingHealthLookup.HasComponent(buildingEntity) &&
                        !DeadTagLookup.HasComponent(buildingEntity) &&
                        TransformLookup.HasComponent(buildingEntity))
                    {
                        float3 targetCenter = TransformLookup[buildingEntity].Position;
                        float distSq = math.distancesq(transform.Position, targetCenter);

                        if (distSq < minTargetDistSq)
                        {
                            minTargetDistSq = distSq;
                            bestTarget = buildingEntity;
                            bestTargetPos = targetCenter;
                            targetType = TargetType.Building;
                        }
                    }
                }

                // --- B. 检查士兵 ---
                if (SoldierMap.IsCreated && SoldierMap.TryGetFirstValue(index, out Entity soldierEntity, out var it))
                {
                    do
                    {
                        if (SoldierHealthLookup.HasComponent(soldierEntity) && TransformLookup.HasComponent(soldierEntity))
                        {
                            float3 targetCenter = TransformLookup[soldierEntity].Position;
                            float distSq = math.distancesq(transform.Position, targetCenter);

                            if (distSq < minTargetDistSq)
                            {
                                minTargetDistSq = distSq;
                                bestTarget = soldierEntity;
                                bestTargetPos = targetCenter;
                                targetType = TargetType.Soldier;
                            }
                        }
                    } while (SoldierMap.TryGetNextValue(out soldierEntity, ref it));
                }
            }
        }

        // 2. 决策阶段
        if (bestTarget != Entity.Null)
        {
            // 基础攻击距离 (1.5米平方 = 2.25)
            float attackRangeSq = 2.25f;
            bool isHQ = false;

            if (targetType == TargetType.Building && MainBaseLookup.HasComponent(bestTarget))
            {
                // [设置] HQ 攻击距离：4米 (4x4 = 16)
                attackRangeSq = 16.0f;
                isHQ = true;
            }

            if (targetType == TargetType.Soldier) attackRangeSq = 3.0f;

            if (minTargetDistSq <= attackRangeSq)
            {
                // --- 攻击 ---
                Attack(sortKey, bestTarget, targetType, DeltaTime);
                speed.Value = 0;
            }
            else
            {
                // --- 追击 ---
                float chaseSpeed = 6f;
                float3 dir = math.normalizesafe(bestTargetPos - transform.Position);
                float3 nextPos = transform.Position + dir * chaseSpeed * DeltaTime;

                int nextX = (int)math.floor(nextPos.x);
                int nextZ = (int)math.floor(nextPos.z);

                bool canChase = true;
                bool hitTarget = false;

                if (nextX >= 0 && nextX < Width && nextZ >= 0 && nextZ < Height)
                {
                    int nextIndex = nextZ * Width + nextX;
                    var terrainType = MapData[nextIndex].TerrainType;

                    if (terrainType == 0) canChase = false;
                    else if (terrainType == 2)
                    {
                        if (BuildingMap.TryGetValue(nextIndex, out Entity obstacleEntity))
                        {
                            if (obstacleEntity == bestTarget || isHQ)
                            {
                                canChase = false;
                                hitTarget = true;
                            }
                            else canChase = false;
                        }
                        else canChase = false;
                    }
                }

                if (hitTarget)
                {
                    // 撞到了 HQ 模型，强制攻击
                    // 必须在合理距离内 (6米 = 36)
                    if (minTargetDistSq <= 36.0f)
                    {
                        Attack(sortKey, bestTarget, targetType, DeltaTime);
                    }
                    speed.Value = 0;
                }
                else if (canChase)
                {
                    transform.Position = nextPos;
                    float angle = math.atan2(dir.x, dir.z);
                    transform.Rotation = quaternion.RotateY(angle);
                    speed.Value = 0;
                }
                else
                {
                    speed.Value = 5f;
                }
            }
        }
        else
        {
            speed.Value = 5f;
        }
    }

    private void Attack(int sortKey, Entity target, TargetType type, float dt)
    {
        float damage = 50f * dt;

        if (type == TargetType.Building)
        {
            var health = BuildingHealthLookup[target];
            health.Value -= damage;
            BuildingHealthLookup[target] = health;

            if (health.Value <= 0)
            {
                Ecb.AddComponent<DeadBuildingTag>(sortKey, target);
            }
        }
        else if (type == TargetType.Soldier)
        {
            var health = SoldierHealthLookup[target];
            health.Value -= damage;
            SoldierHealthLookup[target] = health;

            if (health.Value <= 0)
            {
                Ecb.DestroyEntity(sortKey, target);
            }
        }
    }
}