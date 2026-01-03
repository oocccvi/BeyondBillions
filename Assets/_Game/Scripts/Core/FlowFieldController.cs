using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities; // [New] 需要引用 Entities 命名空间

public class FlowFieldController : MonoBehaviour
{
    public static FlowFieldController Instance { get; private set; }

    [Header("Debug")]
    public bool showDebugGizmos = false;
    public Vector2Int targetPosition;

    // --- Data ---
    public NativeArray<float2> FlowDirections;
    public NativeArray<byte> CostField;
    public NativeArray<ushort> IntegrationField;

    // [Fix] 添加 HasHQ 属性，满足 UnitSpawner 的调用需求
    public bool HasHQ { get; private set; }

    private int _width;
    private int _height;
    private bool _isInitialized = false;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void OnDestroy()
    {
        // [Critical Fix] 在释放 NativeArray 之前，必须确保所有可能正在读取它的 ECS Job 都已完成
        // 否则会报 "InvalidOperationException: The previously scheduled job ... reads from the Unity.Collections.NativeArray"
        if (World.DefaultGameObjectInjectionWorld != null)
        {
            World.DefaultGameObjectInjectionWorld.EntityManager.CompleteAllTrackedJobs();
        }

        if (FlowDirections.IsCreated) FlowDirections.Dispose();
        if (CostField.IsCreated) CostField.Dispose();
        if (IntegrationField.IsCreated) IntegrationField.Dispose();
    }

    public void Initialize(int width, int height)
    {
        _width = width;
        _height = height;

        if (FlowDirections.IsCreated) FlowDirections.Dispose();
        if (CostField.IsCreated) CostField.Dispose();
        if (IntegrationField.IsCreated) IntegrationField.Dispose();

        FlowDirections = new NativeArray<float2>(_width * _height, Allocator.Persistent);
        CostField = new NativeArray<byte>(_width * _height, Allocator.Persistent);
        IntegrationField = new NativeArray<ushort>(_width * _height, Allocator.Persistent);

        _isInitialized = true;
    }

    public void RegisterHQ(int x, int y)
    {
        // [Fix] 标记 HQ 已存在
        HasHQ = true;
        UpdateTargetPosition(x, y);
    }

    public void UpdateTargetPosition(int x, int y)
    {
        if (MapGenerator.Instance == null) return;
        if (!_isInitialized) Initialize(MapGenerator.Instance.width, MapGenerator.Instance.height);

        // [Fix] 寻找最近的可行走点作为目标
        // 防止 HQ 占用的格子被标记为障碍物，导致流场算法无法从中心开始扩散
        targetPosition = FindNearestWalkable(x, y);

        CalculateFlowField();
    }

    // [New] 螺旋搜索最近的可行走点
    private Vector2Int FindNearestWalkable(int startX, int startY)
    {
        var mapData = MapGenerator.Instance.MapData;
        int w = _width;
        int h = _height;

        // 1. 检查原点是否可行走
        if (IsWalkable(startX, startY, mapData, w, h))
            return new Vector2Int(startX, startY);

        // 2. 螺旋向外搜索 (最大半径10)
        for (int r = 1; r <= 10; r++)
        {
            for (int i = -r; i <= r; i++)
            {
                // 检查这一圈的四个边
                if (Check(startX + i, startY + r, mapData, w, h, out var p1)) return p1;
                if (Check(startX + i, startY - r, mapData, w, h, out var p2)) return p2;
                if (Check(startX + r, startY + i, mapData, w, h, out var p3)) return p3;
                if (Check(startX - r, startY + i, mapData, w, h, out var p4)) return p4;
            }
        }

        // 实在找不到，返回原点
        return new Vector2Int(startX, startY);
    }

    private bool Check(int x, int y, NativeArray<MapGenerator.CellData> mapData, int w, int h, out Vector2Int result)
    {
        result = new Vector2Int(x, y);
        return IsWalkable(x, y, mapData, w, h);
    }

    private bool IsWalkable(int x, int y, NativeArray<MapGenerator.CellData> mapData, int width, int height)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return false;
        int idx = y * width + x;
        byte t = mapData[idx].TerrainType;
        // 0=DeepWater, 1=Water, 6=Mountain/Wall, 7=Snow, 9=Ruins
        // 注意：如果 BuildingManager 把 HQ 下方的 TerrainType 改为了 6 (Wall)，这里就会返回 false
        return !(t <= 1 || t == 6 || t == 7 || t == 9);
    }

    [ContextMenu("Force Recalculate")]
    public void CalculateFlowField()
    {
        if (!_isInitialized || MapGenerator.Instance == null) return;

        // [Safety Fix] 在重新计算前，也确保没有 Job 正在读取旧数据
        // 这在运行时重新计算（如波次开始时）非常重要
        if (World.DefaultGameObjectInjectionWorld != null)
        {
            World.DefaultGameObjectInjectionWorld.EntityManager.CompleteAllTrackedJobs();
        }

        // 1. 生成代价场 (Cost Field) - 这里包含了"防卡死"的核心逻辑
        var costJob = new GenerateCostFieldJob
        {
            MapData = MapGenerator.Instance.MapData,
            CostField = CostField,
            Width = _width,
            Height = _height,
            WallBufferCost = 20, // 墙壁缓冲区的代价 (越高越排斥墙壁)
            BaseCost = 1
        };
        JobHandle costHandle = costJob.Schedule(_width * _height, 64);

        // 2. 生成积分场 (Integration Field) - Dijkstra 算法
        var integrationJob = new CalculateIntegrationFieldJob
        {
            CostField = CostField,
            IntegrationField = IntegrationField,
            Width = _width,
            Height = _height,
            TargetIndex = targetPosition.y * _width + targetPosition.x
        };
        // Dijkstra 很难并行化，所以我们在单线程 Job 中运行 (或使用 Wavefront 算法)
        JobHandle integrationHandle = integrationJob.Schedule(costHandle);

        // 3. 生成流场向量 (Flow Directions)
        var flowJob = new GenerateFlowDirectionsJob
        {
            IntegrationField = IntegrationField,
            FlowDirections = FlowDirections,
            Width = _width,
            Height = _height
        };
        JobHandle flowHandle = flowJob.Schedule(_width * _height, 64, integrationHandle);

        flowHandle.Complete();
    }

    // --- 辅助功能：为小队生成局部流场 (如攻击移动) ---
    public Entity CreateLocalFlowField(System.Collections.Generic.List<Entity> units, float3 target)
    {
        return Entity.Null;
    }

    // --- Jobs ---

    [BurstCompile]
    struct GenerateCostFieldJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<MapGenerator.CellData> MapData;
        [WriteOnly] public NativeArray<byte> CostField;
        public int Width;
        public int Height;
        public byte WallBufferCost;
        public byte BaseCost;

        public void Execute(int index)
        {
            byte terrainType = MapData[index].TerrainType;

            // 阻挡判断: 0(DeepWater), 1(Water), 6(Mountain), 7(Snow), 9(Ruins)
            bool isObstacle = (terrainType <= 1 || terrainType == 6 || terrainType == 7 || terrainType == 9);

            if (isObstacle)
            {
                CostField[index] = 255; // 不可通行
            }
            else
            {
                // [核心优化] 检查 8 邻居是否有障碍物
                // 如果旁边有墙，增加代价，形成 "软阻挡"
                bool nearWall = false;
                int x = index % Width;
                int y = index / Width;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx >= 0 && nx < Width && ny >= 0 && ny < Height)
                        {
                            int nIdx = ny * Width + nx;
                            byte nType = MapData[nIdx].TerrainType;
                            if (nType <= 1 || nType == 6 || nType == 7 || nType == 9)
                            {
                                nearWall = true;
                                break;
                            }
                        }
                    }
                    if (nearWall) break;
                }

                if (nearWall)
                {
                    CostField[index] = WallBufferCost; // 靠近墙壁，代价高
                }
                else
                {
                    // 道路 (Terrain 8) 依然可以有更低代价
                    if (terrainType == 8) CostField[index] = 1;
                    else CostField[index] = BaseCost;
                }
            }
        }
    }

    [BurstCompile]
    struct CalculateIntegrationFieldJob : IJob
    {
        [ReadOnly] public NativeArray<byte> CostField;
        public NativeArray<ushort> IntegrationField;
        public int Width;
        public int Height;
        public int TargetIndex;

        public void Execute()
        {
            for (int i = 0; i < IntegrationField.Length; i++)
            {
                IntegrationField[i] = ushort.MaxValue;
            }

            NativeList<int> openList = new NativeList<int>(Allocator.Temp);
            NativeList<int> nextList = new NativeList<int>(Allocator.Temp);

            IntegrationField[TargetIndex] = 0;
            openList.Add(TargetIndex);

            while (openList.Length > 0)
            {
                for (int i = 0; i < openList.Length; i++)
                {
                    int idx = openList[i];
                    ushort currentCost = IntegrationField[idx];
                    int cx = idx % Width;
                    int cy = idx / Width;

                    CheckNeighbor(idx, cx, cy + 1, currentCost, ref nextList);
                    CheckNeighbor(idx, cx, cy - 1, currentCost, ref nextList);
                    CheckNeighbor(idx, cx - 1, cy, currentCost, ref nextList);
                    CheckNeighbor(idx, cx + 1, cy, currentCost, ref nextList);
                }

                openList.Clear();
                var temp = openList;
                openList = nextList;
                nextList = temp;
            }

            openList.Dispose();
            nextList.Dispose();
        }

        private void CheckNeighbor(int currentIdx, int nx, int ny, ushort currentIntCost, ref NativeList<int> nextList)
        {
            if (nx >= 0 && nx < Width && ny >= 0 && ny < Height)
            {
                int nIdx = ny * Width + nx;
                byte moveCost = CostField[nIdx];

                if (moveCost == 255) return;

                int newCost = currentIntCost + moveCost;
                if (newCost < IntegrationField[nIdx])
                {
                    IntegrationField[nIdx] = (ushort)newCost;
                    nextList.Add(nIdx);
                }
            }
        }
    }

    [BurstCompile]
    struct GenerateFlowDirectionsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ushort> IntegrationField;
        [WriteOnly] public NativeArray<float2> FlowDirections;
        public int Width;
        public int Height;

        public void Execute(int index)
        {
            ushort myCost = IntegrationField[index];
            if (myCost == ushort.MaxValue)
            {
                FlowDirections[index] = float2.zero;
                return;
            }

            int x = index % Width;
            int y = index / Width;

            int bestIdx = -1;
            ushort bestCost = myCost;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    int ny = y + dy;

                    if (nx >= 0 && nx < Width && ny >= 0 && ny < Height)
                    {
                        int nIdx = ny * Width + nx;
                        ushort nCost = IntegrationField[nIdx];
                        if (nCost < bestCost)
                        {
                            bestCost = nCost;
                            bestIdx = nIdx;
                        }
                    }
                }
            }

            if (bestIdx != -1)
            {
                int bx = bestIdx % Width;
                int by = bestIdx / Width;
                float2 dir = new float2(bx - x, by - y);
                FlowDirections[index] = math.normalizesafe(dir);
            }
            else
            {
                FlowDirections[index] = float2.zero;
            }
        }
    }

    void OnDrawGizmos()
    {
        if (!showDebugGizmos || !_isInitialized || !FlowDirections.IsCreated) return;

        Vector3 camPos = Camera.main ? Camera.main.transform.position : Vector3.zero;
        int range = 20;
        int cx = (int)camPos.x;
        int cz = (int)camPos.z;

        Gizmos.color = Color.yellow;
        for (int y = cz - range; y <= cz + range; y++)
        {
            for (int x = cx - range; x <= cx + range; x++)
            {
                if (x >= 0 && x < _width && y >= 0 && y < _height)
                {
                    int i = y * _width + x;
                    float2 dir = FlowDirections[i];
                    if (math.lengthsq(dir) > 0.1f)
                    {
                        Vector3 start = new Vector3(x, 1, y);
                        Vector3 end = start + new Vector3(dir.x, 0, dir.y) * 0.4f;
                        Gizmos.DrawLine(start, end);
                    }
                }
            }
        }
    }
}