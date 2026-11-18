using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 融合版外框的网格（见 ShellView.axaml）。按 <see cref="DockLayout"/> 摆停靠区：两侧列宽（停靠 296 / 340，收起 / 浮出 30）、浮出的面板跨列摆在内容上（设备面板靠左 330，检查器靠右 400，ZIndex 60）；
/// 根元素的类 narrow（&lt; 1600）/ short（矮于 820）。布局只看窗口尺寸，这里只是把结果写到网格上。
/// 点在浮出面板 / 报警列表外面：收回去（样稿捕获阶段的 click），这一下照常落到下面。
/// </summary>
public partial class ShellView : UserControl
{
    private ShellVm? _vm;

    public ShellView()
    {
        InitializeComponent();
        InspPane.ToggleCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _vm?.Layout.Toggle(DockPaneId.Inspector));
        AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.Layout.Changed -= OnLayout; _vm.PropertyChanged -= OnVm; }
        _vm = DataContext as ShellVm;
        if (_vm != null) { _vm.Layout.Changed += OnLayout; _vm.PropertyChanged += OnVm; Apply(_vm.Layout); }
    }

    private void OnLayout(object? sender, EventArgs e) { if (_vm != null) Apply(_vm.Layout); }

    private void OnVm(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellVm.ShowInspector) && _vm != null) Apply(_vm.Layout);
    }

    /// <summary>点在外面：收回浮出的面板、关掉报警列表（点在打开它的按钮上不算，按钮自己切换）。</summary>
    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_vm == null || e.Source is not Visual src) return;
        if (_vm.Alarms.IsOpen && !AlarmPop.IsVisualAncestorOf(src) && src.FindAncestorOfType<Button>(includeSelf: true) is not { Name: "AlarmButton" }
            && !IsInDialog(src))
            _vm.Alarms.IsOpen = false;
        if (_vm.Layout.AnyFloat && !IsIn(src, DevPane, DockPaneId.Devices) && !IsIn(src, InspPane, DockPaneId.Inspector) && !IsInDialog(src))
            _vm.Layout.CloseFloats();
    }

    private bool IsIn(Visual src, DockPane pane, DockPaneId id) => _vm!.Layout.Mode(id) == PaneMode.Float && (ReferenceEquals(pane, src) || pane.IsVisualAncestorOf(src));

    private static bool IsInDialog(Visual src) => src.FindAncestorOfType<DialogHost>(includeSelf: true) != null;

    private void Apply(DockLayout L)
    {
        var cols = Dock.ColumnDefinitions;
        var dw = new GridLength(L.ColumnWidth(DockPaneId.Devices));
        bool insp = _vm?.ShowInspector ?? true;
        var iw = new GridLength(insp ? L.ColumnWidth(DockPaneId.Inspector) : 0);
        var ig = new GridLength(insp ? 8 : 0);
        if (cols[0].Width != dw) cols[0].Width = dw;
        if (cols[3].Width != ig) cols[3].Width = ig;
        if (cols[4].Width != iw) cols[4].Width = iw;
        InspPane.IsVisible = insp;
        Place(DevPane, L.Mode(DockPaneId.Devices), 0, DockLayout.DevFloatW, HorizontalAlignment.Left);
        Place(InspPane, L.Mode(DockPaneId.Inspector), 4, DockLayout.InspFloatW, HorizontalAlignment.Right);
        Root.Classes.Set("narrow", !L.IsWide);
        Root.Classes.Set("short", L.IsShort);
    }

    /// <summary>浮出：跨全部列、固定宽、靠左 / 右，压在看板上；否则回到自己的列。</summary>
    private static void Place(DockPane p, PaneMode mode, int col, double floatW, HorizontalAlignment side)
    {
        if (mode == PaneMode.Float)
        {
            Grid.SetColumn(p, 0); Grid.SetColumnSpan(p, 5);
            p.Width = floatW;
            p.HorizontalAlignment = side;
            p.ZIndex = 60;
            p.Classes.Set("audit-layer", true);   // 截图审计：浮出的面板盖住下面是设计如此，重叠只在面板里比
        }
        else
        {
            Grid.SetColumn(p, col); Grid.SetColumnSpan(p, 1);
            p.Width = double.NaN;
            p.HorizontalAlignment = HorizontalAlignment.Stretch;
            p.ZIndex = 2;
            p.Classes.Set("audit-layer", false);
        }
    }
}
