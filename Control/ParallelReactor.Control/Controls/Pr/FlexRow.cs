using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 一行 flex（CSS <c>display: flex; align-items: center; gap; flex-shrink; margin-left: auto</c>）：子元素按自然宽度从左排，
/// 放不下时按 <see cref="GetShrink"/> 的权重 × 自然宽度收缩（同 CSS flex-shrink；权重 0 = 不缩），缩到 MinWidth 为止；
/// <see cref="GetPushRight"/> 的子元素及其后面的子元素靠右（margin-left: auto）。
/// 例：面板标题栏「标题（权重 1）· 副标题（权重 100）· 操作（靠右）」—— 副标题先让位，标题最后才省略。
/// </summary>
public class FlexRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FlexRow, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<bool> StretchItemsProperty = AvaloniaProperty.Register<FlexRow, bool>(nameof(StretchItems));
    public static readonly AttachedProperty<double> ShrinkProperty = AvaloniaProperty.RegisterAttached<FlexRow, Layoutable, double>("Shrink");
    public static readonly AttachedProperty<bool> PushRightProperty = AvaloniaProperty.RegisterAttached<FlexRow, Layoutable, bool>("PushRight");

    static FlexRow()
    {
        AffectsMeasure<FlexRow>(SpacingProperty, StretchItemsProperty);
        AffectsParentMeasure<FlexRow>(ShrinkProperty, PushRightProperty);
    }

    /// <summary>子元素间距（CSS gap）。</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    /// <summary>true = 子元素纵向拉满（align-items: stretch）；默认纵向居中。</summary>
    public bool StretchItems { get => GetValue(StretchItemsProperty); set => SetValue(StretchItemsProperty, value); }

    public static double GetShrink(Layoutable c) => c.GetValue(ShrinkProperty);
    public static void SetShrink(Layoutable c, double v) => c.SetValue(ShrinkProperty, v);
    public static bool GetPushRight(Layoutable c) => c.GetValue(PushRightProperty);
    public static void SetPushRight(Layoutable c, bool v) => c.SetValue(PushRightProperty, v);

    private readonly List<double> _widths = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _widths.Clear();
        var vis = new List<Layoutable>();
        double total = 0, h = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            vis.Add(c);
            _widths.Add(c.DesiredSize.Width);
            total += c.DesiredSize.Width;
        }
        if (vis.Count > 1) total += (vis.Count - 1) * Spacing;

        // 收缩：按 权重 × 自然宽度 分摊不足的宽度，缩到 MinWidth 的冻结后再分（CSS flex-shrink 的算法）
        var deficit = double.IsInfinity(availableSize.Width) ? 0 : total - availableSize.Width;
        var frozen = new bool[vis.Count];
        for (int round = 0; deficit > 0.01 && round < vis.Count + 1; round++)
        {
            double sum = 0;
            for (int i = 0; i < vis.Count; i++)
                if (!frozen[i]) sum += GetShrink(vis[i]) * _widths[i];
            if (sum <= 0) break;
            double used = 0;
            for (int i = 0; i < vis.Count; i++)
            {
                if (frozen[i]) continue;
                var w = GetShrink(vis[i]) * _widths[i];
                if (w <= 0) continue;
                var cut = deficit * w / sum;
                var min = Math.Max(0, vis[i].MinWidth);
                if (_widths[i] - cut <= min) { cut = _widths[i] - min; frozen[i] = true; }
                _widths[i] -= cut;
                used += cut;
            }
            deficit -= used;
        }

        for (int i = 0; i < vis.Count; i++)
        {
            if (_widths[i] < vis[i].DesiredSize.Width - 0.01) vis[i].Measure(new Size(_widths[i], availableSize.Height));
            h = Math.Max(h, vis[i].DesiredSize.Height);
        }
        double width = 0;
        foreach (var w in _widths) width += w;
        if (vis.Count > 1) width += (vis.Count - 1) * Spacing;
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var vis = new List<Layoutable>();
        foreach (var c in Children) if (c.IsVisible) vis.Add(c);
        double content = 0;
        for (int i = 0; i < vis.Count && i < _widths.Count; i++) content += _widths[i];
        if (vis.Count > 1) content += (vis.Count - 1) * Spacing;
        var free = Math.Max(0, finalSize.Width - content);
        double x = 0;
        bool pushed = false;
        for (int i = 0; i < vis.Count && i < _widths.Count; i++)
        {
            var c = vis[i];
            if (!pushed && GetPushRight(c)) { x += free; pushed = true; }
            var w = _widths[i];
            var hh = StretchItems ? finalSize.Height : Math.Min(c.DesiredSize.Height, finalSize.Height);
            c.Arrange(new Rect(x, (finalSize.Height - hh) / 2, w, hh));
            x += w + Spacing;
        }
        return finalSize;
    }
}
