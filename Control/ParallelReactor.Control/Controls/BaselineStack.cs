using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// 横排、按第一行文字基线对齐（CSS <c>display: flex; align-items: baseline</c>）：标题 16/600 + 说明 14、说明 14 + 读数 26 这类混排。
/// <see cref="FirstWidth"/>：第一个子元素的固定宽度（NaN = 按内容，CSS grid 的「260px 1fr」）；最后一个子元素拿剩余宽度（可省略号）。
/// 非 TextBlock 的子元素按底边对齐。
/// </summary>
public sealed class BaselineStack : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<BaselineStack, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<double> FirstWidthProperty = AvaloniaProperty.Register<BaselineStack, double>(nameof(FirstWidth), double.NaN);

    /// <summary>这个子元素（和它后面的）靠右（CSS justify-content: space-between / margin-left: auto）；基线照样对齐。</summary>
    public static readonly AttachedProperty<bool> PushRightProperty = AvaloniaProperty.RegisterAttached<BaselineStack, Layoutable, bool>("PushRight");

    public static bool GetPushRight(Layoutable c) => c.GetValue(PushRightProperty);
    public static void SetPushRight(Layoutable c, bool v) => c.SetValue(PushRightProperty, v);

    static BaselineStack()
    {
        AffectsMeasure<BaselineStack>(SpacingProperty, FirstWidthProperty);
        AffectsParentArrange<BaselineStack>(PushRightProperty);
    }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double FirstWidth { get => GetValue(FirstWidthProperty); set => SetValue(FirstWidthProperty, value); }

    private double _above;

    /// <summary>子元素（含外边距）顶边到第一行基线的距离。</summary>
    private static double BaselineOf(Layoutable c)
    {
        if (c is TextBlock tb)
        {
            var lines = tb.TextLayout.TextLines;
            if (lines.Count > 0) return tb.Margin.Top + tb.Padding.Top + lines[0].Baseline;
        }
        return c.DesiredSize.Height;
    }

    private double SlotWidth(int i, int n, Size size, double x)
    {
        if (i == 0 && !double.IsNaN(FirstWidth)) return FirstWidth;
        if (i == n - 1 && !double.IsInfinity(size.Width) && !GetPushRight(Children[i])) return Math.Max(0, size.Width - x);
        return double.PositiveInfinity;
    }

    /// <summary>从第 i 个子元素起（含）到最后的宽度（含间距）：靠右的子元素从这里排起。</summary>
    private double TailWidth(int i)
    {
        double w = 0;
        int k = 0;
        for (int j = i; j < Children.Count; j++)
        {
            var c = Children[j];
            if (!c.IsVisible) continue;
            if (k++ > 0) w += Spacing;
            w += c.DesiredSize.Width;
        }
        return w;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, above = 0, below = 0;
        int n = Children.Count, k = 0;
        for (int i = 0; i < n; i++)
        {
            var c = Children[i];
            if (!c.IsVisible) continue;
            if (k++ > 0) x += Spacing;
            var w = SlotWidth(i, n, availableSize, x);
            c.Measure(new Size(w, double.PositiveInfinity));
            var b = BaselineOf(c);
            above = Math.Max(above, b);
            below = Math.Max(below, c.DesiredSize.Height - b);
            // 最后一个子元素：排版时拿剩余宽度（可省略），但期望宽度只报它自己的宽度（放进 WrapPanel / 靠左对齐时不占满整行）
            bool fixedFirst = i == 0 && !double.IsNaN(FirstWidth);
            x += double.IsInfinity(w) ? c.DesiredSize.Width : i == n - 1 && !fixedFirst ? Math.Min(w, c.DesiredSize.Width) : w;
        }
        _above = above;
        return new Size(double.IsInfinity(availableSize.Width) ? x : Math.Min(x, availableSize.Width), above + below);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        int n = Children.Count, k = 0;
        for (int i = 0; i < n; i++)
        {
            var c = Children[i];
            if (!c.IsVisible) continue;
            if (k++ > 0) x += Spacing;
            if (GetPushRight(c) && !double.IsInfinity(finalSize.Width)) x = Math.Max(x, finalSize.Width - TailWidth(i));
            var w = SlotWidth(i, n, finalSize, x);
            if (double.IsInfinity(w)) w = c.DesiredSize.Width;
            c.Arrange(new Rect(x, _above - BaselineOf(c), w, c.DesiredSize.Height));
            x += w;
        }
        return finalSize;
    }
}
