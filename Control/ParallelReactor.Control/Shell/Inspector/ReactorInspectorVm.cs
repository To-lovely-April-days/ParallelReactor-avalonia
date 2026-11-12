using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 反应釜检查器（样稿 shell.js inspReactor）。最要紧的放最上面：对象标题（小釜体 · 「1号机 · RV1」· 状态点 + 字；型号 · 釜）→ 急停中红框 →
/// 这个反应釜未确认的报警（带确认；页面上已经有这条报警的「确认」时不再列出）→ 控制权卡 → 单台急停。然后分区：
/// 运行（启用 / 停用、启动 / 停止）、加热（设定温度 + 设定、当前温度 / 加热输出、启动 / 停止加热）、搅拌（独立型号：本釜转速；
/// 共用型号：一行说明 +「设置」跳到设备检查器的共用转速）、程序（进度卡 + 步骤条、程序下拉 + 下发、运行 / 中止、编辑程序）、记录（打标记，
/// 或「会话在设备上开始 · 去1号机」）。启动类操作「已经是这个状态」时画状态格（已启用、反应中 / 报警暂停、加热中、搅拌中、程序运行中），
/// 不画灰按钮；停止类按钮永远照常可用（只要连接）。
/// </summary>
public sealed partial class ReactorInspectorVm : ObservableObject
{
    private readonly ShellVm _shell;
    private bool _spDirty, _rpmDirty, _progDirty, _setting;
    private IReadOnlyList<ProgramChoice>? _choices;

    public ReactorInspectorVm(ShellVm shell, DeviceVm d, ReactorVm r)
    {
        _shell = shell;
        D = d;
        R = r;
        N = r.N;
        Rv = r.Rv;
        Lease = new LeaseBoxVm(shell);
        var P = ActionPayload.Ch(N);
        Estop = new InspAct("急停", "estop", () => Act(DeviceAction.Estop));
        Enable = new InspAct("启用", "power", () => Act(DeviceAction.Enable, P));
        Disable = new InspAct("停用", "stop", () => Act(DeviceAction.Disable, P));
        Start = new InspAct("启动", "play", () => Act(DeviceAction.Start, new ActionPayload(N: N, Sp: SpValue)));
        Stop = new InspAct("停止", "stop", () => Act(DeviceAction.Stop, P));
        SetSp = new InspAct("设定", null, () => { var sp = SpValue; _spDirty = false; Act(DeviceAction.SetSp, new ActionPayload(N: N, Sp: sp)); });
        HeatStart = new InspAct("启动加热", "flame", () => { var sp = SpValue; _spDirty = false; Act(DeviceAction.HeatStart, new ActionPayload(N: N, Sp: sp)); });
        HeatStop = new InspAct("停止加热", "stop", () => Act(DeviceAction.HeatStop, P));
        ChStirSet = new InspAct("设定", null, () => { var rpm = RpmValue; _rpmDirty = false; Act(DeviceAction.ChStirSet, new ActionPayload(N: N, Rpm: rpm)); });
        ChStirOn = new InspAct("启动搅拌", "fan", () => { var rpm = RpmValue; _rpmDirty = false; Act(DeviceAction.ChStirOn, new ActionPayload(N: N, Rpm: rpm)); });
        ChStirOff = new InspAct("停止搅拌", "stop", () => Act(DeviceAction.ChStirOff, P));
        ProgLoad = new InspAct("下发", "send", () => { var id = SelectedProgram?.Id; _progDirty = false; Act(DeviceAction.ProgLoad, new ActionPayload(N: N, Id: id)); });
        ProgRun = new InspAct("运行", "play", () => Act(DeviceAction.ProgRun, P));
        ProgAbort = new InspAct("中止", "stop", () => Act(DeviceAction.ProgAbort, P));
        EditProgram = new InspAct("编辑程序", "edit", () => shell.Navigate(Route.Placeholder("program")));
        Mark = new InspAct("打标记", "flag", () => Act(DeviceAction.SessionMark, new ActionPayload(N: N, Text: Rv + " 标记")));
        StirLink = new InspAct("设置", null, () => { shell.Select(D.K); shell.Inspector.RequestFocus("rpm"); });
        GoDevice = new InspAct("去" + d.Alias, null, () => shell.Select(D.K));
    }

    public DeviceVm D { get; }
    public ReactorVm R { get; }
    public int N { get; }
    public string Rv { get; }
    public LeaseBoxVm Lease { get; }

    public InspAct Estop { get; }
    public InspAct Enable { get; }
    public InspAct Disable { get; }
    public InspAct Start { get; }
    public InspAct Stop { get; }
    public InspAct SetSp { get; }
    public InspAct HeatStart { get; }
    public InspAct HeatStop { get; }
    public InspAct ChStirSet { get; }
    public InspAct ChStirOn { get; }
    public InspAct ChStirOff { get; }
    public InspAct ProgLoad { get; }
    public InspAct ProgRun { get; }
    public InspAct ProgAbort { get; }
    public InspAct EditProgram { get; }
    public InspAct Mark { get; }
    public InspAct StirLink { get; }
    public InspAct GoDevice { get; }

    // —— 对象标题 ——
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _subTip = "";
    [ObservableProperty] private StateKey _key;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private bool _neverConnected;

    // —— 急停 / 报警 ——
    [ObservableProperty] private bool _estopOn;
    [ObservableProperty] private string _estopText = "";
    [ObservableProperty] private string _estopWhere = "";
    [ObservableProperty] private string _estopNote = "";
    [ObservableProperty] private string _estopNoteTip = "";
    /// <summary>这个反应釜未确认的报警（标题下面，带确认；页面上已有确认的不列出）。</summary>
    public ObservableCollection<InspAlarmVm> TopAlarms { get; } = new();
    [ObservableProperty] private bool _hasTopAlarms;

    // —— 运行 ——
    [ObservableProperty] private string? _runMeta;
    /// <summary>「已启用」状态格（在线且不是停用）。</summary>
    [ObservableProperty] private bool _enabledCell;
    /// <summary>运行状态格（在线且在运行）：报警时「报警暂停」（暂停图标），否则状态字 + 状态点。</summary>
    [ObservableProperty] private bool _runCell;
    [ObservableProperty] private bool _runCellHeld;
    [ObservableProperty] private string _runCellText = "";
    [ObservableProperty] private string? _runCellTip;

    // —— 加热 ——
    [ObservableProperty] private string _heatMeta = "";
    [ObservableProperty] private double? _spValue;
    [ObservableProperty] private double _spMax;
    [ObservableProperty] private string? _heatReason;
    [ObservableProperty] private string _tempNow = "";
    [ObservableProperty] private string _heaterNow = "";
    [ObservableProperty] private bool _heatingCell;

    // —— 搅拌 ——
    [ObservableProperty] private bool _independent;
    [ObservableProperty] private string _stirMeta = "";
    [ObservableProperty] private double? _rpmValue;
    [ObservableProperty] private double _rpmMin;
    [ObservableProperty] private double _rpmMax;
    [ObservableProperty] private string? _rpmReason;
    [ObservableProperty] private bool _stirCell;
    [ObservableProperty] private string? _stirCellTip;
    /// <summary>共用搅拌型号的一行说明：「全釜共用磁力搅拌 · 300 rpm · 运行中」。</summary>
    [ObservableProperty] private string _stirNote = "";
    [ObservableProperty] private string _stirLinkTip = "";

    // —— 程序 ——
    [ObservableProperty] private string? _progMeta;
    [ObservableProperty] private bool _hasProgram;
    [ObservableProperty] private string _progName = "";
    [ObservableProperty] private string _progText = "";
    [ObservableProperty] private IReadOnlyList<StepSeg>? _steps;
    /// <summary>进度卡里画步骤条（本机知道步骤的程序）；真机对不上台账的程序只有 k/n，不画。</summary>
    [ObservableProperty] private bool _hasSteps;
    /// <summary>程序名的悬停（对不上台账时是整行「RV3 程序 · 第 2/5 步 · 消息」）。</summary>
    [ObservableProperty] private string _progTip = "";
    /// <summary>真机对不上台账的程序：设备报的消息（「充压 1.2 / 3.5 MPa」，运行中才有），写在步骤条的位置。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHmiMessage))] private string? _hmiMessage;
    public bool HasHmiMessage => !string.IsNullOrEmpty(HmiMessage);
    [ObservableProperty] private bool _stepsRunning;
    [ObservableProperty] private string? _waitText;
    /// <summary>等待说明前的图标：演示「hand」（去触屏开阀）；真机「clock」（排队等共用气路）。</summary>
    [ObservableProperty] private string _waitIcon = "hand";
    [ObservableProperty] private IReadOnlyList<ProgramChoice> _programs = Array.Empty<ProgramChoice>();
    [ObservableProperty] private ProgramChoice? _selectedProgram;
    [ObservableProperty] private string? _progReason;
    /// <summary>程序运行中 / 报警暂停（状态格）。</summary>
    [ObservableProperty] private bool _progCell;
    [ObservableProperty] private bool _progCellHeld;
    [ObservableProperty] private string _progCellText = "";

    // —— 记录 ——
    [ObservableProperty] private bool _sessionActive;
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _recNote = "";

    [ObservableProperty] private bool _narrow;

    partial void OnSpValueChanged(double? value) { if (!_setting) _spDirty = true; }
    partial void OnRpmValueChanged(double? value) { if (!_setting) _rpmDirty = true; }
    partial void OnSelectedProgramChanged(ProgramChoice? value) { if (!_setting) _progDirty = true; }

    /// <summary>重新选中这个反应釜：输入框 / 下拉回到当前值（同样稿换对象时整块重画）；程序库重新读一次。</summary>
    internal void ResetInputs()
    {
        _spDirty = _rpmDirty = _progDirty = false;
        _choices = null;
    }

    private void Act(DeviceAction a, ActionPayload? p = null) => _ = D.Actions.RunAsync(a, p);

    /// <summary>HMI 的程序进度 → 接口的快照（降级文字 HmiProgramText 用它）。</summary>
    private static ParallelReactor.Api.ChannelProgramSnap Snap(HmiProgram h) =>
        new(h.Running, h.StepIndex, h.StepCount, h.State, h.Message, h.StartedBy, h.StartedAt, h.WaitingGas);

    private void SetQuiet(Action a)
    {
        _setting = true;
        try { a(); } finally { _setting = false; }
    }

    internal void Refresh(InspCtx c, bool pageOwnsTop)
    {
        var d = D;
        var r = R;
        var m = d.Model;
        var lim = m.Limits;
        bool on = c.Online;
        int n = N;
        var P = ActionPayload.Ch(n);
        Narrow = c.Narrow;
        var prog = d.Source.Program(n);
        var pr = r.Program?.Progress;

        // 标题
        Alias = d.Alias;
        var sub = m.Label + " · " + m.Vessel.Kind + (on || !d.EverOnline ? "" : " · 断线前" + r.OwnWord);
        Sub = sub;
        SubTip = sub;
        Key = r.Key;
        Word = r.Word;
        NeverConnected = !d.EverOnline;

        // 急停 → 报警 → 控制权 → 单台急停
        EstopOn = d.EstopActive;
        EstopText = d.EstopText;
        EstopWhere = d.Alias + "触屏";
        EstopNote = "只急停" + d.Alias + " · 复位在触屏";
        EstopNoteTip = "只急停" + d.Alias + " · 复位只能在触屏";
        Estop.Reason = c.Why(DeviceAction.Estop);
        Estop.Tip = "只急停" + d.Alias + " · 切断加热 / 进气 / 搅拌 · 复位只能在触屏";
        // 打开的页签就是这个反应釜、页面顶部有它的报警条（含唯一的「确认」）时，检查器不再重复这条报警
        var top = d.Alarms.Where(a => a.Ch == n && !a.Acked && !(pageOwnsTop && c.OwnedWhere(a) != null)).ToList();
        ListSync.Sync(TopAlarms, top, a => a.Id, v => v.Id, a => new InspAlarmVm(a), v => v.Refresh(c));
        HasTopAlarms = TopAlarms.Count > 0;
        Lease.Refresh(c);

        // 运行（已启用 / 正在运行时显示状态格，不画灰按钮；报警暂停是中性的「报警暂停」格 —— 红色只在状态字上）
        bool running = r.IsRunning;
        var own = r.Own;
        EnabledCell = on && own != StateKey.Idle;
        RunMeta = EnabledCell ? null : own == StateKey.Idle ? "已停用" : "已启用";
        RunCell = on && running;
        RunCellHeld = own == StateKey.Alarm;
        RunCellText = RunCellHeld ? "报警暂停" : r.OwnWord;
        RunCellTip = RunCellHeld ? "程序因报警暂停 · 先处理报警（停止类操作照常可用）" : null;
        Enable.Reason = c.Why(DeviceAction.Enable, P);
        Disable.Reason = c.Why(DeviceAction.Disable, P);
        Start.Reason = c.Why(DeviceAction.Start, P);
        Stop.Reason = c.Why(DeviceAction.Stop, P);

        // 加热：设定值 = 当前 SP，没有时程序第一步的设定值，再没有 80
        double sp = r.SpC ?? prog?.Steps.FirstOrDefault()?.Sp ?? 80;
        HeatMeta = "量程 ≤ " + JsNumber.ToString(lim.TempMaxC) + " ℃";
        SpMax = lim.TempMaxC;
        if (!_spDirty) SetQuiet(() => SpValue = sp);
        var ctl = c.Guard(GuardKind.Control);
        HeatReason = ctl;
        SetSp.Reason = c.Why(DeviceAction.SetSp, new ActionPayload(N: n, Sp: sp));
        TempNow = Fmt.Temp(r.TempC);
        HeaterNow = Fmt.Pct(r.HeaterPct);
        HeatingCell = on && r.Heating;
        HeatStart.Reason = c.Why(DeviceAction.HeatStart, new ActionPayload(N: n, Sp: sp));
        HeatStop.Reason = c.Why(DeviceAction.HeatStop, P);

        // 搅拌
        Independent = m.StirMode == StirMode.Independent;
        var ex = r.Extras;
        if (Independent)
        {
            // 转速框、「设定」「启动搅拌」用设定转速（样稿 ch.rpm）：搅拌关着时有效转速是 0，拿它就会按 0 rpm 启动；
            // 没有设定值的来源（真机）才用读数。「搅拌中」的悬停写有效转速
            double? rpmNow = ex?.Rpm ?? r.Rpm;
            double? rpm = ex?.RpmSp ?? rpmNow;
            StirMeta = JsNumber.ToString(lim.RpmMin) + "–" + JsNumber.ToString(lim.RpmMax) + " rpm";
            RpmMin = lim.RpmMin;
            RpmMax = lim.RpmMax;
            if (!_rpmDirty) SetQuiet(() => RpmValue = rpm);
            RpmReason = ctl;
            ChStirSet.Reason = c.Why(DeviceAction.ChStirSet, new ActionPayload(N: n, Rpm: rpm));
            bool stirOn = ex?.StirOn ?? false;
            StirCell = on && stirOn;
            StirCellTip = "本釜搅拌 " + Fmt.Rpm(rpmNow) + " rpm";
            ChStirOn.Reason = c.Why(DeviceAction.ChStirOn, P);
            ChStirOff.Reason = c.Why(DeviceAction.ChStirOff, P);
        }
        else
        {
            // 「全釜共用磁力搅拌 · 400 rpm · 运行中」只在「 · 」后面断行；「设置」= 选中这台设备并定位到共用搅拌的转速框
            var stir = d.Stir;
            var t = "全釜共用" + m.StirKind + " · " + (!d.EverOnline ? "尚未收到数据"
                : stir is { On: true } s ? Fmt.Rpm(s.Rpm) + " rpm · " + (on ? "运行中" : "断线前运行中") : "已停止");
            StirNote = t;
            StirLinkTip = "在" + d.Alias + "的检查器里设置共用转速";
        }

        // 程序
        _choices ??= d.Source.ProgramChoices(n);
        if (!ReferenceEquals(Programs, _choices)) SetQuiet(() => Programs = _choices);
        var curId = prog?.Id ?? _choices.FirstOrDefault()?.Id;
        if (!_progDirty || SelectedProgram == null || !_choices.Contains(SelectedProgram))
            SetQuiet(() => SelectedProgram = _choices.FirstOrDefault(x => x.Id == curId) ?? _choices.FirstOrDefault());
        // 真机：设备上有程序、但台账对不上（不是本机下发的 / 已作废）—— 同反应釜页与总览卡片，只写设备报的 k/n、状态与消息（§3.4 / R5），不画步骤条
        var hmi = prog is null && !d.Source.IsDemo && r.Hmi is { StepCount: > 0 } h0 ? h0 : null;
        ProgMeta = pr != null ? (pr.Offline ? "离线" : pr.WaitGas ? pr.WaitWord : pr.StatusText)
            : hmi != null ? (!on ? "离线" : hmi is { Running: true, WaitingGas: true } ? StateMap.WaitGasWord : Progress.StatusText(LiveDeviceSource.HmiProgramStatus(Snap(hmi))))
            : null;
        HasProgram = pr != null || hmi != null;
        if (pr != null && prog != null)
        {
            ProgName = pr.Name;
            ProgTip = pr.Name;
            ProgText = Progress.Text(pr);
            Steps = StepSegs.Update(Steps, Progress.Bar(prog, pr, on));
            HasSteps = true;
            HmiMessage = null;
            StepsRunning = on && prog.Status == ProgramStatus.Running && !pr.WaitGas;
            // 等待进气：演示 = 样稿（进气口关闭，去触屏打开）；真机 = 排队等共用气路「等待气路（RV3 占用中）」（§3.5 / R6）
            WaitText = !pr.WaitGas ? null
                : d.Source.IsDemo ? pr.WaitWord + " · " + pr.GasName + " 进气口关闭 · 需在" + d.Alias + "触屏打开"
                : StateMap.WaitGasText(d.GasHolder) + " · 第 " + pr.K + "/" + pr.N + " 步「" + pr.StepName + "」暂停计时";
            WaitIcon = d.Source.IsDemo ? "hand" : "clock";
        }
        else if (hmi != null)
        {
            var snap = Snap(hmi);
            ProgName = HmiProgramText.Name(n);
            ProgText = HmiProgramText.Steps(snap);
            ProgTip = HmiProgramText.Full(n, snap);
            Steps = null;
            HasSteps = false;
            bool active = hmi.State is ParallelReactor.Api.ChannelProgramStates.Preparing or ParallelReactor.Api.ChannelProgramStates.Running
                or ParallelReactor.Api.ChannelProgramStates.WaitingGas or ParallelReactor.Api.ChannelProgramStates.Finishing;
            HmiMessage = active && !string.IsNullOrWhiteSpace(hmi.Message) ? hmi.Message.Trim() : null;
            StepsRunning = false;
            WaitText = on && hmi is { Running: true, WaitingGas: true }
                ? StateMap.WaitGasText(d.GasHolder) + " · 第 " + Math.Clamp(hmi.StepIndex + 1, 1, hmi.StepCount) + "/" + hmi.StepCount + " 步暂停计时" : null;
            WaitIcon = "clock";
        }
        else
        {
            ProgName = "";
            ProgTip = "";
            ProgText = "";
            Steps = null;
            HasSteps = false;
            HmiMessage = null;
            StepsRunning = false;
            WaitText = null;
        }
        ProgReason = ctl;
        ProgLoad.Reason = c.Why(DeviceAction.ProgLoad, new ActionPayload(N: n, Id: curId));
        bool busy = on && prog is { Status: ProgramStatus.Running or ProgramStatus.Held };
        ProgCell = busy;
        ProgCellHeld = prog?.Status == ProgramStatus.Held;
        ProgCellText = ProgCellHeld ? "报警暂停" : "程序运行中";
        ProgRun.Reason = c.Why(DeviceAction.ProgRun, P);
        ProgAbort.Reason = c.Why(DeviceAction.ProgAbort, P);

        // 记录：有记录中的会话才放「打标记」；没有时一行说明 + 去设备（会话在设备上开始）
        var sess = d.Source.Session;
        SessionActive = sess is { Active: true };
        SessionId = SessionActive ? sess!.Id ?? "" : "";
        Mark.Reason = c.Why(DeviceAction.SessionMark, new ActionPayload(N: n, Text: Rv + " 标记"));
        RecNote = "会话在设备上开始";
        GoDevice.Label = "去" + d.Alias;
    }
}
