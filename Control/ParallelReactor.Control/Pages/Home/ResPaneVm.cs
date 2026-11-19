using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>共用资源面板的一个阀门小块（样稿 .res .vp）：开 = g-1 底 + g-line 边 + g-ink 600 + 实心绿点；未接 = 虚线边、t-dis。</summary>
public sealed partial class ValvePillVm : ObservableObject
{
    public ValvePillVm(string key) => Key = key;

    public string Key { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _gas = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isNa;
    [ObservableProperty] private string _tip = "";
}

/// <summary>最近事件的一条（样稿 .res .rev li）：圆点（报警红、提醒琥珀、其余灰）· 时刻 hh:mm · 来源（粗体）+ 标题。</summary>
public sealed record ResEventVm(string Time, string Src, string Title, EventLevel Level)
{
    public bool IsErr => Level == EventLevel.Err;
    public bool IsWarn => Level == EventLevel.Warn;
}

/// <summary>
/// 共用资源面板（融合版 F3，样稿 resPane）：气路（占用 / 排队）+ 阀门小块（惰性气 · 气体 A · 气体 B · 排空，只读）· 共用搅拌（釜转速框 + 开关 + 电流 +「全部釜共用」）·
/// 气路小块点击开关阀门（HMI v0.7，<see cref="ValveCtl"/>，不经维护模式）· 看气路图 · 会话 · 最近事件（报警页的「全部」）。
/// </summary>
public sealed partial class ResPaneVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Action _showGas;
    private int _k;

    public ResPaneVm(ShellVm shell, Action showGas)
    {
        _shell = shell;
        _showGas = showGas;
        ShowGasCommand = new RelayCommand(() => _showGas());
        AllEventsCommand = new RelayCommand(() => shell.Go(AppPage.Alarms));
        StirToggleCommand = new RelayCommand(ToggleStir);
        ApplyRpmCommand = new RelayCommand(ApplyRpm);
    }

    public ObservableCollection<ValvePillVm> Valves { get; } = new();
    public ObservableCollection<ResEventVm> Events { get; } = new();
    public ICommand ShowGasCommand { get; }
    public ICommand AllEventsCommand { get; }
    public ICommand StirToggleCommand { get; }
    public ICommand ApplyRpmCommand { get; }

    [ObservableProperty] private string _gasState = "";
    [ObservableProperty] private bool _gasBusy;
    [ObservableProperty] private bool _hasGasState = true;
    /// <summary>v0.8.1：被测试 / 标定挡住排队的程序「排队 RV5」。</summary>
    [ObservableProperty] private string _gasQueue = "";
    [ObservableProperty] private bool _hasGasQueue;
    /// <summary>v0.8.1：触屏手动阀控中（琥珀标签）。</summary>
    [ObservableProperty] private bool _manualValve;
    /// <summary>v0.8.1：搅拌电机故障（搅拌一行下面的红条「电机故障 0x80 · … · 在触屏清除 · 全部釜已停止搅拌」）。</summary>
    [ObservableProperty] private bool _stirFault;
    [ObservableProperty] private string _stirFaultTitle = "";
    [ObservableProperty] private string _stirFaultRest = "";
    [ObservableProperty] private bool _hasStir;
    [ObservableProperty] private bool _stirOn;
    [ObservableProperty] private string _rpmText = "";
    [ObservableProperty] private string _ampText = "";
    [ObservableProperty] private string? _stirReason;
    [ObservableProperty] private string? _toggleReason;
    [ObservableProperty] private string _stirTip = "";
    [ObservableProperty] private string _rpmTip = "";
    [ObservableProperty] private bool _recording;
    [ObservableProperty] private string _sessText = "";
    [ObservableProperty] private string _sessTip = "";
    [ObservableProperty] private bool _hasEvents;
    /// <summary>矮屏（&lt; 820）：节距收紧、不显示最近事件。</summary>
    [ObservableProperty] private bool _isShort;

    /// <summary>用户正在改转速（框里有焦点）：刷新不覆盖。</summary>
    public bool EditingRpm { get; set; }

    private DeviceVm? Device => _k > 0 ? _shell.Fleet.Device(_k) : null;

    /// <summary>气路的说明（样稿 h6 .st）：「空闲 · 无排队」/「RV9 升压中 · 占用」/「RV3 占用 · 排队 RV5、RV6」。</summary>
    public static string GasStateText(int? holder, System.Collections.Generic.IReadOnlyList<int>? queue, string? holderWord)
    {
        var q = queue is { Count: > 0 } ? "排队 " + string.Join("、", queue.Select(n => "RV" + n.ToString(CultureInfo.InvariantCulture))) : "无排队";
        if (holder is not int h) return "空闲 · " + q;
        return "RV" + h.ToString(CultureInfo.InvariantCulture) + " " + (holderWord is { Length: > 0 } w ? w + " · " : "") + "占用" + (queue is { Count: > 0 } ? " · " + q : "");
    }

    /// <summary>
    /// v0.8.1 气路一行（ACT-SPEC §3.2 / §3.3 第 3 条）：泄漏测试 / 标定占用 →（「泄漏测试占用 · RV1–4」, 品牌蓝 600, 「排队 RV5」）；
    /// 手动阀控、没有别的占用 →（空串：只画琥珀标签）；否则同原来的 <see cref="GasStateText"/>（程序的 gasHolder）。
    /// </summary>
    public static (string State, bool Busy, string Queue) GasStateOf(Core.Activity.DeviceActivity? act, Core.Activity.DeviceMarks m, int? holder, System.Collections.Generic.IReadOnlyList<int>? queue, string? holderWord)
    {
        var q = queue is { Count: > 0 } ? "排队 " + string.Join("、", queue.Select(n => "RV" + n.ToString(CultureInfo.InvariantCulture))) : "";
        if (m.GasOwner == "leak" || act is { Live: true, Kind: Core.Activity.ActKind.Leak })
            return ("泄漏测试占用" + (act is { Kind: Core.Activity.ActKind.Leak } a && a.Chs.Count > 0 ? " · " + Core.Activity.ActivityText.Range(a.Chs) : ""), true, q);
        if (m.GasOwner == "calib" || act is { Live: true, Kind: Core.Activity.ActKind.Calib })
            return ("容积标定占用" + (act is { Kind: Core.Activity.ActKind.Calib, Channel: int c } ? " · RV" + c.ToString(CultureInfo.InvariantCulture) : ""), true, q);
        if (m.ManualValve && holder is null) return ("", false, "");
        return (GasStateText(holder, queue, holderWord), holder != null, "");
    }

    /// <summary>
    /// 阀门小块的文字：只写气路名（惰性气 / 气体 A / 气体 B），不写气体种类——接什么气由工艺决定，
    /// HMI 也不报告实际接的气（2026-11-18 用户确认）。不再猜「未接」。
    /// </summary>
    public static (string Name, string Gas, bool Na) InletText(string key, string name) => key switch
    {
        "inert" => ("惰性气", "", false),
        _ => (name, "", false),
    };

    /// <summary>点气路小块：开关这一路的阀（HMI v0.7）。</summary>
    public void TogglePill(ValvePillVm v)
    {
        if (Device is { } d && !v.IsNa) ValveCtl.ToggleGas(_shell, d, v.Key, v.IsOn);
    }

    internal void Refresh(DeviceVm d)
    {
        _k = d.K;
        var src = d.Source;
        var plant = src.Plant;
        var m = d.Model;

        // 气路：先看 gas.owner（v0.8.1：泄漏测试 / 标定占用时不再写「空闲」，排队的另写「排队 RV5」），再看程序的 gasHolder；触屏手动阀控时琥珀标签
        var marks = d.Marks;
        var (gs, busy, gq) = GasStateOf(d.Act, marks, plant.GasHolder, plant.GasQueue,
            plant.GasHolder is int hn && d.Reactor(hn) is { } hr ? RunRowText.Word(hr.Own, hr.Cause, hr.OwnWord) : null);
        GasState = gs;
        GasBusy = busy;
        GasQueue = gq;
        HasGasQueue = gq.Length > 0;
        HasGasState = gs.Length > 0;
        ManualValve = marks.ManualValve;
        StirFault = marks.HasStirFault;
        StirFaultTitle = "电机故障" + (marks.StirCodeText is { } sc ? " " + sc : "");
        StirFaultRest = (marks.StirFault is { Length: > 0 } fn ? " · " + fn : "") + " · 在触屏清除 · 全部釜已停止搅拌";
        int i = 0;
        foreach (var g in m.GasInlets)
        {
            var (nm, gas, na) = InletText(g.Key, g.Name);
            var v = i < Valves.Count && Valves[i].Key == g.Key ? Valves[i] : null;
            if (v == null) { v = new ValvePillVm(g.Key); if (i < Valves.Count) Valves[i] = v; else Valves.Add(v); }
            v.Name = nm; v.Gas = gas; v.IsNa = na; v.IsOn = src.GasOpen(g.Key);
            // 悬停：气路名 · 接什么气由工艺决定 · 开 / 关 · 点击开关（HMI v0.7，不经维护模式）
            v.Tip = ValveCtl.Tip(g.Name + " · " + DevicePaneVm.GasSourceNote, v.IsOn, ValveCtl.GasReason(d, g.Key, v.IsOn));
            i++;
        }
        {
            var key = m.Vent?.Key ?? "vent";
            var v = i < Valves.Count && Valves[i].Key == key ? Valves[i] : null;
            if (v == null) { v = new ValvePillVm(key); if (i < Valves.Count) Valves[i] = v; else Valves.Add(v); }
            v.Name = "排空"; v.Gas = ""; v.IsNa = false; v.IsOn = src.GasOpen(key);
            v.Tip = ValveCtl.Tip("排空 / 泄压阀", v.IsOn, ValveCtl.GasReason(d, key, v.IsOn));
            i++;
        }
        while (Valves.Count > i) Valves.RemoveAt(Valves.Count - 1);

        // 共用搅拌（釜转速）
        var stir = d.Stir;
        HasStir = stir != null;
        if (stir != null)
        {
            StirOn = stir.On;
            if (!EditingRpm) RpmText = Math.Round(stir.Rpm).ToString(CultureInfo.InvariantCulture);
            AmpText = (stir.On && plant.StirCurrentA is double a ? a : 0).ToString("0.0", CultureInfo.InvariantCulture) + " A";
            var pay = new ActionPayload(Rpm: Math.Round(stir.Rpm));
            // 融合版 F8（样稿 stirCtl：本地只读时开关灰掉）：没有控制权时开、关都不让点（停搅拌本来不需要控制权，用户决定照样稿；全部停止照样会停搅拌）
            var why = StirLockReason(d) ?? d.Actions.Why(stir.On ? DeviceAction.StirOff : DeviceAction.StirOn, pay);
            var whySet = d.Actions.Why(DeviceAction.StirSet, pay);
            ToggleReason = why.Length > 0 ? why : null;
            StirReason = whySet.Length > 0 ? whySet : null;
            StirTip = why.Length > 0 ? why : stir.On ? "搅拌运行中 · 点击停止（全部釜）" : "搅拌已停 · 点击开启（全部釜）";
            RpmTip = whySet.Length > 0 ? whySet : "釜转速（全部釜共用）· 0 – " + RpmMax(d).ToString(CultureInfo.InvariantCulture) + " rpm · 回车写入";
        }

        // 会话
        Recording = d.SessionId != null;
        SessText = Recording ? "记录中 · " + d.SessionId : "未记录";
        SessTip = Recording && src.Session is { StartAt: { } at } ? d.SessionId + " · " + at.ToString("HH:mm", CultureInfo.InvariantCulture) + " 起" : "";

        // 最近事件（最多 6 条，最新在前）
        var evs = d.Events.Take(6).Select(e => Event(e)).ToList();
        if (Events.Count != evs.Count || !Events.SequenceEqual(evs))
        {
            Events.Clear();
            foreach (var e in evs) Events.Add(e);
        }
        HasEvents = Events.Count > 0;
    }

    /// <summary>事件 → 来源 + 标题：反应釜事件「RV4」+ 去掉开头 RVn 的文字；整机事件按第一个「 · 」拆（「控制权 · PC-测试工位1 获得远程控制」）。</summary>
    public static ResEventVm Event(EventVm e)
    {
        string src, title;
        if (e.N is int n)
        {
            src = "RV" + n.ToString(CultureInfo.InvariantCulture);
            title = e.Text.StartsWith(src + " ", StringComparison.Ordinal) ? e.Text[(src.Length + 1)..] : e.Text;
        }
        else
        {
            int i = e.Text.IndexOf(" · ", StringComparison.Ordinal);
            src = i > 0 ? e.Text[..i] : "整机";
            title = i > 0 ? e.Text[(i + 3)..] : e.Text;
        }
        return new ResEventVm(e.At.ToString("HH:mm", CultureInfo.InvariantCulture), src, title, e.Level);
    }

    /// <summary>共用搅拌开关的「本地只读」原因（在线、但本机没有控制权）；能点时为 null（断线等由守卫说）。</summary>
    public static string? StirLockReason(DeviceVm d) =>
        !d.IsOffline && d.Lease != Controls.Pr.LeaseState.Remote ? d.Alias + "为本地只读 · 先申请控制权" : null;

    /// <summary>釜转速上限（设备设置；没有时 450）。</summary>
    public static double RpmMax(DeviceVm d) => d.Source.Plant.RpmMax ?? 450;

    private void ToggleStir()
    {
        if (Device is not { } d || d.Stir is not { } s) return;
        if (StirLockReason(d) is { } why) { _shell.Toast(why, Services.ToastKind.Warn); return; }
        // 关搅拌是停止类：先确认（F9，「停止一律先确认」；一台电机带全部釜，关掉就全停）；开搅拌直接发
        if (s.On) _shell.ShowDialog(Shell.Dialogs.StopConfirmDialogVm.StirOff(_shell, d, () => StirLockReason(d)));
        else _ = d.Actions.RunAsync(DeviceAction.StirOn, new ActionPayload(Rpm: s.Rpm));
    }

    private void ApplyRpm()
    {
        EditingRpm = false;
        if (Device is not { } d || d.Stir is not { } s) return;
        if (!double.TryParse(RpmText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { RpmText = Math.Round(s.Rpm).ToString(CultureInfo.InvariantCulture); return; }
        double max = RpmMax(d);
        v = Math.Clamp(Math.Round(v / 10) * 10, 0, max);
        RpmText = v.ToString(CultureInfo.InvariantCulture);
        if (Math.Abs(v - s.Rpm) < 0.5) return;
        _ = d.Actions.RunAsync(DeviceAction.StirSet, new ActionPayload(Rpm: v));
    }
}
