using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Api;

namespace ParallelReactor.Control.ViewModels;

public enum TabKind { Reactor, Curves, Overview, NewProgram, Perf }

/// <summary>标签栏一项（RV1–RV8 / 全部曲线 / 总览 / 新建程序 / 性能测试）。</summary>
public sealed partial class TabVm : ObservableObject
{
    public TabVm(string title, TabKind kind, int rv = 0) { Title = title; Kind = kind; Rv = rv; }
    public string Title { get; }
    public TabKind Kind { get; }
    public int Rv { get; }
    /// <summary>mock：.reactor-tab（RV 与性能测试）有激活态顶条；.utility-tab 没有。</summary>
    public bool IsReactorTab => Kind is TabKind.Reactor or TabKind.Perf;
    [ObservableProperty] private bool _isActive;
}

/// <summary>一个通道的最新读数（反应釜列表 / 属性卡 / 时间轴 / 总览共用）。</summary>
public sealed partial class ChannelVm : ObservableObject
{
    public ChannelVm(int id) { Id = id; Name = "RV" + id; }
    public int Id { get; }
    public string Name { get; }

    [ObservableProperty] private bool _installed = true;
    [ObservableProperty] private string _state = ChannelStates.Idle;
    [ObservableProperty] private string _stateText = "—";
    [ObservableProperty] private string? _alarmCause;
    [ObservableProperty] private double? _tempC;
    [ObservableProperty] private double _tempSpC;
    [ObservableProperty] private bool _tempOnline = true;
    [ObservableProperty] private double? _heatOutputPct;
    [ObservableProperty] private string _spMode = "fixed";
    [ObservableProperty] private string _approachPhase = "off";
    [ObservableProperty] private string _approachText = "";
    [ObservableProperty] private double? _pressurePsi;
    [ObservableProperty] private int? _pressureRaw;
    [ObservableProperty] private bool _valveOpen;
    [ObservableProperty] private bool _autotuning;
    [ObservableProperty] private string? _startedBy;
    // channels[i].program
    [ObservableProperty] private bool _hasProgram;
    [ObservableProperty] private bool _programRunning;
    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private string _programState = "";
    [ObservableProperty] private string _programMessage = "";
    [ObservableProperty] private DateTime? _programStartedAt;
    [ObservableProperty] private bool _waitingGas;
    // 全局（每行同值）
    [ObservableProperty] private string _voltageText = "24V";
    [ObservableProperty] private bool _isRunning;

    public bool IsEnabled => Installed;
    public string ListStateText => Installed ? StateText : "未安装";
    public string TempText => TempC is double t ? t.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    public string SpText => TempSpC.ToString("0.0", CultureInfo.InvariantCulture);
    public string PressureText => PressurePsi is double p ? p.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    public string ValveText => ValveOpen ? "开" : "关";
    public string ProgramText => !HasProgram ? "无程序" : $"{ChannelProgramStates.Zh(ProgramState)} {StepIndex}/{StepCount}";
    public string OverviewLine => $"{TempText} ℃ · SP {SpText} ℃ · {PressureText} psi";

    public void Apply(ChannelSnap c, double? busV)
    {
        Installed = c.Installed;
        State = c.State;
        AlarmCause = c.AlarmCause;
        StateText = ChannelStates.Zh(c.State, c.AlarmCause);
        TempC = c.TempC; TempSpC = c.TempSpC; TempOnline = c.TempOnline; HeatOutputPct = c.HeatOutputPct;
        SpMode = c.SpMode; ApproachPhase = c.Approach?.Phase ?? "off"; ApproachText = c.Approach?.StatusText ?? "";
        PressurePsi = c.PressurePsi; PressureRaw = c.PressureRaw; ValveOpen = c.ValveOpen; Autotuning = c.Autotuning; StartedBy = c.StartedBy;
        var p = c.Program;
        HasProgram = p != null;
        ProgramRunning = p?.Running ?? false;
        StepIndex = p?.StepIndex ?? 0; StepCount = p?.StepCount ?? 0;
        ProgramState = p?.State ?? ""; ProgramMessage = p?.Message ?? ""; ProgramStartedAt = p?.StartedAt; WaitingGas = p?.WaitingGas ?? false;
        IsRunning = ProgramRunning || ChannelStates.IsActive(c.State);
        VoltageText = busV is double v ? Math.Round(v).ToString(CultureInfo.InvariantCulture) + "V" : "24V";
        OnPropertyChanged(nameof(IsEnabled)); OnPropertyChanged(nameof(ListStateText)); OnPropertyChanged(nameof(TempText));
        OnPropertyChanged(nameof(SpText)); OnPropertyChanged(nameof(PressureText)); OnPropertyChanged(nameof(ProgramText)); OnPropertyChanged(nameof(OverviewLine)); OnPropertyChanged(nameof(ValveText));
    }
}

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

/// <summary>时间轴一行（run-bar 几何以 0–1 比例给自绘控件）。</summary>
public sealed partial class TimelineRowVm : ObservableObject
{
    public TimelineRowVm(ChannelVm ch) { Channel = ch; }
    public ChannelVm Channel { get; }
    [ObservableProperty] private bool _hasBar;      // 只有有程序（运行中 / 已结束）的行才画条；空闲 / 未连接不画
    [ObservableProperty] private double _left;      // 0–1：StartedAt 在窗口里的位置
    [ObservableProperty] private double _right;     // 0–1：now（运行中）或条尾
    [ObservableProperty] private double _complete;  // 0–1：已完成步数比例
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _waitingGas;
    [ObservableProperty] private bool _disabled;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _playTitle = "启动";
}

/// <summary>总览视图一格。</summary>
public sealed class OverviewTileVm
{
    public OverviewTileVm(ChannelVm ch) { Channel = ch; }
    public ChannelVm Channel { get; }
}
