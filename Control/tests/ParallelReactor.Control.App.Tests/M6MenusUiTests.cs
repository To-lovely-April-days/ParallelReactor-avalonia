#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shot;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 1 组：菜单与弹出框在真的主窗口里（无头 Avalonia + Skia，演示数据、冻结，1920 × 1080；弹出层是窗口里的覆盖层）——
/// <list type="bullet">
/// <item>键盘（样稿 shell.js keydown）：菜单栏每个菜单一个 Tab 停靠点，空格打开并进第一项（:focus-visible，内侧环、不画悬停底色），↑ ↓ 循环，
///   Esc 一次就关、焦点回到菜单栏项；«⋯» 上 Enter 打开进第一项，Esc 回到 «⋯»。</item>
/// <item>点在外面：菜单 / 弹出框开着时一下点到急停（对话框打开）；再点同一个按钮 = 关掉。</item>
/// <item>Esc 只关弹出层：放大的面板不还原。</item>
/// <item>打开着的右键菜单跟着刷新：演示申请控制权 5 s 后批准，同一个菜单项从「取消申请控制权」变成「释放控制权」。</item>
/// <item>标题项 32 高（行高 20），别名里的「_」照原样显示。</item>
/// <item>审计：弹出层是单独的一层（不和下面的页面比重叠），字、颜色照常查。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6MenusUiTests
{
    private static Task Run(Action body) => M5ReviewHeadless.RunAsync(body);

    private static void Settle(TopLevel? w = null)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            w?.UpdateLayout();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Win, ShellVm Shell) Open(string query = "", Route? route = null, int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse(query) with { Freeze = true };
        var fleet = FleetVm.CreateDemo(p, a => Dispatcher.UIThread.Post(a));
        var shell = new ShellVm(fleet);
        shell.Toasts.Frozen = true;
        var win = new MainWindow { DataContext = shell, Width = w, Height = h };
        Frozen.Apply(win);
        win.Show();
        Settle(win);
        shell.ApplyDemoParams(p, route ?? Route.Overview);
        Settle(win);
        win.MouseMove(new Point(w - 2, h - 2));
        Settle(win);
        return (win, shell);
    }

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    /// <summary>菜单项自己画的底色（PART_LayoutRoot；透明 = null）。</summary>
    private static Avalonia.Media.Color? Bg(MenuItem m) =>
        m.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot").Background is Avalonia.Media.ISolidColorBrush sb && sb.Color.A > 1 ? sb.Color : null;

    private static void Close(MainWindow win)
    {
        PopupMenus.CloseAll();
        win.Close();
        Settle();
    }

    private static Point Center(Visual v, Visual root) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root) ?? default;

    private static void Click(Window w, Visual v)
    {
        var p = Center(v, w);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Settle(w);
    }

    private static void RightClick(Window w, Visual v)
    {
        var p = Center(v, w);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
        Settle(w);
    }

    private static void Press(Window w, Key key)
    {
        var pk = key switch
        {
            Key.Down => PhysicalKey.ArrowDown, Key.Up => PhysicalKey.ArrowUp, Key.Enter => PhysicalKey.Enter, Key.Space => PhysicalKey.Space,
            Key.Escape => PhysicalKey.Escape, Key.Tab => PhysicalKey.Tab, Key.End => PhysicalKey.End, Key.Home => PhysicalKey.Home, _ => PhysicalKey.None,
        };
        w.KeyPress(key, RawInputModifiers.None, pk, null);
        w.KeyRelease(key, RawInputModifiers.None, pk, null);
        Settle(w);
    }

    private static MenuItem Head(Window w, string title) =>
        w.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().First(m => (m.Header as string) == title);

    private static Button OvMore(Window w, int k) =>
        w.GetVisualDescendants().OfType<Button>().First(b => Behaviors.Pr.GetIcon(b) == "more" && b.DataContext is OverviewSectionVm s && s.K == k);

    private static object? Focused(Window w) => w.FocusManager?.GetFocusedElement();

    private static bool Fv(object? o) => o is StyledElement s && s.Classes.Contains(":focus-visible");

    private static string Header(object? o) => (o as MenuItem)?.Header as string ?? o?.GetType().Name ?? "null";

    [Fact]
    public Task MenuBar_EachMenuIsATabStop_SpaceOpensOnTheFirstItem_EscReturnsToTheMenu() => Run(() =>
    {
        var (win, _) = Open();
        try
        {
            Head(win, "文件").Focus(NavigationMethod.Tab);
            Press(win, Key.Tab);
            Assert.Same(Head(win, "设备"), Focused(win));
            Press(win, Key.Space);
            Assert.True(Head(win, "设备").IsSubMenuOpen);
            var first = Assert.IsType<MenuItem>(Focused(win));
            Assert.Equal("添加设备…", first.Header);
            Assert.True(Fv(first), "键盘进的第一项是 :focus-visible（画内侧的环）");
            var root = first.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            Assert.True(root.Background is not Avalonia.Media.ISolidColorBrush sb || sb.Color.A == 0, "键盘焦点不画悬停底色");
            Assert.True(first.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Focus").IsVisible);
            Press(win, Key.Up);
            Assert.Equal("急停…", Header(Focused(win)));
            Press(win, Key.Down);
            Assert.Equal("添加设备…", Header(Focused(win)));
            Press(win, Key.End);
            Assert.Equal("急停…", Header(Focused(win)));
            Press(win, Key.Escape);
            Assert.False(Head(win, "设备").IsSubMenuOpen);
            Assert.Null(PopupMenus.Current);
            Assert.Same(Head(win, "设备"), Focused(win));
            Assert.True(Fv(Head(win, "设备")));
        }
        finally { Close(win); }
    });

    [Fact]
    public Task OvMore_EnterOpensOnTheFirstItem_EscFocusesTheAnchor() => Run(() =>
    {
        var (win, shell) = Open();
        try
        {
            var more = OvMore(win, 1);
            more.Focus(NavigationMethod.Tab);
            Press(win, Key.Enter);
            Assert.Same(more, PopupMenus.Current?.Anchor);
            Assert.Equal("打开设备页", Header(Focused(win)));
            Assert.True(Fv(Focused(win)));
            Press(win, Key.Down);
            Assert.Equal("设备信息", Header(Focused(win)));
            Assert.True(Fv(Focused(win)));
            Press(win, Key.Escape);
            Assert.Null(PopupMenus.Current);
            Assert.Same(more, Focused(win));
            Assert.True(Fv(more), "Esc 之后焦点回到 «⋯» 并画焦点环（样稿 a.focus()）");
            Assert.Equal("overview", shell.Route.Id);
        }
        finally { Close(win); }
    });

    [Fact]
    public Task OpenMenu_TheFirstClickOnEstopOpensItsDialog_AndTheSameAnchorCloses() => Run(() =>
    {
        var (win, shell) = Open();
        try
        {
            var estop = win.GetVisualDescendants().OfType<GlobalStopCell>().First().GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("estop"));
            Click(win, OvMore(win, 1));
            Assert.NotNull(PopupMenus.Current);
            Click(win, estop);
            Assert.Null(PopupMenus.Current);
            Assert.Equal("estop", shell.Dialogs.Current?.Name);
            shell.Dialogs.Close();
            Settle(win);

            Click(win, Head(win, "设备"));
            Click(win, estop);
            Assert.False(Head(win, "设备").IsSubMenuOpen);
            Assert.Equal("estop", shell.Dialogs.Current?.Name);
            shell.Dialogs.Close();
            Settle(win);

            var add = win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "Add");
            Click(win, add);
            Assert.Same(add, PopupMenus.Current?.Anchor);
            Click(win, add);
            Assert.Null(PopupMenus.Current);
            Assert.True((add.Flyout as Flyout)?.IsOpen != true, "再点「+」= 关掉，不会又打开");
        }
        finally { Close(win); }
    });

    [Fact]
    public Task Esc_ClosesOnlyThePopup_TheMaximizedPanelStays() => Run(() =>
    {
        var (win, _) = Open(route: Route.Device(1));
        try
        {
            var res = win.GetVisualDescendants().OfType<PrPanel>().First(p => p.Title as string == "共用资源");
            Click(win, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
            Assert.True(res.IsMaximized);
            Click(win, win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "More"));
            Assert.NotNull(PopupMenus.Current);
            Press(win, Key.Escape);
            Assert.Null(PopupMenus.Current);
            Assert.True(res.IsMaximized, "第一下 Esc 只关菜单");
            Press(win, Key.Escape);
            Assert.False(res.IsMaximized);
        }
        finally { Close(win); }
    });

    [Fact]
    public Task OpenContextMenu_FollowsTheLeaseGrant_InPlace() => Run(() =>
    {
        var (win, shell) = Open();
        try
        {
            shell.ApplyDemoParams(DemoParams.Parse(""));   // 不冻结：演示数据每次 OnTick 推进 1 s
            var demo = shell.Fleet.DemoData!;
            demo.Act(demo.Device(2)!, Core.Guard.DeviceAction.LeaseRequest, null, quiet: true);
            shell.Fleet.RefreshNow();
            Settle(win);
            var tree = win.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Tree");
            var row = tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == "d2");
            RightClick(win, row);
            var s = Assert.IsType<PopupMenus.Session>(PopupMenus.Current);
            var lease = PopupMenus.Focusables(s).First(m => (m.Header as string)?.Contains("控制权") == true);
            Assert.Equal("取消申请控制权", lease.Header);
            for (int i = 0; i < 6; i++) shell.Fleet.OnTick(true);
            shell.Fleet.RefreshNow();
            Settle(win);
            Assert.Same(s, PopupMenus.Current);
            Assert.Equal("释放控制权", lease.Header);
            Assert.Same(lease, PopupMenus.Focusables(s).First(m => (m.Header as string)?.Contains("控制权") == true));
        }
        finally { Close(win); }
    });

    [Fact]
    public Task DeviceMenu_HeadIs32High_AndAnUnderscoreInTheAliasIsShownAsIs() => Run(() =>
    {
        var (win, shell) = Open();
        try
        {
            var demo = shell.Fleet.DemoData!;
            demo.EditDevice(demo.Device(1)!, new DemoDeviceEdit("Reactor_A"));
            shell.Fleet.RefreshNow();
            Settle(win);
            Click(win, OvMore(win, 1));
            var s = Assert.IsType<PopupMenus.Session>(PopupMenus.Current);
            var items = s.Host!.GetRealizedContainers().OfType<MenuItem>().ToList();
            Assert.Equal(32, items.First(m => m.Classes.Contains("head")).Bounds.Height, 1);
            var last = PopupMenus.Focusables(s).Last();
            var text = last.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name != "PART_Hint" && t.IsEffectivelyVisible);
            Assert.IsNotType<AccessText>(text);
            Assert.Equal("急停 Reactor_A…", text.Text);
            Assert.True(text.TextLayout.TextLines.Sum(l => l.Length) >= text.Text!.Length, "排出来的字一个不少（访问键会吃掉「_」）");
        }
        finally { Close(win); }
    });

    [Fact]
    public Task Reopen_AfterHoverAndEsc_NoItemKeepsTheHoverBackground() => Run(() =>
    {
        // «⋯»「›」的菜单项原地修补、再打开还是同一批对象：Esc 关掉时悬停着的那一项不能还是 :selected（11.1 只在指针移出菜单项时清）
        var (win, _) = Open();
        try
        {
            foreach (var anchorOf in new Func<Button>[] { () => OvMore(win, 1), () => win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "More") })
            {
                Click(win, anchorOf());
                var items = PopupMenus.Focusables(Assert.IsType<PopupMenus.Session>(PopupMenus.Current));
                var hovered = items[1];
                win.MouseMove(Center(hovered, win));
                Settle(win);
                Assert.Contains(":selected", hovered.Classes);
                Assert.NotNull(Bg(hovered));
                Press(win, Key.Escape);
                win.MouseMove(new Point(1918, 1078));
                Settle(win);
                Click(win, anchorOf());
                win.MouseMove(new Point(1918, 1078));
                Settle(win);
                var again = PopupMenus.Focusables(Assert.IsType<PopupMenus.Session>(PopupMenus.Current));
                Assert.Contains(hovered, again);   // 同一批菜单项（原地修补）
                Assert.All(again, m => Assert.DoesNotContain(":selected", m.Classes));
                Assert.All(again, m => Assert.Null(Bg(m)));
                Press(win, Key.Escape);
            }
        }
        finally { Close(win); }
    });

    [Fact]
    public Task KeyboardOpen_MouseChoice_DoesNotPutTheFocusRingBackOnTheAnchor() => Run(() =>
    {
        // 样稿：鼠标选的项（e.detail 1）不 anchor.focus()；键盘打开之后用鼠标点也一样
        var (win, _) = Open();
        try
        {
            var more = OvMore(win, 1);
            more.Focus(NavigationMethod.Tab);
            Press(win, Key.Enter);
            Assert.Equal("打开设备页", Header(Focused(win)));
            var info = PopupMenus.Focusables(PopupMenus.Current!).First(m => m.Header as string == "设备信息");
            Click(win, info);
            Settle(win);
            Assert.True(PopupMenus.Current is { IsMenu: false } infoPop && ReferenceEquals(infoPop.Anchor, more), "设备信息打开了（锚在 «⋯» 上的弹出框）");
            Assert.False(Fv(more), "«⋯» 不画焦点环：" + string.Join(" ", more.Classes));
            Assert.False(Fv(Focused(win)));
            // 菜单栏同样：Enter 打开「视图」，鼠标点「时间轴」
            Head(win, "视图").Focus(NavigationMethod.Tab);
            Press(win, Key.Enter);
            Assert.Equal("总览", Header(Focused(win)));
            Click(win, PopupMenus.Focusables(PopupMenus.Current!).First(m => m.Header as string == "时间轴"));
            Assert.Null(PopupMenus.Current);
            Assert.False(Fv(Head(win, "视图")), "「视图」不画焦点环");
        }
        finally { Close(win); }
    });

    [Fact]
    public Task TallMenus_AreAtMostWindowHeightMinus16_AndFlipUpToFour() => Run(() =>
    {
        // 样稿 .pr-menu max-height: calc(100% − 16px) + placeMenu Math.max(4, …)：1366 × 768、页签很多时「›」「窗口」都在 4..756，里面滚动
        var (win, shell) = Open(w: 1366, h: 768);
        try
        {
            foreach (var d in shell.Fleet.Devices.ToList())
            {
                shell.Navigate(Route.Device(d.K));
                for (int n = 1; n <= 16; n++)
                    if (Route.Parse($"reactor/{d.K}/{n}") is { } r && TabsVm.Routable(shell.Fleet, r)) shell.Navigate(r);
            }
            shell.Navigate(Route.Overview);
            Settle(win);
            Assert.True(shell.Tabs.Tabs.Count > 22, "页签够多（菜单比窗口高）：" + shell.Tabs.Tabs.Count);
            foreach (var open in new Action[]
            {
                () => Click(win, win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "More")),
                () => Click(win, Head(win, "窗口")),
            })
            {
                open();
                win.MouseMove(new Point(1364, 766));
                Settle(win);
                var s = Assert.IsType<PopupMenus.Session>(PopupMenus.Current);
                var rect = RectIn(Assert.IsAssignableFrom<Visual>(s.Root), win);
                Assert.Equal(4, rect.Top, 1);
                Assert.Equal(768 - 16, rect.Height, 1);
                var sv = s.Root!.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(sv.Extent.Height > sv.Viewport.Height + 1, $"里面滚动：{sv.Viewport.Height} / {sv.Extent.Height}");
                Press(win, Key.Escape);
                Assert.Null(PopupMenus.Current);
            }
            // 页签少的时候照常在按钮下面
            Click(win, Head(win, "设备"));
            var dev = RectIn(Assert.IsAssignableFrom<Visual>(PopupMenus.Current!.Root), win);
            Assert.Equal(RectIn(Head(win, "设备"), win).Bottom + 3, dev.Top, 1);
        }
        finally { Close(win); }
    });

    [Fact]
    public Task Audit_TreatsAnOpenMenuAsItsOwnLayer() => Run(() =>
    {
        var (win, _) = Open();
        try
        {
            var closed = ShotAudit.Run(win);
            string statsClosed = ShotAudit.LastStats;
            Click(win, Head(win, "设备"));
            Assert.True(Head(win, "设备").IsSubMenuOpen);
            // 菜单栏项的颜色过渡（PrMenuBarItem 0.12 s；代码里设的主题，frozen 关不掉）走完再审计 —— 否则偶尔截到半路的颜色（色板外）
            System.Threading.Thread.Sleep(250);
            Settle(win);
            var open = ShotAudit.Run(win);
            Assert.Empty(closed);
            Assert.True(open.Count == 0, "菜单盖在页面上不算重叠：" + string.Join("；", open));
            Assert.NotEqual(statsClosed, ShotAudit.LastStats);   // 弹出层也审计了（文字行数多了）
        }
        finally { Close(win); }
    });
}
#endif
