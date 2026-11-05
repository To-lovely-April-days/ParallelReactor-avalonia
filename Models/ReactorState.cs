namespace ParallelReactor.Models;

/// <summary>
/// 反应釜运行状态，对应 HTML 中的 st 字段：
/// react(反应中) / pressing(升压中) / heating(升温中) / alarm(超压) / done(已结束) / idle(停用)
/// ready(已启用) 为本机新增：通道已启用参与运行，但尚未开始加热（区别于 idle 停用与 heating 升温中）。
/// wait(等待中) / stir(搅拌中) / cooling(降温中) 为程序执行新增：对应程序的等待 / 搅拌 / 结束降温阶段。
/// </summary>
public enum ReactorState
{
    React,
    Pressing,
    Heating,
    Alarm,
    Done,
    Idle,
    Ready,
    Wait,
    Stir,
    Cooling
}
