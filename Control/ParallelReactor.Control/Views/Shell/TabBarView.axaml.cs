using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 页签栏：DocTabStrip 量出的隐藏集合写回 TabsVm（「›」上的个数；收起哪些按「›」不写个数时的宽度定，见 TabOverflowPanel）；当前页签变了就重排（当前页签始终可见）；
/// 「›」菜单（DynamicMenuFlyout）和「+」弹出框在打开时按当前状态生成。
/// </summary>
public partial class TabBarView : UserControl
{
    private ShellVm? _vm;
    private NewTabVm? _newTab;

    public TabBarView()
    {
        InitializeComponent();
        if (More.Flyout is DynamicMenuFlyout mf) mf.Opening += OnMoreOpening;
        if (Add.Flyout is Flyout f) f.Opening += OnAddOpening;
        Strip.HiddenChanged += OnHidden;
        Strip.AddHandler(ContextRequestedEvent, OnContext, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// 页签的右键菜单：设备页签 = 这台设备的菜单（MenuModel.DeviceItems，同资源管理器右键与总览 «⋯»），反应釜页签 = 反应釜菜单
    /// （MenuModel.ReactorItems）；总览、视图页签没有右键菜单。不改选中对象、不切页签（菜单标题写明是哪一台）。
    /// </summary>
    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm == null || e.Source is not Avalonia.Visual v) return;
        var tab = v.GetSelfAndVisualAncestors().OfType<Avalonia.StyledElement>().Select(x => x.DataContext).OfType<DocTabVm>().FirstOrDefault();
        if (tab == null || e.Source is not Avalonia.Controls.Control c) return;
        var d = _vm.Fleet.Device(tab.DeviceId);
        if (d == null) return;
        if (tab.Kind == PageKind.Device) ContextMenus.Open(c, MenuModel.DeviceItems(_vm, d), e);
        else if (tab.Kind == PageKind.Reactor && d.Reactor(tab.Rv) is { } r) ContextMenus.Open(c, MenuModel.ReactorItems(_vm, d, r), e);
        else return;
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.Tabs.PropertyChanged -= OnTabsProp;
        _vm = DataContext as ShellVm;
        if (_vm != null) { _vm.Tabs.PropertyChanged += OnTabsProp; OnHidden(Strip.Hidden); }
    }

    private void OnTabsProp(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabsVm.Active)) Strip.Relayout();
    }

    private void OnHidden(IReadOnlyCollection<string> ids) => _vm?.Tabs.SetHidden(ids);

    private void OnMoreOpening(object? sender, EventArgs e)
    {
        if (_vm == null || sender is not DynamicMenuFlyout mf) return;
        mf.SetEntries(MenuModel.TabItems(_vm));
    }

    private void OnAddOpening(object? sender, EventArgs e)
    {
        if (_vm == null || sender is not Flyout f) return;
        if (_newTab != null) _newTab.Navigated -= OnNavigated;
        _newTab = new NewTabVm(_vm);
        _newTab.Navigated += OnNavigated;
        f.Content = new NewTabPopover { DataContext = _newTab };
    }

    private void OnNavigated(object? sender, EventArgs e) => Add.Flyout?.Hide();
}
