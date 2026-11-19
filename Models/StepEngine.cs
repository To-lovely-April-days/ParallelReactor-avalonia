using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Models;

/// <summary>程序步骤故障（STEPS-SPEC「停止这一路」）：这一路的程序失败，照现在的程序失败收尾（不自动放空）。</summary>
public sealed class StepFaultException : Exception
{
    public StepFaultException(string message) : base(message) { }
}

/// <summary>
/// 步骤引擎看到的设备（一路程序一份）。ViewModels/ProgramViewModel.Run.cs 的 StepIo 把它接到 MainViewModel（真实的气路租约、
/// 阀门、温控、搅拌、吸气计量、事件）；单元测试用虚拟时钟的假设备（tests/StepEngineTests.cs）。
/// 约定：所有等待都经 <see cref="DelayAsync"/> / <see cref="IStepGas.HoldAsync"/>（真机实现里带安全检查与心跳），计时一律用 <see cref="Now"/>。
/// </summary>
public interface IStepIo
{
    /// <summary>通道号 1..8。</summary>
    int Rv { get; }
    /// <summary>当前全局限值（设置可能在下发后被改）。</summary>
    ProgramLimitSettings Limits { get; }
    /// <summary>单调时间。</summary>
    TimeSpan Now { get; }
    /// <summary>切片等待（真机：每秒查急停 / 本通道报警 / 被外部停止）。</summary>
    Task DelayAsync(double seconds, CancellationToken ct);
    /// <summary>运行状态一行文字。</summary>
    void Status(string text);
    /// <summary>事件（level = info / warn / err）。</summary>
    void Log(string level, string title, string detail);
    void SetState(ReactorState state);
    /// <summary>界面上的压力目标（PSp，psi）。</summary>
    void SetPressureSp(double psi);
    /// <summary>釜温 PV ℃（离线为 null）。</summary>
    double? Temp { get; }
    /// <summary>最近一次采样的釜压 psi（离线为 null）。</summary>
    double? Pressure { get; }
    /// <summary>立即读一次这一路的压力 psi（读不到抛 <see cref="GasFaultException"/>）。</summary>
    Task<double> ReadPressureAsync();

    /// <summary>气路公共检查（手动阀控 / 泄漏测试 / 其他通道超压未解除 / 压力采集离线 …）：null = 可以。</summary>
    string? GasBlockReason(bool needPressure);
    /// <summary>排队取共用气路（AcquireGasAsync，先到先用，超过上限故障）。vessel = 要开这一路的 SV（参与、监视、需要压力读数）。</summary>
    Task<IStepGas> AcquireGasAsync(string task, bool vessel, CancellationToken ct);

    /// <summary>这一路的后台稳压回路在运行。</summary>
    bool RegulationActive { get; }
    Task StopRegulationAsync();
    /// <summary>启动后台稳压：P &lt; 目标 × (1 − refill%)（且不小于 1 psi）时补气。</summary>
    void StartRegulation(int gas, double target, double refillPct);

    bool UptakeCounting { get; }
    bool UptakeValid { get; }
    double UptakeMmol { get; }
    /// <summary>最近 1 min 平均吸气速率 mmol/min（数据不够 / 未计量为 null）。</summary>
    double? GasRate { get; }
    void StartUptake(string why);
    void StopUptake(string why);

    string? HeatBlockReason();
    /// <summary>定值加热（防过冲逼近）。</summary>
    Task StartFixedHeatAsync(double temp);
    /// <summary>跑一段曲线到结束（SP 由引擎周期下发）。没有有效段（例如单段且起点已在目标）返回 false，什么也不做。</summary>
    Task<bool> RunCurveAsync(IReadOnlyList<(double Temp, double Rate, double Hold)> segs, string head, CancellationToken ct);
    /// <summary>逼近器的状态文字（没有为空串）。</summary>
    string ApproachText { get; }
    Task StopHeatAsync(string why);

    /// <summary>全机共用搅拌的程序引用计数。</summary>
    StirShare StirShare { get; }
    /// <summary>全局转速（釜转速）；写入即下发。</summary>
    int StirRpm { get; set; }
    bool StirOn { get; set; }
    /// <summary>释放这一路的搅拌持有（最后一个释放者才停；仍有别的持有者时恢复其转速并记事件）。</summary>
    void ReleaseStir();
    /// <summary>关这一路的 SV（开着时）。</summary>
    Task CloseOwnValveAsync();

    /// <summary>程序结束原因（反应步骤写入，<see cref="ProgramRunInfo.EndReason"/>）。</summary>
    string? EndReason { get; set; }
}

/// <summary>一段气路租约（using 结束 = 全关阀、释放气路）。阀与保持都在租约的令牌下进行（程序中止 / 超压联锁取消它）。</summary>
public interface IStepGas : IAsyncDisposable
{
    Task ApplyAsync(bool[] frame);
    Task HoldAsync(bool[] frame, double seconds);
    /// <summary>吹总管（旧 purge：气源开 3 s → 关 → 排空开 5 s → 关，死区 0.5 s，SV 全程关）。</summary>
    Task LineFlushAsync(int gas, int cycles, Action<int, int, string> progress);
    void ThrowIfUnsafe();
}

/// <summary>
/// v0.8 步骤的执行语义（STEPS-SPEC §1 / §2，纯逻辑）：leak / exchange / stir / heat / press / hold / react / vent / end。
/// 跨步规则：进入 leak / exchange / press / vent / end 之前停止稳压（记「RVn 稳压结束（进入 x）」）并停止吸气计量。
/// 「停止这一路」抛 <see cref="StepFaultException"/>（程序失败，不自动放空；唯一例外：气密不通过且 onFail = stop 先放空）。
/// </summary>
public static class StepEngine
{
    /// <summary>置换 / 放空读压间隔 s。</summary>
    public const double PollSec = 0.2;
    /// <summary>传感器离线超过这个时间按超时 / 故障处理 s。</summary>
    public const double OfflineSec = 60;
    /// <summary>高于这个温度放空记 warn ℃。</summary>
    public const double HotVentC = 60;
    /// <summary>气密检查充气：偏差 %、最长 s；放空到 psi。</summary>
    public const double LeakTolPct = 2, LeakFillMaxSec = 180, LeakVentTo = 2.9, LeakVentMaxSec = 120;
    /// <summary>吹总管的充 / 排时间 s（旧 purge）。</summary>
    public const double FlushFillSec = 3, FlushVentSec = 5;

    /// <summary>开始前要停止稳压与计量的步骤（STEPS-SPEC §2）。</summary>
    public static bool BreaksGas(StepType t) => t is StepType.Leak or StepType.Exchange or StepType.Press or StepType.Vent or StepType.End;

    public static async Task RunAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (BreaksGas(s.Type))
        {
            if (io.RegulationActive)
            {
                await io.StopRegulationAsync();
                io.Log("info", $"RV{io.Rv} 稳压结束（进入 {s.Name}）", $"第 {s.Ordinal:00} 步 {s.Name}开始，后台稳压停止");
            }
            io.StopUptake($"第 {s.Ordinal:00} 步{s.Name}");
            ct.ThrowIfCancellationRequested();
        }
        switch (s.Type)
        {
            case StepType.Leak: await LeakAsync(io, s, head, ct); break;
            case StepType.Exchange: await ExchangeAsync(io, s, head, ct); break;
            case StepType.Stir: Stir(io, s, head); break;
            case StepType.Heat: await HeatAsync(io, s, head, ct); break;
            case StepType.Press: await PressAsync(io, s, head, ct); break;
            case StepType.Hold: await HoldAsync(io, s, head, ct); break;
            case StepType.React: await ReactAsync(io, s, head, ct); break;
            case StepType.Vent:
                await VentCoreAsync(io, s.Num("to", 2.9), s.Num("maxSec", 120), (int)Math.Round(s.Num("flush", 3)), s.Num("flushPres", 43.5), head, ct);
                break;
            case StepType.End: await EndAsync(io, s, head); break;
            default: throw new StepFaultException($"第 {s.Ordinal:00} 步「{s.Type.Zh()}」是旧格式，须先转换");
        }
    }

    // ============ leak 气密检查 ============

    public static async Task LeakAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        double pres = s.Num("pres", 435.1), settle = s.Num("settle", 5), hold = s.Num("hold", 30), maxDrop = s.Num("maxDrop", StepSpecs.LeakRateDefault);
        bool stopOnFail = s.Str("onFail", "stop") != "continue", ventAfter = s.Str("after", "vent") != "keep";
        if (io.GasBlockReason(needPressure: true) is { } why) throw new StepFaultException($"气密检查：{why}");
        io.SetState(ReactorState.Pressing);
        io.SetPressureSp(pres);
        io.Status(head);
        io.Log("info", $"RV{rv} 程序 · 气密检查",
            $"惰性气体充到 {Units.FormatP(pres)} · 稳定 {StepSpecs.N(settle)} min · 保压 {StepSpecs.N(hold)} min · 允许 ≤ {StepSpecs.N(maxDrop)} psi/h");
        await using (var g = await io.AcquireGasAsync($"RV{rv} 程序 · 气密检查", vessel: true, ct))
        {
            await FillToAsync(io, g, 0, pres, LeakTolPct, LeakFillMaxSec, head);
        }
        // 充完释放气路：静置与保压期间 SV 关着，别的路可以同时充气
        io.SetState(ReactorState.Wait);
        var t0 = io.Now;
        while ((io.Now - t0).TotalMinutes < settle)
        {
            io.Status($"{head} · 静置 剩 {Left(io, t0, settle)}");
            await io.DelayAsync(1, ct);
        }
        double p0 = await io.ReadPressureAsync();
        double? temp0 = io.Temp;
        t0 = io.Now;
        while ((io.Now - t0).TotalMinutes < hold)
        {
            io.Status($"{head} · 保压 剩 {Left(io, t0, hold)} · P0 {Units.FormatP(p0)} · 当前 {(io.Pressure is double pn ? Units.FormatP(pn) : "—")}");
            await io.DelayAsync(1, ct);
        }
        double p1 = await io.ReadPressureAsync();
        double? temp1 = io.Temp;
        double rate = LeakDropRate(p0, temp0, p1, temp1, hold);
        if (rate <= maxDrop)
            io.Log("info", $"RV{rv} 气密通过", $"压降 {rate:0.00} psi/h ≤ 阈值 {StepSpecs.N(maxDrop)} psi/h" + CompText(temp0, temp1));
        else
        {
            string msg = $"{rate:0.00} psi/h > 阈值 {StepSpecs.N(maxDrop)} psi/h";
            if (stopOnFail)
            {
                io.Log("err", $"RV{rv} 气密不通过", msg + CompText(temp0, temp1) + " · 放空后停止这一路");
                await VentCoreAsync(io, LeakVentTo, LeakVentMaxSec, 0, 0, head, ct);
                throw new StepFaultException($"RV{rv} 气密不通过 · {msg}");
            }
            io.Log("warn", $"RV{rv} 气密不通过", msg + CompText(temp0, temp1) + " · 记录后继续");
        }
        if (ventAfter) await VentCoreAsync(io, LeakVentTo, LeakVentMaxSec, 0, 0, head, ct);
    }

    /// <summary>
    /// 压降速率 psi/h（正 = 在降）：(P0 − P1·T0/T1) / (hold/60)，P 用绝压、T 用绝对温度做温度补偿（<see cref="LeakMath.Compensate"/>）；
    /// 任一温度读不到就不补偿（同泄漏测试的 LeakFit）。
    /// </summary>
    public static double LeakDropRate(double p0, double? t0, double p1, double? t1, double holdMin)
    {
        double p1c = t0 is double a && t1 is double b && a > -273 && b > -273 ? LeakMath.Compensate(p1, b, a) : p1;
        return holdMin > 0 ? (p0 - p1c) / (holdMin / 60.0) : 0;
    }

    private static string CompText(double? t0, double? t1)
        => t0 is double a && t1 is double b ? $" · 温度补偿 {a:0.0} → {b:0.0}℃" : " · 温度读不到，未补偿";

    // ============ exchange 置换 ============

    public static async Task ExchangeAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        string gasCode = s.Str("gas", "inert");
        int gi = StepSpecs.GasIndex(gasCode);
        string gasName = StepSpecs.GasLabel("exchange", gasCode);
        double fill = s.Num("fill", 72.5), release = s.Num("release", 2.9), maxSec = s.Num("maxSec", 60);
        int cycles = (int)Math.Round(s.Num("cycles", 3)), flush = (int)Math.Round(s.Num("lineFlush", 2));
        bool vessel = cycles > 0;
        if (io.GasBlockReason(needPressure: vessel) is { } why) throw new StepFaultException($"置换：{why}");
        if (vessel) io.SetState(ReactorState.Pressing);
        io.Status(head);
        io.Log("info", $"RV{rv} 程序 · 置换",
            vessel ? $"{gasName} · {Units.FormatP(fill)} → {Units.FormatP(release)} × {cycles} 次 · 先吹总管 {flush} 次"
                   : $"{gasName} · 只吹总管 {flush} 次（SV 关，釜不置换）");
        await using (var g = await io.AcquireGasAsync($"RV{rv} 程序 · 置换", vessel, ct))
        {
            if (flush > 0)
                await g.LineFlushAsync(gi, flush, (c, n, phase) => io.Status($"{head} · 吹总管 {gasName} {c}/{n} · {phase}"));
            for (int i = 1; i <= cycles; i++)
                await ExchangeCycleAsync(io, g, gi, fill, release, maxSec, $"{head} · {gasName} {i}/{cycles}", "置换");
            await g.ApplyAsync(ValveFrame.AllClosed());
        }
        io.Log("info", $"RV{rv} 置换完成", $"{gasName} · {cycles} 次" + (flush > 0 ? $" · 吹总管 {flush} 次" : ""));
    }

    /// <summary>
    /// 一次置换：开 SVn + 气源每 0.2 s 读压，≥ fill（或到压力上限）关；静置 1 s；开 SVn + 排空每 0.2 s 读压，≤ release 关。
    /// 充或放超过 maxSec 都是故障（充不上去 / 放不下来），文字照 STEPS-SPEC §1.2 / §1.8。
    /// </summary>
    public static async Task ExchangeCycleAsync(IStepIo io, IStepGas g, int gi, double fill, double release, double maxSec, string status, string what)
    {
        int rv = io.Rv;
        double cap = ProgramLimits.PressMax(io.Limits);
        // 充
        var frame = ValveFrame.Gas(gi, rv);
        await g.ApplyAsync(frame);
        var t0 = io.Now;
        while (true)
        {
            await g.HoldAsync(frame, PollSec);
            double p = await io.ReadPressureAsync();
            io.Status($"{status} · 充 {Units.FormatP(p)} / {Units.FormatP(fill)}");
            if (p >= fill || p >= cap) break;
            if ((io.Now - t0).TotalSeconds >= maxSec)
            {
                await g.ApplyAsync(ValveFrame.AllClosed());
                throw new StepFaultException($"RV{rv} {what}：充气 {StepSpecs.N(maxSec)} s 未到 {Units.FormatP(fill)}（气源压力不够或阀门没开）");
            }
        }
        await g.ApplyAsync(ValveFrame.AllClosed());
        await g.HoldAsync(ValveFrame.AllClosed(), 1.0);
        // 放
        await VentToAsync(io, g, release, maxSec, status, what);
    }

    /// <summary>开 SVn + 排空每 0.2 s 读压，到 ≤ to 关阀并返回读数；到 maxSec 还没到就是故障（釜里的气放不掉）。</summary>
    private static async Task<double> VentToAsync(IStepIo io, IStepGas g, double to, double maxSec, string status, string what)
    {
        int rv = io.Rv;
        var frame = ValveFrame.VentOnly(rv);
        await g.ApplyAsync(frame);
        var t0 = io.Now;
        while (true)
        {
            await g.HoldAsync(frame, PollSec);
            double p = await io.ReadPressureAsync();
            io.Status($"{status} · 放 {Units.FormatP(p)} / {Units.FormatP(to)}");
            if (p <= to)
            {
                await g.ApplyAsync(ValveFrame.AllClosed());
                return p;
            }
            if ((io.Now - t0).TotalSeconds >= maxSec)
            {
                await g.ApplyAsync(ValveFrame.AllClosed());
                throw new StepFaultException(what == "放空"
                    ? $"RV{rv} 放空 {StepSpecs.N(maxSec)} s 后釜压仍 {Units.FormatP(p)} · 釜里的气放不掉 · 不要开釜"
                    : $"RV{rv} {what}：开 SV{rv} + 排空阀 {StepSpecs.N(maxSec)} s 后釜压仍 {Units.FormatP(p)} · 釜里的气放不掉（可能有进气单向阀）· 不要开釜");
            }
        }
    }

    // ============ vent 放空 ============

    /// <summary>
    /// 放空（vent 步骤，以及气密检查不通过 / 之后放空）：停稳压与计量（RunAsync 已做）→ 取得气路 → 开 SVn + 排空放到 ≤ to →
    /// 惰性置换 flush 次（fill = flushPres、release = to、maxSec 同）→ 全关、释放气路 → 记「RVn 已放空到 y」（只有真的放下来了才写）。
    /// </summary>
    public static async Task VentCoreAsync(IStepIo io, double to, double maxSec, int flush, double flushPres, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        io.StopUptake("放空");
        if (io.Temp is double t && t > HotVentC)
            io.Log("warn", $"RV{rv} 热态放空", $"当前 {t:0.0}℃ · 热态放空会带出溶剂蒸气");
        if (io.GasBlockReason(needPressure: true) is { } why) throw new StepFaultException($"放空：{why}");
        io.SetState(ReactorState.Pressing);
        io.SetPressureSp(0);
        io.Status($"{head} · 放空");
        double p;
        await using (var g = await io.AcquireGasAsync($"RV{rv} 程序 · 放空", vessel: true, ct))
        {
            p = await VentToAsync(io, g, to, maxSec, $"{head} · 放空", "放空");
            for (int i = 1; i <= flush; i++)
                await ExchangeCycleAsync(io, g, 0, flushPres, to, maxSec, $"{head} · 惰性置换 {i}/{flush}", "放空后置换");
            if (flush > 0) p = await io.ReadPressureAsync();
            await g.ApplyAsync(ValveFrame.AllClosed());
        }
        io.Log("info", $"RV{rv} 已放空到 {Units.FormatP(p)}", flush > 0 ? $"之后惰性置换 {flush} 次（{Units.FormatP(flushPres)}）· 阀全关" : "不置换 · 阀全关");
    }

    // ============ stir 搅拌（不阻塞）============

    public static void Stir(IStepIo io, StepSnapshot s, string head)
    {
        int rv = io.Rv;
        int rpm = (int)Math.Round(s.Num("rpm", 400));
        if (rpm > 0)
        {
            int before = io.StirRpm;
            var others = io.StirShare.Holders.Where(h => h != rv).ToList();
            if (others.Count > 0 && io.StirOn && before != rpm)
                io.Log("warn", $"RV{rv} 改了共用搅拌", $"RV{rv} 把共用搅拌从 {before} 改为 {rpm} rpm（{string.Join("、", others.Select(x => "RV" + x))} 的程序也在用）");
            io.StirShare.Request(rv, rpm);
            io.StirRpm = rpm;
            if (!io.StirOn) io.StirOn = true;
            io.Log("info", $"RV{rv} 程序 · 搅拌", $"{rpm} rpm（全机共用一台电机）· 保持到下一个搅拌或程序结束");
            io.Status($"{head} · {rpm} rpm");
        }
        else
        {
            // 「搅拌 0」：释放这一路的持有（最后一个释放者才停）；这一路没持有、也没有别的程序持有时直接停（同旧的「搅拌停止」）
            if (io.StirShare.Holds(rv)) io.ReleaseStir();
            else if (io.StirShare.Count == 0 && io.StirOn) io.StirOn = false;
            string text = io.StirShare.Current is { } keep ? $"搅拌停止请求 · RV{keep.Rv} 程序仍在搅拌，保持 {keep.Rpm} rpm" : "搅拌停止";
            io.Log("info", $"RV{rv} 程序 · 搅拌", text);
            io.Status($"{head} · 停止");
        }
    }

    // ============ heat 升温 ============

    public static async Task HeatAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        if (io.HeatBlockReason() is { } why) throw new StepFaultException($"升温：{why}");
        double band = s.Num("band", 2), maxWait = s.Num("maxWait", 120);
        bool stop = s.Str("onTimeout", "stop") != "continue";
        if (!s.IsCurve)
        {
            double temp = s.Num("temp", 80), rate = s.Num("rate", 0), wait = s.Num("wait", 15);
            var t0 = io.Now;   // maxWait 从步骤开始算
            io.Log("info", $"RV{rv} 程序 · 升温（定值）",
                $"目标 {StepSpecs.N(temp)}℃ ± {StepSpecs.N(band)}℃" + (rate > 0 ? $" · {StepSpecs.N(rate)}℃/min" : " · 防过冲逼近") + $" · 到位后平衡 {StepSpecs.N(wait)} min");
            // rate > 0：设定值从当前 PV 按速率推进到目标（等价于单段曲线）；起点已在目标附近（没有有效段）时按定值启动
            io.SetState(ReactorState.Heating);
            if (rate <= 0 || !await io.RunCurveAsync(new[] { (temp, rate, 0.0) }, head, ct))
                await io.StartFixedHeatAsync(temp);
            if (!await WaitBandAsync(io, temp, band, t0, maxWait, head, ct))
            {
                TimedOut(io, stop, $"RV{rv} 升温 {StepSpecs.N(maxWait)} min 未到 {StepSpecs.N(temp)} ± {StepSpecs.N(band)} °C（当前 {Pv(io)}）");
                return;
            }
            t0 = io.Now;
            while ((io.Now - t0).TotalMinutes < wait)
            {
                io.Status($"{head} · 已到 {StepSpecs.N(temp)}℃ · 平衡剩 {Left(io, t0, wait)}");
                await io.DelayAsync(1, ct);
            }
            return;
        }

        if (s.Segments.Count == 0) throw new StepFaultException("升温曲线为空");
        double final = s.Segments[^1].Temp;
        io.Log("info", $"RV{rv} 程序 · 升温（曲线）", $"{s.Segments.Count} 段 · 终温 {StepSpecs.N(final)}℃ ± {StepSpecs.N(band)}℃");
        io.SetState(ReactorState.Heating);
        if (!await io.RunCurveAsync(s.Segments, head, ct)) await io.StartFixedHeatAsync(final);
        // 曲线跑完后同样等到位；maxWait 从曲线结束开始算，到位后不再额外平衡（曲线自己的保温就是平衡）
        if (!await WaitBandAsync(io, final, band, io.Now, maxWait, head, ct))
            TimedOut(io, stop, $"RV{rv} 升温 {StepSpecs.N(maxWait)} min 未到 {StepSpecs.N(final)} ± {StepSpecs.N(band)} °C（当前 {Pv(io)}）");
    }

    /// <summary>等 |PV − target| ≤ band；到 maxWait（从 since 起）返回 false。温控离线 ≥ 60 s 是故障（同现在）。</summary>
    private static async Task<bool> WaitBandAsync(IStepIo io, double target, double band, TimeSpan since, double maxWaitMin, string head, CancellationToken ct)
    {
        TimeSpan? offline = null;
        while (true)
        {
            if (io.Temp is double t)
            {
                offline = null;
                if (Math.Abs(t - target) <= band) return true;
            }
            else
            {
                offline ??= io.Now;
                if ((io.Now - offline.Value).TotalSeconds >= OfflineSec) throw new StepFaultException("温控离线超过 60s，升温中止");
            }
            if ((io.Now - since).TotalMinutes >= maxWaitMin) return false;
            string ap = io.ApproachText;
            io.Status($"{head} · 升温 {Pv(io)} → {StepSpecs.N(target)}℃" + (ap.Length > 0 ? $" · {ap}" : ""));
            await io.DelayAsync(2, ct);
        }
    }

    /// <summary>超时：stop = 故障（停止这一路）；continue = 记 warn，进入下一步。</summary>
    private static void TimedOut(IStepIo io, bool stop, string msg)
    {
        if (stop) throw new StepFaultException(msg);
        io.Log("warn", $"RV{io.Rv} 程序 · 超时继续", msg + " · 按设置继续下一步");
    }

    // ============ press 充压 ============

    public static async Task PressAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        string gasCode = s.Str("gas", "A");
        string gasName = StepSpecs.GasLabel("press", gasCode);
        int gi = StepSpecs.GasIndex(gasCode);
        double target = s.Num("pres", 290.1), tol = s.Num("tol", 2), refill = s.Num("refill", 3), maxSec = s.Num("maxSec", 180);
        bool regulate = s.Str("regulate", "on") != "off";
        if (io.GasBlockReason(needPressure: true) is { } why) throw new StepFaultException($"充压：{why}");
        io.SetPressureSp(target);
        io.SetState(ReactorState.Pressing);
        io.Log("info", $"RV{rv} 程序 · 充压", $"{gasName} 到 {Units.FormatP(target)} · 偏差 {StepSpecs.N(tol)}% · 稳压{(regulate ? $"开（补气阈值 {StepSpecs.N(refill)}%）" : "关")}");
        io.Status(head);
        double now = await io.ReadPressureAsync();
        if (now >= target)
        {
            // 只能升、不能降
            io.Log("info", $"RV{rv} 已高于目标 {Units.FormatP(target)}，不放气", $"当前 {Units.FormatP(now)}");
        }
        else
        {
            await using var g = await io.AcquireGasAsync($"RV{rv} 程序 · 充压", vessel: true, ct);
            double p = await FillToAsync(io, g, gi, target, tol, maxSec, head);
            ct.ThrowIfCancellationRequested();
            io.Log("info", $"RV{rv} 程序 · 充压完成", $"{gasName} 到 {Units.FormatP(p)}（目标 {Units.FormatP(target)}）");
        }
        // 吸气量：充压完成后开始计量（遇到下一次换气停止）
        io.StartUptake($"第 {s.Ordinal:00} 步充压完成");
        if (regulate) io.StartRegulation(gi, target, refill);
    }

    /// <summary>
    /// 单通道自适应脉冲充压：首轮 0.2 s 试探，之后按实测增益（psi / 开阀秒）补 60% 差值，关阀静置 1 s 再读，从下方逼近不超调。
    /// 到 目标 − max(0.5, 目标 × tol%) 以上算到位；超过 maxSec 或连续 5 次几乎没涨按「充不上去」故障。返回最后的读数。
    /// </summary>
    public static async Task<double> FillToAsync(IStepIo io, IStepGas g, int gi, double target, double tolPct, double maxSec, string head)
    {
        int rv = io.Rv;
        double tol = Math.Max(0.5, target * tolPct / 100.0);
        double? gain = null;
        int stall = 0;
        var t0 = io.Now;
        while (true)
        {
            g.ThrowIfUnsafe();
            double p0 = await io.ReadPressureAsync();
            if (p0 >= target - tol) return p0;
            if ((io.Now - t0).TotalSeconds > maxSec)
                throw new GasFaultException($"RV{rv} 充压 {StepSpecs.N(maxSec)} s 未到 {Units.FormatP(target)}（当前 {Units.FormatP(p0)}，气源压力不够或减压阀没开）");
            double sec = gain is > 0.05 ? Math.Clamp(0.6 * (target - p0) / gain.Value, 0.1, 2.0) : 0.2;
            var frame = ValveFrame.Gas(gi, rv);
            await g.ApplyAsync(frame);
            await g.HoldAsync(frame, sec);
            await g.ApplyAsync(ValveFrame.AllClosed());
            await g.HoldAsync(ValveFrame.AllClosed(), 1.0);   // 静置再读，避开充气瞬间的动态压力
            double p1 = await io.ReadPressureAsync();
            gain = Math.Max(0.01, (p1 - p0) / sec);
            stall = p1 - p0 < 0.3 ? stall + 1 : 0;
            if (stall >= 5) throw new GasFaultException($"RV{rv} 充压无响应（气源压力不足或管路不通）");
            io.Status($"{head} · 充压 {Units.FormatP(p1)} / {Units.FormatP(target)}");
        }
    }

    // ============ hold 保持 ============

    public static async Task HoldAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        string until = s.Str("until", "time");
        io.SetState(ReactorState.Wait);
        var t0 = io.Now;
        if (until == "time")
        {
            double time = s.Num("time", 10);
            while ((io.Now - t0).TotalMinutes < time)
            {
                io.Status($"{head} · 保持 剩 {Left(io, t0, time)}");
                await io.DelayAsync(1, ct);
            }
            io.Log("info", $"RV{rv} 程序 · 保持完成", $"时间 {StepSpecs.N(time)} min");
            return;
        }
        bool temp = until is "tempAbove" or "tempBelow", above = until is "tempAbove" or "pressAbove";
        double target = temp ? s.Num("temp", 40) : s.Num("pres", 14.5);
        double maxWait = s.Num("maxWait", 60);
        bool stop = s.Str("onTimeout", "continue") == "stop";
        string cond = temp ? $"温度 {(above ? "≥" : "≤")} {StepSpecs.N(target)}℃" : $"压力 {(above ? "≥" : "≤")} {Units.FormatP(target)}";
        TimeSpan? offline = null;
        while (true)
        {
            double? v = temp ? io.Temp : io.Pressure;
            if (v is double x)
            {
                offline = null;
                if (above ? x >= target : x <= target)
                {
                    io.Log("info", $"RV{rv} 程序 · 保持完成", $"{cond} · 当前 {Fmt(temp, x)} · 用时 {(io.Now - t0).TotalMinutes:0.#} min");
                    return;
                }
            }
            else
            {
                offline ??= io.Now;
                // 传感器离线不再悄悄改成按时间等：离线超过 60 s 按超时处理
                if ((io.Now - offline.Value).TotalSeconds >= OfflineSec)
                {
                    TimedOut(io, stop, $"RV{rv} 保持：{(temp ? "温控" : "压力采集")}离线超过 60 s，无法判断「{cond}」");
                    return;
                }
            }
            if ((io.Now - t0).TotalMinutes >= maxWait)
            {
                TimedOut(io, stop, $"RV{rv} 保持 {StepSpecs.N(maxWait)} min 未达到「{cond}」（当前 {(v is double c ? Fmt(temp, c) : "—")}）");
                return;
            }
            io.Status($"{head} · 等 {cond} · 当前 {(v is double y ? Fmt(temp, y) : "—")} · 剩 {Left(io, t0, maxWait)}");
            await io.DelayAsync(1, ct);
        }
    }

    // ============ react 反应 ============

    public static async Task ReactAsync(IStepIo io, StepSnapshot s, string head, CancellationToken ct)
    {
        int rv = io.Rv;
        double time = s.Num("time", 480), minTime = s.Num("minTime", 30), mmol = s.Num("mmol", 5);
        double stallRate = s.Num("stallRate", 0.005), stallMin = s.Num("stallMin", 30);
        bool mmolOn = s.Str("mmolOn", "off") == "on", stallOn = s.Str("stallOn", "on") == "on";
        bool metered = io.UptakeCounting;
        bool useMmol = mmolOn && metered, useStall = stallOn && metered;
        if ((mmolOn || stallOn) && !metered)
            io.Log("warn", $"RV{rv} 程序 · 反应", $"吸气量未计量（反应前没有充压），只按最长时间 {StepSpecs.N(time)} min 结束");
        io.SetState(ReactorState.React);
        var t0 = io.Now;
        TimeSpan? lowSince = null;
        string reason;
        while (true)
        {
            double el = (io.Now - t0).TotalMinutes;
            if (el >= time) { reason = "最长时间到"; break; }
            if (el >= minTime)
            {
                if (useMmol && io.UptakeValid && io.UptakeMmol >= mmol) { reason = $"吸气量达到 {mmol:0.00} mmol"; break; }
                if (useStall)
                {
                    // 连续 stallMin 分钟 GasRate < stallRate 才算吸气停止；GasRate 为 null（数据不够）的时段不算，计时重新开始
                    if (io.GasRate is double r && r < stallRate)
                    {
                        lowSince ??= io.Now;
                        if ((io.Now - lowSince.Value).TotalMinutes >= stallMin)
                        {
                            reason = $"吸气停止（{StepSpecs.N(stallMin)} min < {StepSpecs.N(stallRate)} mmol/min）";
                            break;
                        }
                    }
                    else lowSince = null;
                }
            }
            string gas = io.UptakeValid ? $" · 吸气 {io.UptakeMmol:0.00}{(useMmol ? $" / {StepSpecs.N(mmol)}" : "")} mmol" : "";
            string rate = io.GasRate is double gr ? $" · {gr:0.000} mmol/min" : "";
            io.Status($"{head} · 反应中 · 剩 {Left(io, t0, time)}{gas}{rate}");
            await io.DelayAsync(2, ct);
        }
        io.EndReason = reason;
        io.Log("info", $"RV{rv} 反应结束 · {reason}",
            $"反应 {(io.Now - t0).TotalMinutes:0.#} min" + (io.UptakeValid ? $" · 吸气 {io.UptakeMmol:0.00} mmol" : " · 吸气量未计量"));
    }

    // ============ end 结束 ============

    public static async Task EndAsync(IStepIo io, StepSnapshot s, string head)
    {
        int rv = io.Rv;
        io.Status($"{head} · 收尾");
        await io.StopHeatAsync($"RV{rv} 程序 · 结束步骤停加热");
        if (io.StirShare.Holds(rv)) io.ReleaseStir();   // 别的路还在用就继续转；本程序没搅拌过不碰（操作员手动开的搅拌）
        await io.CloseOwnValveAsync();
        io.SetState(ReactorState.Done);
        io.Log("info", $"RV{rv} 程序 · 结束", "关加热、阀全关、释放搅拌、停止计量" + (io.EndReason is { } r ? $" · 结束原因：{r}" : ""));
    }

    // ============ 小件 ============

    private static string Left(IStepIo io, TimeSpan t0, double capMin) => $"{Math.Max(0, capMin - (io.Now - t0).TotalMinutes):0.#} 分";
    private static string Pv(IStepIo io) => io.Temp is double t ? $"{t:0.0}℃" : "—";
    private static string Fmt(bool temp, double v) => temp ? $"{v:0.0}℃" : Units.FormatP(v);
}
