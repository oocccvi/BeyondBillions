using UnityEngine; // 必须引用以使用 Vector2Int
using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;

// 共享数据：迷雾地图状态
// 0=Unknown, 1=Explored(Fog), 2=Visible
public struct FogMapData : IComponentData
{
    public NativeArray<byte> GridStatus;
    public int Width;
    public int Height;
}

[BurstCompile]
public partial struct FogOfWarSystem : ISystem
{
    private NativeArray<byte> _gridStatus;
    private Entity _singletonEntity;

    public void OnCreate(ref SystemState state)
    {
    }

    public void OnDestroy(ref SystemState state)
    {
        // 销毁前确保 Job 完成，防止内存泄漏警告
        state.Dependency.Complete();
        if (_gridStatus.IsCreated) _gridStatus.Dispose();
    }

    // 主线程 Update
    public void OnUpdate(ref SystemState state)
    {
        // 检测重置信号：如果数据还在但实体没了（被 GameResultManager 删了），说明重开了
        if (_gridStatus.IsCreated && !state.EntityManager.Exists(_singletonEntity))
        {
            state.Dependency.Complete();
            _gridStatus.Dispose();
            _gridStatus = default;
        }

        // 1. 初始化
        if (!_gridStatus.IsCreated)
        {
            if (MapGenerator.Instance != null && MapGenerator.Instance.IsInitialized)
            {
                int w = MapGenerator.Instance.width;
                int h = MapGenerator.Instance.height;
                _gridStatus = new NativeArray<byte>(w * h, Allocator.Persistent);

                // [核心修复] 初始开视野位置跟随玩家出生点
                // 之前写死 cx = w/2, cz = h/2，导致迷雾只在地图中间打开
                Vector2Int spawn = MapGenerator.Instance.PlayerSpawnPoint;
                int cx = spawn.x;
                int cz = spawn.y;

                int initRadius = 45; // 初始视野半径
                int rSq = initRadius * initRadius;

                for (int x = 0; x < w; x++)
                {
                    for (int z = 0; z < h; z++)
                    {
                        int index = z * w + x;
                        int distSq = (x - cx) * (x - cx) + (z - cz) * (z - cz);

                        if (distSq <= rSq)
                        {
                            _gridStatus[index] = 2; // 可见
                        }
                        else
                        {
                            _gridStatus[index] = 0; // 未探索
                        }
                    }
                }

                _singletonEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(_singletonEntity, new FogMapData
                {
                    GridStatus = _gridStatus,
                    Width = w,
                    Height = h
                });
            }
            return;
        }

        // 2. 调度 Job
        // A. 降级 Job (可见 -> 迷雾)
        var resetJob = new FogResetJob { Grid = _gridStatus };
        state.Dependency = resetJob.Schedule(_gridStatus.Length, 64, state.Dependency);

        // B. 视野 Job (单位开视野)
        var width = MapGenerator.Instance.width;
        var height = MapGenerator.Instance.height;

        var visionJob = new FogVisionJob
        {
            Grid = _gridStatus,
            Width = width,
            Height = height
        };
        state.Dependency = visionJob.ScheduleParallel(state.Dependency);

        // 强制等待 Job 完成，以便 FogOfWarDisplay 读取数据
        state.Dependency.Complete();
    }
}

[BurstCompile]
public partial struct FogResetJob : IJobParallelFor
{
    public NativeArray<byte> Grid;

    public void Execute(int index)
    {
        // 如果当前是 Visible(2)，降级为 Explored(1)
        if (Grid[index] == 2)
        {
            Grid[index] = 1;
        }
    }
}

[BurstCompile]
public partial struct FogVisionJob : IJobEntity
{
    [NativeDisableParallelForRestriction]
    public NativeArray<byte> Grid;

    public int Width;
    public int Height;

    public void Execute(in LocalTransform transform, in SightRange sight)
    {
        int cx = (int)math.floor(transform.Position.x);
        int cz = (int)math.floor(transform.Position.z);
        int r = (int)math.ceil(sight.Value);
        int rSq = r * r;

        for (int x = cx - r; x <= cx + r; x++)
        {
            for (int z = cz - r; z <= cz + r; z++)
            {
                if (x >= 0 && x < Width && z >= 0 && z < Height)
                {
                    int distSq = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                    if (distSq <= rSq)
                    {
                        Grid[z * Width + x] = 2; // 标记为可见
                    }
                }
            }
        }
    }
}