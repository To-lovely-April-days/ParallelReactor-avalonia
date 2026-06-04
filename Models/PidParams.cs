using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>压力 PID 整定参数，对应 HTML 中的 pid:{maxpulse,pboost}</summary>
public partial class PidParams : ObservableObject
{
    [ObservableProperty] private double _maxPulse = 3;
    [ObservableProperty] private double _pBoost = 7;
}
