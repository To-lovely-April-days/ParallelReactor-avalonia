#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 总览（M4）的行为核对（样稿 overview.js 的交互）：单击卡片 = 选中反应釜、单击分区头 = 选中设备、右键卡片 = 反应釜菜单、双击卡片 = 打开反应釜页签；
/// «⋯» →「设备信息」弹出框；顶部报警条的「定位」（滚到那台设备、选中那个反应釜、检查器里那条报警是「确认在顶部报警条」、屏幕上只有一个「确认」）；
/// 放大分区（以滚动区为定位框：报警条照常显示、别的分区不显示、滚动区回到顶部；Esc 还原后回到原来的滚动位置）。
/// </summary>
public static partial class ShellChecks
{
    private static async Task OverviewChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        if (pages.CurrentView is not OverviewPage ov || vm.Page is not OverviewPageVm pvm)
        {
            Check(false, "总览页面是 OverviewPage / OverviewPageVm");
            return;
        }
        Check(pvm.Layout != null && Math.Abs(pvm.ViewWidth - ov.Bounds.Width) < 0.5 && Math.Abs(pvm.ViewHeight - ov.Bounds.Height) < 0.5,
            $"总览版面按根元素的尺寸算（视图 {ov.Bounds.Width:0.#}×{ov.Bounds.Height:0.#}，视图模型 {pvm.ViewWidth:0.#}×{pvm.ViewHeight:0.#}）");

        Panel Tile(int k, int n) => ov.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Classes.Contains("ovtile") && p.DataContext is OverviewCardVm c && c.Section.K == k && c.N == n);
        PrPanel Sec(int k) => ov.GetVisualDescendants().OfType<PrPanel>().First(p => p.Classes.Contains("ovdev") && p.DataContext is OverviewSectionVm s && s.K == k);

        // ---------------- 单击 / 右键 / 双击 ----------------
        var t12 = Tile(1, 2);
        Click(window, t12);
        await Settle();
        var c12 = (OverviewCardVm)t12.DataContext!;
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == 2 && c12.IsSelected && vm.Route.Id == "overview" && vm.Inspector.PathTip == "/1号机/RV2",
            $"单击 1号机 · RV2 卡片 → 选中（检查器 {vm.Inspector.PathTip}），卡片 a-line 边 + 外圈，页面不变");
        var head2 = Sec(2).GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Head");
        var hp = head2.TranslatePoint(new Point(head2.Bounds.Width * 0.6, head2.Bounds.Height / 2), window) ?? default;
        ClickAt(window, hp);
        await Settle();
        var s2 = (OverviewSectionVm)Sec(2).DataContext!;
        Check(vm.SelectedDevice?.K == 2 && vm.SelectedRv == null && s2.IsSelected && !c12.IsSelected, $"单击 2号机 分区头空白 → 选中设备（检查器 {vm.Inspector.PathTip}），分区头 a-tint + 3 px a-tab");
        RightClick(window, t12);
        await Settle();
        var ctx = ContextMenus.Last;
        var ctxItems = MenuLabels(ctx?.ItemsSource?.Cast<object>());
        Check(ctx?.IsOpen == true && vm.SelectedRv == 2 && ctxItems.FirstOrDefault()?.StartsWith("#1号机 · RV2 · ", StringComparison.Ordinal) == true,
            "右键 1号机 · RV2 卡片 → 选中 + 反应釜菜单：" + string.Join(" | ", ctxItems));
        ctx?.Close();
        await Settle();
        var p12 = Center(t12, window);
        t12.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, new PointerEventArgs(InputElement.PointerMovedEvent, t12, new Pointer(1, PointerType.Mouse, true), window, p12, 0, default, KeyModifiers.None)));
        await Settle();
        Check(vm.Route.Id == "reactor/1/2", $"双击 1号机 · RV2 卡片 → 打开反应釜页签（{vm.Route.Id}）");
        vm.Navigate(Route.Overview);
        await Settle();

        // ---------------- «⋯» → 设备信息 ----------------
        var more1 = Sec(1).GetVisualDescendants().OfType<Button>().First(b => Behaviors.Pr.GetIcon(b) == "more");
        Click(window, more1);
        await Settle();
        var mp = PresenterOf(more1.Flyout as MenuFlyout);
        var info = mp?.GetRealizedContainers().OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "设备信息");
        if (info != null) { Click(window, info); await Settle(); await Settle(); }
        var fp = window.GetVisualDescendants().OfType<FlyoutPresenter>().FirstOrDefault(f => f.IsEffectivelyVisible)
                 ?? (TopLevel.GetTopLevel(window) as Visual)?.GetVisualDescendants().OfType<FlyoutPresenter>().FirstOrDefault();
        var infoTexts = fp?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string?>();
        Check(info != null && fp != null && infoTexts.Contains("1号机") && infoTexts.Contains("192.168.1.20:8080") && infoTexts.Contains("打开设备页"),
            "«⋯» →「设备信息」：别名 · 型号 · 连接 / 地址 / SN / 版本 / 通道 / 会话 · 打开设备页（" + string.Join(" | ", infoTexts) + "）");
        if (fp != null) Check(Save(fp, Path.Combine(dir, $"shell-ovinfo-{width}.png"), lines), $"设备信息弹出框截图 shell-ovinfo-{width}.png");
        OverviewPage.LastInfo?.Hide();
        await Settle();
        Check(OverviewPage.LastInfo?.IsOpen != true, "设备信息弹出框关掉了");

        // ---------------- 报警条「定位」 ----------------
        var scroll = ov.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        if (pvm.Strip.IsVisible)
        {
            var locate = ov.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "定位");
            Click(window, locate);
            await Settle();
            await Settle();
            var rv = vm.Inspector.Body as ReactorInspectorVm;
            var owned = rv?.TopAlarms.FirstOrDefault(a => a.Where != null);
            int acks = ov.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "确认" && b.IsEffectivelyVisible)
                       + (rv?.TopAlarms.Count(a => a.ShowAck) ?? 0);
            Check(vm.SelectedDevice?.K == 3 && vm.SelectedRv == 11 && scroll.Offset.Y > 0 && owned?.WhereText == "确认在顶部报警条" && acks == 1,
                $"报警条「定位」→ 滚到 3号机（偏移 {scroll.Offset.Y:0}）、选中 RV11（{vm.Inspector.PathTip}），检查器：{owned?.WhereText ?? "（没有）"}，屏幕上的「确认」{acks} 个");
            await Capture(window, Path.Combine(dir, $"shell-ovlocate-{width}.png"), lines);
        }
        else Info($"{width}：内容放得下一屏，没有报警条（样稿同）");

        // ---------------- 放大分区（以滚动区为定位框） ----------------
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        await Settle();
        double before = scroll.Offset.Y;
        bool stripBefore = pvm.Strip.IsVisible;
        var sec3 = Sec(3);
        var max = sec3.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max");
        Click(window, max);
        await Settle();
        await Settle();
        bool othersHidden = pvm.Sections.Where(s => s.K != 3).All(s => !s.IsShown);
        var secTop = sec3.TranslatePoint(default, scroll);
        Check(sec3.IsMaximized && pvm.MaxK == 3 && othersHidden && scroll.Offset.Y == 0 && pvm.Strip.IsVisible == stripBefore && Math.Abs(sec3.Bounds.Height - pvm.ScrollHeight) < 1.5 && secTop?.Y is >= -0.5 and <= 0.5,
            $"放大 3号机 分区 → 铺满滚动区（高 {sec3.Bounds.Height:0} / 滚动区 {pvm.ScrollHeight:0}），别的分区不显示，滚动区回到顶部（{scroll.Offset.Y:0}），报警条照旧（{pvm.Strip.IsVisible}）");
        await Capture(window, Path.Combine(dir, $"shell-ovmax-{width}.png"), lines);
        Esc(window);
        await Settle();
        await Settle();
        Check(!sec3.IsMaximized && pvm.MaxK == null && pvm.Sections.All(s => s.IsShown) && Math.Abs(scroll.Offset.Y - before) < 1,
            $"Esc → 还原，回到放大前的滚动位置（{before:0} → {scroll.Offset.Y:0}）");
        scroll.Offset = default;
        await Settle();
        await OverviewReviewChecks(window, vm, dir, width, Check, Info, lines);
    }

    /// <summary>
    /// M4 复核的几处（总览）：按住卡片 / 分区头是按下的样子（样稿 :active）；分区头可 Tab 到（空格选中设备、Enter 打开设备页，键盘焦点有环）；
    /// 从别的页面换到 scroll=end 的总览（同截图 reactor11 → overview4dev：换了设备组合，总览页面重建）也滚到底。最后恢复演示数据与外框。
    /// </summary>
    private static async Task OverviewReviewChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        var ov = (OverviewPage)pages.CurrentView!;
        Panel Tile(int k, int n) => ov.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Classes.Contains("ovtile") && p.DataContext is OverviewCardVm c && c.Section.K == k && c.N == n);
        PrPanel Sec(int k) => ov.GetVisualDescendants().OfType<PrPanel>().First(p => p.Classes.Contains("ovdev") && p.DataContext is OverviewSectionVm s && s.K == k);
        static bool Is(IBrush? b, Color c) => b is ISolidColorBrush sb && sb.Color == c;

        // ---------------- 按下：卡片 bg-hover / line-strong 边 / 没有阴影；分区头 bg-press ----------------
        var t13 = Tile(1, 3);
        var card13 = t13.GetVisualChildren().OfType<Border>().First(b => b.Classes.Contains("ovcard"));
        var pt = Center(t13, window);
        window.MouseMove(pt);
        window.MouseDown(pt, MouseButton.Left);
        await Settle();
        bool pressedLook = t13.Classes.Contains("pressed") && Is(card13.Background, Theme.Palette.BgHoverColor) && Is(card13.BorderBrush, Theme.Palette.LineStrongColor)
            && card13.BoxShadow.Count > 0 && card13.BoxShadow[0].Color.A == 0;
        window.MouseUp(pt, MouseButton.Left);
        await Settle();
        Check(pressedLook && !t13.Classes.Contains("pressed") && Is(card13.Background, Theme.Palette.BgCardColor),
            "按住 1号机 · RV3 卡片 → 按下的样子（bg-hover、line-strong 边、没有阴影），松开还原");
        vm.Select(1);
        await Settle();
        var sec2 = Sec(2);
        var head2 = sec2.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Head");
        var hp = head2.TranslatePoint(new Point(head2.Bounds.Width * 0.6, head2.Bounds.Height / 2), window) ?? default;
        window.MouseMove(hp);
        window.MouseDown(hp, MouseButton.Left);
        await Settle();
        bool headPressed = sec2.Classes.Contains("pressed") && Is(head2.Background, Theme.Palette.BgPressColor);
        window.MouseUp(hp, MouseButton.Left);
        await Settle();
        Check(headPressed && !sec2.Classes.Contains("pressed") && vm.SelectedDevice?.K == 2,
            $"按住 2号机 分区头 → bg-press，松开 → 选中 2号机（{vm.Inspector.PathTip}），头换成选中的 a-tint / 悬停 a-soft（{Hex(head2.Background)}）");

        // ---------------- 分区头的键盘：Tab 到头上，空格选中设备，Enter 打开设备页 ----------------
        vm.Select(1);
        await Settle();
        var head3 = Sec(3).GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Head");
        bool focused = head3.Focusable && head3.Focus(NavigationMethod.Tab);
        await Settle();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        await Settle();
        Check(focused && head3.IsKeyboardFocusWithin && vm.SelectedDevice?.K == 3 && vm.SelectedRv == null && vm.Route.Id == "overview",
            $"Tab 到 3号机 分区头（可聚焦 {head3.Focusable}），空格 → 选中 3号机（{vm.Inspector.PathTip}），页面不变");
        await Capture(window, Path.Combine(dir, $"shell-ovheadfocus-{width}.png"), lines);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        await Settle();
        Check(vm.Route.Id == "device/3", $"Enter → 打开 3号机设备页（{vm.Route.Id}）");

        // ---------------- 从别的页面换到 scroll=end 的总览（截图 reactor11 → overview4dev） ----------------
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        vm.ResetView();
        vm.ApplyDemoParams(p0, Route.Reactor(3, 11));
        await Settle();
        var p4 = DemoParams.Parse("devs=4&scroll=end") with { Freeze = true };
        vm.Fleet.ResetDemo(p4);
        vm.ApplyDemoParams(p4);
        vm.ResetView();
        vm.ApplyDemoParams(p4, Route.Overview);
        Frozen.Apply(window);
        await Settle();
        await Settle();
        var ov4 = pages.CurrentView as OverviewPage;
        var sc4 = ov4?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(x => x.Name == "Scroll");
        double end = sc4 == null ? -1 : Math.Max(0, sc4.Extent.Height - sc4.Viewport.Height);
        Check(sc4 != null && end > 0 && Math.Abs(sc4.Offset.Y - end) < 0.5 && (vm.Page as OverviewPageVm)?.PendingScroll == null,
            $"反应釜页 → devs=4&scroll=end 的总览：滚到底（偏移 {sc4?.Offset.Y:0} / {end:0}）");

        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
    }
}
#endif
