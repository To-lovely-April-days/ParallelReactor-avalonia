using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（v4-program-1）：独立搅拌型号的检查器「搅拌（本釜）」用本釜的设定转速（样稿 numInput 的 ch.rpm、sim 的 s.rpm || 500），
/// 不用有效转速 —— 搅拌关着时有效转速是 0，以前转速框写 0、「启动搅拌」按 0 rpm 启动（2号机 HT-PR4 · RV4）。「搅拌中」的悬停照旧写有效转速。
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口。
/// </summary>
public class M5ReviewInspectorTests
{
    private static ShellVm Shell(string query)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.RefreshChrome();
        return s;
    }

    private static DemoChannel Channel(ReactorInspectorVm r) => ((DemoDeviceSource)r.D.Source).Device.Channel(r.N)!;

    /// <summary>演示数据给出设定转速（不看搅拌开没开）；有效转速关着为 0。</summary>
    [Fact]
    public void DemoExtras_CarryTheSetpoint_EvenWithTheStirrerOff()
    {
        var s = Shell("freeze=1");
        var src = (DemoDeviceSource)s.Fleet.Device(2)!.Source;
        var off = src.Extras(4)!;
        Assert.False(off.StirOn);
        Assert.Equal(0, off.Rpm);              // 有效转速：关着是 0（读数、曲线用）
        Assert.Equal(500, off.RpmSp);          // 设定转速：样稿 s.rpm || 500
        var on = src.Extras(1)!;
        Assert.True(on.StirOn);
        Assert.Equal(800, on.Rpm);
        Assert.Equal(800, on.RpmSp);
        Assert.Null(src.Extras(9));            // 没有这一路（HT-PR4 只有 4 路）
    }

    /// <summary>搅拌关着：转速框写设定值 500（样稿 done24 截图「− 500 rpm +」），不是有效转速 0；本机只读时「设定」「启动搅拌」只因控制权不可用。</summary>
    [Fact]
    public void StirrerOff_FieldShowsTheSetpoint()
    {
        var s = Shell("sel=2/4&freeze=1");
        s.Select(2, 4);
        var r = Assert.IsType<ReactorInspectorVm>(s.Inspector.Body);
        Assert.True(r.Independent);
        Assert.False(r.StirCell);
        Assert.Equal(500, r.RpmValue);
        Assert.NotNull(r.HeatReason);
        Assert.Equal(r.HeatReason, r.ChStirSet.Reason);
        Assert.Equal(r.HeatReason, r.ChStirOn.Reason);
    }

    /// <summary>复核要的回归：远程控制中，2号机 RV4 搅拌关着，点「启动搅拌」按 500 rpm 启动（样稿 chStirOn 用转速框的值）。</summary>
    [Fact]
    public void StartStirring_PR4_RV4_StirrerOff_Sends500()
    {
        var s = Shell("lease=2:remote&sel=2/4&freeze=1");
        s.Select(2, 4);
        var r = Assert.IsType<ReactorInspectorVm>(s.Inspector.Body);
        Assert.Null(r.ChStirOn.Reason);
        Assert.Null(r.ChStirSet.Reason);
        Assert.Equal(500, r.RpmValue);

        r.ChStirOn.Command.Execute(null);

        var ch = Channel(r);
        Assert.True(ch.StirOn);
        Assert.Equal(500, ch.Rpm);
        Assert.Equal("2号机 · RV4 搅拌已启动 · 500 rpm", s.Toasts.Items.Last().Text);
        // 开着以后：「搅拌中」格，悬停写有效转速
        Assert.True(r.StirCell);
        Assert.Equal(500, r.RpmValue);
        Assert.Equal("本釜搅拌 500 rpm", r.StirCellTip);
    }

    /// <summary>「设定」发的是转速框里改过的值；搅拌关着时改了设定值，之后启动也按新的设定值。</summary>
    [Fact]
    public void SetRpm_WhileOff_ThenStart_UsesTheEditedSetpoint()
    {
        var s = Shell("lease=2:remote&sel=2/4&freeze=1");
        s.Select(2, 4);
        var r = Assert.IsType<ReactorInspectorVm>(s.Inspector.Body);
        r.RpmValue = 650;                       // 操作员改了转速（还没点「设定」）
        s.Fleet.RefreshNow();                   // 每秒的刷新不能冲掉它
        Assert.Equal(650, r.RpmValue);
        r.ChStirSet.Command.Execute(null);
        var ch = Channel(r);
        Assert.False(ch.StirOn);
        Assert.Equal(650, ch.Rpm);
        Assert.Equal("2号机 · RV4 转速 650 rpm", s.Toasts.Items.Last().Text);
        s.Fleet.RefreshNow();
        Assert.Equal(650, r.RpmValue);          // 设定之后转速框跟着设定值（不是有效转速 0）
        r.ChStirOn.Command.Execute(null);
        Assert.True(ch.StirOn);
        Assert.Equal(650, ch.Rpm);
        Assert.Equal("2号机 · RV4 搅拌已启动 · 650 rpm", s.Toasts.Items.Last().Text);
    }
}
