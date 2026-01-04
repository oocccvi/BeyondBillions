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
        // [核心修复] 强制等待所有 Job 完成。
        // 因为本系统要在主线程直接修改 MapData (NativeArray)，
        // 必须确保后台没有其他 Job (如 ZombieAttackJob) 正在读取它。
        EntityManager.CompleteAllTrackedJobs();

        if (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized) return;

        var ecb = new EntityCommandBuffer(Allocator.Temp);
        bool mapChanged = false;

        var mapData = MapGenerator.Instance.MapData;
        var buildingMap = MapGenerator.Instance.BuildingMap;
        int width = MapGenerator.Instance.width;
        int height = MapGenerator.Instance.height;

        // 查询所有带有 DeadBuildingTag 的实体
        foreach (var (transform, entity) in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<DeadBuildingTag>().WithEntityAccess())
        {
            int x = (int)math.floor(transform.ValueRO.Position.x);
            int z = (int)math.floor(transform.ValueRO.Position.z);

            bool isHQ = EntityManager.HasComponent<MainBaseTag>(entity);
            // HQ 占 4x4, 普通塔占 2x2
            int size = isHQ ? 4 : 2;

            if (isHQ)
            {
                if (GameResultManager.Instance != null)
                {
                    GameResultManager.Instance.TriggerGameOver();
                }
                Debug.Log($"HQ 在 ({x},{z}) 被摧毁！");
            }

            int offset = size / 2;

            // 清理占据的网格数据
            for (int i = x - offset; i < x - offset + size; i++)
            {
                for (int j = z - offset; j < z - offset + size; j++)
                {
                    if (i >= 0 && i < width && j >= 0 && j < height)
                    {
                        int index = j * width + i;

                        // 1. 恢复地形数据
                        var cell = mapData[index];
                        if (cell.BuildingType != 0 || cell.TerrainType == 2)
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

            // 销毁实体
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();

        // 只有地图改变了才重算流场
        if (mapChanged)
        {
            if (FlowFieldController.Instance != null)
            {
                FlowFieldController.Instance.CalculateFlowField();
            }
        }
    }
}