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

/// <summary>
/// §6.3 的单通道。profile 在 /snapshot 与 WS 帧里只在 spMode=curve 时给出，/channels/{id} 总是给出。
/// v0.6 末尾追加（带默认值：旧 HMI 的帧反序列化后为 null）：gasRate = 最近 1 分钟平均吸气速率 mmol/min（Reactor.GasRate，未在计量或数据不够时 null）；
/// solutionVolume = 溶液体积 mL（Reactor.Vol，0.1 mL 分辨率，参与气相体积计算）。
/// </summary>
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
    ChannelProgramSnap? Program,
    double? GasRate = null,
    double? SolutionVolume = null);

/// <summary>
/// §6.3 的 channels[i].program（v0.5 多程序并行）：该通道正在执行 / 最近一次执行的程序。没跑过程序为 null。
/// state：preparing / running / waitingGas / finishing / completed / failed / aborted；waitingGas = 当前气路步骤正在排队等待其他程序释放气路。
/// </summary>
public sealed record ChannelProgramSnap(bool Running, int StepIndex, int StepCount, string State, string Message, string? StartedBy, DateTime? StartedAt, bool WaitingGas);

/// <summary>
/// GET /channels/{id}/program/run（v0.6，§7.9）：该通道正在执行 / 最近一次执行的程序。steps 是下发时冻结的副本（§6.9 格式，sel 字段为英文枚举码），
/// 其余字段每个 Tick 刷新：state = preparing / running / waitingGas / finishing / completed / failed / aborted（同 channels[i].program.state）；
/// stepIndex 0 起（尚未开始为 −1）；phase = 监视阶段文字（「执行第 4 步 · 升温」/「程序执行完成」…）；message = 监视详情；startedBy = local / 远程来源标签。
/// 只保留本次开机内的记录（HMI 重启后为空）。
/// endReason（v0.8，STEPS-SPEC §1.7）：反应步骤的结束原因（「最长时间到」「吸气量达到 5.00 mmol」「吸气停止（30 min &lt; 0.005 mmol/min）」），没有反应步骤为 null。
/// </summary>
public sealed record ProgramRunSnap(int Channel, bool Running, string State, int StepIndex, int StepCount, string Phase, string Message,
    string? StartedBy, DateTime StartedAt, bool WaitingGas, ProgramStepDto[] Steps, string? EndReason = null);

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
public sealed record LeakSnap(bool Running, double Progress, string? Phase, string? RunId, bool Done, bool Failed,
    string? Stage = null, double? StageLeftSec = null, double? TotalLeftSec = null, DateTime? StartedAt = null, string? StartedBy = null,
    double? TargetPsi = null, double? ThresholdPsiH = null, LeakChannelSnap[]? Channels = null, bool? Vented = null, bool Aborted = false,
    string? FailReason = null, DateTime? FinishedAt = null);

/// <summary>
/// v0.8.1（ACT-SPEC.md，PC 端同步显示触屏发起的泄漏测试）：一路的状态。status：testing（进行中，detail 是触屏的文字「充压中 60%」「平衡中」）/
/// pass / fail（不通过）/ fillFail（充压不上）/ tooHigh（初始压力过高，未测）/ untested（没参加）。pressurePsi 表压 psi；ratePsiH 压降速率（测量阶段起有）。
/// </summary>
public sealed record LeakChannelSnap(int Id, string Status, double? PressurePsi, double? RatePsiH, string Detail);

/// <summary>
/// v0.8.1：容积标定（只能在触屏上开始 / 继续 / 中止；PC 只显示）。stage：first（第一遍 · 空釜）/ waitWater（等操作员加水后点继续）/
/// second（第二遍 · 读数）/ done / failed。waitingOperator = 正在等触屏操作；firstPassMl = 第一遍算出的有效容积（只有一遍就结束时是结果）；
/// resultMl = 两遍做完算出的有效容积；startedBy：「触屏」。
/// </summary>
public sealed record CalibSnap(bool Running, int? Channel, string Stage, bool WaitingOperator, double? WaterMl, double? FirstPassMl,
    double? ResultMl, double Progress, string? PhaseText, DateTime? StartedAt, DateTime? WaitingSince, string? StartedBy, bool Done, bool Failed,
    string? ResultText);

/// <summary>§6.3 的 session（P0 会话：id 即 testId，见 §9.1 K6）。</summary>
public sealed record SessionSnap(bool Active, string? Id, string? TestId, string? File);

/// <summary>控制循环门闸统计（/device/comm 的 tick）。</summary>
public sealed record TickStats(double IntervalMs, long Entered, long Skipped, long Overruns, double LastTookMs, double MaxTookMs, double LastOverrunMs);

/// <summary>单个通讯模块的统计（/device/comm）。</summary>
public sealed record ModuleCommSnap(bool Configured, bool Online, long Polls, long Fails, DateTime? LastOkAt, DateTime? ValuesAt);

/// <summary>§6.7 的 tempBands 一项。</summary>
public sealed record TempBandSnap(int Index, double LoC, double HiC);

/// <summary>
/// GET /pid 的温度档位一项（v0.6）：下标（= PUT /channels/{id}/pid/{band} 与自整定 band 的取值）、中文名、温区 [loC, hiC)。
/// 最高档另外包含 ≥ hiC 的温度（HMI 按目标温度选档时超出上限归最高档）。分界只能在触屏上改。
/// </summary>
public sealed record PidBandSnap(int Index, string Name, double LoC, double HiC);

/// <summary>
/// GET /pid 的某通道某档 PID（v0.6）：P（比例带）/ I（积分时间 s）/ D（微分时间 s）+ 来历。
/// tunedSpC / tunedAt = 最近一次完成并存档的自整定的目标温度（℃）与完成时刻（UTC；被取消 / 急停打断的不算）；
/// manualEditedAt = 整定之后又被手改的最近时刻（UTC）。都为 null = 出厂默认 / 首次开机从仪表读来的种子值。
/// </summary>
public sealed record PidBandValueSnap(int Index, double P, double I, double D, double? TunedSpC, DateTime? TunedAt, DateTime? ManualEditedAt);

/// <summary>
/// GET /pid 的一个通道（v0.6）：是否正在自整定（与遥测 channels[i].autotuning 同源）；整定中时结果将存入的档位与整定目标温度
/// （软件发起的整定才有；仪表面板发起的为 null）；各档 PID。
/// </summary>
public sealed record PidChannelSnap(int Id, bool Autotuning, int? TuneBand, double? TuneSpC, PidBandValueSnap[] Bands);

/// <summary>PID 快照（每 Tick 随 SnapshotBundle 生成；GET /pid 取它，不进 WS 帧、不进 UI 线程）。</summary>
public sealed record PidSnap(PidBandSnap[] Bands, PidChannelSnap[] Channels);

/// <summary>设置快照（/settings 与 /program-schema 的限值来源；不进 WS 帧）。</summary>
public sealed record SettingsSnap(
    double TMaxSp, double PMaxSp, double RpmMax, double VolMax,
    double OverPressure, double OverTemp, bool StopHeatOnOverPressure, double HeatTimeout, double PressDeviation,
    double LeakTestPsi, double LeakSettleMin, double LeakMeasureMin, double LeakRate, double LeakReminderDays, DateTime? LastLeakTestUtc,
    double ApproachDelta, double ApproachRate, double ApproachFastRate, double ApproachFinalZone, double ApproachSettleBand, double ApproachSettleSec,
    double StirCurrent, double StirStandbyPct, double StirRampMs, double StirMicrostep,
    double PressFullScaleMPa,
    TempBandSnap[] TempBands,
    long Revision,
    VolumeSettingsSnap? Volume = null,
    double StirGearRatio = 1);

/// <summary>
/// 吸气量计算 / 容积标定相关设置（v0.6，只读；标定只允许在触屏上做）。effectiveVolume / volumeCalibratedAt 各 8 项（通道 1..8），未标定为 null。
/// </summary>
public sealed record VolumeSettingsSnap(double VesselVolume, double GasLineVolume, double? ManifoldVolume, double?[] EffectiveVolume, DateTime?[] VolumeCalibratedAt);

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
    SessionSnap Session,
    CalibSnap? Calib = null);

/// <summary>
/// 一个 Tick 生成的全部只读数据：对外帧 + 通道全量（含曲线）+ 设置 + 门闸 / 通讯统计 + 运行中的程序 + 档位 PID（v0.6）。
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
    long LoggerDroppedLines,
    ProgramRunSnap?[]? ProgramRuns = null,
    PidSnap? Pid = null)
{
    /// <summary>通道号 1..8 → 全量通道快照；越界为 null。</summary>
    public ChannelSnap? Channel(int id) => id >= 1 && id <= FullChannels.Length ? FullChannels[id - 1] : null;

    /// <summary>通道号 1..8 → 该通道正在执行 / 最近一次执行的程序（含冻结步骤）；没跑过或越界为 null。</summary>
    public ProgramRunSnap? ProgramRun(int id) => ProgramRuns is { } r && id >= 1 && id <= r.Length ? r[id - 1] : null;
}
