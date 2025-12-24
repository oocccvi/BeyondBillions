using Unity.Entities;
using Unity.Mathematics;

// 1. 标签组件：用来标记 "这个实体是僵尸"
// 这样我们的系统就不会去移动房子或树木
public struct ZombieTag : IComponentData { }

// 2. 速度组件：定义僵尸跑多快
public struct MoveSpeed : IComponentData
{
    public float Value;
}

// [新增] 只有加了这个，子弹打上去才有意义
public struct ZombieHealth : IComponentData
{
    public float Value; // 当前血量
    public float Max;   // 最大血量
}
// [新增] 僵尸的行为状态枚举
public enum ZombieBehavior : byte
{
    Rush = 0,   // 冲锋模式（听从流场指挥，进攻基地）
    Wander = 1  // 游荡模式（随机乱走）
}

// [新增] 状态组件
public struct ZombieState : IComponentData
{
    public ZombieBehavior Behavior;
    public float Timer;           // 计时器（比如每隔几秒换个方向）
    public float3 WanderDirection; // 当前游荡的方向
}