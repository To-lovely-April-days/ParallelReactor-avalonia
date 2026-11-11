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
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 反应釜页「釜体 / 属性查看器」页签面板（M5 第二部分，样稿 tabsPanel 的交互）：
/// <list type="bullet">
/// <item>釜体页签：六个标注框都在釜体两侧（不盖住它）。</item>
/// <item>面板放大后点「属性查看器」：还是放大、还是同一个面板、焦点在点过的页签上；← → 在页签之间切换（放大照旧）；Esc 还原。</item>
/// <item>属性卡片的 × 隐藏这一张（后面的补上来），「恢复全部属性」全部回来并提示。</item>
/// <item>关掉面板：曲线占满右边一栏；关掉「程序」：右边一栏铺满整个宽度（样稿 reactor.css :has(.is-closed)）；视图 › 重置布局恢复。</item>
/// </list>
/// 最后把演示数据和外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static void Key1(Window w, Key key, PhysicalKey pk)
    {
        w.KeyPress(key, RawInputModifiers.None, pk, null);
        w.KeyRelease(key, RawInputModifiers.None, pk, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task ReactorChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        vm.Navigate(Route.Reactor(1, 1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        if (pages.CurrentView is not ReactorPage page || vm.Page is not ReactorPageVm pvm)
        {
            Check(false, "反应釜页是 ReactorPage / ReactorPageVm");
            return;
        }
        var v = pvm.Vessel;
        v.ResetView();
        await Settle();
        var panel = page.GetVisualDescendants().OfType<ReactorVesselPanel>().First();
        var frame = panel.PanelControl;
        var stage = panel.StageControl;
        var grid = page.GetVisualDescendants().OfType<ReactorGridPanel>().First();

        // ---------------- 釜体页签：标注框都在釜体两侧 ----------------
        var res = stage.Result;
        Check(!res.Empty && res.Boxes.Count == 6 && res.Boxes.All(b => b.Right <= res.Vx || b.Left >= res.Vx + res.Vw),
            $"釜体页签：{res.Mode} 样式（面板内容区 {stage.Bounds.Width:0}×{stage.Bounds.Height:0}），六个标注框都在釜体（{res.Vx:0}–{res.Vx + res.Vw:0}）两侧");

        // ---------------- 面板窄于 420：抓手和状态让出去，页签与放大 / 关闭照常（样稿 @container rvves (max-width: 419px)） ----------------
        var badgeHost = panel.FindControl<Panel>("BadgeHost");
        Check(frame.ShowGrip && badgeHost?.IsVisible == true, "面板够宽：抓手和状态都在");
        panel.Width = 400;
        await Settle();
        Check(!frame.ShowGrip && badgeHost?.IsVisible == false && frame.GetVisualDescendants().OfType<PTabs>().Any(t => t.IsEffectivelyVisible)
              && frame.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "PART_Max" && b.IsEffectivelyVisible),
            $"面板窄于 420（{frame.Bounds.Width:0}）→ 抓手和状态让出去，页签与放大 / 关闭照常");
        panel.ClearValue(Avalonia.Layout.Layoutable.WidthProperty);
        await Settle();
        Check(frame.ShowGrip && badgeHost?.IsVisible == true, $"宽回来（{frame.Bounds.Width:0}）→ 抓手和状态回来");

        // ---------------- 宽版式（工作区 ≥ 1800）的两块面板：同一个视图模型，Mode = Vessel / Props ----------------
        var propsBody = panel.FindControl<ScrollViewer>("PropsBody");
        panel.Mode = VesselPanelMode.Vessel;
        await Settle();
        Check(frame.Title as string == "釜体" && frame.Icon == "flask" && frame.Sub == v.VesselKind && stage.IsEffectivelyVisible && propsBody?.IsVisible == false
              && badgeHost?.IsVisible == true, $"宽版式「釜体」面板：标题「釜体」+ 烧瓶图标 + 副标题「{frame.Sub}」，只放釜体和状态");
        panel.Mode = VesselPanelMode.Props;
        await Settle();
        Check(frame.Title as string == "属性查看器" && frame.Icon == "tree" && !stage.IsEffectivelyVisible && propsBody?.IsEffectivelyVisible == true
              && badgeHost?.IsVisible == false && panel.FindControl<Button>("Restore")?.IsVisible == true, "宽版式「属性查看器」面板：标题 + 树图标，只放属性卡片和「恢复全部属性」");
        panel.Mode = VesselPanelMode.Tabs;
        await Settle();
        Check(frame.Title is PTabs && stage.IsEffectivelyVisible && v.IsVesselTab, "回到页签面板「釜体 / 属性查看器」");

        // ---------------- 放大后换页签：放大、面板、焦点都保留；← → 切换 ----------------
        ToggleButton TabBtn(string label) => frame.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == label);
        Click(window, frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        Check(frame.IsMaximized, $"放大「釜体 / 属性查看器」（{frame.Bounds.Width:0}×{frame.Bounds.Height:0}）");
        var propsTab = TabBtn("属性查看器");
        Click(window, propsTab);
        await Settle();
        var focused = window.FocusManager?.GetFocusedElement();
        Check(v.IsPropsTab && frame.IsMaximized && ReferenceEquals(panel.PanelControl, frame) && ReferenceEquals(focused, propsTab),
            $"点「属性查看器」→ 换内容（{v.Tab}），面板还是放大的同一个，焦点在这个页签上（{(focused as Visual)?.GetType().Name}）");
        await Capture(window, Path.Combine(dir, $"shell-rvprops-max-{width}.png"), lines);
        Key1(window, Key.Left, PhysicalKey.ArrowLeft);
        await Settle();
        focused = window.FocusManager?.GetFocusedElement();
        Check(v.IsVesselTab && frame.IsMaximized && ReferenceEquals(focused, TabBtn("釜体")), $"← →「釜体」（{v.Tab}），放大照旧，焦点跟着页签");
        Key1(window, Key.Right, PhysicalKey.ArrowRight);
        await Settle();
        Check(v.IsPropsTab && frame.IsMaximized, $"→ 回到「属性查看器」（{v.Tab}）");

        // ---------------- 属性卡片：× 隐藏一张、恢复全部 ----------------
        // 指针先移开：页签的悬停提示（弹出层）会盖住第一张卡片的 ×（1600：页签在卡片正上方）
        ToolTip.SetIsOpen(propsTab, false);
        window.MouseMove(new Point(2, 2));
        await Settle();
        var cards = frame.GetVisualDescendants().OfType<PropCard>().Where(c => c.IsEffectivelyVisible).ToList();
        int before = v.ShownProps.Count;
        var first = cards.FirstOrDefault(c => c.DataContext is ReactorPropVm { Key: "pv" });
        if (first != null)
        {
            var close = first.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close");
            var at = Center(close, window);
            var hit = window.InputHitTest(at) as Visual;
            Info($"× 在窗口的 ({at.X:0.#}, {at.Y:0.#})，{close.Bounds.Width:0}×{close.Bounds.Height:0}，可见 {close.IsEffectivelyVisible}；命中 {(hit == null ? "（无）" : ShotAudit.Describe(hit))}");
            Click(window, close);
            await Settle();
            Check(v.Hidden.Contains("pv") && v.ShownProps.FirstOrDefault()?.Key == "p" && !v.ShownProps.Any(p => p.Key == "pv"),
                $"点「反应器温度」的 × → 这一张拿掉，后面的补上来（{string.Join(" ", v.ShownProps.Select(p => p.Key))}）");
            vm.Toasts.Clear();
            var restore = frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "Restore");
            Click(window, restore);
            await Settle();
            Check(v.Hidden.Count == 0 && v.ShownProps.Count == before && vm.Toasts.Items.Any(t => t.Text == "已恢复全部属性"),
                "「恢复全部属性」→ 全部回来，提示「已恢复全部属性」");
        }
        else Check(false, "属性查看器里有「反应器温度」卡片");
        Esc(window);
        await Settle();
        Check(!frame.IsMaximized && v.IsPropsTab, "Esc → 还原放大（页签不变）");
        v.Tab = ReactorVesselVm.VesselTabValue;
        await Settle();

        // ---------------- 关掉面板 / 关掉「程序」：其余面板补位 ----------------
        var prog = grid.Children.OfType<ReactorProgramPanel>().First(p => p.Tag as string == "prog").PanelControl;
        var curves = grid.Children.OfType<ReactorCurvesPanel>().First(p => p.Tag as string == "curves");
        Click(window, frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
        await Settle();
        Check(frame.IsClosed && !panel.IsVisible && Math.Abs(curves.Bounds.Height - grid.Bounds.Height) < 0.5 && curves.Bounds.X > 0,
            $"关掉「釜体 / 属性查看器」→ 曲线占满右边一栏（{curves.Bounds.Width:0}×{curves.Bounds.Height:0} / 网格 {grid.Bounds.Height:0}）");
        vm.ResetLayout();
        await Settle();
        Check(!frame.IsClosed && panel.IsVisible && panel.Bounds.Height > 0, "视图 › 重置布局 → 面板回来");
        Click(window, prog.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
        await Settle();
        Check(prog.IsClosed && Math.Abs(panel.Bounds.Width - grid.Bounds.Width) < 0.5 && Math.Abs(curves.Bounds.Width - grid.Bounds.Width) < 0.5,
            $"关掉「程序」→「釜体 / 属性查看器」和曲线铺满整个宽度（{panel.Bounds.Width:0} / {grid.Bounds.Width:0}），标注 {stage.Result.Mode} 样式");
        await Capture(window, Path.Combine(dir, $"shell-rvnoprog-{width}.png"), lines);
        vm.ResetLayout();
        await Settle();
        Check(!prog.IsClosed && prog.Bounds.Width > 0 && panel.Bounds.X > prog.Bounds.X, "重置布局 →「程序」回来");

        vm.Toasts.Clear();
        v.ResetView();
        vm.Fleet.ResetDemo(vm.Fleet.Params ?? DemoParams.Default);
        vm.ResetView();
        await Settle();
    }
}
#endif
