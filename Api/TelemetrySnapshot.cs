using System;

namespace ParallelReactor.Api;

// ============================================================================
// 遥测快照（设计稿 §6.3 / §6.6）。全部为不可变 record：每个 Tick 末尾在 UI 线程生成一份，
// 原子替换 SnapshotStore 里的引用；Kestrel 线程与 WebSocket 推送只读它，不碰 VM。
// 时间一律 UTC（DateTime.Kind = Utc，序列化为 ...Z）；无效读数为 null；枚举以小写字符串给出。
// ============================================================================

/// <summary>§6.3 的 mode。</summary>
public sealed record ModeSnap(string Control, string? Holder);

/// <summary>§6.3 的 estop（含 §7.3 的 latched / restoredOnBoot）。各部分：pending / ok / failed / notConfigured。</summary>
public sealed record EstopSnap(bool Active, string Heat, string Stir, string Valves, DateTime? Since, bool Latched, bool RestoredOnBoot);

/// <summary>§6.3 的 comm：各总线是否在线。</summary>
public sealed record CommSnap(bool Temp, bool Pressure, bool Stir, bool Io);

/// <summary>§6.6 的 approach。phase：off / settle / creep / hold。</summary>
public sealed record ApproachSnap(string Phase, double? TargetC, double? CurrentSpC, string StatusText);

/// <summary>§6.6 的曲线段。</summary>
public sealed record ProfileSegmentSnap(int Index, double TempC, double Minutes, bool Enabled, bool Active);

/// <summary>§6.6 的 profile。</summary>
public sealed record ProfileSnap(bool Running, int SegCount, ProfileSegmentSnap[] Segments, double ElapsedMin, double TotalMinutes, bool Complete, string StatusText);

/// <summary>§6.3 的单通道。profile 在 /snapshot 与 WS 帧里只在 spMode=curve 时给出，/channels/{id} 总是给出。</summary>
public sealed record ChannelSnap(
    int Id,
    bool Installed,
    string State,
    string? AlarmCause,
    bool OverTempTripped,
    bool OverPressureTripped,
    string? StartedBy,
    double? TempC,
    double TempSpC,
    double? TempSpDeviceC,
    double? HeatOutputPct,
    bool TempOnline,
    bool Autotuning,
    int? AtMode,
    string SpMode,
    ApproachSnap Approach,
    ProfileSnap? Profile,
    double? PressurePsi,
    int? PressureRaw,
    double PressureSpPsi,
    double? GasConsumed,
    bool ValveOpen,
    bool ProgramOwned,
    ChannelProgramSnap? Program);

/// <summary>
/// §6.3 的 channels[i].program（v0.5 多程序并行）：该通道正在执行 / 最近一次执行的程序。没跑过程序为 null。
/// state：preparing / running / waitingGas / finishing / completed / failed / aborted；waitingGas = 当前气路步骤正在排队等待其他程序释放气路。
/// </summary>
public sealed record ChannelProgramSnap(bool Running, int StepIndex, int StepCount, string State, string Message, string? StartedBy, DateTime? StartedAt, bool WaitingGas);

/// <summary>§6.3 的 stir。</summary>
public sealed record StirSnap(bool On, int RpmSp, bool Online, bool Locked, double? MotorCurrentA, double? BusVoltageV, string? Fault, int? AlarmCode, int RampMsPer1000Rpm);

public sealed record GasInletSnap(string Id, bool Open);

/// <summary>§6.3 的 gas。owner：program / leak / null。</summary>
public sealed record GasSnap(GasInletSnap[] Inlets, bool VentOpen, bool ManualValve, string? Task, string? Owner);

/// <summary>
/// §6.3 的 program。<b>已废弃（v0.5 起用 channels[i].program 与 programs；下一版本删除）</b>：
/// 多程序并行后只填首个（通道号最小）正在执行的程序，没有时 running=false。
/// </summary>
public sealed record ProgramSnap(bool Running, int? Channel, string Status, int StepIndex, int StepCount, string Phase, string Detail);

/// <summary>§6.3 的 programs（v0.5）：并行程序总览。gasHolder = 当前持有气路的程序通道；waitingGas = 正在排队等气路的通道（先后顺序）。</summary>
public sealed record ProgramsSnap(int Running, int[] Channels, int? GasHolder, int[] WaitingGas, string Summary);

/// <summary>§6.3 的 leakTest。</summary>
public sealed record LeakSnap(bool Running, double Progress, string? Phase, string? RunId, bool Done, bool Failed);

/// <summary>§6.3 的 session（P0 会话：id 即 testId，见 §9.1 K6）。</summary>
public sealed record SessionSnap(bool Active, string? Id, string? TestId, string? File);

/// <summary>控制循环门闸统计（/device/comm 的 tick）。</summary>
public sealed record TickStats(double IntervalMs, long Entered, long Skipped, long Overruns, double LastTookMs, double MaxTookMs, double LastOverrunMs);

/// <summary>单个通讯模块的统计（/device/comm）。</summary>
public sealed record ModuleCommSnap(bool Configured, bool Online, long Polls, long Fails, DateTime? LastOkAt, DateTime? ValuesAt);

/// <summary>§6.7 的 tempBands 一项。</summary>
public sealed record TempBandSnap(int Index, double LoC, double HiC);

/// <summary>设置快照（/settings 与 /program-schema 的限值来源；不进 WS 帧）。</summary>
public sealed record SettingsSnap(
    double TMaxSp, double PMaxSp, double RpmMax, double VolMax,
    double OverPressure, double OverTemp, bool StopHeatOnOverPressure, double HeatTimeout, double PressDeviation,
    double LeakTestPsi, double LeakSettleMin, double LeakMeasureMin, double LeakRate, double LeakReminderDays, DateTime? LastLeakTestUtc,
    double ApproachDelta, double ApproachRate, double ApproachFastRate, double ApproachFinalZone, double ApproachSettleBand, double ApproachSettleSec,
    double StirCurrent, double StirStandbyPct, double StirRampMs, double StirMicrostep,
    double PressFullScaleMPa,
    TempBandSnap[] TempBands,
    long Revision);

/// <summary>§6.3 的遥测帧（WebSocket telemetry 消息 / GET /snapshot 的正文）。</summary>
public sealed record TelemetrySnapshot(
    string Type,
    long Seq,
    DateTime Ts,
    bool Simulated,
    long EventSeqApplied,
    ModeSnap Mode,
    EstopSnap Estop,
    CommSnap Comm,
    ChannelSnap[] Channels,
    StirSnap Stir,
    GasSnap Gas,
    ProgramSnap Program,
    ProgramsSnap Programs,
    LeakSnap LeakTest,
    SessionSnap Session);

/// <summary>
/// 一个 Tick 生成的全部只读数据：对外帧 + 通道全量（含曲线）+ 设置 + 门闸 / 通讯统计。
/// SnapshotStore 存的是它；各端点按需取子集。
/// </summary>
public sealed record SnapshotBundle(
    TelemetrySnapshot Frame,
    ChannelSnap[] FullChannels,
    SettingsSnap Settings,
    TickStats Tick,
    ModuleCommSnap TempComm,
    ModuleCommSnap PressureComm,
    ModuleCommSnap StirComm,
    ModuleCommSnap IoComm,
    long LoggerDroppedLines)
{
    /// <summary>通道号 1..8 → 全量通道快照；越界为 null。</summary>
    public ChannelSnap? Channel(int id) => id >= 1 && id <= FullChannels.Length ? FullChannels[id - 1] : null;
}
