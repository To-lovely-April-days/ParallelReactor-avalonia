using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.PerfTest.Measure;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.PerfTest.Engine;

public sealed class PerfRunnerOptions
{
    /// <summary>引擎节拍（Hold / WaitStable 采样与进度推送间隔）。</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>t_s 的「之后不再越出」时长（方案 10 min）。</summary>
    public double SettlingHoldSec { get; init; } = 600;
    /// <summary>开始前要求持有控制权。</summary>
    public bool RequireControl { get; init; } = true;
    /// <summary>人工录入校验失败最多重试次数。</summary>
    public int EntryRetries { get; init; } = 3;
    public Func<string, string> IdempotencyKey { get; init; } = IdempotencyKeys.New;
    /// <summary>运行目录根（null → RunRequest.OutputRoot → ResultStore.DefaultRoot()）。</summary>
    public string? OutputRoot { get; init; }
}

/// <summary>
/// 性能测试引擎：顺序执行选中的测试项；每步推进度；Pause / Resume / Abort；超时；安全钩子（报警 / 急停 / 控制权丢失 → 自动暂停并询问，绝不自动继续）；
/// 结果累积并落盘（ResultStore），结束生成报告。单线程 async 状态机，所有等待经 <see cref="IClock"/>。
/// </summary>
public sealed class PerfRunner
{
    private readonly TestPlan _plan;
    private readonly IReactorApi _api;
    private readonly ITelemetryFeed _feed;
    private readonly IOperatorPrompt _prompt;
    private readonly IClock _clock;
    private readonly PerfRunnerOptions _opt;
    private readonly SampleRecorder _recorder = new();
    private readonly ConcurrentQueue<SafetyEvent> _safety = new();
    private readonly object _lock = new();

    private ResultStore? _store;
    private RunResult? _result;
    private RunRequest? _req;
    private CancellationTokenSource? _runCts;
    private volatile bool _paused, _abortRequested;
    private string? _abortReason;
    private TaskCompletionSource _resumeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _curItemId, _curStepId;
    private int _doneUnits, _totalUnits;
    private double _maxOverall;
    private bool _stirOnByEngine, _leakByEngine;
    private readonly HashSet<int> _heatedByEngine = new();
    private readonly HashSet<int> _autotuneByEngine = new();
    private DateTime? _selfEstopAt;
    private double? _approachDelta;
    /// <summary>引擎自己下发急停后，这段时间内的急停 / 控制权安全事件是预期内的，只记日志不暂停。</summary>
    public static readonly TimeSpan SelfEstopGrace = TimeSpan.FromSeconds(90);
    private readonly Dictionary<(string Item, int Iter, string Key), List<(int? Rv, double Value)>> _manual = new();
    private readonly Dictionary<string, ItemOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private PerfProgress? _last;
    private ItemResult? _curItem;
    private TestItem? _curDef;
    private StepResult? _curStep;
    private int _curIter, _curIterCount;
    private double? _curIterValue;

    public PerfRunner(TestPlan plan, IReactorApi api, ITelemetryFeed feed, IOperatorPrompt prompt, IClock? clock = null, PerfRunnerOptions? options = null)
    {
        _plan = plan; _api = api; _feed = feed; _prompt = prompt;
        _clock = clock ?? SystemClock.Instance;
        _opt = options ?? new PerfRunnerOptions();
    }

    public event Action<PerfProgress>? Progress;
    /// <summary>安全事件到达（界面弹红用；引擎同时自动暂停并通过 IOperatorPrompt.SafetyAsync 询问）。</summary>
    public event Action<SafetyEvent>? SafetyRaised;

    public RunResult? Result => _result;
    public PerfProgress? LastProgress => _last;
    public SampleRecorder Samples => _recorder;
    public bool IsPaused => _paused;
    public bool IsRunning => _runCts != null && !_runCts.IsCancellationRequested && _result?.Status is RunStatus.Running or RunStatus.Paused or RunStatus.WaitingConfirm or RunStatus.WaitingEntry or RunStatus.WaitingSafety;

    // ------------------------------------------------------------------ 控制

    public void Pause()
    {
        lock (_lock)
        {
            if (_paused) return;
            _paused = true;
            _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Log("pause", "操作员暂停");
        Report("已暂停", waiting: WaitingKind.Paused);
    }

    public void Resume()
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            if (!_paused) return;
            _paused = false;
            tcs = _resumeTcs;
        }
        Log("resume", "操作员继续");
        tcs.TrySetResult();
    }

    /// <summary>中止：停止本项通道的加热（及引擎开的搅拌 / 程序），结束会话，照常生成报告。</summary>
    public void Abort(string reason)
    {
        lock (_lock)
        {
            if (_abortRequested) return;
            _abortRequested = true;
            _abortReason = reason;
        }
        Log("abort", "请求中止：" + reason);
        _runCts?.Cancel();
        Resume();
    }

    // ------------------------------------------------------------------ 运行

    public async Task<RunResult> RunAsync(RunRequest req, CancellationToken ct)
    {
        if (_result != null) throw new InvalidOperationException("PerfRunner 只能运行一次");
        _req = req;
        var now = _clock.UtcNow;
        var runId = req.RunId ?? $"{req.TestIdPrefix}-{now.ToLocalTime():yyyyMMdd-HHmmss}";
        var root = _opt.OutputRoot ?? req.OutputRoot ?? ResultStore.DefaultRoot();
        _store = new ResultStore(root, runId);
        _result = new RunResult
        {
            RunId = runId, PlanId = _plan.PlanId, PlanTitle = _plan.Title, PlanVersion = _plan.Version, StartedAt = now, Status = RunStatus.Running,
            Operator = req.Operator, SoftwareVersion = req.SoftwareVersion, Environment = req.Environment, Channels = req.Channels.ToList(), SelectedItems = req.ItemIds.ToList(),
        };
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rct = _runCts.Token;

        // 选项解析
        var items = new List<TestItem>();
        foreach (var id in req.ItemIds)
        {
            var it = _plan.FindItem(id) ?? throw new ArgumentException("方案中无测试项 " + id);
            if (!it.Enabled) throw new ArgumentException($"测试项 {id} 未启用（{it.DisabledReason ?? "二期"}）");
            items.Add(it);
        }
        foreach (var c in req.Channels) if (c < 1 || c > 8) throw new ArgumentException("非法通道 " + c);
        _totalUnits = items.Sum(i => Units(i));

        _feed.Frame += OnFrame;
        _feed.Safety += OnSafety;
        try
        {
            Log("run", $"开始运行 {runId}：{items.Count} 项，通道 {string.Join(",", req.Channels)}");
            await OpeningAsync(rct).ConfigureAwait(false);

            if (_opt.RequireControl && !_api.CanControl)
            {
                _result.Status = RunStatus.Failed;
                _result.AbortReason = "未持有远程控制权，无法开始";
                Log("run", _result.AbortReason);
            }
            else
            {
                if (!req.NoSession) await BeginSessionAsync(rct).ConfigureAwait(false);
                for (int i = 0; i < items.Count; i++)
                {
                    if (_abortRequested) break;
                    await RunItemAsync(items[i], i, items.Count, rct).ConfigureAwait(false);
                }
                _result.Status = _abortRequested ? RunStatus.Aborted : RunStatus.Completed;
                if (_abortRequested) _result.AbortReason = _abortReason;
            }
        }
        catch (OperationCanceledException) when (_abortRequested)
        {
            _result.Status = RunStatus.Aborted;
            _result.AbortReason = _abortReason;
        }
        catch (OperationCanceledException)
        {
            _result.Status = RunStatus.Aborted;
            _result.AbortReason = "外部取消";
            _abortRequested = true;
        }
        catch (Exception ex)
        {
            _result.Status = RunStatus.Failed;
            _result.AbortReason = ex.GetType().Name + ": " + ex.Message;
            Log("error", _result.AbortReason);
        }
        finally
        {
            try { await FinishAsync().ConfigureAwait(false); }
            finally
            {
                _feed.Frame -= OnFrame;
                _feed.Safety -= OnSafety;
                _store.Dispose();
            }
        }
        return _result;
    }

    private async Task OpeningAsync(CancellationToken ct)
    {
        var r = _result!;
        var dev = await _api.GetDeviceAsync(ct).ConfigureAwait(false);
        if (dev != null)
        {
            r.DeviceModel = dev.Model; r.DeviceSerial = dev.Serial; r.HmiVersion = dev.HmiVersion; r.ApiVersion = dev.ApiVersion; r.BootId = dev.BootId;
            r.Simulated = dev.Simulated; r.ClockSynced = dev.ClockSynced;
        }
        var settings = await _api.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings != null)
        {
            var json = ApiJson.Serialize(settings);
            r.SettingsSnapshot = JsonDocument.Parse(json).RootElement.Clone();
            _store!.SaveSettingsSnapshot(json);
            _approachDelta = settings.Approach?.DeltaC;
        }
        var snap = await _api.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snap != null) { if (snap.Simulated) r.Simulated = true; OnFrame(snap); }
        Report("准备中", 0);
    }

    private async Task FinishAsync()
    {
        var r = _result!;
        var ct = CancellationToken.None;
        if (_abortRequested) await SafeStopAsync(ct).ConfigureAwait(false);
        if (r.SessionId != null)
        {
            var end = await _api.EndSessionAsync(ct).ConfigureAwait(false);
            Log("session", "结束会话 → " + end);
            try
            {
                using var fs = _store!.OpenHmiFile(r.SessionId + ".csv");
                if (await _api.DownloadSessionCsvAsync(r.SessionId, fs, ct).ConfigureAwait(false)) Log("session", "已下载触屏会话 CSV");
            }
            catch (Exception ex) { Log("session", "会话 CSV 下载失败：" + ex.Message); }
        }
        r.EndedAt = _clock.UtcNow;
        r.TotalFrames = _recorder.FrameCount;
        _curItemId = _curStepId = null;
        _store!.SaveResult(r);
        _store.WriteReport(r, _plan, _recorder);
        Log("run", $"结束：{r.Status}，报告 {_store.ReportPath}");
        Report(r.Status == RunStatus.Completed ? "已完成" : "已中止", 100, force: true);
    }

    /// <summary>中止时的安全停止：停止类命令，不急停（急停按钮在界面，不经引擎）。</summary>
    private async Task SafeStopAsync(CancellationToken ct)
    {
        var chans = new HashSet<int>(_heatedByEngine);
        chans.UnionWith(_autotuneByEngine);
        if (_curItem != null) foreach (var c in _curItem.Channels) chans.Add(c);
        foreach (var rv in chans.OrderBy(x => x))
        {
            var s = _recorder.Latest(rv);
            if (s?.ProgramState is "running" or "preparing" or "waitingGas") Log("abort", $"RV{rv} 中止程序 → " + await _api.AbortProgramAsync(rv, ct).ConfigureAwait(false));
            // 自整定不会被 heat/stop 停掉（方案 §2 T0 安全条）：引擎开过的自整定先取消
            var snapCh = _feed.Latest?.Channels.FirstOrDefault(c => c.Id == rv);
            if (_autotuneByEngine.Contains(rv) || snapCh?.Autotuning == true) Log("abort", $"RV{rv} 取消自整定 → " + await _api.CancelAutotuneAsync(rv, ct).ConfigureAwait(false));
            Log("abort", $"RV{rv} 停止加热 → " + await _api.StopHeatAsync(rv, ct).ConfigureAwait(false));
        }
        _autotuneByEngine.Clear();
        if (_leakByEngine || _feed.Latest?.LeakTest.Running == true) { Log("abort", "中止泄漏测试 → " + await _api.AbortLeakTestAsync(ct).ConfigureAwait(false)); _leakByEngine = false; }
        if (_stirOnByEngine) { Log("abort", "停止搅拌 → " + await _api.StopStirAsync(ct).ConfigureAwait(false)); _stirOnByEngine = false; }
    }

    private async Task BeginSessionAsync(CancellationToken ct)
    {
        var r = _result!;
        var testId = r.RunId;
        var o = await _api.BeginSessionAsync(testId, $"性能测试 {_plan.PlanId}", _opt.IdempotencyKey("perf-sess"), ct).ConfigureAwait(false);
        if (!o.Ok && o.Code == OpCodes.SessionActive)
        {
            var dec = await _prompt.ConfirmAsync(new ConfirmRequest("", "session", "已有活动会话", "触屏侧已有正在进行的会话。结束它并新建本次测试会话？（否 → 本次不建会话，仅本地记录）", null, false, Array.Empty<int>()), ct).ConfigureAwait(false);
            if (dec.Confirmed)
            {
                await _api.EndSessionAsync(ct).ConfigureAwait(false);
                o = await _api.BeginSessionAsync(testId, $"性能测试 {_plan.PlanId}", _opt.IdempotencyKey("perf-sess"), ct).ConfigureAwait(false);
            }
        }
        if (o.Ok) { r.SessionId = o.Message ?? testId; r.SessionTestId = testId; Log("session", $"会话开始 {r.SessionId} opId={o.OpId}"); }
        else Log("session", "未建会话：" + o);
    }

    private async Task MarkerAsync(string text, CancellationToken ct)
    {
        if (_result!.SessionId == null) return;
        var o = await _api.AddMarkerAsync(text, _opt.IdempotencyKey("perf-mk"), ct).ConfigureAwait(false);
        Log("marker", text + (o.Ok ? "" : " （打点失败：" + o + "）"));
    }

    // ------------------------------------------------------------------ 项

    private static int Units(TestItem it) => Math.Max(1, it.Steps.Count) * (it.Repeat?.Iterations ?? 1);

    private async Task RunItemAsync(TestItem def, int index, int count, CancellationToken ct)
    {
        var chans = def.ChannelSelectable ? _req!.Channels.Where(c => def.TargetChannels.Contains(c)).ToList() : def.TargetChannels.ToList();
        if (def.ChannelSelectable && chans.Count == 0) chans = def.TargetChannels.ToList();
        var ir = new ItemResult { ItemId = def.Id, Title = def.Title, SourceRef = def.SourceRef, Channels = chans, HardCriterion = def.HardCriterion, StartedAt = Now() };
        _result!.Items.Add(ir);
        _curItem = ir; _curDef = def; _curItemId = def.Id;
        _curIterCount = def.Repeat?.Iterations ?? 1;

        // 前置：本次运行中未完成（失败 / 中止 / 被跳过）的前置项 → 跳过（跳过也算阻断，否则 R0 → T1 → T4/T5 的链会漏）
        var blocked = def.Preconditions.FirstOrDefault(p => _outcomes.TryGetValue(p, out var o) && o != ItemOutcome.Completed);
        // §0.4 门禁：带热 / 带压项必须有已通过的 R0（本次运行里完成，或界面声明早先已签字）；方案里没有 R0 项（合成自检方案）则不门禁
        string? gateNote = null;
        if (blocked == null && def.NeedsPrecheck() && _plan.FindItem("R0") != null && !_req!.R0SignedOff
            && !(_outcomes.TryGetValue("R0", out var r0) && r0 == ItemOutcome.Completed))
            gateNote = "§0.4 前置检查表（R0）未通过 / 未入选，带热 / 带压项不得运行";
        if (blocked != null || gateNote != null)
        {
            ir.Outcome = ItemOutcome.Skipped; ir.Note = gateNote ?? $"前置 {blocked} 未通过，跳过"; ir.EndedAt = Now();
            _outcomes[def.Id] = ir.Outcome;
            _doneUnits += Units(def);
            Log("item", $"{def.Id} 跳过：{ir.Note}", def.Id);
            return;
        }

        Log("item", $"{def.Id} {def.Title} 开始（RV{string.Join(",", chans)}）", def.Id);
        await MarkerAsync($"{def.Id} 开始", ct).ConfigureAwait(false);
        if (def.RecordComm) { ir.CommStart = CommSummary(await _api.GetCommAsync(ct).ConfigureAwait(false)); Log("comm", "开始：" + ir.CommStart, def.Id); }
        var startFrames = _recorder.FrameCount;
        bool itemAborted = false;
        for (int iter = 1; iter <= _curIterCount && !itemAborted; iter++)
        {
            _curIter = iter;
            _curIterValue = def.Repeat?.Values is { Count: > 0 } v ? v[iter - 1] : null;
            if (_curIterCount > 1) await MarkerAsync($"{def.Id} 第 {iter}/{_curIterCount} 轮" + (_curIterValue.HasValue ? $" {def.Repeat!.Variable}={Metrics.Fmt(_curIterValue.Value)}" : ""), ct).ConfigureAwait(false);
            for (int s = 0; s < def.Steps.Count; s++)
            {
                if (_abortRequested) { itemAborted = true; break; }
                await CheckpointAsync(ct).ConfigureAwait(false);
                if (_abortRequested) { itemAborted = true; break; }
                var step = def.Steps[s];
                var sr = new StepResult { StepId = step.Id, Kind = step.Kind.ToString(), Title = Resolve(step.Title, null), Iteration = _curIterCount > 1 ? iter : null };
                ir.Steps.Add(sr);
                _curStep = sr; _curStepId = step.Id;
                Report(PhaseFor(step), 0, stepIndex: s, stepCount: def.Steps.Count, itemIndex: index, itemCount: count);
                var outcome = await RunStepAsync(def, step, sr, chans, ct).ConfigureAwait(false);
                _doneUnits++;
                if (outcome == StepFlow.Continue) continue;
                if (outcome == StepFlow.SkipItem) { ir.Note ??= $"步骤 {step.Id} 要求跳过本项"; break; }
                if (outcome == StepFlow.AbortItem)
                {
                    itemAborted = true;
                    ir.Note ??= $"步骤 {step.Id} 失败：{sr.Note}";
                    break;
                }
            }
        }
        if (_abortRequested) { ir.Outcome = ItemOutcome.Aborted; ir.Note ??= _abortReason; }
        else if (itemAborted)
        {
            ir.Outcome = ItemOutcome.Aborted;
            // 项级中止：停止该项通道的加热（停止类，无需租约）
            foreach (var rv in chans.Where(_heatedByEngine.Contains)) Log("item", $"RV{rv} 停止加热 → " + await _api.StopHeatAsync(rv, ct).ConfigureAwait(false), def.Id);
            if (_stirOnByEngine) { Log("item", "停止搅拌 → " + await _api.StopStirAsync(ct).ConfigureAwait(false), def.Id); _stirOnByEngine = false; }
        }
        else ir.Outcome = ItemOutcome.Completed;
        if (ir.Outcome == ItemOutcome.Completed && ir.Steps.Any(x => x.Status is StepStatus.Failed or StepStatus.TimedOut)) ir.Note ??= "部分步骤失败 / 超时，见步骤时间线";
        // 判定：只在方案给硬性判据（Compare.Hard）时填
        var hard = ir.Steps.Where(x => x.ComparePassed.HasValue && def.Steps.Any(d => d.Id == x.StepId && d.Compare is { Hard: true })).ToList();
        if (hard.Count > 0) ir.Verdict = hard.All(x => x.ComparePassed == true) ? Verdict.Pass : Verdict.Fail;
        if (def.RecordComm) { ir.CommEnd = CommSummary(await _api.GetCommAsync(ct).ConfigureAwait(false)); Log("comm", "结束：" + ir.CommEnd, def.Id); }
        ir.EndedAt = Now();
        ir.FrameCount = _recorder.FrameCount - startFrames;
        _outcomes[def.Id] = ir.Outcome;
        await MarkerAsync($"{def.Id} 结束 · {ir.Outcome}", ct).ConfigureAwait(false);
        Log("item", $"{def.Id} 结束：{ir.Outcome}{(ir.Note != null ? " · " + ir.Note : "")}", def.Id);
        _store!.SaveResult(_result);
        _curStep = null; _curStepId = null; _curItem = null; _curItemId = null; _curDef = null;
    }

    private enum StepFlow { Continue, AbortItem, SkipItem }

    private StepFlow Fail(TestStep step, StepResult sr, StepStatus status, string note)
    {
        sr.Status = status; sr.Note = note;
        Log(status == StepStatus.TimedOut ? "timeout" : "step-failed", $"{step.Id} {note}", _curItemId, step.Id);
        return step.OnFail switch { "continue" => StepFlow.Continue, "skipItem" => StepFlow.SkipItem, _ => StepFlow.AbortItem };
    }

    private async Task<StepFlow> RunStepAsync(TestItem def, TestStep step, StepResult sr, List<int> itemChans, CancellationToken ct)
    {
        sr.StartedAt = Now(); sr.Status = StepStatus.Running;
        var chans = step.Channels == null ? itemChans : step.Channels.ToList();
        var t0 = _clock.UtcNow;
        double paused0 = _pausedAtStepStart = _pausedTotalSec;
        StepFlow flow;
        try
        {
            flow = step.Kind switch
            {
                StepKind.Instruction => await InstructionAsync(step, sr, chans, ct).ConfigureAwait(false),
                StepKind.Checklist => await ChecklistAsync(step, sr, chans, ct).ConfigureAwait(false),
                StepKind.ManualEntry => await ManualEntryAsync(def, step, sr, chans, ct).ConfigureAwait(false),
                StepKind.Command => await CommandAsync(step, sr, chans, ct).ConfigureAwait(false),
                StepKind.WaitStable => await WaitStableAsync(step, sr, chans, ct).ConfigureAwait(false),
                StepKind.Hold => await HoldAsync(step, sr, ct).ConfigureAwait(false),
                StepKind.Measure => MeasureStep(def, step, sr, chans),
                StepKind.Compare => await CompareAsync(step, sr, ct).ConfigureAwait(false),
                StepKind.Marker => await MarkerStepAsync(step, sr, ct).ConfigureAwait(false),
                StepKind.SessionBegin => await SessionBeginStepAsync(sr, ct).ConfigureAwait(false),
                StepKind.SessionEnd => await SessionEndStepAsync(sr, ct).ConfigureAwait(false),
                _ => Fail(step, sr, StepStatus.Failed, "未知步骤种类"),
            };
        }
        catch (OperationCanceledException) when (_abortRequested)
        {
            sr.Status = StepStatus.Aborted; sr.Note = _abortReason;
            flow = StepFlow.AbortItem;
        }
        sr.EndedAt = Now();
        sr.PausedSec = _pausedTotalSec - paused0;
        sr.DurationSec = Math.Max(0, (_clock.UtcNow - t0).TotalSeconds - sr.PausedSec);
        if (sr.Status == StepStatus.Running) sr.Status = StepStatus.Done;
        return flow;
    }

    // ------------------------------------------------------------------ 各步

    private async Task<StepFlow> InstructionAsync(TestStep step, StepResult sr, List<int> chans, CancellationToken ct)
    {
        var text = Resolve(step.Text ?? "", null);
        sr.Status = StepStatus.WaitingConfirm;
        var req = new ConfirmRequest(_curItemId!, step.Id, sr.Title, text, null, false, chans);
        var res = await WaitPromptAsync(() => _prompt.ConfirmAsync(req, ct), "等待确认", WaitingKind.Confirm, $"{sr.Title}：{text}", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(res.Note)) _curItem!.OperatorNotes.Add($"{step.Id}：{res.Note}");
        if (!res.Confirmed)
        {
            sr.Status = StepStatus.Skipped; sr.Note = "操作员未确认 / 跳过";
            return step.OnFail == "abortItem" ? Fail(step, sr, StepStatus.Skipped, "操作员未确认") : StepFlow.Continue;
        }
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;
    }

    private async Task<StepFlow> ChecklistAsync(TestStep step, StepResult sr, List<int> chans, CancellationToken ct)
    {
        var items = step.ChecklistItems ?? new List<string>();
        sr.Status = StepStatus.WaitingConfirm;
        var req = new ConfirmRequest(_curItemId!, step.Id, sr.Title, Resolve(step.Text ?? "", null), items, step.TwoSigners, chans);
        var res = await WaitPromptAsync(() => _prompt.ConfirmAsync(req, ct), "等待检查表确认", WaitingKind.Confirm, sr.Title, ct).ConfigureAwait(false);
        sr.Checked = res.Checked?.ToList() ?? (res.Confirmed ? items.Select(_ => true).ToList() : items.Select(_ => false).ToList());
        sr.Signer1 = res.Signer1; sr.Signer2 = res.Signer2;
        if (!string.IsNullOrWhiteSpace(res.Note)) _curItem!.OperatorNotes.Add($"{step.Id}：{res.Note}");
        bool allChecked = res.Confirmed && sr.Checked.Count >= items.Count && sr.Checked.Take(items.Count).All(x => x);
        bool signed = !step.TwoSigners || (!string.IsNullOrWhiteSpace(res.Signer1) && !string.IsNullOrWhiteSpace(res.Signer2));
        if (!allChecked) return Fail(step, sr, StepStatus.Failed, $"检查表未全部完成（{sr.Checked.Count(x => x)}/{items.Count}）");
        if (!signed) return Fail(step, sr, StepStatus.Failed, "检查表需两人签字");
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;
    }

    private async Task<StepFlow> ManualEntryAsync(TestItem def, TestStep step, StepResult sr, List<int> chans, CancellationToken ct)
    {
        var fields = (step.Fields ?? new List<ManualField>()).Select(f => new EntryField(f.Key, Resolve(f.Label, null), f.Unit, f.Instrument, f.Repeat, f.PerChannel, f.Min, f.Max, f.IsText, f.Optional)).ToList();
        sr.Status = StepStatus.WaitingEntry;
        string? error = null;
        for (int attempt = 0; attempt <= _opt.EntryRetries; attempt++)
        {
            var req = new EntryRequest(_curItemId!, step.Id, sr.Title, Resolve(step.Text, null), fields, chans, error);
            var res = await WaitPromptAsync(() => _prompt.EnterAsync(req, ct), "等待人工录入", WaitingKind.Entry, $"{sr.Title}：{string.Join("、", fields.Select(f => f.Label + (f.Unit.Length > 0 ? "(" + f.Unit + ")" : "")))}", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(res.Note)) _curItem!.OperatorNotes.Add($"{step.Id}：{res.Note}");
            if (res.Cancelled) return step.OnFail == "abortItem" ? Fail(step, sr, StepStatus.Skipped, "操作员取消录入") : StepFlow.Continue;
            error = Validate(fields, chans, res);
            if (error == null)
            {
                Store(def, step, fields, chans, res);
                sr.Status = StepStatus.Done;
                return StepFlow.Continue;
            }
            Log("entry", $"{step.Id} 录入校验失败：{error}", _curItemId, step.Id);
        }
        return Fail(step, sr, StepStatus.Failed, "录入多次校验失败：" + error);
    }

    private static string? Validate(List<EntryField> fields, List<int> chans, EntryResult res)
    {
        foreach (var f in fields)
        {
            if (f.IsText)
            {
                if (!f.Optional && (res.Texts == null || !res.Texts.TryGetValue(f.Key, out var t) || string.IsNullOrWhiteSpace(t))) return $"缺少「{f.Label}」";
                continue;
            }
            int need = f.Repeat * (f.PerChannel ? Math.Max(1, chans.Count) : 1);
            if (res.Values == null || !res.Values.TryGetValue(f.Key, out var vals))
            {
                if (f.Optional) continue;
                return $"缺少「{f.Label}」";
            }
            if (vals.Count != need) return $"「{f.Label}」需要 {need} 个读数，收到 {vals.Count}";
            foreach (var v in vals)
            {
                if (double.IsNaN(v)) return $"「{f.Label}」含无效数值";
                if (f.Min.HasValue && v < f.Min) return $"「{f.Label}」{v} 低于下限 {f.Min}";
                if (f.Max.HasValue && v > f.Max) return $"「{f.Label}」{v} 高于上限 {f.Max}";
            }
        }
        return null;
    }

    private void Store(TestItem def, TestStep step, List<EntryField> fields, List<int> chans, EntryResult res)
    {
        var at = Now();
        foreach (var f in fields)
        {
            // 回填运行环境记录（§0.5 公共记录 → 报告头）
            var envKey = step.Fields?.FirstOrDefault(x => x.Key == f.Key)?.EnvKey;
            if (envKey != null)
            {
                var env = _result!.Environment;
                double? num = res.Values != null && res.Values.TryGetValue(f.Key, out var nv) && nv.Count > 0 ? nv[0] : null;
                string? txt = res.Texts != null && res.Texts.TryGetValue(f.Key, out var tv) ? tv : null;
                switch (envKey)
                {
                    case "ambientTempC": env.AmbientTempC = num ?? env.AmbientTempC; break;
                    case "humidityPct": env.HumidityPct = num ?? env.HumidityPct; break;
                    case "atmPressureKpa": env.AtmPressureKpa = num ?? env.AtmPressureKpa; break;
                    case "supplyVoltageV": env.SupplyVoltageV = num ?? env.SupplyVoltageV; break;
                    case "assemblyNo": env.AssemblyNo = txt ?? (num.HasValue ? Metrics.Fmt(num.Value) : env.AssemblyNo); break;
                    case "mediumAndMassG": env.MediumAndMassG = txt ?? (num.HasValue ? Metrics.Fmt(num.Value) : env.MediumAndMassG); break;
                }
            }
            if (f.IsText)
            {
                if (res.Texts != null && res.Texts.TryGetValue(f.Key, out var t))
                    AddManual(new ManualEntryRecord { At = at, ItemId = def.Id, StepId = step.Id, Iteration = _curIterCount > 1 ? _curIter : null, Key = f.Key, Label = f.Label, Unit = f.Unit, Instrument = f.Instrument, Index = 1, Text = t });
                continue;
            }
            if (res.Values == null || !res.Values.TryGetValue(f.Key, out var vals)) continue;
            var list = _manual.TryGetValue((def.Id, _curIter, f.Key), out var l) ? l : _manual[(def.Id, _curIter, f.Key)] = new List<(int?, double)>();
            int k = 0;
            var chanList = f.PerChannel ? chans : new List<int> { 0 };
            foreach (var rv in chanList)
                for (int i = 0; i < f.Repeat && k < vals.Count; i++, k++)
                {
                    int? ch = rv == 0 ? null : rv;
                    list.Add((ch, vals[k]));
                    AddManual(new ManualEntryRecord { At = at, ItemId = def.Id, StepId = step.Id, Iteration = _curIterCount > 1 ? _curIter : null, Key = f.Key, Label = f.Label, Unit = f.Unit, Instrument = f.Instrument, Channel = ch, Index = i + 1, Value = vals[k] });
                }
        }
    }

    private void AddManual(ManualEntryRecord m) { _curItem!.ManualEntries.Add(m); _store!.AppendManualEntry(m); }

    private async Task<StepFlow> CommandAsync(TestStep step, StepResult sr, List<int> chans, CancellationToken ct)
    {
        var spec = step.Command!;
        var targets = step.PerChannel && !CommandOps.IsGlobal(spec.Op) ? chans : new List<int> { 0 };
        foreach (var rv in targets)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (_abortRequested) { sr.Status = StepStatus.Aborted; return StepFlow.AbortItem; }
            var args = spec.Args ?? new Dictionary<string, JsonElement>();
            if (spec.Op == CommandOps.Estop) _selfEstopAt = _clock.UtcNow;   // 先标记：WS control:invalidated 可能先于 REST 响应到达
            var o = await ExecuteAsync(spec.Op, rv, args, chans, ct).ConfigureAwait(false);
            var rec = new CommandRecord { At = Now(), Op = spec.Op, Channel = rv == 0 ? null : rv, Args = ArgsText(args), Ok = o.Ok, OpId = o.OpId, Status = o.Status, HttpStatus = o.HttpStatus, Code = o.Code, Message = o.Message, OpEventSeq = o.OpEventSeq };
            sr.Commands.Add(rec);
            bool ok = o.Ok || (o.Code != null && spec.AllowCodes != null && spec.AllowCodes.Contains(o.Code));
            if (ok && o.Ok && spec.ExpectStatus is { Count: > 0 } && !spec.ExpectStatus.Contains(o.Status)) ok = false;
            Log(ok ? "command" : "command-failed", $"{spec.Op}{(rv != 0 ? " RV" + rv : "")} {ArgsText(args)} → {o}", _curItemId, step.Id);
            if (!ok) return Fail(step, sr, StepStatus.Failed, $"{spec.Op}{(rv != 0 ? " RV" + rv : "")} 被拒：{o.HttpStatus} {o.Code} {o.Message}");
            if (o.Ok)
            {
                if (spec.Op is CommandOps.HeatStart or CommandOps.Setpoint && rv != 0) _heatedByEngine.Add(rv);
                if (spec.Op is CommandOps.HeatStop or CommandOps.StopChannel or CommandOps.Disable && rv != 0) _heatedByEngine.Remove(rv);
                if (spec.Op == CommandOps.Stir) _stirOnByEngine = !(args.TryGetValue("on", out var on) && on.ValueKind == JsonValueKind.False);
                if (spec.Op is CommandOps.StirStop or CommandOps.StopAll or CommandOps.Estop) _stirOnByEngine = false;
                if (spec.Op == CommandOps.AutotuneStart && rv != 0) _autotuneByEngine.Add(rv);
                if (spec.Op is CommandOps.AutotuneCancel or CommandOps.HeatStop or CommandOps.StopChannel && rv != 0) _autotuneByEngine.Remove(rv);
                if (spec.Op == CommandOps.LeakTestStart) _leakByEngine = true;
                if (spec.Op == CommandOps.LeakTestAbort) _leakByEngine = false;
                if (spec.Op is CommandOps.Estop or CommandOps.StopAll) { _heatedByEngine.Clear(); _autotuneByEngine.Clear(); if (spec.Op == CommandOps.Estop) _selfEstopAt = _clock.UtcNow; }
            }
        }
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;
    }

    private Task<ApiOutcome> ExecuteAsync(string op, int rv, Dictionary<string, JsonElement> args, List<int> chans, CancellationToken ct)
    {
        double? D(string k) => args.TryGetValue(k, out var e) ? ResolveNumber(e) : null;
        bool? Bo(string k) => args.TryGetValue(k, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        string? S(string k) => args.TryGetValue(k, out var e) && e.ValueKind == JsonValueKind.String ? Resolve(e.GetString(), null) : null;
        switch (op)
        {
            case CommandOps.Enable: return _api.EnableChannelAsync(rv, ct);
            case CommandOps.Disable: return _api.DisableChannelAsync(rv, ct);
            case CommandOps.StopChannel: return _api.StopChannelAsync(rv, ct);
            case CommandOps.HeatStart: return _api.StartHeatAsync(rv, D("tempC"), ct);
            case CommandOps.Setpoint: return _api.SetSetpointAsync(rv, D("tempC") ?? double.NaN, ct);
            case CommandOps.HeatStop: return _api.StopHeatAsync(rv, ct);
            case CommandOps.Stir: return _api.SetStirAsync(Bo("on") ?? true, D("rpm") is { } r ? (int)Math.Round(r) : null, ct);
            case CommandOps.StirStop: return _api.StopStirAsync(ct);
            case CommandOps.ProgramPut:
            {
                var steps = args.TryGetValue("steps", out var s) ? Resolve(s.GetRawText(), null) : "[]";
                return _api.ReplaceProgramAsync(rv, "{\"steps\":" + steps + "}", ct);
            }
            case CommandOps.ProgramRun: return _api.RunProgramAsync(rv, _result!.SessionId, _opt.IdempotencyKey($"perf-run-rv{rv}"), ct);
            case CommandOps.ProgramAbort: return _api.AbortProgramAsync(rv, ct);
            case CommandOps.AutotuneStart: return _api.StartAutotuneAsync(rv, D("tempC") ?? double.NaN, ct);
            case CommandOps.AutotuneCancel: return _api.CancelAutotuneAsync(rv, ct);
            case CommandOps.LeakTestStart:
            {
                var list = args.TryGetValue("channels", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetInt32()).ToArray() : chans.ToArray();
                return _api.StartLeakTestAsync(list, D("testPsi"), _opt.IdempotencyKey("perf-leak"), ct);
            }
            case CommandOps.LeakTestAbort: return _api.AbortLeakTestAsync(ct);
            case CommandOps.Estop: return _api.EstopAsync(S("note") ?? "性能测试 F4", ct);
            case CommandOps.StopAll: return _api.StopAllAsync(Bo("stopStir") ?? true, ct);
            case CommandOps.ControlRequest: return _api.RequestControlAsync(ct);
            default: return Task.FromResult(ApiOutcome.Fail("UNKNOWN_OP", op));
        }
    }

    private async Task<StepFlow> WaitStableAsync(TestStep step, StepResult sr, List<int> chans, CancellationToken ct)
    {
        var signal = step.Signal!;
        var window = TimeSpan.FromSeconds(step.WindowSec ?? 600);
        var timeout = TimeSpan.FromSeconds(step.TimeoutSec ?? 3600);
        var targets = step.PerChannel ? chans : new List<int> { chans.FirstOrDefault(1) };
        var start = _clock.UtcNow;
        double waited = 0;
        while (true)
        {
            await TickAsync(ct).ConfigureAwait(false);
            if (_abortRequested) { sr.Status = StepStatus.Aborted; return StepFlow.AbortItem; }
            waited = (_clock.UtcNow - start).TotalSeconds - (sr.PausedSec = _pausedTotalSec - _pausedAtStepStart);
            var now = Now();
            bool all = true; double stableSec = double.MaxValue;
            foreach (var rv in targets)
            {
                var pts = _recorder.Recent(rv, signal, now, window);
                var refv = Reference(step.Reference, step.Value, rv);
                bool ok = pts.Count >= 2 && (now - (now - window)).TotalSeconds > 0 && pts[0].Sec <= Math.Max(2.0, window.TotalSeconds * 0.1) && Metrics.IsStable(pts, refv, step.Band, step.MaxSlopePerMin);
                // 稳定计时：窗口内带内持续秒数（从末尾往前数）
                double sec = 0;
                if (step.Band.HasValue && refv.HasValue)
                    for (int i = pts.Count - 1; i >= 0; i--) { if (Math.Abs(pts[i].Y - refv.Value) > step.Band.Value) break; sec = pts[^1].Sec - pts[i].Sec; }
                else sec = pts.Count > 1 ? pts[^1].Sec - pts[0].Sec : 0;
                stableSec = Math.Min(stableSec, sec);
                all &= ok;
            }
            if (stableSec == double.MaxValue) stableSec = 0;
            double pct = Math.Min(99, stableSec / window.TotalSeconds * 100);
            Report(stableSec > 0 ? "稳定计时" : PhaseFor(step), pct, countdown: TimeSpan.FromSeconds(Math.Max(0, window.TotalSeconds - stableSec)));
            if (all)
            {
                sr.Note = $"实际等待 {Metrics.Fmt(waited / 60, 1)} min";
                sr.Status = StepStatus.Done;
                return StepFlow.Continue;
            }
            if (waited >= timeout.TotalSeconds) return Fail(step, sr, StepStatus.TimedOut, $"等待稳定超时（{Metrics.Fmt(timeout.TotalMinutes, 1)} min），继续记录当前状态");
        }
    }

    private async Task<StepFlow> HoldAsync(TestStep step, StepResult sr, CancellationToken ct)
    {
        var duration = step.DurationSec ?? 0;
        var start = _clock.UtcNow;
        int frames0 = _recorder.FrameCount;
        while (true)
        {
            double elapsed = (_clock.UtcNow - start).TotalSeconds - (_pausedTotalSec - _pausedAtStepStart);
            if (elapsed >= duration) break;
            Report(PhaseFor(step), Math.Min(99.9, elapsed / duration * 100), countdown: TimeSpan.FromSeconds(duration - elapsed));
            await TickAsync(ct).ConfigureAwait(false);
            if (_abortRequested) { sr.Status = StepStatus.Aborted; return StepFlow.AbortItem; }
        }
        sr.Note = $"采样 {_recorder.FrameCount - frames0} 帧";
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;
    }

    private StepFlow MeasureStep(TestItem def, TestStep step, StepResult sr, List<int> chans)
    {
        int n = 0;
        foreach (var m in step.Metrics!)
        {
            var (from, to, winStep) = Window(m.Window);
            if (m.Formula == MetricFormulas.Duration)
            {
                Add(m, null, winStep?.DurationSec ?? double.NaN, 0); n++;
                continue;
            }
            var targets = m.PerChannel ? chans : new List<int>();
            if (m.PerChannel)
            {
                foreach (var rv in targets)
                {
                    var (val, cnt) = Compute(m, rv, from, to);
                    Add(m, rv, val, cnt); n++;
                }
            }
            else
            {
                // 跨通道：对每通道取 mean 后做极差；其他公式则把所有通道样本合并
                if (m.Formula == MetricFormulas.CrossChannelRange)
                {
                    var means = chans.Select(rv => Compute(m, rv, from, to, MetricFormulas.Mean).Value).ToList();
                    Add(m, null, Metrics.Range(means), means.Count); n++;
                }
                else
                {
                    var all = new List<double>();
                    foreach (var rv in chans) { var (v, _) = Compute(m, rv, from, to); if (!double.IsNaN(v)) all.Add(v); }
                    Add(m, null, all.Count == 0 ? double.NaN : all.Average(), all.Count); n++;
                }
            }
        }
        sr.Note = $"{n} 个指标";
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;

        void Add(MetricSpec m, int? rv, double value, int cnt) => _curItem!.Measures.Add(new MeasureValue
        {
            Key = m.Key, Label = Resolve(m.Label, rv), Unit = m.Unit, Formula = m.Formula, Channel = rv, Value = value, SampleCount = cnt,
            Iteration = _curIterCount > 1 ? _curIter : null, IterationValue = _curIterValue, Reference5 = m.Reference5, Direction = m.Direction,
            Note = double.IsNaN(value) ? "数据不足" : null,
        });
    }

    private (DateTime From, DateTime To, StepResult? Step) Window(string? w)
    {
        var item = _curItem!;
        var to = Now();
        if (w != null && w.StartsWith("step:", StringComparison.Ordinal))
        {
            var id = w.Substring(5);
            var s = item.Steps.LastOrDefault(x => x.StepId == id && (x.Iteration ?? _curIter) == _curIter && x.StartedAt.HasValue) ?? item.Steps.LastOrDefault(x => x.StepId == id && x.StartedAt.HasValue);
            if (s != null) return (s.StartedAt!.Value, s.EndedAt ?? to, s);
        }
        if (w != null && w.StartsWith("from:", StringComparison.Ordinal))
        {
            var id = w.Substring(5);
            var s = item.Steps.LastOrDefault(x => x.StepId == id && (x.Iteration ?? _curIter) == _curIter && x.StartedAt.HasValue) ?? item.Steps.LastOrDefault(x => x.StepId == id && x.StartedAt.HasValue);
            if (s != null) return (s.StartedAt!.Value, to, null);
        }
        if (w == "iteration")
        {
            var first = item.Steps.FirstOrDefault(x => (x.Iteration ?? 1) == _curIter && x.StartedAt.HasValue);
            return (first?.StartedAt ?? item.StartedAt ?? to, to, null);
        }
        return (item.StartedAt ?? to, to, null);
    }

    private (double Value, int Count) Compute(MetricSpec m, int rv, DateTime from, DateTime to, string? formulaOverride = null)
    {
        var formula = formulaOverride ?? m.Formula;
        // 人工录入信号
        if (m.Signal != null && m.Signal.StartsWith("manual:", StringComparison.Ordinal))
        {
            var vals = ManualValues(m.Signal.Substring(7), rv);
            if (vals.Count == 0) return (double.NaN, 0);
            var refv = Reference(m.Reference, m.Value, rv);
            return formula switch
            {
                MetricFormulas.RpmError => (refv.HasValue ? Metrics.RpmErrorPct(vals, refv.Value) : double.NaN, vals.Count),
                MetricFormulas.Mean => (vals.Average(), vals.Count),
                MetricFormulas.Sigma => (Metrics.Describe(vals).Sigma, vals.Count),
                MetricFormulas.Min => (vals.Min(), vals.Count),
                MetricFormulas.Max => (vals.Max(), vals.Count),
                MetricFormulas.PeakToPeak => (Metrics.Describe(vals).Fluctuation, vals.Count),
                MetricFormulas.MeanDeviation => (refv.HasValue ? vals.Average() - refv.Value : double.NaN, vals.Count),
                MetricFormulas.Count => (vals.Count, vals.Count),
                _ => (vals.Average(), vals.Count),
            };
        }
        var pts = _recorder.Series(rv, m.Signal ?? "tempC", from, to);
        var st = Metrics.Describe(pts.Select(p => p.Y).ToList());
        var reference = Reference(m.Reference, m.Value, rv, to);
        double sp = reference ?? double.NaN;
        double target = m.Reference == "iteration" && _curIterValue.HasValue ? _curIterValue.Value : m.Value ?? sp;
        double band = EffectiveBand(m, target);
        double v = formula switch
        {
            MetricFormulas.Overshoot => Metrics.Overshoot(pts, sp),
            MetricFormulas.TimeToBand => Metrics.TimeToBand(pts, sp, band),
            MetricFormulas.SettlingTime => Metrics.SettlingTime(pts, sp, band, _opt.SettlingHoldSec),
            MetricFormulas.HeatingRate => Metrics.HeatingRate(pts, sp, m.Delta ?? _approachDelta ?? 0),
            MetricFormulas.Mean => st.Mean,
            MetricFormulas.Sigma => st.Sigma,
            MetricFormulas.Min => st.Min,
            MetricFormulas.Max => st.Max,
            MetricFormulas.PeakToPeak => st.Fluctuation,
            MetricFormulas.MeanDeviation => st.Mean - sp,
            MetricFormulas.MaxDeviation => st.Max - sp,
            MetricFormulas.MinDeviation => st.Min - sp,
            MetricFormulas.LinearSlopePerHour => Metrics.FitPerHour(pts).Slope,
            MetricFormulas.LinearR2 => Metrics.Fit(pts).R2,
            MetricFormulas.TimeToValue => Metrics.TimeToValue(pts, target - (m.Band.HasValue || m.BandPct.HasValue ? band : 0)),
            MetricFormulas.Count => pts.Count,
            MetricFormulas.CrossChannelRange => st.Mean,
            _ => double.NaN,
        };
        return (v, pts.Count);
    }

    /// <summary>有效带宽 = max(Band, BandPct% × 目标)；两者都没有时 0.5（t_r / t_s 的默认）。</summary>
    public static double EffectiveBand(MetricSpec m, double target)
    {
        double b = m.Band ?? (m.BandPct.HasValue ? 0 : 0.5);
        if (m.BandPct.HasValue && !double.IsNaN(target)) b = Math.Max(b, Math.Abs(target) * m.BandPct.Value / 100.0);
        return b;
    }

    private static string CommSummary(CommDto? c)
    {
        if (c == null) return "（GET /device/comm 不可用）";
        string M(string n, ModuleCommSnap? m) => m == null ? $"{n}:—" : $"{n}:{(m.Online ? "在线" : "离线")} polls={m.Polls} fails={m.Fails}";
        return string.Join(" · ", M("AI-8", c.Temp), M("8AI", c.Pressure), M("DM2C", c.Stir), M("IO16R", c.Io));
    }

    private List<double> ManualValues(string key, int rv)
    {
        if (!_manual.TryGetValue((_curItemId!, _curIter, key), out var list)) return new List<double>();
        var mine = list.Where(x => x.Rv == rv).Select(x => x.Value).ToList();
        return mine.Count > 0 ? mine : list.Where(x => x.Rv == null).Select(x => x.Value).ToList();
    }

    /// <summary>参考值：sp（通道当前 SP）/ value / iteration / manual:&lt;key&gt;（录入均值）。</summary>
    private double? Reference(string? reference, double? value, int rv, DateTime? at = null)
    {
        switch (reference)
        {
            case null: return value;
            case "sp": return _recorder.LatestSp(rv) ?? _feed.Latest?.Channels.FirstOrDefault(c => c.Id == rv)?.TempSpC;
            case "value": return value;
            case "iteration": return _curIterValue ?? value;
            default:
                if (reference.StartsWith("manual:", StringComparison.Ordinal)) { var v = ManualValues(reference.Substring(7), rv); return v.Count == 0 ? null : v.Average(); }
                return value;
        }
    }

    private async Task<StepFlow> CompareAsync(TestStep step, StepResult sr, CancellationToken ct)
    {
        var c = step.Compare!;
        var settings = await _api.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings == null) return Fail(step, sr, StepStatus.Failed, "读取设置失败");
        var actual = SettingsValue(settings, c.Key);
        sr.CompareActual = actual;
        double? eq = c.Expect, min = c.Min, max = c.Max;
        if (step.Reference == "iteration" && _curIterValue.HasValue)
        {
            switch (c.IterationAs) { case "min": min = _curIterValue; break; case "max": max = _curIterValue; break; default: eq = _curIterValue; break; }
        }
        var exp = new List<string>();
        if (min.HasValue) exp.Add("≥ " + Metrics.Fmt(min.Value));
        if (max.HasValue) exp.Add("≤ " + Metrics.Fmt(max.Value));
        if (eq.HasValue) exp.Add("= " + Metrics.Fmt(eq.Value));
        sr.CompareExpected = string.Join(" 且 ", exp);
        bool pass = !double.IsNaN(actual) && (!min.HasValue || actual >= min - c.Tolerance) && (!max.HasValue || actual <= max + c.Tolerance) && (!eq.HasValue || Math.Abs(actual - eq.Value) <= c.Tolerance);
        sr.ComparePassed = pass;
        sr.Note = $"{c.Key} = {Metrics.Fmt(actual)}，期望 {sr.CompareExpected}";
        Log("compare", $"{step.Id} {sr.Note} → {(pass ? "满足" : "不满足")}", _curItemId, step.Id);
        if (!pass && c.Hard) return Fail(step, sr, StepStatus.Failed, "硬性判据不满足：" + sr.Note);
        sr.Status = StepStatus.Done;
        return StepFlow.Continue;
    }

    public static double SettingsValue(SettingsDto s, string key) => key switch
    {
        SettingsKeys.TempSpMaxC => s.Limits.TempSpMaxC, SettingsKeys.RpmMax => s.Limits.RpmMax, SettingsKeys.PressureSpMaxPsi => s.Limits.PressureSpMaxPsi,
        SettingsKeys.OverPressurePsi => s.Alarms.OverPressurePsi, SettingsKeys.OverTempC => s.Alarms.OverTempC,
        SettingsKeys.LeakTestPsi => s.LeakTest.TestPsi, SettingsKeys.LeakSettleMin => s.LeakTest.SettleMin, SettingsKeys.LeakMeasureMin => s.LeakTest.MeasureMin,
        SettingsKeys.ApproachDeltaC => s.Approach.DeltaC, SettingsKeys.ApproachRate => s.Approach.RateCPerMin, SettingsKeys.ApproachFastRate => s.Approach.FastRateCPerMin,
        SettingsKeys.StirRampMs => s.Stir.RampMsPer1000Rpm, SettingsKeys.StirPeakCurrentA => s.Stir.PeakCurrentA, SettingsKeys.PressFullScaleMPa => s.Pressure.FullScaleMPa,
        _ => double.NaN,
    };

    private async Task<StepFlow> MarkerStepAsync(TestStep step, StepResult sr, CancellationToken ct)
    {
        await MarkerAsync(Resolve(step.Template ?? step.Title, null), ct).ConfigureAwait(false);
        sr.Status = StepStatus.Done; return StepFlow.Continue;
    }
    private async Task<StepFlow> SessionBeginStepAsync(StepResult sr, CancellationToken ct)
    {
        if (_result!.SessionId == null && !_req!.NoSession) await BeginSessionAsync(ct).ConfigureAwait(false);
        sr.Note = _result.SessionId ?? "无会话"; sr.Status = StepStatus.Done; return StepFlow.Continue;
    }
    private async Task<StepFlow> SessionEndStepAsync(StepResult sr, CancellationToken ct)
    {
        if (_result!.SessionId != null) { Log("session", "结束会话 → " + await _api.EndSessionAsync(ct).ConfigureAwait(false)); _result.SessionId = null; }
        sr.Status = StepStatus.Done; return StepFlow.Continue;
    }

    // ------------------------------------------------------------------ 节拍 / 暂停 / 安全

    private double _pausedTotalSec, _pausedAtStepStart;

    /// <summary>一个节拍：等 Tick，处理暂停 / 安全事件，推进度。</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        await _clock.Delay(_opt.Tick, ct).ConfigureAwait(false);
        await CheckpointAsync(ct).ConfigureAwait(false);
    }

    /// <summary>检查点：安全事件 → 暂停并询问；暂停 → 等待继续（暂停时长不计入步骤）。</summary>
    private async Task CheckpointAsync(CancellationToken ct)
    {
        while (_safety.TryDequeue(out var ev) && !_abortRequested)
        {
            if (!_paused) Pause();
            _result!.Status = RunStatus.WaitingSafety;
            // 压力模块 / 其他模块离线：方案 §0.1-11「立即中止，不等待」→ 先执行停止类收尾（停加热 / 搅拌 / 程序 / 自整定 / 泄漏测试），再询问是否结束运行
            if (ev.Kind == SafetyKind.CommLost) { Log("safety", "通讯丢失：先执行停止类收尾", _curItemId, _curStepId); try { await SafeStopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { Log("safety", "收尾失败：" + ex.Message, _curItemId, _curStepId); } }
            Report("安全暂停：" + ev.Title, waiting: WaitingKind.Safety, prompt: ev.Title + " · " + ev.Detail);
            var pausedAt = _clock.UtcNow;
            SafetyDecision dec;
            try { dec = await _prompt.SafetyAsync(new SafetyPrompt(_curItemId ?? "", _curStepId, ev, Recommend(ev)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_abortRequested) { return; }
            _pausedTotalSec += (_clock.UtcNow - pausedAt).TotalSeconds;
            Log("safety", $"{ev.Title} → 操作员{(dec == SafetyDecision.Continue ? "继续" : "中止")}", _curItemId, _curStepId);
            if (dec == SafetyDecision.AbortRun) { Abort("安全事件：" + ev.Title); return; }
            Resume();
        }
        if (_paused)
        {
            var pausedAt = _clock.UtcNow;
            Task wait; lock (_lock) wait = _resumeTcs.Task;
            _result!.Status = RunStatus.Paused;
            Report("已暂停", waiting: WaitingKind.Paused);
            await wait.ConfigureAwait(false);
            _pausedTotalSec += (_clock.UtcNow - pausedAt).TotalSeconds;
            if (!_abortRequested) _result.Status = RunStatus.Running;
        }
        ct.ThrowIfCancellationRequested();
    }

    private static string Recommend(SafetyEvent ev) => ev.Kind switch
    {
        SafetyKind.Estop => "急停已激活：按 §0.1-12 顺序处置后，建议中止本次运行并复位（§0.1-10）。",
        SafetyKind.ControlLost => "控制权丢失：引擎已暂停。重新获得控制权后可继续；命令步会重试。",
        SafetyKind.CommLost => "通讯模块离线：方案 §0.1-11 要求立即中止；引擎已先执行停止类收尾（停加热 / 搅拌 / 程序），请确认现场后中止本次运行。",
        SafetyKind.OverTempAlert => "PV 超过 SP+15 ℃：方案 §0.1-11 中止条件，建议中止并停止加热。",
        SafetyKind.Alarm => "出现报警：确认现场状态后决定继续或中止。",
        _ => "请现场确认。",
    };

    /// <summary>在等待操作员时每节拍推一次进度（倒计时等待时长），并允许 Abort 中断。</summary>
    private async Task<T> WaitPromptAsync<T>(Func<Task<T>> start, string phase, WaitingKind kind, string prompt, CancellationToken ct)
    {
        _result!.Status = kind == WaitingKind.Entry ? RunStatus.WaitingEntry : RunStatus.WaitingConfirm;
        Report(phase, waiting: kind, prompt: prompt);
        var started = _clock.UtcNow;
        var task = start();
        while (true)
        {
            var delay = _clock.Delay(_opt.Tick, ct);
            var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (done == task)
            {
                _pausedTotalSec += 0; // 等待操作员的时间计入步骤耗时（方案要求记录实际等待）
                _result.Status = RunStatus.Running;
                return await task.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            // 等待录入 / 确认期间到达的安全事件不能等操作员先提交录入：立即走检查点（暂停 + 红卡询问；中止则取消本步等待）
            if (!_safety.IsEmpty) { await CheckpointAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
            Report(phase, waiting: kind, prompt: prompt, countdown: _clock.UtcNow - started);
        }
    }

    private void OnFrame(TelemetrySnapshot f)
    {
        var rows = _recorder.Add(f);
        if (rows.Count > 0) _store?.AppendTelemetry(rows, _curItemId, _curStepId);
    }

    private void OnSafety(SafetyEvent ev)
    {
        if (_result == null || _result.Status is RunStatus.Completed or RunStatus.Aborted or RunStatus.Failed) return;
        // 引擎自己下发的急停（F4）：随之而来的急停上升沿 / 控制权 invalidated / 本机租约失效都是预期内的，只记日志
        if (ev.Kind is SafetyKind.Estop or SafetyKind.ControlLost && _selfEstopAt is { } se && _clock.UtcNow - se < SelfEstopGrace)
        {
            Log("safety", $"预期内（引擎急停后）：{ev.Kind}: {ev.Title} · {ev.Detail}", _curItemId, _curStepId);
            return;
        }
        _safety.Enqueue(ev);
        Log("safety", $"{ev.Kind}: {ev.Title} · {ev.Detail}", _curItemId, _curStepId);
        try { SafetyRaised?.Invoke(ev); } catch { }
        if (!_paused) Pause();
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>服务端时间优先（最近 30 s 内有帧）；否则本机时钟。</summary>
    private DateTime Now()
    {
        var l = _feed.Latest;
        var now = _clock.UtcNow;
        if (l != null && (now - l.Ts).Duration() < TimeSpan.FromSeconds(30)) return l.Ts;
        return now;
    }

    private void Log(string kind, string text, string? itemId = null, string? stepId = null)
    {
        var e = new RunEventRecord { At = _clock.UtcNow, Kind = kind, ItemId = itemId ?? _curItemId, StepId = stepId ?? _curStepId, Text = text };
        _result?.Events.Add(e);
        _store?.AppendEvent(e);
    }

    private static string PhaseFor(TestStep s) => s.Kind switch
    {
        StepKind.Instruction => "等待现场操作", StepKind.Checklist => "等待检查表", StepKind.ManualEntry => "等待人工录入", StepKind.Command => "下发命令",
        StepKind.WaitStable => "等待稳定", StepKind.Hold => "定时记录", StepKind.Measure => "计算指标", StepKind.Compare => "核对设置",
        StepKind.Marker => "打点", StepKind.SessionBegin => "开始会话", StepKind.SessionEnd => "结束会话", _ => s.Kind.ToString(),
    };

    private string Resolve(string? text, int? rv)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        if (s.Contains("{rv}")) s = s.Replace("{rv}", rv.HasValue ? rv.Value.ToString() : string.Join(",", _curItem?.Channels ?? new List<int>()));
        if (s.Contains("{value}")) s = s.Replace("{value}", _curIterValue.HasValue ? _curIterValue.Value.ToString("0.###", CultureInfo.InvariantCulture) : "");
        if (s.Contains("{item}")) s = s.Replace("{item}", _curItemId ?? "");
        if (s.Contains("{iteration}")) s = s.Replace("{iteration}", _curIter.ToString());
        if (s.Contains("{unit}")) s = s.Replace("{unit}", _curDef?.Repeat?.Unit ?? "");
        // {manual:key}：本项本轮（找不到则本项任一轮）该录入字段的均值（如 F6 用刚录入的 n_max）
        int k;
        while ((k = s.IndexOf("{manual:", StringComparison.Ordinal)) >= 0)
        {
            int e = s.IndexOf('}', k);
            if (e < 0) break;
            var key = s.Substring(k + 8, e - k - 8);
            var vals = ManualValues(key, rv ?? 0);
            if (vals.Count == 0 && _curItemId != null)
                foreach (var kv in _manual) if (kv.Key.Item == _curItemId && kv.Key.Key == key && kv.Value.Count > 0) { vals = kv.Value.Select(x => x.Value).ToList(); break; }
            s = s.Substring(0, k) + (vals.Count == 0 ? "" : vals.Average().ToString("0.###", CultureInfo.InvariantCulture)) + s.Substring(e + 1);
        }
        return s;
    }

    private double? ResolveNumber(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = Resolve(e.GetString(), null);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return null;
    }

    private static string ArgsText(Dictionary<string, JsonElement> args) => args.Count == 0 ? "" : "{" + string.Join(",", args.Select(kv => kv.Key + ":" + kv.Value.GetRawText())) + "}";

    private void Report(string phase, double stepPercent = double.NaN, WaitingKind waiting = WaitingKind.None, string? prompt = null, TimeSpan? countdown = null,
        int? stepIndex = null, int? stepCount = null, int? itemIndex = null, int? itemCount = null, bool force = false)
    {
        var r = _result;
        if (r == null) return;
        var item = _curItem; var def = _curDef;
        int sIdx = stepIndex ?? (_last?.StepIndex ?? 0), sCnt = stepCount ?? (def?.Steps.Count ?? _last?.StepCount ?? 0);
        int iIdx = itemIndex ?? (_last?.ItemIndex ?? 0), iCnt = itemCount ?? (_last?.ItemCount ?? r.SelectedItems.Count);
        double sp = double.IsNaN(stepPercent) ? (_last?.StepPercent ?? 0) : Math.Clamp(stepPercent, 0, 100);
        double overall = _totalUnits == 0 ? 0 : Math.Clamp((_doneUnits + sp / 100.0) / _totalUnits * 100, 0, 100);
        if (r.Status is RunStatus.Completed or RunStatus.Aborted or RunStatus.Failed) overall = 100;
        overall = Math.Max(overall, _maxOverall); _maxOverall = overall;
        var chans = item?.Channels ?? r.Channels;
        var latest = _feed.Latest;
        var temps = new List<double?>(); var press = new List<double?>();
        foreach (var c in chans) { var cs = latest?.Channels.FirstOrDefault(x => x.Id == c); temps.Add(cs?.TempC); press.Add(cs?.PressurePsi); }
        TimeSpan? eta = null;
        if (def != null && item != null && _req != null)
        {
            double remainingThis = def.EstimatedMinutes * (1 - Math.Clamp((item.Steps.Count - 1 + sp / 100.0) / Math.Max(1, Units(def)), 0, 1));
            double others = 0;
            bool after = false;
            foreach (var id in _req.ItemIds) { if (after) others += _plan.FindItem(id)?.EstimatedMinutes ?? 0; if (id == def.Id) after = true; }
            eta = TimeSpan.FromMinutes(Math.Max(0, remainingThis + others));
        }
        var p = new PerfProgress(r.RunId, r.Status, iIdx, iCnt, item?.ItemId, item?.Title, sIdx, sCnt, _curStep?.StepId, _curStep?.Title, _curStep?.Kind,
            _curIter, _curIterCount, phase, sp, overall, _clock.UtcNow - r.StartedAt, eta, waiting, prompt, countdown, chans, temps, press,
            latest?.Stir.On == true ? latest.Stir.RpmSp : 0, latest?.Stir.On ?? false, _clock.UtcNow);
        _last = p;
        try { Progress?.Invoke(p); } catch { }
    }
}
