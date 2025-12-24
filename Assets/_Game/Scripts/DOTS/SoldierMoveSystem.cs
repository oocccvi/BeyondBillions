using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

[UpdateAfter(typeof(SoldierBehaviorSystem))]
[UpdateAfter(typeof(SoldierSpatialSystem))]
[BurstCompile]
public partial struct SoldierMoveSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        float dt = SystemAPI.Time.DeltaTime;
        var mapData = MapGenerator.Instance.MapData;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;

        NativeParallelMultiHashMap<int, Entity> soldierMap = default;
        bool hasMap = SystemAPI.HasSingleton<SoldierSpatialMap>();
        if (hasMap) soldierMap = SystemAPI.GetSingleton<SoldierSpatialMap>().Map;

        new SoldierMoveJob
        {
            DeltaTime = dt,
            MapData = mapData,
            Width = width,
            Height = height,
            SoldierMap = soldierMap,
            HasMap = hasMap,
            OtherTransforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
            FlowFieldDataLookup = SystemAPI.GetComponentLookup<LocalFlowFieldData>(true),
            FlowFieldBufferLookup = SystemAPI.GetBufferLookup<LocalFlowFieldBuffer>(true)
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct SoldierMoveJob : IJobEntity
{
    public float DeltaTime;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
    public int Width;
    public int Height;

    [ReadOnly] public NativeParallelMultiHashMap<int, Entity> SoldierMap;
    public bool HasMap;

    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public ComponentLookup<LocalTransform> OtherTransforms;

    [ReadOnly] public ComponentLookup<LocalFlowFieldData> FlowFieldDataLookup;
    [ReadOnly] public BufferLookup<LocalFlowFieldBuffer> FlowFieldBufferLookup;

    public void Execute(Entity entity, ref LocalTransform transform, RefRO<MoveSpeed> speed, ref SoldierState state)
    {
        if (!state.IsMoving) return;

        // [新增] 地形减速逻辑
        int cx = (int)math.floor(transform.Position.x);
        int cz = (int)math.floor(transform.Position.z);
        float terrainModifier = 1.0f;

        if (cx >= 0 && cx < Width && cz >= 0 && cz < Height)
        {
            byte type = MapData[cz * Width + cx].TerrainType;
            if (type == 3) terrainModifier = 1.2f;
            else if (type == 5) terrainModifier = 0.7f;
            else if (type == 6) terrainModifier = 0.4f;
        }

        float currentSpeed = speed.ValueRO.Value * terrainModifier;
        if (state.Command == SoldierCommand.Sprint) currentSpeed *= 2.0f;

        float3 currentPos = transform.Position;
        float3 targetPos = state.TargetPosition;
        targetPos.y = currentPos.y;

        // 1. 计算期望方向
        float3 desiredDir = float3.zero;
        bool hasFlowField = false;

        if (state.CurrentFlowFieldEntity != Entity.Null && FlowFieldDataLookup.HasComponent(state.CurrentFlowFieldEntity))
        {
            var ffData = FlowFieldDataLookup[state.CurrentFlowFieldEntity];
            var ffBuffer = FlowFieldBufferLookup[state.CurrentFlowFieldEntity];

            int localX = (int)math.floor(currentPos.x) - ffData.Offset.x;
            int localZ = (int)math.floor(currentPos.z) - ffData.Offset.y;

            if (localX >= 0 && localX < ffData.Size.x && localZ >= 0 && localZ < ffData.Size.y)
            {
                int idx = localZ * ffData.Size.x + localX;
                if (idx < ffBuffer.Length)
                {
                    float2 flow2D = ffBuffer[idx].Vector;
                    if (math.lengthsq(flow2D) > 0.001f)
                    {
                        desiredDir = new float3(flow2D.x, 0, flow2D.y);
                        hasFlowField = true;
                    }
                }
            }
        }

        if (!hasFlowField)
        {
            float distSq = math.distancesq(currentPos, targetPos);
            float stopDistSq = state.StopDistance * state.StopDistance;

            if (distSq <= stopDistSq)
            {
                if (state.Command == SoldierCommand.Move ||
                    state.Command == SoldierCommand.AttackMove ||
                    state.Command == SoldierCommand.Sprint)
                {
                    state.Command = SoldierCommand.Idle;
                    state.IsMoving = false;
                }
                return;
            }
            desiredDir = math.normalizesafe(targetPos - currentPos);
        }

        // 2. 排斥力
        float3 separation = float3.zero;
        if (HasMap)
        {
            int index = cz * Width + cx;
            int count = 0;
            if (SoldierMap.TryGetFirstValue(index, out Entity neighbor, out var it))
            {
                do
                {
                    if (neighbor == entity) continue;
                    if (OtherTransforms.HasComponent(neighbor))
                    {
                        float3 otherPos = OtherTransforms[neighbor].Position;
                        float dist = math.distance(currentPos, otherPos);

                        if (dist < 0.6f && dist > 0.001f)
                        {
                            float3 push = currentPos - otherPos;
                            separation += math.normalizesafe(push) / dist;
                            count++;
                        }
                    }
                } while (SoldierMap.TryGetNextValue(out neighbor, ref it));
            }

            if (count > 0)
            {
                desiredDir += separation * 1.5f;
                desiredDir = math.normalizesafe(desiredDir);
            }
        }

        // 3. 移动
        float moveDist = currentSpeed * DeltaTime;
        float nextX = currentPos.x + desiredDir.x * moveDist;
        if (IsWalkable(nextX, currentPos.z)) transform.Position.x = nextX;

        float nextZ = currentPos.z + desiredDir.z * moveDist;
        if (IsWalkable(transform.Position.x, nextZ)) transform.Position.z = nextZ;

        if (math.lengthsq(desiredDir) > 0.001f)
        {
            float angle = math.atan2(desiredDir.x, desiredDir.z);
            transform.Rotation = math.slerp(transform.Rotation, quaternion.RotateY(angle), DeltaTime * 15f);
        }
    }

    // [核心修复] 允许森林(5)、沼泽(6)、矿脉(7)通行
    private bool IsWalkable(float x, float z)
    {
        int ix = (int)math.floor(x);
        int iz = (int)math.floor(z);

        if (ix < 0 || ix >= Width || iz < 0 || iz >= Height) return false;

        int index = iz * Width + ix;
        byte type = MapData[index].TerrainType;

        if (type == 0 || type == 2 || type == 4) return false;

        return true;
    }
}