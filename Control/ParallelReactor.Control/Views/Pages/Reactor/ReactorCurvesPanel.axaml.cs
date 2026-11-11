using System;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Controls.Chart;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Chart;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 反应釜页的「曲线」面板（样稿 curvesPanel；视图模型 = <see cref="Pages.ReactorCurvesVm"/>）：标题栏的图例 + 10 / 30 / 60 min 分段、放大 / 关闭，内容是
/// <see cref="TimeSeriesChart"/>（Core 的 ReactorChartOptions：温度 / 压力分带、设定值虚线、会话标记、接近上限才有的上限线）。
/// <list type="bullet">
/// <item>图例按面板宽度取舍（样稿 @container rvcurve：≤ 720 去掉「标记」，≤ 640 去掉「设定」「目标」，≤ 500 整个图例不显示；Core 的 <see cref="ReactorChartOptions.Legend"/>）——
///   只看面板自己的宽度（父元素给的，R4）。</item>
/// <item>关掉面板（PrPanel :closed）时整块不显示（Animation 优先级，重新打开时撤掉：页面给这一块的 IsVisible 不被顶掉），页面版式让其余面板补位（同「釜体 / 属性查看器」）。</item>
/// <item>放大由 PageHost 处理（同其它 PrPanel）：放大后曲线按面板的新尺寸重排。</item>
/// </list>
/// </summary>
public partial class ReactorCurvesPanel : UserControl, IReactorPanel
{
    /// <summary>
    /// 页面版式算出来的面板宽度（小数，同样稿 CSS 网格的 640.28）：图例的阈值按它判断（样稿的容器查询比的是小数宽度 —— 1366 下 640.28 &gt; 640，设定值照样显示），
    /// 面板按整像素对齐之后的 640 不会误判。NaN（默认）= 按面板实际的宽度。
    /// </summary>
    public static readonly StyledProperty<double> LayoutWidthProperty = AvaloniaProperty.Register<ReactorCurvesPanel, double>(nameof(LayoutWidth), double.NaN);

    private IDisposable? _closedHide;
    private double _legendFor = -1;

    public ReactorCurvesPanel()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, e) => SyncLegend();
        Frame.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) MirrorClosed(); };
        SyncLegend();
    }

    public double LayoutWidth { get => GetValue(LayoutWidthProperty); set => SetValue(LayoutWidthProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutWidthProperty) SyncLegend();
    }

    /// <summary>里面的 PrPanel（页面的版式按它的放大 / 关闭排）。</summary>
    public PrPanel PanelControl => Frame;

    /// <summary>曲线（核对用）。</summary>
    public TimeSeriesChart ChartControl => Chart;

    /// <summary>10 / 30 / 60 分段（核对用）。</summary>
    public SegControl RangeControl => RangeSeg;

    /// <summary>图例里显示着的几项的文字（核对用）。</summary>
    public string LegendText
    {
        get
        {
            if (!Legend.IsVisible) return "";
            var parts = new System.Collections.Generic.List<string>();
            foreach (var c in Legend.Children)
                if (c is StackPanel { IsVisible: true } sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb) parts.Add(tb.Text ?? "");
            return string.Join(" ", parts);
        }
    }

    /// <summary>图例按面板宽度取舍（Core 的 Legend(宽)：标记 → 设定值 → 整个图例）。放大时按放大后的实际宽度。</summary>
    private void SyncLegend()
    {
        double actual = Frame.Bounds.Width;
        double width = !double.IsNaN(LayoutWidth) && !Frame.IsMaximized && Math.Abs(LayoutWidth - actual) < 1 ? LayoutWidth : actual;
        if (Math.Abs(width - _legendFor) < 0.001) return;
        _legendFor = width;
        var items = ReactorChartOptions.Legend(width > 0 ? width : 1e6);
        bool Has(string label)
        {
            foreach (var it in items) if (it.Label == label) return true;
            return false;
        }
        Legend.IsVisible = items.Count > 0;
        LgT.IsVisible = Has("温度");
        LgTsp.IsVisible = Has("设定");
        LgP.IsVisible = Has("压力");
        LgPsp.IsVisible = Has("目标");
        LgMk.IsVisible = Has("标记");
    }

    /// <summary>面板关掉了：这一块也不显示（Animation 优先级，重新打开时撤掉 —— 原来的值 / 绑定原样回来）。</summary>
    private void MirrorClosed()
    {
        bool hide = !Frame.IsVisible;
        if (hide && _closedHide == null) _closedHide = SetValue(IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation);
        else if (!hide && _closedHide != null)
        {
            _closedHide.Dispose();
            _closedHide = null;
        }
    }
}
