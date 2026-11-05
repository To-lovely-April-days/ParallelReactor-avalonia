using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

public partial class RunMonitorView : UserControl
{
    private GraphControl? _chart;
    private GraphViewModel? _graph;

    public RunMonitorView()
    {
        AvaloniaXamlLoader.Load(this);
        _chart = this.FindControl<GraphControl>("MonitorChart");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_graph != null) _graph.Changed -= Redraw;
        var vm = DataContext as ProgramViewModel;
        _graph = vm?.MonitorGraph;
        if (_chart != null) _chart.Vm = _graph;
        if (_graph != null) _graph.Changed += Redraw;
        Redraw();
    }

    private void Redraw() => _chart?.InvalidateVisual();
}
