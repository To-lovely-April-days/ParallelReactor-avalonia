using Avalonia;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls.Chart;

/// <summary>
/// 图例里的一小段线（样稿的 svg 小样）：横线从 (1, h/2) 到 (w − 1, h/2)，竖线（事件标记）从 (w/2, 1) 到 (w/2, h − 1)；颜色 = 色板令牌名，
/// 线宽、虚线（SVG px）、线帽同样稿：
/// 反应釜页 .reactor-lg-*（22 × 10：温度 / 压力 2.5 圆帽实线，设定 / 目标 1.5「5 3」虚线；标记 10 × 16：1.4「3 2」竖虚线）；
/// 总览温度趋势 .overview-lg（24 × 8：线宽 2，虚线按 0.6 缩短）。
/// </summary>
public sealed class LegendSwatch : Avalonia.Controls.Control
{
    public static readonly StyledProperty<string?> TokenProperty = AvaloniaProperty.Register<LegendSwatch, string?>(nameof(Token));
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<LegendSwatch, double>(nameof(StrokeWidth), 2);
    public static readonly StyledProperty<string?> DashProperty = AvaloniaProperty.Register<LegendSwatch, string?>(nameof(Dash));
    public static readonly StyledProperty<bool> VerticalProperty = AvaloniaProperty.Register<LegendSwatch, bool>(nameof(Vertical));
    public static readonly StyledProperty<bool> RoundCapProperty = AvaloniaProperty.Register<LegendSwatch, bool>(nameof(RoundCap));

    static LegendSwatch()
    {
        AffectsRender<LegendSwatch>(TokenProperty, StrokeWidthProperty, DashProperty, VerticalProperty, RoundCapProperty);
        ClipToBoundsProperty.OverrideDefaultValue<LegendSwatch>(false);
    }

    /// <summary>颜色令牌（「--ch-t」）。</summary>
    public string? Token { get => GetValue(TokenProperty); set => SetValue(TokenProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    /// <summary>虚线（SVG 的 px 写法「5 3」；空 = 实线）。</summary>
    public string? Dash { get => GetValue(DashProperty); set => SetValue(DashProperty, value); }
    public bool Vertical { get => GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }
    /// <summary>圆线帽（样稿 stroke-linecap: round 的实线小样）。</summary>
    public bool RoundCap { get => GetValue(RoundCapProperty); set => SetValue(RoundCapProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds.Size;
        if (b.Width <= 0 || b.Height <= 0) return;
        var pen = ChartPaint.Pen(Token, StrokeWidth, Dash, RoundCap ? PenLineCap.Round : PenLineCap.Flat);
        if (Vertical) ctx.DrawLine(pen, new Point(b.Width / 2, 1), new Point(b.Width / 2, b.Height - 1));
        else ctx.DrawLine(pen, new Point(1, b.Height / 2), new Point(b.Width - 1, b.Height / 2));
    }
}
