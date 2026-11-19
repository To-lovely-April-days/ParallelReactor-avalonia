using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;

namespace ParallelReactor.Control.Core.Activity;

/// <summary>设备活动的种类（v0.8.1 设备活动同步，ACT-SPEC §1）：泄漏测试 / 容积标定。</summary>
public enum ActKind { Leak, Calib }

/// <summary>
/// 活动处在哪一步：进行中 / 等人在触屏上操作（标定加水）/ 结束（通过 · 有不通过 · 已中止 · 失败没出结果）。
/// </summary>
public enum ActPhase { Running, Waiting, Passed, Failed, Aborted, Error }

/// <summary>泄漏测试一路的状态（LeakChannelSnap.status）：进行中 / 通过 / 不通过 / 充压不上 / 初始压力过高（未测）/ 没参加。</summary>
public enum LeakChState { Testing, Pass, Fail, FillFail, TooHigh, Untested }

/// <summary>泄漏测试的一路：状态、压力（psi，表压）、压降速率（psi/h，测量阶段起有）、触屏格子上的文字。</summary>
public sealed record ActChannel(int N, LeakChState State, double? PressurePsi, double? RatePsiH, string Detail);

/// <summary>
/// 一台设备此刻的活动（泄漏测试或容积标定；ACT-SPEC §1 的 LeakSnap / CalibSnap 归一化）。纯数据，时刻一律本地时间。
/// <list type="bullet">
/// <item><see cref="Stage"/>：0–3，泄漏测试 = 充压 / 平衡 / 测量 / 判读 · 泄压；容积标定 = 第一遍 · 空釜 / 加水 / 第二遍 · 读数 / 算出容积。</item>
/// <item><see cref="Channels"/>：参加的几路（没参加的在 <see cref="Untested"/>）；容积标定只有 <see cref="Channel"/> 一路。</item>
/// <item><see cref="Key"/>：这一次活动的身份（种类 + 编号 / 开始时刻）：换了就是新的一次（自动展开、关掉过的不再出现都按它）。</item>
/// </list>
/// </summary>
public sealed record DeviceActivity
{
    public ActKind Kind { get; init; }
    public ActPhase Phase { get; init; }
    /// <summary>当前阶段 0–3（结束后 = 3）。</summary>
    public int Stage { get; init; }
    /// <summary>设备给的阶段码（leak：prep / fill / lineVent / settle / measure / vent / done；calib：first / waitWater / second / done / failed）。</summary>
    public string? StageCode { get; init; }
    /// <summary>本阶段还剩多少秒（平衡、测量有）。</summary>
    public double? StageLeftSec { get; init; }
    /// <summary>整个测试的估算剩余秒数。</summary>
    public double? TotalLeftSec { get; init; }
    /// <summary>进度 0–100（设备给的，不再乘 100）。</summary>
    public double Progress { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    /// <summary>发起人原文（「触屏」/「远程（PC-x）」，CommandSource.Label）。</summary>
    public string? StartedBy { get; init; }
    public double? TargetPsi { get; init; }
    public double? ThresholdPsiH { get; init; }
    public IReadOnlyList<ActChannel> Channels { get; init; } = Array.Empty<ActChannel>();
    public IReadOnlyList<int> Untested { get; init; } = Array.Empty<int>();
    /// <summary>结束后泄压成功没有（null = 没做到那一步）。</summary>
    public bool? Vented { get; init; }
    public string? FailReason { get; init; }
    public string? RunId { get; init; }
    /// <summary>触屏上的阶段文字（泄漏测试 phase、标定 phaseText），只用来显示。</summary>
    public string? PhaseText { get; init; }

    // ———— 容积标定 ————
    public int? Channel { get; init; }
    public double? WaterMl { get; init; }
    public double? FirstPassMl { get; init; }
    public double? ResultMl { get; init; }
    public DateTime? WaitingSince { get; init; }
    public string? ResultText { get; init; }

    /// <summary>进行中或等人操作。</summary>
    public bool Live => Phase is ActPhase.Running or ActPhase.Waiting;
    public bool Finished => !Live;
    public bool Waiting => Phase == ActPhase.Waiting;

    /// <summary>这一次活动的身份：种类 + 开始时刻（HMI 结束后 runId 会变成 null；标定的第二遍沿用第一遍的开始时刻），没有开始时刻时用编号。</summary>
    public string Key => Kind + ":" + (StartedAt is { } t ? t.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : RunId is { Length: > 0 } id ? id : Channel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");

    /// <summary>参加的通道号（泄漏测试 = 不含没参加的；标定 = 那一路）。</summary>
    public IReadOnlyList<int> Chs => Kind == ActKind.Calib ? (Channel is int c ? new[] { c } : Array.Empty<int>()) : Channels.Select(c => c.N).ToArray();

    /// <summary>通过 / 不通过（含充压不上）/ 初始压力过高（未测）的路数。</summary>
    public (int Ok, int No, int Amb) Counts =>
        (Channels.Count(c => c.State == LeakChState.Pass),
         Channels.Count(c => c.State is LeakChState.Fail or LeakChState.FillFail),
         Channels.Count(c => c.State == LeakChState.TooHigh));
}

/// <summary>
/// 快照 → <see cref="DeviceActivity"/>（纯函数，单元测试直接调）。旧 HMI 没有 stage 码时按进度分段（同 HMI 的进度：充压 0–12、平衡 12–35、测量 35–97）；
/// <b>不解析中文的 phase</b>（ACT-SPEC §3.3 第 1 条）。
/// </summary>
public static class ActivityBuild
{
    /// <summary>泄漏测试阶段码 → 0–3：prep / fill / lineVent = 充压，settle = 平衡，measure = 测量，vent / done = 判读 · 泄压。不认识的按进度。</summary>
    public static int LeakStage(string? code, double progress) => code switch
    {
        "prep" or "fill" or "lineVent" => 0,
        "settle" => 1,
        "measure" => 2,
        "vent" or "done" => 3,
        _ => progress < 12 ? 0 : progress < 35 ? 1 : progress < 97 ? 2 : 3,
    };

    /// <summary>标定阶段码 → 0–3：first = 第一遍，waitWater = 加水，second = 第二遍，done / failed = 算出容积。</summary>
    public static int CalibStage(string? code) => code switch
    {
        "first" => 0,
        "waitWater" => 1,
        "second" => 2,
        _ => 3,
    };

    /// <summary>一路的状态码 → 枚举（不认识的按进行中）。</summary>
    public static LeakChState ChState(string? status) => status switch
    {
        "pass" => LeakChState.Pass,
        "fail" => LeakChState.Fail,
        "fillFail" => LeakChState.FillFail,
        "tooHigh" => LeakChState.TooHigh,
        "untested" => LeakChState.Untested,
        _ => LeakChState.Testing,
    };

    /// <summary>接口的 UTC 时刻 → 本地时间（已经是本地的原样）。</summary>
    public static DateTime? Local(DateTime? t) => t is not { } v ? null
        : v.Kind == DateTimeKind.Local ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc).ToLocalTime();

    /// <summary>没参加的格子上的字（「不测」「运行中」/ 空）；其余（「未完成」…）= 参加了没出结果。</summary>
    public static bool IsUnfinished(string? detail) => detail is { Length: > 0 } d && d != "不测" && d != "运行中" && d != "待测";

    /// <summary>泄漏测试；没有在测、也没有结果时为 null（本次开机还没测过：新字段都是 null、done = false）。</summary>
    public static DeviceActivity? Leak(LeakSnap? s)
    {
        if (s is null || !(s.Running || s.Done || s.Stage is { Length: > 0 })) return null;
        var all = (s.Channels ?? Array.Empty<LeakChannelSnap>()).Where(c => c != null)
            .Select(c => new ActChannel(c.Id, ChState(c.Status), c.PressurePsi, c.RatePsiH, c.Detail ?? "")).OrderBy(c => c.N).ToList();
        // 中止 / 失败时参加了、但没出结果的路，HMI 报 untested、detail「未完成」（act-hmi-handover §3）：算参加的（显示「未完成」），不算「不测」
        bool cut = s.Aborted || s.FailReason is { Length: > 0 };
        bool Picked(ActChannel c) => c.State != LeakChState.Untested || (cut && !s.Running && IsUnfinished(c.Detail));
        var chs = all.Where(Picked).ToList();
        var off = all.Where(c => !Picked(c)).Select(c => c.N).ToList();
        double pr = Math.Clamp(s.Progress, 0, 100);
        ActPhase ph;
        if (s.Running) ph = ActPhase.Running;
        else if (s.Aborted) ph = ActPhase.Aborted;
        else if (chs.Any(c => c.State is LeakChState.Fail or LeakChState.FillFail)) ph = ActPhase.Failed;
        else if (chs.Any(c => c.State == LeakChState.Pass)) ph = ActPhase.Passed;
        else if (s.FailReason is { Length: > 0 }) ph = ActPhase.Error;
        // 旧 HMI（没有每路结果）：只有 failed 一个标志
        else ph = s.Failed ? ActPhase.Failed : ActPhase.Passed;
        return new DeviceActivity
        {
            Kind = ActKind.Leak,
            Phase = ph,
            Stage = ph == ActPhase.Running ? LeakStage(s.Stage, pr) : 3,
            StageCode = s.Stage,
            StageLeftSec = s.Running ? s.StageLeftSec : null,
            TotalLeftSec = s.Running ? s.TotalLeftSec : null,
            Progress = ph is ActPhase.Passed or ActPhase.Failed ? 100 : pr,
            StartedAt = Local(s.StartedAt),
            FinishedAt = Local(s.FinishedAt),
            StartedBy = s.StartedBy,
            TargetPsi = s.TargetPsi,
            ThresholdPsiH = s.ThresholdPsiH,
            Channels = chs,
            Untested = off,
            Vented = s.Vented,
            FailReason = s.FailReason,
            RunId = s.RunId,
            PhaseText = s.Phase,
        };
    }

    /// <summary>容积标定；没有标定（或结束状态都没有）时为 null。</summary>
    public static DeviceActivity? Calib(CalibSnap? c)
    {
        if (c is null || !(c.Running || c.WaitingOperator || c.Done || c.Failed)) return null;
        var ph = c.WaitingOperator ? ActPhase.Waiting : c.Running ? ActPhase.Running : c.Failed ? ActPhase.Error : ActPhase.Passed;
        return new DeviceActivity
        {
            Kind = ActKind.Calib,
            Phase = ph,
            Stage = ph == ActPhase.Waiting ? 1 : ph == ActPhase.Running ? CalibStage(c.Stage) : 3,
            StageCode = c.Stage,
            Progress = Math.Clamp(c.Progress, 0, 100),
            StartedAt = Local(c.StartedAt),
            StartedBy = c.StartedBy,
            Channel = c.Channel,
            WaterMl = c.WaterMl,
            FirstPassMl = c.FirstPassMl,
            ResultMl = c.ResultMl,
            WaitingSince = Local(c.WaitingSince),
            PhaseText = c.PhaseText,
            ResultText = c.ResultText,
        };
    }

    /// <summary>
    /// 一台设备只显示一项活动：进行中 / 等人操作的优先（两样都在时等人操作的优先），都结束了取开始得晚的那个（同 HMI：结束状态保持到下一次开始）。
    /// </summary>
    public static DeviceActivity? Pick(DeviceActivity? leak, DeviceActivity? calib)
    {
        if (leak is null) return calib;
        if (calib is null) return leak;
        if (calib.Waiting) return calib;
        if (leak.Live) return leak;
        if (calib.Live) return calib;
        return (calib.StartedAt ?? DateTime.MinValue) > (leak.StartedAt ?? DateTime.MinValue) ? calib : leak;
    }

    /// <summary>一帧遥测 → 这台设备此刻的活动。</summary>
    public static DeviceActivity? From(TelemetrySnapshot? f) => f is null ? null : Pick(Leak(f.LeakTest), Calib(f.Calib));
}
