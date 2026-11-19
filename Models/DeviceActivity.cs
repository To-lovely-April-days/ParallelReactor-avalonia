using System;
using System.Collections.Generic;
using ParallelReactor.Api;

namespace ParallelReactor.Models;

// ============================================================================
// 设备活动同步（v0.8.1，research/fusion-port/notes/ACT-SPEC.md）：触屏上的泄漏测试 / 容积标定，PC 要同步显示。
// 这里只记「快照要用、但面板里没有」的状态（阶段码、阶段结束时刻、发起人、结果），并把它们拍成线上记录；
// 纯逻辑、时间由调用方传入（测试工程直接链接本文件）。LeakViewModel / CalibViewModel 在流程的各个节点调用，
// 不改触屏的行为；SnapshotBuilder 每 Tick 调 ToSnap。
// ============================================================================

/// <summary>泄漏测试阶段码（快照 leakTest.stage）。PC 按它分阶段，不解析中文的 phase。</summary>
public static class LeakStageCodes
{
    public const string Prep = "prep";          // 关阀、读初始压力
    public const string Fill = "fill";          // 脉冲充压
    public const string LineVent = "lineVent";  // 总管泄压
    public const string Settle = "settle";      // 平衡
    public const string Measure = "measure";    // 测量
    public const string Vent = "vent";          // 判读 + 结束泄压
    public const string Done = "done";          // 结束（通过 / 不通过 / 中止 / 失败）

    /// <summary>运行中的阶段顺序（估算总剩余时间用）。</summary>
    public static readonly string[] Order = { Prep, Fill, LineVent, Settle, Measure, Vent };
}

/// <summary>泄漏测试单路状态码（快照 leakTest.channels[i].status）。</summary>
public static class LeakChannelCodes
{
    public const string Testing = "testing";
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string FillFail = "fillFail";
    public const string TooHigh = "tooHigh";
    public const string Untested = "untested";
}

/// <summary>
/// 一次泄漏测试的进行状态与结果（给快照用）。<see cref="Begin"/> 时清空上一次；结束后的结果保持到下一次开始
/// （触屏重新打开面板会重建格子、清掉 Done，但这里不清）。
/// </summary>
public sealed class LeakActivity
{
    // 没有固定时长的阶段按经验估：关阀读压约 0.5 s 死区 + 一次读压；充压通常 1–2 min（超时 180 s）；
    // 总管泄压 = 0.5 s + 0.5 s + 3 s 排空 + 写阀；结束泄压 = 读压 + 6 s 排空 + 0.5 s + 读压
    public const double PrepEstSec = 10;
    public const double FillEstSec = 120;
    public const double FillMinLeftSec = 10;   // 充压超过估计时长仍未完成：剩余至少按 10 s 报，不报 0
    public const double LineVentEstSec = 10;
    public const double VentEstSec = 15;

    private readonly HashSet<int> _picked = new();
    private readonly double?[] _rates = new double?[9];   // 下标 = 通道号 1..8

    /// <summary>当前阶段码；本次开机还没做过泄漏测试为 null。</summary>
    public string? Stage { get; private set; }
    public DateTimeOffset StageStartedAt { get; private set; }
    /// <summary>本阶段预计结束时刻（只有平衡、测量有）。</summary>
    public DateTimeOffset? StageEndsAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string? StartedBy { get; private set; }
    public double TargetPsi { get; private set; }
    public double ThresholdPsiH { get; private set; }
    public double SettleSec { get; private set; }
    public double MeasureSec { get; private set; }
    public bool? Vented { get; private set; }
    public bool Aborted { get; private set; }
    public string? FailReason { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    /// <summary>结果红色（不通过 / 中止 / 失败），同 LeakViewModel.Failed，结束时取一次。</summary>
    public bool Failed { get; private set; }
    /// <summary>结束时的进度（触屏重新打开面板会把进度清 0，快照仍报这个）。</summary>
    public double FinalProgress { get; private set; }

    /// <summary>正在进行（已开始、未结束）。</summary>
    public bool Active => Stage is not null && Stage != LeakStageCodes.Done;
    public bool Finished => Stage == LeakStageCodes.Done;

    public bool Picked(int id) => _picked.Contains(id);
    public double? Rate(int id) => id >= 1 && id <= 8 ? _rates[id] : null;

    /// <summary>开始一次测试：清空上一次的结果，进入 prep。</summary>
    public void Begin(DateTimeOffset now, string startedBy, IEnumerable<int> ids, double targetPsi, double thresholdPsiH, double settleMin, double measureMin)
    {
        _picked.Clear();
        foreach (int id in ids) _picked.Add(id);
        Array.Clear(_rates);
        StartedAt = now;
        StartedBy = startedBy;
        TargetPsi = targetPsi;
        ThresholdPsiH = thresholdPsiH;
        SettleSec = Math.Max(0, settleMin * 60);
        MeasureSec = Math.Max(0, measureMin * 60);
        Vented = null;
        Aborted = false;
        FailReason = null;
        FinishedAt = null;
        Failed = false;
        FinalProgress = 0;
        Enter(LeakStageCodes.Prep, now);
    }

    /// <summary>进入一个阶段；durationSec 给出时（平衡、测量）记下预计结束时刻。</summary>
    public void Enter(string stage, DateTimeOffset now, double? durationSec = null)
    {
        Stage = stage;
        StageStartedAt = now;
        StageEndsAt = durationSec is double d ? now.AddSeconds(Math.Max(0, d)) : null;
    }

    /// <summary>一路的压降速率 psi/hr（测量阶段起；即格子上「≈ x psi/hr」的那个数）。</summary>
    public void SetRate(int id, double? psiPerH)
    {
        if (id >= 1 && id <= 8) _rates[id] = psiPerH is double r && double.IsFinite(r) ? r : null;
    }

    /// <summary>结束（通过 / 不通过 / 中止 / 失败都走这里）。vented = null：没做到结束泄压那一步。</summary>
    public void End(DateTimeOffset now, bool? vented, bool aborted, string? fail, bool failed, double progress)
    {
        Enter(LeakStageCodes.Done, now);
        Vented = vented;
        Aborted = aborted;
        FailReason = fail;
        FinishedAt = now;
        Failed = failed;
        FinalProgress = progress;
    }

    /// <summary>本阶段剩余秒数：只有平衡、测量有（其余 null）。</summary>
    public double? StageLeftSec(DateTimeOffset now)
        => Active && StageEndsAt is { } e ? Math.Round(Math.Max(0, (e - now).TotalSeconds)) : null;

    /// <summary>
    /// 整个测试的估算剩余秒数：当前阶段剩余 + 之后各阶段的时长（平衡 / 测量按设置，其余按经验值）。没在进行为 null。
    /// </summary>
    public double? TotalLeftSec(DateTimeOffset now)
    {
        if (!Active || Stage is not { } stage) return null;
        int idx = Array.IndexOf(LeakStageCodes.Order, stage);
        if (idx < 0) return null;
        double el = Math.Max(0, (now - StageStartedAt).TotalSeconds);
        double left = stage switch
        {
            LeakStageCodes.Fill => Math.Max(FillMinLeftSec, FillEstSec - el),
            LeakStageCodes.Settle or LeakStageCodes.Measure => StageLeftSec(now) ?? 0,
            _ => Math.Max(0, EstSec(stage) - el),
        };
        for (int i = idx + 1; i < LeakStageCodes.Order.Length; i++) left += EstSec(LeakStageCodes.Order[i]);
        return Math.Round(left);
    }

    private double EstSec(string stage) => stage switch
    {
        LeakStageCodes.Prep => PrepEstSec,
        LeakStageCodes.Fill => FillEstSec,
        LeakStageCodes.LineVent => LineVentEstSec,
        LeakStageCodes.Settle => SettleSec,
        LeakStageCodes.Measure => MeasureSec,
        LeakStageCodes.Vent => VentEstSec,
        _ => 0,
    };

    /// <summary>
    /// 触屏格子（Status：""/ok/bad/skip + Detail 文字）→ 状态码。picked = 参加了本次测试；finished = 测试已结束。
    /// 中止 / 失败时没出结果的格子（触屏写「未完成」）报 untested：本次不出结果。
    /// </summary>
    public static string StatusOf(bool picked, bool finished, string status, string detail)
    {
        if (!picked) return LeakChannelCodes.Untested;
        return status switch
        {
            "ok" => LeakChannelCodes.Pass,
            "bad" => detail is "充压不上" or "充压超时" ? LeakChannelCodes.FillFail : LeakChannelCodes.Fail,
            "skip" => detail == "初始压力过高" ? LeakChannelCodes.TooHigh : LeakChannelCodes.Untested,
            _ => finished ? LeakChannelCodes.Untested : LeakChannelCodes.Testing,
        };
    }

    /// <summary>8 路的状态（按通道号 1..8；cells 里没有的通道按没参加）。还没做过测试为 null。</summary>
    public LeakChannelSnap[]? Channels(IEnumerable<(int Id, string Status, string Detail, double Pressure)> cells)
    {
        if (Stage is null) return null;
        var byId = new Dictionary<int, (string Status, string Detail, double Pressure)>();
        foreach (var c in cells) byId[c.Id] = (c.Status, c.Detail, c.Pressure);
        var r = new LeakChannelSnap[8];
        for (int id = 1; id <= 8; id++)
        {
            bool picked = Picked(id);
            if (!byId.TryGetValue(id, out var c)) c = ("skip", "不测", double.NaN);
            string code = StatusOf(picked, Finished, c.Status, c.Detail);
            double? p = picked && double.IsFinite(c.Pressure) ? Math.Round(c.Pressure, 3) : null;
            double? rate = picked && _rates[id] is double x ? Math.Round(x, 3) : null;
            r[id - 1] = new LeakChannelSnap(id, code, p, rate, c.Detail);
        }
        return r;
    }

    /// <summary>
    /// 快照 leakTest。running / progress / phase / runId 取面板的实时值；done / failed 取这里（保持到下一次开始）；
    /// 结束后 progress 报结束时的进度。
    /// </summary>
    public LeakSnap ToSnap(DateTimeOffset now, bool running, double progress, string? phase, string? runId,
        IEnumerable<(int Id, string Status, string Detail, double Pressure)> cells)
    {
        if (Stage is null) return new LeakSnap(running, Math.Round(progress, 1), running ? phase : null, runId, false, false);
        double prog = Finished && !running ? FinalProgress : progress;
        return new LeakSnap(running, Math.Round(prog, 1), running ? phase : null, runId, Finished, Finished && Failed,
            Stage, StageLeftSec(now), TotalLeftSec(now), StartedAt?.UtcDateTime, StartedBy,
            TargetPsi, ThresholdPsiH, Channels(cells), Vented, Aborted, FailReason, FinishedAt?.UtcDateTime);
    }
}

/// <summary>容积标定阶段码（快照 calib.stage）。</summary>
public static class CalibStageCodes
{
    public const string First = "first";          // 第一遍 · 空釜（总管体积已知时只做这一遍）
    public const string WaitWater = "waitWater";  // 第一遍做完，等操作员加水后在触屏点「继续第二遍」
    public const string Second = "second";        // 第二遍 · 加水
    public const string Done = "done";
    public const string Failed = "failed";        // 失败或中止
}

/// <summary>
/// 容积标定的进行状态与结果（给快照用）。标定只能在触屏上开始 / 继续 / 中止；结束后的结果保持到下一次开始。
/// 等加水时不管面板开没开都算在等触屏操作。
/// </summary>
public sealed class CalibActivity
{
    /// <summary>当前阶段码；本次开机还没标定过（或放弃了第一遍）为 null。</summary>
    public string? Stage { get; private set; }
    public int? Channel { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string? StartedBy { get; private set; }
    public DateTimeOffset? WaitingSince { get; private set; }
    /// <summary>这一次要做两遍（空釜 + 加水）。</summary>
    public bool TwoRuns { get; private set; }
    /// <summary>第二遍实际加入的水 mL（第二遍开始时记下）。</summary>
    public double? UsedWaterMl { get; private set; }
    public double? FirstPassMl { get; private set; }
    public double? ResultMl { get; private set; }
    public string? ResultText { get; private set; }
    public double FinalProgress { get; private set; }
    public string? FinalPhase { get; private set; }

    public bool WaitingOperator => Stage == CalibStageCodes.WaitWater;

    /// <summary>
    /// 开始一遍。withWater = false：空釜那一遍（新的一次标定，清空上一次）；true：加水后的第二遍（开始时刻沿用第一遍）。
    /// </summary>
    public void Begin(DateTimeOffset now, int ch, bool withWater, double waterMl, bool twoRuns, string startedBy)
    {
        if (!withWater || StartedAt is null || Channel != ch)
        {
            StartedAt = now;
            FirstPassMl = null;
        }
        Stage = withWater ? CalibStageCodes.Second : CalibStageCodes.First;
        Channel = ch;
        StartedBy = startedBy;
        TwoRuns = withWater || twoRuns;
        UsedWaterMl = withWater ? waterMl : null;
        WaitingSince = null;
        ResultMl = null;
        ResultText = null;
        FinalProgress = 0;
        FinalPhase = null;
    }

    /// <summary>第一遍做完，开始等操作员加水。firstPassMl：这一遍算得出有效容积时给（两遍法的第一遍只有比值，为 null）。</summary>
    public void AwaitWater(DateTimeOffset now, double? firstPassMl, string? resultText)
    {
        Stage = CalibStageCodes.WaitWater;
        WaitingSince = now;
        FirstPassMl = firstPassMl;
        ResultText = resultText;
    }

    /// <summary>做完：得出有效容积。只做一遍的，第一遍的结果就是最终结果。</summary>
    public void Complete(double? resultMl, string? resultText, double progress, string? phase)
    {
        Stage = CalibStageCodes.Done;
        WaitingSince = null;
        ResultMl = resultMl;
        if (!TwoRuns) FirstPassMl = resultMl;
        ResultText = resultText;
        FinalProgress = progress;
        FinalPhase = phase;
    }

    /// <summary>失败或中止。</summary>
    public void Fail(string? resultText, double progress, string? phase)
    {
        Stage = CalibStageCodes.Failed;
        WaitingSince = null;
        ResultText = resultText;
        FinalProgress = progress;
        FinalPhase = phase;
    }

    /// <summary>操作员放弃了第一遍的结果：不再有进行中的标定。</summary>
    public void Clear()
    {
        Stage = null;
        WaitingSince = null;
    }

    /// <summary>
    /// 快照 calib；没有标定为 null。running / progress / phaseText 取面板的实时值，结束后报结束时的值；
    /// waterMl：两遍法的第一遍与等加水时报面板上当前的加水体积（还可以改），第二遍起报实际加入的量；只做一遍为 null。
    /// </summary>
    public CalibSnap? ToSnap(bool running, double progress, string? phaseText, double panelWaterMl)
    {
        if (Stage is not { } stage) return null;
        bool ended = !running && stage is CalibStageCodes.Done or CalibStageCodes.Failed;
        double? water = !TwoRuns ? null : UsedWaterMl ?? panelWaterMl;
        return new CalibSnap(running, Channel, stage, WaitingOperator, water, FirstPassMl, ResultMl,
            Math.Round(ended ? FinalProgress : progress, 1), ended ? FinalPhase : phaseText,
            StartedAt?.UtcDateTime, WaitingSince?.UtcDateTime, StartedBy,
            stage == CalibStageCodes.Done, stage == CalibStageCodes.Failed, ResultText);
    }
}

/// <summary>
/// 搅拌电机故障报警（v0.8.1）：驱动器报警码由 0 变为非 0（或换了一个码）时记一条 err，同一个故障只记一次；回到 0 时解除。
/// 报警本身由 MainViewModel 按条件键 <see cref="Key"/> 记 / 清（同模块掉线）。
/// </summary>
public sealed class StirFaultWatch
{
    public const string Key = "stir:fault";

    public enum Change { None, Raise, Clear }

    private ushort _code;

    /// <summary>当前登记的故障码（0 = 无故障）。</summary>
    public ushort Code => _code;

    /// <summary>每读到一次报警码调用一次（读失败不调用）。</summary>
    public Change Update(ushort code)
    {
        var prev = _code;
        _code = code;
        if (code == prev) return Change.None;
        return code == 0 ? Change.Clear : Change.Raise;
    }

    public static string CodeText(ushort code) => $"0x{code:X}";

    /// <summary>报警标题：「搅拌电机故障 · 代码 0x80 · 在触屏清除」。</summary>
    public static string Title(ushort code) => $"搅拌电机故障 · 代码 {CodeText(code)} · 在触屏清除";
}
