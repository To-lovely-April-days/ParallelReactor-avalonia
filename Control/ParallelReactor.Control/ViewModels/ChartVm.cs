using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls;

namespace ParallelReactor.Control.ViewModels;

/// <summary>曲线面板的数据（Series 列表固定，Revision 每帧 +1 触发重绘）。</summary>
public sealed partial class ChartVm : ObservableObject
{
    public ChartVm(string title, string subtitle, IReadOnlyList<ChartSeries> series) { _title = title; _subtitle = subtitle; Series = series; }
    public IReadOnlyList<ChartSeries> Series { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle;
    [ObservableProperty] private int _revision;
    [ObservableProperty] private double _leftMax = 100;
    [ObservableProperty] private double _rightMax = 500;
    [ObservableProperty] private double _xMax = 88;
    [ObservableProperty] private string _xLabel = "程序时间 (mins)";
    [ObservableProperty] private string _leftLabel = "温度 (℃)";
    [ObservableProperty] private string _rightLabel = "压力 (psi) / 转速 (rpm ÷ 2)";
}
