using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

[UpdateAfter(typeof(ZombieAttackSystem))]
public partial class BuildingCleanupSystem : SystemBase
{
    protected override void OnUpdate()
    {
        // 如果地图单例不存在，直接返回
        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        var ecb = new EntityCommandBuffer(Allocator.Temp);
        bool mapChanged = false;

        // 获取地图数据的引用（直接操作 NativeArray 比调用方法快，且能避免重复触发重算）
        var mapData = MapGenerator.Instance.MapData;
        var buildingMap = MapGenerator.Instance.BuildingMap;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;

        // 遍历所有死亡建筑
        foreach (var (transform, entity) in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<DeadBuildingTag>().WithEntityAccess())
        {
            int x = (int)math.floor(transform.ValueRO.Position.x);
            int z = (int)math.floor(transform.ValueRO.Position.z);

            // 检查是不是 HQ
            bool isHQ = EntityManager.HasComponent<MainBaseTag>(entity);

            // [核心设定] HQ 占地 4x4，防御塔占地 2x2
            int size = isHQ ? 4 : 2;

            if (isHQ)
            {
                if (GameResultManager.Instance != null)
                {
                    GameResultManager.Instance.TriggerGameOver();
                }
            }

            // [优化] 批量清理地图数据
            // 不再调用 MapGenerator.RemoveStructure (因为它会每次都触发流场重算)
            // 而是直接修改数据，最后统一重算
            int offset = size / 2;

            for (int i = x - offset; i < x - offset + size; i++)
            {
                for (int j = z - offset; j < z - offset + size; j++)
                {
                    // 边界检查
                    if (i >= 0 && i < width && j >= 0 && j < height)
                    {
                        int index = j * width + i;

                        // 1. 恢复地形数据
                        var cell = mapData[index];
                        if (cell.BuildingType != 0 || cell.TerrainType == 2) // 如果有建筑标记
                        {
                            cell.BuildingType = 0;
                            cell.TerrainType = 1; // 恢复为平原
                            mapData[index] = cell;
                            mapChanged = true;
                        }

                        // 2. 移除阻挡字典
                        if (buildingMap.ContainsKey(index))
                        {
                            buildingMap.Remove(index);
                            mapChanged = true;
                        }
                    }
                }
            }

            if (isHQ)
            {
                Debug.Log($"HQ 在 ({x},{z}) 被摧毁！");
            }

            // 标记实体销毁
            ecb.DestroyEntity(entity);
        }

        // 执行实体销毁
        ecb.Playback(EntityManager);
        ecb.Dispose();

        // [核心优化] 只有在真正有数据变动时，才重算流场，且每帧最多只算一次
        if (mapChanged)
        {
            if (FlowFieldController.Instance != null)
            {
                FlowFieldController.Instance.CalculateFlowField();
            }
        }
    }
}