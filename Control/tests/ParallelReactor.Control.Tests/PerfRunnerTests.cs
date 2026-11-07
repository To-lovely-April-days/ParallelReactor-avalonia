using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>引擎端到端（假设备 + 假时钟 + 一阶温度模型）：产出文件、进度单调、暂停 / 中止、安全钩子、超时、录入校验、前置跳过。</summary>
public class PerfRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-perf-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (PerfRunner Runner, FakeReactor Dev, FakeClock Clock, AutoPrompt Prompt, List<PerfProgress> Progress) Make(TestPlan plan, PerfRunnerOptions? opt = null, double tau = 20)
    {
        var clock = new FakeClock();
        var dev = new FakeReactor(clock, tauSec: tau);
        var prompt = new AutoPrompt();
        var runner = new PerfRunner(plan, dev, dev, prompt, clock, opt ?? new PerfRunnerOptions { OutputRoot = _root });
        var progress = new List<PerfProgress>();
        runner.Progress += p => { lock (progress) progress.Add(p); };
        return (runner, dev, clock, prompt, progress);
    }

    private static RunRequest Req(params string[] items) => new() { PlanId = "smoke", ItemIds = items.ToList(), Channels = new List<int> { 1 }, Operator = "测试员", RunId = "PT-TEST-" + Guid.NewGuid().ToString("N")[..6] };

    [Fact]
    public async Task Smoke_T0Q_EndToEnd_ProducesFilesAndMeasures()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, progress) = Make(plan);
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q", "S1Q"), CancellationToken.None), clock, dev);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, result.Items.Count);
        var t0 = result.Items[0];
        Assert.Equal(ItemOutcome.Completed, t0.Outcome);
        Assert.All(t0.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Contains("enable 1", dev.Commands); Assert.Contains("heatStart 1 60", dev.Commands); Assert.Contains("heatStop 1", dev.Commands);
        Assert.Contains(dev.Commands, c => c.StartsWith("sessionBegin")); Assert.Contains("sessionEnd", dev.Commands);
        Assert.Contains(dev.Commands, c => c.StartsWith("marker T0Q 开始"));
        Assert.False(dev.Heating(1));
        // 指标：保温窗均值在 25..60 之间、样本数 ≈ 30、转速表录入 600
        var mean = t0.Measures.Single(m => m.Key == "tMean");
        Assert.InRange(mean.Value, 25, 60);
        Assert.InRange(t0.Measures.Single(m => m.Key == "frames").Value, 28, 33);
        Assert.Equal(600, t0.Measures.Single(m => m.Key == "tacho").Value);
        Assert.True(t0.Measures.Single(m => m.Key == "rate").Value > 0, "升温速率应为正");
        var s1 = result.Items[1];
        Assert.Equal(ItemOutcome.Completed, s1.Outcome);
        Assert.Contains("stir on=True rpm=300", dev.Commands); Assert.Contains("stirStop", dev.Commands);
        Assert.Equal(100.0, s1.Measures.Single(m => m.Key == "eN").Value, 6); // 600 vs 300 → +100%
        Assert.Equal(300, s1.Measures.Single(m => m.Key == "eN").IterationValue);

        // 文件
        var dir = Path.Combine(_root, result.RunId);
        Assert.Equal(dir, result.OutputDir);
        foreach (var f in new[] { "result.json", "report.html", "summary.csv", "events.csv", "manual-entries.csv", "settings-snapshot.json", Path.Combine("raw", "telemetry.csv"), Path.Combine("hmi", result.SessionId + ".csv") })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        var telemetry = File.ReadAllLines(Path.Combine(dir, "raw", "telemetry.csv"));
        Assert.Equal(ResultStore.TelemetryHeader, telemetry[0]);
        Assert.True(telemetry.Length > 8 * 40, "应有 ≥ 40 帧 × 8 通道");
        Assert.Contains(telemetry, l => l.Contains(",T0Q,T0Q-3"));
        var back = ResultJson.Deserialize(File.ReadAllText(Path.Combine(dir, "result.json")))!;
        Assert.Equal(result.RunId, back.RunId); Assert.Equal(2, back.Items.Count); Assert.Equal(6, back.Items[0].Steps.Count);
        var manual = File.ReadAllLines(Path.Combine(dir, "manual-entries.csv"));
        Assert.Equal(ResultStore.ManualHeader, manual[0]);
        Assert.Equal(1 + 1 + 3, manual.Length);
        Assert.True(result.Simulated);

        // 进度：单调不减、最后 100、含等待录入阶段
        var overall = progress.Select(p => p.OverallPercent).ToList();
        for (int i = 1; i < overall.Count; i++) Assert.True(overall[i] >= overall[i - 1] - 1e-9, $"进度回退 {overall[i - 1]} → {overall[i]} @{i}");
        Assert.Equal(100, overall[^1]);
        Assert.Contains(progress, p => p.Waiting == WaitingKind.Entry && p.Phase == "等待人工录入");
        Assert.Contains(progress, p => p.Phase == "定时记录" && p.Countdown.HasValue);
        Assert.Contains(progress, p => p.LiveTempC.Count == 1 && p.LiveTempC[0] > 25);
        Assert.All(progress.Where(p => p.Status == RunStatus.Running && p.ItemId != null), p => Assert.NotNull(p.Eta));
    }

    [Fact]
    public async Task Abort_StopsHeatAndStir_WritesAbortedReport()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, _) = Make(plan);
        var run = runner.RunAsync(Req("T0Q"), CancellationToken.None);
        // 跑到保温中途再中止
        for (int i = 0; i < 12; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.True(dev.Heating(1));
        Assert.Equal("定时记录", runner.LastProgress!.Phase);
        runner.Abort("测试中止");
        var result = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Equal(RunStatus.Aborted, result.Status);
        Assert.Equal("测试中止", result.AbortReason);
        Assert.Equal(ItemOutcome.Aborted, result.Items[0].Outcome);
        Assert.Contains("heatStop 1", dev.Commands);
        Assert.Contains("sessionEnd", dev.Commands);
        Assert.False(dev.Heating(1));
        Assert.DoesNotContain("estop", dev.Commands);
        var html = File.ReadAllText(Path.Combine(_root, result.RunId, "report.html"));
        Assert.Contains("本次运行已中止", html);
        Assert.Contains(result.Items[0].Steps, s => s.Status == StepStatus.Aborted && s.StepId == "T0Q-3");
    }

    [Fact]
    public async Task PauseResume_HoldDoesNotCountPausedTime()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, _, progress) = Make(plan);
        var run = runner.RunAsync(Req("T0Q"), CancellationToken.None);
        for (int i = 0; i < 8; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Equal("定时记录", runner.LastProgress!.Phase);
        runner.Pause();
        for (int i = 0; i < 20; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.True(runner.IsPaused);
        Assert.Equal("已暂停", runner.LastProgress!.Phase);
        Assert.Equal(StepStatus.Running, runner.Result!.Items[0].Steps[2].Status);
        runner.Resume();
        var result = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Equal(RunStatus.Completed, result.Status);
        var hold = result.Items[0].Steps.Single(s => s.StepId == "T0Q-3");
        Assert.InRange(hold.PausedSec, 19, 22);
        Assert.InRange(hold.DurationSec, 29, 32);
        Assert.Contains(result.Events, e => e.Kind == "pause");
    }

    [Fact]
    public async Task SafetyEvent_PausesAndAsks_AbortWhenOperatorSaysSo()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, _) = Make(plan);
        prompt.SafetyAnswer = SafetyDecision.AbortRun;
        var raised = new List<SafetyEvent>();
        runner.SafetyRaised += raised.Add;
        var run = runner.RunAsync(Req("T0Q"), CancellationToken.None);
        for (int i = 0; i < 6; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        dev.RaiseSafety(new SafetyEvent(SafetyKind.Alarm, "RV1 超温", "PV 99 ℃", clock.UtcNow, 1));
        var result = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Single(raised);
        Assert.Single(prompt.Safeties);
        Assert.Equal(RunStatus.Aborted, result.Status);
        Assert.Contains("安全事件", result.AbortReason);
        Assert.Contains("heatStop 1", dev.Commands);
        Assert.Contains(result.Events, e => e.Kind == "safety");
    }

    [Fact]
    public async Task SafetyEvent_ContinueResumes_NeverAutoContinues()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, progress) = Make(plan);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int asked = 0;
        var slowPrompt = new SlowSafetyPrompt(prompt, gate, () => asked++);
        var runner2 = new PerfRunner(plan, dev, dev, slowPrompt, clock, new PerfRunnerOptions { OutputRoot = _root });
        var run = runner2.RunAsync(Req("T0Q"), CancellationToken.None);
        for (int i = 0; i < 6; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        dev.RaiseSafety(new SafetyEvent(SafetyKind.ControlLost, "控制权丢失", "lost", clock.UtcNow));
        for (int i = 0; i < 30; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Equal(1, asked);
        Assert.Equal(RunStatus.WaitingSafety, runner2.Result!.Status);
        Assert.True(runner2.IsPaused);
        gate.SetResult();
        var result = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Equal(RunStatus.Completed, result.Status);
        var hold = result.Items[0].Steps.Single(s => s.StepId == "T0Q-3");
        Assert.True(hold.PausedSec >= 25, "安全暂停时长应计入 pausedSec：" + hold.PausedSec);
    }

    private sealed class SlowSafetyPrompt : IOperatorPrompt
    {
        private readonly IOperatorPrompt _inner; private readonly TaskCompletionSource _gate; private readonly Action _onAsk;
        public SlowSafetyPrompt(IOperatorPrompt inner, TaskCompletionSource gate, Action onAsk) { _inner = inner; _gate = gate; _onAsk = onAsk; }
        public Task<ConfirmResult> ConfirmAsync(ConfirmRequest r, CancellationToken ct) => _inner.ConfirmAsync(r, ct);
        public Task<EntryResult> EnterAsync(EntryRequest r, CancellationToken ct) => _inner.EnterAsync(r, ct);
        public async Task<SafetyDecision> SafetyAsync(SafetyPrompt p, CancellationToken ct) { _onAsk(); await _gate.Task.WaitAsync(ct); return SafetyDecision.Continue; }
    }

    [Fact]
    public async Task CommandRejected_AbortsItem_StopsHeat_NextItemStillRuns()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, _, _) = Make(plan);
        dev.Intercept = c => c.StartsWith("heatStart") ? new ApiOutcome(false, "op-x", "failed", 409, "INVALID_STATE", "通道未就绪") : null;
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q", "S1Q"), CancellationToken.None), clock, dev);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(ItemOutcome.Aborted, result.Items[0].Outcome);
        Assert.Contains("INVALID_STATE", result.Items[0].Note);
        var cmd = result.Items[0].Steps.Single(s => s.StepId == "T0Q-2");
        Assert.Equal(StepStatus.Failed, cmd.Status);
        Assert.Equal(409, cmd.Commands[0].HttpStatus);
        Assert.Equal(ItemOutcome.Completed, result.Items[1].Outcome);
        Assert.Contains(result.Events, e => e.Kind == "command-failed");
    }

    [Fact]
    public async Task ManualEntry_ValidationRetriesThenFails()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, _) = Make(plan, new PerfRunnerOptions { OutputRoot = _root, EntryRetries = 1 });
        int calls = 0;
        prompt.OnEntry = r => { calls++; return calls == 1 ? AutoPrompt.Fill(r, 9999) : new EntryResult(false, new Dictionary<string, IReadOnlyList<double>> { ["tacho"] = new[] { 1.0, 2.0 } }); };
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q"), CancellationToken.None), clock, dev);
        // 第一次 9999 被 Clamp 到 max=3000 → 合法；为了测校验，这里让第二项走无效路径：用 S1Q 的 3 读数字段
        Assert.Equal(ItemOutcome.Completed, result.Items[0].Outcome);
        Assert.Equal(1, calls);

        var (runner2, dev2, clock2, prompt2, _) = Make(plan, new PerfRunnerOptions { OutputRoot = _root, EntryRetries = 1 });
        var errors = new List<string?>();
        prompt2.OnEntry = r => { errors.Add(r.ValidationError); return new EntryResult(false, new Dictionary<string, IReadOnlyList<double>> { ["n"] = new[] { 1.0, 2.0 } }); };
        var result2 = await PerfDrive.RunAsync(runner2.RunAsync(Req("S1Q"), CancellationToken.None), clock2, dev2);
        Assert.Equal(2, errors.Count);
        Assert.Null(errors[0]);
        Assert.Contains("需要 3 个读数", errors[1]);
        Assert.Equal(ItemOutcome.Aborted, result2.Items[0].Outcome);
        Assert.Contains("stirStop", dev2.Commands); // 项中止 → 引擎开的搅拌被停止
    }

    [Fact]
    public async Task InstructionDeclined_WithContinue_IsSkipped_And_WaitStableTimesOut()
    {
        var plan = PlanLoader.Parse(ReadSmoke().Replace("\"id\": \"T0Q-3\", \"kind\": \"hold\", \"title\": \"保温记录 30 s\", \"durationSec\": 30,",
            "\"id\": \"T0Q-3\", \"kind\": \"waitStable\", \"title\": \"等待 60 ℃ 稳定\", \"signal\": \"tempC\", \"band\": 0.5, \"reference\": \"sp\", \"windowSec\": 10, \"timeoutSec\": 20, \"onFail\": \"continue\",")
            .Replace("\"id\": \"T0Q-1\", \"kind\": \"command\", \"title\": \"启用 RV{rv}\",", "\"id\": \"T0Q-1\", \"kind\": \"instruction\", \"title\": \"确认现场\", \"text\": \"请确认\", \"onFail\": \"continue\","));
        var (runner, dev, clock, prompt, _) = Make(plan, tau: 600); // τ 很大 → 20 s 内到不了 60 ℃ → 超时
        prompt.OnConfirm = _ => ConfirmResult.No;
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q"), CancellationToken.None), clock, dev);
        Assert.Equal(ItemOutcome.Completed, result.Items[0].Outcome);
        Assert.Equal(StepStatus.Skipped, result.Items[0].Steps[0].Status);
        Assert.Equal(StepStatus.TimedOut, result.Items[0].Steps[2].Status);
        Assert.Contains("超时", result.Items[0].Steps[2].Note);
        Assert.Contains("部分步骤失败", result.Items[0].Note);
        Assert.Contains(result.Events, e => e.Kind == "timeout");
    }

    [Fact]
    public async Task WaitStable_SucceedsWhenInBand()
    {
        var plan = PlanLoader.Parse(ReadSmoke().Replace("\"id\": \"T0Q-3\", \"kind\": \"hold\", \"title\": \"保温记录 30 s\", \"durationSec\": 30,",
            "\"id\": \"T0Q-3\", \"kind\": \"waitStable\", \"title\": \"等待 60 ℃ 稳定\", \"signal\": \"tempC\", \"band\": 0.5, \"maxSlopePerMin\": 1.0, \"reference\": \"sp\", \"windowSec\": 10, \"timeoutSec\": 600,"));
        var (runner, dev, clock, _, progress) = Make(plan, tau: 5);
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q"), CancellationToken.None), clock, dev);
        var ws = result.Items[0].Steps[2];
        Assert.Equal(StepStatus.Done, ws.Status);
        Assert.Contains("实际等待", ws.Note);
        Assert.InRange(ws.DurationSec, 15, 60);
        Assert.Contains(progress, p => p.Phase == "稳定计时");
    }

    [Fact]
    public async Task Precondition_R0Failed_SkipsDependentItem()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var (runner, dev, clock, prompt, _) = Make(plan);
        prompt.OnConfirm = r => r.Checklist != null ? new ConfirmResult(true, r.Checklist.Select((_, i) => i != 6).ToList(), "甲", "乙") : ConfirmResult.Yes;
        var req = Req("R0", "F5", "F4"); req.PlanId = plan.PlanId; req.Channels = new List<int> { 1, 2 };
        var result = await PerfDrive.RunAsync(runner.RunAsync(req, CancellationToken.None), clock, dev, maxSteps: 3000);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(ItemOutcome.Aborted, result.Items[0].Outcome);
        Assert.Contains("检查表未全部完成（12/13）", result.Items[0].Note);
        Assert.Equal(ItemOutcome.Completed, result.Items[1].Outcome); // F5 无 R0 前置
        Assert.Equal(ItemOutcome.Skipped, result.Items[2].Outcome);   // F4 依赖 R0
        Assert.Contains("前置 R0 未通过", result.Items[2].Note);
        Assert.DoesNotContain("estop", dev.Commands);
        Assert.Equal(2, result.Items[1].Steps.Count(s => s.Kind == "ManualEntry")); // F5 重复 2 次 × 1 录入步
        Assert.Equal(14, result.Items[1].Steps.Count);                                  // 7 步 × 2 轮
    }

    [Fact]
    public async Task RequireControl_FailsFastWithoutLease()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, _, _) = Make(plan);
        dev.CanControl = false;
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("T0Q"), CancellationToken.None), clock, dev);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("控制权", result.AbortReason);
        Assert.DoesNotContain(dev.Commands, c => c.StartsWith("heat"));
        Assert.True(File.Exists(Path.Combine(_root, result.RunId, "report.html")));
    }

    [Fact]
    public async Task SessionActive_AsksOperator_EndsAndRecreates()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var (runner, dev, clock, prompt, _) = Make(plan);
        dev.SessionAlreadyActive = true;
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("S1Q"), CancellationToken.None), clock, dev);
        Assert.Contains(prompt.Confirms, c => c.Title == "已有活动会话");
        Assert.NotNull(result.SessionId);
        Assert.Equal(2, dev.Commands.Count(c => c.StartsWith("sessionBegin")));
        Assert.Equal(2, dev.Commands.Count(c => c == "sessionEnd"));
    }

    [Fact]
    public async Task Repeat_RunsEachValue_WithIterationMarkersAndValues()
    {
        var plan = PlanLoader.Parse(ReadSmoke().Replace("\"repeat\": { \"count\": 1, \"variable\": \"rpm\", \"values\": [300], \"unit\": \"rpm\" }", "\"repeat\": { \"variable\": \"rpm\", \"values\": [100, 500], \"unit\": \"rpm\" }"));
        var (runner, dev, clock, prompt, _) = Make(plan);
        prompt.OnEntry = r => AutoPrompt.Fill(r, 505);
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req("S1Q"), CancellationToken.None), clock, dev);
        var it = result.Items[0];
        Assert.Equal(10, it.Steps.Count);
        Assert.Contains("stir on=True rpm=100", dev.Commands); Assert.Contains("stir on=True rpm=500", dev.Commands);
        var e = it.Measures.Where(m => m.Key == "eN").OrderBy(m => m.Iteration).ToList();
        Assert.Equal(2, e.Count);
        Assert.Equal(405, e[0].Value, 6); Assert.Equal(1, e[1].Value, 6);
        Assert.Equal(100, e[0].IterationValue); Assert.Equal(500, e[1].IterationValue);
        Assert.Contains(dev.Commands, c => c.Contains("第 2/2 轮") && c.Contains("rpm=500"));
    }

    private static string ReadSmoke()
    {
        using var s = typeof(PlanLoader).Assembly.GetManifestResourceStream("plans/smoke.json")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
