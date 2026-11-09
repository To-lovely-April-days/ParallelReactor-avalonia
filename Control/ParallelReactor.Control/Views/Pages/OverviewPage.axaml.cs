using System;
using Avalonia.Controls;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>总览骨架（M2）：每台设备一个分区面板；卡片网格等在 M4。「⋯」菜单（DynamicMenuFlyout）在打开时按当前状态生成（同菜单栏）。</summary>
public partial class OverviewPage : UserControl
{
    public OverviewPage() => InitializeComponent();

    private void OnMoreOpening(object? sender, EventArgs e)
    {
        if (sender is DynamicMenuFlyout mf && mf.Target?.DataContext is OverviewSectionVm s) mf.SetEntries(s.MenuItems());
    }
}
