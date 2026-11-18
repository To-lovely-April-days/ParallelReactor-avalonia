using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Pages.Curves;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Curves;

/// <summary>
/// 通道识别色的短线（样稿 svg.key：22 × 4，线宽 2.4、圆头；RV9–16 虚线 6 4）。通道列表、读数表、提示框都用它。
/// </summary>
public sealed class KeyLine : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<KeyLine, Color>(nameof(Color));
    public static readonly StyledProperty<bool> DashedProperty = AvaloniaProperty.Register<KeyLine, bool>(nameof(Dashed));

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool Dashed { get => GetValue(DashedProperty); set => SetValue(DashedProperty, value); }

    static KeyLine()
    {
        AffectsRender<KeyLine>(ColorProperty, DashedProperty);
        WidthProperty.OverrideDefaultValue<KeyLine>(22);
        HeightProperty.OverrideDefaultValue<KeyLine>(4);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, y = Bounds.Height / 2;
        ctx.DrawLine(Palette.Pen(Color, 2.4, Dashed ? "2.5 1.67" : null, Dashed ? PenLineCap.Flat : PenLineCap.Round), new Point(1, y), new Point(w - 1, y));
    }
}

/// <summary>竖着排、每个可见的子元素一样高（样稿 .cvc{flex:1}：勾了几个量就几张图平分高度）。</summary>
public sealed class EqualRows : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var vis = Children.Where(c => c.IsVisible).ToList();
        double h = vis.Count > 0 && double.IsFinite(availableSize.Height) ? availableSize.Height / vis.Count : double.PositiveInfinity;
        double w = 0;
        foreach (var c in vis)
        {
            c.Measure(new Size(availableSize.Width, h));
            w = Math.Max(w, c.DesiredSize.Width);
        }
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : w, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var vis = Children.Where(c => c.IsVisible).ToList();
        double h = vis.Count > 0 ? Math.Floor(finalSize.Height / vis.Count) : 0, y = 0;
        for (int i = 0; i < vis.Count; i++)
        {
            double hh = i == vis.Count - 1 ? finalSize.Height - y : h;
            vis[i].Arrange(new Rect(0, y, finalSize.Width, hh));
            y += hh;
        }
        return finalSize;
    }
}

/// <summary>
/// 十字光标的提示框（样稿 .cvtip）：浮在几张图上面，顶边离图区 8；在光标右边 16，放不下改到左边，不出图区。
/// 白底、line-2 细边、圆角 3、ShadowTip；内边距 8 10、最小宽 180；第一行时刻 13/600 t-1；每路一行（行高 20）：识别色短线 14 · RVn（36 宽，t-3）· 各量读数（600 t-1）+ 单位（t-3）。
/// 不接指针（IsHitTestVisible = false）。自绘文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class CurveTipLayer : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<CurveTipData?> TipProperty = AvaloniaProperty.Register<CurveTipLayer, CurveTipData?>(nameof(Tip));

    public CurveTipData? Tip { get => GetValue(TipProperty); set => SetValue(TipProperty, value); }

    /// <summary>图区右侧的内边距（样稿 .cvcharts padding-right 6）：图比这一层窄这么多。</summary>
    public double ChartRightPad { get; set; } = 6;

    private const double Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    static CurveTipLayer()
    {
        AffectsRender<CurveTipLayer>(TipProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<CurveTipLayer>(false);
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    private FormattedText Text(string s, IBrush b, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, w), Fs, b);

    private double Draw(DrawingContext ctx, string s, double x, double top, IBrush b, Color c, FontWeight w = FontWeight.Normal)
    {
        var t = Text(s, b, w);
        ctx.DrawText(t, new Point(x, top));
        _audit.Add(new AuditTextRun(s, Fs, new Rect(x, top, t.Width, t.Height), c));
        return t.Width;
    }

    public override void Render(DrawingContext ctx)
    {
        _audit.Clear();
        var d = Tip;
        if (d == null || Bounds.Width < 160) return;
        double cw = Bounds.Width - ChartRightPad;
        double iw = cw - CurveChart.L - CurveChart.RightOf(d.WideRight);
        double cx = CurveChart.L + (d.T - d.T0) / Math.Max(1, d.T1 - d.T0) * iw;

        // 宽度：最长的一行
        double valW = 0;
        foreach (var r in d.Rows)
        {
            double w = 0;
            for (int i = 0; i < r.Values.Count; i++) w += 10 + Text(r.Values[i], Palette.T1Brush, FontWeight.SemiBold).Width + 6 + Text(d.Units[i], Palette.T3Brush).Width;
            valW = Math.Max(valW, w);
        }
        double rowW = 14 + 6 + 36 + valW;
        double contentW = Math.Max(rowW, Text(d.Head, Palette.T1Brush, FontWeight.SemiBold).Width);
        if (d.More != null) contentW = Math.Max(contentW, Text(d.More, Palette.T3Brush).Width);
        double w0 = Math.Max(180, contentW + 20);
        double h = 8 + 20 + 4 + 20 * d.Rows.Count + (d.More != null ? 22 : 0) + 8;
        double x = cx + 16 + w0 > Bounds.Width ? cx - w0 - 16 : cx + 16;
        x = Math.Min(Bounds.Width - w0 - 8, Math.Max(8, x));
        double y = 8;
        var box = new Rect(Math.Round(x) + 0.5, y + 0.5, Math.Round(w0), Math.Round(h));
        ctx.DrawRectangle(Palette.BgCardBrush, Palette.Pen(Palette.Line2Color, 1), new RoundedRect(box, 3), Palette.ShadowTip);
        double tx = box.X + 10, ty = box.Y + 8;
        Draw(ctx, d.Head, tx, ty + 2, Palette.T1Brush, Palette.T1Color, FontWeight.SemiBold);
        ty += 24;
        foreach (var r in d.Rows)
        {
            ctx.DrawLine(Palette.Pen(r.Color, 2.4, r.Dashed ? "1.25 0.83" : null), new Point(tx + 1, ty + 10), new Point(tx + 13, ty + 10));
            Draw(ctx, "RV" + r.N.ToString(CultureInfo.InvariantCulture), tx + 20, ty + 2, Palette.T3Brush, Palette.T3Color);
            // 样稿 .tr：flex 间距 6，读数另有左边距 4 → 名字 36 之后 10 · 读数 · 6 · 单位 · 10 · 读数 …
            double vx = tx + 20 + 36;
            for (int i = 0; i < r.Values.Count; i++)
            {
                vx += 10;
                vx += Draw(ctx, r.Values[i], vx, ty + 2, Palette.T1Brush, Palette.T1Color, FontWeight.SemiBold) + 6;
                vx += Draw(ctx, d.Units[i], vx, ty + 2, Palette.T3Brush, Palette.T3Color);
            }
            ty += 20;
        }
        if (d.More != null) Draw(ctx, d.More, tx, ty + 4, Palette.T3Brush, Palette.T3Color);
    }
}
