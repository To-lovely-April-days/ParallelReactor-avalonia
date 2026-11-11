#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Chart;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Steps;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;
using ParallelReactor.Control.Views.Pages.Reactor;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（g5：反应釜页的框架、面板网格、釜体 / 属性面板、曲线）在真的窗口里核对（演示数据、冻结）：
/// <list type="bullet">
/// <item>c2-ui-6：宽版式（2560 宽）的「釜体」面板不再报属性查看器的尺寸 —— 关掉 / 打开「程序」、放大 / 还原、窄到页签版式再宽回来之后，
///   属性卡片的列数、放得下几张、「另有 N 项」都与属性网格（PropGridPanel 按自己的宽度）一致。</item>
/// <item>v3-states-1：状态条的字用子选择器 ——「连接中断」徽标的字 14 px r-ink、「正在连接…」14 px t-2，「立即重连」的字跟着按钮（悬停 a-ink）；条上自己的字照旧。</item>
/// <item>v3-states-5：断线的舞台上缺值的「—」是 t-3（stale 盖过 n/a），在线时 t-dis；字重都是 400。</item>
/// <item>v3-states-4：未安装的当前位置的号码 600 a-ink。</item>
/// <item>c2-ui-3：纵坐标算不出来（NaN）的曲线点当断开、末端圆点不画（画出来的几何里没有 NaN）。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M5ReviewHeadlessTests
{
    private static Task Run(Action body) => M5ReviewHeadless.RunAsync(body);

    /// <summary>排版 + 渲染几轮（无头平台的渲染计时器不会自己跑）。</summary>
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

    /// <summary>新外壳的主窗口（演示数据、冻结），套用地址参数后打开 <paramref name="route"/>（同截图的 ShotRunner.Show）。</summary>
    private static (MainWindow Win, ShellVm Shell) Open(string query, double w, double h, Route route, Action<FleetVm>? setup = null)
    {
        var p = DemoParams.Parse(query) with { Freeze = true };
        var fleet = FleetVm.CreateDemo(p, a => Dispatcher.UIThread.Post(a));
        var shell = new ShellVm(fleet);
        shell.Toasts.Frozen = true;
        var win = new MainWindow { DataContext = shell, Width = w, Height = h };
        Frozen.Apply(win);
        win.Show();
        Settle(win);
        if (setup != null)
        {
            setup(fleet);
            fleet.RefreshNow();
        }
        shell.ApplyDemoParams(p, route);
        Settle(win);
        return (win, shell);
    }

    private static ReactorGridPanel Grid(Window w) => w.GetVisualDescendants().OfType<ReactorGridPanel>().First(g => g.IsEffectivelyVisible);

    private static Border Strip(Window w) => w.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("rvalert") && b.IsEffectivelyVisible);

    private static void AssertColor(Color want, IBrush? brush, string what) =>
        Assert.True(brush is ISolidColorBrush sb && sb.Color == want, $"{what}：{(brush as ISolidColorBrush)?.Color.ToString() ?? brush?.ToString() ?? "null"}，应为 {want}");

    // ———————————————————— c2-ui-6：属性查看器的尺寸只由放属性卡片的面板报 ————————————————————

    /// <summary>视图模型的列数 / 行数 / 显示的卡片 /「另有 N 项」与放属性卡片的那一块（内容区 Body、卡片网格 PropGridPanel）一致。</summary>
    private static void AssertConsistent(ReactorVesselVm vm, ReactorVesselPanel panel, string what)
    {
        var body = panel.FindControl<Panel>("Body")!;
        var cards = panel.GetVisualDescendants().OfType<PropGridPanel>().First();
        double bw = body.Bounds.Width, bh = body.Bounds.Height;
        int cols = ReactorVesselVm.ColumnsFor(bw);
        int rows = Math.Max(1, (int)Math.Floor((bh - 4) / ReactorVesselVm.PropH));
        int total = vm.Props.Count - vm.Hidden.Count;
        int fit = Math.Max(cols, cols * rows);
        string state = $"{what}：属性面板（{panel.Mode}）内容区 {bw:0.#}×{bh:0.#} = {cols} 列 × {rows} 行；视图模型 {vm.PropCols} 列 × {vm.PropRows} 行、"
                       + $"显示 {vm.ShownProps.Count} 张、「{vm.MoreText}」；卡片网格 {cards.Columns} 列";
        Assert.True(bw > 0 && bh > 0 && panel.IsEffectivelyVisible, state);
        Assert.True(vm.PropCols == cols && vm.PropRows == rows && cards.Columns == cols, state);
        Assert.True(vm.ShownProps.Count == Math.Min(total, fit) && Math.Ceiling(vm.ShownProps.Count / (double)cards.Columns) <= rows, state);
        Assert.Equal(total > fit ? $"另有 {total - fit} 项" : "", vm.MoreText);
    }

    [Fact]
    public Task WideLayout_PropsViewport_FollowsThePropsPanelOnly() => Run(() =>
    {
        var (win, _) = Open("explorer=0", 2560, 1080, Route.Reactor(1, 1));
        try
        {
            var grid = Grid(win);
            Assert.True(grid.Grid is { Wide: true }, $"2560 收起资源管理器：宽版式（网格 {grid.Bounds.Width:0}）");
            var vessel = grid.Children.OfType<ReactorVesselPanel>().Single(p => p.Tag as string == "vessel");
            var props = grid.Children.OfType<ReactorVesselPanel>().Single(p => p.Tag as string == "props");
            Assert.Equal(VesselPanelMode.Vessel, vessel.Mode);
            Assert.Equal(VesselPanelMode.Props, props.Mode);
            var vm = (ReactorVesselVm)props.DataContext!;
            AssertConsistent(vm, props, "宽版式");

            // 关掉「程序」：只有「釜体」面板变宽（属性查看器不变）—— 以前它报的尺寸盖掉了属性查看器的（4 列 12 张 vs 网格 2 列）
            var prog = grid.Children.OfType<ReactorProgramPanel>().Single();
            double vw = vessel.Bounds.Width;
            prog.PanelControl.IsClosed = true;
            Settle(win);
            Assert.True(vessel.Bounds.Width > vw + 300, $"关掉「程序」后「釜体」面板变宽（{vw:0} → {vessel.Bounds.Width:0}）");
            AssertConsistent(vm, props, "关掉「程序」");
            prog.PanelControl.IsClosed = false;
            Settle(win);
            AssertConsistent(vm, props, "重新打开「程序」");

            // 放大「釜体」面板（属性查看器藏起来）再还原：属性查看器回来时尺寸没变（没有 SizeChanged）
            vessel.PanelControl.IsMaximized = true;
            Settle(win);
            Assert.False(props.IsVisible);
            vessel.PanelControl.IsMaximized = false;
            Settle(win);
            AssertConsistent(vm, props, "放大「釜体」再还原");

            // 窄到页签版式（一块「釜体 / 属性查看器」页签面板报自己的尺寸）再宽回来：属性查看器回来时尺寸与之前相同（不会再有 SizeChanged），
            // 要靠它重新显示时报一次
            var propsBody = props.FindControl<Panel>("Body")!;
            var before = propsBody.Bounds.Size;
            win.Width = 1920;
            Settle(win);
            Assert.True(grid.Grid is { Wide: false } && vessel.Mode == VesselPanelMode.Tabs && !props.IsVisible, "1920：页签版式");
            vm.Tab = ReactorVesselVm.PropsTabValue;
            Settle(win);
            AssertConsistent(vm, vessel, "页签版式");
            vm.Tab = ReactorVesselVm.VesselTabValue;
            Settle(win);
            win.Width = 2560;
            Settle(win);
            Assert.True(grid.Grid is { Wide: true } && vessel.Mode == VesselPanelMode.Vessel, "宽回 2560：宽版式");
            Assert.Equal(before, propsBody.Bounds.Size);
            AssertConsistent(vm, props, "窄到页签版式再宽回来");
        }
        finally { win.Close(); }
    });

    // ———————————————————— v3-states-1：状态条的字用子选择器 ————————————————————

    [Fact]
    public Task AlertStrip_OfflineBadgeAndButton_KeepTheirOwnText() => Run(() =>
    {
        var (win, _) = Open("offline=1", 1920, 1080, Route.Reactor(1, 1));
        try
        {
            var strip = Strip(win);
            var badge = strip.GetVisualDescendants().OfType<ConnBadge>().Single(b => b.IsVisible);
            var bt = badge.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Text");
            Assert.Equal("连接中断 · 重连中 12 s", bt.Text);
            Assert.Equal(14, bt.FontSize);
            Assert.Equal(FontWeight.Medium, bt.FontWeight);
            AssertColor(Palette.RInkColor, bt.Foreground, "「连接中断」徽标的字");
            // 条上自己的说明照旧 15 px t-2
            var line = strip.GetVisualDescendants().OfType<ChemLine>().Single();
            Assert.Equal(15, line.FontSize);
            AssertColor(Palette.T2Color, line.Foreground, "条上的说明");
            // 「立即重连」：字跟着按钮（15 px 500 t-2；悬停 a-ink）—— 以前被条上的后代选择器钉成 t-2，悬停不变色
            var btn = strip.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible);
            var label = btn.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "立即重连");
            Assert.Equal(15, label.FontSize);
            Assert.Equal(FontWeight.Medium, label.FontWeight);
            AssertColor(Palette.T2Color, label.Foreground, "「立即重连」的字");
            // 悬停直接设 :pointerover（按钮主题的悬停样式给按钮 a-ink，字靠继承）：要核对的是样式的层叠（条上的规则不再压住按钮的字），
            // 与命中测试无关；把旧的后代选择器加回去时这条照样失败（复核实测）
            ((IPseudoClasses)btn.Classes).Set(":pointerover", true);
            Settle(win);
            AssertColor(Palette.AInkColor, label.Foreground, "悬停时「立即重连」的字");
        }
        finally { win.Close(); }
    });

    [Fact]
    public Task AlertStrip_ConnectingBadge_IsFourteenPxT2() => Run(() =>
    {
        // 刚添加、从未连上的设备（同截图 rv-never 的「adddevice」）：「正在连接…」徽标
        var (win, _) = Open("", 1920, 1080, Route.Reactor(4, 1), f =>
            f.DemoData!.AddDevice(new DemoNewDevice(Host: "192.168.1.50", Port: 8080, Token: "x", Alias: "5号机", AutoConnect: true, ModelKey: "PR8")));
        try
        {
            var badge = Strip(win).GetVisualDescendants().OfType<ConnBadge>().Single(b => b.IsVisible);
            var bt = badge.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Text");
            Assert.Equal("正在连接…", bt.Text);
            Assert.Equal(14, bt.FontSize);
            AssertColor(Palette.T2Color, bt.Foreground, "「正在连接…」徽标的字");
        }
        finally { win.Close(); }
    });

    [Fact]
    public Task AlertStrip_OwnTexts_KeepTheStripStyle() => Run(() =>
    {
        // 2号机 RV1 超压报警：粗体标题 15 px 600 t-1、时刻 13 px t-3、「确认」的字跟着按钮
        var (win, _) = Open("alarm=2:1", 1920, 1080, Route.Reactor(2, 1));
        try
        {
            var strip = Strip(win);
            var title = strip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("t"));
            Assert.Equal("超压报警", title.Text);
            Assert.True(title.FontSize == 15 && title.FontWeight == FontWeight.SemiBold && title.LineHeight == 21, $"标题 {title.FontSize} {title.FontWeight} {title.LineHeight}");
            AssertColor(Palette.T1Color, title.Foreground, "标题");
            var time = strip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("time"));
            Assert.Equal(13, time.FontSize);
            AssertColor(Palette.T3Color, time.Foreground, "时刻");
            var ok = strip.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "确认");
            Assert.Equal(FontWeight.Medium, ok.FontWeight);
            AssertColor(Palette.T2Color, ok.Foreground, "「确认」的字");
        }
        finally { win.Close(); }
    });

    // ———————————————————— v3-states-4：未安装的当前位置 ————————————————————

    [Fact]
    public Task UninstalledSlots_CurrentNumberIsSemiBold() => Run(() =>
    {
        var (win, _) = Open("", 1920, 1080, Route.Reactor(1, 5));
        try
        {
            var un = Grid(win).Children.OfType<ReactorUnPanel>().Single();
            var slots = un.GetVisualDescendants().OfType<Button>().Where(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("slotn"))).ToList();
            TextBlock Num(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("slotn"));
            var cur = slots.Single(b => b.Classes.Contains("cur"));
            Assert.True(cur.Classes.Contains("un"), "当前位置（RV5）未安装");
            Assert.Equal("5", Num(cur).Text);
            Assert.Equal(FontWeight.SemiBold, Num(cur).FontWeight);
            AssertColor(Palette.AInkColor, Num(cur).Foreground, "当前位置的号码");
            foreach (var b in slots.Where(b => !b.Classes.Contains("cur")))
            {
                bool un2 = b.Classes.Contains("un");
                Assert.Equal(un2 ? FontWeight.Normal : FontWeight.SemiBold, Num(b).FontWeight);
                AssertColor(un2 ? Palette.T4Color : Palette.T2Color, Num(b).Foreground, $"位置 {Num(b).Text}");
            }
        }
        finally { win.Close(); }
    });

    // ———————————————————— v3-states-5：断线时缺值的「—」t-3 ————————————————————

    [Fact]
    public Task StageCallout_StaleBeatsNa_ForTheValueColour() => Run(() =>
    {
        var na = new CalloutContent("TT", "反应器温度", Fmt.Dash, "℃", "加热器", Main: true);
        var val = new CalloutContent("TT", "反应器温度", "25.0", "℃", "加热器", Main: true);
        var stage = new VesselStage { Tt = na, IsStale = true };
        PrText Value()
        {
            stage.Measure(new Size(900, 600));
            stage.Arrange(new Rect(0, 0, 900, 600));
            return stage.Box(VesselCallout.Tt).GetVisualDescendants().OfType<PrText>().Single(t => t.Classes.Contains("num"));
        }
        var v = Value();
        Assert.Equal(Fmt.Dash, v.Text);
        AssertColor(Palette.T3Color, v.Foreground, "断线时缺值的「—」（样稿 .reactor-stage.is-stale 盖过 b.is-na）");
        Assert.Equal(FontWeight.Normal, v.FontWeight);
        stage.IsStale = false;
        v = Value();
        AssertColor(Palette.TDisColor, v.Foreground, "在线时缺值的「—」");
        Assert.Equal(FontWeight.Normal, v.FontWeight);
        stage.Tt = val;
        v = Value();
        AssertColor(Palette.T1Color, v.Foreground, "在线的读数");
        Assert.Equal(FontWeight.SemiBold, v.FontWeight);
        stage.IsStale = true;
        v = Value();
        AssertColor(Palette.T3Color, v.Foreground, "断线的读数");
        Assert.Equal(FontWeight.SemiBold, v.FontWeight);
    });

    // ———————————————————— c2-ui-3：纵坐标是 NaN 的点当断开 ————————————————————

    [Fact]
    public Task Chart_NonFiniteY_IsABreak_NothingNaNIsDrawn() => Run(() =>
    {
        // 样稿分带在 −5 ℃ 时给的颠倒范围（下限 0 > 上限 −1.4）：温度轴算成 NaN —— ChartBands 现在不会再给出它，这里直接写进轴里
        const double t0 = 1_762_567_920_000, step = 10_000;
        ChartPoint[] S(double v) => Enumerable.Range(0, 181).Select(i => new ChartPoint(t0 + i * step, v)).ToArray();
        var opts = new ChartOptions
        {
            Series = new[]
            {
                new ChartSeries(ReactorChartOptions.TempName, S(-5)) { Color = ChartTokens.Temp, Axis = 0, Width = 2.5, Unit = "℃", Dec = 1 },
                new ChartSeries(ReactorChartOptions.PressureName, S(0.35)) { Color = ChartTokens.Pressure, Axis = 1, Width = 2.5, Unit = "MPa", Dec = 3 },
            },
            YAxes = new[]
            {
                new ChartAxis { Label = ReactorChartOptions.TempAxis, Unit = "℃", MinSpan = 20, Min = 0, Max = -1.4, MaxTicks = 5 },
                new ChartAxis { Label = ReactorChartOptions.PressureAxis, Unit = "MPa", Dec = 3, Min = 0, Max = 1, MaxTicks = 5 },
            },
            XMin = t0,
            XMax = t0 + 180 * step,
        };
        var chart = new TimeSeriesChart { Options = opts };
        chart.Measure(new Size(640, 315));
        chart.Arrange(new Rect(0, 0, 640, 315));
        var g = chart.Geometry!;
        Assert.True(double.IsNaN(g.Axes[0].Lo), "前提：温度轴算成 NaN");
        Assert.Contains(chart.Scene.Dots, d => double.IsNaN(d.Y));

        // 录下 Render 画的东西（DrawingGroup 把几何一律包成 PlatformGeometry）：曲线 = 2.5 px 的系列色画笔、没有填充；末端圆点 = 系列色填充、没有画笔
        var group = new DrawingGroup();
        using (var ctx = group.Open()) chart.Render(ctx);
        Color temp = ChartPaint.Of(ChartTokens.Temp), pres = ChartPaint.Of(ChartTokens.Pressure);
        var bad = new List<string>();
        int tempPaths = 0, presPaths = 0, tempDots = 0, presDots = 0, geos = 0;
        void Walk(Drawing d)
        {
            if (d is DrawingGroup dg)
            {
                foreach (var c in dg.Children) Walk(c);
                return;
            }
            if (d is not GeometryDrawing gd) return;
            geos++;
            var b = gd.Geometry?.Bounds ?? default;
            if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Width) || !double.IsFinite(b.Height)) bad.Add("几何的范围 " + b);
            var pen = (gd.Pen?.Brush as ISolidColorBrush)?.Color;
            var fill = (gd.Brush as ISolidColorBrush)?.Color;
            if (gd.Pen is { Thickness: 2.5 } && gd.Brush == null)
            {
                if (pen == temp) tempPaths++;
                else if (pen == pres) presPaths++;
            }
            else if (gd.Pen == null && fill == temp) tempDots++;
            else if (gd.Pen == null && fill == pres) presDots++;
        }
        Walk(group);
        string what = $"画了 {geos} 个几何：温度线 {tempPaths}、压力线 {presPaths}、温度末端圆点 {tempDots}、压力末端圆点 {presDots}";
        Assert.True(bad.Count == 0, string.Join("；", bad) + "；" + what);
        // 温度线一个有限的点都没有：整条不画、末端圆点也不画；压力照常
        Assert.True(tempPaths == 0 && tempDots == 0 && presPaths == 1 && presDots == 1, what);
    });
}
#endif
