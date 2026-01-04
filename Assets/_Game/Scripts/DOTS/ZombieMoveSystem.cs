using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe; // [必须引入] 包含 NativeDisableContainerSafetyRestriction

[UpdateAfter(typeof(ZombieAttackSystem))]
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
        if (MapGenerator.Instance == null) return;

        NativeArray<float2> flowDirections = default;
        bool hasFlowField = false;

        // 安全获取流场数据
        if (FlowFieldController.Instance != null && FlowFieldController.Instance.FlowDirections.IsCreated)
        {
            flowDirections = FlowFieldController.Instance.FlowDirections;
            hasFlowField = true;
        }

        var spatialMap = SystemAPI.GetSingleton<ZombieSpatialMap>();
        var mapData = MapGenerator.Instance.MapData;
        int mapWidth = MapGenerator.Instance.width;
        int mapHeight = MapGenerator.Instance.height;

        state.Dependency = new ZombieMoveJob
        {
            DeltaTime = dt,
            FlowField = flowDirections,
            HasFlowField = hasFlowField,
            ZombieMap = spatialMap.Map,
            MapData = mapData,
            Width = mapWidth,
            Height = mapHeight,
            OtherTransforms = SystemAPI.GetComponentLookup<LocalTransform>(true)
        }.ScheduleParallel(state.Dependency);
    }
}

[BurstCompile]
public partial struct ZombieMoveJob : IJobEntity
{
    public float DeltaTime;

    // [关键修复] 允许此 NativeArray 为空/未分配
    // 这样当流场还没计算好时，传入 default 即使是无效的，Job 也不会报错
    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public NativeArray<float2> FlowField;

    public bool HasFlowField;

    [ReadOnly] public NativeParallelMultiHashMap<int, Entity> ZombieMap;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;

    [ReadOnly]
    [NativeDisableContainerSafetyRestriction]
    public ComponentLookup<LocalTransform> OtherTransforms;

    public int Width;
    public int Height;

    public void Execute(Entity entity, ref LocalTransform transform, in MoveSpeed speed, in ZombieTag tag, in ZombieState state)
    {
        // 1. 根据状态决定 "期望方向" (Desired Direction)
        float2 desiredDir = float2.zero;
        bool wantsToMove = false;

        if (state.Behavior == ZombieBehavior.Rush)
        {
            // 只有当流场数据有效时，才执行 Rush 逻辑
            if (HasFlowField)
            {
                desiredDir = GetFlowDirection(transform.Position.x, transform.Position.z);
                wantsToMove = true;
            }
            // 如果没有流场（HasFlowField=false），Rush 状态的僵尸会原地待命，等待流场生成
        }
        else if (state.Behavior == ZombieBehavior.Chase)
        {
            // 追击模式不需要流场，直接冲向目标
            float3 targetDiff = state.TargetPosition - transform.Position;
            desiredDir = math.normalizesafe(new float2(targetDiff.x, targetDiff.z));
            wantsToMove = true;
        }
        else if (state.Behavior == ZombieBehavior.Wander)
        {
            desiredDir = new float2(state.WanderDirection.x, state.WanderDirection.z);
            wantsToMove = true;
        }
        else if (state.Behavior == ZombieBehavior.Attack)
        {
            // 攻击时保持朝向目标，但不产生移动意愿
            float3 targetDiff = state.TargetPosition - transform.Position;
            desiredDir = math.normalizesafe(new float2(targetDiff.x, targetDiff.z));
            wantsToMove = false;
        }

        // 2. 计算避障力 (Separation)
        float2 separation = CalculateSeparation(transform.Position, entity);

        // 3. 混合方向
        float2 finalMoveDir = float2.zero;

        if (wantsToMove)
        {
            bool isVacuum = math.lengthsq(desiredDir) < 0.001f;
            float sepWeight = isVacuum ? 3.0f : 1.5f;
            finalMoveDir = desiredDir + separation * sepWeight;
            finalMoveDir = math.normalizesafe(finalMoveDir);
        }
        else
        {
            // 如果不想移动(比如Attack)，但避障力很大(被挤着了)，还是稍微动一下
            if (math.lengthsq(separation) > 0.1f)
            {
                finalMoveDir = separation;
            }
        }

        // 4. 执行移动
        if (math.lengthsq(finalMoveDir) > 0.001f)
        {
            float terrainMod = GetTerrainSpeedModifier(transform.Position);
            float actualSpeed = speed.Value * terrainMod;
            if (!wantsToMove) actualSpeed *= 0.3f; // 被挤动时速度慢点

            float3 nextPos = transform.Position;
            float3 moveStep = new float3(finalMoveDir.x, 0, finalMoveDir.y) * actualSpeed * DeltaTime;

            if (IsWalkable(nextPos.x + moveStep.x, nextPos.z)) nextPos.x += moveStep.x;
            if (IsWalkable(nextPos.x, nextPos.z + moveStep.z)) nextPos.z += moveStep.z;

            transform.Position = nextPos;
        }

        // 5. 执行旋转
        float2 lookDir = (state.Behavior == ZombieBehavior.Attack) ? desiredDir : finalMoveDir;
        if (math.lengthsq(lookDir) > 0.01f)
        {
            float targetAngle = math.atan2(lookDir.x, lookDir.y);
            transform.Rotation = math.slerp(transform.Rotation, quaternion.RotateY(targetAngle), DeltaTime * 12f);
        }
    }

    private float2 CalculateSeparation(float3 currentPos, Entity me)
    {
        float2 sep = float2.zero;
        int count = 0;
        int maxN = 6;

        int cx = (int)math.floor(currentPos.x);
        int cz = (int)math.floor(currentPos.z);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                int idx = (cz + dz) * Width + (cx + dx);
                if (ZombieMap.TryGetFirstValue(idx, out Entity neighbor, out var it))
                {
                    do
                    {
                        if (neighbor == me) continue;
                        if (OtherTransforms.HasComponent(neighbor))
                        {
                            float3 nPos = OtherTransforms[neighbor].Position;
                            float distSq = math.distancesq(currentPos, nPos);
                            if (distSq < 0.49f && distSq > 0.0001f)
                            {
                                float2 push = new float2(currentPos.x - nPos.x, currentPos.z - nPos.z);
                                sep += math.normalizesafe(push) / (distSq + 0.05f);
                                count++;
                            }
                        }
                    } while (count < maxN && ZombieMap.TryGetNextValue(out neighbor, ref it));
                }
                if (count >= maxN) return sep;
            }
        }
        return sep;
    }

    private float2 GetFlowDirection(float x, float z)
    {
        // 再次检查防止越界访问 (双重保险)
        if (!FlowField.IsCreated) return float2.zero;

        float u = x - 0.5f; float v = z - 0.5f;
        int x0 = (int)math.floor(u); int z0 = (int)math.floor(v);
        float wX = u - x0; float wZ = v - z0;
        x0 = math.clamp(x0, 0, Width - 1); int x1 = math.clamp(x0 + 1, 0, Width - 1);
        z0 = math.clamp(z0, 0, Height - 1); int z1 = math.clamp(z0 + 1, 0, Height - 1);

        float2 v00 = SafeGetFlow(x0, z0); float2 v10 = SafeGetFlow(x1, z0);
        float2 v01 = SafeGetFlow(x0, z1); float2 v11 = SafeGetFlow(x1, z1);
        return math.normalizesafe(math.lerp(math.lerp(v00, v10, wX), math.lerp(v01, v11, wX), wZ));
    }

    private float2 SafeGetFlow(int x, int y)
    {
        int i = y * Width + x;
        // 确保数组已创建且索引有效
        if (FlowField.IsCreated && i >= 0 && i < FlowField.Length) return FlowField[i];
        return float2.zero;
    }

    private float GetTerrainSpeedModifier(float3 pos)
    {
        int i = (int)math.floor(pos.z) * Width + (int)math.floor(pos.x);
        if (i < 0 || i >= MapData.Length) return 1f;
        byte t = MapData[i].TerrainType;
        if (t == 8) return 1.3f;
        if (t == 4) return 0.8f;
        return 1f;
    }

    private bool IsWalkable(float x, float z)
    {
        int ix = (int)math.floor(x); int iz = (int)math.floor(z);
        if (ix < 0 || ix >= Width || iz < 0 || iz >= Height) return false;
        byte t = MapData[iz * Width + ix].TerrainType;
        return !(t <= 1 || t == 6 || t == 7 || t == 9);
    }
}