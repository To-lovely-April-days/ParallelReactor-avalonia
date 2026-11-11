using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（外壳状态与共用组件）：
/// <list type="bullet">
/// <item>截图的「新开页面」（同 ShotRunner.Show：ResetDemo → ApplyDemoParams → ResetView → ApplyDemoParams(路由)）之后，时间轴与资源管理器的展开状态
/// 同样稿新开的页面，不取决于前一张截图（以前 ResetView 选中第一台时路由还是上一张的页面，跟随把它的 16 路组展开；sel 在导航之后才套用）。</item>
/// <item>交互走查启动（PR_CONTROL_ROUTE：构造 + ApplyStartParams）也同截图的新开页面（以前构造时跟随第一台展开的 16 路组留着）。</item>
/// <item>视图 › 重置布局把两边的展开状态也回到默认（样稿 resetLayout 的 xOpen = {}、tlOpen = {}：不跟随、不滚动，跟随状态留着）。</item>
/// <item>断线设备的组里合并的未安装行也带断线标记（样稿 .pr-tl-group.is-off .pr-tl-row 同样套在 .is-un 上）。</item>
/// <item>步骤条段间总是 3 px（以前 Math.Round 的银行家舍入让段间成了 4 / 3 / 2 px）。</item>
/// </list>
/// </summary>
public class M5ReviewShellTests
{
    private static ShellVm NewShell()
    {
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse("freeze=1")));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.RefreshChrome();
        return s;
    }

    /// <summary>拍一张（同 ShotRunner.Show 的顺序）。</summary>
    private static void Shot(ShellVm s, string route, string query)
    {
        var p = DemoParams.Parse(query) with { Freeze = true };
        s.Fleet.ResetDemo(p);
        s.ApplyDemoParams(p);
        s.ResetView();
        s.ApplyDemoParams(p, Route.Parse(route));
    }

    /// <summary>时间轴各组「K+行数 / K-行数」（+ 展开、- 收起）| 资源管理器各设备「K+ / K-」。</summary>
    private static string OpenState(ShellVm s) =>
        string.Join(" ", s.Timeline.Groups.Select(g => g.K + (g.IsOpen ? "+" : "-") + g.Rows.Count))
        + " | " + string.Join(" ", s.Fleet.Devices.Select(d => d.K + (s.Explorer.IsOpen(d) ? "+" : "-")));

    private static TlGroupVm Group(ShellVm s, int k) => s.Timeline.Groups.Single(g => g.K == k);

    /// <summary>截图计划里会动到 16 路组的几张（设备页 / 反应釜页 / sel / tl=0 / 换设备组合）。</summary>
    private static readonly (string Route, string Query)[] Shots =
    {
        ("overview", ""),
        ("device/1", ""),
        ("reactor/1/1", ""),
        ("reactor/3/11", "sel=3/11"),
        ("device/3", ""),
        ("device/2", "stress=1"),
        ("overview", "devs=PR16,PR4&sel=2"),
        ("overview", "devs=PR16,PR4&stress=2"),
        ("overview", "sel=1/6"),
        ("reactor/3/16", "stress=2&sel=3/16"),
        ("device/1", "devs=PR8,PR4,PR16"),
        ("overview", "devs=PR8,PR4,PR16"),
        ("reactor/4/1", "devs=4&sel=4/1"),
        ("overview", "tl=0"),
        ("device/2", "tl=0"),
        ("overview", "devs=PR16,PR4&tl=0"),
    };

    [Fact]
    public void ShotReset_OpenState_DoesNotDependOnPreviousShot()
    {
        var solo = Shots.Select(x =>
        {
            var s = NewShell();
            Shot(s, x.Route, x.Query);
            return OpenState(s);
        }).ToList();
        foreach (var order in new[] { Enumerable.Range(0, Shots.Length).ToList(), Enumerable.Range(0, Shots.Length).Reverse().ToList() })
        {
            var s = NewShell();
            foreach (int i in order)
            {
                Shot(s, Shots[i].Route, Shots[i].Query);
                Assert.True(solo[i] == OpenState(s), $"{Shots[i].Route}?{Shots[i].Query}：接着别的截图拍 {OpenState(s)}，单独拍 {solo[i]}");
            }
        }
    }

    [Fact]
    public void ShotReset_OpenState_MatchesFreshMockPage()
    {
        var s = NewShell();
        // stress-device2 接在 device3 后面：16 路的 3号机收起（样稿「16 路已折叠」），不是上一张跟随展开的样子
        Shot(s, "device/3", "");
        Assert.True(Group(s, 3).IsOpen);
        Shot(s, "device/2", "stress=1");
        Assert.Equal(new[] { true, true, false }, s.Timeline.Groups.Select(g => g.IsOpen));
        Assert.EndsWith(" · 16 路已折叠", Group(s, 3).LeaseText);
        Assert.Empty(Group(s, 3).Rows);

        // ov-trend-sel2：sel=2 在导航之前套用 —— 跟随的是 2号机，1号机（16 路）保持收起（样稿「1号机 … 16 路已折叠 | 2号机 … 4 行」）
        Shot(s, "overview", "devs=PR16,PR4&sel=2");
        Assert.Equal((2, (int?)null), (s.Selection.K, s.Selection.N));
        Assert.False(Group(s, 1).IsOpen);
        Assert.EndsWith(" · 16 路已折叠", Group(s, 1).LeaseText);
        Assert.True(Group(s, 2).IsOpen);
        Assert.Equal(4, Group(s, 2).Rows.Count);

        // 页面的对象（16 路）照样跟随展开：反应釜页 3/11
        Shot(s, "reactor/3/11", "sel=3/11");
        Assert.True(Group(s, 3).IsOpen);
        Assert.Equal(16, Group(s, 3).Rows.Count);
        Assert.True(s.Explorer.IsOpen(s.Fleet.Device(3)!));

        // tl=0：样稿先读 tl 再跟随，收起时不跟随 —— 1号机（16 路）保持默认的收起
        Shot(s, "overview", "devs=PR16,PR4&tl=0");
        Assert.True(s.Layout.TimelineFolded);
        Assert.False(Group(s, 1).IsOpen);
    }

    /// <summary>交互走查启动（ShotRunner.StartDemo）：构造 + ApplyStartParams(参数, 路径)。</summary>
    private static ShellVm Start(string? route, string query)
    {
        var p = DemoParams.Parse(query) with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyStartParams(p, Route.Parse(route));
        return s;
    }

    [Theory]
    [InlineData("overview", "devs=PR16,PR4&sel=2")]
    [InlineData("overview", "devs=PR16,PR4&tl=0")]
    [InlineData("device/2", "devs=PR16,PR4")]
    [InlineData(null, "devs=PR16,PR4")]
    [InlineData(null, "sel=3/11")]
    [InlineData("overview", "")]
    [InlineData("reactor/3/11", "sel=3/11")]
    [InlineData("reactor/2/1", "stress=1")]
    public void DemoStart_SameAsShotReset(string? route, string query)
    {
        var start = Start(route, query);
        var shot = NewShell();
        Shot(shot, route ?? "overview", query);
        Assert.Equal(OpenState(shot), OpenState(start));
        Assert.Equal(shot.Route.Id, start.Route.Id);
        Assert.Equal((shot.Selection.K, shot.Selection.N), (start.Selection.K, start.Selection.N));
    }

    [Fact]
    public void DemoStart_MatchesFreshMockPage()
    {
        // overview?devs=PR16,PR4&sel=2（样稿 m-sel2）：跟随的是 2号机，1号机（16 路）保持收起 —— 以前构造时跟随第一台展开的组留着（1+16 2+4）
        var s = Start("overview", "devs=PR16,PR4&sel=2");
        Assert.Equal((2, (int?)null), (s.Selection.K, s.Selection.N));
        Assert.False(Group(s, 1).IsOpen);
        Assert.EndsWith(" · 16 路已折叠", Group(s, 1).LeaseText);
        Assert.True(Group(s, 2).IsOpen);
        Assert.Equal(4, Group(s, 2).Rows.Count);
        // device/2：页面的对象是 2号机，1号机同样收起
        Assert.False(Group(Start("device/2", "devs=PR16,PR4"), 1).IsOpen);
        // tl=0：收起时不跟随
        var folded = Start("overview", "devs=PR16,PR4&tl=0");
        Assert.True(folded.Layout.TimelineFolded);
        Assert.False(Group(folded, 1).IsOpen);
        // 没给路径：到总览，跟随选中的第一台（同样稿新开的总览：16 路的 1号机展开）
        var plain = Start(null, "devs=PR16,PR4");
        Assert.Equal("overview", plain.Route.Id);
        Assert.True(Group(plain, 1).IsOpen);
        Assert.Equal(16, Group(plain, 1).Rows.Count);
    }

    [Fact]
    public void OfflineGroup_UninstalledRow_CarriesOffline()
    {
        var s = NewShell();
        Shot(s, "reactor/1/1", "offline=1");
        var g = Group(s, 1);
        Assert.True(g.IsOff);
        var un = Assert.IsType<TlUnRowVm>(g.Rows[4]);
        Assert.Equal("RV5–RV8 未安装", un.Text);
        Assert.True(un.Offline);
        Assert.All(g.Rows.OfType<TlRowVm>(), r => Assert.True(r.Offline));
        // 在线设备的未安装行不带（默认场景里 1号机在线）
        Shot(s, "reactor/1/1", "");
        Assert.False(Assert.IsType<TlUnRowVm>(Group(s, 1).Rows[4]).Offline);
        // 设备连上 / 断开：同一行就地改
        un = Assert.IsType<TlUnRowVm>(Group(s, 1).Rows[4]);
        s.ApplyDemoParams(DemoParams.Parse("offline=1&freeze=1"));
        Assert.Same(un, Group(s, 1).Rows[4]);
        Assert.True(un.Offline);
        s.ApplyDemoParams(DemoParams.Parse("freeze=1"));
        Assert.Same(un, Group(s, 1).Rows[4]);
        Assert.False(un.Offline);
    }

    [Fact]
    public void ResetLayout_RestoresDefaultOpenState_WithoutFollowing()
    {
        var s = NewShell();
        s.Navigate(Route.Device(3));                     // 跟随：16 路的 3号机在时间轴上展开
        Assert.True(Group(s, 3).IsOpen);
        s.Timeline.ToggleGroup(1);                       // 时间轴：1号机收起
        s.Explorer.Toggle(1);                            // 资源管理器：1号机收起、3号机展开
        s.Explorer.Toggle(3);
        Assert.False(Group(s, 1).IsOpen);
        Assert.False(s.Explorer.IsOpen(s.Fleet.Device(1)!));
        Assert.True(s.Explorer.IsOpen(s.Fleet.Device(3)!));

        s.ResetLayout();
        // 样稿 resetLayout：xOpen = {}、tlOpen = {} 之后只重画 —— ≤ 8 路展开、16 路收起，打开着的 3号机页也不再跟随展开
        Assert.Equal(new[] { true, true, false }, s.Timeline.Groups.Select(g => g.IsOpen));
        Assert.Empty(Group(s, 3).Rows);
        Assert.Equal("1+ 2+ 3-", string.Join(" ", s.Fleet.Devices.Select(d => d.K + (s.Explorer.IsOpen(d) ? "+" : "-"))));
        Assert.Contains(s.Explorer.Rows, r => r.Key == "r1/1");
        Assert.DoesNotContain(s.Explorer.Rows, r => r.Key == "r3/1");
        Assert.Equal("布局已重置", s.Toasts.Items.Last().Text);

        // 跟随状态留着（样稿 resetLayout 不清 tlFollowK）：同一台设备里改选反应釜不再展开它的组；资源管理器照样 xReveal 展开
        s.Select(3, 5);
        Assert.False(Group(s, 3).IsOpen);
        Assert.True(s.Explorer.IsOpen(s.Fleet.Device(3)!));
        // 换一台设备再回来：照常跟随
        s.Navigate(Route.Device(1));
        s.Navigate(Route.Reactor(3, 5));
        Assert.True(Group(s, 3).IsOpen);
    }

    [Fact]
    public void ExplorerResetOpen_DefaultsAndKeepsSelectionMark()
    {
        var s = NewShell();
        s.Select(3, 11);                                 // xReveal：16 路的 3号机展开
        Assert.True(s.Explorer.IsOpen(s.Fleet.Device(3)!));
        s.Explorer.ResetOpen();
        Assert.False(s.Explorer.IsOpen(s.Fleet.Device(3)!));
        Assert.DoesNotContain(s.Explorer.Rows, r => r.Key == "r3/11");
        // 选中的反应釜藏在收起的设备里：设备头标出（左侧 a-line 条，HasSel）
        var head = s.Explorer.Rows.OfType<XDeviceRow>().Single(r => r.D.K == 3);
        Assert.True(head.HasSel);
        Assert.Equal((3, (int?)11), (s.Selection.K, s.Selection.N));
    }

    [Theory]
    [InlineData(99, new double[] { 0, 23, 26, 22, 51, 23, 77, 22 })]     // 样稿 23 / 22 / 23 / 22（以前 22 / 22 / 23 / 23，段间 4 / 3 / 2）
    [InlineData(78, new double[] { 0, 17, 20, 18, 41, 17, 61, 17 })]     // 以前段间 3 / 2 / 3
    [InlineData(140, new double[] { 0, 33, 36, 33, 72, 32, 107, 33 })]   // 以前段间 3 / 4 / 3
    public void StepBar_FourSegments_EvenGaps(double width, double[] xw)
    {
        var got = Enumerable.Range(0, 4).SelectMany(i => { var (x, w) = Controls.Pr.StepBar.SegBounds(i, 4, width); return new[] { x, w }; });
        Assert.Equal(xw, got);
    }

    [Fact]
    public void StepBar_GapsAlwaysThreePx()
    {
        for (int n = 1; n <= 16; n++)
            for (double width = 20; width <= 600; width += 0.25)
            {
                double s = Math.Max(Controls.Pr.StepBar.MinSegment, (width - (n - 1) * Controls.Pr.StepBar.Gap) / n);
                var segs = Enumerable.Range(0, n).Select(i => Controls.Pr.StepBar.SegBounds(i, n, width)).ToList();
                for (int i = 0; i < n; i++)
                {
                    var (x, w) = segs[i];
                    Assert.True(x == Math.Floor(x) && w == Math.Floor(w), $"n={n} w={width} 段 {i}：边不在整像素上（{x}, {w}）");
                    Assert.True(w == Math.Floor(s) || w == Math.Ceiling(s), $"n={n} w={width} 段 {i}：宽 {w}，应为 {s:0.###} 取整");
                    if (i > 0) Assert.True(x - (segs[i - 1].X + segs[i - 1].W) == 3, $"n={n} w={width}：段 {i - 1}–{i} 之间 {x - segs[i - 1].X - segs[i - 1].W} px");
                }
            }
    }
}
