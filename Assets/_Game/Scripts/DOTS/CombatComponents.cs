using Unity.Entities;
using Unity.Mathematics;

// 1. 防御塔组件
public struct Turret : IComponentData
{
    public float Range;
    public float FireRate;
    public float Cooldown;
    public Entity ProjectilePrefab;
    public float3 MuzzleOffset;
}

// 2. 子弹组件
public struct Projectile : IComponentData
{
    public float Speed;
    public float Damage;
    public float3 Velocity;
    public float Lifetime;
}

public struct BuildingHealth : IComponentData
{
    public float Value;
    public float Max;
}

public struct DeadBuildingTag : IComponentData { }
public struct MainBaseTag : IComponentData { }

// --- 士兵相关 ---

public struct SoldierTag : IComponentData { }

public struct SoldierHealth : IComponentData
{
    public float Value;
    public float Max;
}

public enum SoldierCommand : byte
{
    Idle = 0,
    Move = 1,
    AttackMove = 2,
    Hunt = 3,
    Patrol = 4,
    Scout = 5,
    Sprint = 6
}

public struct SoldierState : IComponentData
{
    public SoldierCommand Command;
    public float3 TargetPosition;
    public float3 PatrolStartPosition;
    public bool IsMoving;
    public float StopDistance;

    // [新增] 当前正在使用的局部流场 Entity
    public Entity CurrentFlowFieldEntity;
}

public struct SightRange : IComponentData
{
    public float Value;
}

// --- [新增] 局部流场专用组件 ---

// 1. 流场元数据 (位置和大小)
public struct LocalFlowFieldData : IComponentData
{
    public int2 Offset; // 在大地图上的左下角坐标
    public int2 Size;   // 局部流场的宽和高
    public int2 Target; // 目标点 (相对于大地图)
}

// 2. 流场向量数据 (Buffer)
// 这是一个动态数组，存储局部区域内的所有向量
public struct LocalFlowFieldBuffer : IBufferElementData
{
    public float2 Vector;
}

// 3. 标记这是一个流场实体
public struct FlowFieldTag : IComponentData { }