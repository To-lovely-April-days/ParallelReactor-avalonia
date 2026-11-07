using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 三路审查（保真 / 客户端 / 方案）后的修复回归：每条逻辑修复一个测试。
/// Client：F1 pong 超时 Abort、F3 clientName 规范化、F4 重启清遥测、F5 重连后自动重申请、F6 WS error 喂租约、F7 停止类独立信号量。
/// PerfTest：H1 程序枚举码校验、H2 自发急停豁免 + controlRequest、H3 R0 门禁 / 跳过阻断、M1 P3 窗口、M2 Δ、M4 中止收尾自整定 / 泄漏、M6 {manual:}、M7 bandPct、M8 allowCodes、L1 环境回填、L2 iterationAs、L4 等待录入时的安全事件 / 通讯丢失先收尾、模块离线 &gt; 30 s。
/// </summary>
public class FixRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-perf-fix-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ====================================================================== Client

    [Fact]
    public void F3_ClientName_IsSanitizedLikeHmi()
    {
        var o = new ClientOptions(new Uri("http://127.0.0.1:1"), "tok", "  PC  工位\t X​ ");
        Assert.Equal("PC 工位 X", o.ClientName);
        Assert.Equal(ClientNames.Sanitize("PC  工位  X"), o.ClientName);
        Assert.Equal(ClientNames.Max, ClientNames.Sanitize(new string('x', 200)).Length);
        Assert.Throws<ArgumentException>(() => new ClientOptions(new Uri("http://127.0.0.1:1"), "tok", "​​"));
    }

    [Fact]
    public async Task F1_PongTimeout_AbortsEvenWhenPeerNeverAnswersClose()
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var server = new FakeWsServer();
        server.OnConnect = t => { t.CloseWaitsForPeer = true; t.OnSent = (tr, m) => { if (m.GetProperty("type").GetString() == "subscribe") tr.Push(new SubscribedMsg("subscribed", new[] { "telemetry" }, 0, 10)); }; };
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC") { MinWriteGap = TimeSpan.Zero };
        var rest = new ReactorApiClient(opt, http, clock);
        var ws = new TelemetryWsClient(opt, rest, clock, server.Next);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => ws.RunAsync(cts.Token));
        await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
        server[0].Push(ApiJson.Serialize(new HelloMsg("hello", "1.0", DateTime.UtcNow, null, "b1", true, 1, 10, 50, new ModeSnap("local", null), 5000)));
        await FakeClock.WaitUntil(() => ws.State == ConnectionState.Connected);
        // 三个 ping 周期无 pong → pongTimeout；对端半死（CloseWaitsForPeer）只有 Abort 能结束接收循环
        for (int i = 0; i < 3; i++) { await FakeClock.WaitUntil(() => clock.Pending >= 1); clock.Advance(TimeSpan.FromSeconds(5.1)); }
        await FakeClock.WaitUntil(() => server[0].Aborted, what: "Abort()");
        await FakeClock.WaitUntil(() => ws.State == ConnectionState.Reconnecting, what: "进入重连");
        Assert.Equal(1, ws.MessageCounts["pongTimeout"]);
        cts.Cancel();
        await ws.StopAsync();
        rest.Dispose();
    }

    private static (ReactorConnection Conn, ScriptedHttpHandler Http, FakeWsServer Server, FakeClock Clock) Facade(bool autoReacquire)
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var server = new FakeWsServer();
        server.OnConnect = t => t.OnSent = (tr, m) => { if (m.GetProperty("type").GetString() == "subscribe") tr.Push(new SubscribedMsg("subscribed", new[] { "telemetry" }, 0, 10)); };
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC-测试工位1") { AutoReacquireLease = autoReacquire, MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
        http.On("GET", "/device", _ => ScriptedHttpHandler.Ok(new DeviceInfoDto("m", null, "1", "1.0", 8, Array.Empty<ChannelInstalledDto>(), new[] { 1, 2, 3, 4 },
            new HardwareDto(new(false, null, null, null), new(false, null, null, null), new(false, null, null, null), new(false, null, null, null)), new UnitsDto("C", "psi", "rpm"), true, false, DateTime.UtcNow, "b", null, 10, 1)));
        http.On("GET", "/snapshot", _ => ScriptedHttpHandler.Ok(ClientRestTests.Frame(100)));
        int leaseNo = 0;
        http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq" + (leaseNo + 1), "granted", null, "L" + (++leaseNo), clock.UtcNow.AddSeconds(10), 10, 60, "k")));
        http.On("POST", "/control/heartbeat", call => ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, clock.UtcNow.AddSeconds(10), clock.UtcNow.AddSeconds(70), 10)));
        return (new ReactorConnection(opt, http, clock, server.Next), http, server, clock);
    }

    private static string Hello(string boot) => ApiJson.Serialize(new HelloMsg("hello", "1.0", DateTime.UtcNow, null, boot, true, 1, 10, 50, new ModeSnap("local", null), 5000));

    [Fact]
    public async Task F4_ServerRestart_ClearsTelemetryStore_SoLowSeqFramesAreAccepted()
    {
        var (conn, http, server, clock) = Facade(false);
        http.On("GET", "/control", _ => ScriptedHttpHandler.Ok(new ControlStateDto("local", null, null, "local", null, null, null, true, 10, 60, 60, "keep")));
        int restarts = 0; conn.ServerRestarted += () => restarts++;
        await using (conn)
        {
            await conn.StartAsync(CancellationToken.None);
            Assert.Equal(100, conn.Telemetry.Latest!.Seq);
            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            server[0].Push(Hello("bootA"));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            server[0].Push(ApiJson.Serialize(ClientRestTests.Frame(5)));   // 旧 seq：被当重复帧丢弃（正确）
            await Task.Delay(30);
            Assert.Equal(100, conn.Telemetry.Latest!.Seq);
            server[0].ServerClose();
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Reconnecting);
            await FakeClock.WaitUntil(() => clock.Pending >= 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await FakeClock.WaitUntil(() => server.Count == 2 && server[1].Connected);
            server[1].Push(Hello("bootB"));   // HMI 重启：bootId 变
            await FakeClock.WaitUntil(() => restarts == 1, what: "ServerRestarted");
            await FakeClock.WaitUntil(() => conn.Telemetry.Count == 0, what: "缓冲清空");
            server[1].Push(ApiJson.Serialize(ClientRestTests.Frame(1)));
            await FakeClock.WaitUntil(() => conn.Telemetry.Latest?.Seq == 1, what: "重启后 seq=1 的帧被采纳");
        }
    }

    [Fact]
    public async Task F5_ReconnectShowsLocal_WhenPreviouslyHolding_AutoReacquires()
    {
        var (conn, http, server, clock) = Facade(true);
        int n = 0;
        // 第一次 GET /control（StartAsync）本地；之后（hello 后对齐）也是本地 —— 模拟重连期间租约在服务端过期
        http.On("GET", "/control", _ => { n++; return ScriptedHttpHandler.Ok(new ControlStateDto("local", null, null, "local", null, null, null, true, 10, 60, 60, "keep")); });
        var reasons = new List<string>();
        conn.LeaseTransitioned += t => { lock (reasons) reasons.Add($"{t.From}->{t.To}:{t.Reason}"); };
        await using (conn)
        {
            await conn.StartAsync(CancellationToken.None);
            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            server[0].Push(Hello("bootA"));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            Assert.True(await conn.Lease.AcquireAsync(CancellationToken.None));
            Assert.Equal("L1", conn.Lease.LeaseId);
            server[0].ServerClose();
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Reconnecting);
            await FakeClock.WaitUntil(() => clock.Pending >= 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await FakeClock.WaitUntil(() => server.Count == 2 && server[1].Connected);
            server[1].Push(Hello("bootA"));
            // hello → GET /control = local → Revoke(state:local) → 自动重申请 → L2
            await FakeClock.WaitUntil(() => conn.Lease.LeaseId == "L2", what: "自动重申请拿到 L2");
            lock (reasons) { Assert.Contains("Holding->None:state:local", reasons); Assert.Contains("Requesting->Holding:granted", reasons); }
        }
    }

    [Fact]
    public async Task F6_WsErrorWithLeaseCode_IsFedToLeaseMachine()
    {
        var (conn, http, server, clock) = Facade(false);
        http.On("GET", "/control", _ => ScriptedHttpHandler.Ok(new ControlStateDto("remote", "PC-测试工位1", "PC-测试工位1", "remote", null, null, null, true, 10, 60, 60, "keep")));
        await using (conn)
        {
            await conn.StartAsync(CancellationToken.None);
            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            server[0].Push(Hello("bootA"));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            Assert.True(await conn.Lease.AcquireAsync(CancellationToken.None));
            server[0].Push(ApiJson.Serialize(new WsErrorMsg("error", OpCodes.ControlLeaseExpired, "租约已过期")));
            await FakeClock.WaitUntil(() => conn.Lease.State == LeaseState.None, what: "WS error → None");
            // 非租约类 error 不影响
            Assert.True(await conn.Lease.AcquireAsync(CancellationToken.None));
            server[0].Push(ApiJson.Serialize(new WsErrorMsg("error", OpCodes.BadRequest, "坏消息")));
            await Task.Delay(30);
            Assert.Equal(LeaseState.Holding, conn.Lease.State);
        }
    }

    [Fact]
    public async Task F7_StopClass_NotQueuedBehindHangingControlledWrite()
    {
        var clock = new FakeClock();
        var h = new ScriptedHttpHandler();
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC") { MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
        var c = new ReactorApiClient(opt, h, clock);
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.On("POST", "/channels/1/heat/start", call => { hang.Task.Wait(); return ScriptedHttpHandler.Command(call, true, null); });
        h.On("POST", "/stop-all", call => ScriptedHttpHandler.Command(call, false, null));
        h.On("POST", "/channels/1/heat/stop", call => ScriptedHttpHandler.Command(call, false, null));
        var heat = Task.Run(() => c.StartHeatAsync("L", 1, 60, CancellationToken.None));
        await FakeClock.WaitUntil(() => h.Count("POST", "/channels/1/heat/start") == 1);
        // 受控写卡住期间停止类必须立刻通过
        var stop = await c.StopAllAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Ok);
        var stopHeat = await c.StopHeatAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopHeat.Ok);
        Assert.False(heat.IsCompleted);
        hang.SetResult(true);
        Assert.True((await heat).IsAccepted);
        c.Dispose();
    }

    // ====================================================================== PerfTest · 方案 / 校验

    [Fact]
    public void H1_P4_ProgramBody_UsesSchemaEnumCodes_AndValidatorCatchesWrongOnes()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var p4 = plan.FindItem("P4")!.Steps.Single(s => s.Id == "P4-3").Command!.Args!["steps"];
        var press = p4.EnumerateArray().First().GetProperty("params");
        Assert.Equal("A", press.GetProperty("gas").GetString());
        Assert.Equal("on", press.GetProperty("regulate").GetString());
        var json = PlanLoader.ToJson(plan);
        var bad = json.Replace("\"gas\": \"A\"", "\"gas\": \"a\"");
        var ex = Assert.Throws<PlanValidationException>(() => PlanLoader.Parse(bad));
        Assert.Contains(ex.Errors, e => e.Contains("gas") && e.Contains("不在枚举里"));
        var badBool = json.Replace("\"regulate\": \"on\"", "\"regulate\": true");
        ex = Assert.Throws<PlanValidationException>(() => PlanLoader.Parse(badBool));
        Assert.Contains(ex.Errors, e => e.Contains("regulate") && e.Contains("枚举码字符串"));
    }

    [Fact]
    public void M8_AllowCodes_MustBeRealOpCodes()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        foreach (var it in plan.AllItems()) foreach (var s in it.Steps) if (s.Command?.AllowCodes != null)
            Assert.All(s.Command.AllowCodes, c => Assert.Contains(c, PlanValidator.KnownOpCodes));
        var json = PlanLoader.ToJson(PlanLoader.LoadEmbedded(PlanLoader.SmokePlan));
        var ex = Assert.Throws<PlanValidationException>(() => PlanLoader.Parse(json.Replace("\"CHANNEL_STATE_INVALID\"", "\"ALREADY_ENABLED\"")));
        Assert.Contains(ex.Errors, e => e.Contains("allowCodes") && e.Contains("ALREADY_ENABLED"));
    }

    [Fact]
    public void Plan_FixedEncodings_F4_F6_T1_P3_S2_R0()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        Assert.Empty(PlanValidator.Validate(plan));
        // H2：急停后显式重新申请控制权；硬件急停单列
        Assert.Equal(CommandOps.ControlRequest, plan.FindItem("F4")!.Steps.Last().Command!.Op);
        var f4h = plan.FindItem("F4H")!;
        Assert.True(f4h.Enabled); Assert.Equal(5, f4h.Repeat!.Iterations); Assert.DoesNotContain(f4h.Steps, s => s.Command?.Op == CommandOps.Estop);
        // M6：F6-3 用录入的 n_max
        Assert.Equal("{manual:nMax}", plan.FindItem("F6")!.Steps.Single(s => s.Id == "F6-3").Command!.Args!["rpm"].GetString());
        // M2 / L6：T1 升温速率 Δ=5、窗口从下发 SP 起；T2 Δ=0
        var rate = plan.FindItem("T1")!.Steps.Single(s => s.Id == "T1-9").Metrics!.Single(m => m.Key == "rate");
        Assert.Equal(5, rate.Delta); Assert.Equal("from:T1-5", rate.Window);
        Assert.Equal(0, plan.FindItem("T2")!.Steps.Single(s => s.Id == "T2-9").Metrics!.Single(m => m.Key == "rateMax").Delta);
        Assert.All(plan.FindItem("T1")!.Steps.Single(s => s.Id == "T1-9").Metrics!.Where(m => m.Key is "os" or "tr" or "ts05"), m => Assert.Equal("from:T1-5", m.Window));
        // L2：TMaxSp ≥ 档位
        Assert.Equal("min", plan.FindItem("T1")!.Steps.Single(s => s.Id == "T1-2").Compare!.IterationAs);
        // M1：P3 压降只在测量窗拟合
        var p3 = plan.FindItem("P3")!;
        Assert.Equal(1800, p3.Steps.Single(s => s.Id == "P3-4b").DurationSec);
        Assert.All(p3.Steps.Single(s => s.Id == "P3-6").Metrics!, m => Assert.Equal("step:P3-4b", m.Window));
        Assert.Null(p3.Steps.Single(s => s.Id == "P3-3").Command!.AllowCodes);
        // M7：P4 t_P 带 = max(1 psi, 3%)
        var tp = plan.FindItem("P4")!.Steps.Single(s => s.Id == "P4-6").Metrics!.Single(m => m.Key == "tP");
        Assert.Equal(1, tp.Band); Assert.Equal(3, tp.BandPct);
        Assert.Equal(4.35, PerfRunner.EffectiveBand(tp, 145), 9);
        Assert.Equal(1.305, PerfRunner.EffectiveBand(tp, 43.5), 9);
        // L3：S2 转速被拒（RpmMax 未抬）→ 结束本项而不是中止
        Assert.Equal("skipItem", plan.FindItem("S2")!.Steps.Single(s => s.Id == "S2-2").OnFail);
        // L1：R0-5 环境字段回填键
        Assert.Equal("ambientTempC", plan.FindItem("R0")!.Steps.Single(s => s.Id == "R0-5").Fields!.Single(f => f.Key == "ambientC").EnvKey);
        // H3：带热 / 带压项都需 R0；F1 / F6 记通讯计数
        Assert.True(plan.FindItem("T1")!.NeedsPrecheck()); Assert.True(plan.FindItem("P4")!.NeedsPrecheck()); Assert.True(plan.FindItem("F4")!.NeedsPrecheck());
        Assert.False(plan.FindItem("S1")!.NeedsPrecheck()); Assert.False(plan.FindItem("F1")!.NeedsPrecheck());
        Assert.True(plan.FindItem("F1")!.RecordComm); Assert.True(plan.FindItem("F6")!.RecordComm);
        var smoke = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        Assert.False(smoke.FindItem("T0Q")!.NeedsPrecheck());   // 合成自检项显式关闭
    }

    // ====================================================================== PerfTest · 引擎

    private (PerfRunner Runner, FakeReactor Dev, FakeClock Clock, AutoPrompt Prompt) Make(TestPlan plan, double tau = 20)
    {
        var clock = new FakeClock();
        var dev = new FakeReactor(clock, tauSec: tau);
        var prompt = new AutoPrompt();
        var runner = new PerfRunner(plan, dev, dev, prompt, clock, new PerfRunnerOptions { OutputRoot = _root });
        return (runner, dev, clock, prompt);
    }

    private static RunRequest Req(TestPlan plan, bool r0SignedOff, params string[] items) => new()
    {
        PlanId = plan.PlanId, ItemIds = items.ToList(), Channels = new List<int> { 1 }, Operator = "测试员", R0SignedOff = r0SignedOff, RunId = "PT-FIX-" + Guid.NewGuid().ToString("N")[..6],
    };

    [Fact]
    public async Task H3_HeatItemWithoutR0_IsSkipped_UnlessSignedOff_AndSkippedPreconditionBlocks()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        // 未选 R0、未签字 → F4（带热）被引擎拒绝；S1（纯搅拌）照跑
        var (runner, dev, clock, prompt) = Make(plan);
        prompt.DefaultValue = 300;
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req(plan, false, "F4", "S1"), CancellationToken.None), clock, dev, maxSteps: 5000);
        Assert.Equal(ItemOutcome.Skipped, result.Items[0].Outcome);
        Assert.Contains("§0.4", result.Items[0].Note);
        Assert.DoesNotContain(dev.Commands, c => c.StartsWith("heatStart") || c == "estop");
        Assert.Equal(ItemOutcome.Completed, result.Items[1].Outcome);
        // R0 检查表没全勾 → T1 跳过 → 以 T1 为前置的 T4 也跳过（Skipped 也阻断）
        var (runner2, dev2, clock2, prompt2) = Make(plan);
        prompt2.OnConfirm = r => r.Checklist != null ? new ConfirmResult(true, r.Checklist.Select((_, i) => i != 0).ToList(), "甲", "乙") : ConfirmResult.Yes;
        var res2 = await PerfDrive.RunAsync(runner2.RunAsync(Req(plan, false, "R0", "T1", "T4"), CancellationToken.None), clock2, dev2, maxSteps: 3000);
        Assert.Equal(ItemOutcome.Aborted, res2.Items[0].Outcome);
        Assert.Equal(ItemOutcome.Skipped, res2.Items[1].Outcome);
        Assert.Equal(ItemOutcome.Skipped, res2.Items[2].Outcome);
        Assert.Contains("前置 T1 未通过", res2.Items[2].Note);
        Assert.DoesNotContain(dev2.Commands, c => c.StartsWith("heatStart"));
    }

    [Fact]
    public async Task H2_F4_SelfEstop_IsNotASafetyPause_AndControlIsReacquired_5Rounds()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var (runner, dev, clock, prompt) = Make(plan, tau: 10);
        prompt.DefaultValue = 1;   // t_heat 等录 1 s
        int estops = 0;
        dev.Intercept = c =>
        {
            if (c == "estop")
            {
                estops++;
                // 真机：WS control:invalidated 可能先于 REST 响应到达，急停帧随后到达，本机租约随之失效
                dev.RaiseSafety(new SafetyEvent(SafetyKind.ControlLost, "控制权变化：invalidated", "急停", clock.UtcNow));
                dev.RaiseSafety(new SafetyEvent(SafetyKind.Estop, "急停", "急停激活", clock.UtcNow));
                dev.RaiseSafety(new SafetyEvent(SafetyKind.ControlLost, "本机租约失效", "Holding→None：invalidated", clock.UtcNow));
                dev.CanControl = false;
            }
            return null;
        };
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req(plan, true, "F4"), CancellationToken.None), clock, dev, maxSteps: 12000);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(ItemOutcome.Completed, result.Items[0].Outcome);
        Assert.Empty(prompt.Safeties);                       // 预期内的急停不弹安全暂停
        Assert.Equal(5, estops);
        Assert.Equal(5, dev.Commands.Count(c => c == "controlRequest"));
        Assert.True(dev.CanControl);
        Assert.Equal(5, result.Items[0].Steps.Count(s => s.StepId == "F4-11" && s.Status == StepStatus.Done));
        Assert.Contains(result.Events, e => e.Kind == "safety" && e.Text.Contains("预期内"));
        // 急停之外的安全事件仍然暂停询问
        var (runner2, dev2, clock2, prompt2) = Make(PlanLoader.LoadEmbedded(PlanLoader.SmokePlan));
        var run2 = runner2.RunAsync(Req(plan, true, "S1Q"), CancellationToken.None);
        for (int i = 0; i < 4; i++) { dev2.Tick(); await clock2.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        dev2.RaiseSafety(new SafetyEvent(SafetyKind.Estop, "急停", "操作员按下", clock2.UtcNow));
        await PerfDrive.RunAsync(run2, clock2, dev2);
        Assert.Single(prompt2.Safeties);
    }

    [Fact]
    public async Task L4_SafetyWhileWaitingForEntry_IsHandledImmediately_AndCommLostStopsFirst()
    {
        var smoke = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        // ① 录入挂起期间报警 → 不等录入提交就弹安全卡；操作员选中止 → 运行中止
        var (runner, dev, clock, prompt) = Make(smoke);
        prompt.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prompt.SafetyAnswer = SafetyDecision.AbortRun;
        var run = runner.RunAsync(Req(smoke, true, "T0Q"), CancellationToken.None);
        for (int i = 0; i < 60 && prompt.Entries.Count == 0; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Single(prompt.Entries);
        dev.RaiseSafety(new SafetyEvent(SafetyKind.Alarm, "RV1 超温", "PV 99", clock.UtcNow, 1));
        for (int i = 0; i < 5 && prompt.Safeties.Count == 0; i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Single(prompt.Safeties);
        var result = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Equal(RunStatus.Aborted, result.Status);
        Assert.Contains("heatStop 1", dev.Commands);
        // ② 压力模块离线：询问之前先停止类收尾（停加热），操作员选继续后运行可完成
        var (runner2, dev2, clock2, prompt2) = Make(smoke);
        var run2 = runner2.RunAsync(Req(smoke, true, "T0Q"), CancellationToken.None);
        for (int i = 0; i < 8; i++) { dev2.Tick(); await clock2.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.True(dev2.Heating(1));
        dev2.RaiseSafety(new SafetyEvent(SafetyKind.CommLost, "压力模块离线", "§0.1-11", clock2.UtcNow));
        var res2 = await PerfDrive.RunAsync(run2, clock2, dev2);
        Assert.Single(prompt2.Safeties);
        int stopIdx = dev2.Commands.IndexOf("heatStop 1");
        Assert.True(stopIdx >= 0);
        Assert.Contains(res2.Events, e => e.Kind == "safety" && e.Text.Contains("先执行停止类收尾"));
        Assert.Equal(RunStatus.Completed, res2.Status);
    }

    [Fact]
    public async Task M4_Abort_CancelsAutotune_AndAbortsLeakTest()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var (runner, dev, clock, _) = Make(plan);
        var run = runner.RunAsync(Req(plan, true, "T0"), CancellationToken.None);
        for (int i = 0; i < 20 && !dev.Commands.Any(c => c.StartsWith("autotuneStart")); i++) { dev.Tick(); await clock.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Contains(dev.Commands, c => c.StartsWith("autotuneStart 1"));
        runner.Abort("测试");
        var r = await PerfDrive.RunAsync(run, clock, dev);
        Assert.Equal(RunStatus.Aborted, r.Status);
        Assert.Contains("autotuneCancel 1", dev.Commands);
        Assert.Contains("heatStop 1", dev.Commands);

        var (runner2, dev2, clock2, _) = Make(plan);
        var run2 = runner2.RunAsync(Req(plan, true, "P3"), CancellationToken.None);
        for (int i = 0; i < 30 && !dev2.Commands.Any(c => c.StartsWith("leakTestStart")); i++) { dev2.Tick(); await clock2.AdvanceAsync(TimeSpan.FromSeconds(1), 2); }
        Assert.Contains(dev2.Commands, c => c.StartsWith("leakTestStart 1 43.5"));
        runner2.Abort("测试");
        var r2 = await PerfDrive.RunAsync(run2, clock2, dev2);
        Assert.Equal(RunStatus.Aborted, r2.Status);
        Assert.Contains("leakTestAbort", dev2.Commands);
    }

    [Fact]
    public async Task M6_ManualTemplate_F6_UsesEnteredNMax_AndRecordsComm()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var (runner, dev, clock, prompt) = Make(plan);
        prompt.OnEntry = r => r.StepId == "F6-2" ? AutoPrompt.Fill(r, 850) : AutoPrompt.Fill(r, 10);
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req(plan, true, "F6"), CancellationToken.None), clock, dev, maxSteps: 6000);
        Assert.Equal(ItemOutcome.Completed, result.Items[0].Outcome);
        Assert.Contains("stir on=True rpm=850", dev.Commands);
        Assert.DoesNotContain("stir on=True rpm=1000", dev.Commands);
        Assert.NotNull(result.Items[0].CommStart);   // 假设备 GET /device/comm 为 null → 文案「不可用」，但字段已填
        Assert.Contains(result.Events, e => e.Kind == "comm");
    }

    [Fact]
    public async Task L1_R0_EnvironmentEntries_FillReportHeader()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var (runner, dev, clock, prompt) = Make(plan);
        prompt.OnEntry = r => new EntryResult(false, new Dictionary<string, IReadOnlyList<double>>
        {
            ["ambientC"] = new[] { 23.5 }, ["humidity"] = new[] { 45.0 }, ["atmKpa"] = new[] { 101.3 }, ["supplyV"] = new[] { 229.0 },
        }, new Dictionary<string, string> { ["assembly"] = "A-07 / 水 300 g" });
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req(plan, false, "R0"), CancellationToken.None), clock, dev, maxSteps: 500);
        Assert.Equal(ItemOutcome.Completed, result.Items[0].Outcome);
        Assert.Equal(23.5, result.Environment.AmbientTempC);
        Assert.Equal(45.0, result.Environment.HumidityPct);
        Assert.Equal(101.3, result.Environment.AtmPressureKpa);
        Assert.Equal(229.0, result.Environment.SupplyVoltageV);
        Assert.Equal("A-07 / 水 300 g", result.Environment.AssemblyNo);
        var html = File.ReadAllText(Path.Combine(_root, result.RunId, "report.html"));
        Assert.Contains("环境温度 23.5 ℃", html);
        Assert.Contains("A-07 / 水 300 g", html);
    }

    [Fact]
    public async Task L2_Compare_IterationAsMin_PassesWhenLimitAboveStep()
    {
        var plan = PlanLoader.Parse("""
        { "planId": "cmp", "title": "t", "version": "1", "source": "test", "stages": [ { "index": 1, "name": "s", "items": [ {
          "id": "C1", "title": "比较", "purpose": "p", "sourceRef": "test", "targetChannels": [1], "estimatedMinutes": 1, "requiresPrecheck": false,
          "repeat": { "variable": "tempC", "values": [50, 100, 300] },
          "steps": [
            { "id": "C1-1", "kind": "compare", "title": "TMaxSp ≥ {value}", "compare": { "key": "limits.tempSpMaxC", "iterationAs": "min", "hard": false }, "reference": "iteration", "onFail": "continue" },
            { "id": "C1-2", "kind": "compare", "title": "TMaxSp = {value}", "compare": { "key": "limits.tempSpMaxC", "hard": false }, "reference": "iteration", "onFail": "continue" } ] } ] } ] }
        """);
        var (runner, dev, clock, _) = Make(plan);
        var result = await PerfDrive.RunAsync(runner.RunAsync(Req(plan, false, "C1"), CancellationToken.None), clock, dev, maxSteps: 200);
        var steps = result.Items[0].Steps;
        // 设置 TMaxSp=250：≥50 ✓、≥100 ✓、≥300 ✗；=50 ✗、=100 ✗、=300 ✗
        Assert.Equal(new[] { true, false, true, false, false, false }, steps.Select(s => s.ComparePassed!.Value).ToArray());
        Assert.Equal("≥ 50", steps[0].CompareExpected);
        Assert.Equal("= 50", steps[1].CompareExpected);
    }

    [Fact]
    public void M2_HeatingRate_UsesDelta()
    {
        // 线性升温 1 ℃/s 从 25 到 60：Δ=0 时取 [28.5, 56.5]，Δ=5 时取 [28, 52]；两者斜率都应 ≈ 60 ℃/min，但拟合区间不同
        var pts = Enumerable.Range(0, 40).Select(i => new ParallelReactor.PerfTest.Measure.Pt(i, Math.Min(60, 25 + i))).ToList();
        Assert.Equal(60, ParallelReactor.PerfTest.Measure.Metrics.HeatingRate(pts, 60, 0), 6);
        Assert.Equal(60, ParallelReactor.PerfTest.Measure.Metrics.HeatingRate(pts, 60, 5), 6);
        // 末段减速（逼近）：Δ=5 把上界压到 (60−5)−25 的 90% = 52，不把慢段算进去 → 速率更接近真实快段
        var slow = Enumerable.Range(0, 60).Select(i => new ParallelReactor.PerfTest.Measure.Pt(i, i <= 27 ? 25 + i : Math.Min(60, 52 + (i - 27) * 0.25))).ToList();
        double r0 = ParallelReactor.PerfTest.Measure.Metrics.HeatingRate(slow, 60, 0), r5 = ParallelReactor.PerfTest.Measure.Metrics.HeatingRate(slow, 60, 5);
        Assert.True(r5 > r0 * 1.3, $"Δ=5 {r5:0.0} 应明显大于 Δ=0 {r0:0.0}");
    }

    // ====================================================================== 适配器：其他模块离线 > 30 s

    private sealed class FakeConn : IReactorConnection
    {
        public ClientOptions Options => null!; public ReactorApiClient Rest => null!; public ControlLeaseClient Lease => null!; public TelemetryWsClient Ws => null!;
        public TelemetryStore Telemetry { get; } = new(); public ControlledWrites Control => null!; public ChannelReader<object> Messages => null!;
        public DeviceInfoDto? Device => null; public ControlStateDto? ControlState => null;
        public event Action<TelemetrySnapshot>? TelemetryReceived; public event Action<EventMsg>? EventReceived; public event Action<AlarmMsg>? AlarmChanged; public event Action<ControlMsg>? ControlChanged;
        public event Action<OpEventMsg>? OpEventReceived; public event Action<LeaseTransition>? LeaseTransitioned; public event Action<ConnectionState>? ConnectionStateChanged; public event Action<Exception>? Error; public event Action? ServerRestarted;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Push(TelemetrySnapshot f) { Telemetry.Add(f); TelemetryReceived?.Invoke(f); }
        public void Touch() { EventReceived?.Invoke(null!); AlarmChanged?.Invoke(null!); ControlChanged?.Invoke(null!); OpEventReceived?.Invoke(null!); LeaseTransitioned?.Invoke(null!); ConnectionStateChanged?.Invoke(default); Error?.Invoke(null!); ServerRestarted?.Invoke(); }
    }

    [Fact]
    public void Adapter_OtherModuleOffline_Over30s_RaisesCommLostOnce()
    {
        var c = new FakeConn();
        using var feed = new TelemetryFeedAdapter(c);
        var events = new List<SafetyEvent>();
        feed.Safety += events.Add;
        var t0 = new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);
        TelemetrySnapshot F(long seq, bool temp, bool stir) => ClientRestTests.Frame(seq) with { Ts = t0.AddSeconds(seq), Comm = new CommSnap(temp, true, stir, true) };
        c.Push(F(1, false, true));
        c.Push(F(20, false, true));
        Assert.Empty(events);                                  // 19 s：未到 30 s
        c.Push(F(31, false, true));
        Assert.Single(events); Assert.Contains("温度模块", events[0].Title); Assert.Equal(SafetyKind.CommLost, events[0].Kind);
        c.Push(F(40, false, true));
        Assert.Single(events);                                 // 只报一次
        c.Push(F(41, true, true));
        c.Push(F(42, false, false));
        c.Push(F(80, false, false));
        Assert.Equal(3, events.Count);                         // 温度模块重新离线 ≥30 s 再报一次 + 搅拌驱动一次
        Assert.Contains(events, e => e.Title.Contains("搅拌驱动"));
    }
}
