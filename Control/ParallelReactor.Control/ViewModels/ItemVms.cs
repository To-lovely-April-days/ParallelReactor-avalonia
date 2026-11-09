using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Api;

namespace ParallelReactor.Control.ViewModels;

/// <summary>属性查看器一张卡（mock .property-card）。</summary>
public sealed partial class PropertyCardVm : ObservableObject
{
    public PropertyCardVm(string key, string path, string primary, string secondary, string number, string unit, bool greenEdge)
    { Key = key; _path = path; _primary = primary; _secondary = secondary; _number = number; _unit = unit; _greenEdge = greenEdge; }
    public string Key { get; }
    [ObservableProperty] private string _path;
    [ObservableProperty] private string _primary;
    [ObservableProperty] private string _secondary;
    [ObservableProperty] private string _number;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private bool _greenEdge;
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private bool _isFault;
    [ObservableProperty] private bool _isOffline;
}
