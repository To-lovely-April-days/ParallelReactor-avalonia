using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;

namespace ParallelReactor.Control.Core.Activity;

/// <summary>急停的一部分（加热 / 搅拌 / 阀门）的确认状态：设备写的 ok / pending / failed / notConfigured。</summary>
public enum EstopPartState { Ok, Pending, Failed, NotConfigured }

/// <summary>急停各部分（EstopSnap.heat / stir / valves；程序在急停时一律中止，不另报）。</summary>
public sealed record EstopParts(DateTime? Since, EstopPartState Heat, EstopPartState Stir, EstopPartState Valves)
{
    /// <summary>设备写的码 → 状态（SnapshotBuilder：Ok → "ok"、Failed → "failed"、其余 → "pending"；没配的 notConfigured）。</summary>
    public static EstopPartState Parse(string? s) => s switch
    {
        "ok" => EstopPartState.Ok,
        "failed" => EstopPartState.Failed,
        "notConfigured" => EstopPartState.NotConfigured,
        _ => EstopPartState.Pending,
    };

    /// <summary>急停之后等确认的宽限：pending 超过这么久才算「没收到确认」（刚急停的一两秒里各部分本来就是 pending）。</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(10);

    /// <summary>这一部分算没确认：failed，或急停已超过 <see cref="PendingGrace"/> 仍是 pending（act-hmi-handover：pending 在 active 时也算没确认）。notConfigured 不适用。</summary>
    public bool Unconfirmed(EstopPartState s, DateTime now) =>
        s == EstopPartState.Failed || (s == EstopPartState.Pending && (Since is not { } t || now - t >= PendingGrace));

    /// <summary>有一部分没确认 —— 报警弹窗、设备栏红行。</summary>
    public bool AnyUnconfirmed(DateTime now) => Unconfirmed(Heat, now) || Unconfirmed(Stir, now) || Unconfirmed(Valves, now);

    /// <summary>
    /// 四行（程序 / 加热 / 搅拌 / 阀门）：名字、做了什么、是否已确认（Ok）、是否没确认（Bad：标红）、不适用（Na：notConfigured）。
    /// 急停之后宽限内的 pending 既不 Ok 也不 Bad（「等待确认」）。
    /// </summary>
    public IReadOnlyList<(string Part, string Done, bool Ok, bool Bad, bool Na)> Rows(DateTime now) => new[]
    {
        ("程序", "全部中止", true, false, false),
        Row("加热", "已切断并锁定", "加热未确认切断", Heat, now),
        Row("搅拌", "已停止并锁定", "搅拌未确认停止", Stir, now),
        Row("阀门", "已全部关闭", "阀门未确认关闭", Valves, now),
    };

    private (string, string, bool, bool, bool) Row(string part, string ok, string bad, EstopPartState s, DateTime now) =>
        s == EstopPartState.NotConfigured ? (part, "没有配置", false, false, true)
        : s == EstopPartState.Ok ? (part, ok, true, false, false)
        : Unconfirmed(s, now) ? (part, bad, false, true, false)
        : (part, "等待确认", false, false, false);

    /// <summary>没确认的部分（名字），按 加热 / 搅拌 / 阀门。</summary>
    public IReadOnlyList<string> UnconfirmedParts(DateTime now) => Rows(now).Where(r => r.Bad).Select(r => r.Part).ToArray();

    /// <summary>已确认的几项 / 一共几项（含「程序」一行，不算没配的）。</summary>
    public (int Ok, int Of) Confirmed(DateTime now)
    {
        var rows = Rows(now).Where(r => !r.Na).ToList();
        return (rows.Count(r => r.Ok), rows.Count);
    }
}

/// <summary>
/// 一台设备的全局标记（ACT-SPEC §1.3 / §3.2）：气路被谁占用、触屏手动阀控、搅拌电机故障、急停没确认的部分、自整定中的通道。
/// 纯数据；<see cref="From"/> 从一帧遥测取（演示数据直接构造）。
/// </summary>
public sealed record DeviceMarks
{
    public static readonly DeviceMarks None = new();

    /// <summary>gas.owner：leak / calib / program / null。</summary>
    public string? GasOwner { get; init; }
    /// <summary>gas.manualValve：触屏的手动阀控开着。</summary>
    public bool ManualValve { get; init; }
    /// <summary>stir.fault：搅拌电机故障的名称（「过流」…）；没有故障为 null。</summary>
    public string? StirFault { get; init; }
    /// <summary>stir.alarmCode：驱动器的报警码。</summary>
    public int? StirCode { get; init; }
    /// <summary>急停中各部分的确认（不在急停为 null）。</summary>
    public EstopParts? Estop { get; init; }
    /// <summary>正在自整定的通道 → 整定温度（设定值，没有为 null）。</summary>
    public IReadOnlyList<(int N, double? SpC)> Autotune { get; init; } = Array.Empty<(int, double?)>();

    public bool HasStirFault => StirFault is { Length: > 0 } || StirCode is > 0;

    /// <summary>故障码的写法「0x80」（驱动器的报警码，十六进制，同 HMI 报警标题「搅拌电机故障 · 代码 0x80 · 在触屏清除」）；没有码为 null。</summary>
    public string? StirCodeText => StirCode is int c && c > 0 ? "0x" + c.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>气路被泄漏测试 / 标定占用（这时排队的程序写「排队」）。</summary>
    public bool GasByActivity => GasOwner is "leak" or "calib";

    public bool Tuning(int n) => Autotune.Any(a => a.N == n);

    /// <summary>一帧遥测里的标记。</summary>
    public static DeviceMarks From(TelemetrySnapshot? f)
    {
        if (f is null) return None;
        var tune = (f.Channels ?? Array.Empty<ChannelSnap>()).Where(c => c is { Installed: true, Autotuning: true })
            .Select(c => (c.Id, (double?)(c.TempSpC > 0 ? c.TempSpC : null))).OrderBy(x => x.Id).ToArray();
        var e = f.Estop;
        return new DeviceMarks
        {
            GasOwner = f.Gas?.Owner is { Length: > 0 } o ? o : null,
            ManualValve = f.Gas?.ManualValve ?? false,
            StirFault = f.Stir?.Fault is { Length: > 0 } sf ? sf : null,
            StirCode = f.Stir?.AlarmCode is int ac && ac > 0 ? ac : null,
            Estop = e is { Active: true } ? new EstopParts(ActivityBuild.Local(e.Since), EstopParts.Parse(e.Heat), EstopParts.Parse(e.Stir), EstopParts.Parse(e.Valves)) : null,
            Autotune = tune,
        };
    }
}
