using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 图标栏（PORT-PLAN §2.1「图标栏 64 px」；样稿 shell.js renderSidebar）：设备（开关资源管理器，只用图标亮度表示开 / 关）、总览、程序、曲线、记录、
/// 报警（白底错误报警数 + 红点；只有提醒时不画数字，提醒数在悬停提示里）、测试、设置；底部「收起 / 展开」（专注模式）。
/// 当前项（总览页、或对应的占位页）加深 + 左侧 3 px 白条。
/// </summary>
public sealed partial class SidebarVm : ObservableObject
{
    private readonly ShellVm _shell;

    public SidebarVm(ShellVm shell)
    {
        _shell = shell;
        ToggleExplorerCommand = new RelayCommand(shell.ToggleExplorer);
        ToggleFocusCommand = new RelayCommand(shell.ToggleFocus);
        GoCommand = new RelayCommand<string>(p => { if (Route.Parse(p) is { } r) shell.Navigate(r); });
    }

    public ICommand ToggleExplorerCommand { get; }
    public ICommand ToggleFocusCommand { get; }
    /// <summary>参数是路由路径（overview、placeholder/program …）。</summary>
    public ICommand GoCommand { get; }

    [ObservableProperty] private bool _explorerOpen;
    [ObservableProperty] private string _explorerTip = "";
    [ObservableProperty] private bool _overviewOn;
    [ObservableProperty] private bool _programOn;
    [ObservableProperty] private bool _curvesOn;
    [ObservableProperty] private bool _recordsOn;
    [ObservableProperty] private bool _alarmsOn;
    [ObservableProperty] private bool _perfOn;
    [ObservableProperty] private bool _settingsOn;
    /// <summary>错误报警数（没有时 null，不画）。</summary>
    [ObservableProperty] private string? _alarmBadge;
    [ObservableProperty] private string _alarmTip = "";
    [ObservableProperty] private string _focusLabel = "收起";
    [ObservableProperty] private string _focusIcon = "collapse";

    internal void Refresh()
    {
        var r = _shell.Route;
        bool Ph(string k) => r.Page == PageKind.Placeholder && r.Key == k;
        ExplorerOpen = _shell.Layout.ExplorerOpen;
        ExplorerTip = ExplorerOpen ? "收起设备资源管理器" : "打开设备资源管理器";
        OverviewOn = r.Page == PageKind.Overview;
        ProgramOn = Ph("program"); CurvesOn = Ph("curves"); RecordsOn = Ph("records"); AlarmsOn = Ph("alarms"); PerfOn = Ph("perf"); SettingsOn = Ph("settings");
        int err = _shell.Fleet.Devices.Sum(d => d.ErrAlarms), warn = _shell.Fleet.Devices.Sum(d => d.WarnAlarms);
        AlarmBadge = err > 0 ? err.ToString() : null;
        AlarmTip = "报警 · " + ShellText.AlarmText(err, warn, "没有报警");
        bool focus = _shell.Layout.Focus;
        FocusLabel = focus ? "展开" : "收起";
        FocusIcon = focus ? "expand" : "collapse";
    }
}

/// <summary>状态栏报警项里的一段（「● 急停 1 台 · 报警 1 · 提醒 1」：红色的事实加粗，「·」是 t-3）。</summary>
public sealed record StatusPart(string Text, bool Bold, bool Mid);

/// <summary>
/// 状态栏（PORT-PLAN §2.1「状态栏 30 px」；样稿 shell.js renderStatus）：左 已连接 k/n 台 | 远程控制 k 台（· 宽限中 k 台）| 报警（可点：选中有报警的对象）| 会话 …；
/// 右 示例型号 · 参数为假设（演示时前面加「演示数据」，R17）| 日期 周几 时间。字一律中性；红色只在图标 / 点上。
/// </summary>
public sealed partial class StatusBarVm : ObservableObject
{
    private readonly ShellVm _shell;
    private (int K, int N) _alarmTarget;

    public StatusBarVm(ShellVm shell)
    {
        _shell = shell;
        SelectAlarmCommand = new RelayCommand(() => { if (_alarmTarget.K > 0) shell.Select(_alarmTarget.K, _alarmTarget.N > 0 ? _alarmTarget.N : null); });
    }

    public ICommand SelectAlarmCommand { get; }

    [ObservableProperty] private string _connText = "";
    [ObservableProperty] private bool _allOnline = true;
    [ObservableProperty] private string _connIcon = "link";
    [ObservableProperty] private string _remoteText = "";
    [ObservableProperty] private string? _graceText;
    [ObservableProperty] private IReadOnlyList<StatusPart> _alarmParts = Array.Empty<StatusPart>();
    [ObservableProperty] private bool _alarmDot;
    [ObservableProperty] private string _alarmTip = "";
    [ObservableProperty] private bool _alarmClickable;
    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private string? _noteText;
    [ObservableProperty] private string _clockText = "";

    internal void Refresh()
    {
        var f = _shell.Fleet;
        var ds = f.Devices;
        int connected = ds.Count(d => d.Conn == ConnState.Online);
        AllOnline = connected == ds.Count;
        ConnIcon = AllOnline ? "link" : "wifioff";
        ConnText = "已连接 " + connected + "/" + ds.Count + " 台";
        RemoteText = "远程控制 " + ds.Count(d => d.Conn == ConnState.Online && d.Lease == LeaseState.Remote) + " 台";
        int grace = ds.Count(d => d.Lease == LeaseState.Lost);
        GraceText = grace > 0 ? "宽限中 " + grace + " 台" : null;

        int nE = ds.Count(d => d.EstopActive), err = ds.Sum(d => d.ErrAlarms), warn = ds.Sum(d => d.WarnAlarms);
        var parts = new List<(string, bool)>();
        if (nE > 0) parts.Add(("急停 " + nE + " 台", true));
        if (err > 0) parts.Add(("报警 " + err, true));
        if (warn > 0) parts.Add(("提醒 " + warn, false));
        if (parts.Count == 0) parts.Add(("无报警", false));
        var list = new List<StatusPart>();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) list.Add(new StatusPart("·", false, true));
            list.Add(new StatusPart(parts[i].Item1, parts[i].Item2, false));
        }
        if (!list.SequenceEqual(AlarmParts)) AlarmParts = list;
        AlarmDot = nE > 0 || err > 0;
        AlarmTip = nE > 0 || err > 0 || warn > 0
            ? (nE > 0 ? "急停 " + nE + " 台 · " : "") + "报警 " + err + " 条 · 提醒 " + warn + " 条 · 点击查看"
            : "没有报警";
        // 点击选中：急停中的设备 > 第一条错误报警（没有时第一条提醒）所在的设备 / 反应釜（急停那条不计）
        _alarmTarget = default;
        if (ds.FirstOrDefault(d => d.EstopActive) is { } es) _alarmTarget = (es.K, 0);
        else if (ds.FirstOrDefault(d => d.ErrAlarms > 0) is { } de) _alarmTarget = (de.K, de.AlarmRv(true));
        else if (ds.FirstOrDefault(d => d.WarnAlarms > 0) is { } dw) _alarmTarget = (dw.K, dw.AlarmRv(false));
        AlarmClickable = _alarmTarget.K > 0;

        var sessions = ds.Where(d => d.SessionId != null).Select(d => d.SessionId + "（" + d.Alias + "）").ToList();
        SessionText = "会话 " + (sessions.Count > 0 ? string.Join("、", sessions) : "无");
        NoteText = f.IsDemo ? "演示数据 · " + Core.Models.ModelProfiles.SampleNote : f.HasSampleModels ? Core.Models.ModelProfiles.SampleNote : null;
        var now = f.Now;
        ClockText = Fmt.Date(now) + " " + Fmt.Weekday(now) + " " + Fmt.Time(now);
    }

}

/// <summary>
/// 全局停止格（PORT-PLAN §2.1「全局停止格」；样稿 shell.js renderEstopCell）：页签栏右端、检查器上方：「■ 全部停止」+ 整个界面唯一的实心红「急停」。
/// 压在对话框遮罩之上（ZIndex 201 / 200）。没有已连接的设备时两个都带原因；有设备急停中时「急停」外加 2 px r-line 环。
/// 「已连接」按停止类算（DeviceVm.CanStop）：真机只是 WebSocket 在重连时照样能急停（旧界面也是只要有连接对象就能急停）。
/// </summary>
public sealed partial class StopCellVm : ObservableObject
{
    private readonly ShellVm _shell;

    public StopCellVm(ShellVm shell)
    {
        _shell = shell;
        StopAllCommand = new RelayCommand(() => shell.OpenDialog("stopall"));
        EstopCommand = new RelayCommand(() => shell.OpenDialog("estop"));
    }

    public ICommand StopAllCommand { get; }
    public ICommand EstopCommand { get; }

    public const string StopAllTip = "选择设备后停止全部反应釜（不需要控制权）";
    public const string EstopTip = "选择设备后急停 · 复位只能在触屏";

    /// <summary>没有已连接的设备时的原因（b:Pr.Reason）；有连接时 null。</summary>
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private bool _anyEstop;

    internal void Refresh()
    {
        var ds = _shell.Fleet.Devices;
        Reason = ds.Any(d => d.CanStop) ? null : "没有已连接的设备";
        AnyEstop = ds.Any(d => d.EstopActive);
    }
}
