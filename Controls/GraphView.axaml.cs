using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

public partial class GraphView : UserControl
{
    private GraphControl? _chart;
    private GraphViewModel? _vm;

    public GraphView()
    {
        AvaloniaXamlLoader.Load(this);
        _chart = this.FindControl<GraphControl>("Chart");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.Changed -= Redraw;
        _vm = DataContext as GraphViewModel;
        if (_chart != null) _chart.Vm = _vm;
        if (_vm != null) _vm.Changed += Redraw;
        Redraw();
    }

    private void Redraw() => _chart?.InvalidateVisual();
}
