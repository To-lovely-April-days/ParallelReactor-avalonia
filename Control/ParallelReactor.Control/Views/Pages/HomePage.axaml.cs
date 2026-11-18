using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ParallelReactor.Control.Controls.Home;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 主页（见 HomePage.axaml）。看板两行的比例按矮屏改（RowDefinitions 不能用样式改）；三块面板折叠成 30 的竖条（样稿 .pane.col：flex 0 0 32）；
/// 运行表的列宽按表宽算（<see cref="RunColumns"/>，设在表上、行和表头继承）；16 路出滚动条时表头右边让出滚动条的宽度。
/// </summary>
public partial class HomePage : UserControl
{
    private HomePageVm? _vm;

    public HomePage()
    {
        InitializeComponent();
        RunRows.SizeChanged += (_, _) => ApplyColumns();
        RunScroll.SizeChanged += (_, _) => ApplyColumns();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as HomePageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; ApplyRows(); ApplyPanes(); ApplyColumns(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomePageVm.IsShort): ApplyRows(); ApplyColumns(); break;
            case nameof(HomePageVm.MonMode) or nameof(HomePageVm.CurveMode) or nameof(HomePageVm.ResMode): ApplyPanes(); break;
            case nameof(HomePageVm.IsNarrow): ApplyColumns(); break;
        }
    }

    private void ApplyRows()
    {
        var top = new GridLength(_vm?.IsShort == true ? 1.3 : 1.14, GridUnitType.Star);
        if (Board.RowDefinitions[0].Height != top) Board.RowDefinitions[0].Height = top;
    }

    /// <summary>折叠的面板：30 宽的竖条（同一行的另一块占满）；通道面板一个人一行，收起后靠左。</summary>
    private void ApplyPanes()
    {
        if (_vm == null) return;
        bool mon = _vm.MonMode == PaneMode.Collapsed, cur = _vm.CurveMode == PaneMode.Collapsed, res = _vm.ResMode == PaneMode.Collapsed;
        PMon.Width = mon ? 30 : double.NaN;
        PMon.HorizontalAlignment = mon ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        var cols = Row2.ColumnDefinitions;
        cols[0].Width = cur ? new GridLength(30) : new GridLength(1.9, GridUnitType.Star);
        cols[2].Width = res ? new GridLength(30) : new GridLength(1, GridUnitType.Star);
        if (cur && !res) cols[2].Width = new GridLength(1, GridUnitType.Star);
        if (res && !cur) cols[0].Width = new GridLength(1, GridUnitType.Star);
        PRes.MinWidth = res ? 0 : 300;
    }

    private void ApplyColumns()
    {
        double w = RunRows.Bounds.Width;
        if (w <= 0) return;
        var cols = RunColumns.For(w, _vm?.IsNarrow == true, _vm?.IsShort == true);
        if (RunRowPanel.GetColumns(RunTable) != cols) RunRowPanel.SetColumns(RunTable, cols);
        double sb = System.Math.Max(0, RunScroll.Bounds.Width - w);
        var m = new Thickness(0, 0, sb, 0);
        if (RunHead.Margin != m) RunHead.Margin = m;
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RunRowVm row }) _vm?.OpenRow(row);
    }
}
