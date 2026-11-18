using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（v4-program-1）：演示数据同时给出设定转速和有效转速 —— 搅拌关着时有效转速是 0，设定转速照样有（不按 0 rpm 启动）。
/// （原来的 pc-v3 检查器部分随检查器一起在融合版 F9 删掉。）
/// </summary>
public class M5ReviewInspectorTests
{
    private static ShellVm Shell(string query)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        // pc-v3 样稿的演示数据与文字：用样稿当时的型号表（内置表 2026-11-18 起是真实规格）
        var s = new ShellVm(FleetVm.CreateDemo(p, models: PcV3Models.Table));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.RefreshChrome();
        return s;
    }

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
}
