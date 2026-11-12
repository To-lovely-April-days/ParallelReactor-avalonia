#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 反应釜页「程序」面板与整页的版式（M5 第四部分，样稿 reactor.js progPanel / flowLayout / autoScroll / fitCrumb、css .reactor-grid）：
/// <list type="bullet">
/// <item>StepFlowPanel 排出来的每张卡、每个条件框的位置与尺寸 = Core 的 StepFlowLayout（同一个卡宽量出来的卡高）；画布宽 = 版面宽 × 放大倍数，居中。</item>
/// <item>自动滚动：当前步的整列（卡片 + 条件框）在可见范围里；放大一档 / 两档：每行的卡变少、不横向滚动；适应回到 100 %。</item>
/// <item>面包屑：面板窄于等于 360 时不写别名段（「RV1 ›」从不切掉）；状态行窄画布写短的。</item>
/// <item>放大「程序」：铺满面板网格，状态条照样显示（样稿 .reactor-grid { position: relative }）；Esc 还原。</item>
/// <item>宽版式（工作区 ≥ 1800）：程序 ｜ 釜体 ｜ 属性查看器 / 曲线 四块；关掉「程序」右边一栏铺满整个宽度。</item>
/// </list>
/// 最后把演示数据和外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task ProgramChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        vm.Navigate(Route.Reactor(1, 1));
        if (vm.Page is ReactorPageVm rp0) rp0.ResetView();
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        if (pages.CurrentView is not ReactorPage page || vm.Page is not ReactorPageVm pvm)
        {
            Check(false, "反应釜页是 ReactorPage / ReactorPageVm");
            return;
        }
        var panel = page.GetVisualDescendants().OfType<ReactorProgramPanel>().First();
        var flow = panel.FlowControl;
        var scroll = panel.ScrollControl;
        var grid = page.GetVisualDescendants().OfType<ReactorGridPanel>().First();

        // ---------------- StepFlowPanel 的排布 = Core 的 StepFlowLayout ----------------
        bool FlowMatches(out string why)
        {
            why = "";
            var g = flow.Geometry;
            if (g is null) { why = "没有版面"; return false; }
            var heights = flow.Pairs.Select(p => p.Card.DesiredSize.Height).ToArray();
            var fit = StepFlowLayout.Fit(scroll.Bounds.Width, flow.Pairs.Count, pvm.Program.Zoom);
            var want = StepFlowLayout.Arrange(fit, heights, flow.Tail?.DesiredSize.Height, 0);
            if (fit != flow.Fit) { why = $"每行张数 / 卡宽 {flow.Fit} ≠ {fit}"; return false; }
            for (int i = 0; i < flow.Pairs.Count; i++)
            {
                var (card, cond) = flow.Pairs[i];
                var w = want.Items[i];
                if (Math.Abs(card.Bounds.X - w.Card.X) > 0.01 || Math.Abs(card.Bounds.Y - w.Card.Y) > 0.01 || Math.Abs(card.Bounds.Width - w.Card.W) > 0.01 || Math.Abs(card.Bounds.Height - w.Card.H) > 0.01)
                { why = $"第 {i + 1} 张卡 {card.Bounds} ≠ {w.Card}"; return false; }
                if (Math.Abs(cond.Bounds.X - w.Cond.X) > 0.01 || Math.Abs(cond.Bounds.Y - w.Cond.Y) > 0.01 || Math.Abs(cond.Bounds.Width - w.Cond.W) > 0.01 || Math.Abs(cond.Bounds.Height - w.Cond.H) > 0.01)
                { why = $"第 {i + 1} 个条件框 {cond.Bounds} ≠ {w.Cond}"; return false; }
            }
            // 画布尺寸 = 版面 × 放大倍数（排版取整到整像素，向上 < 1 px）
            if (flow.Bounds.Width - want.Width * fit.Scale is < -0.01 or >= 1 || flow.Bounds.Height - want.Height * fit.Scale is < -0.01 or >= 1)
            { why = $"画布 {flow.Bounds.Width:0.#}×{flow.Bounds.Height:0.#} ≠ {want.Width * fit.Scale:0.#}×{want.Height * fit.Scale:0.#}"; return false; }
            // 居中（可见宽度里）
            double off = flow.Bounds.X, expect = Math.Max(0, (scroll.Viewport.Width - flow.Bounds.Width) / 2);
            if (Math.Abs(off - expect) > 1) { why = $"画布左边 {off:0.#} ≠ 居中 {expect:0.#}"; return false; }
            return true;
        }
        bool CurVisible()
        {
            var g = flow.Geometry;
            int i = pvm.Program.CurIndex;
            if (g is null || i < 0) return false;
            var col = g.Items[i].Col;
            double top = col.Y * g.Scale - scroll.Offset.Y, bottom = (col.Y + col.H) * g.Scale - scroll.Offset.Y;
            return top >= -0.5 && bottom <= scroll.Viewport.Height + 0.5 || top >= -0.5 && top <= 16 * g.Scale + 0.5;
        }
        Check(FlowMatches(out var why0), $"画布（{flow.Fit.Per} 张一行、卡宽 {flow.Fit.StepW}）的每张卡、条件框 = Core 的 StepFlowLayout{(why0.Length > 0 ? "：" + why0 : "")}");
        Check(flow.Pairs.All(p => Math.Abs(p.Card.DesiredSize.Height - 339) < 0.5), $"卡片高 339（同样稿在 WebKit 里量的）：{string.Join(" ", flow.Pairs.Select(p => p.Card.DesiredSize.Height.ToString("0.#")))}");
        Check(CurVisible(), $"自动滚动：当前步（第 {pvm.Program.CurIndex + 1} 步）的整列在可见范围（滚动 {scroll.Offset.Y:0}，可见高 {scroll.Viewport.Height:0}）");
        Check(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"画布不横向滚动（内容宽 {scroll.Extent.Width:0.#} / 可见 {scroll.Viewport.Width:0.#}）");

        // ---------------- 放大一档 / 两档：每行的卡变少、照样不横向滚动；适应回到 100 % ----------------
        int per0 = flow.Fit.Per;
        scroll.Offset = default;
        await Settle();
        pvm.Program.ZoomInCommand.Execute(null);
        await Settle();
        bool ok1 = FlowMatches(out var why1);
        Check(pvm.Program.Zoom == 1 && Math.Abs(flow.Scale - 1.15) < 1e-9 && flow.Fit.Per <= per0 && ok1 && scroll.Extent.Width <= scroll.Viewport.Width + 0.5 && CurVisible(),
            $"放大画布 → 115 %（每行 {per0} → {flow.Fit.Per} 张、卡宽 {flow.Fit.StepW}），版面照样 = Core，不横向滚动，定位到当前步{(why1.Length > 0 ? "：" + why1 : "")}");
        await Capture(window, Path.Combine(dir, $"shell-rvzoom-{width}.png"), lines);
        if (pvm.Program.ZoomInReason is null)
        {
            pvm.Program.ZoomInCommand.Execute(null);
            await Settle();
            bool ok2 = FlowMatches(out var why2);
            Check(pvm.Program.Zoom == 2 && Math.Abs(flow.Scale - 1.3) < 1e-9 && ok2 && scroll.Extent.Width <= scroll.Viewport.Width + 0.5,
                $"再放大 → 130 %（每行 {flow.Fit.Per} 张）{(why2.Length > 0 ? "：" + why2 : "")}，「放大」不能再点：{pvm.Program.ZoomInReason}");
        }
        else Info("画布太窄，放不到 130 %：" + pvm.Program.ZoomInReason);
        pvm.Program.FitCommand.Execute(null);
        await Settle();
        Check(pvm.Program.Zoom == 0 && flow.Scale == 1 && flow.Fit.Per == per0 && CurVisible(), "适应 → 回到 100 %，每行张数复原，定位当前步");

        // ---------------- 面包屑与状态行（容器查询：面板 ≤ 360 不写别名段，< 460 状态行写短的） ----------------
        var crumb = panel.CrumbControl;
        double pw = panel.PanelControl.Bounds.Width;
        Check(crumb.DevShown == pw > ReactorProgramPanel.NoDevMax && panel.StatusText == (pw >= ReactorProgramPanel.LongStatusMin ? "运行中 · 第 3/4 步 · 预计 11:09 结束" : "预计 11:09 结束"),
            $"面板 {pw:0.##}：面包屑{(crumb.DevShown ? "写" : "不写")}别名段，状态行「{panel.StatusText}」");
        panel.Width = 330;
        await Settle();
        Check(!crumb.DevShown && panel.StatusText == "预计 11:09 结束" && crumb.Bounds.Width > 0, $"面板窄到 330 → 面包屑只剩「RV1 › 加氢-标准」、状态行写短的（{panel.StatusText}）");
        panel.ClearValue(Avalonia.Layout.Layoutable.WidthProperty);
        await Settle();
        Check(crumb.DevShown == panel.PanelControl.Bounds.Width > ReactorProgramPanel.NoDevMax, "宽回来 → 面包屑复原");

        // ---------------- 放大「程序」：铺满面板网格，状态条照样显示 ----------------
        vm.Navigate(Route.Reactor(3, 11));
        await Settle();
        var page2 = (ReactorPage)pages.CurrentView!;
        var p2 = page2.GetVisualDescendants().OfType<ReactorProgramPanel>().First();
        var grid2 = page2.GetVisualDescendants().OfType<ReactorGridPanel>().First();
        var strip = page2.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Alerts");
        Click(window, p2.PanelControl.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        var others = grid2.Children.Where(c => !ReferenceEquals(c, p2)).ToList();
        Check(p2.PanelControl.IsMaximized && Math.Abs(p2.Bounds.Width - grid2.Bounds.Width) < 0.5 && Math.Abs(p2.Bounds.Height - grid2.Bounds.Height) < 0.5
              && others.All(c => !c.IsVisible) && strip.IsEffectivelyVisible && strip.Bounds.Height >= 40,
            $"放大「程序」→ 铺满面板网格（{p2.Bounds.Width:0}×{p2.Bounds.Height:0}），报警条照样显示（{strip.Bounds.Height:0} 高），其余面板藏起来");
        await Capture(window, Path.Combine(dir, $"shell-rvprogmax-{width}.png"), lines);
        Esc(window);
        await Settle();
        Check(!p2.PanelControl.IsMaximized && grid2.Children.Where(c => c.Tag as string is "prog" or "vessel" or "curves").All(c => c.IsVisible), "Esc → 还原，面板都回来");

        // ---------------- 宽版式（工作区 ≥ 1800）：四块；关掉「程序」右边铺满 ----------------
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var size0 = new Size(window.Width, window.Height);
        window.Width = 2560;
        window.Height = 1440;
        vm.Layout.Update(new Size(2560, 1440));
        vm.ApplyDemoParams(p0 with { Explorer = false }, Route.Reactor(1, 1));
        await Settle();
        var page3 = (ReactorPage)pages.CurrentView!;
        var grid3 = page3.GetVisualDescendants().OfType<ReactorGridPanel>().First();
        var g3 = grid3.Grid;
        var props = grid3.Children.OfType<ReactorVesselPanel>().First(c => c.Tag as string == "props");
        var vessel = grid3.Children.OfType<ReactorVesselPanel>().First(c => c.Tag as string == "vessel");
        Check(g3 is { Wide: true } && props.IsVisible && props.Mode == VesselPanelMode.Props && vessel.Mode == VesselPanelMode.Vessel && props.Bounds.Width >= 516 && vessel.Bounds.Width >= 724,
            $"工作区 {grid3.Bounds.Width:0} ≥ 1800 → 宽版式：程序 {grid3.Children.OfType<ReactorProgramPanel>().First().Bounds.Width:0} ｜ 釜体 {vessel.Bounds.Width:0} ｜ 属性查看器 {props.Bounds.Width:0} / 曲线");
        await Capture(window, Path.Combine(dir, $"shell-rvwide-{width}.png"), lines);
        var prog3 = grid3.Children.OfType<ReactorProgramPanel>().First();
        Click(window, prog3.PanelControl.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
        await Settle();
        var curves3 = grid3.Children.OfType<ReactorCurvesPanel>().First();
        Check(prog3.PanelControl.IsClosed && Math.Abs(curves3.Bounds.Width - grid3.Bounds.Width) < 0.5 && curves3.Bounds.X < 0.5,
            $"宽版式关掉「程序」→ 曲线铺满整个宽度（{curves3.Bounds.Width:0} / {grid3.Bounds.Width:0}）");
        vm.ResetLayout();
        window.Width = size0.Width;
        window.Height = size0.Height;
        vm.Layout.Update(size0);
        vm.Toasts.Clear();
        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0, Route.Overview);
        vm.Layout.ApplyParams(width >= 1600 ? "1" : "0", null);
        vm.ResetView();
        await Settle();
    }
}
#endif
