using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 检查器（PORT-PLAN §5 M2「InspectorVm：分 DeviceInspectorVm 和 ReactorInspectorVm」；样稿 shell.js renderInspector / inspDevice / inspReactor /
/// leaseBox / estopBox / alarmCard / otherStrip / inspUninstalled）：正文的种类与身份、状态格代替灰按钮、控制权卡的 4 种写法、
/// 停止类永远不因控制权而锁、急停红框、「同一条报警只有一个确认」、「不是当前页签」提示条、未安装的位置、独立搅拌、数值框不被刷新冲掉。
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口。
/// </summary>
public class InspectorTests
{
    private static ShellVm Shell(string query = "freeze=1")
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.RefreshChrome();
        return s;
    }

    /// <summary>
    /// 复核（低）：检查器的「最近事件」在演示数据重建（截图的每个场景：DeviceVm 留着、换数据源）之后是这一份数据的 ——
    /// 新数据的事件 Id（'e13'）会与旧的重复，以前按 Id 留着旧行（截图 ov-trend-sel2 → ov-devsmix 写着上一份的「09:44:00 RV5 进入第 3 步 反应」）。
    /// </summary>
    [Fact]
    public void RecentEvents_AfterADemoRebuild_AreTheNewData()
    {
        var s = Shell("devs=PR16,PR4&sel=2&freeze=1");
        s.ApplyDemoParams(DemoParams.Parse("devs=PR16,PR4&sel=2&freeze=1"), Route.Overview);
        s.Select(1);
        var a = Assert.IsType<DeviceInspectorVm>(s.Inspector.Body);
        Assert.NotEmpty(a.Events);
        var q = DemoParams.Parse("devs=PR8,PR4,PR16&freeze=1");
        s.Fleet.ResetDemo(q);
        s.ApplyDemoParams(q);
        s.ResetView();
        s.ApplyDemoParams(q, Route.Overview);
        var b = Assert.IsType<DeviceInspectorVm>(s.Inspector.Body);
        var d = s.Fleet.Device(b.D.K)!;
        var want = d.Source.Events.Take(5).Select(e => Core.Format.Fmt.Time(e.At) + " " + e.Text).ToList();
        Assert.NotEmpty(want);
        Assert.Equal(want, b.Events.Select(e => e.AtText + " " + e.Text));
        Assert.All(b.Events, e => Assert.Contains(e.E, d.Events));
    }

    [Fact]
    public void Overview_FirstDevice_DeviceInspector()
    {
        var s = Shell();
        var d = Assert.IsType<DeviceInspectorVm>(s.Inspector.Body);
        Assert.True(d.HasHead);
        Assert.True(d.ShowLog);
        Assert.True(d.SharedStir);
        Assert.Equal("1号机", d.Alias);
        Assert.Equal("HT-PR8 · 现场 · RV1–RV4（4/8\u00a0路）", d.Sub);
        Assert.Equal("192.168.1.20:8080", d.HostPort);
        Assert.Equal("SN PR8-2026-0001", d.SnText);
        Assert.Equal("运行 4/4 · 提醒 1", d.RunMeta);
        Assert.True(d.AllRunning);   // 全部运行中：状态格，不画灰掉的「全部运行」
        Assert.Equal("只作用于1号机 · 停止不需要控制权", d.Hint);
        Assert.Equal("磁力搅拌 · RV1–RV8", d.StirMeta);
        Assert.Equal(300, d.RpmValue);
        Assert.Equal("300", d.LiveRpm);
        Assert.Equal("运行中", d.LiveStirText);
        Assert.True(d.SessionActive);
        Assert.StartsWith("已 42 min · 标记 3 个", d.SessionText);
        Assert.Equal("提醒 1", d.AlarmMeta);
        Assert.Single(d.Alarms);
        Assert.True(d.Alarms[0].ShowAck);                   // M2 的页面是骨架、没有报警条：确认在检查器里
        Assert.Equal(5, d.Events.Count);
        Assert.Equal("/1号机", s.Inspector.PathAlias);
        Assert.Equal("", s.Inspector.PathRv);
        Assert.Equal("打开设备页", s.Inspector.OpenTip);
    }

    [Fact]
    public void Lease_Remote_ReleaseButton_HeartbeatNote()
    {
        var s = Shell();
        var lb = ((DeviceInspectorVm)s.Inspector.Body!).Lease;
        Assert.True(lb.ShowLease);
        Assert.Equal(LeaseState.Remote, lb.LeaseState);
        Assert.Equal("远程控制中", lb.LeaseText);
        Assert.Equal("释放", lb.BtnLabel);
        Assert.False(lb.HasCta);
        Assert.Equal("PC-测试工位1 · 心跳正常", lb.Note);
        Assert.True(lb.ShowKey);
        s.Layout.Update(new Size(1600, 900));
        s.RefreshChrome();
        Assert.False(lb.ShowKey);   // 检查器 300 px：不写「控制权」
        Assert.True(s.Inspector.Narrow);
    }

    [Fact]
    public void Reactor_StateCells_InsteadOfGreyButtons()
    {
        var s = Shell();
        s.Select(1, 1);
        var r = Assert.IsType<ReactorInspectorVm>(s.Inspector.Body);
        Assert.Equal("1号机", r.Alias);
        Assert.Equal("RV1", r.Rv);
        Assert.Equal("HT-PR8 · 现场 · 50 mL 釜", r.Sub);
        Assert.True(r.EnabledCell);
        Assert.Null(r.RunMeta);
        Assert.True(r.RunCell);
        Assert.False(r.RunCellHeld);
        Assert.Equal("反应中", r.RunCellText);
        Assert.True(r.HeatingCell);
        Assert.True(r.ProgCell);
        Assert.Equal("程序运行中", r.ProgCellText);
        Assert.Equal(120, r.SpValue);
        Assert.Equal(200, r.SpMax);          // 量程来自型号表
        Assert.Equal("量程 ≤ 200 ℃", r.HeatMeta);
        Assert.Equal("119.8", r.TempNow);
        Assert.Equal("38", r.HeaterNow);
        Assert.False(r.Independent);
        Assert.Equal("全釜共用磁力搅拌 · 300 rpm · 运行中", r.StirNote);
        Assert.True(r.HasProgram);
        Assert.Equal("加氢-标准", r.ProgName);
        Assert.Equal("第 3/4 步 · 剩 42 min", r.ProgText);
        Assert.Equal("运行中", r.ProgMeta);
        Assert.Equal(4, r.Steps!.Count);
        Assert.Equal(StepSegState.Current, r.Steps[2].State);
        Assert.Equal("反应", r.Steps[2].Label);
        Assert.NotNull(r.ProgLoad.Reason);    // 程序运行中：「下发」带原因
        Assert.Equal("加氢-标准", r.SelectedProgram?.Name);
        Assert.True(r.SessionActive);
        Assert.Equal("/1号机/RV1", s.Inspector.PathTip);
        Assert.Equal("打开反应釜页", s.Inspector.OpenTip);
    }

    [Fact]
    public void LocalLease_PrimaryCta_ControlLocked_StopClassNeverLocked()
    {
        var s = Shell();
        s.Select(2);
        var d = (DeviceInspectorVm)s.Inspector.Body!;
        Assert.Equal(LeaseState.Local, d.Lease.LeaseState);
        Assert.True(d.Lease.HasCtaPrimary);
        Assert.Equal("申请控制权", d.Lease.CtaLabel);
        Assert.Null(d.Lease.CtaReason);
        Assert.Equal("本机只读 · 启动、设定等需要控制权\n停止类操作随时可用", d.Lease.Note);
        Assert.False(d.SharedStir);   // HT-PR4 各釜独立搅拌
        Assert.Equal("各釜独立顶置机械搅拌 · 在反应釜上设置", d.StirNote);

        s.Select(2, 1);
        var r = (ReactorInspectorVm)s.Inspector.Body!;
        Assert.NotNull(r.HeatReason);
        Assert.NotNull(r.SetSp.Reason);
        Assert.NotNull(r.Enable.Reason);
        Assert.NotNull(r.ProgReason);
        // 停止类：只要连接，永远不因为控制权而锁
        Assert.Null(r.Stop.Reason);
        Assert.Null(r.Disable.Reason);
        Assert.Null(r.HeatStop.Reason);
        Assert.Null(r.ProgAbort.Reason);
        Assert.Null(r.ChStirOff.Reason);
        Assert.Null(r.Estop.Reason);
    }

    [Fact]
    public void Lease_Requested_And_Lost()
    {
        var s = Shell("lease=2:requested&freeze=1");
        s.Select(2);
        var lb = ((DeviceInspectorVm)s.Inspector.Body!).Lease;
        Assert.True(lb.IsRequested);
        Assert.Equal(LeaseState.Requested, lb.LeaseState);
        Assert.StartsWith("申请中 · ", lb.LeaseText);
        Assert.Equal("取消申请", lb.CtaLabel);
        Assert.False(lb.CtaPrimary);
        Assert.Equal("等待2号机触屏确认 · 超时自动取消\n确认前只能查看；停止类操作随时可用", lb.Note);

        s = Shell("lease=1:lost&freeze=1");
        lb = ((DeviceInspectorVm)s.Inspector.Body!).Lease;
        Assert.Equal(LeaseState.Lost, lb.LeaseState);
        Assert.Equal("心跳中断", lb.LeaseText);
        Assert.StartsWith("控制权心跳中断 · 宽限 ", lb.LeaseTip);
        Assert.Equal("释放", lb.BtnLabel);
        Assert.Matches(@"^宽限 \d+ s · 心跳恢复后继续\n超时交回触屏$", lb.Note);
    }

    [Fact]
    public void Offline_ConnBadge_Reconnect_NotOnItsOwnPage()
    {
        var s = Shell("offline=1&freeze=1");
        var d = (DeviceInspectorVm)s.Inspector.Body!;
        Assert.False(d.Online);
        Assert.Equal("离线", d.OffWord);
        Assert.False(d.Lease.ShowLease);
        Assert.Equal(ConnState.Offline, d.Lease.ConnState);
        Assert.Equal("立即重连", d.Lease.BtnLabel);
        Assert.Matches(@"^重连中 \d+ s · 数据停在 \d\d:\d\d:\d\d\n", d.Lease.Note);
        Assert.StartsWith("断线前运行 ", d.RunMeta);
        // 打开的页签就是这台设备：设备页（M4）有断线说明条，「立即重连」只在页面上（一个动作只放一个地方，样稿 shell.js leaseBox）——
        // 检查器的控制权卡不再放第二个
        s.Navigate(Route.Device(1));
        Assert.True(s.Page!.ShowsReconnect);
        var page = Assert.IsType<DevicePageVm>(s.Page);
        Assert.Equal((true, "立即重连"), (page.HasOffBar, page.OffButton));
        Assert.Null(((DeviceInspectorVm)s.Inspector.Body!).Lease.BtnLabel);
        // 换到别的页面（总览）：按钮回到检查器
        s.Navigate(Route.Overview);
        Assert.Equal("立即重连", ((DeviceInspectorVm)s.Inspector.Body!).Lease.BtnLabel);
    }

    [Fact]
    public void Estop_RedBox_NoSingleEstopButton_AlarmsCountedRight()
    {
        var s = Shell("estop=1&freeze=1");
        var d = (DeviceInspectorVm)s.Inspector.Body!;
        Assert.True(d.EstopOn);
        Assert.Equal("急停中", d.EstopText);
        Assert.Equal("1号机触屏", d.EstopWhere);
        // 摘要计数不数「急停已触发」那一条；报警分区的标题和列出的卡片一一对应（急停那条也算）
        Assert.Equal("运行 0/4 · 提醒 1", d.RunMeta);
        Assert.Equal("报警 1 · 提醒 1", d.AlarmMeta);
        Assert.Equal(2, d.Alarms.Count);
        Assert.Contains(d.Alarms, a => a.A.Code == "ESTOP" && a.IsErrCard);
        Assert.False(d.AllRunning);
    }

    [Fact]
    public void OneAckPerAlarm_PageOwned_ShowsWhereInsteadOfButton()
    {
        var s = Shell();
        var dev = s.Fleet.Device(3)!;
        var alarm = dev.Alarms.First(a => a.IsErr);
        var owned = new Dictionary<string, string> { ["3:" + alarm.Id] = "顶部报警条" };
        var card = new InspAlarmVm(alarm);
        card.Refresh(new InspCtx(s, dev, dev.Source.Facts(), owned, null, false, s.Fleet.Now));
        Assert.False(card.ShowAck);
        Assert.Equal("确认在顶部报警条", card.WhereText);
        Assert.True(card.IsErr);          // 红色只剩图标
        Assert.False(card.IsErrCard);     // 卡片中性
        card.Refresh(new InspCtx(s, dev, dev.Source.Facts(), new Dictionary<string, string>(), null, false, s.Fleet.Now));
        Assert.True(card.ShowAck);
        Assert.Null(card.WhereText);
        Assert.True(card.IsErrCard);

        // 反应釜页（M5）：顶部报警条上有这条报警唯一的「确认」（页面拥有它）—— 反应釜检查器不再在标题下面重复它（样稿 final-reactor311）
        s.Navigate(Route.Reactor(3, 11));
        var r = (ReactorInspectorVm)s.Inspector.Body!;
        Assert.Empty(r.TopAlarms);
        Assert.True(r.RunCellHeld);
        Assert.Equal("报警暂停", r.RunCellText);
        Assert.Equal("报警暂停", r.ProgCellText);
        // 同一页上选中整台设备：设备检查器照样列出这条报警，但不放第二个「确认」（中性卡片，写「确认在顶部报警条」）
        s.Select(3);
        var dv = (DeviceInspectorVm)s.Inspector.Body!;
        var c = dv.Alarms.Single(a => a.A.Id == alarm.Id);
        Assert.False(c.ShowAck);
        Assert.Equal("确认在顶部报警条", c.WhereText);
        // 别的页面（没有报警条）：反应釜检查器照常带「确认」
        s.Navigate(Route.Device(1));
        s.Select(3, 11);
        r = (ReactorInspectorVm)s.Inspector.Body!;
        Assert.Single(r.TopAlarms);
        Assert.True(r.TopAlarms[0].ShowAck);
    }

    [Fact]
    public void OtherTabStrip_WhenInspectorObjectIsNotTheTab()
    {
        var s = Shell();
        s.Navigate(Route.Reactor(1, 1));
        Assert.False(s.Inspector.HasOther);
        s.Select(3, 11);
        Assert.True(s.Inspector.HasOther);
        Assert.Equal("3号机 · RV11", s.Inspector.OtherName);
        Assert.Equal("回到 1号机 · RV1", s.Inspector.OtherBack);
        s.Select(1);   // 反应釜页上选中设备本身：也不是页签的对象
        Assert.True(s.Inspector.HasOther);
        s.Inspector.BackCommand.Execute(null);
        Assert.Equal(1, s.Selection.K);
        Assert.Equal(1, s.Selection.N);
        Assert.False(s.Inspector.HasOther);

        s.Navigate(Route.Device(1));
        s.Select(1, 2);   // 设备页上选中它的反应釜：同一台，不提示
        Assert.False(s.Inspector.HasOther);
        s.Select(2);
        Assert.True(s.Inspector.HasOther);
        Assert.Equal("回到 1号机", s.Inspector.OtherBack);
    }

    [Fact]
    public void Uninstalled_Position_DeviceActionsBelow()
    {
        var s = Shell();
        s.Select(1, 6);
        var u = Assert.IsType<UninstalledInspectorVm>(s.Inspector.Body);
        Assert.Equal("RV6", u.Rv);
        Assert.Equal("RV6 未安装 · 无可用操作", u.Note);
        Assert.Equal("HT-PR8 · 现场 · 已装 RV1–RV4", u.Sub);
        Assert.False(u.Device.HasHead);
        Assert.True(u.Device.ShowLog);
        Assert.Equal("运行 4/4 · 提醒 1", u.Device.RunMeta);
    }

    [Fact]
    public void IndependentStir_PR4_RangeFromModel()
    {
        var s = Shell();
        s.Select(2, 1);
        var r = (ReactorInspectorVm)s.Inspector.Body!;
        Assert.True(r.Independent);
        Assert.Equal(r.D.Model.Limits.RpmMin, r.RpmMin);
        Assert.Equal(r.D.Model.Limits.RpmMax, r.RpmMax);
        Assert.Equal(r.RpmMin + "–" + r.RpmMax + " rpm", r.StirMeta);
        Assert.Equal(800, r.RpmValue);
        Assert.True(r.StirCell);
        Assert.Equal("本釜搅拌 800 rpm", r.StirCellTip);
        Assert.Equal("量程 ≤ " + r.D.Model.Limits.TempMaxC + " ℃", r.HeatMeta);
    }

    [Fact]
    public void Inputs_UserEdit_SurvivesRefresh_ResetOnReselect()
    {
        var s = Shell();
        s.Select(1, 1);
        var r = (ReactorInspectorVm)s.Inspector.Body!;
        r.SpValue = 130;                 // 操作员改了设定值（还没点「设定」）
        s.Fleet.RefreshNow();            // 每秒的刷新不能冲掉它
        Assert.Equal(130, r.SpValue);
        s.Select(1, 2);
        s.Select(1, 1);                  // 换对象再回来：回到设备的当前值
        Assert.Same(r, s.Inspector.Body);
        Assert.Equal(120, r.SpValue);
    }

    [Fact]
    public void Body_Identity_Stable_AcrossRefresh_ScrollKeyChangesWithObject()
    {
        var s = Shell();
        var b0 = s.Inspector.Body;
        var k0 = s.Inspector.BodyKey;
        s.Fleet.RefreshNow();
        Assert.Same(b0, s.Inspector.Body);
        Assert.Equal(k0, s.Inspector.BodyKey);
        s.Select(1, 3);
        Assert.NotEqual(k0, s.Inspector.BodyKey);
        s.Select(1);
        Assert.Same(b0, s.Inspector.Body);
    }

    [Fact]
    public void NoDevices_EmptyState()
    {
        var s = new ShellVm(FleetVm.CreateLive(Services.ControlConfig.InMemory()));
        Assert.True(s.Inspector.IsEmpty);
        Assert.Null(s.Inspector.Body);
        Assert.False(s.Inspector.HasSelection);
    }
}
