using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls;

namespace ParallelReactor.Control.ViewModels;

/// <summary>「连接设备」对话框（覆盖层）。</summary>
public sealed partial class ConnectDialogVm : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _port = "8080";
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string _clientName = "PC-测试工位1";
    [ObservableProperty] private bool _rememberToken;
    [ObservableProperty] private string _status = "未连接";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _hasError;
}

/// <summary>「申请远程控制」对话框（覆盖层）：状态行 + 倒计时。</summary>
public sealed partial class ControlDialogVm : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _modeText = "本地";
    [ObservableProperty] private string _holderText = "—";
    [ObservableProperty] private string _statusLine = "本地";
    [ObservableProperty] private bool _requesting;
    [ObservableProperty] private bool _holding;
    [ObservableProperty] private int _remainingSec;
    [ObservableProperty] private string _leaseText = "—";
}

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
