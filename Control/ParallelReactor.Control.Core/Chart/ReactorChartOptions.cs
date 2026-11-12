using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Chart;

/// <summary>一条会话标记（样稿 session.markers 的一项）：时刻、文字、反应釜号（整机的标记为 null，所有反应釜的曲线上都画）。</summary>
public readonly record struct SessionMark(DateTime At, string Text, int? N);

/// <summary>曲线面板图例的一项（样稿 reactor.js curvesPanel）：文字、颜色令牌、线宽、虚线（SVG px；null = 实线）、是否竖线（标记）、是否设定值（窄面板先藏）。</summary>
public sealed record ChartLegendItem(string Label, string Color, double Width, string? Dash, bool Vertical = false, bool SetPoint = false);

/// <summary>
/// 反应釜的标准曲线（样稿 js/ui.js ui.reactorChartOpts 的移植）：温度（--ch-t 实线 2.5）+ 温度设定（--ch-t-sp 虚线 6 4、1.5、末端无圆点）+
/// 压力（--ch-p，右轴）+ 压力设定（--ch-p-sp 虚线）+ 会话标记（--ch-mark；只画这个反应釜的和整机的）；
/// 接近 / 超过压力上限（反应釜自己的状态是报警，或当前压力 ≥ 量程 × 90 %）时加 --ch-limit 虚线「上限 3.000 MPa」（文字 --r-ink）。
/// 坐标默认分带（<see cref="ChartBands"/>）：温度在上、压力在下；<c>bands: false</c> 关闭。横轴 = [现在 − 窗口, 现在]。
/// 反应釜页另外把空数据文字换成「离线 · 暂无数据」（设备断线时，样稿 reactor.js chartOpts）—— 传 <c>offline: true</c>。
/// </summary>
public static class ReactorChartOptions
{
    /// <summary>曲线窗口（分钟）：分段控件 10 / 30 / 60（样稿 RANGES），默认 30。</summary>
    public static readonly IReadOnlyList<int> Ranges = Array.AsReadOnly(new[] { 10, 30, 60 });
    public const int DefaultMinutes = 30;

    public const string TempName = "温度", TempSpName = "温度设定", PressureName = "压力", PressureSpName = "压力设定";
    public const string TempAxis = "温度 ℃", PressureAxis = "压力 MPa";
    public const string Empty = "暂无数据", EmptyOffline = "离线 · 暂无数据";
    /// <summary>曲线面板标题（样稿 curvesPanel）。</summary>
    public const string PanelTitle = "曲线";
    /// <summary>分段控件右侧的单位、每段的悬停提示（'近 30 min'）。</summary>
    public const string RangeUnit = "min";
    public static string RangeTip(int minutes) => "近 " + minutes + " min";

    /// <summary>上限线的文字：'上限 3.000 MPa'（小数位按型号）。</summary>
    public static string LimitText(ModelProfile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return "上限 " + Fmt.Mpa(model.Limits.PressureMaxMPa, model.PressureDecimals) + " MPa";
    }

    /// <summary>接近 / 超过上限（样稿 nearLimit）：反应釜自己的状态是报警，或当前压力 ≥ 量程 × 0.9。</summary>
    public static bool NearLimit(ModelProfile model, bool alarm, double? pressureNow)
    {
        ArgumentNullException.ThrowIfNull(model);
        double lim = model.Limits.PressureMaxMPa;
        return alarm || (pressureNow is double p && p >= lim * 0.9);
    }

    /// <summary>
    /// 曲线输入。<paramref name="temp"/> 等是窗口内的点（最旧在前；<see cref="History.ChannelHistory.Points"/> 或演示的 series）；
    /// <paramref name="markers"/>：这台设备当前会话的标记（不在记录中为空）；<paramref name="pressureNow"/>：当前压力读数（MPa，没有为 null）；
    /// <paramref name="alarm"/>：反应釜自己的状态是报警（不按断线改写）；<paramref name="minutes"/>：窗口，0 / 负数按 30（JS <c>o.minutes || 30</c>）。
    /// </summary>
    public static ChartOptions Build(ModelProfile model, int n, DateTime now, int minutes,
        IReadOnlyList<ChartPoint> temp, IReadOnlyList<ChartPoint> tempSp, IReadOnlyList<ChartPoint> pressure, IReadOnlyList<ChartPoint> pressureSp,
        IEnumerable<SessionMark>? markers, double? pressureNow, bool alarm, bool bands = true, bool offline = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        int min = minutes > 0 ? minutes : DefaultMinutes;
        double nowMs = ChartTime.Ms(now);
        var marks = new List<ChartMarker>();
        if (markers is not null)
            foreach (var m in markers)
                if (m.N is null || m.N == n) marks.Add(new ChartMarker(ChartTime.Ms(m.At), m.Text) { Color = ChartTokens.Mark });
        int pDec = model.PressureDecimals;
        double lim = model.Limits.PressureMaxMPa;
        bool near = NearLimit(model, alarm, pressureNow);
        var opts = new ChartOptions
        {
            Series = Array.AsReadOnly(new[]
            {
                new ChartSeries(TempName, temp ?? Array.Empty<ChartPoint>()) { Color = ChartTokens.Temp, Axis = 0, Width = 2.5, Unit = "℃", Dec = 1 },
                new ChartSeries(TempSpName, tempSp ?? Array.Empty<ChartPoint>()) { Color = ChartTokens.TempSp, Axis = 0, Dash = "6 4", Width = 1.5, Unit = "℃", Dec = 1, NoEnd = true },
                new ChartSeries(PressureName, pressure ?? Array.Empty<ChartPoint>()) { Color = ChartTokens.Pressure, Axis = 1, Width = 2.5, Unit = "MPa", Dec = 3 },
                new ChartSeries(PressureSpName, pressureSp ?? Array.Empty<ChartPoint>()) { Color = ChartTokens.PressureSp, Axis = 1, Dash = "6 4", Width = 1.5, Unit = "MPa", Dec = 3, NoEnd = true },
            }),
            YAxes = Array.AsReadOnly(new[]
            {
                new ChartAxis { Label = TempAxis, Unit = "℃", MinSpan = 20 },
                new ChartAxis { Label = PressureAxis, Unit = "MPa", Dec = pDec, MinSpan = Math.Max(0.2, lim * 0.25) },
            }),
            RefLines = near
                ? Array.AsReadOnly(new[] { new ChartRefLine(1, lim) { Color = ChartTokens.Limit, LabelColor = ChartTokens.LimitInk, Label = LimitText(model) } })
                : Array.AsReadOnly(Array.Empty<ChartRefLine>()),
            XMin = nowMs - min * 60000.0,
            XMax = nowMs,
            Markers = marks.AsReadOnly(),
            EmptyText = offline ? EmptyOffline : Empty,
        };
        return bands ? ChartBands.Apply(opts, lim, near ? lim : null) : opts;
    }

    /// <summary>
    /// 演示数据（样稿 reactorChartOpts(dev, ch, { minutes })）：DemoDevice.Series + 当前会话的标记 + 当前压力读数 + 反应釜自己的状态。
    /// 反应釜页传 <c>offline: !d.Online</c>（空数据时「离线 · 暂无数据」）；组件陈列等照样稿用默认的「暂无数据」。
    /// </summary>
    public static ChartOptions FromDemo(DemoDevice d, DemoChannel ch, int minutes = DefaultMinutes, bool bands = true, bool offline = false)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(ch);
        int min = minutes > 0 ? minutes : DefaultMinutes;
        IReadOnlyList<ChartPoint> S(DemoReading k) => Points(d.Series(ch.N, k, min));
        var marks = new List<SessionMark>();
        if (d.Session is { } s) foreach (var m in s.Markers) marks.Add(new SessionMark(m.At, m.Text, m.N));
        return Build(d.Model, ch.N, d.Fleet.Now, min, S(DemoReading.TempC), S(DemoReading.SpC), S(DemoReading.PressureMPa), S(DemoReading.PTargetMPa),
            marks, d.Reading(ch, DemoReading.PressureMPa), ch.State == StateKey.Alarm, bands, offline);
    }

    /// <summary>(时刻, 值) 序列 → 曲线点（null → NaN）。</summary>
    public static IReadOnlyList<ChartPoint> Points(IReadOnlyList<(DateTime T, double? V)> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var a = new ChartPoint[series.Count];
        for (int i = 0; i < a.Length; i++) a[i] = ChartPoint.At(series[i].T, series[i].V);
        return a;
    }

    /// <summary>
    /// 曲线面板的图例（样稿 curvesPanel + reactor.css .reactor-lg-*）：温度 / 设定 / 压力 / 目标 / 标记，按面板宽度取舍（容器 rvcurve）：
    /// ≤ 720 去掉「标记」，≤ 640 再去掉两个设定值（「设定」「目标」），≤ 500 整个图例不显示。图例线的虚线是 SVG px（5 3 / 3 2）。
    /// </summary>
    public static IReadOnlyList<ChartLegendItem> Legend(double panelWidth)
    {
        var list = new List<ChartLegendItem>(5);
        if (panelWidth <= 500) return list.AsReadOnly();
        bool sp = panelWidth > 640, mk = panelWidth > 720;
        list.Add(new ChartLegendItem("温度", ChartTokens.Temp, 2.5, null));
        if (sp) list.Add(new ChartLegendItem("设定", ChartTokens.TempSp, 1.5, "5 3", SetPoint: true));
        list.Add(new ChartLegendItem("压力", ChartTokens.Pressure, 2.5, null));
        if (sp) list.Add(new ChartLegendItem("目标", ChartTokens.PressureSp, 1.5, "5 3", SetPoint: true));
        if (mk) list.Add(new ChartLegendItem("标记", ChartTokens.Mark, 1.4, "3 2", Vertical: true));
        return list.AsReadOnly();
    }
}

/// <summary>
/// 曲线的绘制样式（样稿 base.css .pr-chart-* / reactor.css）：线宽、颜色令牌、虚线（SVG px）与字号。界面画线时虚线要换成「线宽的倍数」（Avalonia DashStyle），见 <see cref="DashInWidths"/>。
/// </summary>
public static class ChartStyle
{
    /// <summary>绘图区底色、网格（1 px）、坐标框（1.5 px）。</summary>
    public const string Plot = "--ch-bg", Grid = "--ch-grid", AxisLine = "--ch-axis";
    public const double GridWidth = 1, AxisWidth = 1.5;
    /// <summary>刻度文字 13 px（--t-3，等宽数字）；轴标题 14 px / 500（--t-3）；参考线 / 标记文字 14 px / 600，带 4 px --ch-bg 描边；空数据 15 px（--t-3）。</summary>
    public const double TickFont = 13, AxisTitleFont = 14, LabelFont = 14, EmptyFont = 15, LabelHalo = 4;
    public const int AxisTitleWeight = 500, LabelWeight = 600;
    public const string TickInk = "--t-3";
    /// <summary>事件标记 1.4 px、虚线 5 4；参考线 1.4 px（虚线 6 4）。</summary>
    public const double MarkerWidth = 1.4, RefWidth = 1.4;
    public const string MarkerDash = "5 4", RefDash = "6 4";
    /// <summary>悬停十字线：--t-4、1 px、虚线 4 3；悬停圆点半径 4、描边 2（系列色）、填充 --bg-card；末端圆点半径 3。</summary>
    public const string CrossInk = "--t-4", CrossDash = "4 3";
    public const double CrossWidth = 1, HoverDotR = 4, HoverDotStroke = 2, EndDotR = 3;
    /// <summary>反应釜页曲线在面板内容区里的内边距（reactor.css .reactor-chart：上 4、右 12、下 0、左 6）。</summary>
    public const double InsetTop = 4, InsetRight = 12, InsetBottom = 0, InsetLeft = 6;
    /// <summary>悬停提示框：最小宽 180、内边距 10 / 14、行高 1.8 × 14、时间 13 px。</summary>
    public const double TipMinWidth = 180;

    /// <summary>SVG 的 stroke-dasharray（px）→ Avalonia DashStyle 的段长（线宽的倍数）。null / 空 → null（实线）。</summary>
    public static double[]? DashInWidths(string? dash, double width)
    {
        if (string.IsNullOrWhiteSpace(dash)) return null;
        var parts = dash.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var a = new double[parts.Length];
        double w = width > 0 ? width : 1;
        for (int i = 0; i < parts.Length; i++)
            a[i] = double.Parse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) / w;
        return a;
    }
}
