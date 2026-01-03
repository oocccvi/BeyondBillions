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

        // 简单的移动逻辑 (直接修改坐标，实际位移在 MoveSystem 或者这里做都可以，
        // 但通常 MoveSystem 会处理 Rush，Wander 往往需要自己的一点位移逻辑或复用 MoveSystem)
        // 注意：目前的架构似乎是 WanderSystem 只负责定方向，MoveSystem 负责实际移动？
        // 如果是这样，我们需要确保 MoveSystem 能读取 WanderDirection。
        // 但查看之前的 ZombieMoveSystem，它主要处理 Rush。
        // 为了确保 Wander 能动，我们在这里直接处理简单的游荡位移。

        float moveDist = 2.0f * DeltaTime; // 假设游荡速度
        float3 nextPos = transform.Position + state.WanderDirection * moveDist;

        // [核心修复] 移除地形判断，不做 IsWalkable 检查，直接走
        // 或者做一个极简的边界检查
        if (IsWalkable(nextPos.x, nextPos.z))
        {
            // 这里我们只更新方向让 MoveSystem 去处理？
            // 不，之前的 ZombieMoveSystem 似乎只处理 Rush。
            // 为了保证游荡生效，我们这里直接更新位置。
            transform.Position = nextPos;

            // 旋转朝向
            float angle = math.atan2(state.WanderDirection.x, state.WanderDirection.z);
            transform.Rotation = math.slerp(transform.Rotation, quaternion.RotateY(angle), DeltaTime * 5f);
        }
        else
        {
            // 撞墙（边界）了，立即换方向
            var random = new Unity.Mathematics.Random(Seed + (uint)entityIndex * 77);
            float2 dir = random.NextFloat2Direction();
            state.WanderDirection = new float3(dir.x, 0, dir.y);
            state.Timer = random.NextFloat(1f, 3f); // 重置计时
        }
    }

    private bool IsWalkable(float x, float z)
    {
        int ix = (int)math.floor(x);
        int iz = (int)math.floor(z);

        // 只检查地图边界，不再检查 TerrainType
        if (ix < 0 || ix >= Width || iz < 0 || iz >= Height) return false;

        return true;
    }
}