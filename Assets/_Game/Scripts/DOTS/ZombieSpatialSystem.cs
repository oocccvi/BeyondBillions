using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

public struct ZombieSpatialMap : IComponentData
{
    public NativeParallelMultiHashMap<int, Entity> Map;
}

[UpdateBefore(typeof(ZombieMoveSystem))]
[BurstCompile]
public partial struct ZombieSpatialSystem : ISystem
{
    private NativeParallelMultiHashMap<int, Entity> _zombieMap;
    private Entity _singletonEntity;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ZombieTag>();
        _zombieMap = new NativeParallelMultiHashMap<int, Entity>(20000, Allocator.Persistent);
        _singletonEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(_singletonEntity, new ZombieSpatialMap { Map = _zombieMap });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_zombieMap.IsCreated) _zombieMap.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        // [核心修复] 在清空 Map 之前，必须确保所有读取该 Map 的 Job (如 ZombieMoveJob) 全部完成！
        // 因为我们没有通过 JobHandle 手动串联两个 System，所以这里必须强制等待。
        // state.Dependency.Complete() 通常可以解决 System 内部的依赖，但跨 System 时，
        // 如果出现 InvalidOperationException，最稳妥的方法是 CompleteAllTrackedJobs()。
        state.EntityManager.CompleteAllTrackedJobs();

        _zombieMap.Clear();

        if (MapGenerator.Instance == null) return;
        int width = MapGenerator.Instance.width;

        SystemAPI.SetComponent(_singletonEntity, new ZombieSpatialMap { Map = _zombieMap });

        var job = new PopulateZombieMapJob
        {
            Width = width,
            MapWriter = _zombieMap.AsParallelWriter()
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct PopulateZombieMapJob : IJobEntity
    {
        public int Width;
        public NativeParallelMultiHashMap<int, Entity>.ParallelWriter MapWriter;

        public void Execute(Entity entity, in LocalTransform transform, in ZombieTag tag)
        {
            int x = (int)math.floor(transform.Position.x);
            int z = (int)math.floor(transform.Position.z);
            int index = z * Width + x;
            MapWriter.Add(index, entity);
        }
    }
}