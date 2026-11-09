namespace ParallelReactor.Control.Core;

/// <summary>
/// 等用户拍板的产品行为开关。每个开关全软件只有这一处，改一行即可（同 <see cref="Format.PressureDisplay"/> 的做法）。
/// 用到它的地方都把它当默认值：<c>FleetVm.StopAllStopsStirring</c>、<c>LiveDeviceSource.StopAllStopsStirring</c>、
/// <see cref="Demo.DemoFleet.StopAllStopsStirring"/>。那几个属性只给测试换值，界面上没有入口。
/// </summary>
public static class ControlOptions
{
    /// <summary>
    /// 「全部停止」要不要连搅拌一起停，对应 HMI <c>POST /stop-all {stopStir}</c>。
    /// <list type="bullet">
    /// <item><b>false（默认，同样稿）</b>：中止程序、停止加热、关闭进气，搅拌保持当前状态。对话框说明照样稿写「搅拌保持当前状态；需要彻底切断请用急停」，
    ///   HMI 自己的 /stop-all 默认也不停搅拌（与触屏一致）。</item>
    /// <item>true（旧界面 b1a4c11 的做法）：搅拌一起停（共用搅拌，演示里独立搅拌的型号是各釜搅拌）。对话框的副标题、说明和提示跟着改，见 <see cref="StopAllText"/>。</item>
    /// </list>
    /// 用户还没确认（doc/控制软件说明.md §3.3）。要改只改这一行；实时设备、演示数据、全部停止对话框和提示都跟着它。
    /// </summary>
    public static readonly bool StopAllStopsStirring = false;
}
