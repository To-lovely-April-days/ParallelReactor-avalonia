using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F2 外框（样稿 main-fusion.html）：
/// <list type="bullet">
/// <item>路由 NavVm：页面 + 设备 + 通道（样稿 PAGE / VIEW / SEL）—— 一次看一台的页面不许「全部设备」、每台记自己的通道、设备没了修正。</item>
/// <item>停靠布局 DockLayout：宽屏设备面板停靠、检查器收起；窄屏都收起、展开时浮出；跨 1600 改默认；点外面收回；重置。</item>
/// <item>菜单栏「报警」徽标 = 全部设备未确认的报警条数（确认一条少一条）；报警摘要按钮、状态栏同一个数。</item>
/// <item>工具条：设备下拉的第一项按页面范围；主页的工具（打标记 / 导出 / 重置布局 / 全屏）；占位页的工具。</item>
/// </list>
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口。
/// </summary>
public class F2FrameTests
{
    private static ShellVm Shell(string query = "freeze=1", int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(p);
        return s;
    }

    // ———————————————————— 路由 ————————————————————

    [Fact]
    public void Nav_StartsAtHome_FirstDevice_Rv1()
    {
        var s = Shell();
        Assert.Equal((AppPage.Home, 1, 1), (s.Nav.Page, s.Nav.DeviceK, s.Nav.Channel));
        Assert.IsType<HomePageVm>(s.Page);
        Assert.Equal("main?dev=1&ch=1", s.Nav.ToString());
    }

    [Fact]
    public void Nav_OnePageScope_NeverAll_AndKeepsTheDeviceAcrossPages()
    {
        var s = Shell();
        s.SelectDevice(0);
        Assert.True(s.Nav.IsAll);                                   // 主页：总览
        s.Go(AppPage.Curves);
        Assert.Equal((AppPage.Curves, 1), (s.Nav.Page, s.Nav.DeviceK));   // 一次看一台：选第一台
        Assert.False(s.Nav.SelectDevice(0));                        // 曲线页不接受「全部设备」
        s.SelectDevice(3);
        s.Go(AppPage.Alarms);
        Assert.Equal(3, s.Nav.DeviceK);                             // 换页面设备不变
        Assert.True(s.Nav.SelectDevice(0));                         // 报警页可以看全部设备
        s.Go(AppPage.Home);
        Assert.True(s.Nav.IsAll);
        // F5 起曲线 / 报警是真页面（不再是占位）
        Assert.NotNull(s.OpenPage<ParallelReactor.Control.Pages.Curves.CurvePageVm>());
        Assert.NotNull(s.OpenPage<ParallelReactor.Control.Pages.Alarms.AlarmPageVm>());
    }

    [Fact]
    public void Nav_EachDeviceKeepsItsChannel()
    {
        var s = Shell();
        s.SelectChannel(1, 3);
        s.SelectChannel(3, 11);
        Assert.Equal((3, 11), (s.Nav.DeviceK, s.Nav.Channel));
        s.SelectDevice(1);
        Assert.Equal(3, s.Nav.Channel);                             // 回到 1号机：还是 RV3
        Assert.False(s.Nav.SelectChannel(1, 99));                   // 没有这一路：不变
        Assert.Equal(3, s.Nav.Channel);
        s.ResetView();
        Assert.Equal((AppPage.Home, 1, 1), (s.Nav.Page, s.Nav.DeviceK, s.Nav.Channel));
    }

    [Fact]
    public void Nav_DeviceGone_PrunesPerScope()
    {
        var s = Shell("freeze=1&devs=4");
        s.SelectDevice(4);
        s.Fleet.ApplyDemoParams(DemoParams.Parse("freeze=1&devs=1"));
        Assert.Equal((AppPage.Home, 0), (s.Nav.Page, s.Nav.DeviceK));   // 主页：回到总览
        var t = Shell("freeze=1&devs=4");
        t.Go(AppPage.Program);
        t.SelectDevice(4);
        t.Fleet.ApplyDemoParams(DemoParams.Parse("freeze=1&devs=1"));
        Assert.Equal((AppPage.Program, 1), (t.Nav.Page, t.Nav.DeviceK));   // 一次看一台：第一台
    }

    [Fact]
    public void Nav_DemoParams_PageDevChannelPanesAndAlarmList()
    {
        var s = Shell("freeze=1&page=curve&dev=3&ch=11&po=insp&alm=1");
        Assert.Equal((AppPage.Curves, 3, 11), (s.Nav.Page, s.Nav.DeviceK, s.Nav.Channel));
        Assert.True(s.Layout.IsOpen(DockPaneId.Inspector));
        Assert.True(s.Alarms.IsOpen);
        Assert.Equal((3, (int?)11), (s.Selection.K, s.Selection.N));   // 给了通道：检查器显示那一路
        Assert.Equal(AppPage.Records, AppPages.Parse("rec"));
        Assert.Equal(AppPage.Settings, AppPages.Parse("设置"));
        Assert.Null(AppPages.Parse("nope"));
    }

    [Fact]
    public void Inspector_ColumnOnlyOnHomeWithADevice()
    {
        var s = Shell();
        Assert.True(s.ShowInspector);
        s.SelectDevice(0);
        Assert.False(s.ShowInspector);                              // 总览：看板铺到右边
        s.SelectDevice(2);
        s.Go(AppPage.Records);
        Assert.False(s.ShowInspector);                              // 别的页面：页面自己的面板
    }

    // ———————————————————— 停靠布局 ————————————————————

    [Fact]
    public void Dock_Wide_DevicesDocked_InspectorCollapsed_ToggleDocks()
    {
        var L = new DockLayout();
        L.Update(new Size(1920, 1080));
        Assert.Equal((PaneMode.Docked, PaneMode.Collapsed), (L.DevicesMode, L.InspectorMode));
        Assert.Equal((296.0, 30.0), (L.ColumnWidth(DockPaneId.Devices), L.ColumnWidth(DockPaneId.Inspector)));
        L.Toggle(DockPaneId.Inspector);
        Assert.Equal(PaneMode.Docked, L.InspectorMode);
        Assert.Equal(340.0, L.ColumnWidth(DockPaneId.Inspector));
        L.Toggle(DockPaneId.Devices);
        Assert.Equal(PaneMode.Collapsed, L.DevicesMode);
        Assert.False(L.AnyFloat);
        Assert.False(L.CloseFloats());                              // 宽屏没有浮出
        L.Reset();
        Assert.Equal((PaneMode.Docked, PaneMode.Collapsed), (L.DevicesMode, L.InspectorMode));
    }

    [Fact]
    public void Dock_Narrow_BothCollapsed_OpenFloats_ClickOutsideCloses()
    {
        var L = new DockLayout();
        L.Update(new Size(1366, 768));
        Assert.False(L.IsWide);
        Assert.True(L.IsShort);
        Assert.Equal((PaneMode.Collapsed, PaneMode.Collapsed), (L.DevicesMode, L.InspectorMode));
        L.Toggle(DockPaneId.Devices);
        Assert.Equal(PaneMode.Float, L.DevicesMode);
        Assert.Equal(30.0, L.ColumnWidth(DockPaneId.Devices));      // 浮出：竖条照样占 30
        Assert.Equal(330.0, DockLayout.FloatWidth(DockPaneId.Devices));
        Assert.True(L.AnyFloat);
        Assert.True(L.CloseFloats());
        Assert.Equal(PaneMode.Collapsed, L.DevicesMode);
    }

    [Fact]
    public void Dock_Crossing1600_ResetsTheDefaults()
    {
        var L = new DockLayout();
        L.Update(new Size(1920, 1080));
        L.Toggle(DockPaneId.Inspector);                             // 宽屏展开检查器
        L.Update(new Size(1500, 900));                              // 变窄：两个都收起
        Assert.Equal((PaneMode.Collapsed, PaneMode.Collapsed), (L.DevicesMode, L.InspectorMode));
        L.Update(new Size(1700, 900));                              // 变宽：设备面板展开
        Assert.Equal((PaneMode.Docked, PaneMode.Collapsed), (L.DevicesMode, L.InspectorMode));
        L.ApplyParams("pInsp", "dev");
        Assert.Equal((PaneMode.Collapsed, PaneMode.Docked), (L.DevicesMode, L.InspectorMode));
    }

    [Fact]
    public void Dock_FrameScalesBelow1366x768()
    {
        var (f, k) = DockLayout.FrameFor(new Size(1024, 768));
        Assert.Equal(1024 / 1366.0, k, 6);
        Assert.Equal(1366, f.Width, 6);
        Assert.Equal((new Size(1920, 1080), 1.0), DockLayout.FrameFor(new Size(1920, 1080)));
    }

    // ———————————————————— 报警徽标 ————————————————————

    [Fact]
    public async System.Threading.Tasks.Task MenubarBadge_IsTheUnackedCount_AndGoesDownOnAck()
    {
        var s = Shell();
        int want = s.Fleet.Devices.Sum(d => d.Alarms.Count(a => !a.Acked && !(d.EstopActive && a.Code == "ESTOP")));
        Assert.True(want > 0);
        var alarms = s.Menubar.Pages.Single(p => p.Info.Page == AppPage.Alarms);
        Assert.Equal(want.ToString(System.Globalization.CultureInfo.InvariantCulture), alarms.Badge);
        Assert.All(s.Menubar.Pages.Where(p => p.Info.Page != AppPage.Alarms), p => Assert.Null(p.Badge));
        Assert.Equal(want, s.Alarms.Unacked);
        Assert.Equal("未确认 " + want, s.Alarms.AckText);
        Assert.Equal("未确认 " + want, s.Status.AlarmRed);
        // 确认一条：徽标少一
        var d = s.Fleet.Devices.First(x => x.Alarms.Any(a => !a.Acked));
        var a1 = d.Alarms.First(a => !a.Acked);
        var o = await d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: a1.Id));
        Assert.True(o.Ok, o.Message);
        s.Fleet.RefreshNow();
        Assert.Equal(want - 1, s.Menubar.Unacked);
        Assert.Equal(want == 1 ? null : (want - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), alarms.Badge);
    }

    [Fact]
    public void AlarmList_FilterLocateAndHistory()
    {
        var s = Shell();
        s.Alarms.IsOpen = true;
        Assert.Equal(s.Alarms.Active, s.Alarms.Rows.Count);
        Assert.DoesNotContain(s.Alarms.Rows.Zip(s.Alarms.Rows.Skip(1)), p => p.First.Acked && !p.Second.Acked);   // 先未确认
        s.Alarms.Filter = "lo";
        Assert.All(s.Alarms.Rows, r => Assert.False(r.IsErr));
        s.Alarms.Filter = "hi";
        var row = s.Alarms.Rows.First();
        row.LocateCommand.Execute(null);
        Assert.False(s.Alarms.IsOpen);
        Assert.Equal((AppPage.Home, row.D.K), (s.Nav.Page, s.Nav.DeviceK));
        if (row.A.Ch is int n) Assert.Equal(n, s.Nav.Channel);
        s.Alarms.IsOpen = true;
        s.Alarms.HistoryCommand.Execute(null);
        Assert.Equal(AppPage.Alarms, s.Nav.Page);
        Assert.False(s.Alarms.IsOpen);
        Assert.True(s.Escape() == false || !s.Alarms.IsOpen);
    }

    // ———————————————————— 工具条 ————————————————————

    [Fact]
    public void Ribbon_DeviceOptions_FollowThePageScope()
    {
        var s = Shell();
        Assert.Equal("总览 · 全部设备", s.Ribbon.Options[0].Label);
        Assert.Equal(1, s.Ribbon.Selected!.K);
        s.Go(AppPage.Records);
        Assert.Equal("全部设备", s.Ribbon.Options[0].Label);
        s.Go(AppPage.Program);
        Assert.DoesNotContain(s.Ribbon.Options, o => o.K == 0);
        s.Ribbon.Selected = s.Ribbon.Options.Single(o => o.K == 2);
        Assert.Equal(2, s.Nav.DeviceK);                             // 下拉选设备 = 路由选设备
    }

    [Fact]
    public void Ribbon_HomeTools_AndPlaceholderTools()
    {
        var s = Shell();
        Assert.Equal(new[] { "数据", "视图" }, s.Ribbon.Groups.Select(g => g.Group.Title));
        Assert.Equal(new[] { "打标记", "导出", "重置布局", "全屏" }, s.Ribbon.Groups.SelectMany(g => g.Tools).Select(t => t.Label));
        Assert.True(s.Ribbon.Groups[1].Compact);                    // 「视图」窄屏只剩图标
        Assert.False(s.Ribbon.Groups[1].First);
        s.SelectDevice(0);
        Assert.Equal("先选一台设备", s.Ribbon.Groups[0].Tools[0].Reason);   // 总览：不能打标记
        s.Ribbon.Groups[1].Tools[1].Command.Execute(null);
        Assert.True(s.IsFullScreen);
        s.Layout.Toggle(DockPaneId.Inspector);
        s.Ribbon.Groups[1].Tools[0].Command.Execute(null);          // 重置布局
        Assert.Equal(PaneMode.Collapsed, s.Layout.InspectorMode);
        s.Go(AppPage.Records);
        Assert.Equal(new[] { "会话", "导出" }, s.Ribbon.Groups.Select(g => g.Group.Title));
    }

    [Fact]
    public void Capsule_And_Status_Texts()
    {
        var s = Shell();
        Assert.Equal("1号机", s.Capsule.Label);
        Assert.Null(s.Capsule.StopReason);
        s.SelectDevice(0);
        Assert.Equal("全部设备", s.Capsule.Label);
        Assert.Equal("启动程序：先选一台设备", s.Capsule.StartReason);
        s.Capsule.EstopCommand.Execute(null);
        Assert.IsType<ParallelReactor.Control.Shell.Dialogs.EstopDialogVm>(s.Dialogs.Current);
        Assert.True(s.Escape());
        Assert.False(s.Dialogs.IsOpen);
        Assert.StartsWith("已连接 ", s.Status.Conn.Text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", s.Status.ClockText);
        Assert.Equal(ShellVm.ProductTitle + " — 3 台设备", s.StripTitle);
    }
}
