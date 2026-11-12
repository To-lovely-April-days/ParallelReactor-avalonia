using System;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 2 组（菜单以外的键盘与焦点）的视图模型部分：资源管理器的树 —— 样稿 shell.js treeKey（↑ ↓ Home End ← → Enter 空格）与
/// xTabTarget（Tab 进树落在哪一行），以及「Enter 打开页签时浮层不收起」的导航选项（ShellVm.Navigate(route, keepFloat)）。
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口；控件上的焦点、焦点环、Tab 顺序见 M6KeyboardHeadlessTests（Debug）与 ShellChecks.M6G2。
/// </summary>
public class M6KeyboardTests
{
    private static ShellVm Shell(string query = "freeze=1")
    {
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse(query)));
        s.Toasts.Frozen = true;
        return s;
    }

    private static XRow Row(ShellVm s, string key) => s.Explorer.Rows.Single(r => r.Key == key);

    private static string Sel(ShellVm s) => s.Selection.ToString();

    [Fact]
    public void TreeKey_UpDownHomeEnd_MoveAndSelect_VisibleRowsOnly_NoWrap()
    {
        var s = Shell();
        var x = s.Explorer;
        s.Select(1, 2);
        var to = x.TreeKey(Row(s, "r1/2"), XTreeKey.Down);
        Assert.Equal(("r1/3", "1/3"), (to.Key, Sel(s)));
        to = x.TreeKey(to, XTreeKey.Up);
        Assert.Equal(("r1/2", "1/2"), (to.Key, Sel(s)));
        // 不可选的行（未安装的合并行、间隔）不算：RV4 往下是 2号机的设备头
        to = x.TreeKey(Row(s, "r1/4"), XTreeKey.Down);
        Assert.Equal(("d2", "2"), (to.Key, Sel(s)));
        to = x.TreeKey(to, XTreeKey.Home);
        Assert.Equal(("d1", "1"), (to.Key, Sel(s)));
        Assert.Same(to, x.TreeKey(to, XTreeKey.Up));                           // 第一项上 ↑ 不动（不循环）
        Assert.Equal("1", Sel(s));
        to = x.TreeKey(to, XTreeKey.End);
        var last = x.Rows.Last(r => r.Selectable);
        Assert.Equal("d3", last.Key);                                          // 16 路的 3号机默认收起：最后一项是它的设备头
        Assert.Same(last, to);
        Assert.Equal("3", Sel(s));
        Assert.Same(to, x.TreeKey(to, XTreeKey.Down));                         // 最后一项上 ↓ 不动
        Assert.Equal("overview", s.Route.Id);                                  // 只选中，不导航
    }

    [Fact]
    public void TreeKey_RightLeft_ExpandCollapse_EnterAndLeaveTheDevice()
    {
        var s = Shell();
        var x = s.Explorer;
        var d1 = Row(s, "d1");
        var dev1 = s.Fleet.Device(1)!;
        Assert.Same(d1, x.TreeKey(d1, XTreeKey.Left));                         // 展开的设备头：← 收起，焦点留着
        Assert.False(x.IsOpen(dev1));
        Assert.DoesNotContain(x.Rows, r => r.Key == "r1/1");
        Assert.Same(d1, x.TreeKey(d1, XTreeKey.Left));                         // 收起的设备头：← 不做事
        Assert.False(x.IsOpen(dev1));
        Assert.Same(d1, x.TreeKey(d1, XTreeKey.Right));                        // 收起的设备头：→ 展开，选中不变
        Assert.True(x.IsOpen(dev1));
        Assert.Equal("1", Sel(s));
        var r11 = x.TreeKey(d1, XTreeKey.Right);                               // 展开的设备头：→ 第一个反应釜并选中
        Assert.Equal(("r1/1", "1/1"), (r11.Key, Sel(s)));
        Assert.Same(r11, x.TreeKey(r11, XTreeKey.Right));                      // 反应釜行：→ 不做事
        Assert.Equal("1/1", Sel(s));
        Assert.Same(d1, x.TreeKey(r11, XTreeKey.Left));                        // 反应釜行：← 回到设备头并选中设备
        Assert.Equal("1", Sel(s));

        // 选中的反应釜藏在收起的设备里：→ 展开（选中照旧是这个反应釜）
        s.Select(3, 11);
        x.Toggle(3);
        var d3 = (XDeviceRow)Row(s, "d3");
        Assert.True(d3.HasSel);
        Assert.Same(d3, x.TreeKey(d3, XTreeKey.Right));
        Assert.Equal("3/11", Sel(s));
        Assert.Contains(x.Rows, r => r.Key == "r3/11");
        Assert.Equal("overview", s.Route.Id);
    }

    [Fact]
    public void TreeKey_EnterOpensTheTab_KeepsTheFloat_SpaceOnlySelects()
    {
        var s = Shell();
        var x = s.Explorer;
        Assert.True(s.Layout.IsNarrow);                                        // 没有窗口：外框按 1366 排，资源管理器是浮层
        s.ToggleExplorer();
        Assert.True(s.Layout.ExplorerFloat);
        var r13 = Row(s, "r1/3");
        Assert.Same(r13, x.TreeKey(r13, XTreeKey.Enter));
        Assert.Equal("reactor/1/3", s.Route.Id);
        Assert.True(s.Layout.ExplorerFloat);                                   // 样稿 treeKey 只导航，浮层不收起
        var r12 = Row(s, "r1/2");
        Assert.Same(r12, x.TreeKey(r12, XTreeKey.Space));
        Assert.Equal("1/2", Sel(s));
        Assert.Equal("reactor/1/3", s.Route.Id);                               // 空格只选中
        x.Open(Row(s, "d2"));                                                  // 指针的打开（双击）照旧收起浮层
        Assert.Equal("device/2", s.Route.Id);
        Assert.False(s.Layout.ExplorerFloat);
    }

    [Fact]
    public void Navigate_KeepFloat_OnlyWhenAsked()
    {
        var s = Shell();
        s.ToggleExplorer();
        s.Navigate(Route.Device(1), keepFloat: true);
        Assert.True(s.Layout.ExplorerFloat);
        Assert.Equal("device/1", s.Route.Id);
        s.Navigate(Route.Reactor(1, 1));                                       // 别的导航（页签、菜单、指针）照旧收起
        Assert.False(s.Layout.ExplorerFloat);
    }

    [Fact]
    public void TabTarget_SameAsMockXTabTarget()
    {
        var s = Shell();
        var x = s.Explorer;
        int refreshed = 0;
        x.Refreshed += (_, _) => refreshed++;
        Assert.Equal("d1", x.TabTarget?.Key);                                  // 没选过：第一台（选中第一台）
        s.Select(1, 2);
        Assert.Equal("r1/2", x.TabTarget?.Key);
        Assert.True(refreshed > 0);
        s.Select(2);
        Assert.Equal("d2", x.TabTarget?.Key);
        s.Select(3, 11);                                                       // 选中收起的设备里的反应釜：展开它
        Assert.Equal("r3/11", x.TabTarget?.Key);
        x.Toggle(3);                                                           // 再收起：选中的藏在里面 → 设备头（样稿 xTabTarget）
        Assert.Null(x.SelectedRow);
        Assert.Equal("d3", x.TabTarget?.Key);
        s.Select(1, 6);                                                        // 未安装的位置（树里没有这一行）：设备头（样稿这时一行都不给）
        Assert.Equal("1/6", Sel(s));
        Assert.Equal("d1", x.TabTarget?.Key);
    }
}
