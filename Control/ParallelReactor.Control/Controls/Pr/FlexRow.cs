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
/// <see cref="GetFitOpt"/> 的子元素「放得下才显示」（样稿 .pr-fitopt）：整行放不下时整个让出去（不画、不收缩成「H…」），剩下的再按权重收缩。
/// </summary>
public class FlexRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FlexRow, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<bool> StretchItemsProperty = AvaloniaProperty.Register<FlexRow, bool>(nameof(StretchItems));
    public static readonly AttachedProperty<double> ShrinkProperty = AvaloniaProperty.RegisterAttached<FlexRow, Layoutable, double>("Shrink");
    public static readonly AttachedProperty<bool> PushRightProperty = AvaloniaProperty.RegisterAttached<FlexRow, Layoutable, bool>("PushRight");
    public static readonly AttachedProperty<bool> FitOptProperty = AvaloniaProperty.RegisterAttached<FlexRow, Layoutable, bool>("FitOpt");

    static FlexRow()
    {
        AffectsMeasure<FlexRow>(SpacingProperty, StretchItemsProperty);
        AffectsParentMeasure<FlexRow>(ShrinkProperty, PushRightProperty, FitOptProperty);
    }

    /// <summary>子元素间距（CSS gap）。</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    /// <summary>true = 子元素纵向拉满（align-items: stretch）；默认纵向居中。</summary>
    public bool StretchItems { get => GetValue(StretchItemsProperty); set => SetValue(StretchItemsProperty, value); }

    public static double GetShrink(Layoutable c) => c.GetValue(ShrinkProperty);
    public static void SetShrink(Layoutable c, double v) => c.SetValue(ShrinkProperty, v);
    public static bool GetPushRight(Layoutable c) => c.GetValue(PushRightProperty);
    public static void SetPushRight(Layoutable c, bool v) => c.SetValue(PushRightProperty, v);
    public static bool GetFitOpt(Layoutable c) => c.GetValue(FitOptProperty);
    public static void SetFitOpt(Layoutable c, bool v) => c.SetValue(FitOptProperty, v);

    private readonly List<double> _widths = new();
    private readonly HashSet<Layoutable> _dropped = new();
    /// <summary>上一遍排成 0 × 0 的子元素（再显示时里面的文字要重新排，见 Controls/TextRelayout）。</summary>
    private readonly HashSet<Layoutable> _zeroed = new();

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

        // 放得下才显示（FitOpt）：整行放不下时这些子元素整个让出去
        _dropped.Clear();
        if (!double.IsInfinity(availableSize.Width) && total > availableSize.Width + 0.01)
            for (int i = vis.Count - 1; i >= 0; i--)
            {
                if (!GetFitOpt(vis[i])) continue;
                _dropped.Add(vis[i]);
                total -= _widths[i] + (vis.Count > 1 ? Spacing : 0);
                vis.RemoveAt(i);
                _widths.RemoveAt(i);
            }

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
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            if (_dropped.Contains(c)) { c.Arrange(default); _zeroed.Add(c); continue; }
            // 让出去又放得下的可选项（例如资源管理器设备头的型号）：按 0 宽排过的文字重新排
            if (_zeroed.Remove(c)) TextRelayout.Revive(c);
            vis.Add(c);
        }
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
        if (_zeroed.Count > 0) _zeroed.RemoveWhere(c => c is not Avalonia.Controls.Control cc || !Children.Contains(cc));
        return finalSize;
    }
}
