using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F3：设备上的程序（不是本机下发的）—— GET /channels/{id}/program/run 的冻结步骤 → ProgramStep（ProgramDocs.FromRun：类型、参数、名称），
/// 按需取数（Core 的 HmiRunPrograms：每次执行 / 换步一次、同一路不并发、失败退避、取回来的不是这一次执行不认），
/// 记录（ProgramSource.Hmi「设备程序」：HMI 的状态 / 当前步 + 台账同一套步骤计时与估算；触屏启动标出），R5（换了执行 / HMI 重启立即不用旧步骤）。
/// </summary>
public class HmiRunProgramsTests
{
    private static readonly DateTime D0 = new(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;

    private static ProgramStepDto S(string type, string json, string name = "") =>
        new(type, JsonDocument.Parse(json).RootElement.Clone(), name, "");

    /// <summary>4 步：升温 120 ℃ 平衡 10 min → 充压 A 50 psi → 反应 60 min → 结束降温 40 ℃。</summary>
    private static ProgramStepDto[] Steps4() => new[]
    {
        S("heat", "{\"mode\":\"fixed\",\"temp\":120,\"wait\":10}", "升温"),
        S("press", "{\"gas\":\"A\",\"pres\":50,\"regulate\":\"on\"}", "升压"),
        S("react", "{\"endBy\":\"time\",\"time\":60,\"mmol\":0}", "恒温反应"),
        S("end", "{\"mode\":\"vent\",\"cool\":40}", ""),
    };

    private static ProgramRunDto Dto(int n = 1, DateTime? startedAt = null, int step = 0, string by = "local", ProgramStepDto[]? steps = null) =>
        new(1, D0, new ProgramRunSnap(n, true, "running", step, (steps ?? Steps4()).Length, "", "", by, startedAt ?? D0, false, steps ?? Steps4()));

    private static ChannelProgramSnap P(int step, DateTime? startedAt = null, string state = "running", string? by = "local", int count = 4, string message = "") =>
        Prog(step, count, state, startedAt ?? D0, by, message);

    /// <summary>可控的取数：每次请求一个 TaskCompletionSource（测试决定什么时候、回什么）。</summary>
    private sealed class FakeFetch
    {
        public readonly List<(int N, TaskCompletionSource<ProgramRunDto?> Tcs)> Calls = new();
        public Task<ProgramRunDto?> Fetch(int n, CancellationToken ct)
        {
            var t = new TaskCompletionSource<ProgramRunDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Calls) Calls.Add((n, t));
            return t.Task;
        }
        public int Count { get { lock (Calls) return Calls.Count; } }
        public TaskCompletionSource<ProgramRunDto?> Last { get { lock (Calls) return Calls[^1].Tcs; } }
    }

    private static async Task Until(Func<bool> cond, string what)
    {
        for (int i = 0; i < 300 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), what);
    }

    // ———————————————————— 转换 ————————————————————

    [Fact]
    public void FromRun_ConvertsKindsParamsAndNames()
    {
        var steps = ProgramDocs.FromRun(Steps4(), Pr8)!;
        Assert.Equal(new[] { StepKind.Heat, StepKind.Press, StepKind.React, StepKind.End }, steps.Select(s => s.Type));
        Assert.Equal(("升温", "升压", "恒温反应"), (steps[0].Name, steps[1].Name, steps[2].Name));
        Assert.Null(steps[3].Name);                                  // 空名称：按类型显示（StepText.Name）
        Assert.Equal((120.0, 10.0), (steps[0].Sp, steps[0].EstMin));
        Assert.False(steps[0].HeatCurve);
        Assert.Equal(Core.Format.Units.PsiToMpa(50), steps[1].Target!.Value, 6);   // psi → MPa
        Assert.Equal(ProgramDocs.GasKey(Pr8, "A"), steps[1].Gas);
        Assert.True(steps[1].Regulate);
        Assert.Equal((60.0, 60.0, 120.0), (steps[2].Minutes, steps[2].EstMin, steps[2].Sp));   // 反应：保持上一步的温度 / 压力 / 气路
        Assert.Equal(steps[1].Target, steps[2].Target);
        Assert.Equal((40.0, true), (steps[3].CoolTo, steps[3].Vent));
        // 与本机程序（ProgramDoc）的转换完全一致，除了名称
        var doc = new ProgramDoc();
        foreach (var d in Steps4())
        {
            var sd = new ProgramStepDoc(d.Type);
            foreach (var p in d.Params.EnumerateObject()) sd.Params[p.Name] = p.Value.Clone();
            doc.Steps.Add(sd);
        }
        var same = ProgramDocs.ToSteps(doc, Pr8);
        Assert.Equal(same, steps.Select(s => s with { Name = null }).ToArray());
    }

    [Fact]
    public void FromRun_CurveHeat_UnknownType_Empty()
    {
        var curve = ProgramDocs.FromRun(new[] { S("heat", "{\"mode\":\"curve\",\"temp\":150,\"wait\":5,\"segments\":[{\"temp\":80,\"rate\":2,\"hold\":20}]}", "升温") }, Pr8)!;
        Assert.True(curve[0].HeatCurve);
        // 较新的 HMI 有不认识的步骤类型：步骤序号会对不上 → 不显示（降级）
        Assert.Null(ProgramDocs.FromRun(new[] { S("heat", "{}"), S("dose", "{\"ml\":3}") }, Pr8));
        Assert.Null(ProgramDocs.FromRun(null, Pr8));
        Assert.Empty(ProgramDocs.FromRun(Array.Empty<ProgramStepDto>(), Pr8)!);
        // 缺参数：同本机程序的默认值
        var d = ProgramDocs.FromRun(new[] { S("wait", "{}"), S("stir", "{}") }, Pr8)!;
        Assert.Equal((30.0, 5.0, 1000.0), (d[0].Minutes!.Value, d[1].Minutes!.Value, d[1].Rpm!.Value));
    }

    // ———————————————————— 取数的时机 ————————————————————

    [Fact]
    public async Task Fetch_OncePerRun_AndOncePerStepChange_Deduplicated()
    {
        var f = new FakeFetch();
        var runs = new HmiRunPrograms(f.Fetch, () => D0);
        int landed = 0;
        runs.Landed += _ => Interlocked.Increment(ref landed);

        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));           // 还没取到：发起一次
        Assert.Equal(1, f.Count);
        for (int i = 0; i < 5; i++) Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));   // 在途：不再发（每帧都问也只有一个请求）
        Assert.Equal(1, f.Count);
        int v0 = runs.Version;
        f.Last.SetResult(Dto());
        await Until(() => Volatile.Read(ref landed) == 1, "落地");
        Assert.NotEqual(v0, runs.Version);
        var st = runs.StepsFor(1, P(0), "b1", Pr8);
        Assert.Equal(4, st!.Count);
        for (int i = 0; i < 5; i++) Assert.Same(st, runs.StepsFor(1, P(0), "b1", Pr8));   // 同一步：不再取
        Assert.Equal(1, f.Count);

        // 换步：再取一次（取回来之前照旧给已有的步骤）
        Assert.Same(st, runs.StepsFor(1, P(1), "b1", Pr8));
        Assert.Equal(2, f.Count);
        Assert.Same(st, runs.StepsFor(1, P(1), "b1", Pr8));
        Assert.Equal(2, f.Count);
        int v1 = runs.Version;
        f.Last.SetResult(Dto(step: 1));
        await Until(() => runs.RunOf(1, P(1), "b1")?.StepIndex == 1, "换步的运行记录落地");
        Assert.Equal(v1, runs.Version);                            // 步骤没变：不重建、不刷新
        Assert.Equal(1, landed);
        Assert.Same(st, runs.StepsFor(1, P(1), "b1", Pr8));
        Assert.Equal(2, f.Count);

        // 别的反应釜各取各的
        Assert.Null(runs.StepsFor(2, P(0), "b1", Pr8));
        Assert.Equal(3, f.Count);
        Assert.Equal(2, f.Calls[2].N);
        // 不让取（断线）：不发请求
        Assert.Null(runs.StepsFor(3, P(0), "b1", Pr8, allowFetch: false));
        Assert.Equal(3, f.Count);
        // 没有程序 / 没有开始时刻 / 不知道启动号：什么都不做
        Assert.Null(runs.StepsFor(4, null, "b1", Pr8));
        Assert.Null(runs.StepsFor(4, Prog(-1, 4, "preparing", null), "b1", Pr8));
        Assert.Null(runs.StepsFor(4, P(0), null, Pr8));
        Assert.Equal(3, f.Count);
    }

    [Fact]
    public async Task Failure_BacksOff_ThenRetries_NeverSpams()
    {
        var f = new FakeFetch();
        var now = D0;
        var runs = new HmiRunPrograms(f.Fetch, () => now);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        f.Last.SetException(new System.Net.Http.HttpRequestException("连接被拒绝"));
        await Until(() => !runs.Pending(1), "落地");
        for (int i = 0; i < 10; i++) Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));   // 退避中：不发
        Assert.Equal(1, f.Count);
        now = D0.AddSeconds(2.1);                                  // 2 s 后
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        Assert.Equal(2, f.Count);
        f.Last.SetException(new ApiException(OpCodes.Starting, 503, "HMI 启动中"));   // 再失败：翻倍（4 s）
        await Until(() => !runs.Pending(1), "落地");
        now = D0.AddSeconds(2.1 + 3);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        Assert.Equal(2, f.Count);
        now = D0.AddSeconds(2.1 + 4.1);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        Assert.Equal(3, f.Count);
        f.Last.SetResult(Dto());
        await Until(() => runs.StepsFor(1, P(0), "b1", Pr8) != null, "第三次成功");
        Assert.Equal(3, f.Count);
        Assert.Equal(3, runs.Requests);
    }

    [Fact]
    public async Task OtherRun_InResponse_NotAccepted()
    {
        var f = new FakeFetch();
        var now = D0;
        var runs = new HmiRunPrograms(f.Fetch, () => now);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        f.Last.SetResult(Dto(startedAt: D0.AddMinutes(-30)));     // HMI 回的是上一次执行
        await Until(() => !runs.Pending(1), "落地");
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        Assert.Null(runs.RunOf(1, P(0), "b1"));
        now = D0.AddSeconds(3);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));            // 退避之后再取
        Assert.Equal(2, f.Count);
        f.Last.SetResult(Dto(n: 1));
        await Until(() => runs.StepsFor(1, P(0), "b1", Pr8) != null, "这一次执行");
        // 步数与帧里报的不同：不显示
        Assert.Null(runs.StepsFor(1, P(0, count: 5), "b1", Pr8));
    }

    [Fact]
    public async Task NewRun_Or_Restart_DropsOldSteps_Immediately()
    {
        var f = new FakeFetch();
        var runs = new HmiRunPrograms(f.Fetch, () => D0);
        runs.StepsFor(1, P(2), "b1", Pr8);
        f.Last.SetResult(Dto(step: 2));
        await Until(() => runs.StepsFor(1, P(2), "b1", Pr8) != null, "第一次执行");

        // 同一路开始了新的一次执行（同一个程序再跑一遍也是）：旧的步骤立即不用（R5），重新取
        var later = D0.AddHours(1);
        int v = runs.Version;
        Assert.Null(runs.StepsFor(1, P(0, startedAt: later), "b1", Pr8));
        Assert.NotEqual(v, runs.Version);
        Assert.Equal(2, f.Count);
        // 在途时 HMI 重启（启动号变了）：也立即不用；旧请求落地时丢弃
        Assert.Null(runs.StepsFor(1, P(0, startedAt: later), "b2", Pr8));
        Assert.Equal(3, f.Count);
        f.Calls[1].Tcs.SetResult(Dto(startedAt: later));          // 旧启动号那次的请求
        await Task.Delay(30);
        Assert.Null(runs.StepsFor(1, P(0, startedAt: later), "b2", Pr8));
        Assert.Equal(3, f.Count);                                   // 第三个请求还在途
        // Reset（HMI 重启）：在途的请求落地时丢弃
        runs.Reset();
        f.Calls[2].Tcs.SetResult(Dto(startedAt: later));
        await Task.Delay(30);
        Assert.Null(runs.RunOf(1, P(0, startedAt: later), "b2"));
        Assert.Null(runs.StepsFor(1, P(0, startedAt: later), "b2", Pr8));
        Assert.Equal(4, f.Count);
    }

    // ———————————————————— 记录 ————————————————————

    [Fact]
    public async Task RecordOf_StatusStepTimingEta_TouchStarted()
    {
        var f = new FakeFetch();
        var runs = new HmiRunPrograms(f.Fetch, () => D0);
        var tracker = new StepTracker();
        // 设备上 RV1 的程序（触屏启动）：第 0 步开始于 D0，第 1 步在 D0+20 min 前后开始（连续观察：两帧中点）
        var t1 = D0.AddMinutes(20);
        var f0 = Frame(t1, Ch(1, 120, 120, 0, program: P(0)));
        var f1 = Frame(t1.AddSeconds(1.2), Ch(1, 120, 120, 2, program: P(1, message: "")));
        tracker.Observe("b1", f0);
        tracker.Observe("b1", f1);
        Assert.Null(runs.RecordOf(1, f1, "b1", Pr8, tracker));
        f.Last.SetResult(Dto(step: 1));
        await Until(() => runs.Version > 0, "落地");
        var r = runs.RecordOf(1, f1, "b1", Pr8, tracker)!;
        Assert.Equal((ProgramSource.Hmi, HmiRunPrograms.DefaultName, ProgramStatus.Running, 1, 4), (r.Source, r.Name, r.Status, r.StepIndex, r.Steps.Count));
        Assert.True(r.StartedByTouch);
        Assert.Equal(D0.ToLocalTime(), r.StartedAt);
        Assert.NotNull(r.StepStartedAt);                            // 步骤计时：换步取两帧中点
        Assert.True(Math.Abs((r.StepStartedAt!.Value.ToUniversalTime() - t1.AddSeconds(0.6)).TotalSeconds) < 0.01);
        Assert.Equal(new StepLogEntry(0, D0.ToLocalTime(), t1.AddSeconds(0.6).ToLocalTime()).I, r.Log.Single().I);
        Assert.True(r.StepDurS > 0);
        Assert.True(r.Steps[1].EstMin > 0);                        // 充压：按差值 ÷ 充压速率估（同台账）
        Assert.True(r.Steps[3].EstMin > 0);                        // 结束：降温 + 收尾
        Assert.Equal("升压", r.Steps[1].Name);
        // 与台账同一套计算：RunRecord 直接给出同样的结果
        var same = ProgramLedger.RunRecord(new ProgramRecord { Name = HmiRunPrograms.DefaultName, Steps = runs.StepsFor(1, P(1), "b1", Pr8)!, Source = ProgramSource.Hmi, StartedByTouch = true },
            f1.Channels[0], tracker.TrackOf(1, P(1), "b1"), f1.Ts, null, null);
        Assert.Equal(same.StepStartedAt, r.StepStartedAt);
        Assert.Equal(same.Steps, r.Steps);

        // 远程客户端启动：不标触屏启动
        var byPc = Frame(t1.AddSeconds(2.4), Ch(1, 120, 120, 2, program: P(1, by: "远程（PC-别人）")));
        Assert.False(runs.RecordOf(1, byPc, "b1", Pr8, tracker)!.StartedByTouch);
        // 结束（completed）：已结束、当前步 = 最后一步；aborted / failed 同台账的映射
        var done = Frame(t1.AddSeconds(3.6), Ch(1, 40, 0, 0, program: P(3, state: "completed")));
        Assert.Equal((ProgramStatus.Done, 3), (runs.RecordOf(1, done, "b1", Pr8, tracker)!.Status, runs.RecordOf(1, done, "b1", Pr8, tracker)!.StepIndex));
        var failed = Frame(t1.AddSeconds(3.6), Ch(1, 40, 0, 0, program: P(2, state: "failed")));
        Assert.Equal(ProgramStatus.Failed, runs.RecordOf(1, failed, "b1", Pr8, tracker)!.Status);
        // 帧里这一路是另一次执行：不给（R5），不按旧步骤显示
        var other = Frame(t1.AddSeconds(5), Ch(1, 40, 0, 0, program: P(0, startedAt: t1)));
        Assert.Null(runs.RecordOf(1, other, "b1", Pr8, tracker));
        Assert.Null(runs.RecordOf(1, null, "b1", Pr8, tracker));
    }

    [Fact]
    public async Task NotFound_StopsAskingForThisStep_UntilStepChanges()
    {
        var f = new FakeFetch();
        var now = D0;
        var runs = new HmiRunPrograms(f.Fetch, () => now);
        Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));
        f.Last.SetResult(null);                                    // 404「本次开机以来没有执行过程序」
        await Until(() => !runs.Pending(1), "落地");
        now = D0.AddMinutes(10);
        for (int i = 0; i < 10; i++) Assert.Null(runs.StepsFor(1, P(0), "b1", Pr8));   // 同一步：不再问（旧 HMI 每帧都会 404）
        Assert.Equal(1, f.Count);
        Assert.Null(runs.StepsFor(1, P(1), "b1", Pr8));            // 换步：再试一次
        Assert.Equal(2, f.Count);
        f.Last.SetException(new ApiException("HTTP_404", 404, "没有这个端点"));   // 旧 HMI：同样只是这一步不再问
        await Until(() => !runs.Pending(1), "落地");
        for (int i = 0; i < 10; i++) Assert.Null(runs.StepsFor(1, P(1), "b1", Pr8));
        Assert.Equal(2, f.Count);
        Assert.Null(runs.StepsFor(1, P(0, startedAt: D0.AddHours(1)), "b1", Pr8));   // 新的执行：再试
        Assert.Equal(3, f.Count);
    }
}
