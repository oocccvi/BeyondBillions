using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public class SoldierController : MonoBehaviour
{
    public LayerMask terrainLayer;

    void Update()
    {
        // 建议：如果你在使用 BuildingManager 进行士兵控制，
        // 最好在 Inspector 中禁用此脚本，或者删除此脚本，防止右键冲突。

        if (Input.GetMouseButtonDown(1))
        {
            // MoveAllSoldiersToMouse(); 
        }
    }

    void MoveAllSoldiersToMouse()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            float3 targetPos = new float3(hit.point.x, 1f, hit.point.z);

            EntityManager em = World.DefaultGameObjectInjectionWorld.EntityManager;
            EntityQuery query = em.CreateEntityQuery(typeof(SoldierTag), typeof(SoldierState));

            NativeArray<Entity> soldiers = query.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < soldiers.Length; i++)
            {
                Entity e = soldiers[i];
                if (em.HasComponent<SoldierState>(e))
                {
                    SoldierState state = em.GetComponentData<SoldierState>(e);

                    // [修复] MoveTarget -> TargetPosition
                    state.TargetPosition = targetPos;
                    state.IsMoving = true;
                    state.StopDistance = 0.5f + UnityEngine.Random.Range(0f, 2f);

                    em.SetComponentData(e, state);
                }
            }

            soldiers.Dispose();
        }
    }
}