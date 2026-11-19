using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// 步骤引擎的假设备（虚拟时钟）：一路釜的压力按阀态积分（SVn + 气源 → 以 FillRate 上升到气源压力；SVn + 排空 → 以 VentRate 下降，
/// VentBlocked 模拟进气单向阀放不掉），温度按 TempAt(Now) 给出；气路租约、稳压、计量、加热、搅拌都只记录调用。
/// </summary>
internal sealed class FakeIo : IStepIo
{
    public int Rv { get; set; } = 1;
    public ProgramLimitSettings Limits { get; set; } = new(200, 250, 450, 500, 510);
    public TimeSpan Now { get; private set; }

    public double P;                       // 釜压 psi
    public bool POffline;
    public double SourcePsi = 600, FillRate = 20, VentRate = 30;
    public bool VentBlocked;
    public double LeakPerMin;              // SV 关着时的压降 psi/min
    public double? FixedTemp = 25;
    public Func<TimeSpan, double?>? TempAt;
    public Action<FakeIo>? OnTick;
    public bool[] Frame = ValveFrame.AllClosed();

    public readonly List<(string Level, string Title, string Detail)> Logs = new();
    public readonly List<ReactorState> States = new();
    public readonly List<string> Calls = new();
    public readonly List<bool[]> Frames = new();
    public string LastStatus = "";

    public void Advance(double sec)
    {
        double left = sec;
        while (left > 1e-9)
        {
            bool sv = Frame[Rv - 1];
            bool gasOpen = Frame[ValveFrame.GasBase] || Frame[ValveFrame.GasBase + 1] || Frame[ValveFrame.GasBase + 2];
            bool moving = sv && (gasOpen || Frame[ValveFrame.Vent]);
            double dt = moving ? Math.Min(0.05, left) : Math.Min(1.0, left);
            if (sv && gasOpen) P = Math.Min(SourcePsi, P + FillRate * dt);
            else if (sv && Frame[ValveFrame.Vent]) { if (!VentBlocked) P = Math.Max(0, P - VentRate * dt); }
            else P = Math.Max(0, P - LeakPerMin * dt / 60.0);
            Now += TimeSpan.FromSeconds(dt);
            left -= dt;
            OnTick?.Invoke(this);
        }
    }

    public Task DelayAsync(double seconds, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Advance(seconds); return Task.CompletedTask; }
    public void Status(string text) => LastStatus = text;
    public void Log(string level, string title, string detail) => Logs.Add((level, title, detail));
    public void SetState(ReactorState state) => States.Add(state);
    public double PressureSp;
    public void SetPressureSp(double psi) => PressureSp = psi;
    public double? Temp => TempAt != null ? TempAt(Now) : FixedTemp;
    public double? Pressure => POffline ? null : P;
    public Task<double> ReadPressureAsync() => POffline ? throw new GasFaultException("压力读取失败（8AI 通讯）") : Task.FromResult(P);

    public string? GasBlock;
    public string? GasBlockReason(bool needPressure) => GasBlock;
    public int GasAcquired, GasReleased;
    public bool GasHeld => GasAcquired > GasReleased;
    public Task<IStepGas> AcquireGasAsync(string task, bool vessel, CancellationToken ct)
    {
        Calls.Add($"gas:{task}:{vessel}");
        GasAcquired++;
        return Task.FromResult<IStepGas>(new FakeGas(this));
    }

    public bool RegulationActive { get; set; }
    public (int Gas, double Target, double Refill)? Regulation;
    public Task StopRegulationAsync() { RegulationActive = false; Calls.Add("reg:stop"); return Task.CompletedTask; }
    public void StartRegulation(int gas, double target, double refillPct) { RegulationActive = true; Regulation = (gas, target, refillPct); }

    public bool UptakeCounting { get; set; }
    public bool UptakeValid { get; set; }
    public double UptakeMmol { get; set; }
    public Func<TimeSpan, double?>? RateAt;
    public double? GasRate => RateAt?.Invoke(Now);
    public void StartUptake(string why) { UptakeCounting = true; UptakeValid = true; Calls.Add("uptake:start"); }
    public void StopUptake(string why) { if (UptakeCounting) Calls.Add("uptake:stop"); UptakeCounting = false; }

    public string? HeatBlock;
    public string? HeatBlockReason() => HeatBlock;
    public double? FixedHeatTarget;
    public Task StartFixedHeatAsync(double temp) { FixedHeatTarget = temp; Calls.Add($"heat:fixed:{temp}"); return Task.CompletedTask; }
    public List<(double, double, double)>? Curve;
    public Task<bool> RunCurveAsync(IReadOnlyList<(double Temp, double Rate, double Hold)> segs, string head, CancellationToken ct)
    {
        double start = Temp ?? 25;
        double min = StepSpecs.CurveMinutes(segs, start);
        if (min <= 0) return Task.FromResult(false);
        Curve = segs.ToList();
        Calls.Add("heat:curve");
        Advance(min * 60);
        return Task.FromResult(true);
    }
    public string ApproachText => "";
    public Task StopHeatAsync(string why) { Calls.Add("heat:stop"); return Task.CompletedTask; }

    public StirShare StirShare { get; } = new();
    public int StirRpm { get; set; } = 300;
    public bool StirOn { get; set; }
    public void ReleaseStir()
    {
        Calls.Add("stir:release");
        bool others = StirShare.Release(Rv);
        if (!others) { StirOn = false; return; }
        if (StirShare.Current is { } cur) StirRpm = cur.Rpm;
    }
    public Task CloseOwnValveAsync() { Calls.Add("sv:close"); return Task.CompletedTask; }
    public string? EndReason { get; set; }

    public bool HasLog(string titlePart) => Logs.Any(l => l.Title.Contains(titlePart) || l.Detail.Contains(titlePart));

    private sealed class FakeGas : IStepGas
    {
        private readonly FakeIo _io;
        public FakeGas(FakeIo io) { _io = io; }
        public Task ApplyAsync(bool[] frame)
        {
            Assert.True(ValveFrame.IsSafe(frame));
            _io.Frame = (bool[])frame.Clone();
            _io.Frames.Add(_io.Frame);
            return Task.CompletedTask;
        }
        public Task HoldAsync(bool[] frame, double seconds) { _io.Advance(seconds); return Task.CompletedTask; }
        public Task LineFlushAsync(int gas, int cycles, Action<int, int, string> progress)
        {
            _io.Calls.Add($"flush:{gas}:{cycles}");
            _io.Advance(cycles * 9);
            return Task.CompletedTask;
        }
        public void ThrowIfUnsafe() { }
        public ValueTask DisposeAsync()
        {
            _io.Frame = ValveFrame.AllClosed();
            _io.GasReleased++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>v0.8 步骤引擎（Models/StepEngine.cs，STEPS-SPEC §1 / §2）：每种步骤的执行、失败路径与跨步规则。</summary>
public class StepEngineTests
{
    private static StepSnapshot S(StepType t, params (string K, object V)[] kv)
    {
        var p = StepSpecs.Defaults(t);
        foreach (var (k, v) in kv) p[k] = v;
        return StepSnapshot.Create(t, 3, t.Zh(), "", p);
    }

    private static StepSnapshot Curve(params (double, double, double)[] segs)
    {
        var p = StepSpecs.Defaults(StepType.Heat);
        p["mode"] = "curve";
        return StepSnapshot.Create(StepType.Heat, 3, "升温", "", p, segs);
    }

    private static Task Run(FakeIo io, StepSnapshot s) => StepEngine.RunAsync(io, s, "03/09 " + s.Name, CancellationToken.None);

    // ============ exchange ============

    [Fact]
    public void Exchange_FlushesLineThenCyclesFillAndRelease()
    {
        var io = new FakeIo();
        Run(io, S(StepType.Exchange, ("gas", "A"), ("cycles", 3.0), ("lineFlush", 2.0))).GetAwaiter().GetResult();
        Assert.Contains("flush:1:2", io.Calls);   // 气体 A = 气源 1，先吹总管 2 次
        // 每次置换：开 SV1 + 气体 A，之后开 SV1 + 排空
        Assert.Equal(3, io.Frames.Count(f => f[0] && f[ValveFrame.GasBase + 1]));
        Assert.Equal(3, io.Frames.Count(f => f[0] && f[ValveFrame.Vent]));
        Assert.True(io.P <= 2.9);
        Assert.False(io.GasHeld);
        Assert.True(io.HasLog("置换完成"));
        Assert.All(io.Frame, Assert.False);
    }

    [Fact]
    public void Exchange_ZeroCycles_OnlyFlushesLine_NoVesselValve()
    {
        var io = new FakeIo();
        Run(io, S(StepType.Exchange, ("cycles", 0.0), ("lineFlush", 3.0))).GetAwaiter().GetResult();
        Assert.Contains("flush:0:3", io.Calls);
        Assert.Contains("gas:RV1 程序 · 置换:False", io.Calls);   // 不开 SV：不监视、不需要压力
        Assert.DoesNotContain(io.Frames, f => f[0]);
    }

    [Fact]
    public void Exchange_FillTimeout_Faults()
    {
        var io = new FakeIo { SourcePsi = 30 };   // 减压阀出口只有 30 psi，充不到 72.5
        var ex = Assert.Throws<StepFaultException>(() => Run(io, S(StepType.Exchange, ("maxSec", 10.0))).GetAwaiter().GetResult());
        Assert.Contains("RV1 置换：充气 10 s 未到", ex.Message);
        Assert.Contains("气源压力不够或阀门没开", ex.Message);
        Assert.False(io.GasHeld);
        Assert.False(io.HasLog("置换完成"));
    }

    [Fact]
    public void Exchange_ReleaseTimeout_Faults_CheckValveWording()
    {
        var io = new FakeIo { VentBlocked = true };
        var ex = Assert.Throws<StepFaultException>(() => Run(io, S(StepType.Exchange, ("maxSec", 15.0))).GetAwaiter().GetResult());
        Assert.Contains("RV1 置换：开 SV1 + 排空阀 15 s 后釜压仍", ex.Message);
        Assert.Contains("可能有进气单向阀", ex.Message);
        Assert.Contains("不要开釜", ex.Message);
        Assert.False(io.GasHeld);
        Assert.False(io.HasLog("置换完成"));
    }

    // ============ vent ============

    [Fact]
    public void Vent_DropsPressure_ThenLogsVented()
    {
        var io = new FakeIo { P = 290 };
        Run(io, S(StepType.Vent, ("flush", 0.0))).GetAwaiter().GetResult();
        Assert.True(io.P <= 2.9);
        Assert.True(io.HasLog("已放空到"));
        Assert.False(io.GasHeld);
        Assert.Contains(io.Frames, f => f[0] && f[ValveFrame.Vent]);
    }

    [Fact]
    public void Vent_PressureNotDropping_Faults_AndNeverLogsVented()
    {
        var io = new FakeIo { P = 290, VentBlocked = true };
        var ex = Assert.Throws<StepFaultException>(() => Run(io, S(StepType.Vent, ("maxSec", 20.0))).GetAwaiter().GetResult());
        Assert.Contains("RV1 放空 20 s 后釜压仍", ex.Message);
        Assert.Contains("釜里的气放不掉 · 不要开釜", ex.Message);
        Assert.False(io.HasLog("已放空到"));   // 只有真的放下来了才写
        Assert.False(io.GasHeld);
    }

    [Fact]
    public void Vent_WithInertFlush_AndHotWarning()
    {
        var io = new FakeIo { P = 290, FixedTemp = 85 };
        Run(io, S(StepType.Vent, ("flush", 2.0), ("flushPres", 43.5))).GetAwaiter().GetResult();
        Assert.True(io.HasLog("热态放空会带出溶剂蒸气"));
        Assert.Equal(2, io.Frames.Count(f => f[0] && f[ValveFrame.GasBase]));   // 惰性气体 × 2
        Assert.True(io.P <= 2.9);
        Assert.Contains(io.Logs, l => l.Title.StartsWith("RV1 已放空到") && l.Detail.Contains("惰性置换 2 次"));
    }

    [Fact]
    public void Vent_StopsUptakeAndRegulation_LogsRegulationEnd()
    {
        var io = new FakeIo { P = 100, RegulationActive = true, UptakeCounting = true };
        Run(io, S(StepType.Vent, ("flush", 0.0))).GetAwaiter().GetResult();
        Assert.False(io.RegulationActive);
        Assert.False(io.UptakeCounting);
        Assert.Contains(io.Logs, l => l.Title == "RV1 稳压结束（进入 放空）");
    }

    // ============ leak ============

    [Fact]
    public void Leak_Pass_ThenVents()
    {
        var io = new FakeIo();
        Run(io, S(StepType.Leak, ("pres", 100.0), ("settle", 1.0), ("hold", 10.0), ("maxDrop", 2.0))).GetAwaiter().GetResult();
        Assert.Contains(io.Logs, l => l.Title == "RV1 气密通过");
        Assert.True(io.HasLog("已放空到"));
        Assert.True(io.P <= 2.9);
        // 充气用惰性气体
        Assert.Contains(io.Frames, f => f[0] && f[ValveFrame.GasBase]);
        Assert.DoesNotContain(io.Frames, f => f[ValveFrame.GasBase + 1] || f[ValveFrame.GasBase + 2]);
        Assert.Equal(2, io.GasAcquired);   // 充气一段 + 放空一段：保压期间不占用气路
    }

    [Fact]
    public void Leak_Fail_Stop_VentsThenFaults()
    {
        var io = new FakeIo { LeakPerMin = 0.2 };   // 12 psi/h
        var ex = Assert.Throws<StepFaultException>(() =>
            Run(io, S(StepType.Leak, ("pres", 100.0), ("settle", 0.0), ("hold", 10.0), ("maxDrop", 2.0), ("onFail", "stop"))).GetAwaiter().GetResult());
        Assert.Contains("RV1 气密不通过", ex.Message);
        Assert.Contains("> 阈值 2 psi/h", ex.Message);
        Assert.True(io.HasLog("已放空到"));   // 不通过且 onFail = stop：先放空（釜里是惰性气体）再失败
        Assert.True(io.P <= 2.9);
    }

    [Fact]
    public void Leak_Fail_Continue_WarnsAndKeepsPressure()
    {
        var io = new FakeIo { LeakPerMin = 0.2 };
        Run(io, S(StepType.Leak, ("pres", 100.0), ("settle", 0.0), ("hold", 10.0), ("onFail", "continue"), ("after", "keep"))).GetAwaiter().GetResult();
        Assert.Contains(io.Logs, l => l.Level == "warn" && l.Title == "RV1 气密不通过");
        Assert.False(io.HasLog("已放空到"));
        Assert.True(io.P > 90);
    }

    [Fact]
    public void Leak_TemperatureCompensation()
    {
        // 25 → 20 ℃：表压按等容理想气体下降，但没有泄漏
        double p0 = 435.1;
        double p1 = LeakMath.Compensate(p0, 25, 20);
        Assert.True(p0 - p1 > 5);   // 不补偿看起来降了 7 psi
        Assert.Equal(0, StepEngine.LeakDropRate(p0, 25, p1, 20, 30), 6);
        Assert.Equal((p0 - p1) * 2, StepEngine.LeakDropRate(p0, null, p1, 20, 30), 6);   // 读不到温度不补偿
    }

    [Fact]
    public void Leak_Engine_CompensatesCoolingDuringHold()
    {
        // 保压 30 min 内釜温 25 → 20 ℃，压力跟着温度走（无泄漏）：补偿后通过
        var io = new FakeIo();
        double? holdStartP = null;
        TimeSpan holdStart = default;
        io.TempAt = t => holdStartP == null ? 25 : 25 - 5 * Math.Min(1, (t - holdStart).TotalMinutes / 30);
        io.OnTick = f =>
        {
            if (holdStartP is double hp) f.P = LeakMath.Compensate(hp, 25, f.Temp!.Value);
        };
        // 静置 0：充完后立刻记 P0；用状态从「升压中」变「等待中」判断保压开始
        var s = S(StepType.Leak, ("pres", 100.0), ("settle", 0.0), ("hold", 30.0), ("after", "keep"));
        io.OnTick += f =>
        {
            if (holdStartP == null && f.States.Count > 0 && f.States[^1] == ReactorState.Wait) { holdStartP = f.P; holdStart = f.Now; }
        };
        Run(io, s).GetAwaiter().GetResult();
        Assert.True(io.P < holdStartP!.Value - 1);   // 表压确实降了
        Assert.Contains(io.Logs, l => l.Title == "RV1 气密通过" && l.Detail.Contains("温度补偿"));
    }

    // ============ press ============

    [Fact]
    public void Press_FillsToTarget_StartsUptakeAndRegulation()
    {
        var io = new FakeIo();
        Run(io, S(StepType.Press, ("gas", "inert"), ("pres", 100.0), ("tol", 2.0), ("refill", 4.0))).GetAwaiter().GetResult();
        Assert.InRange(io.P, 98, 110);
        Assert.Contains(io.Frames, f => f[0] && f[ValveFrame.GasBase]);   // 惰性气体充压（v0.8 新增）
        Assert.Contains("uptake:start", io.Calls);
        Assert.Equal((0, 100.0, 4.0), io.Regulation);
        Assert.False(io.GasHeld);
    }

    [Fact]
    public void Press_AlreadyAbove_DoesNotVent()
    {
        var io = new FakeIo { P = 150 };
        Run(io, S(StepType.Press, ("pres", 100.0), ("regulate", "off"))).GetAwaiter().GetResult();
        Assert.Equal(150, io.P);
        Assert.Equal(0, io.GasAcquired);
        Assert.Contains(io.Logs, l => l.Title.Contains("已高于目标") && l.Title.Contains("不放气"));
        Assert.Null(io.Regulation);
    }

    [Fact]
    public void Press_FillTimeout_GasFault()
    {
        var io = new FakeIo { FillRate = 0.5 };   // 每次脉冲只涨约 1 psi：不触发「无响应」，到 maxSec 超时
        var ex = Assert.Throws<GasFaultException>(() => Run(io, S(StepType.Press, ("pres", 290.0), ("maxSec", 60.0))).GetAwaiter().GetResult());
        Assert.Contains("RV1 充压 60 s 未到", ex.Message);
        Assert.False(io.GasHeld);
        Assert.Null(io.Regulation);
    }

    [Fact]
    public void Press_StopsPreviousRegulation_AndLogsIt()
    {
        var io = new FakeIo { RegulationActive = true, UptakeCounting = true };
        Run(io, S(StepType.Press, ("pres", 50.0))).GetAwaiter().GetResult();
        Assert.Contains(io.Logs, l => l.Title == "RV1 稳压结束（进入 充压）");
        Assert.Equal("reg:stop", io.Calls.First(c => c.StartsWith("reg:")));
        Assert.True(io.RegulationActive);   // 新的稳压
    }

    // ============ cross-step rules ============

    [Theory]
    [InlineData(StepType.Leak, true)]
    [InlineData(StepType.Exchange, true)]
    [InlineData(StepType.Press, true)]
    [InlineData(StepType.Vent, true)]
    [InlineData(StepType.End, true)]
    [InlineData(StepType.Heat, false)]
    [InlineData(StepType.Hold, false)]
    [InlineData(StepType.React, false)]
    [InlineData(StepType.Stir, false)]
    public void Regulation_StoppedOnlyByGasSteps(StepType t, bool stops)
    {
        Assert.Equal(stops, StepEngine.BreaksGas(t));
    }

    [Fact]
    public void Regulation_StoppedByExchange_IsLogged_HoldKeepsIt()
    {
        var io = new FakeIo { RegulationActive = true };
        Run(io, S(StepType.Hold, ("until", "time"), ("time", 1.0))).GetAwaiter().GetResult();
        Assert.True(io.RegulationActive);
        Run(io, S(StepType.Exchange, ("cycles", 1.0), ("lineFlush", 0.0))).GetAwaiter().GetResult();
        Assert.False(io.RegulationActive);
        Assert.Contains(io.Logs, l => l.Level == "info" && l.Title == "RV1 稳压结束（进入 置换）");
    }

    // ============ stir ============

    [Fact]
    public void Stir_IsNonBlocking_AndHoldsShare()
    {
        var io = new FakeIo();
        var t0 = io.Now;
        Run(io, S(StepType.Stir, ("rpm", 400.0))).GetAwaiter().GetResult();
        Assert.Equal(t0, io.Now);   // 立即进入下一步
        Assert.True(io.StirOn);
        Assert.Equal(400, io.StirRpm);
        Assert.True(io.StirShare.Holds(1));
        Assert.DoesNotContain(io.Logs, l => l.Level == "warn");
    }

    [Fact]
    public void Stir_ChangingSharedRpm_WarnsWithOtherProgram()
    {
        var io = new FakeIo { StirOn = true, StirRpm = 400 };
        io.StirShare.Request(2, 400);   // RV2 的程序正在用 400 rpm
        Run(io, S(StepType.Stir, ("rpm", 300.0))).GetAwaiter().GetResult();
        Assert.Equal(300, io.StirRpm);   // 照样写入
        Assert.Contains(io.Logs, l => l.Level == "warn" && l.Detail == "RV1 把共用搅拌从 400 改为 300 rpm（RV2 的程序也在用）");
    }

    [Fact]
    public void Stir_SameRpm_NoWarning_ZeroReleases()
    {
        var io = new FakeIo { StirOn = true, StirRpm = 400 };
        io.StirShare.Request(2, 400);
        Run(io, S(StepType.Stir, ("rpm", 400.0))).GetAwaiter().GetResult();
        Assert.DoesNotContain(io.Logs, l => l.Level == "warn");
        Run(io, S(StepType.Stir, ("rpm", 0.0))).GetAwaiter().GetResult();
        Assert.False(io.StirShare.Holds(1));
        Assert.True(io.StirOn);   // RV2 还在用
        Assert.True(io.HasLog("RV2 程序仍在搅拌"));
    }

    // ============ heat ============

    [Fact]
    public void Heat_Fixed_WaitsBandThenBalances()
    {
        var io = new FakeIo { TempAt = t => t.TotalMinutes < 20 ? 25 + 2.5 * t.TotalMinutes : 80 };
        Run(io, S(StepType.Heat, ("temp", 80.0), ("band", 2.0), ("wait", 15.0))).GetAwaiter().GetResult();
        Assert.Equal(80, io.FixedHeatTarget);
        // 约 21 min 进入 ±2（78 ℃），再平衡 15 min
        Assert.InRange(io.Now.TotalMinutes, 35, 38);
    }

    [Fact]
    public void Heat_Fixed_WithRate_RunsSingleSegmentCurve()
    {
        var io = new FakeIo { FixedTemp = 25 };
        io.TempAt = t => io.Curve == null ? 25 : 80;
        Run(io, S(StepType.Heat, ("temp", 80.0), ("rate", 5.0), ("wait", 0.0))).GetAwaiter().GetResult();
        Assert.Equal(new List<(double, double, double)> { (80, 5, 0) }, io.Curve);
        Assert.Null(io.FixedHeatTarget);
    }

    [Fact]
    public void Heat_Timeout_Stop_Faults()
    {
        var io = new FakeIo { FixedTemp = 25 };
        var ex = Assert.Throws<StepFaultException>(() =>
            Run(io, S(StepType.Heat, ("temp", 80.0), ("maxWait", 10.0), ("onTimeout", "stop"))).GetAwaiter().GetResult());
        Assert.Equal("RV1 升温 10 min 未到 80 ± 2 °C（当前 25.0℃）", ex.Message);
    }

    [Fact]
    public void Heat_Timeout_Continue_WarnsAndSkipsBalance()
    {
        var io = new FakeIo { FixedTemp = 25 };
        Run(io, S(StepType.Heat, ("temp", 80.0), ("maxWait", 10.0), ("wait", 30.0), ("onTimeout", "continue"))).GetAwaiter().GetResult();
        Assert.Contains(io.Logs, l => l.Level == "warn" && l.Detail.Contains("升温 10 min 未到 80 ± 2 °C"));
        Assert.InRange(io.Now.TotalMinutes, 10, 10.2);   // 不再平衡 30 min
    }

    [Fact]
    public void Heat_TempOffline60s_Faults()
    {
        var io = new FakeIo { FixedTemp = null };
        var ex = Assert.Throws<StepFaultException>(() => Run(io, S(StepType.Heat, ("temp", 80.0))).GetAwaiter().GetResult());
        Assert.Contains("温控离线超过 60s", ex.Message);
        Assert.InRange(io.Now.TotalSeconds, 60, 64);
    }

    [Fact]
    public void Heat_Curve_WaitsBandAfterCurve_MaxWaitFromCurveEnd()
    {
        var io = new FakeIo();
        double curveEnd = -1;
        io.TempAt = t => io.Curve == null ? 25 : (curveEnd >= 0 && t.TotalMinutes > curveEnd + 5 ? 120 : 100);
        io.OnTick = f => { if (f.Curve != null && curveEnd < 0) curveEnd = f.Now.TotalMinutes; };
        Run(io, Curve((80, 5, 10), (120, 2, 20))).GetAwaiter().GetResult();
        Assert.NotNull(io.Curve);
        Assert.True(io.Now.TotalMinutes > curveEnd + 5);   // 曲线时间到不算完：等实测到位

        var io2 = new FakeIo();
        io2.TempAt = t => io2.Curve == null ? 25 : 100;   // 永远不到 120
        var ex = Assert.Throws<StepFaultException>(() => Run(io2, Curve((120, 5, 0))).GetAwaiter().GetResult());
        Assert.Contains("升温 120 min 未到 120 ± 2 °C", ex.Message);
        double curveMin = (120 - 25) / 5.0;
        Assert.InRange(io2.Now.TotalMinutes, curveMin + 120, curveMin + 120.2);   // maxWait 从曲线结束开始算
    }

    [Fact]
    public void Heat_Blocked_Faults()
    {
        var io = new FakeIo { HeatBlock = "温控离线" };
        var ex = Assert.Throws<StepFaultException>(() => Run(io, S(StepType.Heat)).GetAwaiter().GetResult());
        Assert.Equal("升温：温控离线", ex.Message);
    }

    // ============ hold ============

    [Fact]
    public void Hold_Time()
    {
        var io = new FakeIo();
        Run(io, S(StepType.Hold, ("until", "time"), ("time", 12.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 12, 12.05);
        Assert.Equal(ReactorState.Wait, io.States[^1]);
    }

    [Fact]
    public void Hold_TempBelow_EndsWhenReached()
    {
        var io = new FakeIo { TempAt = t => 80 - t.TotalMinutes };
        Run(io, S(StepType.Hold, ("until", "tempBelow"), ("temp", 40.0), ("maxWait", 120.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 40, 40.1);
        Assert.True(io.HasLog("保持完成"));
    }

    [Fact]
    public void Hold_PressAbove_Timeout_Continue()
    {
        var io = new FakeIo { P = 10 };
        Run(io, S(StepType.Hold, ("until", "pressAbove"), ("pres", 50.0), ("maxWait", 5.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 5, 5.05);
        Assert.Contains(io.Logs, l => l.Level == "warn" && l.Detail.Contains("保持 5 min 未达到"));
    }

    [Fact]
    public void Hold_SensorOffline_TreatedAsTimeoutAfter60s()
    {
        var io = new FakeIo { FixedTemp = null };
        Run(io, S(StepType.Hold, ("until", "tempAbove"), ("temp", 40.0), ("maxWait", 60.0), ("onTimeout", "continue"))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalSeconds, 60, 62);   // 不是悄悄按时间等 60 min
        Assert.Contains(io.Logs, l => l.Level == "warn" && l.Detail.Contains("温控离线超过 60 s"));

        var io2 = new FakeIo { POffline = true };
        var ex = Assert.Throws<StepFaultException>(() =>
            Run(io2, S(StepType.Hold, ("until", "pressBelow"), ("pres", 5.0), ("onTimeout", "stop"))).GetAwaiter().GetResult());
        Assert.Contains("压力采集离线超过 60 s", ex.Message);
    }

    // ============ react ============

    [Fact]
    public void React_Stall_CountsOnlyAfterMinTime()
    {
        var io = new FakeIo { UptakeCounting = true, UptakeValid = true, RateAt = _ => 0.001 };
        Run(io, S(StepType.React, ("time", 480.0), ("minTime", 30.0), ("stallOn", "on"), ("stallRate", 0.005), ("stallMin", 30.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 60, 60.1);   // 最短 30 + 持续 30（minTime 之前的低速率不算）
        Assert.Equal("吸气停止（30 min < 0.005 mmol/min）", io.EndReason);
        Assert.Contains(io.Logs, l => l.Title == "RV1 反应结束 · 吸气停止（30 min < 0.005 mmol/min）");
    }

    [Fact]
    public void React_Stall_NullRateDoesNotCount()
    {
        // 前 40 min 速率为 null（数据不够），之后一直很低：从 40 min 起计，70 min 结束
        var io = new FakeIo { UptakeCounting = true, UptakeValid = true, RateAt = t => t.TotalMinutes < 40 ? null : 0.001 };
        Run(io, S(StepType.React, ("minTime", 30.0), ("stallMin", 30.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 70, 70.1);
        // 中途出现 null：计时重新开始
        var io2 = new FakeIo { UptakeCounting = true, UptakeValid = true, RateAt = t => t.TotalMinutes is > 45 and < 46 ? null : 0.001 };
        Run(io2, S(StepType.React, ("minTime", 30.0), ("stallMin", 30.0))).GetAwaiter().GetResult();
        Assert.InRange(io2.Now.TotalMinutes, 76, 76.1);
    }

    [Fact]
    public void React_HighRate_RunsToMaxTime()
    {
        var io = new FakeIo { UptakeCounting = true, UptakeValid = true, RateAt = _ => 0.05 };
        Run(io, S(StepType.React, ("time", 90.0), ("minTime", 10.0))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 90, 90.05);
        Assert.Equal("最长时间到", io.EndReason);
    }

    [Fact]
    public void React_MmolReached_AfterMinTime()
    {
        var io = new FakeIo { UptakeCounting = true, UptakeValid = true, RateAt = _ => 0.1 };
        io.OnTick = f => f.UptakeMmol = 0.1 * f.Now.TotalMinutes;   // 5 mmol 在 50 min
        Run(io, S(StepType.React, ("minTime", 60.0), ("mmolOn", "on"), ("mmol", 5.0), ("stallOn", "off"))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 60, 60.1);   // 到了 5 mmol 但没过最短时间，等到 60 min
        Assert.Equal("吸气量达到 5.00 mmol", io.EndReason);
    }

    [Fact]
    public void React_NotMetered_OnlyTime_WarnsOnce()
    {
        var io = new FakeIo { UptakeCounting = false, RateAt = _ => 0.0 };
        Run(io, S(StepType.React, ("time", 45.0), ("minTime", 0.0), ("mmolOn", "on"), ("mmol", 0.01))).GetAwaiter().GetResult();
        Assert.InRange(io.Now.TotalMinutes, 45, 45.05);
        Assert.Equal("最长时间到", io.EndReason);
        Assert.Single(io.Logs, l => l.Level == "warn" && l.Detail.Contains("吸气量未计量"));
    }

    // ============ end ============

    [Fact]
    public void End_StopsHeatReleasesStirClosesValve()
    {
        var io = new FakeIo { RegulationActive = true, UptakeCounting = true, StirOn = true };
        io.StirShare.Request(1, 400);
        io.EndReason = "最长时间到";
        Run(io, S(StepType.End)).GetAwaiter().GetResult();
        Assert.Contains("heat:stop", io.Calls);
        Assert.Contains("stir:release", io.Calls);
        Assert.Contains("sv:close", io.Calls);
        Assert.False(io.StirOn);
        Assert.False(io.RegulationActive);
        Assert.False(io.UptakeCounting);
        Assert.Equal(ReactorState.Done, io.States[^1]);
        Assert.True(io.HasLog("结束原因：最长时间到"));
    }

    [Fact]
    public void End_DoesNotStopOperatorStir_WhenProgramNeverHeldIt()
    {
        var io = new FakeIo { StirOn = true };   // 操作员手动开的搅拌，本程序没有搅拌步骤
        Run(io, S(StepType.End)).GetAwaiter().GetResult();
        Assert.True(io.StirOn);
        Assert.DoesNotContain("stir:release", io.Calls);
    }

    [Fact]
    public void LegacyStep_Faults()
    {
        var io = new FakeIo();
        var w = StepSnapshot.Create(StepType.Wait, 1, "等待", "", new Dictionary<string, object>());
        Assert.Throws<StepFaultException>(() => Run(io, w).GetAwaiter().GetResult());
    }
}
