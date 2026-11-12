#if DEBUG
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Demo;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6「演示控制」（样稿演示条 demoClick → PR.route.set → applyParams 的移植：Shell/ShellVm.Demo.cs + Views/Demo/DemoControlVm）：
/// <list type="bullet">
/// <item>每个按钮 = 一组新的地址参数按差异套用：与同一串地址参数依次套用到 DemoFleet 上的结果逐项相同（同 Core 的 apply-diff 向量的思路）。</item>
/// <item>参数没变、设备却不在那个状态（自动批准之后、本机下发的急停）时按钮照样起作用。</item>
/// <item>冻结同时改演示数据与 UiTicker / 窗口（回调），toast 不冻结；重置回到默认并提示「演示数据已重置」，选中的对象与已有的提示照样稿留着。</item>
/// <item>工具窗口的位置按桌面缩放换算（不是 Screen.Scaling）。</item>
/// </list>
/// </summary>
public class M6DemoControlTests
{
    private static (ShellVm Shell, DemoControlVm Vm, List<bool> Frozen) Open(string query = "freeze=1")
    {
        var p = DemoParams.Parse(query);
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;   // 测试里没有 UI 线程：toast 不定时消失
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyStartParams(p, null);
        var calls = new List<bool>();
        return (s, new DemoControlVm(s, calls.Add), calls);
    }

    /// <summary>演示数据的状态摘要（每台：连接、控制权、急停、报警、事件、每路的状态与程序）。</summary>
    private static string Dump(DemoFleet f)
    {
        var sb = new StringBuilder();
        sb.Append("now ").Append(f.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(" frozen ").Append(f.Frozen).AppendLine();
        foreach (var d in f.Devices)
        {
            sb.Append(d.K).Append(' ').Append(d.Alias).Append(" conn ").Append(d.Conn.State).Append('/').Append(d.Conn.Forced).Append('/').Append(d.Conn.RetryS)
              .Append(" lease ").Append(d.Lease.State).Append('/').Append(d.Lease.Holder).Append('/').Append(d.Lease.RemainS).Append('/').Append(d.Lease.GraceS)
              .Append(" estop ").Append(d.Estop.Active).Append('/').Append(d.Estop.By).AppendLine();
            foreach (var a in d.Alarms) sb.Append("  alarm ").Append(a.Id).Append(' ').Append(a.Code).Append(' ').Append(a.Level).Append(' ').Append(a.Ch).Append(' ').Append(a.Text).Append(' ').Append(a.Acked).Append(' ').Append(a.Demo).AppendLine();
            foreach (var e in d.Events.Take(8)) sb.Append("  event ").Append(e.Id).Append(' ').Append(e.Level).Append(' ').Append(e.Text).AppendLine();
            foreach (var c in d.Channels) sb.Append("  ch ").Append(c.N).Append(' ').Append(c.State).Append(' ').Append(c.Program?.Status).Append(' ').Append(c.Sv).Append(' ').Append(c.Heating).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>同一串地址参数依次套用（样稿每点一次演示条 = 一次 applyParams）。</summary>
    private static DemoFleet Replay(params string[] queries)
    {
        var f = new DemoFleet(DemoParams.Parse(queries[0]));
        foreach (var q in queries.Skip(1)) f.Apply(DemoParams.Parse(q));
        return f;
    }

    [Fact]
    public void Estop_OnThenOff_IsTheSameAsTheUrlDiff_AndOffIsATouchScreenReset()
    {
        var (s, vm, _) = Open();
        vm.ToggleEstop();
        Assert.True(vm.Estop);
        Assert.Equal("freeze=1&estop=1", s.Fleet.Params!.ToString());
        Assert.Equal(Dump(Replay("freeze=1", "freeze=1&estop=1")), Dump(s.Fleet.DemoData!));
        vm.ToggleEstop();
        Assert.False(vm.Estop);
        Assert.Equal(Dump(Replay("freeze=1", "freeze=1&estop=1", "freeze=1")), Dump(s.Fleet.DemoData!));
        var d1 = s.Fleet.DemoData!.Device(1)!;
        Assert.Equal("急停已在触屏复位", d1.Events[0].Text);
        Assert.DoesNotContain(d1.Alarms, a => a.Code == "ESTOP");
    }

    [Fact]
    public void Alarm_Lease_Offline_AreTheSameAsTheUrlDiffs()
    {
        var (s, vm, _) = Open();
        vm.ToggleAlarm();                       // alarm=1:1
        Assert.True(vm.Alarm);
        vm.SelectTarget(2);
        vm.SetLease("requested");               // lease=2:requested
        vm.SetLease("lost");                    // lease=2:lost
        vm.ToggleOffline();                     // offline=2
        Assert.True(vm.Offline);
        vm.SelectTarget(1);
        vm.ToggleAlarm();                       // alarm=1:0（清除全部报警）
        Assert.False(vm.Alarm);
        Assert.Equal("freeze=1&lease=2:lost&offline=2&alarm=1:0", s.Fleet.Params!.ToString());
        Assert.Equal(Dump(Replay("freeze=1", "freeze=1&alarm=1:1", "freeze=1&alarm=1:1&lease=2:requested", "freeze=1&alarm=1:1&lease=2:lost",
            "freeze=1&alarm=1:1&lease=2:lost&offline=2", "freeze=1&alarm=1:0&lease=2:lost&offline=2")), Dump(s.Fleet.DemoData!));
        Assert.Empty(s.Fleet.DemoData!.Device(1)!.Alarms);
        // 断线关掉：offline 按设备此刻的状态重算 → none
        vm.SelectTarget(2);
        vm.ToggleOffline();
        Assert.Equal(ConnStatus.Online, s.Fleet.DemoData!.Device(2)!.Conn.State);
        Assert.Equal("none", s.Fleet.Params!.Offline!.Count == 0 ? "none" : string.Join(",", s.Fleet.Params.Offline));
    }

    [Fact]
    public void Devs_ClearTheOverrides_ResetTheTabs_AndKeepAValidTarget()
    {
        var (s, vm, _) = Open("freeze=1&lease=2:requested&estop=3");
        s.Navigate(Route.Device(3));
        vm.SelectTarget(3);
        vm.SetDevs("4");
        Assert.Equal(4, s.Fleet.Devices.Count);
        Assert.Equal("devs=4&freeze=1", s.Fleet.Params!.ToString());
        Assert.Equal(Dump(Replay("freeze=1&devs=4")), Dump(s.Fleet.DemoData!));
        Assert.Equal("device/3", s.Route.Id);       // 当前页面还在：留在原地
        vm.SetDevs("1");
        Assert.Single(s.Fleet.Devices);
        Assert.Equal("overview", s.Route.Id);       // 3号机没了：回到总览
        Assert.Equal(1, vm.TargetK);                // 目标没了：回到第一台
        Assert.Equal("1", vm.Combo);
        vm.SetDevs("3");
        Assert.Equal("3", vm.Combo);
        Assert.Null(s.Fleet.Params!.Devs);
    }

    [Fact]
    public void Buttons_StillWork_WhenTheUrlAlreadySaysSo_ButTheDeviceMovedOn()
    {
        var (s, vm, _) = Open("");                  // 不冻结：倒计时要时钟走
        vm.SelectTarget(2);
        vm.SetLease("requested");
        for (int i = 0; i < DemoFleet.LeaseWaitS; i++) s.Fleet.OnTick(true);
        Assert.Equal(LeaseStatus.Local, s.Fleet.DemoData!.Device(2)!.Lease.State);     // 45 s 后申请超时（参数仍是 2:requested）
        vm.Refresh();
        Assert.Equal("local", vm.LeaseCode);
        vm.SetLease("requested");
        Assert.Equal(LeaseStatus.Requested, s.Fleet.DemoData!.Device(2)!.Lease.State); // 按差异套用不会动：先撤掉这一台的覆盖再套上

        // 本机下发的急停（不在地址参数里）：「急停」关照样复位
        var demo = s.Fleet.DemoData!;
        Assert.True(demo.Act(demo.Device(1)!, DeviceAction.Estop, null, quiet: true).Ok);
        vm.SelectTarget(1);
        Assert.True(vm.Estop);
        vm.ToggleEstop();
        Assert.False(demo.Device(1)!.Estop.Active);
        Assert.Equal("急停已在触屏复位", demo.Device(1)!.Events[0].Text);

        // 用户断开的设备：「断线」关 = 悄悄重连（样稿 act reconnect quiet）
        Assert.True(demo.Act(demo.Device(1)!, DeviceAction.Disconnect, null, quiet: true).Ok);
        vm.Refresh();
        Assert.True(vm.Offline);
        vm.ToggleOffline();
        Assert.Equal(ConnStatus.Connecting, demo.Device(1)!.Conn.State);
    }

    [Fact]
    public void Freeze_GoesToTheDataAndTheWindow_NotTheToasts_AndResetUnfreezes()
    {
        var (s, vm, calls) = Open();
        Assert.True(vm.Frozen);
        s.Toasts.Frozen = false;
        bool toasts = s.Toasts.Frozen;
        vm.ToggleFreeze();
        Assert.False(vm.Frozen);
        Assert.False(s.Fleet.DemoData!.Frozen);
        Assert.Equal(new[] { false }, calls);
        Assert.Equal(toasts, s.Toasts.Frozen);
        s.Fleet.OnTick(true);
        Assert.Equal(DemoFleet.T0.AddSeconds(1), s.Fleet.DemoData.Now);   // 不冻结：时钟走
        vm.ToggleFreeze();
        Assert.True(s.Fleet.DemoData.Frozen);
        Assert.Equal(new[] { false, true }, calls);
        s.Fleet.OnTick(true);
        Assert.Equal(DemoFleet.T0.AddSeconds(1), s.Fleet.DemoData.Now);

        // 重置：参数全部默认（冻结也去掉）、回到总览、提示「演示数据已重置」
        vm.ToggleAlarm();
        s.Navigate(Route.Reactor(3, 11));
        s.Toasts.Frozen = true;
        vm.Reset();
        Assert.Equal(DemoParams.Default, s.Fleet.Params);
        Assert.Equal("overview", s.Route.Id);
        Assert.False(vm.Frozen);
        Assert.False(calls[^1]);
        Assert.Contains(s.Toasts.Items, t => t.Text == "演示数据已重置" && t.Kind == ToastKind.Info);
        Assert.Equal(Dump(new DemoFleet(DemoParams.Default)), Dump(s.Fleet.DemoData!));
        Assert.Equal(DemoFleet.T0, s.Fleet.DemoData!.Now);
    }

    /// <summary>
    /// 重置同样稿（shell.js reset）：目标回到第一台（demoDev = null）；选中的对象还在就留着（不动 shell.sel，pruneSel 只换掉不在了的）；
    /// 已有的提示不清，后面跟「演示数据已重置」。
    /// </summary>
    [Fact]
    public void Reset_KeepsAValidSelectionAndTheToasts_AndTheTargetGoesBackToTheFirstDevice()
    {
        var (s, vm, _) = Open();
        vm.SelectTarget(3);
        s.Navigate(Route.Device(2));
        s.Select(3, 11);
        s.Toast("之前的提示", ToastKind.Info);
        vm.Reset();
        Assert.Equal(1, vm.TargetK);
        Assert.Equal((3, (int?)11), (s.Selection.K, s.Selection.N));
        Assert.Equal("overview", s.Route.Id);
        Assert.Equal(new[] { "之前的提示", "演示数据已重置" }, s.Toasts.Items.Select(t => t.Text));

        // 4 台时选中 4号机 · RV2：重置回到 3 台，它不在了 → 第一台（样稿 pruneSel）
        var (s4, vm4, _) = Open("freeze=1&devs=4");
        s4.Select(4, 2);
        vm4.SelectTarget(4);
        vm4.Reset();
        Assert.Equal(3, s4.Fleet.Devices.Count);
        Assert.Equal((1, (int?)null), (s4.Selection.K, s4.Selection.N));
        Assert.Equal(1, vm4.TargetK);
    }

    /// <summary>
    /// 工具窗口开在主窗口右边：宽度是 DIP，位置按桌面缩放（WindowBase.DesktopScaling）换算 —— macOS 是 1（视网膜屏的 Screen.Scaling = 2 不能用），
    /// Windows 是渲染缩放；放不下时贴着工作区右边，不高过工作区顶。
    /// </summary>
    [Fact]
    public void ToolWindow_SitsBesideTheMainWindow_InDesktopUnits()
    {
        // macOS 视网膜屏 1512 × 982 点：1440 宽的主窗口右边放不下 372 → 贴着屏幕右边（以前按 2 倍算，被夹到 768，压在主窗口中间）
        Assert.Equal(new PixelPoint(1140, 38), DemoControlWindow.BesidePosition(new PixelPoint(0, 38), 1440, 372, 1, new PixelRect(0, 25, 1512, 957)));
        // 外接 2560 宽：放在右边，隔 12
        Assert.Equal(new PixelPoint(1552, 60), DemoControlWindow.BesidePosition(new PixelPoint(100, 60), 1440, 372, 1, new PixelRect(0, 25, 2560, 1415)));
        // Windows 150 %：位置是物理像素，宽 1440 DIP = 2160 px，空隙 12 DIP = 18 px
        Assert.Equal(new PixelPoint(2278, 50), DemoControlWindow.BesidePosition(new PixelPoint(100, 50), 1440, 372, 1.5, new PixelRect(0, 0, 3840, 2088)));
        // 主窗口顶高过工作区：工具窗口不跟着跑到菜单栏后面
        Assert.Equal(new PixelPoint(1452, 25), DemoControlWindow.BesidePosition(new PixelPoint(0, -10), 1440, 372, 1, new PixelRect(0, 25, 2560, 1415)));
        // 不知道屏幕：照算
        Assert.Equal(new PixelPoint(1452, 0), DemoControlWindow.BesidePosition(new PixelPoint(0, 0), 1440, 372, 1, null));
    }

    /// <summary>真机学到令牌只读之后，控制权卡的说明写原因（M6，states-readonly-token 的检查器部分）。</summary>
    [Fact]
    public void LeaseCard_LocalNote_FollowsTheLearnedRole()
    {
        Assert.Equal("本机只读 · 启动、设定等需要控制权\n停止类操作随时可用", LeaseBoxVm.LocalNote("1号机", TokenRole.Unknown));
        Assert.Equal("本机只读 · 启动、设定等需要控制权\n停止类操作随时可用", LeaseBoxVm.LocalNote("1号机", new TokenRole(NoEstop: true)));
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌\n停止类操作随时可用", LeaseBoxVm.LocalNote("1号机", new TokenRole(ReadOnly: true)));
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌\n停止需要 operator 令牌或急停权限", LeaseBoxVm.LocalNote("1号机", new TokenRole(true, true)));
    }

    /// <summary>「不是当前页签」提示条是一整句（名字加粗），名字变了才换新的一段文字。</summary>
    [Fact]
    public void InspectorOtherBanner_IsOneSentence()
    {
        var (s, _, _) = Open();
        s.Navigate(Route.Reactor(1, 1));
        s.Select(3, 11);
        var i = s.Inspector;
        Assert.True(i.HasOther);
        Assert.Equal(new[] { "检查器：", "3号机 · RV11", "（不是当前页签）" }, i.OtherSegments.Select(x => x.Text));
        Assert.Equal(new[] { false, true, false }, i.OtherSegments.Select(x => x.Bold));
        var same = i.OtherSegments;
        s.RefreshChrome();
        Assert.Same(same, i.OtherSegments);
        s.Select(2);
        Assert.Equal("2号机", i.OtherSegments[1].Text);
    }
}
#endif
