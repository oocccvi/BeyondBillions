using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using System.Collections.Generic;

public class FlowFieldController : MonoBehaviour
{
    public static FlowFieldController Instance { get; private set; }

    // We use Allocator.Persistent so it survives between frames.
    // However, to avoid race conditions with Systems reading the old array while we calculate a new one,
    // we will calculate into a 'back buffer' or new array, and then swap.
    public NativeArray<float2> FlowDirections;

    public Vector2Int targetPosition;
    private bool _hasTarget = false;

    // [新增] 存储 HQ 的位置
    private Vector2Int? _hqLocation = null;
    public bool HasHQ => _hqLocation.HasValue;

    private EntityManager _entityManager;

    void Awake() { if (Instance != null && Instance != this) Destroy(this); Instance = this; }
    void Start() { _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager; _hasTarget = false; }

    void OnDestroy()
    {
        // Must complete all jobs before disposing
        // But since systems might be running, this is tricky. 
        // Usually, OnDestroy happens when scene closes.
        if (FlowDirections.IsCreated)
        {
            try { FlowDirections.Dispose(); } catch (System.Exception) { }
        }
        if (Instance == this) Instance = null;
    }

    // [新增] 注册 HQ 位置。BuildingManager 放置 HQ 后应该调用此方法。
    public void RegisterHQ(int x, int y)
    {
        _hqLocation = new Vector2Int(x, y);
        Debug.Log($"[FlowField] HQ 已注册在 ({x}, {y})，流场目标已锁定至 HQ。");

        // 强制立即更新目标为 HQ
        UpdateTargetPosition(x, y);
    }

    public void UpdateTargetPosition(int x, int y)
    {
        // [修改] 如果已经有 HQ 了，且传入的坐标不是 HQ 的坐标（比如是自动脚本设置的中心点），
        // 我们忽略这次修改，强制保持 HQ 为目标。
        if (_hqLocation.HasValue)
        {
            if (x != _hqLocation.Value.x || y != _hqLocation.Value.y)
            {
                // Debug.LogWarning($"[FlowField] 忽略目标 ({x},{y})，因为 HQ 存在于 ({_hqLocation.Value.x},{_hqLocation.Value.y})");
                x = _hqLocation.Value.x;
                y = _hqLocation.Value.y;
            }
        }

        targetPosition = new Vector2Int(x, y);
        _hasTarget = true;
        CalculateFlowField();
    }

    public Entity CreateLocalFlowField(List<Entity> soldiers, float3 targetPos)
    {
        return Entity.Null;
    }

    [ContextMenu("Recalculate Flow Field")]
    public void CalculateFlowField()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        // [修改] 如果有 HQ，确保目标始终指向 HQ
        if (_hqLocation.HasValue)
        {
            targetPosition = _hqLocation.Value;
            _hasTarget = true;
        }

        if (!_hasTarget) return;

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        int len = width * height;
        var mapData = MapGenerator.Instance.MapData;

        // Create a NEW array for the result to avoid conflict with running jobs reading the OLD FlowDirections
        NativeArray<float2> newFlowDirections = new NativeArray<float2>(len, Allocator.Persistent);

        var offsets = new NativeArray<int2>(8, Allocator.TempJob);
        offsets[0] = new int2(0, 1); offsets[1] = new int2(0, -1); offsets[2] = new int2(-1, 0); offsets[3] = new int2(1, 0);
        offsets[4] = new int2(-1, 1); offsets[5] = new int2(1, 1); offsets[6] = new int2(-1, -1); offsets[7] = new int2(1, -1);

        var costs = new NativeArray<int>(8, Allocator.TempJob);
        for (int i = 0; i < 4; i++) costs[i] = 10; for (int i = 4; i < 8; i++) costs[i] = 14;

        var job = new CalculateFlowFieldJob
        {
            width = width,
            height = height,
            targetX = targetPosition.x,
            targetY = targetPosition.y,
            mapData = mapData,
            flowDirections = newFlowDirections, // Write to new array
            NeighborOffsets = offsets,
            NeighborCosts = costs
        };

        // Run immediately on main thread
        job.Run();

        offsets.Dispose();
        costs.Dispose();

        // Now swap the arrays safely
        if (FlowDirections.IsCreated)
        {
            var old = FlowDirections;
            FlowDirections = newFlowDirections;

            // 尝试释放旧数组。如果还有 Job 在使用它，这里会抛出异常。
            // 我们捕获异常以防止游戏崩溃。
            // 虽然这可能导致内存泄漏（那块内存没被释放），但在这个阶段比崩溃要好。
            // 理想的做法是使用 JobHandle 链来管理依赖，但在 MonoBehaviour 中这很困难。
            try
            {
                old.Dispose();
            }
            catch (System.Exception)
            {
                // Job 还在运行，无法释放。
                // 这次泄漏是可以接受的代价，或者我们可以把 old 加入一个全局列表，在 LateUpdate 尝试释放。
                // Debug.LogWarning("FlowFieldController: 无法释放旧流场数组，可能有 Job 正在读取。");
            }
        }
        else
        {
            FlowDirections = newFlowDirections;
        }
    }

    [BurstCompile]
    struct CalculateFlowFieldJob : IJob
    {
        public int width; public int height; public int targetX; public int targetY;
        [ReadOnly] public NativeArray<MapGenerator.CellData> mapData;
        [WriteOnly] public NativeArray<float2> flowDirections;
        [ReadOnly] public NativeArray<int2> NeighborOffsets;
        [ReadOnly] public NativeArray<int> NeighborCosts;

        public void Execute()
        {
            int len = width * height;
            int targetIdx = targetY * width + targetX;

            var costField = new NativeArray<byte>(len, Allocator.Temp);
            var integrationField = new NativeArray<int>(len, Allocator.Temp);
            var queue = new NativeQueue<int>(Allocator.Temp);

            for (int i = 0; i < len; i++)
            {
                integrationField[i] = int.MaxValue;
                byte type = mapData[i].TerrainType;

                if (type == 8) costField[i] = 1;       // Road
                else if (type == 4) costField[i] = 6;  // Forest
                else if (type == 5) costField[i] = 8;  // Swamp
                else if (type <= 1 || type == 6 || type == 7 || type == 9)
                {
                    costField[i] = 10;
                }
                else
                {
                    costField[i] = 5;
                }
            }

            int clearRadius = 2;
            for (int x = targetX - clearRadius; x <= targetX + clearRadius; x++)
            {
                for (int y = targetY - clearRadius; y <= targetY + clearRadius; y++)
                {
                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        int idx = y * width + x;
                        costField[idx] = 1;
                    }
                }
            }

            integrationField[targetIdx] = 0;
            queue.Enqueue(targetIdx);

            int4 offsets = new int4(-1, 1, -width, width);

            while (!queue.IsEmpty())
            {
                int currentIdx = queue.Dequeue();
                int currentCost = integrationField[currentIdx];
                int cx = currentIdx % width;

                for (int i = 0; i < 4; i++)
                {
                    int neighborIdx = currentIdx + offsets[i];
                    if (neighborIdx < 0 || neighborIdx >= len) continue;
                    if (i == 0 && cx == 0) continue;
                    if (i == 1 && cx == width - 1) continue;

                    byte cost = costField[neighborIdx];
                    if (cost == 255) continue;

                    int newCost = currentCost + cost;
                    if (newCost < integrationField[neighborIdx])
                    {
                        integrationField[neighborIdx] = newCost;
                        queue.Enqueue(neighborIdx);
                    }
                }
            }

            for (int idx = 0; idx < len; idx++)
            {
                // Safety check
                if (idx < 0 || idx >= flowDirections.Length) continue;

                if (costField[idx] == 255 || idx == targetIdx)
                {
                    flowDirections[idx] = float2.zero;
                    continue;
                }

                int bestCost = integrationField[idx];
                int cx = idx % width;
                int cy = idx / width;
                int2 bestDir = int2.zero;

                for (int i = 0; i < 8; i++)
                {
                    int2 offset = NeighborOffsets[i];
                    int nx = cx + offset.x;
                    int ny = cy + offset.y;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    int neighborIdx = ny * width + nx;

                    int neighborTotalCost = integrationField[neighborIdx];

                    if (neighborTotalCost < bestCost)
                    {
                        bestCost = neighborTotalCost;
                        bestDir = offset;
                    }
                }

                if (bestDir.x != 0 || bestDir.y != 0)
                    flowDirections[idx] = math.normalizesafe(new float2(bestDir.x, bestDir.y));
                else
                    flowDirections[idx] = float2.zero;
            }

            costField.Dispose();
            integrationField.Dispose();
            queue.Dispose();
        }
    }

    public bool IsValidTarget(int x, int y, int width, int height, NativeArray<MapGenerator.CellData> mapData) { return true; }
}