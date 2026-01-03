using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[UpdateBefore(typeof(ZombieMoveSystem))]
[BurstCompile]
public partial struct ZombieWanderSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ZombieTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null) return;

        float dt = SystemAPI.Time.DeltaTime;
        var mapData = MapGenerator.Instance.MapData;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        uint seed = (uint)System.Diagnostics.Stopwatch.GetTimestamp();

        new ZombieWanderJob
        {
            DeltaTime = dt,
            MapData = mapData,
            Width = width,
            Height = height,
            Seed = seed
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
    public uint Seed;

    public void Execute(ref ZombieState state, ref LocalTransform transform, [EntityIndexInQuery] int entityIndex)
    {
        if (state.Behavior != ZombieBehavior.Wander) return;

        // 计时器倒计时，时间到了换个方向
        state.Timer -= DeltaTime;
        if (state.Timer <= 0)
        {
            var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 33);
            state.Timer = random.NextFloat(2f, 6f);
            float2 dir = random.NextFloat2Direction();
            state.WanderDirection = new float3(dir.x, 0, dir.y);
        }

        // 简单的移动逻辑
        float moveDist = 2.0f * DeltaTime; // 假设游荡速度
        float3 nextPos = transform.Position + state.WanderDirection * moveDist;

        // [核心修复] 增加地形判断，防止游荡到阻挡区域
        // 之前只判断了边界，导致僵尸会走进水里或穿墙
        if (IsWalkable(nextPos.x, nextPos.z))
        {
            transform.Position = nextPos;

            // 旋转朝向
            float angle = math.atan2(state.WanderDirection.x, state.WanderDirection.z);
            transform.Rotation = math.slerp(transform.Rotation, quaternion.RotateY(angle), DeltaTime * 5f);
        }
        else
        {
            // 撞墙（边界或地形）了，立即换方向
            var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 77);
            float2 dir = random.NextFloat2Direction();
            // 重新随机一个方向
            state.WanderDirection = new float3(dir.x, 0, dir.y);
            state.Timer = random.NextFloat(1f, 3f); // 重置计时，稍微快一点再次尝试
        }
    }

    private bool IsWalkable(float x, float z)
    {
        int ix = (int)math.floor(x);
        int iz = (int)math.floor(z);

        // 1. 检查地图边界
        if (ix < 0 || ix >= Width || iz < 0 || iz >= Height) return false;

        // 2. 检查地形阻挡 (与 ZombieMoveSystem 逻辑保持一致)
        int index = iz * Width + ix;
        byte t = MapData[index].TerrainType;

        // 阻挡地形: 0(DeepWater), 1(Water), 6(Mountain/Wall), 7(Snow), 9(Ruins)
        if (t <= 1 || t == 6 || t == 7 || t == 9) return false;

        return true;
    }
}