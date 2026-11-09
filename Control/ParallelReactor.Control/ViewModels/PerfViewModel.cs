using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Controls;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.Control.ViewModels;

/// <summary>
/// 性能测试视图（mock data-view="perf"）：左侧测试项清单 + 通道芯片；右上测试进度（总进度条 / 步骤行 / 四张卡 / 确认卡 / 报告表）；
/// 右下实时曲线；检查器按钮（开始 / 暂停 / 继续 · 确认 / 继续 · 中止 · 生成报告 · 下载报告 · 下载原始数据 · 返回测试项）。
/// 同时实现 <see cref="IOperatorPrompt"/>：引擎线程上的确认 / 录入 / 安全询问被投递到 UI 线程，在 TaskCompletionSource 上等待操作员点「确认 / 继续」。
/// 引擎 = PerfTest 库的 <see cref="PerfRunner"/>（会话 / 打点经 HMI sessions API，HMI 自己的 CSV 也带上标记）。
/// 宿主（连接、客户端名、是否在看、toast）是 <see cref="IPerfHost"/>（M2 起 = 新外壳的性能测试页 Pages/PerfPageVm，不再依赖旧的主视图模型）。
/// </summary>
public sealed partial class PerfViewModel : ObservableObject, IOperatorPrompt
{
    private readonly IPerfHost _main;
    private PerfRunner? _runner;
    private TelemetryFeedAdapter? _feed;
    private Task<RunResult>? _runTask;
    /// <summary>本次会话中 R0 前置检查表已通过（§0.4）；后续运行带热项不再强制加入 R0。</summary>
    private bool _r0Passed;
    private DateTime? _runStartedUtc;
    private string? _curItemId;
    private int _rowsItemBase;
    private TaskCompletionSource<ConfirmResult>? _confirmTcs;
    private TaskCompletionSource<EntryResult>? _entryTcs;
    private TaskCompletionSource<SafetyDecision>? _safetyTcs;
    private EntryRequest? _entryReq;
    private ConfirmRequest? _confirmReq;
    private RunResult? _result;

    public TestPlan Plan { get; }
    public ObservableCollection<PerfGroupVm> Groups { get; } = new();
    public PerfChannelVm[] Channels { get; } = Enumerable.Range(1, 8).Select(i => new PerfChannelVm(i)).ToArray();
    public ObservableCollection<PerfStepRowVm> Rows { get; } = new();
    public ObservableCollection<string> EventLines { get; } = new();
    public ObservableCollection<PerfEntryFieldVm> EntryFields { get; } = new();
    public ObservableCollection<PerfEntryFieldVm> ExtraEntryFields { get; } = new();
    public ObservableCollection<PerfCheckVm> ChecklistItems { get; } = new();
    public ObservableCollection<PerfReportRowVm> ReportRows { get; } = new();
    public ChartVm Chart { get; }

    // —— 清单 ——
    [ObservableProperty] private string _selectedText = "已选 0 项";
    [ObservableProperty] private string _channelNote = "RV5–RV8 未安装";

    // —— 进度 ——
    [ObservableProperty] private string _progressTitle = "测试进度";
    [ObservableProperty] private string _statusText = "待开始";
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private string _overallText = "总进度 0 / 0";
    [ObservableProperty] private string _elapsedText = "已用时 00:00:00";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _curName = "—";
    [ObservableProperty] private string _curHint = "点击右侧「开始测试」";
    [ObservableProperty] private string _countdown = "--:--";
    [ObservableProperty] private string _countdownLabel = "倒计时";
    [ObservableProperty] private string _manualPath = "人工录入";
    [ObservableProperty] private string _manualDev = "外部表计";
    [ObservableProperty] private string _manualPrompt = "无需录入";
    [ObservableProperty] private string _manualUnit = "—";
    [ObservableProperty] private bool _entryActive;
    [ObservableProperty] private bool _entryError;
    [ObservableProperty] private PerfEntryFieldVm? _firstEntry;
    [ObservableProperty] private string _liveRpm = "0";
    [ObservableProperty] private string _liveTp = "— ℃ · — psi";
    [ObservableProperty] private string _liveRv = "RV1";
    [ObservableProperty] private string _livePath = "/RV1/模拟输入";

    // —— 确认 / 安全 ——
    [ObservableProperty] private bool _isWaitingConfirm;
    [ObservableProperty] private bool _isWaitingEntry;
    [ObservableProperty] private bool _isWaitingSafety;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmText = "";
    [ObservableProperty] private bool _confirmTwoSigners;
    [ObservableProperty] private string _signer1 = "";
    [ObservableProperty] private string _signer2 = "";
    [ObservableProperty] private string _operatorNote = "";
    [ObservableProperty] private string _safetyText = "";
    [ObservableProperty] private string _safetyRecommendation = "";

    // —— 运行状态 ——
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private bool _showReport;
    [ObservableProperty] private string _startButtonText = "开始测试";
    [ObservableProperty] private string _reportMeta = "";
    [ObservableProperty] private string _reportTime = "总用时 —";
    [ObservableProperty] private string _reportPath = "";
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private bool _simulated;

    public bool HasChecklist => ChecklistItems.Count > 0;
    public bool HasExtraEntries => ExtraEntryFields.Count > 0;
    public bool HasEntryBoxes => EntryFields.Any(f => !f.IsText);
    public bool ShowStartHint => !IsRunning && !IsFinished;
    public bool CanStart => !IsRunning || !IsWaitingSafety;
    public bool CanConfirm => IsWaitingConfirm || IsWaitingEntry || IsWaitingSafety || IsPaused;
    public bool CanAbort => IsRunning;
    public bool CanShowReport => IsFinished && !ShowReport;
    public bool HasReport => IsFinished && ReportPath.Length > 0;
    /// <summary>运行目录名（完整路径放 ToolTip）。</summary>
    public string OutputDirName => OutputDir.Length == 0 ? "" : Path.GetFileName(OutputDir.TrimEnd(Path.DirectorySeparatorChar));
    partial void OnOutputDirChanged(string value) => OnPropertyChanged(nameof(OutputDirName));
    public bool ShowRunView => !ShowReport;
    public string ChartSubtitle => $"{LiveRv} 温度 · 压力 · 全局转速";

    public PerfViewModel(IPerfHost main, TestPlan plan)
    {
        _main = main; Plan = plan;
        Chart = new ChartVm("实时曲线", ChartSubtitle, new[]
        {
            new ChartSeries("温度", ProcessChart.TempColor),
            new ChartSeries("压力", ProcessChart.OilColor, right: true),
            new ChartSeries("转速", ProcessChart.SpeedColor, right: true),
        })
        { LeftMax = 200, RightMax = 1000, XLabel = "测试时间 (mins)", RightLabel = "压力 (psi) / 转速 (rpm)" };
        BuildGroups();
        for (int i = 0; i < 8; i++)
        {
            var ch = Channels[i];
            ch.IsEnabled = i < 4;            // §0.3：首台只有 RV1–RV4 可用
            ch.IsSelected = i < 4;
            ch.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerfChannelVm.IsSelected)) UpdateSelectedText(); };
        }
        UpdateSelectedText();
    }

    /// <summary>默认方案：正式方案（rv1-4-baseline）+ 快速自检（smoke）附录；PR_CONTROL_PLAN=路径 可改用外部 JSON。</summary>
    public static TestPlan LoadDefaultPlan()
    {
        var file = Environment.GetEnvironmentVariable("PR_CONTROL_PLAN");
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) return PlanLoader.LoadFile(file);
        return PlanLoader.Merge(PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan), PlanLoader.LoadEmbedded(PlanLoader.SmokePlan));
    }

    // ───────────────────────── 清单 ─────────────────────────

    private void BuildGroups()
    {
        Groups.Clear();
        foreach (var st in Plan.Stages)
        {
            var g = new PerfGroupVm(st.Name, st.Phase2 ? "二期" : $"{st.Items.Count(i => i.Enabled)} 项");
            foreach (var it in st.Items)
            {
                var vm = new PerfItemVm(it);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerfItemVm.IsChecked)) UpdateSelectedText(); };
                g.Items.Add(vm);
            }
            Groups.Add(g);
        }
    }

    public IEnumerable<PerfItemVm> AllItems => Groups.SelectMany(g => g.Items);
    public List<string> SelectedItemIds => AllItems.Where(i => i.IsChecked && i.IsEnabled).Select(i => i.Item.Id).ToList();
    public List<int> SelectedChannels => Channels.Where(c => c.IsSelected && c.IsEnabled).Select(c => c.Id).ToList();

    private void UpdateSelectedText()
    {
        var n = SelectedItemIds.Count;
        var est = AllItems.Where(i => i.IsChecked && i.IsEnabled).Sum(i => i.Item.EstimatedMinutes);
        SelectedText = n == 0 ? "已选 0 项" : $"已选 {n} 项 · 约 {FormatMinutes(est)}";
        if (!IsRunning && !IsFinished) OverallText = $"总进度 0 / {n}";
    }

    [RelayCommand] public void SelectAll() { foreach (var i in AllItems) if (i.IsEnabled) i.IsChecked = true; }
    [RelayCommand] public void ClearAll() { foreach (var i in AllItems) i.IsChecked = false; }
    public void Check(string id, bool on = true) { var it = AllItems.FirstOrDefault(i => i.Item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); if (it != null) it.IsChecked = on; }
    public void SelectChannels(params int[] rvs) { foreach (var c in Channels) c.IsSelected = rvs.Contains(c.Id) && c.IsEnabled; }

    /// <summary>设备安装信息到达：RV5–RV8 仍按 §0.3 禁用，仅更新说明文字。</summary>
    public void ApplyInstalled(IReadOnlyCollection<int> installed)
    {
        for (int i = 0; i < 8; i++) if (!installed.Contains(i + 1)) { Channels[i].IsEnabled = false; Channels[i].IsSelected = false; }
        var dis = Channels.Where(c => !c.IsEnabled).Select(c => c.Id).ToList();
        ChannelNote = dis.Count == 0 ? "RV1–RV8" : dis.Count == 4 && dis[0] == 5 ? "RV5–RV8 未安装" : "未安装：" + string.Join(" ", dis.Select(d => "RV" + d));
    }

    // ───────────────────────── 开始 / 暂停 / 继续 / 中止 ─────────────────────────

    [RelayCommand]
    public async Task StartOrPauseAsync()
    {
        if (_runner != null && IsRunning)
        {
            if (IsPaused) { _runner.Resume(); IsPaused = false; _main.Toast("继续测试"); }
            else { _runner.Pause(); IsPaused = true; _main.Toast("测试已暂停"); }
            RefreshButtons();
            return;
        }
        if (IsFinished) { _main.Toast("测试已完成，请生成报告或返回测试项"); return; }
        await StartAsync();
    }

    public async Task StartAsync()
    {
        var c = _main.Connection;
        if (c == null) { _main.Toast("未连接设备"); return; }
        if (c.Lease.State != LeaseState.Holding) { _main.Toast("尚未持有控制权 · 请先申请远程控制"); return; }
        var ids = SelectedItemIds;
        if (ids.Count == 0) { _main.Toast("请先勾选测试项"); return; }
        var chans = SelectedChannels;
        if (chans.Count == 0) { _main.Toast("请至少选择一个测试通道"); return; }
        // §0.4 门禁：带热 / 带压项必须先过 R0 前置检查表（本次会话里已通过一次则不再重复）；引擎侧同样会拒绝
        if (!_r0Passed && Plan.FindItem("R0") is { } r0def && !ids.Contains(r0def.Id) && ids.Any(id => Plan.FindItem(id)?.NeedsPrecheck() == true))
        {
            Check(r0def.Id); ids = SelectedItemIds;
            _main.Toast("已自动加入 R0 前置检查表：带热 / 带压项须先签字（§0.4）");
        }

        Reset(keepSelection: true);
        var api = new ReactorApiAdapter(c);
        _feed = new TelemetryFeedAdapter(c);
        _runner = new PerfRunner(Plan, api, _feed, this);
        _runner.Progress += p => Dispatcher.UIThread.Post(() => ApplyProgress(p));
        _runner.SafetyRaised += ev => Dispatcher.UIThread.Post(() => { AddEvent($"安全：{ev.Title} · {ev.Detail}"); _main.Toast("安全事件：" + ev.Title); });
        var req = new RunRequest
        {
            PlanId = Plan.PlanId, ItemIds = ids, Channels = chans,
            Operator = _main.ClientName, SoftwareVersion = "pc-" + (typeof(PerfViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0"),
            OutputRoot = ResultStore.DefaultRoot(), R0SignedOff = _r0Passed,
        };
        _runStartedUtc = DateTime.UtcNow;
        IsRunning = true; IsPaused = false; IsFinished = false; ShowReport = false;
        ProgressTitle = "测试进度"; StatusText = "运行中";
        LiveRv = "RV" + chans[0]; LivePath = $"/RV{chans[0]}/模拟输入"; Chart.Subtitle = ChartSubtitle; OnPropertyChanged(nameof(ChartSubtitle));
        BuildRows(ids, chans);
        AddEvent($"开始：{ids.Count} 项 · {string.Join(" ", chans.Select(r => "RV" + r))}");
        _main.Toast($"性能测试开始：{string.Join(" ", chans.Select(r => "RV" + r))} · {ids.Count} 项");
        RefreshButtons();

        _runTask = Task.Run(() => _runner.RunAsync(req, CancellationToken.None));
        try
        {
            var r = await _runTask;
            OnFinished(r);
        }
        catch (Exception ex)
        {
            AddEvent("引擎异常：" + ex.Message);
            _main.Toast("测试引擎异常：" + ex.Message);
            IsRunning = false; IsPaused = false; IsFinished = _runner.Result != null;
            if (_runner.Result != null) OnFinished(_runner.Result);
        }
        finally
        {
            _feed?.Dispose(); _feed = null;
            RefreshButtons();
        }
    }

    [RelayCommand]
    public void Abort()
    {
        if (_runner == null || !IsRunning) return;
        if (IsWaitingSafety && _safetyTcs != null) { _safetyTcs.TrySetResult(SafetyDecision.AbortRun); _safetyTcs = null; IsWaitingSafety = false; }
        _runner.Abort("操作员中止");
        IsPaused = false;
        _main.Toast("正在中止：停止本项加热 / 搅拌，生成报告…");
        RefreshButtons();
    }

    /// <summary>断开 / 退出前：中止运行中的测试（停止类命令收尾 + 出报告）并等引擎结束，最多 <paramref name="timeout"/>。任意线程可调。</summary>
    public async Task AbortAndWaitAsync(TimeSpan timeout)
    {
        var task = _runTask; var runner = _runner;
        if (runner == null || task == null || task.IsCompleted) return;
        if (Dispatcher.UIThread.CheckAccess()) Abort(); else Dispatcher.UIThread.Post(Abort);
        await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
    }

    /// <summary>检查器「确认 / 继续」：按当前等待类别提交录入 / 确认 / 安全继续 / 恢复暂停。</summary>
    [RelayCommand]
    public void ConfirmContinue()
    {
        if (IsWaitingEntry && _entryTcs != null) { SubmitEntry(); return; }
        if (IsWaitingConfirm && _confirmTcs != null) { SubmitConfirm(true); return; }
        if (IsWaitingSafety && _safetyTcs != null)
        {
            var tcs = _safetyTcs; _safetyTcs = null; IsWaitingSafety = false;
            _runner?.Resume(); IsPaused = false;
            tcs.TrySetResult(SafetyDecision.Continue);
            AddEvent("安全事件：操作员选择继续");
            _main.Toast("已确认，继续执行");
            RefreshButtons();
            return;
        }
        if (IsPaused && _runner != null) { _runner.Resume(); IsPaused = false; _main.Toast("继续测试"); RefreshButtons(); return; }
        _main.Toast("当前没有待确认的步骤");
    }

    /// <summary>「跳过」：确认类回答否（引擎按 onFail 处理）/ 录入取消。</summary>
    [RelayCommand]
    public void SkipPrompt()
    {
        if (IsWaitingConfirm && _confirmTcs != null) { SubmitConfirm(false); return; }
        if (IsWaitingEntry && _entryTcs != null)
        {
            var tcs = _entryTcs; _entryTcs = null; ClearEntry();
            tcs.TrySetResult(new EntryResult(true, null, null, NoteOrNull()));
            _main.Toast("已跳过录入");
            RefreshButtons();
        }
    }

    private void SubmitConfirm(bool yes)
    {
        var tcs = _confirmTcs!; var req = _confirmReq;
        var checks = ChecklistItems.Count > 0 ? ChecklistItems.Select(c => c.IsChecked).ToList() : null;
        if (yes && req?.Checklist is { Count: > 0 } && checks != null && checks.Any(x => !x)) { _main.Toast($"检查表尚有 {checks.Count(x => !x)} 项未勾选"); return; }
        if (yes && req?.TwoSigners == true && (string.IsNullOrWhiteSpace(Signer1) || string.IsNullOrWhiteSpace(Signer2))) { _main.Toast("检查表需两人签字"); return; }
        _confirmTcs = null; _confirmReq = null;
        IsWaitingConfirm = false; ChecklistItems.Clear(); OnPropertyChanged(nameof(HasChecklist));
        tcs.TrySetResult(new ConfirmResult(yes, checks, string.IsNullOrWhiteSpace(Signer1) ? null : Signer1.Trim(), string.IsNullOrWhiteSpace(Signer2) ? null : Signer2.Trim(), NoteOrNull()));
        OperatorNote = "";
        _main.Toast(yes ? "已确认，继续执行" : "已跳过该步");
        RefreshButtons();
    }

    private void SubmitEntry()
    {
        var req = _entryReq!;
        var values = new Dictionary<string, IReadOnlyList<double>>();
        var texts = new Dictionary<string, string>();
        foreach (var f in req.Fields)
        {
            var rows = EntryFields.Where(e => e.Key == f.Key).ToList();
            if (f.IsText) { var t = rows.FirstOrDefault()?.Text ?? ""; if (t.Length > 0 || !f.Optional) texts[f.Key] = t; continue; }
            var list = new List<double>();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Text)) { if (f.Optional) { list = null!; break; } r.HasError = true; _main.Toast($"请先输入 {r.Label} 读数"); return; }
                if (!double.TryParse(r.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { r.HasError = true; _main.Toast($"「{r.Label}」不是有效数字"); return; }
                if (f.Min.HasValue && v < f.Min || f.Max.HasValue && v > f.Max) { r.HasError = true; _main.Toast($"「{r.Label}」{v} 超出范围 {f.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞"}…{f.Max?.ToString(CultureInfo.InvariantCulture) ?? "+∞"}"); return; }
                r.HasError = false; list.Add(v);
            }
            if (list != null) values[f.Key] = list;
        }
        var tcs = _entryTcs!; _entryTcs = null;
        var summary = string.Join("、", values.Select(kv => $"{req.Fields.First(f => f.Key == kv.Key).Label} {string.Join("/", kv.Value.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))} {req.Fields.First(f => f.Key == kv.Key).Unit}"));
        ClearEntry();
        tcs.TrySetResult(new EntryResult(false, values, texts, NoteOrNull()));
        OperatorNote = "";
        AddEvent($"录入：{summary}");
        _main.Toast(summary.Length > 0 ? summary + " 已记录" : "已记录");
        RefreshButtons();
    }

    /// <summary>截图 / 自动化：把所有待录入字段填成同一个值并提交。</summary>
    public void AutoFillEntry(double value)
    {
        foreach (var f in EntryFields) f.Text = f.IsText ? "正常" : Math.Clamp(value, f.Min ?? double.MinValue, f.Max ?? double.MaxValue).ToString("0.##", CultureInfo.InvariantCulture);
        if (IsWaitingEntry && _entryTcs != null) SubmitEntry();
    }

    private string? NoteOrNull() => string.IsNullOrWhiteSpace(OperatorNote) ? null : OperatorNote.Trim();

    private void ClearEntry()
    {
        IsWaitingEntry = false; EntryActive = false; EntryError = false; _entryReq = null;
        EntryFields.Clear(); ExtraEntryFields.Clear(); FirstEntry = null; OnPropertyChanged(nameof(HasExtraEntries)); OnPropertyChanged(nameof(HasEntryBoxes));
        ManualPath = "人工录入"; ManualDev = "外部表计"; ManualPrompt = "无需录入"; ManualUnit = "—";
    }

    // ───────────────────────── IOperatorPrompt（引擎线程 → UI 线程） ─────────────────────────

    public async Task<ConfirmResult> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConfirmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _confirmTcs = tcs; _confirmReq = request;
            ConfirmTitle = request.Title; ConfirmText = request.Text; ConfirmTwoSigners = request.TwoSigners;
            Signer1 = ""; Signer2 = "";
            ChecklistItems.Clear();
            if (request.Checklist != null) foreach (var c in request.Checklist) ChecklistItems.Add(new PerfCheckVm(c));
            OnPropertyChanged(nameof(HasChecklist));
            IsWaitingConfirm = true;
            StatusText = "等待手动确认";
            AddEvent($"等待确认：{request.Title}");
            RefreshButtons();
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<EntryResult> EnterAsync(EntryRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<EntryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _entryTcs = tcs; _entryReq = request;
            EntryFields.Clear(); ExtraEntryFields.Clear();
            foreach (var f in request.Fields)
            {
                var chans = f.PerChannel ? request.Channels : new List<int> { 0 };
                foreach (var rv in chans)
                    for (int k = 1; k <= Math.Max(1, f.Repeat); k++)
                    {
                        var label = (rv > 0 ? $"RV{rv} · " : "") + f.Label + (f.Repeat > 1 ? $" #{k}" : "");
                        var shortLabel = request.Fields.Count > 1 ? f.Label : "";
                        if (rv > 0) shortLabel = $"RV{rv}" + (f.Repeat > 1 ? $" #{k}" : "") + (shortLabel.Length > 0 ? " " + shortLabel : "");
                        else if (f.Repeat > 1) shortLabel = $"#{k}" + (shortLabel.Length > 0 ? " " + shortLabel : "");
                        EntryFields.Add(new PerfEntryFieldVm(f.Key, label, f.Unit, f.Instrument, f.IsText, f.Min, f.Max) { ShortLabel = shortLabel.Length > 0 ? shortLabel : f.Unit });
                    }
            }
            FirstEntry = EntryFields.FirstOrDefault(e => !e.IsText);
            foreach (var e in EntryFields.Where(e => e.IsText)) ExtraEntryFields.Add(e);
            OnPropertyChanged(nameof(HasExtraEntries)); OnPropertyChanged(nameof(HasEntryBoxes));
            var first = request.Fields.FirstOrDefault();
            ManualPath = $"人工录入 · {request.StepId}";
            ManualDev = first?.Instrument is { Length: > 0 } ins ? ins : "外部表计";
            ManualPrompt = request.ValidationError ?? (request.Text is { Length: > 0 } t ? t : $"请输入 {first?.Label ?? "读数"}");
            EntryError = request.ValidationError != null;
            ManualUnit = first?.Unit is { Length: > 0 } u ? u : "—";
            EntryActive = true; IsWaitingEntry = true;
            StatusText = "等待人工录入";
            AddEvent((request.ValidationError != null ? "录入校验失败：" + request.ValidationError + " · " : "") + $"等待录入：{request.Title}");
            RefreshButtons();
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<SafetyDecision> SafetyAsync(SafetyPrompt prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SafetyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _safetyTcs = tcs;
            SafetyText = $"{prompt.Event.Title} · {prompt.Event.Detail}";
            SafetyRecommendation = prompt.Recommendation;
            IsWaitingSafety = true; IsPaused = true;
            StatusText = "安全暂停";
            RefreshButtons();
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
    }

    // ───────────────────────── 进度 ─────────────────────────

    private void BuildRows(List<string> ids, List<int> chans)
    {
        Rows.Clear();
        int n = 0;
        foreach (var id in Plan.AllItems().Where(i => ids.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).Select(i => i.Id))
        {
            var it = Plan.FindItem(id)!;
            var ch = it.ChannelSelectable ? string.Join(" ", chans.Select(r => "RV" + r)) : "全局";
            Rows.Add(new PerfStepRowVm(id, $"{++n}. {it.Id} {it.Title}", $"{ch} · {(it.Hint.Length > 0 ? it.Hint : "自动")}"));
        }
        _rowsItemBase = Rows.Count;
    }

    private void ApplyProgress(PerfProgress p)
    {
        StatusText = p.Status switch
        {
            RunStatus.Completed => "已完成",
            RunStatus.Aborted => "已中止",
            RunStatus.Failed => "失败",
            _ => p.Waiting switch
            {
                WaitingKind.Safety => "安全暂停",
                WaitingKind.Paused => "已暂停",
                WaitingKind.Confirm => "等待手动确认",
                WaitingKind.Entry => "等待人工录入",
                _ => "运行中",
            },
        };
        OverallPercent = p.OverallPercent;
        int done = p.Status is RunStatus.Completed or RunStatus.Aborted ? p.ItemCount : Math.Min(p.ItemIndex, p.ItemCount);
        OverallText = $"总进度 {done} / {p.ItemCount}" + (p.ItemCount > 0 && p.Status is not (RunStatus.Completed or RunStatus.Aborted) ? $" · {p.OverallPercent:0}%" : "");
        ElapsedText = "已用时 " + Fmt(p.Elapsed);
        EtaText = p.Eta is TimeSpan eta && p.Status is not (RunStatus.Completed or RunStatus.Aborted) ? "预计剩余 " + FormatMinutes(eta.TotalMinutes) : "";
        if (p.ItemId != null)
        {
            var iter = p.IterationCount > 1 ? $" · 第 {p.Iteration}/{p.IterationCount} 轮" : "";
            CurName = (p.StepTitle ?? p.ItemTitle ?? "—") + iter;
            CurHint = $"{p.ItemId} · {p.Phase}" + (p.StepCount > 0 ? $" · 步骤 {Math.Min(p.StepIndex + 1, p.StepCount)}/{p.StepCount}" : "");
        }
        else { CurName = p.Phase; CurHint = p.ItemCount > 0 ? $"{p.ItemCount} 项" : ""; }
        Countdown = p.Countdown is TimeSpan cd ? Fmt(cd, false) : "--:--";
        CountdownLabel = p.Waiting switch
        {
            WaitingKind.Entry => "等待录入",
            WaitingKind.Confirm => "等待确认",
            WaitingKind.Safety => "安全暂停",
            WaitingKind.Paused => "已暂停",
            _ => p.Phase == "稳定计时" ? "稳定计时" : "倒计时",
        };
        LiveRpm = p.LiveRpm.ToString(CultureInfo.InvariantCulture) + (p.StirOn ? "" : "");
        var t = p.LiveTempC.Count > 0 && p.LiveTempC[0] is double tv ? tv.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        var pr = p.LivePressurePsi.Count > 0 && p.LivePressurePsi[0] is double pv ? pv.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        LiveTp = $"{t} ℃ · {pr} psi";
        if (p.Channels.Count > 0) { var rv = "RV" + p.Channels[0]; if (rv != LiveRv) { LiveRv = rv; LivePath = $"/{rv}/模拟输入"; Chart.Subtitle = ChartSubtitle; OnPropertyChanged(nameof(ChartSubtitle)); } }

        // 步骤行：项行 + 当前项的步骤子行
        if (p.ItemId != _curItemId)
        {
            _curItemId = p.ItemId;
            while (Rows.Count > _rowsItemBase) Rows.RemoveAt(Rows.Count - 1);
            var def = p.ItemId != null ? Plan.FindItem(p.ItemId) : null;
            int at = Rows.ToList().FindIndex(r => r.Id.Equals(p.ItemId ?? "", StringComparison.OrdinalIgnoreCase));
            if (def != null && at >= 0)
            {
                int k = 0;
                foreach (var s in def.Steps)
                    Rows.Insert(at + 1 + k, new PerfStepRowVm(def.Id + "/" + s.Id, "    " + ResolveTitle(s.Title, p.Channels, def, p.Iteration), KindZh(s.Kind)) { IsSub = true, Index = k++ });
            }
            if (p.ItemId != null) AddEvent($"测试项 {p.ItemId} 开始");
        }
        int itemIdx = 0;
        foreach (var r in Rows)
        {
            if (!r.IsSub)
            {
                r.IsDone = itemIdx < p.ItemIndex || p.Status is RunStatus.Completed;
                r.IsCurrent = itemIdx == p.ItemIndex && p.Status is not (RunStatus.Completed or RunStatus.Aborted);
                r.IsWaiting = r.IsCurrent && p.Waiting is WaitingKind.Confirm or WaitingKind.Entry or WaitingKind.Safety;
                itemIdx++;
            }
            else
            {
                r.IsDone = r.Index < p.StepIndex;
                r.IsCurrent = r.Index == p.StepIndex && p.Status is not (RunStatus.Completed or RunStatus.Aborted);
                r.IsWaiting = r.IsCurrent && p.Waiting is WaitingKind.Confirm or WaitingKind.Entry or WaitingKind.Safety;
                r.Right = r.IsCurrent && p.Waiting == WaitingKind.None && p.Countdown is TimeSpan c2 ? $"{r.BaseRight} · {Fmt(c2, false)}" : r.BaseRight;
            }
        }
        if (p.Waiting == WaitingKind.Paused && !IsPaused) IsPaused = true;
        if (p.Waiting == WaitingKind.None && p.Status == RunStatus.Running && IsPaused && !IsWaitingSafety) IsPaused = false;
        RefreshButtons();
    }

    private static string ResolveTitle(string title, IReadOnlyList<int> chans, TestItem def, int iteration)
    {
        var rep = def.Repeat;
        string value = rep?.Values is { Count: > 0 } vals && iteration >= 1 && iteration <= vals.Count ? vals[iteration - 1].ToString("0.##", CultureInfo.InvariantCulture)
                     : rep != null && rep.Iterations > 1 ? iteration.ToString(CultureInfo.InvariantCulture) : "…";
        return title.Replace("{rv}", chans.Count == 1 ? chans[0].ToString(CultureInfo.InvariantCulture) : string.Join("/", chans)).Replace("{value}", value).Replace("{item}", def.Id).Replace("{iteration}", iteration.ToString(CultureInfo.InvariantCulture));
    }

    private static string KindZh(StepKind k) => k switch
    {
        StepKind.Instruction => "手动确认",
        StepKind.Checklist => "检查表",
        StepKind.ManualEntry => "仪器读数",
        StepKind.Command => "自动 · 命令",
        StepKind.WaitStable => "等待稳定",
        StepKind.Hold => "自动 · 记录",
        StepKind.Measure => "计算",
        StepKind.Compare => "核对设置",
        StepKind.SessionBegin or StepKind.SessionEnd or StepKind.Marker => "会话",
        _ => k.ToString(),
    };

    private void OnFinished(RunResult r)
    {
        _result = r;
        if (r.Items.Any(i => i.ItemId.Equals("R0", StringComparison.OrdinalIgnoreCase) && i.Outcome == ItemOutcome.Completed)) _r0Passed = true;
        IsRunning = false; IsPaused = false; IsFinished = true;
        IsWaitingConfirm = false; IsWaitingEntry = false; IsWaitingSafety = false; ClearEntry(); ChecklistItems.Clear(); OnPropertyChanged(nameof(HasChecklist));
        OutputDir = r.OutputDir ?? "";
        ReportPath = r.OutputDir != null ? Path.Combine(r.OutputDir, "report.html") : "";
        Simulated = r.Simulated;
        StatusText = r.Status == RunStatus.Completed ? "已完成" : r.Status == RunStatus.Aborted ? "已中止" : "失败";
        ReportMeta = $"报告编号 {r.RunId} · 通道 {string.Join(" ", r.Channels.Select(c => "RV" + c))} · {r.Items.Count} 项" + (r.Simulated ? " · 模拟运行" : "");
        ReportTime = "总用时 " + Fmt(r.Elapsed);
        CurName = StatusText; CurHint = r.AbortReason ?? $"{r.Items.Count(i => i.Outcome == ItemOutcome.Completed)} 项完成";
        Countdown = "--:--"; CountdownLabel = "已结束";
        BuildReportRows(r);
        AddEvent($"{StatusText} · 报告 {ReportPath}");
        _main.Toast(r.Status == RunStatus.Completed ? "测试完成，报告已生成" : "测试已中止，报告已生成");
        RefreshButtons();
    }

    private void BuildReportRows(RunResult r)
    {
        ReportRows.Clear();
        foreach (var it in r.Items)
        {
            var chText = it.Channels.Count == 0 ? "全局" : string.Join(" ", it.Channels.Select(c => "RV" + c));
            // 项行：结论 + 判定；其后每个指标一行（缩进）
            ReportRows.Add(new PerfReportRowVm($"{it.ItemId} {it.Title}", chText, it.HardCriterion ?? "—", OutcomeZh(it.Outcome), "—", VerdictZh(it.Verdict), it.Verdict) { IsItem = true });
            foreach (var m in it.Measures)
            {
                var ch = m.Channel is int c ? "RV" + c : it.Channels.Count == 0 ? "全局" : chText;
                var setting = m.IterationValue is double iv ? $"{iv:0.##}" + (m.Reference5 != null ? " · " + m.Reference5 : "") : m.Reference5 ?? "—";
                var err = m.Formula is "rpmError" or "overshoot" or "drift" or "range" ? $"{m.Value:0.##} {m.Unit}" : "—";
                ReportRows.Add(new PerfReportRowVm("    " + m.Label, ch, setting, $"{m.Value:0.##} {m.Unit}", err, "", it.Verdict));
            }
        }
    }

    private static string OutcomeZh(ItemOutcome o) => o switch { ItemOutcome.Completed => "完成", ItemOutcome.Aborted => "中止", ItemOutcome.Skipped => "跳过", ItemOutcome.Failed => "失败", _ => "—" };
    private static string VerdictZh(Verdict v) => v switch { Verdict.Pass => "合格", Verdict.Fail => "不合格", Verdict.Info => "记录", _ => "—（摸底）" };

    // ───────────────────────── 报告 / 文件 ─────────────────────────

    [RelayCommand]
    public void GenerateReport()
    {
        if (!IsFinished) { _main.Toast("测试尚未结束"); return; }
        ShowReport = true; ProgressTitle = "性能测试报告"; StatusText = _result?.RunId ?? StatusText;
        _main.Toast("报告已生成：" + Path.GetFileName(Path.GetDirectoryName(ReportPath) ?? "") + "/" + Path.GetFileName(ReportPath));
        RefreshButtons();
    }

    [RelayCommand]
    public void OpenReport()
    {
        if (ReportPath.Length == 0 || !File.Exists(ReportPath)) { _main.Toast("报告文件不存在"); return; }
        if (!OpenExternal(ReportPath)) _main.Toast("报告：" + ReportPath);
        else _main.Toast("已打开报告 " + Path.GetFileName(ReportPath));
    }

    [RelayCommand]
    public void OpenFolder()
    {
        if (OutputDir.Length == 0 || !Directory.Exists(OutputDir)) { _main.Toast("原始数据目录不存在"); return; }
        if (!OpenExternal(OutputDir)) _main.Toast("原始数据：" + OutputDir);
        else _main.Toast("已打开目录 " + OutputDir);
    }

    private static bool OpenExternal(string path)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT"))) return false; // 截图模式不拉起外部程序
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    [RelayCommand]
    public void ResetView()
    {
        if (IsRunning) { _main.Toast("测试进行中，请先暂停或中止"); return; }
        Reset(keepSelection: true);
        _main.Toast("已重置，返回测试项选择");
    }

    private void Reset(bool keepSelection)
    {
        _runner = null; _runTask = null; _result = null; _curItemId = null; _runStartedUtc = null;
        _confirmTcs = null; _entryTcs = null; _safetyTcs = null;
        IsRunning = false; IsPaused = false; IsFinished = false; ShowReport = false;
        IsWaitingConfirm = false; IsWaitingEntry = false; IsWaitingSafety = false;
        ProgressTitle = "测试进度"; StatusText = "待开始"; OverallPercent = 0; OverallText = $"总进度 0 / {SelectedItemIds.Count}"; ElapsedText = "已用时 00:00:00"; EtaText = "";
        CurName = "—"; CurHint = "点击右侧「开始测试」"; Countdown = "--:--"; CountdownLabel = "倒计时";
        ClearEntry(); ChecklistItems.Clear(); OnPropertyChanged(nameof(HasChecklist));
        Rows.Clear(); EventLines.Clear(); ReportRows.Clear(); ReportMeta = ""; ReportTime = "总用时 —"; ReportPath = ""; OutputDir = "";
        foreach (var s in Chart.Series) s.Points.Clear(); Chart.XMax = 88; Chart.Revision++;
        if (!keepSelection) ClearAll();
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        StartButtonText = IsRunning ? (IsPaused ? "继续" : "暂停") : "开始测试";
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanConfirm)); OnPropertyChanged(nameof(CanAbort));
        OnPropertyChanged(nameof(CanShowReport)); OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ShowRunView)); OnPropertyChanged(nameof(ShowStartHint));
    }

    partial void OnShowReportChanged(bool value) => RefreshButtons();
    partial void OnIsWaitingConfirmChanged(bool value) => RefreshButtons();
    partial void OnIsWaitingEntryChanged(bool value) => RefreshButtons();
    partial void OnIsWaitingSafetyChanged(bool value) => RefreshButtons();

    private void AddEvent(string text)
    {
        EventLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (EventLines.Count > 40) EventLines.RemoveAt(EventLines.Count - 1);
    }

    // ───────────────────────── 遥测 → 曲线 ─────────────────────────

    /// <summary>宿主每次刷新调用（最新一帧）：运行中按首个目标通道画温度 / 压力 / 全局转速（rpm 原值）。</summary>
    public void OnTelemetry(IReactorConnection c, TelemetrySnapshot f)
    {
        if (!_main.IsVisible && !IsRunning) return;
        int rv = int.TryParse(LiveRv.AsSpan(2), out var r) ? r : 1;
        var origin = _runStartedUtc ?? (c.Telemetry.Snapshot().FirstOrDefault()?.Ts ?? f.Ts);
        foreach (var s in Chart.Series) s.Points.Clear();
        double maxX = 0, maxT = 0, maxR = 0;
        foreach (var fr in c.Telemetry.Snapshot())
        {
            if (fr.Ts < origin) continue;
            double x = (fr.Ts - origin).TotalMinutes; maxX = Math.Max(maxX, x);
            var cs = fr.Channels.FirstOrDefault(ch => ch.Id == rv);
            if (cs?.TempC is double t) { Chart.Series[0].Points.Add(new Point(x, t)); maxT = Math.Max(maxT, t); }
            if (cs?.PressurePsi is double p) { Chart.Series[1].Points.Add(new Point(x, p)); maxR = Math.Max(maxR, p); }
            var rpm = fr.Stir.On ? fr.Stir.RpmSp : 0; Chart.Series[2].Points.Add(new Point(x, rpm)); maxR = Math.Max(maxR, rpm);
        }
        Chart.XMax = NiceXMax(maxX);
        Chart.LeftMax = maxT <= 200 ? 200 : 300;
        Chart.RightMax = Math.Max(1000, Math.Ceiling(maxR / 500) * 500);
        Chart.Revision++;
        if (!IsRunning)
        {
            var cs = f.Channels.FirstOrDefault(ch => ch.Id == rv);
            LiveRpm = (f.Stir.On ? f.Stir.RpmSp : 0).ToString(CultureInfo.InvariantCulture);
            LiveTp = $"{(cs?.TempC is double tt ? tt.ToString("0.0", CultureInfo.InvariantCulture) : "—")} ℃ · {(cs?.PressurePsi is double pp ? pp.ToString("0.0", CultureInfo.InvariantCulture) : "—")} psi";
        }
    }

    // ───────────────────────── 工具 ─────────────────────────

    /// <summary>X 轴范围：无数据 / 超过 70 min 时用 mock 的 88 min 格；短数据按 1 / 2 / 5 / 10 / 20 / 40 min 档位放大，曲线可见（原在旧主视图模型里）。</summary>
    internal static double NiceXMax(double spanMin)
        => spanMin <= 0 ? 88 : spanMin < 0.9 ? 1 : spanMin < 1.8 ? 2 : spanMin < 4.5 ? 5 : spanMin < 9 ? 10 : spanMin < 18 ? 20 : spanMin < 36 ? 40 : spanMin < 70 ? 88 : Math.Ceiling(spanMin * 1.1 / 10) * 10;

    private static string Fmt(TimeSpan t, bool hours = true)
        => hours || t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private static string FormatMinutes(double min)
        => min <= 0 ? "0 min" : min < 60 ? $"{Math.Ceiling(min):0} min" : min < 600 ? $"{min / 60:0.#} h" : $"{Math.Round(min / 60):0} h";

    /// <summary>截图脚本用：当前运行是否处于某状态。</summary>
    public PerfProgress? LastProgress => _runner?.LastProgress;
    public RunResult? Result => _result;
    public Task<RunResult>? RunTask => _runTask;
}

// ───────────────────────── 子 VM ─────────────────────────

public sealed partial class PerfGroupVm : ObservableObject
{
    public PerfGroupVm(string title, string right) { Title = title; Right = right; }
    public string Title { get; }
    public string Right { get; }
    public ObservableCollection<PerfItemVm> Items { get; } = new();
}

public sealed partial class PerfItemVm : ObservableObject
{
    public PerfItemVm(TestItem item) { Item = item; }
    public TestItem Item { get; }
    public string Title => $"{Item.Id} {Item.Title}";
    public string Right => Item.Enabled ? (Item.Hint.Length > 0 ? Item.Hint : $"自动 · {Item.EstimatedMinutes:0} min") : (Item.DisabledReason ?? "二期");
    public bool IsEnabled => Item.Enabled;
    public string Tooltip => $"{Item.SourceRef}\n{Item.Purpose}\n仪器：{Item.Instrument}\n预计 {Item.EstimatedMinutes:0} min" + (Item.Preconditions.Count > 0 ? "\n前置：" + string.Join("、", Item.Preconditions) : "");
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class PerfChannelVm : ObservableObject
{
    public PerfChannelVm(int id) { Id = id; Name = "RV" + id; }
    public int Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;
}

/// <summary>进度列表一行（项行或当前项的步骤子行）。</summary>
public sealed partial class PerfStepRowVm : ObservableObject
{
    public PerfStepRowVm(string id, string title, string right) { Id = id; Title = title; _right = right; BaseRight = right; }
    public string Id { get; }
    public string Title { get; }
    public string BaseRight { get; }
    public bool IsSub { get; init; }
    public int Index { get; init; }
    [ObservableProperty] private string _right;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isWaiting;
}

public sealed partial class PerfEntryFieldVm : ObservableObject
{
    public PerfEntryFieldVm(string key, string label, string unit, string instrument, bool isText, double? min, double? max)
    { Key = key; Label = label; Unit = unit; Instrument = instrument; IsText = isText; Min = min; Max = max; }
    public string Key { get; }
    public string Label { get; }
    public string Unit { get; }
    public string Instrument { get; }
    public bool IsText { get; }
    public double? Min { get; }
    public double? Max { get; }
    public string ShortLabel { get; init; } = "";
    public string Range => Min.HasValue || Max.HasValue ? $"{Min?.ToString("0.##", CultureInfo.InvariantCulture) ?? ""}…{Max?.ToString("0.##", CultureInfo.InvariantCulture) ?? ""}" : "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _hasError;
}

public sealed partial class PerfCheckVm : ObservableObject
{
    public PerfCheckVm(string text) { Text = text; }
    public string Text { get; }
    [ObservableProperty] private bool _isChecked;
}

public sealed class PerfReportRowVm
{
    public PerfReportRowVm(string item, string channel, string setting, string measured, string error, string verdict, Verdict v)
    { Item = item; Channel = channel; Setting = setting; Measured = measured; Error = error; VerdictText = verdict; IsPass = v == Verdict.Pass && verdict.Length > 0; IsFail = v == Verdict.Fail && verdict.Length > 0; }
    public string Item { get; }
    public bool IsItem { get; init; }
    public string Channel { get; }
    public string Setting { get; }
    public string Measured { get; }
    public string Error { get; }
    public string VerdictText { get; }
    public bool IsPass { get; }
    public bool IsFail { get; }
}
