using System;
using System.Linq;
using ParallelReactor.Api;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 手造的实时遥测（ChannelSnap / ChannelProgramSnap / TelemetrySnapshot）：ChannelHistoryTests、LedgerMatchTests、StepTrackerTests、EtaEstimatorTests 共用。
/// 字段的含义同 HMI（Contracts/Api/TelemetrySnapshot.cs）：时刻一律 UTC，压力 psi，程序步骤序号 0 起（准备中为 −1）。
/// </summary>
internal static class LiveFrames
{
    /// <summary>本机客户端名在 HMI 的 startedBy 里的写法（远程来源标签「远程（客户端名）」）。</summary>
    public const string Me = "PC-测试工位1";
    public const string ByMe = "远程（" + Me + "）";

    public static ChannelSnap Ch(int id, double? temp = 25, double sp = 0, double? psi = 0, double pSp = 0, double? heat = 0, string state = "idle",
        int? atMode = 4, bool installed = true, ChannelProgramSnap? program = null, ProfileSnap? profile = null, string phase = "off") =>
        new(id, installed, state, null, false, false, null, temp, sp, sp, heat, temp is not null, false, atMode, profile is null ? "fixed" : "curve",
            new ApproachSnap(phase, null, null, ""), profile, psi, 0, pSp, null, false, program is { Running: true }, program);

    public static ChannelProgramSnap Prog(int step, int count, string state = "running", DateTime? startedAt = null, string? startedBy = ByMe,
        string message = "", bool waitingGas = false, bool? running = null) =>
        new(running ?? state is "preparing" or "running" or "waitingGas" or "finishing", step, count, state, message, startedBy, startedAt, waitingGas);

    public static TelemetrySnapshot Frame(DateTime tsUtc, params ChannelSnap[] chans) => Frame(tsUtc, true, 600, chans);

    public static TelemetrySnapshot Frame(DateTime tsUtc, bool stirOn, int rpm, params ChannelSnap[] chans) =>
        new("telemetry", 1, tsUtc, true, 0, new ModeSnap("remote", Me), new EstopSnap(false, "ok", "ok", "ok", null, false, false),
            new CommSnap(true, true, true, true), chans, new StirSnap(stirOn, rpm, true, false, null, null, null, null, 1000),
            new GasSnap(Array.Empty<GasInletSnap>(), false, false, null, null), new ProgramSnap(false, null, "idle", 0, 0, "", ""),
            new ProgramsSnap(chans.Count(c => c.Program is { Running: true }), chans.Where(c => c.Program is { Running: true }).Select(c => c.Id).ToArray(), null, Array.Empty<int>(), ""),
            new LeakSnap(false, 0, null, null, false, false), new SessionSnap(false, null, null, null));
}
