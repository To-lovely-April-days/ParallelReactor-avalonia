using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 反应釜页的「程序」面板（样稿 progPanel；视图模型 = <see cref="ReactorProgramVm"/>）。
/// <list type="bullet">
/// <item>面包屑（<see cref="ReactorCrumb"/>）：面板 ≤ 360 时不写别名段（样稿 @container rvprog (max-width: 360px)）。</item>
/// <item>状态行：面板 ≥ 460 写全文「运行中 · 第 3/4 步 · 预计 11:09 结束」，否则短的「预计 11:09 结束」（样稿 .reactor-ps-l / -s），悬停都是全文。</item>
/// <item>画布：卡片按滚动区的外宽排（<see cref="WidthProbe"/> → StepFlowPanel.OuterWidth，同一遍排版里生效），视图模型按它判断放大档能不能用。</item>
/// <item>自动滚动（样稿 autoScroll）：设备 / 反应釜 / 程序 / 当前步 / 状态 / 每行张数 / 放大档变了才滚，当前步（或中止的那一步）不在可见范围时把它滚到顶部
///   （条件框只差一点放不下时让条件框整个露出来，Core 的 StepFlowLayout.ScrollTo）；「定位当前步」/ 放大 / 缩小 / 适应时总是滚。</item>
/// <item>关掉面板（PrPanel :closed）时整块不显示（Animation 优先级，重新打开时撤掉），页面版式让其余面板补位（同「釜体 / 属性查看器」与「曲线」）。</item>
/// </list>
/// </summary>
public partial class ReactorProgramPanel : UserControl, IReactorPanel
{
    /// <summary>面包屑不写别名段（≤）、状态行写全文（≥）、预览标题右边换到下一行（≤）的面板宽度（样稿 rvprog 的容器查询）。</summary>
    public const double NoDevMax = 360, LongStatusMin = ProgramStatusLine.LongMinWidth, PreviewWrapMax = 440;

    /// <summary>页面版式算出来的面板宽度（小数，同样稿 CSS 网格的 360.72）：容器查询的阈值按它判断；NaN = 按面板实际的宽度。</summary>
    public static readonly StyledProperty<double> LayoutWidthProperty = AvaloniaProperty.Register<ReactorProgramPanel, double>(nameof(LayoutWidth), double.NaN);

    private ReactorProgramVm? _vm;
    private IDisposable? _closedHide;
    private string? _scrollKey;
    private bool _scrollQueued, _forceScroll, _long = true;

    public ReactorProgramPanel()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, _) => SyncWidthClasses();
        Frame.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) MirrorClosed(); };
        Probe.WidthChanged += OnCanvasWidth;
        Flow.Arranged += (_, _) => QueueScroll(false);
        SyncWidthClasses();
    }

    public double LayoutWidth { get => GetValue(LayoutWidthProperty); set => SetValue(LayoutWidthProperty, value); }

    /// <summary>里面的 PrPanel（页面的版式按它的放大 / 关闭排）。</summary>
    public PrPanel PanelControl => Frame;

    /// <summary>步骤卡片画布（核对用）。</summary>
    public StepFlowPanel FlowControl => Flow;

    /// <summary>程序库的预览画布（核对用）。</summary>
    public StepFlowPanel PreviewControl => PreviewFlow;

    /// <summary>画布的滚动区（核对用）。</summary>
    public ScrollViewer ScrollControl => Scroll;

    /// <summary>面包屑（核对用）。</summary>
    public ReactorCrumb CrumbControl => Crumb;

    /// <summary>状态行此刻显示的文字（核对用）。</summary>
    public string StatusText => (SWord.Text ?? "") + (SSep.Text ?? "") + (SText.Text ?? "");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutWidthProperty) SyncWidthClasses();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVm;
            _vm.LocateRequested -= OnLocate;
            _vm.ViewReset -= OnViewReset;
        }
        _vm = DataContext as ReactorProgramVm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVm;
            _vm.LocateRequested += OnLocate;
            _vm.ViewReset += OnViewReset;
            if (Probe.LastWidth > 0) _vm.SetCanvasWidth(Probe.LastWidth);
        }
        _scrollKey = null;
        SyncStatus();
        SyncName();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReactorProgramVm.StatusWord) or nameof(ReactorProgramVm.StatusFull) or nameof(ReactorProgramVm.StatusShortWord)
                or nameof(ReactorProgramVm.StatusShortText) or nameof(ReactorProgramVm.HasStatus) or nameof(ReactorProgramVm.StatusKey):
                SyncStatus();
                break;
            case nameof(ReactorProgramVm.PanelName):
                SyncName();
                break;
            case nameof(ReactorProgramVm.ScrollKey):
                // 像新打开的页面（视图模型的 ResetView 把键清空）：自动滚动重来
                if (_vm?.ScrollKey.Length == 0) _scrollKey = null;
                QueueScroll(false);
                break;
        }
    }

    private void SyncName() => Avalonia.Automation.AutomationProperties.SetName(Frame, _vm?.PanelName ?? ProgramPanelText.PanelName(null));

    /// <summary>容器查询（样稿 rvprog）：按页面版式给的小数宽度（没有时按实际宽度）。</summary>
    private void SyncWidthClasses()
    {
        double actual = Frame.Bounds.Width;
        double w = !double.IsNaN(LayoutWidth) && !Frame.IsMaximized && Math.Abs(LayoutWidth - actual) < 1 ? LayoutWidth : actual;
        if (w <= 0) w = 1e6;
        Crumb.NoDev = w <= NoDevMax;
        bool wrap = w <= PreviewWrapMax;
        PrevWide.IsVisible = !wrap;
        PrevNarrow.IsVisible = wrap;
        bool lng = w >= LongStatusMin;
        if (lng != _long)
        {
            _long = lng;
            SyncStatus();
        }
    }

    /// <summary>状态行：宽画布「运行中 · 第 3/4 步 · 预计 11:09 结束」，窄画布「预计 11:09 结束」/「报警暂停 · 2/4」（状态字 600 t-1、分隔 t-3、其余 t-2）。</summary>
    private void SyncStatus()
    {
        var vm = _vm;
        string word, rest;
        if (vm is null || !vm.HasStatus) { word = ""; rest = ""; }
        else if (_long) { word = vm.StatusWord; rest = vm.StatusFull; }
        else { word = vm.StatusShortWord; rest = vm.StatusShortText; }
        SWord.Text = word;
        SWord.IsVisible = word.Length > 0;
        SSep.Text = word.Length > 0 && rest.Length > 0 ? " · " : "";
        SSep.IsVisible = SSep.Text.Length > 0;
        SText.Text = rest;
        SText.IsVisible = rest.Length > 0;
        // 点占 11 px（9 + 左右各 1），已结束的 ✓ 占 13 px（样稿 ui.keyDot('done') = 13 px 的 .pr-dot-done，没有外边距）
        SDot.Slot = vm is { StatusKey: StateKey.Done } ? 13 : 11;
    }

    /// <summary>滚动区的外宽（含滚动条）：画布按它排；视图模型按它判断放大档；对不上台账时的安全联锁框最宽 300。</summary>
    private void OnCanvasWidth(double w)
    {
        Flow.OuterWidth = w;
        PreviewFlow.OuterWidth = w;
        DegradedSafety.ApplyWidth(Math.Max(StepFlowLayout.StepMin, Math.Min(StepFlowLayout.StepOneMax, w - StepFlowLayout.ScrollW - 32)));
        _vm?.SetCanvasWidth(w);
    }

    private void OnLocate(object? sender, bool force) => QueueScroll(force);

    /// <summary>像新打开的页面：画布滚回顶部，自动滚动按当前步重新判断（同一个视图换场景时不留上一次的滚动位置）。</summary>
    private void OnViewReset(object? sender, EventArgs e)
    {
        Scroll.Offset = default;
        _scrollKey = null;
        QueueScroll(false);
    }

    /// <summary>排完之后再滚（排版过程中改滚动位置会再排一遍）。</summary>
    private void QueueScroll(bool force)
    {
        _forceScroll |= force;
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(DoScroll, DispatcherPriority.Background);
    }

    private void DoScroll()
    {
        _scrollQueued = false;
        bool force = _forceScroll;
        _forceScroll = false;
        var vm = _vm;
        var g = Flow.Geometry;
        if (vm is null || g is null || !Flow.IsEffectivelyVisible || !vm.HasProgram || vm.Degraded) return;
        // 键 = 样稿 scrollKey：设备 / 反应釜 / 程序 / 当前步 / 状态 + 每行张数 / 放大档
        string key = vm.ScrollKey + "/" + Flow.Fit.Per + "/" + Flow.Fit.Zoom;
        if (!force && key == _scrollKey) return;
        _scrollKey = key;
        int idx = vm.CurIndex;
        if (idx < 0)
        {
            if (force) Scroll.Offset = new Vector(Scroll.Offset.X, 0);
            return;
        }
        var r = StepFlowLayout.ScrollTo(g, idx, Scroll.Viewport.Height, Scroll.Offset.Y, force);
        if (r.Top is double top) Scroll.Offset = new Vector(Scroll.Offset.X, top);
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

/// <summary>反应釜页网格里的一块面板（程序 / 釜体 / 属性查看器 / 曲线 / 未安装）：版式按它的 PrPanel 的放大 / 关闭排。</summary>
public interface IReactorPanel
{
    PrPanel PanelControl { get; }
}
