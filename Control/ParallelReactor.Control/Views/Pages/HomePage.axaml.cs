using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 主页（见 HomePage.axaml）。通道面板固定显示气路图；三块面板（通道 / 曲线 / 共用资源）均可折叠成 30 px 的竖条。
/// </summary>
public partial class HomePage : UserControl
{
    private HomePageVm? _vm;

    public HomePage()
    {
        InitializeComponent();
        PMon.SizeChanged += (_, _) => ApplyMvTool();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as HomePageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; ApplyRows(); ApplyPanes(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomePageVm.IsShort): ApplyRows(); break;
            case nameof(HomePageVm.MonMode) or nameof(HomePageVm.CurveMode) or nameof(HomePageVm.ResMode): ApplyPanes(); break;
        }
    }

    /// <summary>看板两行的比例：常规 1.7 : 1，矮屏（&lt; 820）1.75 : 1，保证气路图总管标签与下方曲线/共用资源均可完整显示。</summary>
    private void ApplyRows()
    {
        var top = new GridLength(_vm?.IsShort == true ? 1.75 : 1.7, GridUnitType.Star);
        if (Board.RowDefinitions[0].Height != top) Board.RowDefinitions[0].Height = top;
    }

    /// <summary>通道面板窄于 800：灰带不写「运行 6/8」和搅拌电流。</summary>
    private void ApplyMvTool()
    {
        bool narrow = PMon.Bounds.Width - 30 <= 800;
        MvRun.IsVisible = !narrow;
        MvSep1.IsVisible = !narrow;
        MvStir.ShowAmp = !narrow;
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
}
