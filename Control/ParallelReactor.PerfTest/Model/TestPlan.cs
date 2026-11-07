using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.PerfTest.Model;

// ============================================================================
// 测试方案模型（对应 test-plan/RV1-4摸底性能测试方案.md）。全部为可 JSON 往返的 POCO：
// 方案以嵌入资源 plans/*.json 给出，负责人可直接改 JSON 不必重编译；PlanValidator 做结构校验。
// 步骤用「扁平 + 按 kind 取用字段」的方式表达，不用多态，便于手写 JSON。
// ============================================================================

/// <summary>步骤种类。</summary>
public enum StepKind
{
    /// <summary>现场操作说明（触屏设定 / 接线 / 调压…），等待操作员确认。</summary>
    Instruction,
    /// <summary>检查表：逐项打勾（如 §0.4 前置检查表 13 项），可要求两人签字。</summary>
    Checklist,
    /// <summary>人工录入仪器读数（PT100 / 标准表 / 转速表 / 示波器…）。</summary>
    ManualEntry,
    /// <summary>通过接口下发写命令（启用 / 加热 / 搅拌 / 程序 / 泄漏测试 / 停止…）。</summary>
    Command,
    /// <summary>等待信号稳定：窗口内带内 + 斜率限制，带超时。</summary>
    WaitStable,
    /// <summary>定时记录（保温 / 保压 / 静置），只采样。</summary>
    Hold,
    /// <summary>按公式对本项已采样数据 / 人工录入做计算。</summary>
    Measure,
    /// <summary>读取服务端设置并与期望比较（本方案只有少数硬性判据）。</summary>
    Compare,
    /// <summary>开始会话（引擎在运行级别自动做；也允许方案显式写）。</summary>
    SessionBegin,
    /// <summary>向会话插入标记。</summary>
    Marker,
    /// <summary>结束会话。</summary>
    SessionEnd,
}

/// <summary>整份方案。</summary>
public sealed class TestPlan
{
    public string PlanId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>方案文档路径或名称（报告附录引用）。</summary>
    public string Source { get; set; } = "";
    public List<TestStage> Stages { get; set; } = new();

    public IEnumerable<TestItem> AllItems()
    {
        foreach (var s in Stages) foreach (var i in s.Items) yield return i;
    }

    public TestItem? FindItem(string id)
    {
        foreach (var i in AllItems()) if (string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

/// <summary>阶段（§2：1 功能与安全 … 7 可选）。</summary>
public sealed class TestStage
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    /// <summary>二期阶段（6/7）整体禁用。</summary>
    public bool Phase2 { get; set; }
    public List<TestItem> Items { get; set; } = new();
}

/// <summary>测试项（F1 / S2 / P3 / T1 / C2-a …）。</summary>
public sealed class TestItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>目的 / 内容一句话。</summary>
    public string Purpose { get; set; } = "";
    /// <summary>方案出处，如「§2 阶段3 P2」。</summary>
    public string SourceRef { get; set; } = "";
    /// <summary>分组标题（界面清单用），默认同阶段名。</summary>
    public string Group { get; set; } = "";
    /// <summary>默认目标通道（1..8）。</summary>
    public List<int> TargetChannels { get; set; } = new() { 1, 2, 3, 4 };
    /// <summary>是否允许操作员改目标通道（全局搅拌类为 false）。</summary>
    public bool ChannelSelectable { get; set; } = true;
    /// <summary>前置条件（文字，含其他项 ID，如 "R0"、"S2"）。</summary>
    public List<string> Preconditions { get; set; } = new();
    /// <summary>所需仪器。</summary>
    public string Instrument { get; set; } = "";
    /// <summary>预计用时（分钟），用于 ETA。</summary>
    public double EstimatedMinutes { get; set; }
    /// <summary>false = 二期 / 暂不可选（界面灰显，右侧标「二期」）。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>界面清单右侧的小字（「自动 · 8 min」「手动确认」「转速表读数」）。</summary>
    public string Hint { get; set; } = "";
    /// <summary>重复执行（如 S1 的转速档、T1 的温度档）。</summary>
    public RepeatSpec? Repeat { get; set; }
    public List<TestStep> Steps { get; set; } = new();
    /// <summary>§4 记录表字段（报告的记录表列）。</summary>
    public List<RecordField> RecordFields { get; set; } = new();
    /// <summary>§5 对标参考值（只作参考，不作判定）。</summary>
    public List<ReferenceValue> ReferenceValues { get; set; } = new();
    /// <summary>方案给出的硬性判据说明（有则报告可填「合格 / 不合格」，否则「—（摸底）」）。</summary>
    public string? HardCriterion { get; set; }
    /// <summary>不是 Enabled 时的原因。</summary>
    public string? DisabledReason { get; set; }
    /// <summary>
    /// 是否要求 §0.4 前置检查表（R0）已通过才允许运行：null = 自动（含加热 / 自整定 / 程序运行 / 泄漏测试 / 充压命令的项为 true）；
    /// false 只用于合成自检项（smoke），正式方案不得关闭。
    /// </summary>
    public bool? RequiresPrecheck { get; set; }
    /// <summary>项开始 / 结束时读 GET /device/comm 记录各模块轮询 / 失败计数（F1 / F6）。</summary>
    public bool RecordComm { get; set; }

    /// <summary>§0.4：任何加热 / 充压 / 自整定 / 程序运行 / 泄漏测试命令都算「带热 / 带压」。</summary>
    public bool NeedsPrecheck()
    {
        if (RequiresPrecheck.HasValue) return RequiresPrecheck.Value;
        foreach (var s in Steps)
            if (s.Kind == StepKind.Command && s.Command != null && CommandOps.IsHeatOrPressure(s.Command.Op)) return true;
        return false;
    }
}

/// <summary>重复规格：Count 次，或对 Values 的每个值一次（步骤文字 / 参数里 {value} 被替换）。</summary>
public sealed class RepeatSpec
{
    public int Count { get; set; } = 1;
    /// <summary>变量名（如 rpm / tempC / mpa），仅作说明。</summary>
    public string? Variable { get; set; }
    public List<double>? Values { get; set; }
    public string? Unit { get; set; }

    public int Iterations => Values is { Count: > 0 } ? Values.Count : Math.Max(1, Count);
}

/// <summary>一步。按 Kind 取用对应字段，其他字段为 null。</summary>
public sealed class TestStep
{
    public string Id { get; set; } = "";
    public StepKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>说明 / 提示文字（支持 {rv} {value} {item} 模板）。</summary>
    public string? Text { get; set; }
    /// <summary>目标通道：null = 本项目标通道；[] = 全局（不按通道）；否则显式列表。</summary>
    public List<int>? Channels { get; set; }
    /// <summary>Command / WaitStable 是否按每个通道分别执行；false = 整体一次（搅拌 / 急停 / 会话）。</summary>
    public bool PerChannel { get; set; } = true;
    /// <summary>该步失败（命令被拒 / 超时 / 操作员拒绝）时：abortItem（默认）/ continue / skipItem。</summary>
    public string OnFail { get; set; } = "abortItem";

    // —— Checklist ——
    public List<string>? ChecklistItems { get; set; }
    public bool TwoSigners { get; set; }

    // —— ManualEntry ——
    public List<ManualField>? Fields { get; set; }

    // —— Command ——
    public CommandSpec? Command { get; set; }

    // —— WaitStable / Hold ——
    /// <summary>tempC / pressurePsi / pressureRaw / heatOutputPct / rpm。</summary>
    public string? Signal { get; set; }
    /// <summary>带宽（±Band），单位同信号。</summary>
    public double? Band { get; set; }
    /// <summary>窗口内允许的最大斜率（单位/min），null 不限。</summary>
    public double? MaxSlopePerMin { get; set; }
    public double? WindowSec { get; set; }
    public double? TimeoutSec { get; set; }
    /// <summary>参考：sp（通道温度设定值）/ value（Value 字段）/ manual:&lt;key&gt;。</summary>
    public string? Reference { get; set; }
    public double? Value { get; set; }
    /// <summary>Hold 时长（秒）。</summary>
    public double? DurationSec { get; set; }
    /// <summary>Hold 时额外关注的信号列表（只作说明；原始帧全部记录）。</summary>
    public List<string>? Signals { get; set; }

    // —— Measure ——
    public List<MetricSpec>? Metrics { get; set; }

    // —— Compare ——
    public CompareSpec? Compare { get; set; }

    // —— Marker / Session ——
    /// <summary>标记文字 / 会话 testId 模板。</summary>
    public string? Template { get; set; }
}

/// <summary>人工录入字段。</summary>
public sealed class ManualField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Instrument { get; set; } = "";
    /// <summary>重复读数次数（如转速表读 3 次）。</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>按通道分别录入（每个目标通道一组）。</summary>
    public bool PerChannel { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>文字型录入（现象 / 状态），不做数值校验。</summary>
    public bool IsText { get; set; }
    public bool Optional { get; set; }
    /// <summary>录入值回填到运行环境记录（报告头 §0.5）：ambientTempC / humidityPct / atmPressureKpa / supplyVoltageV / assemblyNo / mediumAndMassG。</summary>
    public string? EnvKey { get; set; }
}

/// <summary>写命令规格。Op 见 <see cref="CommandOps"/>。Args 为原样 JSON（数值里可用 "{value}" 模板字符串）。</summary>
public sealed class CommandSpec
{
    public string Op { get; set; } = "";
    public Dictionary<string, JsonElement>? Args { get; set; }
    /// <summary>允许的响应状态（confirmed / accepted），空 = 任何 2xx。</summary>
    public List<string>? ExpectStatus { get; set; }
    /// <summary>视为成功的错误码（如 ALREADY_RUNNING）。</summary>
    public List<string>? AllowCodes { get; set; }
}

/// <summary>引擎支持的命令操作名。</summary>
public static class CommandOps
{
    public const string Enable = "enable", Disable = "disable", StopChannel = "stopChannel";
    public const string HeatStart = "heatStart", Setpoint = "setpoint", HeatStop = "heatStop";
    public const string Stir = "stir", StirStop = "stirStop";
    public const string ProgramPut = "programPut", ProgramRun = "programRun", ProgramAbort = "programAbort";
    public const string AutotuneStart = "autotuneStart", AutotuneCancel = "autotuneCancel";
    public const string LeakTestStart = "leakTestStart", LeakTestAbort = "leakTestAbort";
    public const string Estop = "estop", StopAll = "stopAll";
    /// <summary>（重新）申请控制权：急停使租约作废后（F4）由方案显式写一步。</summary>
    public const string ControlRequest = "controlRequest";

    public static readonly string[] All =
    {
        Enable, Disable, StopChannel, HeatStart, Setpoint, HeatStop, Stir, StirStop, ProgramPut, ProgramRun, ProgramAbort,
        AutotuneStart, AutotuneCancel, LeakTestStart, LeakTestAbort, Estop, StopAll, ControlRequest,
    };

    /// <summary>§0.4 门禁：带热 / 带压类命令。</summary>
    public static bool IsHeatOrPressure(string op) => op is HeatStart or Setpoint or AutotuneStart or ProgramRun or LeakTestStart;

    /// <summary>停止类（不需要租约）。</summary>
    public static bool IsStopClass(string op) => op is Disable or StopChannel or HeatStop or StirStop or ProgramAbort or AutotuneCancel or LeakTestAbort or Estop or StopAll;

    /// <summary>全局（不按通道）。</summary>
    public static bool IsGlobal(string op) => op is Stir or StirStop or LeakTestStart or LeakTestAbort or Estop or StopAll or ControlRequest;
}

/// <summary>测量指标规格。Formula 见 <see cref="MetricFormulas"/>。</summary>
public sealed class MetricSpec
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Formula { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>信号：tempC / pressurePsi / pressureRaw / heatOutputPct / rpm；或 manual:&lt;key&gt;。</summary>
    public string? Signal { get; set; }
    /// <summary>数据窗口：step:&lt;stepId&gt;（该步执行期间）/ from:&lt;stepId&gt;（该步开始到现在，本轮）/ iteration（本轮）/ item（整项，默认）。</summary>
    public string? Window { get; set; }
    /// <summary>参考：sp / value / manual:&lt;key&gt;。</summary>
    public string? Reference { get; set; }
    public double? Value { get; set; }
    public double? Band { get; set; }
    /// <summary>带宽的百分比部分：有效带 = max(Band, BandPct% × 目标)（如 P4 的 max(1 psi, 3%)）。</summary>
    public double? BandPct { get; set; }
    /// <summary>heatingRate 的逼近 Δ（span = (SP − Δ) − T₀）；null → 用开场 GET /settings 的 approach.deltaC。</summary>
    public double? Delta { get; set; }
    /// <summary>按通道分别算（默认 true）；false 时对 4 路做跨通道比较。</summary>
    public bool PerChannel { get; set; } = true;
    /// <summary>§5 对标值文字。</summary>
    public string? Reference5 { get; set; }
    /// <summary>§3 规格草案的方向：lowerBetter / higherBetter / twoSided / none。</summary>
    public string Direction { get; set; } = "none";
}

/// <summary>指标公式名。</summary>
public static class MetricFormulas
{
    public const string Overshoot = "overshoot";          // T_peak − SP（PV）
    public const string TimeToBand = "timeToBand";        // 首次进入 SP−Band（t_r）
    public const string SettlingTime = "settlingTime";    // 进入 ±Band 且之后 ≥ 10 min 不越出（t_s）
    public const string HeatingRate = "heatingRate";      // 10–90% 区间最小二乘斜率 ℃/min
    public const string Mean = "mean";
    public const string Sigma = "sigma";
    public const string Min = "min";
    public const string Max = "max";
    public const string PeakToPeak = "p2p";               // (max−min)/2 波动度 ΔT_f
    public const string MeanDeviation = "meanDeviation";  // mean − 参考（ΔT_mean / ΔT_d / e_ij）
    public const string MaxDeviation = "maxDeviation";    // max − 参考（ΔT_max）
    public const string MinDeviation = "minDeviation";    // min − 参考（ΔT_min）
    public const string LinearSlopePerHour = "slopePerHour"; // 线性拟合斜率 /h（压力衰减 psi/h）
    public const string LinearR2 = "r2";
    public const string CrossChannelRange = "crossRange";  // 4 路均值的极差（一致性，PerChannel=false）
    public const string RpmError = "rpmError";            // (manual 均值 − 设定)/设定 ×100%
    public const string Duration = "duration";            // 某步实际耗时（秒）
    public const string TimeToValue = "timeToValue";      // 首次达到 Value（如压力目标−max(1 psi,3%)）
    public const string Count = "count";                  // 样本数

    public static readonly string[] All =
    {
        Overshoot, TimeToBand, SettlingTime, HeatingRate, Mean, Sigma, Min, Max, PeakToPeak, MeanDeviation, MaxDeviation, MinDeviation,
        LinearSlopePerHour, LinearR2, CrossChannelRange, RpmError, Duration, TimeToValue, Count,
    };
}

/// <summary>设置比较。Key 见 <see cref="SettingsKeys"/>。</summary>
public sealed class CompareSpec
{
    public string Key { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>相等期望（步骤 reference=iteration 时用轮次值）。</summary>
    public double? Expect { get; set; }
    public double Tolerance { get; set; } = 1e-6;
    /// <summary>硬性判据：不满足 → 步失败（按 OnFail）；否则仅记录。</summary>
    public bool Hard { get; set; }
    /// <summary>步骤 reference=iteration 时轮次值的用法：expect（相等，默认）/ min（实际 ≥ 轮次值）/ max（实际 ≤ 轮次值）。</summary>
    public string IterationAs { get; set; } = "expect";
}

/// <summary>可比较的设置键（GET /settings）。</summary>
public static class SettingsKeys
{
    public const string TempSpMaxC = "limits.tempSpMaxC", RpmMax = "limits.rpmMax", PressureSpMaxPsi = "limits.pressureSpMaxPsi";
    public const string OverPressurePsi = "alarms.overPressurePsi", OverTempC = "alarms.overTempC";
    public const string LeakTestPsi = "leakTest.testPsi", LeakSettleMin = "leakTest.settleMin", LeakMeasureMin = "leakTest.measureMin";
    public const string ApproachDeltaC = "approach.deltaC", ApproachRate = "approach.rateCPerMin", ApproachFastRate = "approach.fastRateCPerMin";
    public const string StirRampMs = "stir.rampMsPer1000Rpm", StirPeakCurrentA = "stir.peakCurrentA";
    public const string PressFullScaleMPa = "pressure.fullScaleMPa";
    public static readonly string[] All =
    {
        TempSpMaxC, RpmMax, PressureSpMaxPsi, OverPressurePsi, OverTempC, LeakTestPsi, LeakSettleMin, LeakMeasureMin,
        ApproachDeltaC, ApproachRate, ApproachFastRate, StirRampMs, StirPeakCurrentA, PressFullScaleMPa,
    };
}

/// <summary>§4 记录表的一列。</summary>
public sealed class RecordField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>来源：metric:&lt;key&gt; / manual:&lt;key&gt; / step:&lt;id&gt;.status / run.&lt;field&gt; / text。</summary>
    public string Source { get; set; } = "";
}

/// <summary>§5 对标参考值。</summary>
public sealed class ReferenceValue
{
    public string Metric { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>方案 JSON 的序列化选项（camelCase、枚举字符串、忽略 null）。</summary>
public static class PlanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
