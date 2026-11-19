using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 演示数据的设备活动（v0.8.1 设备活动同步，ACT-SPEC §3.4；样稿 mock-src/act.js 的 axLeak / axCalib / axQuery）：
/// <list type="bullet">
/// <item><see cref="SeedLeak"/>：泄漏测试 RV1–4（进行中 = 14:22:47 触屏发起、平衡阶段、本阶段剩 05:20、全程剩 37:20；结束 = 13:44:10 开始、14:28 完成，
///   全部通过 / 有不通过 + 釜内带压 / 已中止）。被测的四路回到「已就绪」、读数同样稿；RV5（同时演示自整定时 RV6）的程序排队等气路；气源全关。</item>
/// <item><see cref="SeedCalib"/>：容积标定 RVn 第一遍完成、等操作员加水（已等 03:12）。</item>
/// <item><see cref="SeedStirFault"/> / <see cref="SeedManualValve"/> / <see cref="SeedEstopPart"/>：搅拌电机故障、触屏手动阀控、急停后阀门没确认关闭。</item>
/// <item><see cref="AbortLeakDemo"/>：本机「中止」（确认之后）：本次不出结果，阀门全关，釜内压力保留。</item>
/// </list>
/// 不冻结时泄漏测试每秒倒计时（<see cref="TickActivity"/>），到时间按全部通过结束。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>演示泄漏测试的测试压力 psi 与判据 psi/h（样稿 AXL）。</summary>
    public const double DemoLeakPsi = 50, DemoLeakThreshold = 0.5;

    private static readonly int[] LeakChs = { 1, 2, 3, 4 };

    private static DateTime Utc(DateTime local) => local.ToUniversalTime();

    /// <summary>
    /// 摆一次泄漏测试（state = run / done / fail / abort；src = hmi / pc；queue = 排队等气路的那一路，0 = 不排队）。
    /// </summary>
    public void SeedLeak(DemoDevice d, string state, string src, int queue = 5)
    {
        bool run = state == "run";
        d.AlarmList.RemoveAll(a => a.Ch is int n && LeakChs.Contains(n));
        var t0 = run ? DemoFusion.At(14, 22, 47) : DemoFusion.At(13, 44, 10);
        double[] p = { 49.9, 49.8, 49.9, 49.7 };
        double[] t = { 24.8, 25.1, 24.9, 25.3 };
        LeakChannelSnap Ch(int n, string st, double? psi, double? rate, string detail) => new(n, st, psi, rate, detail);
        var chs = new List<LeakChannelSnap>();
        string by = src == "hmi" ? "触屏" : "远程（" + Client + "）";
        if (run) chs.AddRange(LeakChs.Select((n, i) => Ch(n, "testing", p[i], null, "平衡中")));
        else if (state == "done") chs.AddRange(new[] { (1, .12), (2, .20), (3, .08), (4, .16) }.Select(x => Ch(x.Item1, "pass", 43.5, x.Item2, x.Item2.ToString("0.00", CultureInfo.InvariantCulture) + " psi/hr")));
        else if (state == "fail")
            chs.AddRange(new[]
            {
                Ch(1, "pass", 43.5, .12, "0.12 psi/hr"), Ch(2, "fillFail", 21.6, null, "充压不上"),
                Ch(3, "fail", 43.5, 2.04, "2.04 psi/hr"), Ch(4, "tooHigh", 43.5, null, "初始压力过高"),
            });
        else chs.AddRange(LeakChs.Select((n, i) => Ch(n, "untested", p[i] * 0.997, null, "未完成")));
        for (int n = 5; n <= 8; n++) chs.Add(Ch(n, "untested", null, null, "不测"));
        d.LeakTest = run
            ? new LeakSnap(true, 16.4, "平衡中 · 剩余 05:20", "leak-20261115-142247", false, false,
                "settle", 320, 2240, Utc(t0), by, DemoLeakPsi, DemoLeakThreshold, chs.ToArray())
            : new LeakSnap(false, state == "abort" ? 22.7 : 100, null, null, true, state is "fail" or "abort",
                "done", null, null, Utc(t0), by, DemoLeakPsi, DemoLeakThreshold, chs.ToArray(),
                Vented: state == "done" ? true : state == "fail" ? false : null, Aborted: state == "abort",
                FinishedAt: Utc(state == "abort" ? DemoFusion.At(14, 30) : DemoFusion.At(14, 28, 40)));
        for (int i = 0; i < LeakChs.Length; i++)
        {
            var c = d.Channel(LeakChs[i]);
            if (c is null) continue;
            Park(c);
            c.TempC = t[i];
            c.PressureMPa = Units.PsiToMpa(run || state == "abort" ? p[i] : state == "fail" && LeakChs[i] != 2 ? 43.5 : state == "fail" ? 21.6 : 0.6);
        }
        if (run) HoldGas(d, queue);
        Log(d, EventLevel.Info, null, "泄漏测试 · " + (src == "hmi" ? "触屏" : "本机") + "开始泄漏测试 RV1–4", t0);
        if (!run) Log(d, state == "done" ? EventLevel.Info : EventLevel.Warn, null, state switch
        {
            "done" => "泄漏测试 · 泄漏测试通过 · 4 路",
            "fail" => "泄漏测试 · 泄漏测试不通过 · RV2、RV3",
            _ => "泄漏测试 · 泄漏测试中止 · 阀门全关",
        }, state == "abort" ? DemoFusion.At(14, 30) : DemoFusion.At(14, 28, 40));
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>容积标定第 n 路：第一遍完成、等加水 5.00 mL（触屏发起，已等 03:12；两遍法的第一遍只有比值，firstPassMl = null）。</summary>
    public void SeedCalib(DemoDevice d, int n)
    {
        var c = d.Channel(n);
        if (c is null) return;
        d.AlarmList.RemoveAll(a => a.Ch == n);
        var t0 = DemoFusion.At(14, 11, 30);
        var wait = AddMs(Now, -192_000);
        d.Calib = new CalibSnap(true, n, "waitWater", true, 5.0, null, null, 100, "等待加水", Utc(t0), Utc(wait), "触屏", false, false, null);
        Park(c);
        c.TempC = 24.6;
        c.PressureMPa = 0.002;
        HoldGas(d, 0);
        Log(d, EventLevel.Info, n, "RV" + n + " 容积标定开始", t0);
        Log(d, EventLevel.Info, n, "RV" + n + " 容积标定 · 第一遍完成 · 等待加水 5.00 mL", wait);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>搅拌电机故障（驱动器报警码 0x80「锁轴/缺相（堵转）」）：搅拌停、记 err 报警（同 HMI 的标题）和事件。</summary>
    public void SeedStirFault(DemoDevice d)
    {
        var at = DemoFusion.At(14, 27, 12);
        d.Marks = d.Marks with { StirFault = "锁轴/缺相（堵转）", StirCode = 0x80 };
        if (d.Stir != null) d.Stir.On = false;
        d.AlarmList.Insert(0, NewAlarm(AlarmLevel.Err, null, "STIR", "搅拌电机故障 · 代码 0x80 · 在触屏清除", at, false));
        Log(d, EventLevel.Err, null, "搅拌 · 搅拌电机故障 · 代码 0x80", at);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>触屏手动阀控：惰性气体开、RV5 进气阀开。</summary>
    public void SeedManualValve(DemoDevice d)
    {
        d.Marks = d.Marks with { ManualValve = true };
        for (int i = 0; i < d.GasList.Count; i++) d.GasList[i] = new KeyValuePair<string, bool>(d.GasList[i].Key, d.GasList[i].Key == "inert");
        if (d.Channel(5) is { } c5) c5.Sv = true;
        Log(d, EventLevel.Op, null, "触屏 · 进入手动阀控", DemoFusion.At(14, 24, 3));
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>急停后阀门没有确认关闭（加热、搅拌已确认）：设备急停 + 分项。</summary>
    public void SeedEstopPart(DemoDevice d)
    {
        var at = DemoFusion.At(14, 29, 52);
        DoEstop(d, Client);
        d.Estop.At = at;
        d.Marks = d.Marks with { Estop = new EstopParts(at, EstopPartState.Ok, EstopPartState.Ok, EstopPartState.Failed) };
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>本机中止泄漏测试（确认之后）：本次不出结果、阀门全关、釜内压力保留。没有进行中的测试返回原因。</summary>
    public string AbortLeakDemo(DemoDevice d)
    {
        if (d.LeakTest is not { Running: true } s) return "当前没有进行中的泄漏测试";
        var chs = (s.Channels ?? Array.Empty<LeakChannelSnap>())
            .Select(c => c.Status == "testing" ? c with { Status = "untested", Detail = "未完成" } : c).ToArray();
        d.LeakTest = s with { Running = false, Done = true, Failed = true, Phase = null, Stage = "done", StageLeftSec = null, TotalLeftSec = null,
            Channels = chs, Aborted = true, Vented = null, FinishedAt = Now.ToUniversalTime() };
        foreach (var c in d.ChannelList) c.Queued = false;
        Log(d, EventLevel.Warn, null, "泄漏测试 · 泄漏测试中止 · 手动中止 · 阀门已全关");
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
        return "";
    }

    /// <summary>
    /// 本机开始泄漏测试（性能测试页「开始泄漏测试」，演示）：所选通道充压阶段起步，之后每秒倒计时（<see cref="TickActivity"/>）。
    /// </summary>
    public void StartLeakDemo(DemoDevice d, IReadOnlyList<int> chs)
    {
        var plant = d.Plant;
        double settle = plant?.Leak.SettleMin ?? 10, measure = plant?.Leak.MeasureMin ?? 30;
        var cells = d.ChannelList.Where(c => c.Installed).Select(c => chs.Contains(c.N)
            ? new LeakChannelSnap(c.N, "testing", Units.MpaToPsi(c.PressureMPa), null, "充压中")
            : new LeakChannelSnap(c.N, "untested", null, null, "不测")).ToArray();
        d.LeakTest = new LeakSnap(true, 0, "充压中", "leak-demo", false, false, "fill", null,
            ActivityText.FillSec + (settle + measure) * 60 + 15, Now.ToUniversalTime(), "远程（" + Client + "）",
            DemoLeakPsi, plant?.Leak.RatePsiH ?? DemoLeakThreshold, cells);
        foreach (var n in chs) if (d.Channel(n) is { } c) Park(c);
        Log(d, EventLevel.Info, null, "泄漏测试 · 本机开始泄漏测试 " + ActivityText.Range(chs));
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>
    /// 每秒推进：泄漏测试的剩余时间减 1 s，阶段到点换下一段（充压 160 s → 平衡 → 测量 → 泄压 15 s），做完按全部通过结束（同 HMI：结果保持到下一次开始）。
    /// </summary>
    private void TickActivity(DemoDevice d)
    {
        if (d.LeakTest is not { Running: true } s) return;
        double settle = (d.Plant?.Leak.SettleMin ?? 10) * 60, measure = (d.Plant?.Leak.MeasureMin ?? 30) * 60;
        double total = Math.Max(0, (s.TotalLeftSec ?? 0) - 1);
        double vent = 15, afterMeasure = vent, afterSettle = measure + vent, afterFill = settle + measure + vent;
        string stage = total > afterFill ? "fill" : total > afterSettle ? "settle" : total > afterMeasure ? "measure" : "vent";
        double? stageLeft = stage switch { "settle" => total - afterSettle, "measure" => total - afterMeasure, _ => null };
        double prog = stage switch
        {
            "fill" => 10 * (1 - (total - afterFill) / ActivityText.FillSec),
            "settle" => 12 + 23 * (1 - stageLeft!.Value / settle),
            "measure" => 35 + 62 * (1 - stageLeft!.Value / measure),
            _ => 97 + 3 * (1 - total / vent),
        };
        string detail = stage switch { "fill" => "充压中", "settle" => "平衡中", "measure" => "测量中", _ => "泄压中" };
        if (total <= 0)
        {
            var res = (s.Channels ?? Array.Empty<LeakChannelSnap>()).Select(c => c.Status == "testing"
                ? c with { Status = "pass", RatePsiH = 0.15, Detail = "0.15 psi/hr" } : c).ToArray();
            d.LeakTest = s with { Running = false, Done = true, Progress = 100, Phase = null, Stage = "done", StageLeftSec = null, TotalLeftSec = null,
                Channels = res, Vented = true, FinishedAt = Now.ToUniversalTime() };
            foreach (var c in d.ChannelList) c.Queued = false;
            Log(d, EventLevel.Info, null, "泄漏测试 · 泄漏测试通过");
            return;
        }
        var chs = (s.Channels ?? Array.Empty<LeakChannelSnap>()).Select(c => c.Status == "testing"
            ? c with { Detail = detail, PressurePsi = stage == "fill" ? Math.Min(DemoLeakPsi, (c.PressurePsi ?? 0) + DemoLeakPsi / 150) : c.PressurePsi } : c).ToArray();
        d.LeakTest = s with { TotalLeftSec = total, StageLeftSec = stageLeft, Stage = stage, Progress = Math.Round(Math.Clamp(prog, 0, 100), 1),
            Phase = detail + (stageLeft is double sl ? " · 剩余 " + ActivityText.Mmss(sl) : ""), Channels = chs };
    }

    /// <summary>被测 / 标定的那一路：不在跑程序、不加热、不进气（同样稿 axSet）。</summary>
    private static void Park(DemoChannel c)
    {
        c.State = StateKey.Ready;
        c.SpC = null;
        c.PTargetMPa = null;
        c.HeaterPct = 0;
        c.Heating = false;
        c.Sv = false;
        c.Mmol = null;
        c.Rate = null;
        c.Program = null;
        c.Alarm = false;
    }

    /// <summary>
    /// 测试 / 标定占用共用气路：气源全关，其余通道不进气；<paramref name="queue"/> 那一路的程序在「反应」步排队等气路（样稿 RV5：恒温反应 3/4）。
    /// </summary>
    private void HoldGas(DemoDevice d, int queue)
    {
        for (int i = 0; i < d.GasList.Count; i++) d.GasList[i] = new KeyValuePair<string, bool>(d.GasList[i].Key, false);
        foreach (var c in d.ChannelList) { c.Sv = false; c.Rate = null; }
        if (queue <= 0 || d.Channel(queue) is not { Installed: true } q) return;
        var def = d.ProgramList.FirstOrDefault(p => p.Id == "A");
        q.State = StateKey.Wait;
        q.Queued = true;
        if (q.Program is null && def != null)
        {
            q.TempC = 150.1;
            q.SpC = 150;
            q.PressureMPa = 0.12;
            q.PTargetMPa = 2.50;
            q.HeaterPct = 40;
            q.Heating = true;
            q.Program = FusionProgram(def, new DemoFusion.Ch(queue, "react", 150.1, 150, .12, 2.5, 400, 40, 3, 0, 45, 0, 0), Now);
        }
    }
}
