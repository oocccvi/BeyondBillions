using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using System.Collections.Generic;

public class FlowFieldController : MonoBehaviour
{
    // ... (Instance, Awake, Start, OnDestroy, UpdateTargetPosition, CreateLocalFlowField 保持不变，请复用) ...
    // 为节省篇幅，重点展示 CalculateFlowField 和 Job 的修改

    // ... (请确保保留之前的完整类结构) ...
    public static FlowFieldController Instance { get; private set; }
    public NativeArray<float2> FlowDirections;
    public Vector2Int targetPosition;
    private bool _hasTarget = false;
    private EntityManager _entityManager;

    void Awake() { if (Instance != null && Instance != this) Destroy(this); Instance = this; }
    void Start() { _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager; _hasTarget = false; }
    void OnDestroy() { if (FlowDirections.IsCreated) { try { FlowDirections.Dispose(); } catch (System.InvalidOperationException) { } } if (Instance == this) Instance = null; }
    public void UpdateTargetPosition(int x, int y) { targetPosition = new Vector2Int(x, y); _hasTarget = true; CalculateFlowField(); }
    public Entity CreateLocalFlowField(List<Entity> soldiers, float3 targetPos) { /* 保持原样 */ return Entity.Null; } // 请使用之前的完整代码

    [ContextMenu("Recalculate Flow Field")]
    public void CalculateFlowField()
    {
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;
        if (!_hasTarget) return;

        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;
        int len = width * height;
        var mapData = MapGenerator.Instance.MapData;

        if (!IsValidTarget(targetPosition.x, targetPosition.y, width, mapData))
        {
            int targetIndex = targetPosition.y * width + targetPosition.x;
            if (mapData[targetIndex].TerrainType == 0)
            {
                targetPosition = FindValidTargetPosition(width, height, mapData);
            }
        }

        if (!FlowDirections.IsCreated || FlowDirections.Length != len)
        {
            if (FlowDirections.IsCreated) FlowDirections.Dispose();
            FlowDirections = new NativeArray<float2>(len, Allocator.Persistent);
        }

        var offsets = new NativeArray<int2>(8, Allocator.TempJob);
        offsets[0] = new int2(0, 1); offsets[1] = new int2(0, -1); offsets[2] = new int2(-1, 0); offsets[3] = new int2(1, 0);
        offsets[4] = new int2(-1, 1); offsets[5] = new int2(1, 1); offsets[6] = new int2(-1, -1); offsets[7] = new int2(1, -1);

        var costs = new NativeArray<int>(8, Allocator.TempJob);
        // 基础移动代价
        for (int i = 0; i < 4; i++) costs[i] = 10; for (int i = 4; i < 8; i++) costs[i] = 14;

        var job = new CalculateFlowFieldJob
        {
            width = width,
            height = height,
            targetX = targetPosition.x,
            targetY = targetPosition.y,
            mapData = mapData,
            flowDirections = FlowDirections,
            NeighborOffsets = offsets,
            NeighborCosts = costs
        };

        job.Run();

        offsets.Dispose();
        costs.Dispose();
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

                // [核心修改] 更新代价场 (Cost Field)
                if (type == 0 || type == 2 || type == 4)
                {
                    costField[i] = 255; // 阻挡
                }
                else if (type == 5)
                {
                    costField[i] = 50; // 森林：代价高，尽量绕开
                }
                else if (type == 6)
                {
                    costField[i] = 100; // 沼泽：代价非常高
                }
                else
                {
                    costField[i] = 1; // 平原/道路/矿脉：代价低
                }
            }

            // HQ 周围强制通行 (半径2)
            int clearRadius = 2;
            for (int x = targetX - clearRadius; x <= targetX + clearRadius; x++)
            {
                for (int y = targetY - clearRadius; y <= targetY + clearRadius; y++)
                {
                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        int idx = y * width + x;
                        if (mapData[idx].TerrainType != 0) costField[idx] = 1;
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

                    // Dijkstra 累加代价
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

                    // 这里不需要再加 terrain cost，因为 integrationField 已经包含了累积代价
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

    private bool IsValidTarget(int x, int y, int width, NativeArray<MapGenerator.CellData> mapData)
    {
        int index = y * width + x;
        return mapData[index].TerrainType != 0;
    }

    private Vector2Int FindValidTargetPosition(int width, int height, NativeArray<MapGenerator.CellData> mapData)
    {
        // 保持原样
        bool[,] visited = new bool[width, height];
        System.Collections.Generic.Queue<Vector2Int> searchQueue = new System.Collections.Generic.Queue<Vector2Int>();
        Vector2Int start = targetPosition;
        searchQueue.Enqueue(start);
        visited[start.x, start.y] = true;
        int maxIterations = 5000;
        int iterations = 0;
        int[] dx = { 0, 0, 1, -1 };
        int[] dy = { 1, -1, 0, 0 };
        while (searchQueue.Count > 0 && iterations < maxIterations)
        {
            Vector2Int current = searchQueue.Dequeue();
            iterations++;
            if (IsValidTarget(current.x, current.y, width, mapData)) return current;
            for (int i = 0; i < 4; i++)
            {
                int nx = current.x + dx[i]; int ny = current.y + dy[i];
                if (nx >= 0 && nx < width && ny >= 0 && ny < height && !visited[nx, ny])
                {
                    visited[nx, ny] = true; searchQueue.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }
        return targetPosition;
    }
}