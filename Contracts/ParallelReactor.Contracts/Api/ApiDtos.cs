using System;
using System.Collections.Generic;
using System.Text.Json;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

// ============================================================================
// REST / WebSocket 的响应模型（设计稿 §6 / §7 / §8 / §9）。全部 record，由 ApiJsonContext 源生成序列化（camelCase）。
// ============================================================================

public static class ApiVersionInfo
{
    public const string ApiVersion = "1.0";
    public const string Model = "ParallelReactor-8";
}

public sealed record HealthDto(bool Ok, string ApiVersion, bool Simulated, string BootId, DateTime ServerTime);

public sealed record HardwareModuleDto(bool Configured, string? Port, int? Baud, double? FullScaleMPa);
public sealed record HardwareDto(HardwareModuleDto Temp, HardwareModuleDto Pressure, HardwareModuleDto Stir, HardwareModuleDto Io);
public sealed record ChannelInstalledDto(int Id, bool Installed);
public sealed record UnitsDto(string Temperature, string Pressure, string Speed);

/// <summary>§6.1 DeviceInfo。</summary>
public sealed record DeviceInfoDto(
    string Model, string? Serial, string HmiVersion, string ApiVersion, int ChannelCount,
    ChannelInstalledDto[] Channels, int[] InstalledChannels, HardwareDto Hardware, UnitsDto Units,
    bool Simulated, bool SimRefused, DateTime BootTime, string BootId, bool? ClockSynced, long EventSeqHead, int SeqEpoch);

/// <summary>§7.1 失败响应。</summary>
public sealed record ErrorBody(string Code, string Message, Dictionary<string, string>? Detail);
public sealed record ErrorDto(bool Ok, ErrorBody Error);

public sealed record ChannelsDto(long Seq, DateTime Ts, int[] InstalledChannels, ChannelSnap[] Channels);
public sealed record ChannelDetailDto(long Seq, DateTime Ts, ChannelSnap Channel);

public sealed record EventsPageDto(int SeqEpoch, long EventSeqHead, long AfterSeq, int Count, bool More, IReadOnlyList<EventRecord> Items);

public sealed record AlarmsDto(int Count, AlarmDto[] Items);

// —— §6.7 Settings ——
public sealed record LimitsDto(double TempSpMaxC, double PressureSpMaxPsi, double RpmMax, double VolumeMaxMl);
public sealed record AlarmSettingsDto(double OverPressurePsi, double OverTempC, bool StopHeatOnOverPressure, double HeatTimeoutMin, double PressDeviationPsi);
public sealed record LeakTestSettingsDto(double TestPsi, double SettleMin, double MeasureMin, double RateLimitPsiPerHr, double ReminderDays, DateTime? LastTestUtc);
public sealed record ApproachSettingsDto(double DeltaC, double RateCPerMin, double FastRateCPerMin, double FinalZoneC, double SettleBandC, double SettleSec);
public sealed record StirSettingsDto(double PeakCurrentA, double StandbyPct, double RampMsPer1000Rpm, double Microstep);
public sealed record PressureSettingsDto(double FullScaleMPa);
public sealed record SettingsNotesDto(string[] Unused);
public sealed record SettingsDto(LimitsDto Limits, AlarmSettingsDto Alarms, LeakTestSettingsDto LeakTest, ApproachSettingsDto Approach,
    StirSettingsDto Stir, PressureSettingsDto Pressure, TempBandSnap[] TempBands, SettingsNotesDto Notes, long Revision)
{
    public static readonly string[] UnusedKeys = { "heatTimeoutMin", "pressDeviationPsi", "reminderDays", "pressureSpMaxPsi" };

    public static SettingsDto From(SettingsSnap s) => new(
        new LimitsDto(s.TMaxSp, s.PMaxSp, s.RpmMax, s.VolMax),
        new AlarmSettingsDto(s.OverPressure, s.OverTemp, s.StopHeatOnOverPressure, s.HeatTimeout, s.PressDeviation),
        new LeakTestSettingsDto(s.LeakTestPsi, s.LeakSettleMin, s.LeakMeasureMin, s.LeakRate, s.LeakReminderDays, s.LastLeakTestUtc),
        new ApproachSettingsDto(s.ApproachDelta, s.ApproachRate, s.ApproachFastRate, s.ApproachFinalZone, s.ApproachSettleBand, s.ApproachSettleSec),
        new StirSettingsDto(s.StirCurrent, s.StirStandbyPct, s.StirRampMs, s.StirMicrostep),
        new PressureSettingsDto(s.PressFullScaleMPa),
        s.TempBands,
        new SettingsNotesDto(UnusedKeys),
        s.Revision);
}

// —— §7.2 /preflight ——
public sealed record PreflightCheckDto(string Kind, string Title, string Detail);
public sealed record PreflightDto(bool CanRun, string Summary, PreflightCheckDto[] Checks, int[] Channels);

// —— §6.9 /program-schema ——
public sealed record FieldSchemaDto(string Key, string Label, string Kind, string? Unit, double? Default, double? Min, double? Max,
    double? DeclaredMin, double? DeclaredMax, SchemaOption[]? Options, bool Available, string? Reason,
    FieldSchemaDto[]? ItemFields, int? MinItems, int? MaxItems);
public sealed record StepSchemaDto(string Type, string Label, string Chip, string Help, FieldSchemaDto[] Fields);
public sealed record ProgramSchemaDto(string ApiVersion, long SettingsRevision, string[] StepTypes, StepSchemaDto[] Steps);

// —— §7.2 /device/comm ——
public sealed record ApiStatsDto(int Clients, int MaxClients, long DroppedTelemetryFrames, long DroppedEvents, long FramesSent, long EventsSent, long AuthFailures,
    long Requests, DateTime? StartedAt, string Bind, int Port);
public sealed record LoggerStatsDto(long DroppedLines, long EventDroppedLines, long EventSeqHead, long EventRingOldest);
public sealed record CommDto(DateTime Ts, long SnapshotSeq, ModuleCommSnap? Temp, ModuleCommSnap? Pressure, ModuleCommSnap? Stir, ModuleCommSnap? Io,
    TickStats? Tick, ApiStatsDto Api, LoggerStatsDto Logger);

// —— §9 历史 ——
public sealed record FilesDto(string Type, int Count, List<HistoryFileInfo> Items);
public sealed record LeakFilesDto(string? Samples, string? SummaryCsv, string? SummaryJson);
public sealed record LeakTestInfoDto(string RunId, DateTime? Start, long Bytes, LeakFilesDto Files);
public sealed record LeakTestsDto(int Count, LeakTestInfoDto[] Items);
public sealed record SessionInfoDto(string TestId, DateTime? StartedAt, string File, long Bytes, DateTime? From, DateTime? To, string? Id = null);
public sealed record SessionsDto(int Count, SessionInfoDto[] Items, SessionSnap? Current);
/// <summary>§10.2 会话详情：当前（或历史）会话的 manifest + 该会话的文件（相对数据目录）。</summary>
public sealed record SessionDetailDto(bool Active, string Id, string? TestId, string? File, SessionManifest? Manifest, string[] Files);

// —— §7.1 写命令响应 ——
/// <summary>成功：200 confirmed / 202 accepted。result 为各 Try* 的 Data（匿名对象先转成 JsonElement，§3.2 不 Trim 允许反射）。</summary>
public sealed record CommandResponseDto(bool Ok, string OpId, string Status, JsonElement? Result, string[] Warnings, long OpEventSeq);
public sealed record CommandErrorBody(string Code, string Message, JsonElement? Detail);
public sealed record CommandErrorDto(bool Ok, string OpId, CommandErrorBody Error, long OpEventSeq);

// —— §7.2 控制权 ——
public sealed record ControlPendingDto(string RequestId, string ClientName, string TokenName, DateTime? ExpiresAt);
/// <summary>GET /control：模式、持有者、租约状态（不含 leaseId）、待处理申请、超时配置。</summary>
public sealed record ControlStateDto(string Mode, string? Holder, string? HolderToken, string State, DateTime? LeaseExpiresAt, DateTime? LeaseHardExpiresAt,
    ControlPendingDto? Pending, bool AutoGrant, int HeartbeatTimeoutSec, int LeaseGraceSec, int RequestTimeoutSec, string OnRemoteLost);
/// <summary>POST /control/request 的 result：pending（等触屏）或 granted（autoGrant，直接带 leaseId）。</summary>
public sealed record ControlRequestResultDto(string RequestId, string Result, DateTime? ExpiresAt, string? LeaseId, DateTime? LeaseExpiresAt, int HeartbeatTimeoutSec, int LeaseGraceSec, string? RequestKey = null);
/// <summary>GET /control/requests/{id}：pending / granted / denied / expired；leaseId 只对申请令牌给出。</summary>
public sealed record ControlRequestStatusDto(string RequestId, string Result, string ClientName, DateTime? ExpiresAt, string? LeaseId, DateTime? LeaseExpiresAt, int HeartbeatTimeoutSec, int LeaseGraceSec);
public sealed record HeartbeatResultDto(string Mode, bool Resumed, DateTime? LeaseExpiresAt, DateTime? LeaseHardExpiresAt, int HeartbeatTimeoutSec);
public sealed record TelemetryRangeInfoDto(DateTime From, DateTime To, string[] Files);

// —— §8 WebSocket ——
public sealed record HelloMsg(string Type, string ApiVersion, DateTime ServerTime, bool? ClockSynced, string BootId, bool Simulated,
    int SeqEpoch, long EventSeqHead, long TelemetrySeqHead, ModeSnap Control, int EventRingSize);
public sealed record PongMsg(string Type, DateTime Ts);
public sealed record GapMsg(string Type, string Reason, long EventSeqHead, long OldestBufferedSeq);
public sealed record WsErrorMsg(string Type, string Code, string Message);
public sealed record SubscribedMsg(string Type, string[] Topics, int Replayed, long EventSeqHead);
public sealed record AlarmMsg(string Type, AlarmDto Alarm);
/// <summary>§8.2 control 消息：模式 / 租约迁移。leaseId 恒为 null（不进 WS 广播；申请方从 HTTP 响应 / GET /control/requests/{id} 取），字段保留以兼容客户端。</summary>
public sealed record ControlMsg(string Type, string Event, string Mode, string? Holder, string State, string? RequestId, string? Result,
    string? LeaseId, DateTime? LeaseExpiresAt, int HeartbeatTimeoutSec, string Detail, DateTime Ts);
/// <summary>§8.2 opEvent：一条远程写命令的结局（与 HTTP 响应同内容，便于其他客户端 / 触屏侧工具观察）。</summary>
public sealed record OpEventMsg(string Type, string OpId, string Action, string Client, string Source, int HttpStatus, string Status, string Code, string Message, long OpEventSeq, DateTime Ts);
public sealed record EventMsg(string Type, long Seq, int SeqEpoch, DateTime Ts, string TsLocal, string Level, string Kind, int? Channel,
    string Source, string Origin, string? Client, string? OpId, string Title, string Message)
{
    public static EventMsg From(EventRecord r) => new("event", r.Seq, r.SeqEpoch, r.Ts, r.TsLocal, r.Level, r.Kind, r.Channel,
        r.Source, r.Origin, r.Client, r.OpId, r.Title, r.Message);
}
