using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 菜单栏：文件 设备 视图 程序 工具 窗口 帮助（Shell/MenuModel.Menus）。顶层项用 PrMenuBarItem；下拉的内容打开时按当前状态生成，
/// 打开着时每秒 / 每次动作后原地刷新（勾选、「当前：1号机」、禁用原因，同样稿 shell.menu(anchor, () => menuItems(name))，见 PopupMenus）。
/// 每个顶层项的 ItemsSource 是一直用的同一个集合（只改内容：原地修补时键盘停着的那一项还在）。点击、键盘见 PopupMenus（悬停不换菜单，同样稿）。
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
            var items = new ObservableCollection<object>();
            var top = new MenuItem { Header = title, Tag = key, ItemsSource = items };
            if (theme != null) top.Theme = theme;
            MenuItems.Patch(items, MenuModel.Items(_vm, key));
            top.SubmenuOpened += OnOpened;
            top.PropertyChanged += OnTopChanged;
            // 下拉的位置（比窗口还高时翻到上面、限高）同右键菜单等，见 PopupMenus.PrepareBar
            top.TemplateApplied += (s, e) => { if (s is MenuItem m && e.NameScope.Find<Popup>("PART_Popup") is { } popup) PopupMenus.PrepareBar(m, popup); };
            // 悬停不换菜单（样稿只认点击，见 PopupMenus.BarClick）：顶层项自己的「指针进入」不交给 11.1 的菜单处理（下拉里的菜单项照常）
            top.AddHandler(MenuItem.PointerEnteredItemEvent, (s, e) => { if (ReferenceEquals(e.Source, s)) e.Handled = true; });
            Bar.Items.Add(top);
        }
    }

    private void OnOpened(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not MenuItem top || !ReferenceEquals(e.Source, top) || top.Tag is not string key
            || top.ItemsSource is not ObservableCollection<object> items) return;
        var entries = MenuModel.Items(_vm, key);
        MenuItems.Patch(items, entries);
        PopupMenus.BarOpened(Bar, top, items, entries);
    }

    private static void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MenuItem.IsSubMenuOpenProperty && sender is MenuItem { IsSubMenuOpen: false } top) PopupMenus.BarClosed(top);
    }
}
