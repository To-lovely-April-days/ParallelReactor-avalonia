using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 菜单栏：文件 设备 视图 程序 工具 窗口 帮助（Shell/MenuModel.Menus）。顶层项用 PrMenuBarItem；下拉的内容每次打开时按当前状态重新生成
/// （勾选、「当前：1号机」、禁用原因都是打开那一刻的，同样稿 shell.menu(anchor, () => menuItems(name))）。
/// </summary>
public partial class MenuBarView : UserControl
{
    private ShellVm? _vm;

    public MenuBarView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as ShellVm;
        Build();
    }

    private void Build()
    {
        Bar.Items.Clear();
        if (_vm == null) return;
        var theme = Application.Current?.TryFindResource("PrMenuBarItem", out var t) == true ? t as ControlTheme : null;
        foreach (var (key, title) in MenuModel.Menus)
        {
            var top = new MenuItem { Header = title, Tag = key };
            if (theme != null) top.Theme = theme;
            top.ItemsSource = MenuItems.Build(MenuModel.Items(_vm, key));
            top.SubmenuOpened += OnOpened;
            Bar.Items.Add(top);
        }
    }

    private void OnOpened(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not MenuItem top || !ReferenceEquals(e.Source, top) || top.Tag is not string key) return;
        top.ItemsSource = MenuItems.Build(MenuModel.Items(_vm, key));
    }
}
