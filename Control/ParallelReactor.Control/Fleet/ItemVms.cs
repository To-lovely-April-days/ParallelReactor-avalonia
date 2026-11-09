using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Fleet;

/// <summary>
/// 反应釜上的程序（样稿 Program + PR.sim.progress / ui.progText）：程序记录（不可变，换了就整个换）+ 此刻的进度。就地更新。
/// 文字：长写法「第 3/4 步 · 剩 42 min」、短写法「3/4 · 42 min」，状态「运行中 / 报警暂停 / 已下发 / 已结束 / 已中止 / 报警中止」。
/// </summary>
public sealed partial class ProgramVm : ObservableObject
{
    [ObservableProperty] private ProgramRecord? _record;
    [ObservableProperty] private ProgressInfo? _progress;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ProgramStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _stepName = "";
    /// <summary>'第 3/4 步 · 剩 42 min'（ui.progText）。</summary>
    [ObservableProperty] private string _progText = "";
    /// <summary>'3/4 · 42 min'（ui.progText short）。</summary>
    [ObservableProperty] private string _progShort = "";
    [ObservableProperty] private bool _isActive;

    public void Apply(ProgramRecord record, ProgressInfo? progress)
    {
        Record = record;
        Progress = progress;
        Name = record.Name;
        Status = record.Status;
        StatusText = progress?.StatusText ?? Core.Programs.Progress.StatusText(record.Status);
        StepName = progress?.StepName ?? "";
        ProgText = Core.Programs.Progress.Text(progress);
        ProgShort = Core.Programs.Progress.Text(progress, shortForm: true);
        IsActive = record.IsActive;
    }
}

/// <summary>
/// 一条当前报警（样稿 alarm；真机 AlarmDto 归一化后的 AlarmItem）。身份 = Id，就地更新已确认状态。
/// 确认报警是停止类操作：不需要控制权，只要连接（<see cref="AckReason"/> 给原因，<see cref="AckCommand"/> 执行时再查一次）。
/// </summary>
public sealed partial class AlarmVm : ObservableObject
{
    private readonly DeviceVm _device;

    public AlarmVm(DeviceVm device, AlarmItem a)
    {
        _device = device;
        Id = a.Id;
        AckCommand = new AsyncRelayCommand(() => _device.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: Id)));
        Apply(a);
    }

    public string Id { get; }
    public ICommand AckCommand { get; }

    [ObservableProperty] private AlarmLevel _level;
    /// <summary>错误报警（红点、「报警」）；否则提醒（中性 ⚠、「提醒」）。</summary>
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private int? _ch;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private DateTime _at;
    /// <summary>'10:11:30'。</summary>
    [ObservableProperty] private string _atText = "";
    [ObservableProperty] private bool _acked;
    [ObservableProperty] private string? _ackBy;
    /// <summary>「确认」不可用的原因（已确认 / 断线）；可用时 null。</summary>
    [ObservableProperty] private string? _ackReason;

    public void Apply(AlarmItem a)
    {
        Level = a.Level;
        IsErr = a.Level == AlarmLevel.Err;
        Ch = a.Ch;
        Code = a.Code;
        Text = a.Text;
        At = a.At;
        AtText = Fmt.Time(a.At);
        Acked = a.Acked;
        AckBy = a.AckBy;
    }

    internal void RefreshReason() => AckReason = Nz(_device.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: Id)));

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>一条事件（样稿 event，最新在前）。不可变：事件不会改。</summary>
public sealed class EventVm
{
    public EventVm(EventItem e)
    {
        Id = e.Id;
        At = e.At;
        AtText = Fmt.Time(e.At);
        Level = e.Level;
        N = e.N;
        Text = e.Text;
    }

    public string Id { get; }
    public DateTime At { get; }
    public string AtText { get; }
    public EventLevel Level { get; }
    public int? N { get; }
    public string Text { get; }
    /// <summary>操作类（设备页「操作」过滤）。</summary>
    public bool IsOp => Level == EventLevel.Op;
}
