using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

[UpdateAfter(typeof(SoldierBehaviorSystem))]
[UpdateAfter(typeof(ZombieSpatialSystem))]
[BurstCompile]
public partial struct ZombieMoveSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ZombieTag>();
        state.RequireForUpdate<ZombieSpatialMap>();
    }

    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
    }

    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        if (FlowFieldController.Instance == null || !FlowFieldController.Instance.FlowDirections.IsCreated)
            return;

        if (MapGenerator.Instance == null) return;

        NativeArray<float2> flowDirections = FlowFieldController.Instance.FlowDirections;
        var spatialMap = SystemAPI.GetSingleton<ZombieSpatialMap>();
        var mapData = MapGenerator.Instance.MapData;

        int mapWidth = MapGenerator.Instance.width;
        int mapHeight = MapGenerator.Instance.height;

        float2 targetPos = float2.zero;
        if (FlowFieldController.Instance != null)
        {
            var t = FlowFieldController.Instance.targetPosition;
            targetPos = new float2(t.x, t.y);
        }

        new ZombieMoveJob
        {
            DeltaTime = dt,
            FlowField = flowDirections,
            ZombieMap = spatialMap.Map,
            MapData = mapData,
            Width = mapWidth,
            Height = mapHeight,
            TargetPosition = targetPos,
            OtherTransforms = SystemAPI.GetComponentLookup<LocalTransform>(true)
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct ZombieMoveJob : IJobEntity
{
    public float DeltaTime;
    [ReadOnly] public NativeArray<float2> FlowField;
    [ReadOnly] public NativeParallelMultiHashMap<int, Entity> ZombieMap;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;

    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public ComponentLookup<LocalTransform> OtherTransforms;

    public int Width;
    public int Height;

    public float2 TargetPosition;

    public void Execute(Entity entity, ref LocalTransform transform, in MoveSpeed speed, in ZombieTag tag, in ZombieState state)
    {
        if (state.Behavior != ZombieBehavior.Rush) return;

        int x = (int)math.floor(transform.Position.x);
        int z = (int)math.floor(transform.Position.z);

        if (x >= 0 && x < Width && z >= 0 && z < Height)
        {
            int index = z * Width + x;

            // [修改] 地形减速逻辑
            float terrainModifier = 1.0f;
            byte type = MapData[index].TerrainType;

            // 保留一点特性：道路加速，但其他阻挡地形不再阻止移动
            if (type == 8) terrainModifier = 1.3f;
            else if (type == 4) terrainModifier = 0.8f;

            float actualSpeed = speed.Value * terrainModifier;

            // --- 1. 获取基础方向 ---
            float2 flowDir;
            {
                float u = transform.Position.x - 0.5f;
                float v = transform.Position.z - 0.5f;
                int x0 = (int)math.floor(u); int z0 = (int)math.floor(v);
                int x1 = x0 + 1; int z1 = z0 + 1;
                float wX = u - x0; float wZ = v - z0;
                x0 = math.clamp(x0, 0, Width - 1); z0 = math.clamp(z0, 0, Height - 1);
                x1 = math.clamp(x1, 0, Width - 1); z1 = math.clamp(z1, 0, Height - 1);

                int idx00 = z0 * Width + x0;
                int idx10 = z0 * Width + x1;
                int idx01 = z1 * Width + x0;
                int idx11 = z1 * Width + x1;

                float2 v00 = (idx00 < FlowField.Length) ? FlowField[idx00] : float2.zero;
                float2 v10 = (idx10 < FlowField.Length) ? FlowField[idx10] : float2.zero;
                float2 v01 = (idx01 < FlowField.Length) ? FlowField[idx01] : float2.zero;
                float2 v11 = (idx11 < FlowField.Length) ? FlowField[idx11] : float2.zero;

                float2 bottom = math.lerp(v00, v10, wX);
                float2 top = math.lerp(v01, v11, wX);
                flowDir = math.lerp(bottom, top, wZ);
                if (math.lengthsq(flowDir) > 0.001f) flowDir = math.normalizesafe(flowDir);
            }

            // --- 2. 侧翼包抄 ---
            float2 currentPos2D = new float2(transform.Position.x, transform.Position.z);
            float distToTargetSq = math.distancesq(currentPos2D, TargetPosition);

            if (distToTargetSq < 2500.0f && distToTargetSq > 9.0f)
            {
                var random = Unity.Mathematics.Random.CreateFromIndex((uint)entity.Index);
                float bias = random.NextFloat(-1f, 1f);
                float2 tangent = new float2(-flowDir.y, flowDir.x);
                flowDir += tangent * bias * 0.8f;
                flowDir = math.normalizesafe(flowDir);
            }

            // --- 3. 排斥力 ---
            float2 separation = float2.zero;
            int count = 0;

            if (ZombieMap.TryGetFirstValue(index, out Entity neighbor, out var it))
            {
                do
                {
                    if (neighbor == entity) continue;

                    if (OtherTransforms.HasComponent(neighbor))
                    {
                        float3 otherPos = OtherTransforms[neighbor].Position;
                        float distSq = math.distancesq(transform.Position, otherPos);

                        if (distSq < 0.25f && distSq > 0.0001f)
                        {
                            float2 push = new float2(transform.Position.x - otherPos.x, transform.Position.z - otherPos.z);
                            separation += math.normalizesafe(push) / (distSq + 0.1f);
                            count++;
                            if (count >= 4) break;
                        }
                    }
                } while (ZombieMap.TryGetNextValue(out neighbor, ref it));
            }

            // --- 4. 混合 ---
            float2 finalDir = flowDir;
            bool isVacuum = math.lengthsq(flowDir) < 0.001f;

            if (count > 0)
            {
                float sepWeight = isVacuum ? 5.0f : 1.5f;
                finalDir += separation * sepWeight;
                finalDir = math.normalizesafe(finalDir);
            }
            else if (isVacuum)
            {
                finalDir = float2.zero;
            }

            // --- 5. 移动 ---
            if (math.lengthsq(finalDir) > 0.001f)
            {
                float moveDist = actualSpeed * DeltaTime;
                float3 currentPos = transform.Position;

                float nextX = currentPos.x + finalDir.x * moveDist;
                // [修改] 直接移动，不再判断 IsWalkable，或者 IsWalkable 永远返回 true
                if (IsWalkable(nextX, currentPos.z))
                {
                    transform.Position.x = nextX;
                }

                float nextZ = currentPos.z + finalDir.y * moveDist;
                if (IsWalkable(transform.Position.x, nextZ))
                {
                    transform.Position.z = nextZ;
                }

                float angle = math.atan2(finalDir.x, finalDir.y);
                transform.Rotation = math.slerp(transform.Rotation, quaternion.RotateY(angle), DeltaTime * 10f);
            }
        }
    }

    // [核心修改] 永远返回 true，任何地形都可通行
    private bool IsWalkable(float x, float z)
    {
        int ix = (int)math.floor(x);
        int iz = (int)math.floor(z);

        if (ix < 0 || ix >= Width || iz < 0 || iz >= Height) return false;

        return true; // 移除所有阻挡判断
    }
}