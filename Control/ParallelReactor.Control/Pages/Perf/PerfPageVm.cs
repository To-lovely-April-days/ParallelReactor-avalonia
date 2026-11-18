using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Client;
using LeaseState = ParallelReactor.Control.Controls.Pr.LeaseState;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Records;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.ViewModels;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.Control.Pages.Perf;

/// <summary>
/// 性能测试页（融合版 F8，样稿 PAGES.test）：左「测试项 · 首台摸底」400（窄屏 340：摸底测试 / 泄漏测试分段、全选 / 清空、测试通道、按阶段分组的测试项）|
/// 右上「进度 · 会话 …」（总进度条 + 已用 / 预计还要、当前一步的卡片：人工录入 / 手动确认 / 安全暂停、实时、说明）| 右下「结果」230（矮屏 170）。
/// 泄漏测试（<see cref="IsLeak"/>）：左「泄漏测试」（参数来自设备设置 · 只读、阶段、通道）| 右「结果 · 最近一次」。
/// <list type="bullet">
/// <item>引擎不动：摸底测试 = PerfTest 库的 PerfRunner，经 <see cref="PerfViewModel"/>（确认 / 录入 / 安全询问、报告）；这一页只是把它排成新版面。
///   测试的设备 = 工具条选的那台（实时设备；测试开始后锁定那台，<see cref="IPerfHost.Connection"/>）。</item>
/// <item>计划只适用 HT-PR8 的 RV1–RV4（样稿 ptOk）；别的型号写「没有对应的测试计划」，泄漏测试照样能用。</item>
/// <item>泄漏测试：参数 = 设备设置 leakTest.*（<see cref="LeakTestInfo"/>，阈值同记录页保压类）；开始 = POST /leak-tests（要控制权），中止不需要；
///   结果 = GET /leak-tests 最新一次的汇总。</item>
/// <item>演示数据：照样稿的状态（P1 第 4 / 11 点人工录入、结果表、泄漏测试结果），按钮只提示。</item>
/// <item>记录页「再做一次泄漏测试」（<see cref="IRedoTarget"/>）：切到泄漏测试、预选那次的空闲通道。</item>
/// </list>
/// </summary>
public sealed partial class PerfPageVm : PageVm, IPerfHost, IRedoTarget
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ShellVm _shell;
    private readonly RibbonTool _run, _skip, _abort, _report, _folder, _lkStart, _lkAbort, _lkExport;
    private readonly List<RibbonGroup> _toolsPerf, _toolsLeak;
    private TelemetrySnapshot? _lastFrame;
    private DeviceInfoDto? _lastInfo;
    /// <summary>测试开始时锁定的设备（跑完前不跟着工具条换）。</summary>
    private int _runK;
    /// <summary>演示数据的状态（样稿 PT：运行 / 暂停 / 中止、录入的读数）。</summary>
    private readonly PerfDemo _demo = new();

    public PerfPageVm(ShellVm shell) : base(AppPage.Perf)
    {
        _shell = shell;
        Perf = new PerfViewModel(this, LoadPlanSafe());
        _run = new RibbonTool("f-play", "开始测试", () => _ = RunOrPauseAsync(), tip: "开始 / 继续测试");
        _skip = new RibbonTool("f-next", "跳过本步", SkipStep, tip: "跳过当前等待确认 / 录入的步骤，结果记为「跳过」");
        _abort = new RibbonTool("f-stop", "中止", Abort, tip: "中止测试：停加热、关阀门 · 不需要控制权");
        _report = new RibbonTool("f-pdf", "生成报告", Report, tip: "生成报告（HTML + 汇总 CSV）");
        _folder = new RibbonTool("f-folder", "打开文件夹", OpenFolder, tip: "打开这次测试的输出目录");
        _toolsPerf = new List<RibbonGroup> { new("测试", new[] { _run, _skip, _abort }), new("报告", new[] { _report, _folder }) };
        _lkStart = new RibbonTool("f-play", "开始泄漏测试", () => _ = StartLeakAsync(), tip: "对选中的通道做泄漏测试", primary: true);
        _lkAbort = new RibbonTool("f-stop", "中止", () => _ = AbortLeakAsync(), tip: "中止泄漏测试 · 不需要控制权");
        _lkExport = new RibbonTool("f-down", "导出结果", () => ExportLeak(), tip: "导出最近一次的泄漏测试结果（CSV）");
        _toolsLeak = new List<RibbonGroup> { new("泄漏测试", new[] { _lkStart, _lkAbort }), new("报告", new[] { _lkExport }) };

        SelectAllCommand = new RelayCommand(() => { Perf.SelectAll(); RefreshItems(); });
        ClearCommand = new RelayCommand(() => { foreach (var i in Perf.AllItems) if (!IsLockedItem(i.Item.Id)) i.IsChecked = false; RefreshItems(); });
        ConfirmCommand = new RelayCommand(Confirm);
        SkipPromptCommand = new RelayCommand(SkipStep);
        OpenReportCommand = new RelayCommand(() => Perf.OpenReport());
        BackCommand = new RelayCommand(() => { Perf.ResetView(); Refresh(_shell.Fleet); });

        BuildGroups();
        foreach (var it in Perf.AllItems) it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerfItemVm.IsChecked)) RefreshSelText(); };
        Perf.PropertyChanged += (_, _) => { if (ReferenceEquals(_shell.Page, this)) RefreshRun(); };
        Perf.EntryFields.CollectionChanged += (_, _) => RefreshRun();
        Perf.ChecklistItems.CollectionChanged += (_, _) => RefreshRun();
        Perf.ReportRows.CollectionChanged += (_, _) => RefreshResults();
        Perf.Rows.CollectionChanged += (_, _) => RefreshItemStates();
    }

    /// <summary>性能测试的视图模型（引擎、确认 / 录入、报告；PerfTest 库不动）。</summary>
    public PerfViewModel Perf { get; }

    public override IReadOnlyList<RibbonGroup> Tools => IsLeak ? _toolsLeak : _toolsPerf;

    /// <summary>测试在跑（关窗前要先中止，ShellVm.ShutdownAsync）。</summary>
    public bool IsBusy => Perf.IsRunning;

    // ———————————————————— IPerfHost ————————————————————

    /// <summary>要测的设备的连接：测试在跑时是开始时那台，否则工具条选的那台（实时设备；演示为 null）。</summary>
    public IReactorConnection? Connection => (_shell.Fleet.Device(Perf.IsRunning && _runK > 0 ? _runK : _shell.Nav.DeviceK)?.Source as LiveDeviceSource)?.Connection;
    public string ClientName => _shell.Fleet.ClientName;
    public bool IsVisible => ReferenceEquals(_shell.Page, this);
    public void Toast(string text) => _shell.Toast(text, ToastKind.Info);

    /// <summary>方案加载失败（外部 JSON 坏了）时退回内置方案并提示。</summary>
    private TestPlan LoadPlanSafe()
    {
        try { return PerfViewModel.LoadDefaultPlan(); }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Toast("性能测试方案加载失败，已用内置方案：" + ex.Message));
            return PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        }
    }

    // ———————————————————— 设备 / 模式 ————————————————————

    public DeviceVm? Dev => _shell.Fleet.Device(Perf.IsRunning && _runK > 0 ? _runK : _shell.Nav.DeviceK);

    /// <summary>摸底测试 / 泄漏测试（样稿 PT.mode）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLeak), nameof(IsPerfMode))] private string _modeKey = "perf";
    public bool IsLeak => ModeKey == "leak";
    public bool IsPerfMode => !IsLeak;

    partial void OnModeKeyChanged(string value)
    {
        RaiseToolsChanged();
        Refresh(_shell.Fleet);
    }

    /// <summary>这台设备有测试计划（样稿 ptOk：只适用 HT-PR8）。</summary>
    [ObservableProperty] private bool _planOk = true;
    [ObservableProperty] private string _noPlanTitle = "";
    [ObservableProperty] private string _devSub = "";
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _isShort;

    public static bool PlanApplies(DeviceVm d) => d.Model.Key == "PR8";

    private bool CanControl(DeviceVm d) => !d.IsOffline && d.Lease == LeaseState.Remote;

    private string? ControlReason(DeviceVm? d) => d == null ? "先选一台设备"
        : d.IsOffline ? d.Alias + "未连接"
        : d.Lease != LeaseState.Remote ? d.Alias + "为本地只读 · 先申请控制权" : null;

    // ———————————————————— 测试项 ————————————————————

    public ObservableCollection<PtGroupVm> Groups { get; } = new();
    public ObservableCollection<PtChanVm> Chans { get; } = new();
    public ICommand SelectAllCommand { get; }
    public ICommand ClearCommand { get; }
    [ObservableProperty] private string _chanNote = "RV5–RV8 未安装";
    [ObservableProperty] private string _selText = "";

    private readonly Dictionary<string, PtItemVm> _items = new(StringComparer.OrdinalIgnoreCase);

    private void BuildGroups()
    {
        var stages = Perf.Plan.Stages;
        for (int g = 0; g < Perf.Groups.Count; g++)
        {
            var src = Perf.Groups[g];
            var idx = g < stages.Count ? stages[g].Index : g;
            var items = src.Items.Select(i => { var vm = new PtItemVm(i); _items[i.Item.Id] = vm; return vm; }).ToList();
            Groups.Add(new PtGroupVm(idx.ToString(Inv) + " " + src.Title, items));
        }
        foreach (var c in Perf.Channels) Chans.Add(new PtChanVm(c.Id, ToggleChan));
        RefreshSelText();
    }

    private bool IsLockedItem(string id) => _items.TryGetValue(id, out var it) && !it.CanCheck;

    private void ToggleChan(PtChanVm c)
    {
        if (c.Reason is { } why) { _shell.Toast(why, ToastKind.Info); return; }
        if (IsLeak) { if (!_leakSel.Remove(c.N)) _leakSel.Add(c.N); RefreshLeak(); return; }
        var src = Perf.Channels.FirstOrDefault(x => x.Id == c.N);
        if (src == null || !src.IsEnabled) return;
        src.IsSelected = !src.IsSelected;
        RefreshChans();
    }

    private void RefreshSelText()
    {
        var sel = Perf.AllItems.Where(i => i.IsChecked && i.IsEnabled).ToList();
        SelText = "已选 " + sel.Count.ToString(Inv) + " 项 · 约 " + PerfTexts.Hm(sel.Sum(i => i.Item.EstimatedMinutes));
    }

    private void RefreshChans()
    {
        if (IsLeak) return;
        var d = Dev;
        bool locked = Perf.IsRunning;
        foreach (var c in Chans)
        {
            var src = Perf.Channels.First(x => x.Id == c.N);
            c.IsOn = src.IsSelected && src.IsEnabled;
            c.Reason = !src.IsEnabled ? "本计划只测 RV1–RV4" : locked ? "测试进行中，不能改通道" : null;
            c.Tip = c.IsOn ? c.Name + " · 参加测试 · 点一下不测" : c.Name + " · 点一下参加";
        }
        ChanNote = Perf.ChannelNote;
        _ = d;
    }

    /// <summary>每一项的状态：演示 = 样稿 PT_PLAN；实时 = 进度行（完成 / 当前）+ 结束后的结果（跳过 / 中止）。</summary>
    private void RefreshItemStates()
    {
        bool locked = Perf.IsRunning;
        if (_shell.Fleet.IsDemo)
        {
            foreach (var (id, it) in _items)
            {
                it.State = !it.Enabled ? "off" : PerfDemo.States.TryGetValue(id, out var s) ? s : "todo";
                it.Locked = false;
            }
            return;
        }
        var rows = Perf.Rows.Where(r => !r.IsSub).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var res = Perf.Result?.Items.ToDictionary(i => i.ItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, it) in _items)
        {
            string st = "todo";
            if (!it.Enabled) st = "off";
            else if (res != null && res.TryGetValue(id, out var r))
                st = r.Outcome == ItemOutcome.Completed ? "done" : "skip";
            else if (rows.TryGetValue(id, out var row)) st = row.IsCurrent ? "cur" : row.IsDone ? "done" : "todo";
            it.State = st;
            it.Locked = locked;
        }
    }

    private void RefreshItems()
    {
        RefreshItemStates();
        RefreshSelText();
        RefreshChans();
    }

    // ———————————————————— 进度 ————————————————————

    [ObservableProperty] private string _runSub = "";
    [ObservableProperty] private string _runTag = "未开始";
    [ObservableProperty] private BadgeTone _runTone;
    [ObservableProperty] private bool _runDot;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private string _remainText = "";

    /// <summary>当前一步的卡片（样稿 .ptcur）。</summary>
    [ObservableProperty] private bool _hasCur;
    [ObservableProperty] private string _curId = "";
    [ObservableProperty] private string _curTitle = "";
    [ObservableProperty] private string _curHint = "";
    [ObservableProperty] private string _curKind = "";
    [ObservableProperty] private string _curPrompt = "";
    [ObservableProperty] private bool _isEntry;
    [ObservableProperty] private bool _isConfirm;
    [ObservableProperty] private bool _isSafety;
    [ObservableProperty] private bool _twoSigners;
    [ObservableProperty] private string _confirmLabel = "确认并继续";
    [ObservableProperty] private string? _confirmReason;
    [ObservableProperty] private bool _canSkipPrompt;
    [ObservableProperty] private string _countdown = "";
    /// <summary>没在测时的卡片（未开始 / 已完成）：说明 + 按钮。</summary>
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private string _idleTitle = "";
    [ObservableProperty] private string _idleText = "";
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _hasNote;

    public ObservableCollection<PtEntryVm> Entries { get; } = new();
    public ObservableCollection<PtKpiVm> Kpis { get; } = new();
    public ObservableCollection<PtResVm> Results { get; } = new();
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _resultsEmpty = "";

    public ICommand ConfirmCommand { get; }
    public ICommand SkipPromptCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>检查表（手动确认步骤）：直接是性能测试视图模型的那几条。</summary>
    public ObservableCollection<PerfCheckVm> Checklist => Perf.ChecklistItems;

    private void RefreshRun()
    {
        var d = Dev;
        if (_shell.Fleet.IsDemo) { _demo.Apply(this, d); RefreshTools(); return; }
        var p = Perf.LastProgress;
        RunSub = p?.RunId is { Length: > 0 } rid ? "会话 " + rid : Perf.Result?.RunId is { } r2 ? "会话 " + r2 : "未开始";
        (RunTag, RunTone, RunDot) = Perf.IsRunning
            ? Perf.IsWaitingSafety ? ("安全暂停", BadgeTone.Err, false) : Perf.IsPaused ? ("已暂停", BadgeTone.Warn, false) : ("运行中", BadgeTone.Ok, true)
            : Perf.IsFinished ? (Perf.StatusText, Perf.StatusText == "已完成" ? BadgeTone.Info : BadgeTone.None, false) : ("未开始", BadgeTone.None, false);
        int total = p?.ItemCount ?? Perf.SelectedItemIds.Count;
        int done = p == null ? 0 : p.Status is RunStatus.Completed or RunStatus.Aborted ? p.ItemCount : Math.Min(p.ItemIndex, p.ItemCount);
        Progress = p == null ? 0 : Math.Clamp(p.OverallPercent / 100, 0, 1);
        DoneText = done.ToString(Inv) + " / " + total.ToString(Inv) + " 项";
        UsedText = p == null ? "" : "已用 " + PerfTexts.Dur(p.Elapsed);
        RemainText = p?.Eta is TimeSpan eta && Perf.IsRunning ? "预计还要 " + PerfTexts.Dur(eta) : "";

        bool waitEntry = Perf.IsWaitingEntry, waitConfirm = Perf.IsWaitingConfirm, waitSafety = Perf.IsWaitingSafety;
        HasCur = Perf.IsRunning;
        IsIdle = !Perf.IsRunning;
        IsFinished = Perf.IsFinished;
        if (Perf.IsRunning)
        {
            CurId = p?.ItemId ?? "";
            CurTitle = p?.ItemTitle ?? Perf.CurName;
            CurHint = Perf.CurName + (p?.ItemTitle != null && Perf.CurHint.Length > 0 ? " · " + Perf.CurHint : "");
            CurKind = waitEntry ? "人工录入" : waitConfirm ? "手动确认" : waitSafety ? "安全暂停" : Perf.IsPaused ? "已暂停" : "自动";
            CurPrompt = waitEntry ? Perf.ManualPrompt : waitConfirm ? (Perf.ConfirmTitle + (Perf.ConfirmText.Length > 0 ? " · " + Perf.ConfirmText : ""))
                : waitSafety ? Perf.SafetyText + (Perf.SafetyRecommendation.Length > 0 ? " · 建议：" + Perf.SafetyRecommendation : "")
                : (p?.Phase ?? "") + (p?.StepTitle is { } stp ? " · " + stp : "");
            IsEntry = waitEntry;
            IsConfirm = waitConfirm;
            IsSafety = waitSafety;
            TwoSigners = waitConfirm && Perf.ConfirmTwoSigners;
            ConfirmLabel = waitSafety ? "确认后继续" : Perf.IsPaused && !waitEntry && !waitConfirm ? "继续" : "确认并继续";
            ConfirmReason = Perf.CanConfirm ? (CanControl(d!) || waitSafety ? null : ControlReason(d)) : "现在没有等待确认的步骤";
            CanSkipPrompt = waitEntry || waitConfirm;
            Countdown = Perf.CountdownLabel + " " + Perf.Countdown;
            SyncEntries();
        }
        else
        {
            Entries.Clear();
            IdleTitle = Perf.IsFinished ? Perf.StatusText + " · " + Perf.ReportMeta : "未开始";
            IdleText = Perf.IsFinished ? Perf.ReportTime + (Perf.ReportPath.Length > 0 ? " · 报告 " + Perf.ReportPath : "")
                : "勾选左边的测试项和通道，点工具条「开始测试」（需要控制权）。带热 / 带压的项会先做 R0 前置检查表。";
        }
        RefreshKpis(d);
        Note = Perf.EventLines.FirstOrDefault() ?? "";
        HasNote = Note.Length > 0;
        RefreshTools();
    }

    private void SyncEntries()
    {
        var want = Perf.EntryFields.ToList();
        if (Entries.Count == want.Count && Entries.Select(e => e.Label).SequenceEqual(want.Select(EntryLabel))) return;
        Entries.Clear();
        var d = Dev;
        foreach (var f in want)
        {
            // 「RV1 本机 0.997」：按录入的单位写本机读数（压力 MPa / 温度 ℃）
            string sub = "";
            if (d != null && f.Label.StartsWith("RV", StringComparison.Ordinal) && int.TryParse(f.Label.AsSpan(2, f.Label.IndexOf(' ') is int sp && sp > 2 ? sp - 2 : f.Label.Length - 2), NumberStyles.Integer, Inv, out var n) && d.Reactor(n) is { } r)
                sub = f.Unit.Contains("MPa", StringComparison.Ordinal) && r.PressureMPa is double pm ? "本机 " + pm.ToString("0.000", Inv)
                    : f.Unit.Contains("℃", StringComparison.Ordinal) && r.TempC is double tc ? "本机 " + tc.ToString("0.0", Inv) : "";
            Entries.Add(new PtEntryVm(EntryLabel(f), sub, f.Unit, f));
        }
    }

    private static string EntryLabel(PerfEntryFieldVm f) => f.ShortLabel.Length > 0 ? f.ShortLabel : f.Label;

    private void RefreshKpis(DeviceVm? d)
    {
        var list = new List<PtKpiVm>();
        if (d != null)
        {
            list.Add(new PtKpiVm("搅拌（整台）", d.Stir is { } s ? Math.Round(s.On ? s.Rpm : 0).ToString(Inv) : "—", "rpm"));
            foreach (var n in Perf.SelectedChannels.DefaultIfEmpty(1).Take(4))
            {
                var r = d.Reactor(n);
                // 实时压力按设置的压力单位（MPa 3 位、bar 2 位、psi 1 位）
                list.Add(new PtKpiVm("RV" + n.ToString(Inv) + (r?.TempC is double t ? " · " + t.ToString("0.0", Inv) + " °C" : ""),
                    PressureDisplay.Fixed(r?.PressureMPa, 3), PressureDisplay.Unit));
            }
        }
        if (Kpis.Count != list.Count || !Kpis.SequenceEqual(list)) { Kpis.Clear(); foreach (var k in list) Kpis.Add(k); }
    }

    private void RefreshResults()
    {
        if (_shell.Fleet.IsDemo) return;
        Results.Clear();
        foreach (var r in Perf.ReportRows)
            Results.Add(new PtResVm(r.Item.Trim(), r.Channel, r.Setting, r.Measured, r.Error,
                r.VerdictText, r.IsPass ? BadgeTone.Ok : r.IsFail ? BadgeTone.Err : r.VerdictText == "记录" ? BadgeTone.Info : BadgeTone.None, r.IsItem));
        HasResults = Results.Count > 0;
        ResultsEmpty = "测试结束后在这里列出各项的实测值（摸底测试默认不判定）";
    }

    // ———————————————————— 工具条 ————————————————————

    private void RefreshTools()
    {
        var d = Dev;
        bool demo = _shell.Fleet.IsDemo;
        string? why = d == null ? "先选一台设备" : !PlanApplies(d) ? "测试计划只适用 HT-PR8 · " + d.Alias + " 是 " + d.ModelLabel : ControlReason(d);
        if (demo)
        {
            bool run = _demo.Running;
            _run.Icon = run ? "f-pause" : "f-play";
            _run.Label = run ? "暂停" : _demo.Paused ? "继续" : "开始测试";
            _run.Reason = why;
            _skip.Reason = why;
            _abort.Reason = null;
            _report.Reason = d != null && PlanApplies(d) ? null : why;
            _folder.Reason = _report.Reason;
        }
        else
        {
            _run.Icon = Perf.IsRunning && !Perf.IsPaused ? "f-pause" : "f-play";
            _run.Label = Perf.IsRunning ? (Perf.IsPaused ? "继续" : "暂停") : "开始测试";
            _run.Tip = Perf.IsRunning ? (Perf.IsPaused ? "继续测试" : "暂停在当前步骤（安全状态）") : "开始测试";
            _run.Reason = Perf.IsRunning ? null : why;
            _skip.Reason = !Perf.IsRunning ? "测试没在进行" : Perf.IsWaitingEntry || Perf.IsWaitingConfirm ? null : "只有等待确认 / 录入的步骤能跳过";
            _abort.Reason = Perf.IsRunning ? null : "测试没在进行";
            _report.Reason = Perf.IsFinished ? null : "测试结束后才有报告";
            _folder.Reason = Perf.OutputDir.Length > 0 ? null : "测试结束后才有输出目录";
        }
        RefreshLeakTools();
    }

    private async System.Threading.Tasks.Task RunOrPauseAsync()
    {
        if (_shell.Fleet.IsDemo)
        {
            _demo.Toggle();
            Toast(_demo.Running ? "测试继续（演示）" : "已暂停在当前步骤（演示）");
            RefreshRun();
            return;
        }
        if (!Perf.IsRunning) _runK = _shell.Nav.DeviceK;
        await Perf.StartOrPauseAsync().ConfigureAwait(true);
        RefreshItems();
        RefreshRun();
    }

    private void SkipStep()
    {
        if (_shell.Fleet.IsDemo) { Toast("已跳过 P1 第 4 点（演示）"); return; }
        Perf.SkipPrompt();
        RefreshRun();
    }

    private void Abort()
    {
        if (_shell.Fleet.IsDemo) { _demo.Aborted = true; Toast("测试已中止：停加热、关阀门（演示）"); RefreshRun(); return; }
        Perf.Abort();
        RefreshRun();
    }

    private void Confirm()
    {
        if (_shell.Fleet.IsDemo)
        {
            if (ConfirmReason is { } why) { Toast(why); return; }
            Toast("已记录 4 路读数 · 进入第 5 点 1.25 MPa（演示）");
            _demo.ClearEntries();
            RefreshRun();
            return;
        }
        if (ConfirmReason is { } w) { Toast(w); return; }
        Perf.ConfirmContinue();
        RefreshRun();
    }

    private void Report()
    {
        if (_shell.Fleet.IsDemo) { Toast("已生成报告 PT-20261115-1305（HTML + 汇总 CSV，演示）"); return; }
        Perf.GenerateReport();
        Perf.OpenReport();
    }

    private void OpenFolder()
    {
        if (_shell.Fleet.IsDemo) { Toast("打开输出目录 PT-20261115-1305（演示）"); return; }
        Perf.OpenFolder();
    }

    // ———————————————————— 刷新 ————————————————————

    public override void Refresh(IFleetView fleet)
    {
        var d = Dev;
        IsNarrow = !_shell.Layout.IsWide;
        IsShort = _shell.Layout.IsShort;
        DevSub = d == null ? "" : d.Alias + " · " + d.ModelLabel;
        PlanOk = d == null || PlanApplies(d);
        NoPlanTitle = d == null ? "" : d.Alias + "（" + d.ModelLabel + "）没有对应的测试计划";
        // 实时：安装信息 → 通道可选；最新一帧 → 性能测试视图模型（运行中的实时值、曲线之外的读数）
        if (Connection is { } c)
        {
            if (c.Device is { } info && !ReferenceEquals(info, _lastInfo)) { _lastInfo = info; Perf.ApplyInstalled(info.InstalledChannels.ToList()); }
            var f = c.Telemetry.Latest;
            if (f != null && !ReferenceEquals(f, _lastFrame)) { _lastFrame = f; Perf.OnTelemetry(c, f); }
        }
        if (_shell.Fleet.IsDemo && !_demoInit) { _demoInit = true; _demo.Init(Perf); }
        RefreshItems();
        RefreshRun();
        if (!_shell.Fleet.IsDemo) RefreshResults(); else _demo.ApplyResults(this);
        if (IsLeak) RefreshLeak();
    }

    private bool _demoInit;

    public override bool RefreshWhenHidden => false;

    public override void ApplyParams(Func<string, string?> param)
    {
        if (param("ptm") is "perf" or "leak") ModeKey = param("ptm")!;
    }

    public override void ResetView()
    {
        ModeKey = "perf";
        _leakSel.Clear();
        _leakSelK = 0;
        _demo.Reset();
    }

    public override void Dispose() { }

    // ———————————————————— 再做一次（记录页） ————————————————————

    /// <summary>
    /// 记录页「再做一次泄漏测试」（F7 的交接）：切到泄漏测试、预选那次会话里此刻空闲的通道（正在运行 / 报警 / 停用的不选）。
    /// </summary>
    public bool AcceptRedo(RedoRequest request, out string? toast)
    {
        toast = null;
        if (request.Kind != RedoKind.LeakTest) return false;
        if (_shell.Nav.DeviceK != request.DeviceK) _shell.SelectDevice(request.DeviceK);
        ModeKey = "leak";
        _leakSel.Clear();
        foreach (var n in request.Channels) _leakSel.Add(n);
        _leakSelK = request.DeviceK;
        RefreshLeak();
        var alias = _shell.Fleet.Device(request.DeviceK)?.Alias ?? "";
        toast = "已打开泄漏测试 · " + alias + " · 预选 " + (request.Channels.Count == 0 ? "没有空闲的通道" : string.Join("、", request.Channels.Select(n => "RV" + n.ToString(Inv))))
            + (request.Busy.Count > 0 ? "（" + string.Join("、", request.Busy.Select(n => "RV" + n.ToString(Inv))) + " 正在运行 / 停用，不能选）" : "")
            + " · 点工具条「开始泄漏测试」";
        return true;
    }
}
