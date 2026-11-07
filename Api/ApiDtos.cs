using System;
using System.Collections.Generic;
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
public sealed record SessionInfoDto(string TestId, DateTime? StartedAt, string File, long Bytes, DateTime? From, DateTime? To);
public sealed record SessionsDto(int Count, SessionInfoDto[] Items, SessionSnap? Current);
public sealed record TelemetryRangeInfoDto(DateTime From, DateTime To, string[] Files);

// —— §8 WebSocket ——
public sealed record HelloMsg(string Type, string ApiVersion, DateTime ServerTime, bool? ClockSynced, string BootId, bool Simulated,
    int SeqEpoch, long EventSeqHead, long TelemetrySeqHead, ModeSnap Control, int EventRingSize);
public sealed record PongMsg(string Type, DateTime Ts);
public sealed record GapMsg(string Type, string Reason, long EventSeqHead, long OldestBufferedSeq);
public sealed record WsErrorMsg(string Type, string Code, string Message);
public sealed record SubscribedMsg(string Type, string[] Topics, int Replayed, long EventSeqHead);
public sealed record AlarmMsg(string Type, AlarmDto Alarm);
public sealed record EventMsg(string Type, long Seq, int SeqEpoch, DateTime Ts, string TsLocal, string Level, string Kind, int? Channel,
    string Source, string Origin, string? Client, string? OpId, string Title, string Message)
{
    public static EventMsg From(EventRecord r) => new("event", r.Seq, r.SeqEpoch, r.Ts, r.TsLocal, r.Level, r.Kind, r.Channel,
        r.Source, r.Origin, r.Client, r.OpId, r.Title, r.Message);
}
