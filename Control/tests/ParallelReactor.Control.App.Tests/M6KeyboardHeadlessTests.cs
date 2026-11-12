#if DEBUG
using System;
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
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 2 组在真的窗口里核对（无头 Avalonia，共用 M5ReviewHeadless 的会话）：
/// <list type="bullet">
/// <item>分段控件同样稿 ui.seg：每段都是 Tab 停靠点、方向键不切换；面板标题页签（PTabs）照旧只有当前项是停靠点、← → 切换。</item>
/// <item>资源管理器的树：Tab 进来落在选中的行，↑ ↓ ← → 之后焦点环（:focus-visible）留着（ListBox 自己的方向键会把它弄没），Enter 打开页签、焦点留在行上。</item>
/// <item>资源管理器收起（浮层 Esc、停靠时收起、键盘按「收起」）时焦点在里面：焦点清掉，树的键与方向键什么都不做，Tab / Shift+Tab 从资源管理器的位置接着走。</item>
/// <item>外框的 Tab 顺序：资源管理器在菜单栏之后；页签栏里 页签 → 「+」→「›」（样稿 DOM 顺序），页签的 × 不是停靠点；图标栏 设备 … 设置 →「收起」；Shift+Tab 反过来。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6KeyboardHeadlessTests
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

    private static void Press(Window w, Key key, PhysicalKey pk, RawInputModifiers m = RawInputModifiers.None)
    {
        w.KeyPress(key, m, pk, null);
        w.KeyRelease(key, m, pk, null);
        Settle(w);
    }

    private static object? Focused(Window w) => w.FocusManager?.GetFocusedElement();

    private static bool Ring(InputElement e) => e.IsFocused && e.Classes.Contains(":focus-visible");

    /// <summary>新外壳的主窗口（演示数据、冻结），套用地址参数后打开 <paramref name="route"/>（同截图的 ShotRunner.Show）。</summary>
    private static (MainWindow Win, ShellVm Shell) Open(string query, double w, double h, Route route)
    {
        var p = DemoParams.Parse(query) with { Freeze = true };
        var fleet = FleetVm.CreateDemo(p, a => Dispatcher.UIThread.Post(a));
        var shell = new ShellVm(fleet);
        shell.Toasts.Frozen = true;
        var win = new MainWindow { DataContext = shell, Width = w, Height = h };
        Frozen.Apply(win);
        win.Show();
        Settle(win);
        shell.ApplyDemoParams(p, route);
        Settle(win);
        return (win, shell);
    }

    [Fact]
    public Task Seg_EverySegmentIsATabStop_ArrowsDoNothing_PTabsStillRove() => Run(() =>
    {
        var seg = new SegControl { Options = { new SegOption { Value = "10", Label = "10" }, new SegOption { Value = "30", Label = "30" }, new SegOption { Value = "60", Label = "60" } }, Value = "30" };
        var pt = new PTabs { Options = { new SegOption { Value = "a", Label = "釜体" }, new SegOption { Value = "b", Label = "属性查看器" } }, Value = "a" };
        var before = new Button { Content = "前" };
        var win = M5ReviewHeadless.ShellWindow(new StackPanel { Children = { before, seg, pt } }, 600, 300);
        win.Show();
        Settle(win);
        try
        {
            var segs = seg.GetVisualDescendants().OfType<PrToggle>().ToList();
            var tabs = pt.GetVisualDescendants().OfType<PrToggle>().ToList();
            Assert.Equal(3, segs.Count);
            Assert.All(segs, b => Assert.True(b.IsTabStop));
            Assert.Equal(new[] { true, false }, tabs.Select(b => b.IsTabStop));
            before.Focus(NavigationMethod.Tab);
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.Same(segs[0], Focused(win));
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.Same(segs[1], Focused(win));
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.Same(segs[2], Focused(win));
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.Same(tabs[0], Focused(win));                                // 面板页签：只停当前项

            segs[0].Focus(NavigationMethod.Tab);
            Press(win, Key.Right, PhysicalKey.ArrowRight);
            Press(win, Key.End, PhysicalKey.End);
            Assert.Equal("30", seg.Value);                                     // 分段：方向键不切换、焦点不动
            Assert.Same(segs[0], Focused(win));
            Press(win, Key.Space, PhysicalKey.Space);
            Assert.Equal("10", seg.Value);                                     // 空格照常点击

            tabs[0].Focus(NavigationMethod.Tab);
            Press(win, Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal("b", pt.Value);                                       // 面板页签：→ 切到下一个并聚焦它
            Assert.Same(tabs[1], Focused(win));
            Assert.Equal(new[] { false, true }, tabs.Select(b => b.IsTabStop));
        }
        finally { win.Close(); }
    });

    [Fact]
    public Task ExplorerTree_KeysKeepTheFocusRing_EnterOpensTheTab() => Run(() =>
    {
        var (win, shell) = Open("", 1920, 1080, Route.Overview);
        try
        {
            shell.Select(1, 2);
            Settle(win);
            var explorer = win.GetVisualDescendants().OfType<ExplorerView>().Single();
            var tree = explorer.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Tree");
            ListBoxItem Item(string key) => tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);
            var close = explorer.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, shell.Explorer.ToggleCommand));

            close.Focus(NavigationMethod.Tab);
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.True(Ring(Item("r1/2")), "Tab 进树：落在选中的 RV2，焦点环在");
            Press(win, Key.Down, PhysicalKey.ArrowDown);
            Assert.True(Ring(Item("r1/3")), "↓：焦点（带焦点环）到 RV3");
            Assert.Equal("1/3", shell.Selection.ToString());
            Press(win, Key.Left, PhysicalKey.ArrowLeft);
            Assert.True(Ring(Item("d1")), "反应釜行上 ←：回到设备头");
            Assert.Equal("1", shell.Selection.ToString());
            Press(win, Key.Left, PhysicalKey.ArrowLeft);
            Assert.DoesNotContain(shell.Explorer.Rows, r => r.Key == "r1/1");
            Assert.True(Ring(Item("d1")), "← 收起之后焦点留在设备头");
            Press(win, Key.Right, PhysicalKey.ArrowRight);
            Press(win, Key.Right, PhysicalKey.ArrowRight);
            Assert.True(Ring(Item("r1/1")), "→ 展开、再 → 进到 RV1");
            Press(win, Key.Enter, PhysicalKey.Enter);
            Assert.Equal("reactor/1/1", shell.Route.Id);
            Assert.True(Ring(Item("r1/1")), "Enter 打开页签，焦点留在这一行");

            // 树之后是页签栏的第一个页签（资源管理器在菜单栏之后、页签栏之前：ShellView 的子元素顺序）
            Press(win, Key.Tab, PhysicalKey.Tab);
            Assert.True(Focused(win) is NavToggle { DataContext: DocTabVm { Id: "overview" } }, $"树之后是第一个页签（{Focused(win)}）");
        }
        finally { win.Close(); }
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public Task ExplorerHidden_FocusIsCleared_TreeKeysDoNothing_TabResumesAfterIt(double w, double h) => Run(() =>
    {
        var (win, shell) = Open("", w, h, Route.Overview);
        try
        {
            if (!shell.Layout.ExplorerOpen) { shell.ToggleExplorer(); Settle(win); }
            shell.Select(1, 2);
            Settle(win);
            var explorer = win.GetVisualDescendants().OfType<ExplorerView>().Single();
            var tree = explorer.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Tree");
            ListBoxItem Item(string key) => tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);

            Item("r1/2").Focus(NavigationMethod.Tab);
            Press(win, Key.Enter, PhysicalKey.Enter);
            Assert.Equal("reactor/1/2", shell.Route.Id);
            Assert.True(shell.Layout.ExplorerOpen, "Enter 打开页签：资源管理器（浮层也是）还开着");
            if (shell.Layout.ExplorerFloat) Press(win, Key.Escape, PhysicalKey.Escape);   // 浮层：Esc 收起
            else { shell.ToggleExplorer(); Settle(win); }                                  // 停靠：收起
            Assert.False(explorer.IsEffectivelyVisible);
            Assert.Null(Focused(win));                                                    // 焦点清掉（样稿 display: none → body）

            Press(win, Key.Down, PhysicalKey.ArrowDown);
            Press(win, Key.Enter, PhysicalKey.Enter);
            Press(win, Key.Left, PhysicalKey.ArrowLeft);
            Assert.Equal("1/2", shell.Selection.ToString());                              // 树的键不起作用
            Assert.Equal("reactor/1/2", shell.Route.Id);
            Assert.True(shell.Explorer.IsOpen(shell.Fleet.Device(1)!), "← 没有收起 1号机");
            Assert.Null(Focused(win));                                                    // 方向键也不把焦点挪到菜单栏「文件」（Avalonia 没有焦点时的方向键）

            Press(win, Key.Tab, PhysicalKey.Tab);                                         // 从资源管理器的位置接着走：第一个页签
            Assert.True(Focused(win) is NavToggle { DataContext: DocTabVm { Id: "overview" } }, $"Tab 落在第一个页签（{Focused(win)}）");

            // 键盘按「收起」：焦点清掉，Shift+Tab 回到菜单栏
            shell.ToggleExplorer();
            Settle(win);
            var close = explorer.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, shell.Explorer.ToggleCommand));
            close.Focus(NavigationMethod.Tab);
            Press(win, Key.Enter, PhysicalKey.Enter);
            Assert.False(shell.Layout.ExplorerOpen);
            Assert.Null(Focused(win));
            Press(win, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.True(Focused(win) is Visual v && v.FindAncestorOfType<MenuBarView>() != null, $"Shift+Tab 回到菜单栏（{Focused(win)}）");

            // 焦点到了别处之后不再接：再收起一次（焦点不在资源管理器里）不清焦点
            shell.ToggleExplorer();
            Settle(win);
            var first = win.GetVisualDescendants().OfType<NavToggle>().First(t => t.DataContext is DocTabVm { Id: "overview" });
            first.Focus(NavigationMethod.Tab);
            shell.ToggleExplorer();
            Settle(win);
            Assert.Same(first, Focused(win));
        }
        finally { win.Close(); }
    });

    [Fact]
    public Task Sidebar_TabOrder_ItemsThenCollapseLast() => Run(() =>
    {
        var (win, shell) = Open("", 1920, 1080, Route.Overview);
        try
        {
            var side = win.GetVisualDescendants().OfType<SidebarView>().Single();
            var items = side.GetVisualDescendants().OfType<NavToggle>().ToList();
            var bottom = items.Single(t => t.Classes.Contains("bottom"));
            var top = items.Where(t => !ReferenceEquals(t, bottom)).ToList();
            Assert.Equal(new[] { "设备", "总览", "程序", "曲线", "记录", "报警", "测试", "设置" }, top.Select(t => t.Content as string));

            var stop = win.GetVisualDescendants().OfType<GlobalStopCell>().Single().GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible && b.IsTabStop);
            stop.Focus(NavigationMethod.Tab);
            var order = new System.Collections.Generic.List<object?>();
            for (int i = 0; i < items.Count; i++)
            {
                Press(win, Key.Tab, PhysicalKey.Tab);
                order.Add(Focused(win));
            }
            Assert.Equal(top.Cast<object?>().Append(bottom), order);                    // 急停格之后：设备 … 设置，最后「收起」
            Press(win, Key.Tab, PhysicalKey.Tab);
            var ws = Focused(win) as Visual;
            Assert.True(ws?.FindAncestorOfType<PageHost>() != null, $"「收起」之后是工作区（{ws}）");
            Press(win, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(bottom, Focused(win));                                            // Shift+Tab 从工作区回到「收起」
        }
        finally { win.Close(); }
    });

    [Fact]
    public Task TabBar_TabsThenAddThenMore_CloseIsNoTabStop() => Run(() =>
    {
        var (win, shell) = Open("", 1920, 1080, Route.Device(1));
        try
        {
            var bar = win.GetVisualDescendants().OfType<TabBarView>().Single();
            var tabs = bar.GetVisualDescendants().OfType<NavToggle>().ToList();
            var add = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Add");
            var more = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "More");
            Assert.All(bar.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "PART_Close"), b => Assert.False(b.IsTabStop));

            tabs[0].Focus(NavigationMethod.Tab);
            var order = new System.Collections.Generic.List<object?> { tabs[0] };
            for (int i = 0; i < tabs.Count + 1; i++)
            {
                Press(win, Key.Tab, PhysicalKey.Tab);
                order.Add(Focused(win));
            }
            Assert.Equal(tabs.Cast<object?>().Concat(new object?[] { add, more }), order);

            Press(win, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(add, Focused(win));
            Press(win, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(tabs[^1], Focused(win));                               // Shift+Tab 反过来：「+」之前是最后一个页签

            // 键盘焦点在没打开的页签上：× 出现、名字左移 10 px（同悬停，样稿 :focus-within）
            var rv = tabs.Single(t => (t.DataContext as DocTabVm)?.Id == "reactor/1/1");
            rv.Focus(NavigationMethod.Tab);
            Settle(win);
            var x = rv.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Close");
            var row = rv.GetVisualDescendants().OfType<DockPanel>().Single(d => d.Name == "PART_Row");
            Assert.Equal(1, x.Opacity);
            Assert.Equal(-10, row.RenderTransform?.Value.M31 ?? 0, 3);
            Press(win, Key.Enter, PhysicalKey.Enter);
            Assert.Equal("reactor/1/1", shell.Route.Id);
        }
        finally { win.Close(); }
    });
}
#endif
