namespace ParallelReactor.Control.Core;

/// <summary>
/// 产品行为开关。每个开关全软件只有这一处，改一行即可（同 <see cref="Format.PressureDisplay"/> 的做法）。
/// 用到它的地方都把它当默认值：<c>FleetVm.StopAllStopsStirring</c>、<c>LiveDeviceSource.StopAllStopsStirring</c>、
/// <see cref="Demo.DemoFleet.StopAllStopsStirring"/>。那几个属性只给测试换值，界面上没有入口。
/// </summary>
public static class ControlOptions
{
    /// <summary>
    /// 「全部停止」要不要连搅拌一起停，对应 HMI <c>POST /stop-all {stopStir}</c>。
    /// <list type="bullet">
    /// <item><b>true（默认，用户 2026-11-09 确认；旧界面 b1a4c11 也是这样）</b>：中止程序、停止加热、关闭进气，搅拌也一起停
    ///   （共用搅拌；演示里独立搅拌的型号是各釜搅拌）。对话框的副标题、说明和提示见 <see cref="StopAllText"/>，样稿已同步改成这样。</item>
    /// <item>false：搅拌保持当前状态（样稿第一版的写法；HMI 自己的 /stop-all 默认也不停搅拌，与触屏一致）。</item>
    /// </list>
    /// 急停另外立即切断并锁定，复位只能在触屏。要改只改这一行；实时设备、演示数据、全部停止对话框和提示都跟着它（doc/控制软件说明.md §3.3）。
    /// </summary>
    public static readonly bool StopAllStopsStirring = true;
}
