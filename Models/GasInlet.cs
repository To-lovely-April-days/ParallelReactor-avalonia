using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>共用气路上的进气阀，对应 HTML 中 gasInlets 数组项</summary>
public partial class GasInlet : ObservableObject
{
    public string Label { get; init; } = "";
    [ObservableProperty] private bool _on;
    public int P { get; init; }
}
