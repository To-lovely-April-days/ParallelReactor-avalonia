using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.PerfTest.Results;

public enum RunStatus { Pending, Running, Paused, WaitingConfirm, WaitingEntry, WaitingSafety, Completed, Aborted, Failed }
public enum StepStatus { Pending, Running, WaitingConfirm, WaitingEntry, Done, Skipped, Failed, Aborted, TimedOut }
/// <summary>报告「结论」列：完成 / 中止 / 跳过 / 超时；合格与否只在方案给硬性判据时另填 Verdict。</summary>
public enum ItemOutcome { Pending, Completed, Aborted, Skipped, Failed }
/// <summary>判定：NotJudged（摸底默认）/ Pass / Fail / Info。</summary>
public enum Verdict { NotJudged, Pass, Fail, Info }

/// <summary>运行环境记录（§0.5 公共记录；开场人工录入或由界面填）。</summary>
public sealed class RunEnvironment
{
    public double? AmbientTempC { get; set; }
    public double? HumidityPct { get; set; }
    public double? AtmPressureKpa { get; set; }
    public string? AssemblyNo { get; set; }
    public string? MediumAndMassG { get; set; }
    public double? SupplyVoltageV { get; set; }
    public string? Note { get; set; }
}

/// <summary>运行请求。</summary>
public sealed class RunRequest
{
    public string PlanId { get; set; } = "";
    public List<string> ItemIds { get; set; } = new();
    /// <summary>选择的目标通道（对 ChannelSelectable 项生效）。</summary>
    public List<int> Channels { get; set; } = new() { 1, 2, 3, 4 };
    public string Operator { get; set; } = "";
    public string SoftwareVersion { get; set; } = "pc-1.0";
    public RunEnvironment Environment { get; set; } = new();
    /// <summary>输出根目录（&lt;PR_CONTROL_DATA&gt;/perf）；null = 默认。</summary>
    public string? OutputRoot { get; set; }
    /// <summary>会话 testId 前缀，默认 PT。</summary>
    public string TestIdPrefix { get; set; } = "PT";
    /// <summary>不建会话（HMI 无会话功能或操作员拒绝）。</summary>
    public bool NoSession { get; set; }
    /// <summary>运行编号（默认 PT-yyyyMMdd-HHmmss）。</summary>
    public string? RunId { get; set; }
    /// <summary>§0.4 前置检查表已在本次会话早先的运行中通过（界面记录）；为 false 且本次未选 / 未通过 R0 时，所有带热 / 带压项被引擎跳过。</summary>
    public bool R0SignedOff { get; set; }
}

public sealed class MeasureValue
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Formula { get; set; } = "";
    /// <summary>通道（跨通道 / 全局指标为 null）。</summary>
    public int? Channel { get; set; }
    /// <summary>重复迭代序号（1 起）；非重复项为 null。</summary>
    public int? Iteration { get; set; }
    public double? IterationValue { get; set; }
    public double Value { get; set; }
    public int SampleCount { get; set; }
    public string? Reference5 { get; set; }
    public string Direction { get; set; } = "none";
    public string? Note { get; set; }
}

public sealed class ManualEntryRecord
{
    public DateTime At { get; set; }
    public string ItemId { get; set; } = "";
    public string StepId { get; set; } = "";
    public int? Iteration { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Instrument { get; set; } = "";
    public int? Channel { get; set; }
    public int Index { get; set; }
    public double? Value { get; set; }
    public string? Text { get; set; }
}

public sealed class CommandRecord
{
    public DateTime At { get; set; }
    public string Op { get; set; } = "";
    public int? Channel { get; set; }
    public string? Args { get; set; }
    public bool Ok { get; set; }
    public string? OpId { get; set; }
    public string Status { get; set; } = "";
    public int HttpStatus { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public long OpEventSeq { get; set; }
}

public sealed class StepResult
{
    public string StepId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Iteration { get; set; }
    public StepStatus Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>实际耗时（秒，不含暂停）。</summary>
    public double DurationSec { get; set; }
    public double PausedSec { get; set; }
    public string? Note { get; set; }
    public List<CommandRecord> Commands { get; set; } = new();
    /// <summary>检查表勾选 / 签名。</summary>
    public List<bool>? Checked { get; set; }
    public string? Signer1 { get; set; }
    public string? Signer2 { get; set; }
    /// <summary>Compare 的实际值 / 期望。</summary>
    public double? CompareActual { get; set; }
    public string? CompareExpected { get; set; }
    public bool? ComparePassed { get; set; }
}

public sealed class ItemResult
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public List<int> Channels { get; set; } = new();
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public ItemOutcome Outcome { get; set; }
    public Verdict Verdict { get; set; } = Verdict.NotJudged;
    public string? HardCriterion { get; set; }
    public string? Note { get; set; }
    public List<StepResult> Steps { get; set; } = new();
    public List<MeasureValue> Measures { get; set; } = new();
    public List<ManualEntryRecord> ManualEntries { get; set; } = new();
    /// <summary>操作员备注（确认 / 录入时附带）。</summary>
    public List<string> OperatorNotes { get; set; } = new();
    /// <summary>本项期间采样帧数。</summary>
    public int FrameCount { get; set; }
    /// <summary>GET /device/comm 摘要（RecordComm 项）：开始 / 结束时各模块 polls / fails。</summary>
    public string? CommStart { get; set; }
    public string? CommEnd { get; set; }
}

public sealed class RunEventRecord
{
    public DateTime At { get; set; }
    public string Kind { get; set; } = "";
    public string? ItemId { get; set; }
    public string? StepId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class RunResult
{
    public string RunId { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string PlanVersion { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public string Operator { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public RunEnvironment Environment { get; set; } = new();
    public List<int> Channels { get; set; } = new();
    public List<string> SelectedItems { get; set; } = new();
    // —— 设备 ——
    public string? DeviceModel { get; set; }
    public string? DeviceSerial { get; set; }
    public string? HmiVersion { get; set; }
    public string? ApiVersion { get; set; }
    public string? BootId { get; set; }
    public bool Simulated { get; set; }
    public bool? ClockSynced { get; set; }
    public string? SessionId { get; set; }
    public string? SessionTestId { get; set; }
    /// <summary>开场 GET /settings 的 JSON（§0.5 参数冻结）。</summary>
    public JsonElement? SettingsSnapshot { get; set; }
    public List<ItemResult> Items { get; set; } = new();
    public List<RunEventRecord> Events { get; set; } = new();
    public string? AbortReason { get; set; }
    public int TotalFrames { get; set; }
    /// <summary>输出目录（绝对路径）。</summary>
    public string? OutputDir { get; set; }
    public List<string> RawFiles { get; set; } = new();

    public TimeSpan Elapsed => (EndedAt ?? StartedAt) - StartedAt;
}

public static class ResultJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    public static string Serialize(RunResult r) => JsonSerializer.Serialize(r, Options);
    public static RunResult? Deserialize(string json) => JsonSerializer.Deserialize<RunResult>(json, Options);
}
