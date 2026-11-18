using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Pages.Programs;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>
/// 条件矩阵（见 MatrixView.axaml）：右列宽 340（窄屏 290）；表头在滚动区外面，表体出滚动条时表头右边让出滚动条的宽度（列对齐）。
/// </summary>
public partial class MatrixView : UserControl
{
    private ProgramPageVm? _vm;
    private Border? _head;
    private ScrollViewer? _body;

    public MatrixView()
    {
        AvaloniaXamlLoader.Load(this);
        _head = this.FindControl<Border>("Head");
        _body = this.FindControl<ScrollViewer>("Body");
        if (_body != null)
        {
            _body.ScrollChanged += (_, _) => Align();
            _body.LayoutUpdated += (_, _) => Align();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as ProgramPageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; Apply(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramPageVm.IsNarrow)) Apply();
    }

    private void Apply()
    {
        if (_vm == null) return;
        var root = this.FindControl<Grid>("Root");
        if (root != null) root.ColumnDefinitions[2].Width = new GridLength(_vm.IsNarrow ? 290 : 340);
        // 下发表单：窄屏标签列收到 56（四个字）、间距 10，会话名称放得下（样稿 .mxfrm 64 + 16，在 290 宽里会被截掉）
        if (this.FindControl<Grid>("SendForm") is { } f)
        {
            f.ColumnDefinitions[0].Width = new GridLength(_vm.IsNarrow ? 56 : 64);
            f.ColumnDefinitions[1].Width = new GridLength(_vm.IsNarrow ? 10 : 16);
        }
    }

    /// <summary>表体有竖滚动条时，表头右边空出同样的宽（滚动条 12）。</summary>
    private void Align()
    {
        if (_head == null || _body == null) return;
        double sb = _body.Extent.Height > _body.Viewport.Height + 0.5 ? Math.Max(0, _body.Bounds.Width - _body.Viewport.Width) : 0;
        var m = new Thickness(0, 0, sb, 0);
        if (_head.Padding != m) _head.Padding = m;
    }
}
