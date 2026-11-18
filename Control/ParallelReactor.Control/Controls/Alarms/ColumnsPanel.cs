using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Controls.Alarms;

/// <summary>
/// 表格的一行（表头和每一行用同一份列宽，融合版 F5 的报警表 / 曲线读数表）：<see cref="Columns"/> = 「96,56,70,76,2*,1*,160,76」——
/// 数字 = 固定宽，「n*」= 按比例分剩下的宽。看不见的子元素（例如只在「全部设备」时才有的「设备」列）那一列宽为 0、不占位。
/// 第 i 个子元素放在第 i 列；行高 = 最高的子元素。
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly StyledProperty<string> ColumnsProperty = AvaloniaProperty.Register<ColumnsPanel, string>(nameof(Columns), "");

    public string Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    static ColumnsPanel() => AffectsMeasure<ColumnsPanel>(ColumnsProperty);

    private (double W, bool Star)[] Defs()
    {
        var parts = (Columns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Select(p => p.EndsWith('*')
            ? (p.Length == 1 ? 1 : double.Parse(p[..^1], CultureInfo.InvariantCulture), true)
            : (double.Parse(p, CultureInfo.InvariantCulture), false)).ToArray();
    }

    /// <summary>各列的宽（给定总宽）。</summary>
    public double[] Widths(double total)
    {
        var defs = Defs();
        var w = new double[defs.Length];
        double fixedW = 0, stars = 0;
        for (int i = 0; i < defs.Length; i++)
        {
            bool vis = i < Children.Count && Children[i].IsVisible;
            if (!vis) continue;
            if (defs[i].Star) stars += defs[i].W; else fixedW += defs[i].W;
        }
        double rest = Math.Max(0, (double.IsFinite(total) ? total : fixedW) - fixedW);
        for (int i = 0; i < defs.Length; i++)
        {
            bool vis = i < Children.Count && Children[i].IsVisible;
            w[i] = !vis ? 0 : defs[i].Star ? (stars > 0 ? rest * defs[i].W / stars : 0) : defs[i].W;
        }
        return w;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = Widths(availableSize.Width);
        double h = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            double cw = i < w.Length ? w[i] : 0;
            c.Measure(new Size(cw, availableSize.Height));
            if (c.IsVisible) h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : w.Sum(), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = Widths(finalSize.Width);
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double cw = i < w.Length ? w[i] : 0;
            Children[i].Arrange(new Rect(x, 0, cw, finalSize.Height));
            x += cw;
        }
        return finalSize;
    }
}
