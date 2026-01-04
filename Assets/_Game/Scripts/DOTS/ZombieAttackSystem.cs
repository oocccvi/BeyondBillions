using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

[UpdateBefore(typeof(ZombieMoveSystem))]
public partial struct ZombieAttackSystem : ISystem
{
    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.BuildingMap.IsCreated) return;

        var buildingMap = MapGenerator.Instance.BuildingMap;
        var mapData = MapGenerator.Instance.MapData;

        NativeParallelMultiHashMap<int, Entity> soldierMap = default;
        if (SystemAPI.HasSingleton<SoldierSpatialMap>())
        {
            soldierMap = SystemAPI.GetSingleton<SoldierSpatialMap>().Map;
        }

        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();

        // [修复] 必须将 JobHandle 赋值给 state.Dependency，形成依赖链
        state.Dependency = new ZombieAttackJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            BuildingMap = buildingMap,
            SoldierMap = soldierMap,
            MapData = mapData,
            Width = MapGenerator.Instance.width,
            Height = MapGenerator.Instance.height,
            BuildingHealthLookup = SystemAPI.GetComponentLookup<BuildingHealth>(false),
            SoldierHealthLookup = SystemAPI.GetComponentLookup<SoldierHealth>(false),
            DeadTagLookup = SystemAPI.GetComponentLookup<DeadBuildingTag>(true),
            MainBaseLookup = SystemAPI.GetComponentLookup<MainBaseTag>(true),
            TransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true),
            Ecb = ecb
        }.ScheduleParallel(state.Dependency);
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

    [NativeDisableParallelForRestriction] public ComponentLookup<BuildingHealth> BuildingHealthLookup;
    [NativeDisableParallelForRestriction] public ComponentLookup<SoldierHealth> SoldierHealthLookup;
    [ReadOnly] public ComponentLookup<DeadBuildingTag> DeadTagLookup;
    [ReadOnly] public ComponentLookup<MainBaseTag> MainBaseLookup;
    [ReadOnly] public ComponentLookup<LocalTransform> TransformLookup;

    public EntityCommandBuffer.ParallelWriter Ecb;

    enum TargetType { None, Building, Soldier }

    public void Execute(Entity zombieEntity, [EntityIndexInQuery] int sortKey, in LocalTransform transform, ref ZombieState state, in ZombieHealth zombieHealth)
    {
        if (state.AttackCooldown > 0)
        {
            state.AttackCooldown -= DeltaTime;
        }

        int x = (int)math.floor(transform.Position.x);
        int z = (int)math.floor(transform.Position.z);

        Entity bestTarget = Entity.Null;
        TargetType targetType = TargetType.None;
        float minTargetDistSq = float.MaxValue;
        float3 bestTargetPos = float3.zero;

        int searchRange = (zombieHealth.Value < zombieHealth.Max) ? 8 : 4;

        for (int xOffset = -searchRange; xOffset <= searchRange; xOffset++)
        {
            for (int zOffset = -searchRange; zOffset <= searchRange; zOffset++)
            {
                int nx = x + xOffset;
                int nz = z + zOffset;
                if (nx < 0 || nx >= Width || nz < 0 || nz >= Height) continue;

                int index = nz * Width + nx;

                if (BuildingMap.TryGetValue(index, out Entity buildingEntity))
                {
                    if (BuildingHealthLookup.HasComponent(buildingEntity) && !DeadTagLookup.HasComponent(buildingEntity))
                    {
                        float3 tPos = new float3(nx + 0.5f, 0, nz + 0.5f);
                        float distSq = math.distancesq(transform.Position, tPos);
                        if (distSq < minTargetDistSq)
                        {
                            minTargetDistSq = distSq;
                            bestTarget = buildingEntity;
                            bestTargetPos = tPos;
                            targetType = TargetType.Building;
                        }
                    }
                }

                if (SoldierMap.IsCreated && SoldierMap.TryGetFirstValue(index, out Entity soldierEntity, out var it))
                {
                    do
                    {
                        if (SoldierHealthLookup.HasComponent(soldierEntity) && TransformLookup.HasComponent(soldierEntity))
                        {
                            float3 tPos = TransformLookup[soldierEntity].Position;
                            float distSq = math.distancesq(transform.Position, tPos);
                            if (distSq < minTargetDistSq)
                            {
                                minTargetDistSq = distSq;
                                bestTarget = soldierEntity;
                                bestTargetPos = tPos;
                                targetType = TargetType.Soldier;
                            }
                        }
                    } while (SoldierMap.TryGetNextValue(out soldierEntity, ref it));
                }
            }
        }

        if (bestTarget != Entity.Null)
        {
            float attackRangeSq = 2.25f;
            if (targetType == TargetType.Building && MainBaseLookup.HasComponent(bestTarget)) attackRangeSq = 16.0f;
            if (targetType == TargetType.Soldier) attackRangeSq = 2.0f;

            if (minTargetDistSq <= attackRangeSq)
            {
                state.Behavior = ZombieBehavior.Attack;
                state.TargetPosition = bestTargetPos;

                if (state.AttackCooldown <= 0)
                {
                    ApplyDamage(sortKey, bestTarget, targetType);
                    state.AttackCooldown = 1.0f;
                }
            }
            else
            {
                state.Behavior = ZombieBehavior.Chase;
                state.TargetPosition = bestTargetPos;
            }
        }
        else
        {
            if (state.Behavior != ZombieBehavior.Wander)
            {
                state.Behavior = ZombieBehavior.Rush;
            }
        }
    }

    private void ApplyDamage(int sortKey, Entity target, TargetType type)
    {
        float damagePerHit = 20f;

        if (type == TargetType.Building)
        {
            var h = BuildingHealthLookup[target];
            h.Value -= damagePerHit;
            BuildingHealthLookup[target] = h;
            if (h.Value <= 0) Ecb.AddComponent<DeadBuildingTag>(sortKey, target);
        }
        else if (type == TargetType.Soldier)
        {
            var h = SoldierHealthLookup[target];
            h.Value -= damagePerHit;
            SoldierHealthLookup[target] = h;
            if (h.Value <= 0) Ecb.DestroyEntity(sortKey, target);
        }
    }
}