using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 键值表（样稿 ui.kv / .pr-kv：grid「max-content 1fr」、行距 8、列距 16）：子元素依次是 键、值、键、值 …
/// 键列宽 = 最宽的键；值列拿剩余宽度。TextBlock 子元素的默认样式（模板优先级，局部值仍可覆盖）：
/// 键 14 px t-3、值 15 px t-2，行高都是 22。
/// </summary>
public sealed class Kv : Panel
{
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<Kv, double>(nameof(RowSpacing), 8);
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<Kv, double>(nameof(ColumnSpacing), 16);

    static Kv() => AffectsMeasure<Kv>(RowSpacingProperty, ColumnSpacingProperty);

    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not TextBlock tb) continue;
            bool key = i % 2 == 0;
            tb.SetValue(TextBlock.FontSizeProperty, key ? 14d : 15d, BindingPriority.Template);
            tb.SetValue(TextBlock.ForegroundProperty, key ? Palette.T3Brush : Palette.T2Brush, BindingPriority.Template);
            tb.SetValue(TextBlock.LineHeightProperty, 22d, BindingPriority.Template);
        }
    }

    private double _keyW;

    protected override Size MeasureOverride(Size availableSize)
    {
        _keyW = 0;
        for (int i = 0; i < Children.Count; i += 2)
        {
            Children[i].Measure(Size.Infinity);
            _keyW = Math.Max(_keyW, Children[i].DesiredSize.Width);
        }
        var valW = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - _keyW - ColumnSpacing);
        double h = 0, maxVal = 0;
        int rows = 0;
        for (int i = 0; i < Children.Count; i += 2)
        {
            double rh = Children[i].DesiredSize.Height;
            if (i + 1 < Children.Count)
            {
                Children[i + 1].Measure(new Size(valW, double.PositiveInfinity));
                rh = Math.Max(rh, Children[i + 1].DesiredSize.Height);
                maxVal = Math.Max(maxVal, Children[i + 1].DesiredSize.Width);
            }
            h += rh;
            rows++;
        }
        if (rows > 1) h += (rows - 1) * RowSpacing;
        var w = double.IsInfinity(availableSize.Width) ? _keyW + ColumnSpacing + maxVal : availableSize.Width;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var valX = _keyW + ColumnSpacing;
        for (int i = 0; i < Children.Count; i += 2)
        {
            var k = Children[i];
            double rh = k.DesiredSize.Height;
            if (i + 1 < Children.Count) rh = Math.Max(rh, Children[i + 1].DesiredSize.Height);
            k.Arrange(new Rect(0, y, _keyW, k.DesiredSize.Height));
            if (i + 1 < Children.Count) Children[i + 1].Arrange(new Rect(valX, y, Math.Max(0, finalSize.Width - valX), Children[i + 1].DesiredSize.Height));
            y += rh + RowSpacing;
        }
        return finalSize;
    }
}

/// <summary>
/// 分区小标题（样稿 ui.subhead / .pr-subhead）：16 px 600 t-1，最小高 24，右侧可放内容（靠右，400）。
/// </summary>
public sealed class Subhead : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Subhead, string?>(nameof(Title));
    public static readonly StyledProperty<object?> RightProperty = AvaloniaProperty.Register<Subhead, object?>(nameof(Right));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public object? Right { get => GetValue(RightProperty); set => SetValue(RightProperty, value); }
}
