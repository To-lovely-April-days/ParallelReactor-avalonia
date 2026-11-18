using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Alarms;

namespace ParallelReactor.Control.Pages.Alarms;

/// <summary>
/// 报警页表格的一行（样稿 .altb tr.rw）：时间（今天不写日期）· 级别图标 ·（全部设备时）设备 · 来源 · 内容（粗体标题 + 第二行说明）·
/// 值 / 限值（粗体值 + 第二行限值，没有为「—」）· 状态（活动红色粗体 / 已解除 · 时刻；第二行 已确认 · 谁 / 未确认红色）· 「确认」小按钮（未确认的报警）。
/// 选中 = a-tint + 左侧 3 px 品牌蓝；未确认的活动报警 = 左侧 3 px 红（选中时品牌蓝）。身份 = <see cref="Key"/>，每次刷新 <see cref="Apply"/> 就地更新。
/// </summary>
public sealed partial class AlarmRowVm : ObservableObject
{
    public AlarmRowVm(string key, ICommand pick, ICommand ack)
    {
        Key = key;
        PickCommand = pick;
        AckCommand = ack;
    }

    public string Key { get; }
    public ICommand PickCommand { get; }
    public ICommand AckCommand { get; }
    public AlarmLogRow Row { get; private set; } = null!;

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private AlarmLogLevel _level;
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private string _src = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _act = "";
    [ObservableProperty] private bool _hasAct;
    [ObservableProperty] private bool _hasVal;
    [ObservableProperty] private string _val = "";
    [ObservableProperty] private string _lim = "";
    [ObservableProperty] private bool _hasLim;
    [ObservableProperty] private bool _isEvent;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private bool _stateActive;
    [ObservableProperty] private string _ackText = "";
    [ObservableProperty] private bool _ackRed;
    [ObservableProperty] private bool _hasAckText;
    [ObservableProperty] private bool _showAck;
    [ObservableProperty] private string? _ackReason;
    [ObservableProperty] private bool _isSelected;
    /// <summary>未确认的活动报警（行首红竖条）。</summary>
    [ObservableProperty] private bool _isUa;
    [ObservableProperty] private bool _showDevice;
    /// <summary>悬停全文（标题 · 说明 · 值）。</summary>
    [ObservableProperty] private string _tip = "";

    internal void Apply(AlarmLogRow r, DateTime now, bool showDevice, bool selected, string? ackReason)
    {
        Row = r;
        Time = AlarmLog.TimeText(r.At, now);
        Level = r.Level;
        Device = r.Device;
        ShowDevice = showDevice;
        Src = r.Src;
        Title = r.Title;
        Act = r.Act;
        HasAct = r.Act.Length > 0;
        HasVal = r.Val != null;
        Val = r.Val ?? "—";
        Lim = r.Lim ?? "";
        HasLim = HasVal && Lim.Length > 0;
        IsEvent = !r.IsAlarm;
        StateText = AlarmLog.StateText(r);
        StateActive = r.State == AlarmLogState.Active;
        AckText = AlarmLog.AckText(r);
        HasAckText = AckText.Length > 0;
        AckRed = r.Unacked;
        ShowAck = r.Unacked;
        AckReason = ackReason;
        IsSelected = selected;
        IsUa = r.Unacked && r.State == AlarmLogState.Active;
        Tip = r.Title + (r.Act.Length > 0 ? " · " + r.Act : "") + (r.Val != null ? " · " + r.Val + (r.Lim is { Length: > 0 } l ? "（" + l + "）" : "") : "");
    }
}
