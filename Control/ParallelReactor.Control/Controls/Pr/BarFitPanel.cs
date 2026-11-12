using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 「进度条 + 剩余时间」一行（样稿 ui.fitStepBars 的 is-barfit，设备页 .device-pline.is-bar / 总览 .overview-prog）：
/// 第 1 个子元素是进度条（<see cref="StepBar"/> 按段数算最小宽度，其它控件用 MinWidth），第 2 个是后面的文字。
/// 文字放得下时：进度条拿剩余宽度、文字按自身宽度靠右；进度条会窄于最小宽度（每段 &lt; 6 px）时：这一行的文字整个不显示，
/// 进度条占满整行，完整文字放进这一行的悬停提示（<see cref="IsBarFit"/> = true，伪类 :barfit）。
/// 判断只看父元素给的宽度和文字的自然宽度，不会来回振荡；隐藏的文字排到行外并被裁掉（不改 IsVisible，自然宽度一直量得到）。
/// 文字藏起来期间提示绑定在文字上（剩余时间变了提示也跟着变）。
/// </summary>
public sealed class BarFitPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<BarFitPanel, double>(nameof(Spacing), 10);
    public static readonly DirectProperty<BarFitPanel, bool> IsBarFitProperty =
        AvaloniaProperty.RegisterDirect<BarFitPanel, bool>(nameof(IsBarFit), o => o.IsBarFit);

    static BarFitPanel() => AffectsMeasure<BarFitPanel>(SpacingProperty);

    public BarFitPanel() => ClipToBounds = true;

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private bool _barFit;
    /// <summary>文字放不下、已隐藏（进度条占满整行）。</summary>
    public bool IsBarFit { get => _barFit; private set { if (SetAndRaise(IsBarFitProperty, ref _barFit, value)) PseudoClasses.Set(":barfit", value); } }

    // 文字被藏起来时，整行的提示跟着文字走（剩余时间每秒在变）：以 Animation 优先级绑定，放得下时撤销，原来的提示原样回来
    private IDisposable? _tipBinding;
    private Avalonia.Controls.Control? _tipSource;

    private static double MinOf(Layoutable bar) => bar is StepBar sb ? Math.Max(sb.MinBarWidthSelf, sb.MinWidth) : bar.MinWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return base.MeasureOverride(availableSize);
        var bar = Children[0];
        var text = Children[1];
        text.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var tw = text.DesiredSize.Width;
        bool fit = !double.IsInfinity(availableSize.Width) && availableSize.Width - tw - Spacing < MinOf(bar);
        IsBarFit = fit;
        var barW = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - (fit ? 0 : tw + Spacing));
        bar.Measure(new Size(barW, availableSize.Height));
        var h = Math.Max(bar.DesiredSize.Height, fit ? 0 : text.DesiredSize.Height);
        var w = double.IsInfinity(availableSize.Width) ? bar.DesiredSize.Width + Spacing + tw : availableSize.Width;
        UpdateTip(fit, text);
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return base.ArrangeOverride(finalSize);
        var bar = Children[0];
        var text = Children[1];
        var tw = text.DesiredSize.Width;
        if (IsBarFit)
        {
            bar.Arrange(new Rect(0, (finalSize.Height - bar.DesiredSize.Height) / 2, finalSize.Width, bar.DesiredSize.Height));
            text.Arrange(new Rect(finalSize.Width + Spacing, 0, tw, text.DesiredSize.Height));   // 行外（被裁掉）
        }
        else
        {
            var barW = Math.Max(0, finalSize.Width - tw - Spacing);
            bar.Arrange(new Rect(0, (finalSize.Height - bar.DesiredSize.Height) / 2, barW, bar.DesiredSize.Height));
            text.Arrange(new Rect(finalSize.Width - tw, (finalSize.Height - text.DesiredSize.Height) / 2, tw, text.DesiredSize.Height));
        }
        return finalSize;
    }

    private void UpdateTip(bool fit, Avalonia.Controls.Control text)
    {
        if (fit && (_tipBinding == null || !ReferenceEquals(_tipSource, text)))
        {
            _tipBinding?.Dispose();
            _tipSource = text;
            // TextBlock：提示 = 它的 Text（实时）；其它控件：提示 = 它自己的提示
            var src = text is TextBlock ? text.GetObservable(TextBlock.TextProperty) : text.GetObservable(ToolTip.TipProperty);
            _tipBinding = this.Bind(ToolTip.TipProperty, src, BindingPriority.Animation);
        }
        else if (!fit && _tipBinding != null)
        {
            _tipBinding.Dispose();
            _tipBinding = null;
            _tipSource = null;
        }
    }
}
