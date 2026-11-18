using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Pages.Alarms;

namespace ParallelReactor.Control.Views.Pages.Alarms;

/// <summary>
/// 报警页（融合版 F5）的视图：详情列宽跟着宽窄屏（400 / 340，样稿 @media max-width 1599px）；表头在竖滚动条出现时右边让出滚动条的宽
/// （样稿的表头是表格里 sticky 的一行，滚动条从表头上面开始）。
/// </summary>
public partial class AlarmPage : UserControl
{
    private AlarmPageVm? _vm;

    public AlarmPage()
    {
        InitializeComponent();
        Scroll.ScrollChanged += (_, _) => SyncHead();
        Scroll.LayoutUpdated += (_, _) => SyncHead();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as AlarmPageVm;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        SyncCols();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlarmPageVm.IsNarrow)) SyncCols();
    }

    private void SyncCols() => Root.ColumnDefinitions[2].Width = new GridLength(_vm?.IsNarrow == true ? 340 : 400);

    private void SyncHead()
    {
        bool bar = Scroll.Extent.Height > Scroll.Viewport.Height + 0.5;
        var m = new Thickness(0, 0, bar ? 12 : 0, 0);
        if (Head.Padding != m) Head.Padding = m;
    }
}
