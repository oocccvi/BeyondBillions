using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[UpdateBefore(typeof(SoldierMoveSystem))]
[BurstCompile]
public partial struct SoldierBehaviorSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null) return;

        NativeParallelMultiHashMap<int, Entity> zombieMap = default;
        if (SystemAPI.HasSingleton<ZombieSpatialMap>())
        {
            zombieMap = SystemAPI.GetSingleton<ZombieSpatialMap>().Map;
        }
        else return;

        float dt = SystemAPI.Time.DeltaTime;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        uint seed = (uint)System.Diagnostics.Stopwatch.GetTimestamp();

        new SoldierBrainJob
        {
            DeltaTime = dt,
            ZombieMap = zombieMap,
            Width = width,
            Height = height,
            Seed = seed,
            MapData = MapGenerator.Instance.MapData,
            ZombieTransforms = SystemAPI.GetComponentLookup<LocalTransform>(true)
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct SoldierBrainJob : IJobEntity
{
    public float DeltaTime;
    [ReadOnly] public NativeParallelMultiHashMap<int, Entity> ZombieMap;
    public int Width;
    public int Height;
    public uint Seed;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
    [ReadOnly] public ComponentLookup<LocalTransform> ZombieTransforms;

    public void Execute(ref SoldierState state, in LocalTransform transform, [EntityIndexInQuery] int entityIndex)
    {
        // 1. 处理 Stop (Idle)
        // [修改] 移除了 Hold，因为 Hold 已经被 Hunt 替代，Hunt 需要移动
        if (state.Command == SoldierCommand.Idle)
        {
            state.IsMoving = false;
            return;
        }

        // 2. 处理索敌 (AttackMove, Patrol, Scout, Hunt)
        // Move (强制移动) 和 Sprint (急行军) 不索敌
        bool enemyInRange = false;
        if (state.Command == SoldierCommand.AttackMove ||
            state.Command == SoldierCommand.Patrol ||
            state.Command == SoldierCommand.Scout ||
            state.Command == SoldierCommand.Hunt) // [新增] Hunt 也要索敌
        {
            int searchRange = 3;
            float3 myPos = transform.Position;
            int cx = (int)math.floor(myPos.x);
            int cz = (int)math.floor(myPos.z);

            for (int x = cx - searchRange; x <= cx + searchRange; x++)
            {
                for (int z = cz - searchRange; z <= cz + searchRange; z++)
                {
                    if (x < 0 || x >= Width || z < 0 || z >= Height) continue;
                    int index = z * Width + x;

                    if (ZombieMap.TryGetFirstValue(index, out Entity zombie, out var it))
                    {
                        do
                        {
                            if (ZombieTransforms.HasComponent(zombie))
                            {
                                float distSq = math.distancesq(myPos, ZombieTransforms[zombie].Position);
                                if (distSq < 324f)
                                {
                                    enemyInRange = true;
                                    goto FoundEnemy;
                                }
                            }
                        } while (ZombieMap.TryGetNextValue(out zombie, ref it));
                    }
                }
            }
        }

    FoundEnemy:

        if (enemyInRange)
        {
            state.IsMoving = false; // 发现敌人，停下射击
        }
        else
        {
            state.IsMoving = true; // 没敌人，继续走

            // 3. 处理到达目标后的逻辑
            // 只有 Patrol, Scout, Hunt 需要自动更新目标
            float distToTarget = math.distance(transform.Position, state.TargetPosition);

            // 如果目标是 (0,0) (初始值)，或者已经到达，都需要找新目标
            bool needsNewTarget = distToTarget < 1.0f || math.lengthsq(state.TargetPosition) < 0.1f;

            if (needsNewTarget)
            {
                if (state.Command == SoldierCommand.Patrol)
                {
                    float3 temp = state.TargetPosition;
                    state.TargetPosition = state.PatrolStartPosition;
                    state.PatrolStartPosition = temp;
                }
                else if (state.Command == SoldierCommand.Scout || state.Command == SoldierCommand.Hunt)
                {
                    // [逻辑] 侦察/歼敌：随机找全图的一个平地去
                    // 这里可以优化为"找最近的未探索区域"，但全图随机对"歼敌"来说也够用了
                    var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 100);

                    for (int i = 0; i < 10; i++)
                    {
                        int rx = random.NextInt(0, Width);
                        int ry = random.NextInt(0, Height);
                        int index = ry * Width + rx;

                        if (MapData[index].TerrainType == 1)
                        {
                            state.TargetPosition = new float3(rx, 1f, ry);
                            break;
                        }
                    }
                }
            }
        }
    }
}