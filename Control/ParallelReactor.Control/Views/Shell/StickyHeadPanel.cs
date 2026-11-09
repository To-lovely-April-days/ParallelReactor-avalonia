using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 时间轴的一组（样稿 .pr-tl-group 里的 .pr-tl-ghead：position: sticky; top: 0）：子元素竖排，第一个子元素（组头）在本组范围内吸顶 ——
/// 滚到 16 路的后几行时仍看得到是哪台设备；滚出本组时跟着上去（不压到下一组）。组头画在行的上面（ZIndex 1），自己要有底色。
/// 吸顶只用 RenderTransform（滚动时不重新排版）。
/// </summary>
public sealed class StickyHeadPanel : Panel
{
    private ScrollViewer? _sv;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sv = this.FindAncestorOfType<ScrollViewer>();
        if (_sv != null) _sv.ScrollChanged += OnScroll;
        LayoutUpdated += OnLayoutUpdated;
    }

    // 排版之后（自己的位置已经更新）再算一次：组展开 / 收起、上面的组变高时组头位置跟着变
    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateSticky();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_sv != null) _sv.ScrollChanged -= OnScroll;
        LayoutUpdated -= OnLayoutUpdated;
        _sv = null;
    }

    private void OnScroll(object? sender, ScrollChangedEventArgs e) => UpdateSticky();

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            w = Math.Max(w, c.DesiredSize.Width);
            h += c.DesiredSize.Height;
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var c in Children)
        {
            c.Arrange(new Rect(0, y, finalSize.Width, c.DesiredSize.Height));
            y += c.DesiredSize.Height;
        }
        if (Children.Count > 0) Children[0].ZIndex = 1;
        _shift = -1;   // 尺寸变了：裁剪框要按新的宽高重算（LayoutUpdated 里）
        return finalSize;
    }

    private void UpdateSticky()
    {
        if (Children.Count == 0 || _sv == null) return;
        var head = Children[0];
        var p = this.TranslatePoint(default, _sv);
        if (p == null) return;
        double total = Bounds.Height;
        double shift = Math.Clamp(-p.Value.Y, 0, Math.Max(0, total - head.DesiredSize.Height));
        if (shift == _shift) return;
        _shift = shift;
        var cur = head.RenderTransform as TranslateTransform;
        if (shift <= 0) { if (cur != null) head.RenderTransform = null; }
        else if (cur == null) head.RenderTransform = new TranslateTransform(0, shift);
        else cur.Y = shift;
        // 组头盖住的那一截行裁掉（不画、截图审计也不再当成重叠：同网页 sticky 元素下面的内容被它遮住）
        for (int i = 1; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Clip = shift <= 0 ? null : new RectangleGeometry(new Rect(0, shift, c.Bounds.Width, Math.Max(0, c.Bounds.Height - shift)));
        }
    }

    private double _shift;
}
