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
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Chart;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 曲线（M5 第三部分：Controls/Chart/TimeSeriesChart、反应釜页「曲线」面板、总览「温度趋势」）的行为核对：
/// <list type="bullet">
/// <item>反应釜页：曲线容器 = 面板内容区去掉 上 4 / 右 12 / 左 6；分段 10 / 30 / 60（点击换窗口、横轴跟着变）；图例按面板宽度取舍（≤ 720 去掉标记、≤ 640 去掉设定值、≤ 500 整个不显示）；
///   悬停（十字线对齐到最近的采样、四行读数、提示框在曲线里；同一个采样点只挪提示、不重排版面；指针离开收起）；数据不变时不重排（冻结）；放大 / 还原、关闭让釜体铺满右栏。</item>
/// <item>总览：只有一台设备时温度趋势铺满分区下面的地方（样稿 1920 下 1200 × 493 的曲线），四条线、图例；放大 / Esc 还原；三台设备时不显示。</item>
/// </list>
/// 最后把演示数据与外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task CurvesChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
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
        pvm.Curves.ResetView();
        await Settle();
        var panel = page.GetVisualDescendants().OfType<ReactorCurvesPanel>().First();
        var frame = panel.PanelControl;
        var chart = panel.ChartControl;
        var cv = pvm.Curves;

        // ---------------- 容器与版面 ----------------
        var body = chart.GetVisualParent() as Avalonia.Controls.Control;
        var g = chart.Geometry;
        Check(g != null && g.Drawn && body != null && Math.Abs(chart.Bounds.Width - (body.Bounds.Width - 18)) < 0.5 && Math.Abs(chart.Bounds.Height - (body.Bounds.Height - 4)) < 0.5,
            $"曲线容器 = 面板内容区去掉 上 4 / 右 12 / 左 6（{chart.Bounds.Width:0}×{chart.Bounds.Height:0} / 内容区 {body?.Bounds.Width:0}×{body?.Bounds.Height:0}）");
        Info("曲线 " + string.Join(" · ", ShotRunner.ChartFacts(window).Where(l => l.StartsWith("chart", StringComparison.Ordinal))));
        Check(g != null && g.TwoAxes && g.Axes[0].Label == "温度 ℃" && g.Axes[1].Label == "压力 MPa" && g.Series.Count == 4 && g.DrawOrder.Take(2).All(i => !string.IsNullOrEmpty(g.Series[i].Dash))
              && g.Markers.Any(m => m.Label == "RV1 开始反应") && g.RefLines.Count == 0,
            "1号机 RV1：双轴（温度 ℃ / 压力 MPa）、四条线（两条设定值虚线先画、在实线下面）、标记「RV1 开始反应」、没有上限线（压力离上限远）");
        var texts = chart.AuditTexts.ToList();
        Check(texts.Count > 0 && texts.All(t => t.FontSize >= 13) && texts.Where(t => t.Text.Any(ch => ch >= 0x4e00)).All(t => t.FontSize >= 14),
            $"自绘文字报给截图审计（{texts.Count} 段，刻度 ≥ 13 px、中文 ≥ 14 px）");
        int ver = chart.Version;
        await Settle();
        await Settle();
        Check(chart.Version == ver, $"数据不变（冻结）时不重排版面（版本 {ver} → {chart.Version}）");

        // ---------------- 分段 10 / 30 / 60 ----------------
        var seg = panel.RangeControl;
        PrToggle Seg(string label) => seg.GetVisualDescendants().OfType<PrToggle>().First(b => (b.Content as string) == label);
        Check(cv.Range == "30" && Seg("30").IsChecked == true, "分段默认 30（当前段 a-soft）");
        Click(window, Seg("10"));
        await Settle();
        var g10 = chart.Geometry;
        Check(cv.Range == "10" && cv.Options is { } o10 && o10.XMax - o10.XMin == 10 * 60000.0 && g10 != null && g10.TimeStep <= 2 * 60000.0,
            $"点「10」→ 近 10 min（横轴刻度每 {g10?.TimeStep / 60000:0} min：{string.Join(" ", g10?.TimeTicks.Select(t => t.Text) ?? Array.Empty<string>())}）");
        Click(window, Seg("60"));
        await Settle();
        Check(cv.Range == "60" && cv.Options is { } o60 && o60.XMax - o60.XMin == 60 * 60000.0, "点「60」→ 近 60 min");
        Seg("60").Focus(NavigationMethod.Directional);
        Key1(window, Key.Left, PhysicalKey.ArrowLeft);
        await Settle();
        Check(cv.Range == "30", $"← → 在分段之间切换（{cv.Range}）");

        // ---------------- 图例按面板宽度 ----------------
        string Legend() => panel.LegendText;
        async Task<string> At(double w)
        {
            panel.Width = w;
            panel.LayoutWidth = double.NaN;
            await Settle();
            return Legend();
        }
        var full = await At(800);
        var noMk = await At(700);
        var noSp = await At(600);
        var none = await At(480);
        panel.ClearValue(Avalonia.Layout.Layoutable.WidthProperty);
        await Settle();
        Check(full == "温度 设定 压力 目标 标记" && noMk == "温度 设定 压力 目标" && noSp == "温度 压力" && none == "",
            $"图例按面板宽度取舍：800「{full}」· 700「{noMk}」· 600「{noSp}」· 480「{none}」");
        var head = frame.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Head");
        Check(head.Bounds.Height == 42, "标题栏 42 高（图例与分段不撑高）");

        // ---------------- 悬停 ----------------
        g = chart.Geometry!;
        int verHover = chart.Version;
        var inPlot = new Point(g.PadL + g.Pw * 0.5, g.PadT + g.Ph * 0.5);
        var pw = chart.TranslatePoint(inPlot, window) ?? default;
        window.MouseMove(pw);
        await Settle();
        var hv = chart.Hover;
        var tipLayer = chart.GetVisualDescendants().OfType<ChartHoverLayer>().First();
        // 提示框：横向在曲线里（右边放不下就放到十字线左边），纵向 = 指针上方 30、夹在 [绘图区顶, 图高 − 框高 − 4]（同样稿；矮的曲线里框比图高时顶在绘图区顶、下面被裁掉）
        Check(hv != null && hv.Rows.Count == 4 && hv.Rows.Select(r => r.Name).SequenceEqual(new[] { "温度", "温度设定", "压力", "压力设定" })
              && Math.Abs(hv.CrossX - inPlot.X) < g.Pw / 180 + 1 && tipLayer.TipRect.X >= 4 && tipLayer.TipRect.Right <= chart.Bounds.Width
              && tipLayer.TipRect.Y == ChartLayout.TipTop(g, inPlot.Y, tipLayer.TipRect.Height),
            $"指针在曲线上：十字线对齐最近的采样（{hv?.TimeText}，x {hv?.CrossX:0.#}）、四行读数（{string.Join(" · ", hv?.Rows.Select(r => r.Name + " " + r.Value + " " + r.Unit) ?? Array.Empty<string>())}）、提示框 {tipLayer.TipRect}");
        var tipTexts = tipLayer.AuditTexts.ToList();
        Check(tipTexts.Count == 1 + 4 * 3 && tipTexts.All(t => t.FontSize >= 13), $"提示框的文字报给审计（{tipTexts.Count} 段）");
        await Capture(window, Path.Combine(dir, $"shell-rvcurves-hover-{width}.png"), lines);
        var tip0 = tipLayer.TipRect;
        window.MouseMove(new Point(pw.X + 0.3, pw.Y + 20));
        await Settle();
        Check(ReferenceEquals(hv, chart.Hover) && tipLayer.TipRect.Y == ChartLayout.TipTop(g, inPlot.Y + 20, tip0.Height) && tipLayer.TipRect.X == tip0.X && chart.Version == verHover,
            $"同一个采样点：只挪提示框（跟着指针，{tip0.Y:0.#} → {tipLayer.TipRect.Y:0.#}），不重新取读数，曲线不重排（版本 {verHover} → {chart.Version}）");
        window.MouseMove(chart.TranslatePoint(new Point(2, 2), window) ?? default);
        await Settle();
        Check(chart.Hover == null, "指针在绘图区外 → 收起");
        chart.HoverAt(new Point(g.PadL + g.Pw - 1, g.PadT + 10));
        var tipR = tipLayer.TipRect;
        Check(chart.Hover != null && tipR.Right <= chart.Bounds.Width && tipR.X < chart.Hover!.CrossX,
            $"右边放不下：提示框放到十字线左边（框 {tipR.X:0}–{tipR.Right:0}，十字线 {chart.Hover?.CrossX:0}）");
        chart.ClearHover();
        window.MouseMove(new Point(4, 4));
        await Settle();

        // ---------------- 放大 / 关闭 ----------------
        var gridR = page.GetVisualDescendants().OfType<ReactorGridPanel>().First();
        Click(window, frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        var gMax = chart.Geometry;
        Check(frame.IsMaximized && Math.Abs(chart.Bounds.Width - (gridR.Bounds.Width - 18)) < 0.5 && Math.Abs(chart.Bounds.Height - (gridR.Bounds.Height - 42 - 4)) < 0.5
              && Legend() == "温度 设定 压力 目标 标记" && gMax != null && gMax.Width == chart.Bounds.Width,
            $"放大：面板铺满（{frame.Bounds.Width:0}×{frame.Bounds.Height:0}），曲线按新尺寸重排（{chart.Bounds.Width:0}×{chart.Bounds.Height:0}），图例全部显示（{Legend()}）");
        await Capture(window, Path.Combine(dir, $"shell-rvcurves-max-{width}.png"), lines);
        Esc(window);
        await Settle();
        Check(!frame.IsMaximized && Math.Abs(chart.Bounds.Width - (body!.Bounds.Width - 18)) < 0.5, "Esc → 还原");
        var vessel = page.GetVisualDescendants().OfType<ReactorVesselPanel>().First();
        var grid = page.GetVisualDescendants().OfType<ReactorGridPanel>().First();
        Click(window, frame.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
        await Settle();
        Check(frame.IsClosed && !panel.IsVisible && Math.Abs(vessel.Bounds.Height - grid.Bounds.Height) < 0.5,
            $"关掉「曲线」→「釜体 / 属性查看器」铺满右边一栏（{vessel.Bounds.Height:0} / {grid.Bounds.Height:0}）");
        vm.ResetLayout();
        await Settle();
        Check(!frame.IsClosed && panel.IsVisible && chart.Bounds.Height > 100, "视图 › 重置布局 → 曲线回来");

        // ---------------- 总览：温度趋势 ----------------
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var p1 = DemoParams.Parse("devs=1") with { Freeze = true };
        vm.Fleet.ResetDemo(p1);
        vm.ApplyDemoParams(p1);
        vm.ResetView();
        vm.ApplyDemoParams(p1, Route.Overview);
        Frozen.Apply(window);
        await Settle();
        await Settle();
        if (pages.CurrentView is OverviewPage ov && vm.Page is OverviewPageVm opv)
        {
            var tp = ov.GetVisualDescendants().OfType<PrPanel>().First(p => p.Name == "TrendPanel");
            var tc = tp.GetVisualDescendants().OfType<TimeSeriesChart>().First();
            var scroll = ov.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
            Check(opv.Trend.IsShown && tp.IsEffectivelyVisible && tc.Geometry is { Drawn: true } tg && tg.Series.Count == 4 && Math.Abs(scroll.Extent.Height - scroll.Viewport.Height) < 0.5,
                $"一台设备：温度趋势铺满分区下面的地方（面板 {tp.Bounds.Width:0}×{tp.Bounds.Height:0}，曲线 {tc.Bounds.Width:0}×{tc.Bounds.Height:0}，四条线），总览不滚动");
            var lg = tp.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Check(lg.Contains("1号机") && new[] { "RV1", "RV2", "RV3", "RV4" }.All(lg.Contains) && !lg.Any(t => t?.StartsWith("显示", StringComparison.Ordinal) == true),
                $"图例：别名 + RV1–RV4（{string.Join(" ", lg)}）");
            Click(window, tp.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
            await Settle();
            Check(tp.IsMaximized && tp.Bounds.Height >= ov.Bounds.Height - 1 && Math.Abs(tc.Bounds.Height - (tp.Bounds.Height - 42 - 8 - 6)) < 0.5 && tc.Geometry?.Height == tc.Bounds.Height,
                $"放大温度趋势：铺满工作区（{tp.Bounds.Width:0}×{tp.Bounds.Height:0}），曲线跟着变大（{tc.Bounds.Width:0}×{tc.Bounds.Height:0}）");
            await Capture(window, Path.Combine(dir, $"shell-ovtrend-max-{width}.png"), lines);
            Esc(window);
            await Settle();
            Check(!tp.IsMaximized && opv.Trend.IsShown, "Esc → 还原");
        }
        else Check(false, "总览页面是 OverviewPage / OverviewPageVm");

        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
        if (vm.Page is OverviewPageVm back) Check(!back.Trend.IsShown, "三台设备：没有温度趋势（下面不剩 200 px）");
    }
}
#endif
