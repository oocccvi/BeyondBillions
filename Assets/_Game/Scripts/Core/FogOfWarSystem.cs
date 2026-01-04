using UnityEngine;
using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;

// 共享数据
// 0=未探索(有云), 2=已探索(无云)
// (状态 1 已废弃，因为不需要半透明层了)
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

    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
        if (_gridStatus.IsCreated) _gridStatus.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        // 游戏重开检测
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

                Vector2Int spawn = MapGenerator.Instance.PlayerSpawnPoint;
                int cx = spawn.x;
                int cz = spawn.y;
                int initRadius = 45;
                int rSq = initRadius * initRadius;

                for (int x = 0; x < w; x++)
                {
                    for (int z = 0; z < h; z++)
                    {
                        int index = z * w + x;
                        int distSq = (x - cx) * (x - cx) + (z - cz) * (z - cz);

                        if (distSq <= rSq) _gridStatus[index] = 2; // 初始可见
                        else _gridStatus[index] = 0; // 初始迷雾
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
        // [修改 1] 移除了 FogResetJob
        // 我们不再需要把“可见”降级为“半透明”，所以直接跳过重置步骤。
        // 一旦格子被 FogVisionJob 标记为 2，它就永远是 2。

        // A. 视野 Job (只负责开视野)
        var width = MapGenerator.Instance.width;
        var height = MapGenerator.Instance.height;

        var visionJob = new FogVisionJob
        {
            Grid = _gridStatus,
            Width = width,
            Height = height
        };
        state.Dependency = visionJob.ScheduleParallel(state.Dependency);

        state.Dependency.Complete();
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
                        // 直接标记为 2 (可见)
                        // 由于没有 ResetJob，这个 2 会永久保留
                        Grid[z * Width + x] = 2;
                    }
                }
            }
        }
    }
}