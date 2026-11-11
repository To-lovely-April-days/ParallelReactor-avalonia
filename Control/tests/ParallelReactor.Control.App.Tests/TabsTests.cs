using System.Linq;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 文档页签（PORT-PLAN §5 M2「TabsTests」；样稿 shell.js defaultTabs / pruneTabs / layoutTabs）：默认页签、总览固定、打开 / 关闭 / 切换、
/// 按路由去重、不存在的对象回到总览、溢出时的隐藏顺序；顺带核对选择与资源管理器跟着导航走。
/// 用真的外壳视图模型（ShellVm）+ 演示数据（冻结），不创建窗口。
/// </summary>
public class TabsTests
{
    private static ShellVm Shell(string query = "freeze=1")
    {
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse(query)));
        s.Toasts.Frozen = true;   // 测试里没有界面线程的定时器：toast 不自动消失
        return s;
    }

    private static string[] Ids(ShellVm s) => s.Tabs.Tabs.Select(t => t.Id).ToArray();

    [Fact]
    public void Defaults_SameAsMock()
    {
        var s = Shell();
        Assert.Equal(new[] { "overview", "device/1", "reactor/1/1", "reactor/1/3", "reactor/2/1", "device/3", "placeholder/curves" }, Ids(s));
        var ov = s.Tabs.Tabs[0];
        Assert.True(ov.Pinned);
        Assert.Null(ov.CloseCommand);
        Assert.True(ov.IsActive);
        Assert.All(s.Tabs.Tabs.Skip(1), t => Assert.False(t.Pinned));
        Assert.Equal("1号机 · RV1", s.Tabs.Tabs[2].Label);
        Assert.Equal("1号机", s.Tabs.Tabs[2].Alias);
        Assert.True(s.Tabs.Tabs[2].HasRvPart);
        Assert.True(s.Tabs.Tabs.Single(t => t.Id == "device/3").ErrAlarm);    // 3号机 RV11 超压报警：红点
        Assert.False(s.Tabs.Tabs.Single(t => t.Id == "device/1").ErrAlarm);   // 1号机只有提醒：没有红点
    }

    [Fact]
    public void Overview_IsPinned_CannotBeClosed()
    {
        var s = Shell();
        s.Tabs.Close(s.Tabs.Tabs[0]);
        Assert.Equal("overview", Ids(s)[0]);
        s.CloseOtherTabs();
        Assert.Equal(new[] { "overview" }, Ids(s));
    }

    [Fact]
    public void Navigate_AppendsOnce_DeDupsByRoute()
    {
        var s = Shell();
        int n = s.Tabs.Tabs.Count;
        s.Navigate(Route.Device(2));
        Assert.Equal("device/2", Ids(s)[^1]);
        Assert.Equal(n + 1, s.Tabs.Tabs.Count);
        Assert.Single(s.Tabs.Tabs, t => t.IsActive);
        Assert.True(s.Tabs.Tabs[^1].IsActive);
        s.Navigate(Route.Overview);
        s.Navigate(Route.Device(2));
        Assert.Equal(n + 1, s.Tabs.Tabs.Count);
        s.Navigate(Route.Reactor(1, 3));                                       // 已有的页签：只切过去
        Assert.Equal(n + 1, s.Tabs.Tabs.Count);
        Assert.True(s.Tabs.Tabs.Single(t => t.Id == "reactor/1/3").IsActive);
        Assert.Equal(1, s.Pages.Count(p => p.Route.Id == "reactor/1/3"));      // 每个页签一个页面
    }

    [Fact]
    public void CloseActive_GoesToLeftNeighbour_AndReleasesPage()
    {
        var s = Shell();
        s.Navigate(Route.Reactor(1, 3));
        var tab = s.Tabs.Tabs.Single(t => t.Id == "reactor/1/3");
        tab.CloseCommand!.Execute(null);
        Assert.DoesNotContain("reactor/1/3", Ids(s));
        Assert.Equal("reactor/1/1", s.Route.Id);
        Assert.True(s.Tabs.Tabs.Single(t => t.Id == "reactor/1/1").IsActive);
        Assert.DoesNotContain(s.Pages, p => p.Route.Id == "reactor/1/3");
    }

    [Fact]
    public void CloseOthers_KeepsPinnedAndActive()
    {
        var s = Shell();
        s.Navigate(Route.Device(3));
        s.CloseOtherTabs();
        Assert.Equal(new[] { "overview", "device/3" }, Ids(s));
    }

    [Fact]
    public void DefaultTabs_SkipMissingDevicesAndReactors()
    {
        var s = Shell("devs=1&freeze=1");
        Assert.Equal(new[] { "overview", "device/1", "reactor/1/1", "reactor/1/3", "placeholder/curves" }, Ids(s));
        s.Navigate(Route.Device(9));                                           // 不存在：回到总览，不加页签
        Assert.Equal("overview", s.Route.Id);
        Assert.Equal(5, s.Tabs.Tabs.Count);
        s.Navigate(Route.Reactor(1, 9));                                       // 没有这个位置（HT-PR8 只有 8 路）：同上
        Assert.Equal("overview", s.Route.Id);
        // M5：未安装的位置能打开（样稿 route → reactor.js unPanel：一页说明），页签只在它是当前页签时留着（样稿 pruneTabs + ensureRouteTab）
        s.Navigate(Route.Reactor(1, 6));
        Assert.Equal("reactor/1/6", s.Route.Id);
        Assert.Contains("reactor/1/6", Ids(s));
        s.Navigate(Route.Overview);
        s.RefreshChrome();
        Assert.DoesNotContain("reactor/1/6", Ids(s));
        Assert.Equal(new[] { "overview", "device/1", "reactor/1/1", "reactor/1/3", "placeholder/curves" }, Ids(s));
    }

    [Fact]
    public void Overflow_HidingOrder_FirstAndActiveStay_LaterNarrowOnesStillFit()
    {
        var w = new double[] { 80, 60, 90, 100, 70, 60 };
        Assert.Equal(new[] { true, true, false, true, false, true }, TabOverflowPanel.Visible(w, 300, 3));
        Assert.Equal(new[] { true, true, true, false, true, false }, TabOverflowPanel.Visible(w, 300, -1));
        Assert.Equal(new[] { true, false, false, false, false, true }, TabOverflowPanel.Visible(w, 140, 5));
        Assert.All(TabOverflowPanel.Visible(w, 460, 2), Assert.True);         // 正好放下
        Assert.All(TabOverflowPanel.Visible(w, double.PositiveInfinity, 0), Assert.True);
        Assert.Empty(TabOverflowPanel.Visible(System.Array.Empty<double>(), 100, -1));
    }

    [Fact]
    public void Selection_FollowsNavigation_AndExplorerRevealsIt()
    {
        var s = Shell();
        Assert.Equal(1, s.SelectedDevice?.K);                                   // 没有选中时第一台
        Assert.Null(s.SelectedRv);
        var rows = s.Explorer.Rows;
        Assert.Contains(rows, r => r.Key == "d3");
        Assert.DoesNotContain(rows, r => r.Key == "r3/11");                    // 16 路默认收起
        Assert.Contains(rows.OfType<XUninstalledRow>(), r => r.Text == "RV5–RV8 未安装");
        Assert.True(((XDeviceRow)rows.Single(r => r.Key == "d1")).IsSelected);

        s.Navigate(Route.Reactor(3, 11));                                      // 选中收起的设备里的反应釜：展开它
        Assert.Equal((3, 11), (s.SelectedDevice!.K, s.SelectedRv!.Value));
        var row = rows.OfType<XReactorRow>().Single(r => r.Key == "r3/11");
        Assert.True(row.IsSelected);
        Assert.Same(row, s.Explorer.SelectedRow);
        Assert.False(((XDeviceRow)rows.Single(r => r.Key == "d1")).IsSelected);

        s.Navigate(Route.Overview);                                            // 总览不改选择
        Assert.Equal((3, 11), (s.SelectedDevice!.K, s.SelectedRv!.Value));

        s.Explorer.Toggle(3);                                                  // 手动收起：设备头标出「选中的在这里面」
        var head = (XDeviceRow)rows.Single(r => r.Key == "d3");
        Assert.True(head.HasSel);
        Assert.Contains("选中：RV11", head.Tip);
        Assert.DoesNotContain(rows, r => r.Key == "r3/11");

        s.Explorer.SelectedRow = rows.Single(r => r.Key == "d2");              // 单击设备头 = 选中设备
        Assert.Equal(2, s.SelectedDevice!.K);
        Assert.Null(s.SelectedRv);
    }

    [Fact]
    public void Explorer_RowsKeepIdentity_AcrossRefreshes()
    {
        var s = Shell();
        var before = s.Explorer.Rows.ToList();
        s.Fleet.RefreshNow();
        s.RefreshChrome();
        Assert.Equal(before, s.Explorer.Rows.ToList());                        // 同一批实例、同样的顺序
        Assert.Equal("3 台", s.Explorer.CountText);
        // 设备之间：下 6 + 1 px 分隔线 + 上 2；最后一台下面只有 6
        var gaps = s.Explorer.Rows.OfType<XGapRow>().ToList();
        Assert.Equal(2, gaps[0].Height);
        Assert.Contains(gaps, g => g.HasLine && g.Height == 11 && g.LineAt == 8);
        Assert.Equal(6, gaps[^1].Height);
        Assert.False(gaps[^1].HasLine);
    }

    [Fact]
    public void FourDevices_OfflineDeviceGetsDetailRow()
    {
        var s = Shell("devs=4&freeze=1");
        Assert.Contains(s.Explorer.Rows.OfType<XOffRow>(), r => r.D.K == 4 && r.IsConn);
        Assert.Equal("4 台", s.Explorer.CountText);
        Assert.False(s.Fleet.Device(4)!.Reactors[0].Key == Core.State.StateKey.React);
    }
}
