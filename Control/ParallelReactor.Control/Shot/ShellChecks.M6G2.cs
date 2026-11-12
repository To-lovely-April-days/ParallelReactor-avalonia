#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 2 组：菜单以外的键盘与焦点（样稿 shell.js treeKey / xTabTarget / frameHTML、ui.seg、base.css / shell.css 的焦点环）：
/// <list type="bullet">
/// <item>区域的 Tab 顺序：菜单栏 → 资源管理器（添加 · ⓘ · 收起 · 树的一行）→ 页签栏 → 急停格 → 图标栏（设备 … 设置 →「收起」）→ 工作区 → 检查器 → 时间轴 → 状态栏
///   （1366 打开浮层）。</item>
/// <item>资源管理器的树：Tab 只停在一行（选中的行；选中的反应釜藏在收起的设备里时是设备头）；↑ ↓ Home End 移动并选中、焦点环留着；← → 收起 / 展开 / 进出反应釜；
///   Enter 打开页签（浮层不收起；指针的打开照旧收起）、空格选中。资源管理器收起（浮层 Esc、停靠时收起、键盘按「收起」、1366 点遮罩 / 指针打开）时焦点清掉，
///   之后 ↓ Enter 不动树，Tab 接着走到第一个页签、Shift+Tab 回到菜单栏。</item>
/// <item>没有 Fluent 的黑框：设备页卡片、未安装位置页的位置按钮与芯片、程序库的行只画自己的 a-d 环；未安装的位置、没有报警时状态栏的报警项不是 Tab 停靠点。</item>
/// <item>文档页签：× 不是 Tab 停靠点，Tab 走完页签到「+」「›」；键盘焦点在别的页签上时 × 出现、名字左移 10 px；Enter 切过去。</item>
/// <item>焦点环的位置（截图里量像素）：页签、「+」内侧 1–3 px，图标栏内侧 2–4 px（白、直角），状态栏报警项外侧 2–4 px（上下被状态栏裁掉），树的行内侧 0–2 px，
///   卡片 / 位置按钮外侧 2–4 px，程序库的行内侧 0–2 px。</item>
/// </list>
/// 最后把演示数据和外框恢复原样，焦点清掉。
/// </summary>
public static partial class ShellChecks
{
    private static async Task M6G2Checks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var L = vm.Layout;
        vm.ResetView();
        G2NoTips(window);
        await Settle();
        var tree = Find<ListBox>(window, l => l.Name == "Tree");
        var explorer = Find<ExplorerView>(window, _ => true);
        Visual? Focused() => window.FocusManager?.GetFocusedElement() as Visual;
        string KeyOf(Visual? v) => (v as Avalonia.Controls.Control)?.DataContext is XRow r && v is ListBoxItem ? r.Key : v?.GetType().Name ?? "null";
        async Task OpenExplorer()
        {
            if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }
        }
        ListBoxItem Row(string key)
        {
            int i = vm.Explorer.Rows.ToList().FindIndex(r => r.Key == key);
            tree.ScrollIntoView(i);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);
        }
        Button CloseBtn() => explorer.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.Explorer.ToggleCommand));

        // ---------------- 区域的 Tab 顺序（样稿 frameHTML 的 DOM 顺序） ----------------
        await OpenExplorer();
        var help = Find<Menu>(window, _ => true).Items.OfType<MenuItem>().First(m => (m.Header as string) == "帮助");
        help.Focus(NavigationMethod.Tab);
        await Settle();
        var stops = new List<Visual>();
        var regions = new List<string>();
        var expRegions = new[] { "资源管理器", "页签栏", "急停格", "图标栏", "工作区", "检查器", "时间轴", "状态栏" };
        for (int i = 0; i < 800; i++)
        {
            Tab(window);
            var f = Focused();
            if (f == null) break;
            var r = G2Region(f);
            if (r == "菜单栏") break;   // 转了一圈回到菜单栏
            stops.Add(f);
            if (!expRegions.Contains(r)) continue;   // 外框以外的（比如窗口里另加的条）不算区域
            if (regions.Count == 0 || regions[^1] != r) regions.Add(r);
        }
        Check(regions.SequenceEqual(expRegions), $"区域的 Tab 顺序（{stops.Count} 个停靠点，{(L.ExplorerFloat ? "浮层" : "停靠")}）：菜单栏 → " + string.Join(" → ", regions) + " → 菜单栏");
        var firstFive = stops.Take(5).ToList();
        Check(firstFive.Count == 5 && firstFive[0] is Button { Content: "添加" } && firstFive[1] is Button { Name: "Legend" } && ReferenceEquals(firstFive[2], CloseBtn())
              && firstFive[3] is ListBoxItem { DataContext: XRow tr } && ReferenceEquals(tr, vm.Explorer.TabTarget)
              && firstFive[4] is NavToggle { DataContext: DocTabVm { Id: "overview" } },
            "菜单栏之后：添加 → ⓘ → 收起 → 树的一行（选中的那一行）→ 第一个页签：" + string.Join(" → ", firstFive.Select(G2Label)));
        int treeStops = stops.OfType<ListBoxItem>().Count(li => explorer.IsVisualAncestorOf(li));
        Check(treeStops == 1, $"树在 Tab 顺序里只有一个停靠点（{treeStops}）");
        var sideStops = stops.Where(v => G2Region(v) == "图标栏").Select(G2Label).ToList();
        var expSide = new[] { "设备", "总览", "程序", "曲线", "记录", "报警", "测试", "设置", vm.Sidebar.FocusLabel };
        Check(sideStops.SequenceEqual(expSide), "图标栏的 Tab：" + string.Join(" → ", sideStops) + "（「收起」在最后，同样稿 renderSidebar）");
        if (stops.FirstOrDefault(v => G2Region(v) == "工作区") is InputElement ws0)
        {
            ws0.Focus(NavigationMethod.Tab);
            G2ShiftTab(window);
            await Settle();
            Check(Focused() is NavToggle bt && bt.Classes.Contains("bottom"), $"工作区的第一个停靠点上 Shift+Tab → 图标栏的「{(Focused() is { } fb ? G2Label(fb) : "null")}」（最后一项「收起」）");
        }
        else Check(false, "Tab 一圈里有工作区的停靠点");

        // ---------------- 树：一个 Tab 停靠点（roving tabindex） ----------------
        vm.Select(1, 2);
        await Settle();
        CloseBtn().Focus(NavigationMethod.Tab);
        Tab(window);
        await Settle();
        var f1 = Focused() as ListBoxItem;
        bool fv1 = f1?.Classes.Contains(":focus-visible") == true;
        Tab(window);
        await Settle();
        var after = Focused();
        Check(KeyOf(f1) == "r1/2" && fv1 && after != null && !explorer.IsVisualAncestorOf(after) && G2Region(after) == "页签栏",
            $"选中 1号机 · RV2：Tab 进树落在 {KeyOf(f1)}（焦点环 {fv1}），再 Tab 离开资源管理器到 {G2Region(after ?? window)}");
        G2ShiftTab(window);
        await Settle();
        Check(KeyOf(Focused()) == "r1/2", $"Shift+Tab 回到树：还是 {KeyOf(Focused())}（不是最后一行）");
        vm.Select(3, 11);
        await Settle();
        Click(window, Row("d3").GetVisualDescendants().OfType<Button>().First());   // 展开箭头：收起 3号机，选中的 RV11 藏在里面
        G2NoTips(window);
        await Settle();
        CloseBtn().Focus(NavigationMethod.Tab);
        Tab(window);
        await Settle();
        Check(!vm.Explorer.Rows.Any(r => r.Key == "r3/11") && vm.Explorer.SelectedRow == null && KeyOf(Focused()) == "d3" && vm.SelectedRv == 11,
            $"选中的 RV11 在收起的 3号机里：Tab 进树落在设备头 {KeyOf(Focused())}（同样稿 xTabTarget），选中不变");
        vm.Navigate(Route.Reactor(1, 5));
        await Settle();
        await OpenExplorer();
        CloseBtn().Focus(NavigationMethod.Tab);
        Tab(window);
        await Settle();
        Check(vm.SelectedRv == 5 && KeyOf(Focused()) == "d1", $"选中未安装的 RV5（树里没有这一行）：Tab 进树落在设备头 {KeyOf(Focused())}（样稿这时一行都不给，这里给设备头）");

        // ---------------- 树：↑ ↓ Home End ----------------
        vm.ResetView();
        await Settle();
        await OpenExplorer();
        vm.Select(1, 2);
        await Settle();
        Row("r1/2").Focus(NavigationMethod.Tab);
        await Settle();
        G2NoTips(window);
        await Capture(window, Path.Combine(dir, $"shell-kbd-tree-{width}.png"), lines);
        string route0 = vm.Route.Id;
        bool Ring(string key) => Focused() is ListBoxItem li && KeyOf(li) == key && li.Classes.Contains(":focus-visible");
        Key1(window, Key.Down, PhysicalKey.ArrowDown);
        await Settle();
        bool down = vm.SelectedRv == 3 && Ring("r1/3");
        Key1(window, Key.Up, PhysicalKey.ArrowUp);
        await Settle();
        bool up = vm.SelectedRv == 2 && Ring("r1/2");
        Check(down && up, $"↓ → RV3（选中 + 焦点环）、↑ → 回到 RV2：{down} / {up}（焦点 {KeyOf(Focused())}）");
        Key1(window, Key.Home, PhysicalKey.Home);
        await Settle();
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == null && Ring("d1"), $"Home → 第一项 1号机设备头（选中设备，焦点 {KeyOf(Focused())}）");
        var last = vm.Explorer.Rows.Last(r => r.Selectable);
        Key1(window, Key.End, PhysicalKey.End);
        await Settle();
        bool end = Ring(last.Key) && vm.Explorer.SelectedRow == last;
        Key1(window, Key.Down, PhysicalKey.ArrowDown);
        await Settle();
        Check(end && Ring(last.Key) && vm.Explorer.SelectedRow == last && vm.Route.Id == route0,
            $"End → 最后一项 {last.Key}；在最后一项上 ↓ 不动（不循环）；页面不变（{vm.Route.Id}）");

        // ---------------- 树：← → ----------------
        Key1(window, Key.Home, PhysicalKey.Home);
        await Settle();
        Key1(window, Key.Left, PhysicalKey.ArrowLeft);
        await Settle();
        bool collapsed = !vm.Explorer.Rows.Any(r => r.Key == "r1/1") && Ring("d1") && vm.SelectedDevice?.K == 1 && vm.SelectedRv == null;
        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        await Settle();
        bool expanded = vm.Explorer.Rows.Any(r => r.Key == "r1/1") && Ring("d1") && vm.SelectedRv == null;
        Check(collapsed && expanded, $"展开的设备头上 ← → 收起（焦点留在设备头）、收起的设备头上 → → 展开（选中不变）：{collapsed} / {expanded}");
        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        await Settle();
        bool intoRv = vm.SelectedRv == 1 && Ring("r1/1");
        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        await Settle();
        bool rvRight = vm.SelectedRv == 1 && Ring("r1/1");
        Key1(window, Key.Left, PhysicalKey.ArrowLeft);
        await Settle();
        bool back = vm.SelectedDevice?.K == 1 && vm.SelectedRv == null && Ring("d1");
        Check(intoRv && rvRight && back && vm.Route.Id == route0,
            $"展开的设备头上 → → 第一个反应釜 RV1（{intoRv}），反应釜行上 → 不做事（{rvRight}），← → 回到设备头并选中设备（{back}）；页面不变（{vm.Route.Id}）");
        vm.Select(1, 2);
        await Settle();
        Row("d1").Focus(NavigationMethod.Tab);
        Key1(window, Key.Left, PhysicalKey.ArrowLeft);
        await Settle();
        var head1 = vm.Explorer.Rows.OfType<XDeviceRow>().First(r => r.D.K == 1);
        bool hidden = head1.HasSel && vm.SelectedRv == 2 && ReferenceEquals(vm.Explorer.TabTarget, head1) && Ring("d1");
        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        await Settle();
        Check(hidden && vm.SelectedRv == 2 && vm.Explorer.SelectedRow?.Key == "r1/2" && Ring("d1"),
            $"选中 RV2 时在设备头上 ← 收起（设备头标出「选中的在这里面」，成为 Tab 停靠点）、→ 展开，选中一直是 RV2（{vm.Selection}）");

        // ---------------- 树：Enter 打开页签、空格选中 ----------------
        Row("r1/3").Focus(NavigationMethod.Tab);
        var r13 = Focused();
        Key1(window, Key.Enter, PhysicalKey.Enter);
        await Settle();
        Check(vm.Route.Id == "reactor/1/3" && ReferenceEquals(Focused(), r13) && Ring("r1/3"), $"RV3 行上 Enter → 打开 {vm.Route.Id}，焦点留在这一行");
        Row("r1/2").Focus(NavigationMethod.Tab);
        Key1(window, Key.Space, PhysicalKey.Space);
        await Settle();
        Check(vm.SelectedRv == 2 && vm.Route.Id == "reactor/1/3" && Ring("r1/2"), $"RV2 行上空格 → 只选中（{vm.Selection}），页面不变（{vm.Route.Id}）");
        if (L.IsNarrow)
        {
            vm.Navigate(Route.Overview);
            await Settle();
            await OpenExplorer();
            Row("r1/1").Focus(NavigationMethod.Tab);
            Key1(window, Key.Enter, PhysicalKey.Enter);
            await Settle();
            bool kept = L.ExplorerFloat && vm.Route.Id == "reactor/1/1" && Ring("r1/1");
            var xr = vm.Explorer.Rows.First(r => r.Key == "d2");
            vm.Explorer.Open(xr);
            await Settle();
            Check(kept && !L.ExplorerFloat && vm.Route.Id == "device/2",
                $"浮层：Enter 打开 RV1 之后浮层还开着、焦点还在（{kept}，同样稿 treeKey）；指针的打开（双击）照旧收起浮层（{!L.ExplorerFloat}，{vm.Route.Id}）");
        }

        // ---------------- 资源管理器收起之后：焦点清掉（样稿 display: none），树的键不起作用，下一个 Tab 从资源管理器的位置接着走 ----------------
        string FocusInfo() => Focused() is { } fv ? $"{G2Label(fv)}{(fv.IsEffectivelyVisible ? "" : "（看不见）")}" : "没有焦点";
        vm.ResetView();
        await Settle();
        await OpenExplorer();
        vm.Select(1, 2);
        await Settle();
        Row("r1/2").Focus(NavigationMethod.Tab);
        Key1(window, Key.Enter, PhysicalKey.Enter);
        await Settle();
        bool opened = vm.Route.Id == "reactor/1/2" && L.ExplorerOpen;
        string how = L.ExplorerFloat ? "浮层 Esc" : "停靠时收起";
        if (L.ExplorerFloat) Esc(window); else vm.ToggleExplorer();
        await Settle();
        bool gone = !L.ExplorerOpen && !explorer.IsEffectivelyVisible && Focused() == null;
        string hf = FocusInfo();
        Key1(window, Key.Down, PhysicalKey.ArrowDown);
        await Settle();
        Key1(window, Key.Enter, PhysicalKey.Enter);
        await Settle();
        Check(opened && gone && vm.Selection.ToString() == "1/2" && vm.Route.Id == "reactor/1/2" && !L.ExplorerOpen && Focused() == null,
            $"树里 Enter 打开 RV2、{how}：焦点清掉（{hf}），之后 ↓ Enter 不动树（选中 {vm.Selection}、页面 {vm.Route.Id}、焦点 {FocusInfo()}）");
        Tab(window);
        await Settle();
        Check(Focused() is NavToggle { DataContext: DocTabVm { Id: "overview" } },
            $"收起之后 Tab 从资源管理器的位置接着走：{FocusInfo()}（{G2Region(Focused() ?? window)}，第一个页签；同浏览器的顺序焦点导航起点）");
        await OpenExplorer();
        CloseBtn().Focus(NavigationMethod.Tab);
        Key1(window, Key.Enter, PhysicalKey.Enter);
        await Settle();
        bool byClose = !L.ExplorerOpen && Focused() == null;
        G2ShiftTab(window);
        await Settle();
        Check(byClose && G2Region(Focused() ?? window) == "菜单栏", $"键盘按资源管理器的「收起」：焦点清掉（{byClose}），Shift+Tab 回到 {G2Region(Focused() ?? window)}（{FocusInfo()}）");
        if (L.IsNarrow)
        {
            vm.ResetView();
            await Settle();
            await OpenExplorer();
            vm.Select(1, 2);
            await Settle();
            Row("r1/2").Focus(NavigationMethod.Tab);
            await Settle();
            ClickAt(window, new Point(window.Bounds.Width - 40, window.Bounds.Height / 2));   // 遮罩（检查器那一侧）
            G2NoTips(window);
            await Settle();
            bool scrim = !L.ExplorerFloat && Focused() == null;
            Key1(window, Key.Down, PhysicalKey.ArrowDown);
            await Settle();
            bool scrimKeys = vm.Selection.ToString() == "1/2" && Focused() == null;
            await OpenExplorer();
            Click(window, Row("r1/3"));
            vm.Explorer.Open(vm.Explorer.Rows.First(r => r.Key == "r1/3"));   // 双击做的事
            G2NoTips(window);
            await Settle();
            bool pointer = !L.ExplorerFloat && vm.Route.Id == "reactor/1/3" && Focused() == null;
            Key1(window, Key.Down, PhysicalKey.ArrowDown);
            await Settle();
            Check(scrim && scrimKeys && pointer && vm.Selection.ToString() == "1/3",
                $"浮层：点遮罩收起（焦点清掉 {scrim}，↓ 不动树 {scrimKeys}）、指针打开收起（{pointer}，选中 {vm.Selection}）");
        }
        vm.ResetView();
        await Settle();

        // ---------------- 状态栏报警项：没有可选的报警对象时不是 Tab 停靠点 ----------------
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var status = Find<Button>(window, b => b.Theme == Application.Current!.FindResource("PrStatusButton"));
        bool stopWith = status.IsTabStop && vm.Status.AlarmClickable;
        var pq = DemoParams.Parse("alarm=1:0,2:0,3:0") with { Freeze = true };
        vm.Fleet.ResetDemo(pq);
        vm.ApplyDemoParams(pq);
        vm.ResetView();
        await Settle();
        Check(stopWith && !vm.Status.AlarmClickable && !status.IsTabStop && status.Classes.Contains("idle"),
            $"状态栏报警项：有报警时是 Tab 停靠点（{stopWith}），没有报警（alarm=1:0,2:0,3:0：{string.Join("", vm.Status.AlarmParts.Select(x => x.Text))}）时不是（同样稿 tabindex -1）");
        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();

        // ---------------- 文档页签 ----------------
        vm.Navigate(Route.Device(1));
        await Settle();
        var strip = Find<TabOverflowPanel>(window, _ => true);
        NavToggle TabOf(string id) => strip.GetVisualDescendants().OfType<NavToggle>().First(t => (t.DataContext as DocTabVm)?.Id == id);
        TabOf("overview").Focus(NavigationMethod.Tab);
        var tabStops = new List<Visual>();
        for (int i = 0; i < 40; i++)
        {
            Tab(window);
            var f = Focused();
            if (f == null || G2Region(f) != "页签栏") break;
            tabStops.Add(f);
        }
        var tail = tabStops.SkipWhile(v => v is NavToggle).ToList();
        Check(tabStops.All(v => v is not Button { Name: "PART_Close" }) && tail.Count == 2 && tail[0] is Button { Name: "Add" } && tail[1] is Button { Name: "More" },
            $"页签栏的 Tab：{tabStops.Count(v => v is NavToggle)} 个页签之后是「+」「›」，× 不是停靠点（" + string.Join(" → ", tabStops.Select(G2Label)) + "）");
        var rv11 = TabOf("reactor/1/1");
        rv11.Focus(NavigationMethod.Tab);
        await Settle();
        var x = rv11.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close");
        var row = rv11.GetVisualDescendants().OfType<DockPanel>().First(d => d.Name == "PART_Row");
        double dx = row.RenderTransform?.Value.M31 ?? 0;
        Check(rv11.IsChecked != true && x.Opacity == 1 && Math.Abs(dx + 10) < 0.01, $"键盘焦点在没打开的页签「1号机 · RV1」上：× 出现（不透明度 {x.Opacity}）、名字左移 10 px（{dx:0.#}），同悬停");
        G2NoTips(window);
        await Capture(window, Path.Combine(dir, $"shell-kbd-tab-{width}.png"), lines);
        await G2Ring(window, rv11, Palette.ADColor, new[] { 1, 2 }, "页签", Check);
        Key1(window, Key.Enter, PhysicalKey.Enter);
        await Settle();
        Check(vm.Route.Id == "reactor/1/1" && rv11.IsChecked == true, $"Enter → 切到这个页签（{vm.Route.Id}）");
        var add = Find<Button>(window, b => b.Name == "Add");
        await G2Ring(window, add, Palette.ADColor, new[] { 1, 2 }, "「+」", Check);
        await Capture(window, Path.Combine(dir, $"shell-kbd-add-{width}.png"), lines);

        // ---------------- 图标栏、状态栏报警项的焦点环 ----------------
        var side = Find<NavToggle>(window, t => t.Content as string == "总览");
        await G2Ring(window, side, Palette.TInvColor, new[] { 2, 3 }, "图标栏「总览」（白、直角）", Check, square: true);
        await Capture(window, Path.Combine(dir, $"shell-kbd-side-{width}.png"), lines);
        await G2Ring(window, status, Palette.ADColor, new[] { -4, -3 }, "状态栏报警项（外侧）", Check);
        using (var bmp = G2Frame(window))
        {
            var bar = Find<StatusBarView>(window, _ => true);
            var top = bar.TranslatePoint(default, window)!.Value;
            var sc = Center(status, window);
            var line = G2Px(bmp, (int)sc.X, (int)top.Y);
            var bottom = G2Px(bmp, (int)sc.X, (int)(top.Y + bar.Bounds.Height) - 1);
            Check(!G2Near(line, Palette.ADColor) && G2Near(line, Palette.Line2Color, 2) && !G2Near(bottom, Palette.ADColor),
                $"状态栏报警项的环上下两边被裁掉、不压到上边框（上边框那一行 {G2Hex(line)}、最后一行 {G2Hex(bottom)}，同样稿 overflow: hidden 裁到边框里面）");
        }
        await Capture(window, Path.Combine(dir, $"shell-kbd-status-{width}.png"), lines);

        // ---------------- 树的行：焦点环内侧 0–2 px ----------------
        vm.ResetView();
        await Settle();
        await OpenExplorer();
        vm.Select(1, 2);
        await Settle();
        await G2Ring(window, Row("r1/2"), Palette.ADColor, new[] { 0, 1 }, "树的行（内侧）", Check);

        // ---------------- 没有 Fluent 的黑框：设备页卡片、未安装位置页的位置按钮 / 芯片、程序库的行 ----------------
        vm.Navigate(Route.Device(1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        var tile = pages.CurrentView!.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("dvtile") && p.IsEffectivelyVisible);
        tile.Focus(NavigationMethod.Tab);
        await Settle();
        var dvfocus = tile.GetVisualChildren().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("dvfocus"));
        Check(tile.IsFocused && dvfocus?.IsVisible == true && G2NoAdorner(tile), $"设备页卡片：键盘焦点只有自己的外侧环（dvfocus {dvfocus?.IsVisible}），没有 Fluent 的黑框（装饰层 {G2AdornerInfo(tile)}）");
        await G2Ring(window, tile, Palette.ADColor, new[] { -4, -3 }, "设备页卡片（外侧）", Check);
        await Capture(window, Path.Combine(dir, $"shell-kbd-dvcard-{width}.png"), lines);
        vm.Navigate(Route.Overview);
        await Settle();
        var ovtile = pages.CurrentView!.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("ovtile") && p.IsEffectivelyVisible);
        if (ovtile != null)
        {
            ovtile.Focus(NavigationMethod.Tab);
            await Settle();
            Info($"总览卡片（第 1 组的）装饰层：{G2AdornerInfo(ovtile)}");
        }

        vm.Navigate(Route.Reactor(1, 5));
        await Settle();
        var un = pages.CurrentView!.GetVisualDescendants().OfType<ReactorUnPanel>().First(p => p.IsEffectivelyVisible);
        var slots = un.GetVisualDescendants().OfType<Button>().Where(b => b.Theme == un.FindResource("UnSlot")).ToList();
        var chips = un.GetVisualDescendants().OfType<Button>().Where(b => b.Theme == un.FindResource("UnChip")).ToList();
        Check(slots.Count == 8 && slots.Count(b => b.Classes.Contains("un")) == 4 && slots.All(b => b.IsTabStop == !b.Classes.Contains("un")),
            $"未安装的位置不是 Tab 停靠点（样稿 tabindex -1）：{string.Join(" ", slots.Select(b => (b.Classes.Contains("un") ? "-" : "+")))}");
        slots[0].Focus(NavigationMethod.Tab);
        var slotSeq = new List<Visual> { slots[0] };
        for (int i = 0; i < 6; i++)
        {
            Tab(window);
            if (Focused() is { } f) slotSeq.Add(f);
        }
        Check(slotSeq.Take(4).SequenceEqual(slots.Take(4)) && ReferenceEquals(slotSeq[4], chips.FirstOrDefault()),
            "位置按钮的 Tab：RV1 → RV2 → RV3 → RV4 → 跳过未安装的 RV5–RV8 → 第一个芯片");
        slots[0].Focus(NavigationMethod.Tab);
        await Settle();
        Check(G2NoAdorner(slots[0]) && G2PartFocus(slots[0]), $"位置按钮：键盘焦点画自己的 a-d 环，没有 Fluent 的黑框（{G2AdornerInfo(slots[0])}）");
        await G2Ring(window, slots[0], Palette.ADColor, new[] { -4, -3 }, "位置按钮（外侧）", Check);
        await Capture(window, Path.Combine(dir, $"shell-kbd-slot-{width}.png"), lines);
        chips[0].Focus(NavigationMethod.Tab);
        await Settle();
        Check(G2NoAdorner(chips[0]) && G2PartFocus(chips[0]), $"「去已安装的反应釜」芯片：同上（{G2AdornerInfo(chips[0])}）");
        await G2Ring(window, chips[0], Palette.ADColor, new[] { -4, -3 }, "芯片（外侧）", Check);

        var pr = DemoParams.Parse("sel=3/13") with { Freeze = true };
        vm.Fleet.ResetDemo(pr);
        vm.ApplyDemoParams(pr, Route.Reactor(3, 13));
        await Settle();
        var prog = pages.CurrentView!.GetVisualDescendants().OfType<ReactorProgramPanel>().FirstOrDefault(p => p.IsEffectivelyVisible);
        var pick = prog?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Theme == prog.FindResource("LibPick") && b.IsEffectivelyVisible);
        if (pick != null)
        {
            pick.Focus(NavigationMethod.Tab);
            await Settle();
            Check(G2NoAdorner(pick) && G2PartFocus(pick), $"程序库的行：键盘焦点画自己的内侧 a-d 环，没有 Fluent 的黑框（{G2AdornerInfo(pick)}）");
            await G2Ring(window, pick, Palette.ADColor, new[] { 0, 1 }, "程序库的行（内侧）", Check);
            await Capture(window, Path.Combine(dir, $"shell-kbd-libpick-{width}.png"), lines);
        }
        else Check(false, "3号机 RV13 没有程序：程序库里有行");

        // ---------------- 没有焦点时方向键什么也不做（合并后补：MainWindow 的隧道处理；Avalonia 11.1 默认把焦点挪到菜单「文件」） ----------------
        window.FocusManager?.ClearFocus();
        await Settle();
        foreach (var (k, pk) in new[] { (Key.Down, PhysicalKey.ArrowDown), (Key.Right, PhysicalKey.ArrowRight), (Key.Up, PhysicalKey.ArrowUp) })
        {
            window.KeyPress(k, RawInputModifiers.None, pk, null);
            window.KeyRelease(k, RawInputModifiers.None, pk, null);
            Dispatcher.UIThread.RunJobs();
        }
        var afterArrows = window.FocusManager?.GetFocusedElement();
        Check(afterArrows is null || ReferenceEquals(afterArrows, window), $"没有焦点时 ↓ → ↑ 不挪焦点（现在：{afterArrows?.GetType().Name ?? "无"}）");

        // ---------------- 恢复 ----------------
        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        window.FocusManager?.ClearFocus();
        await Settle();
    }

    /// <summary>
    /// 指针移到菜单栏左上角、关掉开着的悬停提示：别让前面留下的提示盖住要量的环（1366：指针停在浮层资源管理器的行上时浮层收起，
    /// 那一行的提示留着不关 —— 它的主人看不见了，收不到指针离开）。
    /// </summary>
    private static void G2NoTips(Window w)
    {
        w.MouseMove(new Point(2, 2));
        foreach (var c in w.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Where(ToolTip.GetIsOpen).ToList()) ToolTip.SetIsOpen(c, false);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>整窗截图（截不到时抛异常：核对不能悄悄跳过）。</summary>
    private static WriteableBitmap G2Frame(Window w) => w.CaptureRenderedFrame() ?? throw new InvalidOperationException("截不到整窗图");

    /// <summary>核对结果里怎么称呼一个停靠点：页签写名字，树的行写键，菜单项写标题，按钮写字 / 名字 / 提示。</summary>
    private static string G2Label(Visual v) => v switch
    {
        NavToggle { DataContext: DocTabVm t } => t.Label,
        ListBoxItem { DataContext: XRow r } => r.Key,
        MenuItem { Header: string hd } => hd,
        Button { Content: string s } => s,
        Button { Name: { Length: > 0 } n } => n,
        Avalonia.Controls.Control c when ToolTip.GetTip(c) is string tip => tip,
        _ => v.GetType().Name,
    };

    /// <summary>焦点所在的外框区域（ShellView 的直接部件）。</summary>
    private static string G2Region(Visual v)
    {
        foreach (var a in v.GetSelfAndVisualAncestors())
            switch (a)
            {
                case MenuBarView: return "菜单栏";
                case ExplorerView: return "资源管理器";
                case TabBarView: return "页签栏";
                case GlobalStopCell: return "急停格";
                case SidebarView: return "图标栏";
                case PageHost: return "工作区";
                case InspectorView: return "检查器";
                case TimelineView: return "时间轴";
                case StatusBarView: return "状态栏";
                case DialogHost: return "对话框";
            }
        return v.GetType().Name;
    }

    private static void G2ShiftTab(Window w)
    {
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        w.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>控件的装饰层里没有画给它的焦点装饰（Fluent 默认的 2 px 黑框是装饰层里的一个 Border）。</summary>
    private static bool G2NoAdorner(Visual v) =>
        AdornerLayer.GetAdornerLayer(v) is { } layer && !layer.Children.Any(c => ReferenceEquals(AdornerLayer.GetAdornedElement(c), v));

    private static string G2AdornerInfo(Visual v) =>
        AdornerLayer.GetAdornerLayer(v) is not { } layer ? "没有装饰层"
            : layer.Children.Count(c => ReferenceEquals(AdornerLayer.GetAdornedElement(c), v)) is var n and > 0 ? $"有 {n} 个焦点装饰" : "没有焦点装饰";

    /// <summary>模板里的 PART_Focus 显示着（:focus-visible）。</summary>
    private static bool G2PartFocus(TemplatedControl c) =>
        c.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Focus") is { IsVisible: true };

    /// <summary>截图里一个像素的 R G B（不造颜色对象：色板检查只许用令牌）。</summary>
    private static (byte R, byte G, byte B) G2Px(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height) return default;
        var o = y * fb.RowBytes + x * 4;
        byte c0 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o), c1 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 1),
            c2 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 2);
        return fb.Format == PixelFormat.Rgba8888 ? (c0, c1, c2) : (c2, c1, c0);
    }

    private static bool G2Near((byte R, byte G, byte B) a, Color b, int tol = 12) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    private static string G2Hex((byte R, byte G, byte B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>
    /// 焦点环的位置：先清掉焦点截一张，再用 Tab 聚焦 <paramref name="v"/> 截一张；控件纵向中线上、左边往外 / 往里 −6 … +5 的像素里，
    /// 由「不是环的颜色」变成「环的颜色」的那几列（相对控件左边的偏移）应当正好是 <paramref name="cols"/>（只看变了的：旁边本来就是近似颜色的线不算）。
    /// square：再看左上角往里 cols[0] 的那个像素也变成了环的颜色（直角；圆角时那里还是底色）。最后焦点留在 v 上。
    /// </summary>
    private static async Task G2Ring(Window w, InputElement v, Color ring, int[] cols, string what, Action<bool, string> Check, bool square = false)
    {
        G2NoTips(w);
        w.FocusManager?.ClearFocus();
        await Settle();
        using var before = G2Frame(w);
        v.Focus(NavigationMethod.Tab);
        await Settle();
        using var after = G2Frame(w);
        var p = v.TranslatePoint(default, w)!.Value;
        int left = (int)Math.Round(p.X), top = (int)Math.Round(p.Y), mid = (int)Math.Round(p.Y + v.Bounds.Height / 2);
        bool Turned(int x, int y) => G2Near(G2Px(after, x, y), ring, 4) && !G2Near(G2Px(before, x, y), ring, 4);
        var hits = Enumerable.Range(-6, 12).Where(dx => Turned(left + dx, mid)).ToList();
        bool ok = v.IsFocused && hits.SequenceEqual(cols);
        string corner = "";
        if (square)
        {
            ok &= Turned(left + cols[0], top + cols[0]);
            corner = $"，左上角 {G2Hex(G2Px(after, left + cols[0], top + cols[0]))}";
        }
        Check(ok, $"焦点环：{what} 在左边 {string.Join("、", cols.Select(c => c.ToString("+0;-0;0")))} 列（实测 {string.Join("、", hits.Select(c => c.ToString("+0;-0;0")))}{corner}）");
    }
}
#endif
