#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M5 复核（外壳）：
/// <list type="bullet">
/// <item>关掉放大了面板的页签：PageHost 不再记着那个面板 —— Esc 交回外壳（窄屏时收起浮层资源管理器），重置布局也不被它挡住；之后放大别的面板照常，Esc 还原。</item>
/// <item>放大的面板在切走（没关掉）的页签里：Esc 交给外壳，面板留着放大；切回去 Esc 还原。</item>
/// <item>提示的阴影画在框外（ItemsControl、ItemsPresenter、每条的 ContentPresenter 都不裁剪），一行提示高 43（11 + 行高 21 + 11，同样稿）。</item>
/// <item>时间轴里断线设备的行（rv-off1）：悬停、选中都保持 bg-panel，选中时只有标签列 a-tint；同组合并的未安装行也 bg-panel、悬停不变。</item>
/// </list>
/// 最后把演示数据和外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task M5ReviewChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        // ---------------- 关掉放大了「曲线」的页签 ----------------
        vm.ResetView();
        vm.Navigate(Route.Reactor(1, 1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        var curves = pages.CurrentView?.GetVisualDescendants().OfType<ReactorCurvesPanel>().FirstOrDefault(c => c.IsEffectivelyVisible);
        if (curves == null)
        {
            Check(false, "反应釜页有显示着的「曲线」面板");
            return;
        }
        var frame = ((IReactorPanel)curves).PanelControl;
        Click(window, frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        Check(frame.IsMaximized && pages.CurrentView is ReactorPage, "放大 1号机 · RV1 的「曲线」");
        vm.Tabs.Close(vm.Tabs.Active!);
        await Settle();
        Check(frame.GetVisualRoot() == null && vm.Route.Id != "reactor/1/1" && !frame.IsMaximized,
            $"关掉这个页签（现在 {vm.Route.Id}）→ 面板跟着页面视图去掉，不再是放大的");
        Check(!pages.Escape() && !pages.Escape(), "PageHost.Escape() → false（不再记着去掉的面板，Esc 交给外壳）");
        if (vm.Layout.IsNarrow)
        {
            vm.ToggleExplorer();
            await Settle();
            bool opened = vm.Layout.ExplorerFloat;
            Esc(window);
            await Settle();
            Check(opened && !vm.Layout.ExplorerFloat, "窄屏：打开浮层资源管理器，Esc → 收起（外壳的 Esc 照常）");
        }
        else Info("宽屏没有浮层资源管理器：外壳的 Esc 只在 1366 上点一遍");
        vm.Toasts.Clear();
        vm.ResetLayout();
        await Settle();
        Check(!pages.Escape(), "视图 › 重置布局之后 PageHost.Escape() 仍是 false");

        // 放大别的面板照常：设备页「共用资源」，Esc 还原
        vm.Navigate(Route.Device(1));
        await Settle();
        var res = pages.CurrentView!.GetVisualDescendants().OfType<Controls.Pr.PrPanel>().First(p => p.Title as string == "共用资源");
        Click(window, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        bool maxed = res.IsMaximized;
        Esc(window);
        await Settle();
        Check(maxed && !res.IsMaximized, "之后放大设备页的「共用资源」照常，Esc 还原");

        // 放大的面板在切走（没关掉）的页签里：Esc 不还原看不见的面板，交给外壳（样稿只在当前页面 els.ws 里找 .is-max）；切回去仍放大，Esc 还原
        int shown = pages.CurrentView!.GetVisualDescendants().OfType<Controls.Pr.PrPanel>().Count(p => p.IsEffectivelyVisible);
        Click(window, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        vm.Navigate(Route.Overview);
        await Settle();
        Check(res.IsMaximized && !pages.Escape() && res.IsMaximized, "放大「共用资源」后切到总览：PageHost.Escape() → false，设备页的面板仍放大着");
        if (vm.Layout.IsNarrow)
        {
            vm.ToggleExplorer();
            await Settle();
            bool opened = vm.Layout.ExplorerFloat;
            Esc(window);
            await Settle();
            Check(opened && !vm.Layout.ExplorerFloat && res.IsMaximized, "窄屏：总览上打开浮层资源管理器，Esc → 收起（不被藏着的放大面板吞掉）");
        }
        vm.Navigate(Route.Device(1));
        await Settle();
        int shownMax = pages.CurrentView!.GetVisualDescendants().OfType<Controls.Pr.PrPanel>().Count(p => p.IsEffectivelyVisible);
        Check(res.IsMaximized && res.IsEffectivelyVisible && shownMax < shown, $"切回设备页：「共用资源」仍是放大的（显示着的面板 {shownMax}，平时 {shown}）");
        Esc(window);
        await Settle();
        int shownAfter = pages.CurrentView!.GetVisualDescendants().OfType<Controls.Pr.PrPanel>().Count(p => p.IsEffectivelyVisible);
        Check(!res.IsMaximized && shownAfter == shown, $"Esc 还原（显示着的面板 {shownAfter}）");

        // ---------------- 提示：阴影不被裁掉，高 43 ----------------
        vm.Toasts.Clear();
        vm.Toast("布局已重置", ToastKind.Info);
        await Settle();
        var list = Find<ItemsControl>(window, c => c.Name == "ToastList");
        var presenter = list.GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault();
        var items = list.GetVisualDescendants().OfType<ContentPresenter>().Where(c => c.DataContext is ToastItem).ToList();
        var box = items.FirstOrDefault()?.GetVisualDescendants().OfType<Border>().FirstOrDefault();
        Check(!list.ClipToBounds && presenter is { ClipToBounds: false } && items.Count == 1 && items.All(c => !c.ClipToBounds),
            "提示的 ItemsControl、ItemsPresenter、每条的 ContentPresenter 都不裁剪（ShadowPop 画在框外）");
        Check(box != null && Math.Abs(box.Bounds.Height - 43) < 0.5, $"一行提示高 43（{box?.Bounds.Height:0.##}，同样稿 11 + 21 + 11）");
        await Capture(window, System.IO.Path.Combine(dir, $"shell-toast-shadow-{width}.png"), lines);
        vm.Toasts.Clear();

        // ---------------- 断线设备的时间轴行：悬停、选中都保持 bg-panel，选中时只有标签列 a-tint ----------------
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var size = new PixelSize((int)Math.Round(window.ClientSize.Width), (int)Math.Round(window.ClientSize.Height));
        ShotRunner.Show(window, ShotPlan.All.First(s => s.Name == "rv-off1"), size);
        await Settle();
        static Color? Bg(Border b) => (b.Background as ISolidColorBrush)?.Color;
        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("tlrow") && b.DataContext is TlRowVm { Offline: true } r && r.D.K == 1 && b.IsEffectivelyVisible).ToList();
        var selRow = rows.FirstOrDefault(b => ((TlRowVm)b.DataContext!).IsSelected);
        var plain = rows.FirstOrDefault(b => !((TlRowVm)b.DataContext!).IsSelected);
        if (selRow == null || plain == null) Check(false, $"rv-off1：时间轴里有 1号机（断线）的选中行和普通行（{rows.Count} 行）");
        else
        {
            var label = selRow.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tllabel"));
            Check(Bg(selRow) == Palette.BgPanelColor && Bg(label) == Palette.ATintColor,
                "rv-off1：选中的断线行 bg-panel（不是 a-tint），只有标签列 a-tint（样稿 .pr-tl-group.is-off .pr-tl-row 压过 .is-sel）");
            window.MouseMove(Center(plain, window));
            await Settle();
            Check(plain.IsPointerOver && Bg(plain) == Palette.BgPanelColor, "rv-off1：悬停断线行仍是 bg-panel（不变成 bg-hover）");
            window.MouseMove(Center(selRow, window));
            await Settle();
            Check(selRow.IsPointerOver && Bg(selRow) == Palette.BgPanelColor && Bg(label) == Palette.ATintColor, "rv-off1：悬停选中的断线行：行 bg-panel、标签列 a-tint");
            window.MouseMove(new Point(2, 2));
            await Settle();
        }
        // 同一组合并的「RV5–RV8 未安装」行（样稿 .pr-tl-group.is-off .pr-tl-row 也套在 .is-un 上）：bg-panel，悬停不变 —— 截图里它在可视区下面，先滚进来
        var un = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tlrow") && b.DataContext is TlUnRowVm
            && b.FindAncestorOfType<StickyHeadPanel>()?.DataContext is TlGroupVm { K: 1 });
        if (un == null) Check(false, "rv-off1：时间轴里有 1号机（断线）的「RV5–RV8 未安装」行");
        else
        {
            un.BringIntoView();
            await Settle();
            var body = un.FindAncestorOfType<ScrollViewer>();
            double top = body != null ? un.TranslatePoint(default, body)?.Y ?? -1 : -1;
            Check(un.Classes.Contains("off") && Bg(un) == Palette.BgPanelColor && body != null && top >= 0 && top + un.Bounds.Height <= body.Bounds.Height + 0.5,
                $"rv-off1：「RV5–RV8 未安装」行带 off、bg-panel（滚进时间轴可视区，y = {top:0.#}）");
            window.MouseMove(Center(un, window));
            await Settle();
            Check(un.IsPointerOver && Bg(un) == Palette.BgPanelColor, "rv-off1：悬停「RV5–RV8 未安装」行仍是 bg-panel（不变成 bg-hover）");
            await Capture(window, System.IO.Path.Combine(dir, $"shell-tl-offun-{width}.png"), lines);
            window.MouseMove(new Point(2, 2));
            await Settle();
        }

        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
    }
}
#endif
