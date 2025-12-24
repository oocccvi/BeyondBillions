using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[UpdateBefore(typeof(ZombieMoveSystem))]
public partial struct ZombieWanderSystem : ISystem
{
    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null) return;

        float dt = SystemAPI.Time.DeltaTime;
        var mapData = MapGenerator.Instance.MapData;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;

        uint seed = (uint)System.Diagnostics.Stopwatch.GetTimestamp();

        bool hasHQ = false;
        float2 hqPosition = float2.zero;

        if (BuildingManager.Instance != null && BuildingManager.Instance.hasPlacedHQ)
        {
            hasHQ = true;
            if (FlowFieldController.Instance != null)
            {
                var target = FlowFieldController.Instance.targetPosition;
                hqPosition = new float2(target.x, target.y);
            }
            else
            {
                hqPosition = new float2(width / 2f, height / 2f);
            }
        }

        new ZombieWanderJob
        {
            DeltaTime = dt,
            MapData = mapData,
            Width = width,
            Height = height,
            HQPosition = hqPosition,
            Seed = seed,
            AggroRadius = 20f,
            HasHQ = hasHQ
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct ZombieWanderJob : IJobEntity
{
    public float DeltaTime;
    [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
    public int Width;
    public int Height;
    public float2 HQPosition;
    public uint Seed;
    public float AggroRadius;
    public bool HasHQ;

    public void Execute(ref LocalTransform transform, ref MoveSpeed speed, ref ZombieState state, [EntityIndexInQuery] int entityIndex)
    {
        if (state.Behavior != ZombieBehavior.Wander) return;

        // 1. 激怒检测
        if (HasHQ)
        {
            float distToHQ = math.distance(new float2(transform.Position.x, transform.Position.z), HQPosition);
            if (distToHQ < AggroRadius)
            {
                state.Behavior = ZombieBehavior.Rush;
                speed.Value *= 2.0f; // 激怒加速
                return;
            }
        }

        // 2. 计时器
        state.Timer -= DeltaTime;
        if (state.Timer <= 0)
        {
            var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex);
            float2 newDir = random.NextFloat2Direction();
            state.WanderDirection = new float3(newDir.x, 0, newDir.y);
            state.Timer = random.NextFloat(2f, 5f);
        }

        // [新增] 地形减速逻辑
        // 获取当前脚下的地形类型
        int cx = (int)math.floor(transform.Position.x);
        int cz = (int)math.floor(transform.Position.z);
        float terrainModifier = 1.0f;

        if (cx >= 0 && cx < Width && cz >= 0 && cz < Height)
        {
            byte currentType = MapData[cz * Width + cx].TerrainType;
            // 根据地形调整速度系数
            if (currentType == 3) terrainModifier = 1.2f;      // 道路加速
            else if (currentType == 5) terrainModifier = 0.6f; // 森林减速
            else if (currentType == 6) terrainModifier = 0.4f; // 沼泽严重减速
        }

        // 3. 移动计算 (应用减速)
        float3 nextPos = transform.Position + state.WanderDirection * speed.Value * terrainModifier * DeltaTime;

        // 4. 碰撞检测
        int nx = (int)math.floor(nextPos.x);
        int nz = (int)math.floor(nextPos.z);
        bool hitWall = false;

        if (nx < 0 || nx >= Width || nz < 0 || nz >= Height) hitWall = true;
        else
        {
            int index = nz * Width + nx;
            byte nextType = MapData[index].TerrainType;

            // [核心修复] 放宽通行条件
            // 允许通过: 1(平原), 3(道路), 5(森林), 6(沼泽), 7(矿脉)
            // 阻挡: 0(水), 2(山), 4(废墟)
            if (nextType == 0 || nextType == 2 || nextType == 4)
            {
                hitWall = true;
            }
        }

        if (hitWall)
        {
            // 撞墙后随机转向，防止在狭窄区域鬼畜
            var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 3 + 1);
            float2 newDir = random.NextFloat2Direction();

            state.WanderDirection = new float3(newDir.x, 0, newDir.y);
            state.Timer = random.NextFloat(0.5f, 1.5f);
        }
        else
        {
            transform.Position = nextPos;

            if (math.lengthsq(state.WanderDirection) > 0.001f)
            {
                float angle = math.atan2(state.WanderDirection.x, state.WanderDirection.z);
                transform.Rotation = quaternion.RotateY(angle);
            }
        }
    }
}