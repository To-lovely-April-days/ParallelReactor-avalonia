namespace ParallelReactor.Models;

/// <summary>
/// 反应釜运行状态，对应 HTML 中的 st 字段：
/// react(反应中) / pressing(升压中) / heating(升温中) / alarm(超压) / done(已结束) / idle(停用)
/// </summary>
public enum ReactorState
{
    React,
    Pressing,
    Heating,
    Alarm,
    Done,
    Idle
}
