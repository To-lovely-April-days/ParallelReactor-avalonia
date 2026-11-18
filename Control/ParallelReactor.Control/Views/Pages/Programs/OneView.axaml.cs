using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Controls.Programs;
using ParallelReactor.Control.Pages.Programs;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>单路程序（见 OneView.axaml）：右列宽 372（窄屏 340）；预计曲线高 176（矮屏 112）。</summary>
public partial class OneView : UserControl
{
    private ProgramPageVm? _vm;

    public OneView() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as ProgramPageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; Apply(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramPageVm.IsNarrow) or nameof(ProgramPageVm.IsShort)) Apply();
    }

    private void Apply()
    {
        if (_vm == null) return;
        var root = this.FindControl<Grid>("Root");
        if (root != null) root.ColumnDefinitions[2].Width = new GridLength(_vm.IsNarrow ? 340 : 372);
        if (this.FindControl<PlanPreviewChart>("Preview") is { } p) p.Height = _vm.IsShort ? 112 : 176;
    }
}
