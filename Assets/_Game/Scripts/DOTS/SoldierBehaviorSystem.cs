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
        if (state.Command == SoldierCommand.Idle)
        {
            state.IsMoving = false;
            return;
        }

        bool enemyInRange = false;
        if (state.Command == SoldierCommand.AttackMove ||
            state.Command == SoldierCommand.Patrol ||
            state.Command == SoldierCommand.Scout ||
            state.Command == SoldierCommand.Hunt)
        {
            int searchRange = 3; // 索敌范围
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
                                if (distSq < 324f) // 18米射程的平方
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
            state.IsMoving = false;
        }
        else
        {
            state.IsMoving = true;

            float distToTarget = math.distance(transform.Position, state.TargetPosition);
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
                    var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 100);

                    for (int i = 0; i < 15; i++)
                    {
                        int rx = random.NextInt(0, Width);
                        int ry = random.NextInt(0, Height);
                        int index = ry * Width + rx;
                        byte t = MapData[index].TerrainType;

                        // [修复] 目标点必须是 平地(3) 或 道路(8) 或 矿(10)
                        if (t == 3 || t == 8 || t == 10)
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