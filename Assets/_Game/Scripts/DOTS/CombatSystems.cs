using Unity.Entities;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

// 0. 共享数据
public struct ZombieSpatialMap : IComponentData
{
    public NativeParallelMultiHashMap<int, Entity> Map;
}

// 1. 空间哈希系统
[UpdateBefore(typeof(TurretSystem))]
[UpdateBefore(typeof(ProjectileSystem))]
[BurstCompile]
public partial struct ZombieSpatialSystem : ISystem
{
    private NativeParallelMultiHashMap<int, Entity> _zombieMap;
    private Entity _singletonEntity;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ZombieTag>();
        _zombieMap = new NativeParallelMultiHashMap<int, Entity>(100000, Allocator.Persistent);
        _singletonEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(_singletonEntity, new ZombieSpatialMap { Map = _zombieMap });
    }

    public void OnDestroy(ref SystemState state)
    {
        state.Dependency.Complete();
        if (_zombieMap.IsCreated) _zombieMap.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        // [新增] 修复 Race Condition: 强制等待所有依赖此 Map 的 Job (如 ZombieMoveJob) 完成
        // 否则 Clear() 会报错
        state.Dependency.Complete();

        _zombieMap.Clear();
        if (MapGenerator.Instance == null) return;

        SystemAPI.SetComponent(_singletonEntity, new ZombieSpatialMap { Map = _zombieMap });

        var job = new PopulateHashMapJob
        {
            Width = MapGenerator.Instance.width,
            MapWriter = _zombieMap.AsParallelWriter()
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct PopulateHashMapJob : IJobEntity
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

// 2. 防御塔系统
[UpdateAfter(typeof(ZombieSpatialSystem))]
[BurstCompile]
public partial struct TurretSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ZombieSpatialMap>();
        state.RequireForUpdate<Turret>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();
        var spatialMap = SystemAPI.GetSingleton<ZombieSpatialMap>();

        var job = new TurretShootJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            Ecb = ecb,
            ZombieMap = spatialMap.Map,
            MapData = MapGenerator.Instance.MapData,
            MapWidth = MapGenerator.Instance.width,
            MapHeight = MapGenerator.Instance.height,
            ZombieTransforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
            SoldierStateLookup = SystemAPI.GetComponentLookup<SoldierState>(true)
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public partial struct TurretShootJob : IJobEntity
    {
        public float DeltaTime;
        public EntityCommandBuffer.ParallelWriter Ecb;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> ZombieMap;
        [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
        [ReadOnly] public ComponentLookup<LocalTransform> ZombieTransforms;
        [ReadOnly] public ComponentLookup<SoldierState> SoldierStateLookup;
        public int MapWidth;
        public int MapHeight;

        public void Execute([EntityIndexInQuery] int sortKey, Entity entity, ref Turret turret, in LocalTransform transform)
        {
            if (SoldierStateLookup.HasComponent(entity))
            {
                var state = SoldierStateLookup[entity];
                if (state.Command == SoldierCommand.Sprint) return;
            }

            if (turret.Cooldown > 0)
            {
                turret.Cooldown -= DeltaTime;
                return;
            }

            Entity targetEntity = Entity.Null;
            float minDistanceSq = turret.Range * turret.Range;
            float3 turretPos = transform.Position;

            int tX = (int)math.floor(turretPos.x);
            int tZ = (int)math.floor(turretPos.z);

            int rangeInt = (int)math.ceil(turret.Range);

            for (int x = tX - rangeInt; x <= tX + rangeInt; x++)
            {
                for (int z = tZ - rangeInt; z <= tZ + rangeInt; z++)
                {
                    if (x < 0 || x >= MapWidth || z < 0 || z >= MapHeight) continue;

                    int cellIndex = z * MapWidth + x;
                    if (ZombieMap.TryGetFirstValue(cellIndex, out Entity zombie, out var it))
                    {
                        do
                        {
                            if (ZombieTransforms.HasComponent(zombie))
                            {
                                float3 zombiePos = ZombieTransforms[zombie].Position;
                                float distSq = math.distancesq(turretPos, zombiePos);
                                if (distSq < minDistanceSq)
                                {
                                    if (CheckLineOfSight(tX, tZ, (int)math.floor(zombiePos.x), (int)math.floor(zombiePos.z)))
                                    {
                                        minDistanceSq = distSq;
                                        targetEntity = zombie;
                                    }
                                }
                            }
                        } while (ZombieMap.TryGetNextValue(out zombie, ref it));
                    }
                }
            }

            if (targetEntity != Entity.Null)
            {
                turret.Cooldown = 1f / turret.FireRate;
                Entity projectile = Ecb.Instantiate(sortKey, turret.ProjectilePrefab);
                float3 spawnPos = turretPos + turret.MuzzleOffset;
                float3 targetPos = ZombieTransforms[targetEntity].Position;
                float3 dir = math.normalizesafe(targetPos - spawnPos);

                Ecb.SetComponent(sortKey, projectile, LocalTransform.FromPosition(spawnPos));
                Ecb.SetComponent(sortKey, projectile, new Projectile
                {
                    Velocity = dir * 20f,
                    Damage = 35f,
                    Lifetime = 2f,
                    Speed = 20f
                });
            }
        }

        private bool CheckLineOfSight(int x0, int y0, int x1, int y1)
        {
            int dx = math.abs(x1 - x0);
            int dy = math.abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int startX = x0; int startY = y0;

            while (true)
            {
                if (x0 != startX || y0 != startY)
                {
                    if (x0 == x1 && y0 == y1) return true;

                    int index = y0 * MapWidth + x0;
                    if (index >= 0 && index < MapData.Length)
                    {
                        if (MapData[index].TerrainType == 2) return false;
                    }
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
            return true;
        }
    }
}

// 3. 子弹系统
[UpdateAfter(typeof(TurretSystem))]
[BurstCompile]
public partial struct ProjectileSystem : ISystem
{
    private NativeQueue<int> _bountyQueue;

    public void OnCreate(ref SystemState state)
    {
        _bountyQueue = new NativeQueue<int>(Allocator.Persistent);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_bountyQueue.IsCreated) _bountyQueue.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (ResourceManager.Instance != null)
        {
            int totalBounty = 0;
            while (_bountyQueue.TryDequeue(out int amount)) totalBounty += amount;
            if (totalBounty > 0) ResourceManager.Instance.AddGold(totalBounty);
        }
        else _bountyQueue.Clear();

        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();

        if (!SystemAPI.HasSingleton<ZombieSpatialMap>()) return;
        var spatialMap = SystemAPI.GetSingleton<ZombieSpatialMap>();

        new ProjectileMoveJob
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            Ecb = ecb,
            ZombieMap = spatialMap.Map,
            MapData = MapGenerator.Instance.MapData,
            MapWidth = MapGenerator.Instance.width,
            MapHeight = MapGenerator.Instance.height,
            HealthLookup = SystemAPI.GetComponentLookup<ZombieHealth>(false),
            BountyQueue = _bountyQueue.AsParallelWriter()
        }.ScheduleParallel();
    }

    [BurstCompile]
    public partial struct ProjectileMoveJob : IJobEntity
    {
        public float DeltaTime;
        public EntityCommandBuffer.ParallelWriter Ecb;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> ZombieMap;
        [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
        public int MapWidth;
        public int MapHeight;

        [NativeDisableParallelForRestriction]
        public ComponentLookup<ZombieHealth> HealthLookup;
        [NativeDisableParallelForRestriction]
        public NativeQueue<int>.ParallelWriter BountyQueue;

        public void Execute([EntityIndexInQuery] int sortKey, Entity entity, ref LocalTransform transform, ref Projectile proj)
        {
            transform.Position += proj.Velocity * DeltaTime;
            proj.Lifetime -= DeltaTime;

            if (proj.Lifetime <= 0)
            {
                Ecb.DestroyEntity(sortKey, entity);
                return;
            }

            int x = (int)math.floor(transform.Position.x);
            int z = (int)math.floor(transform.Position.z);

            if (x >= 0 && x < MapWidth && z >= 0 && z < MapHeight)
            {
                int mapIndex = z * MapWidth + x;
                var cell = MapData[mapIndex];
                if (cell.TerrainType == 2 && cell.BuildingType == 0)
                {
                    Ecb.DestroyEntity(sortKey, entity);
                    return;
                }
            }

            int index = z * MapWidth + x;

            if (ZombieMap.TryGetFirstValue(index, out Entity zombie, out var it))
            {
                do
                {
                    if (HealthLookup.HasComponent(zombie))
                    {
                        var health = HealthLookup[zombie];
                        health.Value -= proj.Damage;
                        HealthLookup[zombie] = health;

                        if (health.Value <= 0)
                        {
                            Ecb.DestroyEntity(sortKey, zombie);
                            BountyQueue.Enqueue(5);
                        }

                        Ecb.DestroyEntity(sortKey, entity);
                        return;
                    }
                } while (ZombieMap.TryGetNextValue(out zombie, ref it));
            }
        }
    }
}