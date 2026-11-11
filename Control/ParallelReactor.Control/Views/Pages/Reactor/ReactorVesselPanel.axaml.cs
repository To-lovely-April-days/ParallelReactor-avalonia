using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>面板里放什么：页签面板（「釜体 / 属性查看器」，支持的屏幕上都是它）/ 只放釜体 / 只放属性查看器（宽版式，工作区 ≥ 1800 的两块面板）。</summary>
public enum VesselPanelMode { Tabs, Vessel, Props }

/// <summary>
/// 反应釜页的「釜体 / 属性查看器」面板（样稿 tabsPanel；宽版式的 vesselPanel / propsPanel 用 <see cref="Mode"/>）。视图模型 = <see cref="ReactorVesselVm"/>。
/// <list type="bullet">
/// <item>标题栏：<see cref="VesselPanelMode.Tabs"/> = 页签「釜体 / 属性查看器」（PTabs，← → Home End 切换；切换只换内容 —— 同一个 PrPanel，放大 / 关闭和页签上的焦点都保留）；
///   <see cref="VesselPanelMode.Vessel"/> =「釜体」+ 烧瓶图标 + 副标题「50 mL 釜」；<see cref="VesselPanelMode.Props"/> =「属性查看器」+ 树图标。</item>
/// <item>右侧：釜体 = 状态（点 + 字；有错误报警时不写）；属性查看器 =「另有 N 项 ▾」+「恢复全部属性」。面板窄于 420 时抓手和状态让出去（样稿 @container rvves (max-width: 419px)）。</item>
/// <item>内容：<see cref="VesselStage"/>（按这一块的尺寸排标注）/ 属性卡片网格（<see cref="PropGridPanel"/>）；卡片的 × 隐藏这一张，换页签后滚动回到顶部。
///   内容区的尺寸交给视图模型（<see cref="ReactorVesselVm.SetPropsViewport"/>：只显示完整的行）—— 只由放属性卡片的那一块报（宽版式的「釜体」面板不报）。</item>
/// </list>
/// 页面把它放进自己的版式（反应釜页的网格），放大由 PageHost 处理（同其它 PrPanel）。
/// </summary>
public partial class ReactorVesselPanel : UserControl, IReactorPanel
{
    /// <summary>面板窄于它时，标题栏去掉抓手和状态（样稿 rvves max-width: 419px）。</summary>
    public const double NarrowHeadWidth = 420;

    public static readonly StyledProperty<VesselPanelMode> ModeProperty = AvaloniaProperty.Register<ReactorVesselPanel, VesselPanelMode>(nameof(Mode));

    public VesselPanelMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    private ReactorVesselVm? _vm;
    private bool _narrow;

    public ReactorVesselPanel()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, e) => SetNarrow(e.NewSize.Width < NarrowHeadWidth);
        // 关掉面板（PrPanel :closed 不显示）时整块一起不显示：页面的版式按显示着的块排（其余面板补位，样稿 reactor.css 的 :has(.is-closed)）。
        // 以 Animation 优先级压成 false、重新打开时撤掉（同 PageHost 藏兄弟元素的做法）：页面自己给这一块的 IsVisible（绑定 / 本地值）不会被顶掉
        Frame.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) MirrorClosed(); };
        Body.SizeChanged += (_, e) => ReportViewport(e.NewSize);
        AddHandler(PropCard.CloseRequestedEvent, OnPropClose, RoutingStrategies.Bubble);
        Sync();
    }

    /// <summary>
    /// 内容区的尺寸交给视图模型（属性卡片的列数、放得下几张、「另有 N 项」）：只由放属性卡片的那一块报 —— 页签面板（哪个页签都报，换页签时尺寸已经对）、
    /// 宽版式的「属性查看器」。宽版式的「釜体」面板与它共用一个视图模型，不报：否则谁后改尺寸谁说了算，关掉 / 打开「程序」后列数按釜体面板算，
    /// 与属性网格（PropGridPanel 按自己的宽度）对不上（复核）。藏起来的面板不排版、不报。
    /// </summary>
    private void ReportViewport(Size size)
    {
        if (Mode != VesselPanelMode.Vessel && size.Width > 0 && size.Height > 0) _vm?.SetPropsViewport(size.Width, size.Height);
    }

    private IDisposable? _closedHide;

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

    /// <summary>里面的 PrPanel（页面的版式按它的放大 / 关闭排）。</summary>
    public PrPanel PanelControl => Frame;

    /// <summary>釜体舞台（核对用）。</summary>
    public VesselStage StageControl => Stage;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            Sync();
            // 换成放属性卡片的版式（宽版式 → 页签版式）：尺寸可能不变（没有 SizeChanged），先按现在的报一次
            ReportViewport(Body.Bounds.Size);
        }
        // 重新显示（宽版式的「属性查看器」回来、还原放大 / 关闭）：尺寸与藏起来之前一样时不会再有 SizeChanged，
        // 而这期间别的面板（页签版式）可能报过自己的尺寸
        else if (change.Property == IsVisibleProperty && IsVisible && Body is not null) ReportViewport(Body.Bounds.Size);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVm;
            _vm.TabSwitched -= OnTabSwitched;
        }
        _vm = DataContext as ReactorVesselVm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVm;
            _vm.TabSwitched += OnTabSwitched;
            ReportViewport(Body.Bounds.Size);
        }
        Sync();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReactorVesselVm.Tab) or nameof(ReactorVesselVm.HasBadge) or nameof(ReactorVesselVm.HasMore)
            or nameof(ReactorVesselVm.VesselKind)) Sync();
    }

    /// <summary>换了页签：属性查看器回到顶部（同一个面板，样稿 body.scrollTop = 0）。</summary>
    private void OnTabSwitched(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => PropsBody.Offset = default, DispatcherPriority.Background);

    private void SetNarrow(bool narrow)
    {
        if (narrow == _narrow) return;
        _narrow = narrow;
        Sync();
    }

    private void OnPropClose(object? sender, RoutedEventArgs e)
    {
        if (e.Source is PropCard { DataContext: ReactorPropVm p }) _vm?.Hide(p.Key);
        e.Handled = true;
    }

    /// <summary>标题、右侧操作与内容按 <see cref="Mode"/> 和当前页签排。</summary>
    private void Sync()
    {
        var mode = Mode;
        var vm = _vm;
        bool vessel = mode == VesselPanelMode.Vessel || (mode == VesselPanelMode.Tabs && (vm?.IsVesselTab ?? true));
        switch (mode)
        {
            case VesselPanelMode.Tabs:
                if (!ReferenceEquals(Frame.Title, Tabs)) Frame.Title = Tabs;
                Frame.Icon = null;
                Frame.Sub = null;
                Avalonia.Automation.AutomationProperties.SetName(Frame, "釜体 / 属性查看器");
                break;
            case VesselPanelMode.Vessel:
                Frame.Title = "釜体";
                Frame.Icon = "flask";
                Frame.Sub = vm?.VesselKind;
                Avalonia.Automation.AutomationProperties.SetName(Frame, "釜体");
                break;
            default:
                Frame.Title = "属性查看器";
                Frame.Icon = "tree";
                Frame.Sub = null;
                Avalonia.Automation.AutomationProperties.SetName(Frame, "属性查看器");
                break;
        }
        Frame.ShowGrip = !_narrow;
        StageBody.IsVisible = vessel;
        PropsBody.IsVisible = !vessel;
        BadgeHost.IsVisible = vessel && !_narrow && (vm?.HasBadge ?? false);
        More.IsVisible = !vessel && (vm?.HasMore ?? false);
        Restore.IsVisible = !vessel;
    }
}
