using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls;

/// <summary>一条曲线：X 为分钟，Y 为原值；Right=true 用右轴。</summary>
public sealed class ChartSeries
{
    public ChartSeries(string name, Color color, bool right = false) { Name = name; Color = color; Right = right; }
    public string Name { get; }
    public Color Color { get; }
    public bool Right { get; }
    public List<Point> Points { get; } = new();
    public double Thickness { get; set; } = 1.15;
}

/// <summary>
/// mock 的 <c>#processChart</c>：viewBox 470×190、preserveAspectRatio=none 的自绘复刻。
/// 绘图区 x 45→432、y 20→158；左轴 0→LeftMax（11 刻度），右轴 0→RightMax，底轴 0→XMaxMinutes（8 刻度）；
/// 网格 #dedfe1 .8，轴 #aaaeb1 .9，刻度字 7px Arial #666a6c，标签 7px #55595b。线宽 1.15 round。
/// </summary>
public sealed class ProcessChart : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> LeftMaxProperty = AvaloniaProperty.Register<ProcessChart, double>(nameof(LeftMax), 100);
    public static readonly StyledProperty<double> RightMaxProperty = AvaloniaProperty.Register<ProcessChart, double>(nameof(RightMax), 500);
    public static readonly StyledProperty<double> XMaxMinutesProperty = AvaloniaProperty.Register<ProcessChart, double>(nameof(XMaxMinutes), 88);
    public static readonly StyledProperty<string> XLabelProperty = AvaloniaProperty.Register<ProcessChart, string>(nameof(XLabel), "程序时间 (mins)");
    public static readonly StyledProperty<string> LeftLabelProperty = AvaloniaProperty.Register<ProcessChart, string>(nameof(LeftLabel), "温度 (℃)");
    public static readonly StyledProperty<string> RightLabelProperty = AvaloniaProperty.Register<ProcessChart, string>(nameof(RightLabel), "压力 (psi) / 转速 (rpm ÷ 2)");
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty = AvaloniaProperty.Register<ProcessChart, IReadOnlyList<ChartSeries>?>(nameof(Series));
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<ProcessChart, int>(nameof(Revision));

    private static readonly Typeface TickFace = new(new FontFamily("Arial, Helvetica, PingFang SC"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#dedfe1"));
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#aaaeb1"));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#666a6c"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#55595b"));

    public static readonly Color TempColor = Color.Parse("#91999a");
    public static readonly Color OilColor = Color.Parse("#9ab4a1");
    public static readonly Color SpeedColor = Color.Parse("#7f8d92");

    static ProcessChart()
    {
        AffectsRender<ProcessChart>(LeftMaxProperty, RightMaxProperty, XMaxMinutesProperty, XLabelProperty, LeftLabelProperty, RightLabelProperty, SeriesProperty, RevisionProperty);
    }

    public double LeftMax { get => GetValue(LeftMaxProperty); set => SetValue(LeftMaxProperty, value); }
    public double RightMax { get => GetValue(RightMaxProperty); set => SetValue(RightMaxProperty, value); }
    public double XMaxMinutes { get => GetValue(XMaxMinutesProperty); set => SetValue(XMaxMinutesProperty, value); }
    public string XLabel { get => GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }
    public string LeftLabel { get => GetValue(LeftLabelProperty); set => SetValue(LeftLabelProperty, value); }
    public string RightLabel { get => GetValue(RightLabelProperty); set => SetValue(RightLabelProperty, value); }
    public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    /// <summary>VM 每帧 +1 触发重绘（Series 列表本身不变）。</summary>
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    private const double VbW = 470, VbH = 190, X0 = 45, X1 = 432, Y0 = 20, Y1 = 158;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w < 10 || h < 10) return;
        double sx = w / VbW, sy = h / VbH;
        double px0 = X0 * sx, px1 = X1 * sx, py0 = Y0 * sy, py1 = Y1 * sy;

        var grid = new Pen(GridBrush, 0.8);
        var axis = new Pen(AxisBrush, 0.9);
        // 横向网格 10 等分（11 条）、纵向 8 等分 + 右边界
        for (int i = 0; i <= 10; i++)
        {
            double y = py0 + (py1 - py0) * i / 10.0;
            ctx.DrawLine(grid, new Point(px0, y), new Point(px1, y));
        }
        double xmax = Math.Max(1, XMaxMinutes);
        var (step, ticks) = NiceStep(xmax);
        for (int i = 1; i <= ticks; i++)
        {
            double x = px0 + (px1 - px0) * (i * step / xmax);
            if (x > px1 + 0.5) break;
            ctx.DrawLine(grid, new Point(x, py0), new Point(x, py1));
        }
        ctx.DrawLine(grid, new Point(px0, py0), new Point(px0, py1));
        ctx.DrawLine(grid, new Point(px1, py0), new Point(px1, py1));
        // 轴
        var axisGeo = new StreamGeometry();
        using (var g = axisGeo.Open())
        {
            g.BeginFigure(new Point(px0, py0), false); g.LineTo(new Point(px0, py1)); g.LineTo(new Point(px1, py1)); g.LineTo(new Point(px1, py0)); g.EndFigure(false);
        }
        ctx.DrawGeometry(null, axis, axisGeo);

        // 刻度文字
        for (int i = 0; i <= 10; i++)
        {
            double y = py0 + (py1 - py0) * i / 10.0;
            var lv = LeftMax - LeftMax * i / 10.0;
            var rv = RightMax - RightMax * i / 10.0;
            DrawText(ctx, Fmt(lv), 39 * sx, y + 3, TextAlignment.Right, TickBrush);
            DrawText(ctx, Fmt(rv), 437 * sx, y + 3, TextAlignment.Left, TickBrush);
        }
        for (int i = 1; i <= ticks; i++)
        {
            double x = px0 + (px1 - px0) * (i * step / xmax);
            if (x > px1 + 0.5) break;
            DrawText(ctx, Fmt(i * step), x, 168 * sy, TextAlignment.Center, TickBrush);
        }
        // 标签
        DrawText(ctx, XLabel, 251 * sx, 183 * sy, TextAlignment.Center, LabelBrush);
        DrawRotated(ctx, LeftLabel, 10 * sx, 90 * sy, -90);
        DrawRotated(ctx, RightLabel, 465 * sx, 90 * sy, 90);

        // 曲线（裁剪到绘图区）
        var series = Series;
        if (series == null) return;
        using (ctx.PushClip(new Rect(px0 - 1, py0 - 1, px1 - px0 + 2, py1 - py0 + 2)))
        {
            foreach (var s in series)
            {
                if (s.Points.Count < 1) continue;
                double ymax = s.Right ? RightMax : LeftMax;
                if (ymax <= 0) continue;
                var pen = new Pen(new SolidColorBrush(s.Color), s.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    bool first = true;
                    foreach (var p in s.Points)
                    {
                        var x = px0 + (px1 - px0) * Math.Clamp(p.X / xmax, 0, 1);
                        var y = py1 - (py1 - py0) * Math.Clamp(p.Y / ymax, 0, 1);
                        if (first) { g.BeginFigure(new Point(x, y), false); first = false; }
                        else g.LineTo(new Point(x, y));
                    }
                    g.EndFigure(false);
                }
                if (s.Points.Count == 1)
                {
                    var p = s.Points[0];
                    var x = px0 + (px1 - px0) * Math.Clamp(p.X / xmax, 0, 1);
                    var y = py1 - (py1 - py0) * Math.Clamp(p.Y / ymax, 0, 1);
                    ctx.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(x, y), 1.2, 1.2);
                }
                else ctx.DrawGeometry(null, pen, geo);
            }
        }
    }

    private static (double step, int ticks) NiceStep(double xmax)
    {
        // mock：88 min 窗口 → 10 min 步、8 个刻度
        double raw = xmax / 8.8;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double n = raw / mag;
        double step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        step *= mag;
        return (step, (int)Math.Floor(xmax / step + 1e-9));
    }

    private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);

    private static void DrawText(DrawingContext ctx, string text, double x, double baselineY, TextAlignment align, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TickFace, 7, brush);
        double ox = align switch { TextAlignment.Right => x - ft.Width, TextAlignment.Center => x - ft.Width / 2, _ => x };
        // SVG 的 y 为基线：FormattedText 的 Baseline 为顶到基线距离
        ctx.DrawText(ft, new Point(ox, baselineY - ft.Baseline));
    }

    private static void DrawRotated(DrawingContext ctx, string text, double cx, double cy, double angle)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TickFace, 7, LabelBrush);
        using (ctx.PushTransform(Matrix.CreateRotation(angle * Math.PI / 180) * Matrix.CreateTranslation(cx, cy)))
            ctx.DrawText(ft, new Point(-ft.Width / 2, -ft.Baseline));
    }
}
