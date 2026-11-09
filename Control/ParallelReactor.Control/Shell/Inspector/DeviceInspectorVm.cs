using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 设备检查器（样稿 shell.js inspDevice + devHead）。从上到下：对象标题（设备图标 · 别名 18/600 · 连接徽标；型号 · 已安装（4/8 路）；地址 · SN）、
/// 急停中红框、控制权卡、运行（全部运行 / 全部停止，只作用于这一台；单台急停）、共用搅拌（转速 + 设定 + 当前 + 启动 / 停止）或「各釜独立」说明、
/// 会话（记录中：会话号 · 已 N min · 标记 n 个 + 打标记 / 结束；否则开始记录）、报警（前 4 条）、最近事件（前 5 条）。
/// 设备页打开着这台设备且页面有「报警与事件」时，最后两个分区不画（页面上有，<see cref="ShowLog"/>）。
/// 「已经是这个状态」的启动类操作不画灰按钮，画状态格（stateCell）：全部运行中、搅拌运行中。
/// 未安装位置的检查器（<see cref="UninstalledInspectorVm"/>）里也用它（<see cref="HasHead"/> = false）。
/// </summary>
public sealed partial class DeviceInspectorVm : ObservableObject
{
    private readonly ShellVm _shell;
    private bool _rpmDirty, _settingRpm;

    public DeviceInspectorVm(ShellVm shell, DeviceVm d, bool hasHead = true)
    {
        _shell = shell;
        D = d;
        HasHead = hasHead;
        Lease = new LeaseBoxVm(shell);
        var k = d.K;
        // 真机（D6）：「全部运行」先列清单确认（实时模式注册的 runall 对话框）；演示数据照样稿直接运行
        RunAll = new InspAct("全部运行", "play", () =>
        {
            if (D.Source is Services.LiveDeviceSource && _shell.Dialogs.IsRegistered("runall")) _shell.OpenDialog("runall", D.K.ToString(CultureInfo.InvariantCulture));
            else Act(DeviceAction.RunAll);
        });
        StopAll = new InspAct("全部停止", "stop", () => Act(DeviceAction.StopAll));
        Estop = new InspAct("急停", "estop", () => Act(DeviceAction.Estop));
        StirSet = new InspAct("设定", null, () => { var r = RpmValue; _rpmDirty = false; Act(DeviceAction.StirSet, new ActionPayload(Rpm: r)); });
        StirOn = new InspAct("启动搅拌", "fan", () => Act(DeviceAction.StirOn));
        StirOff = new InspAct("停止搅拌", "stop", () => Act(DeviceAction.StirOff));
        SessionMark = new InspAct("打标记", "flag", () => Act(DeviceAction.SessionMark, new ActionPayload(Text: D.Alias + " 标记")));
        SessionEnd = new InspAct("结束", "stop", () => Act(DeviceAction.SessionEnd));
        SessionStart = new InspAct("开始记录", "rec", () => Act(DeviceAction.SessionStart));
    }

    public DeviceVm D { get; }
    /// <summary>画对象标题（未安装位置的检查器里不画：上面已经有反应釜的标题和「整机操作」小标题）。</summary>
    public bool HasHead { get; }
    public LeaseBoxVm Lease { get; }

    public InspAct RunAll { get; }
    public InspAct StopAll { get; }
    public InspAct Estop { get; }
    public InspAct StirSet { get; }
    public InspAct StirOn { get; }
    public InspAct StirOff { get; }
    public InspAct SessionMark { get; }
    public InspAct SessionEnd { get; }
    public InspAct SessionStart { get; }

    // —— 对象标题 ——
    [ObservableProperty] private string _alias = "";
    /// <summary>「HT-PR8 · 现场 · RV1–RV4（4/8 路）」（只在装机不全时带括号）。</summary>
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _subTip = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private ConnState _conn;
    [ObservableProperty] private string _connShort = "";
    [ObservableProperty] private string _connTip = "";
    /// <summary>断线的徽标字：「未连接」/「已断开」/「离线」（空心圈 + 字，中性）。</summary>
    [ObservableProperty] private string _offWord = "";
    [ObservableProperty] private string _hostPort = "";
    [ObservableProperty] private string _snText = "";

    // —— 急停 ——
    [ObservableProperty] private bool _estopOn;
    [ObservableProperty] private string _estopText = "";
    /// <summary>「1号机触屏」（整个不断开）。</summary>
    [ObservableProperty] private string _estopWhere = "";
    [ObservableProperty] private string _estopNote = "";
    [ObservableProperty] private string _estopNoteTip = "";

    // —— 运行 ——
    [ObservableProperty] private string _runMeta = "";
    /// <summary>全部运行中（状态格）：在线、有已安装的反应釜、全部在运行。</summary>
    [ObservableProperty] private bool _allRunning;
    [ObservableProperty] private string _hint = "";

    // —— 搅拌 ——
    [ObservableProperty] private bool _sharedStir;
    [ObservableProperty] private string _stirMeta = "";
    [ObservableProperty] private double? _rpmValue;
    [ObservableProperty] private double _rpmMin;
    [ObservableProperty] private double _rpmMax;
    [ObservableProperty] private string? _rpmReason;
    [ObservableProperty] private string _liveRpm = "";
    [ObservableProperty] private bool _liveRpmStale;
    [ObservableProperty] private string _liveRpmUnit = "rpm";
    [ObservableProperty] private string _liveStirText = "";
    [ObservableProperty] private bool _stirRunning;
    /// <summary>各釜独立搅拌：「各釜独立顶置机械搅拌 · 在反应釜上设置」。</summary>
    [ObservableProperty] private string _stirNote = "";

    // —— 会话 ——
    [ObservableProperty] private bool _sessionActive;
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _sessionText = "";

    // —— 报警 / 最近事件 ——
    [ObservableProperty] private bool _showLog = true;
    [ObservableProperty] private string _alarmMeta = "";
    [ObservableProperty] private string _eventMeta = "";
    public ObservableCollection<InspAlarmVm> Alarms { get; } = new();
    public ObservableCollection<InspEventVm> Events { get; } = new();
    [ObservableProperty] private bool _hasAlarms;
    [ObservableProperty] private bool _hasEvents;

    [ObservableProperty] private bool _narrow;

    partial void OnRpmValueChanged(double? value) { if (!_settingRpm) _rpmDirty = true; }

    /// <summary>换了对象（再次选中这台设备）：输入框回到设备的当前值。</summary>
    internal void ResetInputs() => _rpmDirty = false;

    private void Act(DeviceAction a, ActionPayload? p = null) => _ = D.Actions.RunAsync(a, p);

    internal void Refresh(InspCtx c, bool showLog)
    {
        var d = D;
        var m = d.Model;
        bool on = c.Online;
        Narrow = c.Narrow;
        Alias = d.Alias;
        int inst = m.Installed.Count;
        var sub = m.Label + " · " + m.InstalledText + (inst < m.ChannelCount ? "（" + inst + "/" + m.ChannelCount + "\u00a0路）" : "");
        Sub = sub;
        SubTip = sub.Replace('\u00a0', ' ');
        Online = on;
        Conn = d.Conn;
        ConnShort = d.ConnShort;
        ConnTip = d.ConnText;
        OffWord = d.IsFirst ? "未连接" : d.ConnStatus == ConnStatus.Disconnected ? "已断开" : "离线";
        HostPort = d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture);
        // 真机从未连上、devices.json 里也没记下序列号时没有 SN：写「—」（PORT-PLAN §3.2：空值一律「—」；设备管理表同样）
        SnText = "SN " + (string.IsNullOrEmpty(d.Sn) ? Core.Format.Fmt.Dash : d.Sn);

        EstopOn = d.EstopActive;
        EstopText = d.EstopText;
        EstopWhere = d.Alias + "触屏";
        EstopNote = "只急停" + d.Alias + " · 复位在触屏";
        EstopNoteTip = "只急停" + d.Alias + " · 复位只能在触屏";
        Estop.Reason = c.Why(DeviceAction.Estop);
        Estop.Tip = "只急停" + d.Alias + " · 切断加热 / 进气 / 搅拌 · 复位只能在触屏";

        Lease.Refresh(c);

        // 运行：摘要计数同侧栏 / 状态栏 / 时间轴（急停那条不计）；断线时「断线前运行 k/n」
        var at = d.AlarmText;
        RunMeta = (on || !d.EverOnline ? "运行 " : d.ConnStatus == ConnStatus.Disconnected ? "断开前运行 " : "断线前运行 ") + d.Running + "/" + d.Installed + (at.Length > 0 ? " · " + at : "");
        AllRunning = on && d.Installed > 0 && d.Running == d.Installed;
        RunAll.Reason = c.Why(DeviceAction.RunAll);
        RunAll.Tip = "运行" + d.Alias + "上已下发程序的反应釜";
        StopAll.Reason = c.Why(DeviceAction.StopAll);
        StopAll.Tip = "只停止" + d.Alias + "的全部反应釜（不需要控制权）";
        Hint = "只作用于" + d.Alias + " · 停止不需要控制权";

        // 共用搅拌（设置只在这里；设备页「共用资源」只显示状态）
        SharedStir = m.StirMode == StirMode.Shared;
        if (SharedStir)
        {
            var stir = d.Stir;
            StirMeta = m.StirKind + " · RV1–RV" + m.ChannelCount;
            RpmMin = m.Limits.RpmMin;
            RpmMax = m.Limits.RpmMax;
            if (!_rpmDirty) { _settingRpm = true; RpmValue = stir?.Rpm; _settingRpm = false; }
            var ctl = c.Guard(GuardKind.Control);
            RpmReason = ctl;
            StirSet.Reason = c.Why(DeviceAction.StirSet, new ActionPayload(Rpm: stir?.Rpm));
            // 当前转速：在线 = 实际值；断线 = 最后已知值（灰）+「断线前…」；从未连上 = 「—」+「尚未收到数据」（不编一个 0 rpm）
            bool stirOn = stir?.On ?? false;
            LiveRpm = !d.EverOnline ? Fmt.Dash : stirOn ? Fmt.Rpm(stir?.Rpm) : "0";
            LiveRpmStale = !on;
            LiveRpmUnit = d.EverOnline ? "rpm" : "";
            LiveStirText = !d.EverOnline ? "尚未收到数据" : stirOn ? (on ? "运行中" : "断线前运行中") : (on ? "已停止" : "断线前已停止");
            StirRunning = on && stirOn;
            StirOn.Reason = c.Why(DeviceAction.StirOn);
            StirOff.Reason = c.Why(DeviceAction.StirOff);
        }
        else
        {
            StirNote = "各釜独立" + m.StirKind + " · 在反应釜上设置";
        }

        // 会话
        var s = d.Source.Session;
        SessionActive = s is { Active: true };
        SessionId = SessionActive ? s!.Id ?? "" : "";
        SessionText = SessionActive
            ? "已 " + Fmt.Minutes(s!.StartAt is { } t ? (c.Now - t).TotalMinutes : null) + " · 标记 " + s.Markers + " 个"
            : "";
        SessionMark.Reason = c.Why(DeviceAction.SessionMark, new ActionPayload(Text: d.Alias + " 标记"));
        SessionEnd.Reason = c.Why(DeviceAction.SessionEnd);
        SessionStart.Reason = c.Why(DeviceAction.SessionStart);

        // 报警（报警 = 错误；提醒 = 提醒级别；标题的计数和列出的卡片一一对应，急停那条也算）
        ShowLog = showLog;
        if (showLog)
        {
            int err = d.Alarms.Count(a => a.IsErr), warn = d.Alarms.Count - err;
            AlarmMeta = ShellText.AlarmText(err, warn, "无");
            var al = d.Alarms.Take(4).ToList();
            ListSync.Sync(Alarms, al, a => a.Id, v => v.Id, a => new InspAlarmVm(a), v => v.Refresh(c));
            HasAlarms = Alarms.Count > 0;
            EventMeta = d.Events.Count + " 条";
            var ev = d.Events.Take(5).ToList();
            ListSync.Sync(Events, ev, e => e.Id, v => v.Id, e => new InspEventVm(e));
            HasEvents = Events.Count > 0;
        }
        else
        {
            if (Alarms.Count > 0) Alarms.Clear();
            if (Events.Count > 0) Events.Clear();
        }
    }

    /// <summary>「设置」链接跳过来：共用转速的输入框（视图把它滚到可见处、聚焦）。</summary>
    public bool CanFocusRpm => SharedStir;
}

/// <summary>
/// 选中了未安装的位置（样稿 inspUninstalled）：反应釜标题（虚线小釜体 · 「1号机 · RV5」· 「未安装」）、说明「RV5 未安装 · 无可用操作」、
/// 「整机操作」小标题，下面是这台设备的检查器（不带标题）。
/// </summary>
public sealed partial class UninstalledInspectorVm : ObservableObject
{
    public UninstalledInspectorVm(ShellVm shell, DeviceVm d, int n)
    {
        D = d;
        N = n;
        Rv = "RV" + n;
        Device = new DeviceInspectorVm(shell, d, hasHead: false);
    }

    public DeviceVm D { get; }
    public int N { get; }
    public string Rv { get; }
    public DeviceInspectorVm Device { get; }

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _nameTip = "";

    internal void Refresh(InspCtx c, bool showLog)
    {
        Alias = D.Alias;
        NameTip = D.Alias + " · " + Rv;
        Sub = D.Model.Label + " · 已装 " + D.Model.InstalledText;
        Note = Rv + " 未安装 · 无可用操作";
        Device.Refresh(c, showLog);
    }
}
