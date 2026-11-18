using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Pages.Curves;

namespace ParallelReactor.Control.Views.Pages.Curves;

/// <summary>
/// 曲线页（融合版 F5）的视图：几张图的指针 → 页面的光标（页面取整后再设回每张图，竖线同步）；下面一行矮屏 196、否则 236（样稿 @media max-height 820px）；
/// 读数表的表头在竖滚动条出现时右边让出滚动条的宽。
/// </summary>
public partial class CurvePage : UserControl
{
    private CurvePageVm? _vm;

    public CurvePage()
    {
        InitializeComponent();
        foreach (var ch in new[] { ChT, ChP, ChG }) ch.CursorMoved += t => _vm?.SetCursor(t);
        Scroll.ScrollChanged += (_, _) => SyncHead();
        Scroll.LayoutUpdated += (_, _) => SyncHead();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as CurvePageVm;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        SyncRows();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurvePageVm.IsShort)) SyncRows();
    }

    private void SyncRows() => Root.RowDefinitions[2].Height = new GridLength(_vm?.IsShort == true ? 196 : 236);

    private void SyncHead()
    {
        bool bar = Scroll.Extent.Height > Scroll.Viewport.Height + 0.5;
        var m = new Thickness(0, 0, bar ? 12 : 0, 0);
        if (Head.Padding != m) Head.Padding = m;
    }
}
