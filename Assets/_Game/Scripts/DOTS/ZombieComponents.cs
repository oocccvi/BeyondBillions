using Unity.Entities;
using Unity.Mathematics;

public struct ZombieTag : IComponentData { }

public struct MoveSpeed : IComponentData
{
    public float Value;
}

public struct ZombieHealth : IComponentData
{
    public float Value;
    public float Max;
}

public enum ZombieBehavior : byte
{
    Rush = 0,
    Wander = 1,
    Chase = 2,
    Attack = 3
}

public struct ZombieState : IComponentData
{
    public ZombieBehavior Behavior;
    public float Timer;            // 用于 Wander 状态的倒计时
    public float3 WanderDirection;
    public float3 TargetPosition;  // 追击/攻击的目标位置

    // [新增] 攻击冷却计时器
    // 0 表示准备好了，可以咬人；>0 表示正在吞咽/吼叫（冷却中）
    public float AttackCooldown;
}