#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using Avalonia.Controls.Primitives;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 设备页（M4）的行为核对（样稿 device.js 的交互）：版面按根元素的尺寸算；单击卡片 = 选中反应釜、双击 = 打开反应釜页签、右键 = 反应釜菜单；
/// 共用资源的「设置」= 选中设备并聚焦检查器的共用转速；同一条报警屏幕上只有一个「确认」（「报警与事件」可见时检查器写「确认在「报警与事件」」，
/// 关掉面板后检查器的确认回来）；窄工作区的页签（打开时有未确认的错误报警 → 「报警与事件」；用户点过之后不再替用户换；确认报警不换页签）；
/// 放大反应釜面板（卡片铺满、不滚动、紧凑卡画完整釜体），Esc 还原。最后把演示数据恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task DeviceChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        vm.Navigate(Route.Device(1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        if (pages.CurrentView is not DevicePage dv || vm.Page is not DevicePageVm pvm)
        {
            Check(false, "设备页是 DevicePage / DevicePageVm");
            return;
        }
        Check(pvm.Layout != null && Math.Abs(pvm.ViewWidth - dv.Bounds.Width) < 0.5 && Math.Abs(pvm.ViewHeight - dv.Bounds.Height) < 0.5,
            $"设备页版面按根元素的尺寸算（视图 {dv.Bounds.Width:0.#}×{dv.Bounds.Height:0.#}，视图模型 {pvm.ViewWidth:0.#}×{pvm.ViewHeight:0.#}，{pvm.Layout?.WidthClassName}）");

        Panel Tile(int n) => pages.CurrentView!.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Classes.Contains("dvtile") && p.DataContext is DeviceCardVm c && c.N == n);

        // ---------------- 卡片：按下 / 单击 / 右键 / 双击 ----------------
        var t2 = Tile(2);
        // 按住左键：按下的样子（样稿 .device-card:active：bg-hover、line-strong 边、没有阴影），松开还原
        var pt2 = Center(t2, window);
        var card2 = t2.GetVisualChildren().OfType<Border>().First(b => b.Classes.Contains("dvcard"));
        window.MouseMove(pt2);
        window.MouseDown(pt2, MouseButton.Left);
        await Settle();
        bool pressedLook = t2.Classes.Contains("pressed") && card2.Background is ISolidColorBrush pb && pb.Color == Theme.Palette.BgHoverColor
            && card2.BorderBrush is ISolidColorBrush pl && pl.Color == Theme.Palette.LineStrongColor && card2.BoxShadow.Count > 0 && card2.BoxShadow[0].Color.A == 0;
        window.MouseUp(pt2, MouseButton.Left);
        await Settle();
        Check(pressedLook && !t2.Classes.Contains("pressed") && card2.Background is ISolidColorBrush rb && rb.Color == Theme.Palette.BgCardColor,
            "按住 RV2 卡片 → 按下的样子（bg-hover、line-strong 边、没有阴影），松开还原");
        var c2 = (DeviceCardVm)t2.DataContext!;
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == 2 && c2.IsSelected && vm.Route.Id == "device/1",
            $"单击 RV2 卡片 → 选中（检查器 {vm.Inspector.PathTip}），卡片 a-line 边 + 外圈，页面不变");
        RightClick(window, t2);
        await Settle();
        var ctx = ContextMenus.Last;
        var ctxItems = MenuLabels(ctx?.ItemsSource?.Cast<object>());
        Check(ctx?.IsOpen == true && ctxItems.FirstOrDefault()?.StartsWith("#1号机 · RV2 · ", StringComparison.Ordinal) == true,
            "右键 RV2 卡片 → 反应釜菜单：" + string.Join(" | ", ctxItems));
        ctx?.Close();
        await Settle();
        var p2 = Center(t2, window);
        t2.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, new PointerEventArgs(InputElement.PointerMovedEvent, t2, new Pointer(1, PointerType.Mouse, true), window, p2, 0, default, KeyModifiers.None)));
        await Settle();
        Check(vm.Route.Id == "reactor/1/2", $"双击 RV2 卡片 → 打开反应釜页签（{vm.Route.Id}）");
        vm.Navigate(Route.Device(1));
        await Settle();

        // ---------------- 共用资源「设置」→ 检查器的共用转速 ----------------
        var set = pages.CurrentView!.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "设置" && b.IsEffectivelyVisible);
        if (set != null)
        {
            vm.Select(1, 3);
            await Settle();
            Click(window, set);
            await Settle();
            await Settle();
            var focused = window.FocusManager?.GetFocusedElement() as Visual;
            bool inRpm = focused?.GetSelfAndVisualAncestors().OfType<NumBox>().Any(n => n.Name == "SharedRpm") == true;
            Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == null && inRpm, $"共用资源「设置」→ 选中 1号机（检查器 {vm.Inspector.PathTip}），焦点在共用转速框（{focused?.GetType().Name}）");
        }
        else Check(false, "共用资源有「设置」");

        // ---------------- 同一条报警只有一个「确认」（3号机 · RV11 超压报警） ----------------
        vm.Navigate(Route.Device(3));
        await Settle();
        dv = (DevicePage)pages.CurrentView!;
        pvm = (DevicePageVm)vm.Page!;
        bool tabbed = pvm.Tabbed;
        if (tabbed)
            Check(pvm.TabValue == "log" && pvm.TabDot && pvm.LogVisible,
                $"窄工作区：打开 3号机设备页时有未确认的错误报警 →「报警与事件」页签（{pvm.TabValue}），页签上红点（{pvm.TabDot}）");
        vm.Select(3, 11);
        await Settle();
        int Acks() => pages.CurrentView!.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "确认" && b.IsEffectivelyVisible)
                      + ((vm.Inspector.Body as ReactorInspectorVm)?.TopAlarms.Count(a => a.ShowAck) ?? 0);
        var owned = (vm.Inspector.Body as ReactorInspectorVm)?.TopAlarms.FirstOrDefault();
        Check(owned?.WhereText == "确认在「报警与事件」" && Acks() == 1,
            $"「报警与事件」可见：检查器的 RV11 报警写「{owned?.WhereText ?? "（没有）"}」，屏幕上的「确认」{Acks()} 个");
        await Capture(window, Path.Combine(dir, $"shell-dvowned-{width}.png"), lines);
        if (tabbed)
        {
            // 用户点「设备信息」页签：「报警与事件」不在屏幕上了 → 检查器自己放「确认」；之后新的报警也不再替用户换页签
            var infoTab = pages.CurrentView!.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == "设备信息");
            Click(window, infoTab);
            await Settle();
            owned = (vm.Inspector.Body as ReactorInspectorVm)?.TopAlarms.FirstOrDefault();
            Check(pvm.TabValue == "info" && pvm.TabRule.UserPicked && !pvm.LogVisible && owned?.ShowAck == true && Acks() == 1,
                $"点「设备信息」页签 → 页签换过去（{pvm.TabValue}，用户点过 {pvm.TabRule.UserPicked}），检查器的报警卡有「确认」（{owned?.ShowAck}），屏幕上「确认」{Acks()} 个");
            var logTab = pages.CurrentView!.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == "报警与事件");
            Click(window, logTab);
            await Settle();
            Check(pvm.TabValue == "log" && pvm.LogVisible, $"点「报警与事件」页签 → 回来（{pvm.TabValue}）");
        }
        else
        {
            var log = pages.CurrentView!.GetVisualDescendants().OfType<PrPanel>().First(p => p.Title as string == "报警与事件");
            Click(window, log.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
            await Settle();
            owned = (vm.Inspector.Body as ReactorInspectorVm)?.TopAlarms.FirstOrDefault();
            Check(log.IsClosed && !pvm.LogVisible && owned?.ShowAck == true && Acks() == 1,
                $"关掉「报警与事件」→ 检查器的报警卡有「确认」（{owned?.ShowAck}），屏幕上「确认」{Acks()} 个");
            vm.ResetLayout();
            await Settle();
            owned = (vm.Inspector.Body as ReactorInspectorVm)?.TopAlarms.FirstOrDefault();
            Check(!log.IsClosed && pvm.LogVisible && owned?.WhereText == "确认在「报警与事件」", "视图 › 重置布局 →「报警与事件」回来，检查器又写「确认在「报警与事件」」");
            vm.Toasts.Clear();
        }

        // ---------------- 放大反应釜面板：卡片铺满、不滚动、紧凑卡画完整釜体 ----------------
        var rv = pages.CurrentView!.GetVisualDescendants().OfType<PrPanel>().First(p => p.Tag as string == "rv");
        Click(window, rv.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        await Settle();
        var cards = pvm.Tiles.OfType<DeviceCardVm>().ToList();
        Check(rv.IsMaximized && pvm.Layout?.Scroll == false && pvm.TopRow == null || rv.IsMaximized && pvm.Layout?.Scroll == false,
            $"放大「反应釜」→ 铺满工作区（{rv.Bounds.Width:0}×{rv.Bounds.Height:0}），卡片区不滚动");
        Check(cards.All(c => c.Spec.Maximized && !c.SimpleVessel), "放大后紧凑卡画完整釜体（Spec.Maximized）");
        await Capture(window, Path.Combine(dir, $"shell-dvmax-{width}.png"), lines);
        Esc(window);
        await Settle();
        Check(!rv.IsMaximized && cards.All(c => !c.Spec.Maximized && c.SimpleVessel), "Esc → 还原（紧凑卡回到简化釜体）");

        // ---------------- 确认报警不换页签（窄工作区）；之后恢复演示数据 ----------------
        if (tabbed)
        {
            var ack = pages.CurrentView!.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "确认" && b.IsEffectivelyVisible);
            Click(window, ack);
            await Settle();
            await Settle();
            Check(pvm.TabValue == "log" && Acks() == 0, $"在页面上确认 RV11 报警 → 页签不变（{pvm.TabValue}），屏幕上没有「确认」了（{Acks()}）");
        }
        vm.Fleet.ResetDemo(vm.Fleet.Params ?? DemoParams.Default);
        vm.ResetView();
        await Settle();
        await DeviceReviewChecks(window, vm, dir, width, Check, Info, lines);
    }

    /// <summary>
    /// M4 复核的几处（设备页）：
    /// <list type="bullet">
    /// <item>事件筛选「全部 / 报警/提醒 / 操作」在「报警与事件」模板不是由用户点页签实例化时也有三项：新的错误报警自动切到「报警与事件」页签（窄工作区）、
    ///   窗口跨过 1100 改尺寸（两个方向）。</item>
    /// <item>放大「报警与事件」（宽）/ 页签面板（窄）之后窗口跨过 1100：放大撤掉（样稿换了一组面板），整页照常显示，不是一片空白。</item>
    /// <item>下排面板之间 1 px line-strong 的线：资源管理器展开 / 收起都在（截图里那一列像素是 line-strong）。</item>
    /// </list>
    /// 最后把窗口尺寸、外框布局与演示数据恢复原样。
    /// </summary>
    private static async Task DeviceReviewChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var pages = Find<PageHost>(window, _ => true);
        var size0 = new Size(window.Width, window.Height);
        async Task Resize(Size sz)
        {
            window.Width = sz.Width;
            window.Height = sz.Height;
            vm.Layout.Update(sz);
            await Settle();
            await Settle();
        }
        SegControl? Seg() => pages.CurrentView?.GetVisualDescendants().OfType<SegControl>().FirstOrDefault(x => x.Classes.Contains("dvevseg") && x.IsEffectivelyVisible);
        string SegText(SegControl? sc) => sc == null ? "（没有显示）" : sc.Options.Count + " 项：" + string.Join(" | ", sc.Options.Select(o => o.Label));
        bool SegOk(SegControl? sc) => sc != null && sc.Options.Count == 3 && sc.Options[0].Label.StartsWith("全部", StringComparison.Ordinal)
            && sc.GetVisualDescendants().OfType<ToggleButton>().Count(b => b.IsEffectivelyVisible) == 3;

        // ---------------- 自动切到「报警与事件」页签：筛选有三项 ----------------
        vm.ResetView();
        vm.Navigate(Route.Device(1));
        await Settle();
        var pvm = (DevicePageVm)vm.Page!;
        bool narrow = pvm.Tabbed;
        if (narrow && vm.Fleet.DemoData is { } demo && demo.Device(1) is { } d1)
        {
            Check(pvm.TabValue == "info", $"窄工作区：1号机没有未确认的错误报警 → 打开在「设备信息」页签（{pvm.TabValue}）");
            demo.Act(d1, Core.Guard.DeviceAction.Estop, null, quiet: true);
            vm.Fleet.RefreshNow();
            await Settle();
            await Settle();
            var seg = Seg();
            Check(pvm.TabValue == "log" && SegOk(seg), $"新的错误报警（急停）→ 自动切到「报警与事件」，事件筛选 {SegText(seg)}");
            vm.Fleet.ResetDemo(vm.Fleet.Params ?? DemoParams.Default);
            vm.ResetView();
            await Settle();
        }

        // ---------------- 跨过 1100 改窗口尺寸：筛选有三项（3号机：两种宽度类下「报警与事件」都在屏幕上） ----------------
        var other = narrow ? new Size(1920, 1080) : new Size(1366, 768);
        vm.Navigate(Route.Device(3));
        await Settle();
        pvm = (DevicePageVm)vm.Page!;
        Check(SegOk(Seg()), $"3号机设备页（{width}）：事件筛选 {SegText(Seg())}");
        await Resize(other);
        Check(pvm.Tabbed != narrow && SegOk(Seg()), $"窗口改成 {other.Width:0}×{other.Height:0}（{(pvm.Tabbed ? "窄" : "宽")}工作区）→ 事件筛选 {SegText(Seg())}");
        await Resize(size0);
        Check(pvm.Tabbed == narrow && SegOk(Seg()), $"再改回 {size0.Width:0}×{size0.Height:0} → 事件筛选 {SegText(Seg())}");

        // ---------------- 放大下排面板后跨过 1100：放大撤掉，整页照常显示 ----------------
        PrPanel PanelByTag(string tag) => pages.CurrentView!.GetVisualDescendants().OfType<PrPanel>().First(p => p.Tag as string == tag);
        string Shown() => string.Join(" | ", pages.CurrentView!.GetVisualDescendants().OfType<PrPanel>()
            .Where(p => p.Classes.Contains("dvpanel") && p.IsEffectivelyVisible && p.Bounds.Width > 0 && p.Bounds.Height > 0)
            .Select(p => (p.Tag as string) + (p.IsMaximized ? "(放大)" : "")));
        bool AllShown(bool tabbed) => tabbed
            ? new[] { "rv", "res", "tabs" }.All(t => PanelByTag(t) is { IsEffectivelyVisible: true, IsMaximized: false } q && q.Bounds.Width > 0)
            : new[] { "rv", "res", "info", "log" }.All(t => PanelByTag(t) is { IsEffectivelyVisible: true, IsMaximized: false } q && q.Bounds.Width > 0);
        var big = PanelByTag(narrow ? "tabs" : "log");
        Click(window, big.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        Check(big.IsMaximized, $"放大「{(narrow ? "设备信息 / 报警与事件" : "报警与事件")}」");
        await Resize(other);
        Check(!big.IsMaximized && AllShown(pvm.Tabbed) && !pages.Escape(), $"窗口改成 {other.Width:0}×{other.Height:0} → 放大撤掉，面板都在：{Shown()}");
        await Capture(window, Path.Combine(dir, $"shell-dvmaxresize-{width}.png"), lines);
        await Resize(size0);
        Check(AllShown(pvm.Tabbed), $"改回 {size0.Width:0}×{size0.Height:0} → {Shown()}");

        // ---------------- 下排面板之间的 1 px line-strong 线（资源管理器展开 / 收起） ----------------
        async Task Dividers(string what)
        {
            var row = pages.CurrentView!.GetVisualDescendants().OfType<Views.Pages.Device.DvRowPanel>().First();
            var kids = row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).OrderBy(c => c.Bounds.X).ToList();
            await Settle();
            var bmp = window.CaptureRenderedFrame();
            var bad = new List<string>();
            int n = 0;
            for (int i = 1; i < kids.Count; i++)
            {
                double right = kids[i - 1].Bounds.Right, left = kids[i].Bounds.X;
                var at = row.TranslatePoint(new Point(right, row.Bounds.Height / 2), window) ?? default;
                bool line = bmp != null && PixelIs(bmp, new PixelPoint((int)Math.Floor(at.X + 0.01), (int)at.Y), Theme.Palette.LineStrongColor);
                n++;
                if (left - right < 1 || !line) bad.Add($"{right:0.##}→{left:0.##}{(line ? "" : " 不是 line-strong")}");
            }
            Check(n == kids.Count - 1 && kids.Count >= 2 && bad.Count == 0, $"{what}：下排 {kids.Count} 块面板之间的线 {n} 条都在（{(bad.Count == 0 ? "缝 ≥ 1 px、像素 = line-strong" : string.Join("；", bad))}）");
        }
        await Dividers($"{width}，资源管理器{(vm.Layout.ExplorerOpen ? "停靠" : "收起")}");
        if (vm.Layout.ExplorerOpen && !vm.Layout.ExplorerFloat)
        {
            // 资源管理器收起：工作区变宽（1920 → 1526、1600 → 1236：换成三列），线照样在（窄屏的浮层盖着遮罩，不在这里量）
            vm.Layout.ApplyParams("0", null);
            await Settle();
            await Settle();
            await Dividers($"{width}，资源管理器收起（工作区 {pages.CurrentView!.Bounds.Width:0}）");
        }

        vm.Fleet.ResetDemo(vm.Fleet.Params ?? DemoParams.Default);
        vm.ResetView();
        await Settle();
    }

    /// <summary>截图里这个像素是不是这个颜色（RGB 各差 ≤ 2）。</summary>
    private static bool PixelIs(WriteableBitmap bmp, PixelPoint p, Color c)
    {
        using var fb = bmp.Lock();
        if (p.X < 0 || p.Y < 0 || p.X >= fb.Size.Width || p.Y >= fb.Size.Height) return false;
        int o = p.Y * fb.RowBytes + p.X * 4;
        byte b0 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o), b1 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 1),
             b2 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 2);
        bool bgra = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        byte r = bgra ? b2 : b0, g = b1, bl = bgra ? b0 : b2;
        return Math.Abs(r - c.R) <= 2 && Math.Abs(g - c.G) <= 2 && Math.Abs(bl - c.B) <= 2;
    }
}
#endif
