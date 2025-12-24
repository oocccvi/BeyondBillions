using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

// 共享数据组件
public struct SoldierSpatialMap : IComponentData
{
    public NativeParallelMultiHashMap<int, Entity> Map;
}

[UpdateBefore(typeof(ZombieAttackSystem))] // 必须在僵尸攻击前更新
[BurstCompile]
public partial struct SoldierSpatialSystem : ISystem
{
    private NativeParallelMultiHashMap<int, Entity> _soldierMap;
    private Entity _singletonEntity;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SoldierTag>();
        _soldierMap = new NativeParallelMultiHashMap<int, Entity>(10000, Allocator.Persistent);
        _singletonEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(_singletonEntity, new SoldierSpatialMap { Map = _soldierMap });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_soldierMap.IsCreated) _soldierMap.Dispose();
    }

    // [关键修复] 移除了这里的 [BurstCompile]，允许访问 MapGenerator.Instance
    public void OnUpdate(ref SystemState state)
    {
        _soldierMap.Clear();

        // 这里的 MapGenerator.Instance 是托管对象，Burst 不支持，所以 OnUpdate 不能 Burst 编译
        if (MapGenerator.Instance == null) return;

        SystemAPI.SetComponent(_singletonEntity, new SoldierSpatialMap { Map = _soldierMap });

        var job = new PopulateSoldierMapJob
        {
            Width = MapGenerator.Instance.width,
            MapWriter = _soldierMap.AsParallelWriter()
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile] // Job 依然可以 Burst 编译，保持高性能
    public partial struct PopulateSoldierMapJob : IJobEntity
    {
        public int Width;
        public NativeParallelMultiHashMap<int, Entity>.ParallelWriter MapWriter;

        public void Execute(Entity entity, in LocalTransform transform, in SoldierTag tag)
        {
            int x = (int)math.floor(transform.Position.x);
            int z = (int)math.floor(transform.Position.z);
            int index = z * Width + x;
            MapWriter.Add(index, entity);
        }
    }
}