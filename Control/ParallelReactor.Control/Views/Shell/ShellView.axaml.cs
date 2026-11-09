using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 新外壳的网格（见 ShellView.axaml）。列宽 / 行高、资源管理器停靠或浮层、根元素的宽高类（fw-* / fh-*、insp-n、x-*、tl-collapsed）
/// 按 <see cref="ShellLayout"/> 设置 —— 布局只看窗口尺寸（R4），这里只是把结果写到网格上。
/// </summary>
public partial class ShellView : UserControl
{
    private ShellVm? _vm;

    public ShellView()
    {
        InitializeComponent();
        // 浮层资源管理器下面的浅遮罩盖住整个外框（图标栏、菜单、页签、工作区、检查器、时间轴）：点它只收起浮层，这一下不再传给下面的控件
        // （样稿 shell.js：click 落在 #pr-xscrim 上 → xMode = 'collapsed'）。全局停止格（ZIndex 201）在遮罩之上，照常可点。
        XScrim.PointerPressed += OnScrimPressed;
    }

    private void OnScrimPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_vm == null || !_vm.Layout.ExplorerFloat) return;
        _vm.Layout.CloseFloat();
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.Layout.Changed -= OnLayout;
        _vm = DataContext as ShellVm;
        if (_vm != null) { _vm.Layout.Changed += OnLayout; Apply(_vm.Layout); }
    }

    private void OnLayout(object? sender, EventArgs e) { if (_vm != null) Apply(_vm.Layout); }

    private void Apply(ShellLayout L)
    {
        var cols = Root.ColumnDefinitions;
        var rows = Root.RowDefinitions;
        if (cols[1].Width != L.ExplorerColumn) cols[1].Width = L.ExplorerColumn;
        if (cols[3].Width != L.InspectorColumn) cols[3].Width = L.InspectorColumn;
        if (rows[3].Height != L.TimelineRow) rows[3].Height = L.TimelineRow;

        // 资源管理器：停靠（第 2 列，页签行起跨两行）/ 浮层（图标栏右侧，菜单栏下到状态栏上，320 宽，ShadowPop）/ 收起
        var x = Explorer;
        x.IsVisible = L.ExplorerDocked || L.ExplorerFloat;
        if (L.ExplorerFloat)
        {
            Grid.SetColumn(x, 0); Grid.SetColumnSpan(x, 4); Grid.SetRow(x, 1); Grid.SetRowSpan(x, 3);
            x.Width = ShellLayout.ExplorerFloatW;
            x.HorizontalAlignment = HorizontalAlignment.Left;
            x.Margin = new Thickness(ShellLayout.SideW, 0, 0, 0);
            x.ZIndex = 40;
            x.IsFloating = true;
            x.Classes.Set("audit-layer", true);   // 截图审计：浮层盖住下面是设计如此，重叠只在浮层里比
        }
        else
        {
            Grid.SetColumn(x, 1); Grid.SetColumnSpan(x, 1); Grid.SetRow(x, 1); Grid.SetRowSpan(x, 2);
            x.Width = double.NaN;
            x.HorizontalAlignment = HorizontalAlignment.Stretch;
            x.Margin = default;
            x.ZIndex = 2;
            x.IsFloating = false;
            x.Classes.Set("audit-layer", false);
        }

        var c = Root.Classes;
        foreach (var k in new[] { "fw-s", "fw-m", "fw-l" }) c.Set(k, k == L.WidthClass);
        foreach (var k in new[] { "fh-s", "fh-m", "fh-l" }) c.Set(k, k == L.HeightClass);
        c.Set("insp-n", L.IsInspectorNarrow);
        c.Set("x-docked", L.ExplorerDocked);
        c.Set("x-float", L.ExplorerFloat);
        c.Set("x-collapsed", !L.ExplorerDocked && !L.ExplorerFloat);
        c.Set("tl-collapsed", L.TimelineFolded);
    }
}
