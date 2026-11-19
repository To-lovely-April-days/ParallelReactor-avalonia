using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.Services;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// 把 MainViewModel 的当前状态拍成不可变的 <see cref="SnapshotBundle"/>（设计稿 §6.3）。
/// <b>只能在 UI 线程、Tick 结束后调用</b>（ControlLoop.RunTick 的 finally 里）。读取规则与 MainViewModel.BuildTelemetryRow 一致：
/// 温控 / 压力模块离线时读数为 null；压力取 <c>_press.Last</c> 的不可变样本（线程池更新，不能直接引用数组）。
/// 放在 Api/ 而不是 ViewModels/ 下：只读，不在架构测试 R6 的扫描范围内。
/// </summary>
public static class SnapshotBuilder
{
    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string Part(EstopPart p) => p switch
    {
        EstopPart.Ok => "ok",
        EstopPart.Failed => "failed",
        EstopPart.NotConfigured => "notConfigured",
        _ => "pending",
    };

    private static double? Fin(double v) => double.IsFinite(v) ? v : null;

    private static ChannelProgramSnap? ChannelProgram(ProgramRunInfo? ri) => ri == null ? null
        : new ChannelProgramSnap(ri.IsActive, ri.Index, ri.StepCount, ProgramRunSet.PhaseName(ri.Phase), ri.Message,
            ri.StartedBy, ri.StartedAt.UtcDateTime, ri.WaitingGas);


    /// <summary>冻结步骤 → §6.9 JSON 的缓存：键是运行对象的步骤列表（每次运行一份、不可变），每次运行只转换一次。</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<StepSnapshot>, ProgramStepDto[]> StepsJson = new();

    /// <summary>GET /channels/{id}/program/run 的记录：动态字段每 Tick 取自 ProgramRunInfo，步骤取缓存。</summary>
    private static ProgramRunSnap? RunRecord(int id, (ProgramRunInfo Info, IReadOnlyList<StepSnapshot> Steps)? rec)
    {
        if (rec is not { } r) return null;
        var ri = r.Info;
        var steps = StepsJson.GetValue(r.Steps, s => ProgramStepFormat.Format(s));
        return new ProgramRunSnap(id, ri.IsActive, ProgramRunSet.PhaseName(ri.Phase), ri.Index, ri.StepCount, ri.PhaseText, ri.Message,
            ri.StartedBy, ri.StartedAt.UtcDateTime, ri.WaitingGas, steps, ri.EndReason);
    }

    private static ModuleCommSnap Module(bool configured, bool online, CommStats s, DateTimeOffset? valuesAt = null)
        => new(configured, online, s.Polls, s.Fails, s.LastOkAt?.UtcDateTime, valuesAt?.UtcDateTime);

    public static SnapshotBundle Build(MainViewModel vm, long seq, TickGate gate, int[] installed)
    {
        var now = DateTimeOffset.Now;
        bool realT = vm.Temp.Online;
        bool realP = vm._press.Online;
        var ps = vm._press.Last;
        bool pValid = realP && ps.At != DateTimeOffset.MinValue;
        var runInfos = vm.Program.RunInfos;

        var full = new ChannelSnap[8];
        var frameCh = new ChannelSnap[8];
        var runs = new ProgramRunSnap?[8];
        for (int i = 0; i < 8 && i < vm.Reactors.Count; i++)
        {
            var c = vm.Reactors[i];
            var tc = vm.Temp.Channels[i];
            bool tOnline = realT && tc.Online;
            var a = c.Approach;
            var pr = c.Profile;
            var profile = new ProfileSnap(pr.Running, pr.SegCount,
                pr.Segments.Select(s => new ProfileSegmentSnap(s.Index, s.Temp, s.Minutes, s.Enabled, s.Active)).ToArray(),
                Math.Round(pr.ElapsedMin, 2), pr.TotalMinutes, pr.IsComplete, pr.StatusText);
            var ch = new ChannelSnap(
                Id: c.Id,
                Installed: Array.IndexOf(installed, c.Id) >= 0,
                State: Lower(c.State.ToString()),
                AlarmCause: c.State == ReactorState.Alarm ? (c.OverTemp ? "overTemp" : "overPressure") : null,
                OverTempTripped: vm.IsOverTempTripped(c.Id),
                OverPressureTripped: vm.IsOverPressureTripped(c.Id),
                StartedBy: c.State == ReactorState.Idle ? null : c.StartedBy,   // §5.4：local / 远程（客户端名）；停用为 null
                TempC: tOnline ? Fin(tc.Pv) : null,
                TempSpC: c.TSp,
                TempSpDeviceC: tc.SpKnown ? Fin(tc.Sp) : null,
                HeatOutputPct: tOnline ? Fin(tc.OutputPct) : null,
                TempOnline: tOnline,
                Autotuning: tc.Autotuning,
                AtMode: tOnline && tc.Mode >= 0 ? tc.Mode : null,
                SpMode: c.SpMode,
                Approach: new ApproachSnap(Lower(a.Phase.ToString()), a.Active ? Fin(a.Target) : null, a.Active ? Fin(a.CurrentSp) : null, a.StatusText),
                Profile: profile,
                PressurePsi: pValid && i < ps.Psi.Length ? Fin(ps.Psi[i]) : null,
                PressureRaw: pValid && i < ps.Raw.Length ? ps.Raw[i] : null,
                PressureSpPsi: c.PSp,
                GasConsumed: c.GasValid ? Fin(c.Gas) : null,
                ValveOpen: c.Valve,
                ProgramOwned: ProgramRunSet.IsOwned(runInfos, c.Id),
                Program: ChannelProgram(vm.Program.RunInfoOf(c.Id)),
                GasRate: c.GasRate is double rate ? Fin(rate) : null,
                SolutionVolume: c.Vol);
            full[i] = ch;
            runs[i] = RunRecord(c.Id, vm.Program.RunRecordOf(c.Id));
            frameCh[i] = c.SpMode == "curve" ? ch : ch with { Profile = null };
        }

        var estop = new EstopSnap(vm.Estopped, Part(vm.EstopHeat.Part), Part(vm.EstopStir.Part), Part(vm.EstopValves.Part),
            vm.EstopAt?.UtcDateTime, vm.EstopLatched, vm.EstopRestoredOnBoot);
        var stir = new StirSnap(vm.StirOn, vm.StirRpm, vm._stir.Online, vm._stir.Locked,
            vm.StirMotorCurrent > 0 ? vm.StirMotorCurrent : null, vm.StirBusVoltage > 0 ? vm.StirBusVoltage : null,
            string.IsNullOrEmpty(vm.StirFaultText) ? null : vm.StirFaultText, vm._stirAlarmCode, vm._stir.RampMs);
        var gasIds = new[] { "inert", "A", "B" };
        var gas = new GasSnap(
            Enumerable.Range(0, Math.Min(3, vm.GasInlets.Count)).Select(g => new GasInletSnap(gasIds[g], vm.GasInlets[g].On)).ToArray(),
            vm.VentOn, vm.ManualValve,
            vm.GasBusy ? vm.GasTask : null,
            vm.GasOwnedBy(GasOwner.Program) ? "program" : vm.GasOwnedBy(GasOwner.Leak) ? "leak" : vm.GasOwnedBy(GasOwner.Calib) ? "calib" : null);
        // 旧字段 program（已废弃，过渡一个版本）：首个正在执行的程序；没有在执行的则是最近结束的那次（与 v0.4 一样保留终态文字 / 最后步序）；
        // 新字段 channels[i].program + programs
        var first = ProgramRunSet.FirstRunning(runInfos) ?? vm.Program.LastEndedRunInfo;
        var program = first != null
            ? new ProgramSnap(first.IsActive, first.IsActive ? first.Rv : null, first.Status, first.Index, first.StepCount, first.PhaseText, first.Message)
            : new ProgramSnap(false, null, "", -1, 0, "准备运行", "等待开始");
        // waitingGas 只列步骤真正处于「等待气路」的程序（稳压脉冲的静默排队不算），与 channels[i].program.waitingGas 一致；顺序按队列
        var waiting = vm.GasQueue.Waiting.Where(rv => runInfos.TryGetValue(rv, out var w) && w.WaitingGas).ToArray();
        var programs = new ProgramsSnap(runInfos.Count, ProgramRunSet.RunningIds(runInfos).ToArray(), vm.GasHolderRv,
            waiting, ProgramRunSet.Summary(runInfos));
        // v0.8.1 设备活动同步：泄漏测试的阶段码 / 剩余时间 / 发起人 / 8 路状态，结果保持到下一次开始；容积标定（没有为 null）
        var leak = vm.Leak.Activity.ToSnap(now, vm.Leak.Running, vm.Leak.Progress, vm.Leak.PhaseText, vm.Leak.CurrentRunId, vm.Leak.ActivityCells);
        var calib = vm.Calib.Activity.ToSnap(vm.Calib.Running, vm.Calib.Progress, vm.Calib.PhaseText, vm.Calib.WaterMl);
        // id = 会话文件主名（唯一键，接口的会话 id）；testId = 调用方给的测试编号（CSV「会话」列）
        var sid = vm.Logger.SessionId;
        var session = new SessionSnap(sid != null, vm.Logger.SessionKey, sid, vm.Logger.SessionPath is { } sp ? System.IO.Path.GetFileName(sp) : null);
        var mode = new ModeSnap(vm.Arbiter.Mode == ControlMode.Remote ? "remote" : "local",
            vm.Arbiter.Mode == ControlMode.Remote ? vm.Arbiter.HolderName : null);

        var frame = new TelemetrySnapshot("telemetry", seq, now.UtcDateTime, HardwareOptions.Sim, vm.Logger.Events?.Head ?? 0,
            mode, estop, new CommSnap(realT, realP, vm._stir.Online, vm._valves.Online), frameCh, stir, gas, program, programs, leak, session, calib);

        var st = vm.Settings;
        var bands = vm.Temp.Bands.Select((b, i) => new TempBandSnap(i, b.Lo, b.Hi)).ToArray();
        var settings = new SettingsSnap(st.TMaxSp, st.PMaxSp, st.RpmMax, st.VolMax,
            st.OverPressure, st.OverTemp, st.StopHeatOnOverPressure, st.HeatTimeout, st.PressDeviation,
            st.LeakTestPsi, st.LeakSettleMin, st.LeakMeasureMin, st.LeakRate, st.LeakReminderDays,
            st.LastLeakTestUtc is { } lt ? DateTime.SpecifyKind(lt, DateTimeKind.Utc) : null,
            st.ApproachDelta, st.ApproachRate, st.ApproachFastRate, st.ApproachFinalZone, st.ApproachSettleBand, st.ApproachSettleSec,
            st.StirCurrent, st.StirStandbyPct, st.StirRampMs, st.StirMicrostep, st.PressFullScaleMPa, bands, vm.SettingsRevision,
            new VolumeSettingsSnap(st.VesselVolume, st.GasLineVolume, st.ManifoldVolume, Eight(st.EffectiveVolume),
                Eight(st.VolumeCalibUtc).Select(t => t is { } u ? (DateTime?)DateTime.SpecifyKind(u, DateTimeKind.Utc) : null).ToArray()),
            st.StirGearRatio);

        var tick = new TickStats(gate.Interval.TotalMilliseconds, gate.Entered, gate.Skipped, gate.Overruns,
            Math.Round(gate.LastTook.TotalMilliseconds, 1), Math.Round(gate.MaxTook.TotalMilliseconds, 1), Math.Round(gate.LastOverrun.TotalMilliseconds, 1));

        return new SnapshotBundle(frame, full, settings, tick,
            Module(HardwareOptions.UseRealTemp, vm.Temp.Online, vm.Temp.Stats, vm.Temp.ValuesAt),
            Module(HardwareOptions.UseRealAnalog, vm._press.Online, vm._press.Stats, pValid ? ps.At : null),
            Module(HardwareOptions.UseRealStir, vm._stir.Online, vm._stir.Stats),
            Module(HardwareOptions.UseRealIo, vm._valves.Online, vm._valves.Stats),
            vm.Logger.DroppedLines,
            runs,
            Pid(vm.Temp));
    }

    /// <summary>
    /// GET /pid 的快照（v0.6）：档位定义 + 每通道各档 PID 与来历、自整定状态。显示档取界面工作副本（<see cref="TempService.GetPid"/>），
    /// 来历是不可变 record。每 Tick 8 × 3 项，开销可忽略；PID 不进 WS 帧。
    /// </summary>
    private static PidSnap Pid(TempService t)
    {
        var bands = t.Bands.Select((b, i) => new PidBandSnap(i, b.Name, b.Lo, b.Hi)).ToArray();
        var chs = new PidChannelSnap[t.Channels.Count];
        for (int i = 0; i < chs.Length; i++)
        {
            int id = i + 1;
            var vals = new PidBandValueSnap[bands.Length];
            for (int b = 0; b < vals.Length; b++)
            {
                var (p, ii, d) = t.GetPid(id, b);
                var m = t.Tune.Meta(i, b);
                vals[b] = new PidBandValueSnap(b, p, ii, d, m?.TunedSpC, m?.TunedAt, m?.ManualEditedAt);
            }
            int tb = t.Tune.TuneBand(i);
            bool tuning = t.Channels[i].Autotuning;
            chs[i] = new PidChannelSnap(id, tuning, tuning && tb >= 0 ? tb : null, tuning && tb >= 0 ? t.Tune.TuneSp(i) : null, vals);
        }
        return new PidSnap(bands, chs);
    }

    /// <summary>设置里的按通道数组 → 恰好 8 项的副本（设置文件里缺项 / 多项都按 8 通道对齐；快照不共享可变数组）。</summary>
    private static T?[] Eight<T>(T?[]? a)
    {
        var r = new T?[8];
        if (a != null) Array.Copy(a, r, Math.Min(8, a.Length));
        return r;
    }
}
