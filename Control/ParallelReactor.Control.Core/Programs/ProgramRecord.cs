using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 步骤类型（显示用，样稿 PR.STEP 的键 + 程序步骤 v0.8，research/fusion-port/notes/STEPS-SPEC.md）：
/// v0.8 的九种 Leak / Exchange / Stir / Heat / Press / Hold / React / Vent / End；Purge / Wait 是旧格式，只在显示旧程序时出现
/// （较早的 HMI 正在执行的程序、旧的程序台账 —— 那里步骤序号要和设备对得上，不转换，见 <see cref="ProgramDocs"/>）。
/// 没有降温步骤（用户 2026-11-19 决定）：要等凉了再放空，用 Hold「温度 ≤ x」。
/// </summary>
public enum StepKind { Heat, Press, React, End, Purge, Wait, Stir, Leak, Exchange, Hold, Vent }

/// <summary>预计时长的标注（STEPS-SPEC §5）：None = 按时长；About = 按条件结束、只是估计（写「约」）；UpTo = 最长时间（反应，写「≤」）。</summary>
public enum StepMark { None, About, UpTo }

/// <summary>步骤结束条件的种类：按时间 / 温度 / 压力。</summary>
public enum EndKind { Time, Temp, Press }

/// <summary>
/// 程序状态（API.md §3.1 Program.status）：运行中、报警暂停（held，样稿）、已下发待运行、已结束、已中止；
/// Failed =「报警中止」，只给真机用（HMI 报警时中止该通道的程序，见 PORT-PLAN §3.5），演示数据不会出现。
/// </summary>
public enum ProgramStatus { Running, Held, Loaded, Done, Aborted, Failed }

/// <summary>步骤结束条件：{ kind, op, v }。Op 只在温度 / 压力条件里有（'≥' '≤' '<' '>'）；压力 V 的单位是 MPa。</summary>
public sealed record StepEnd(EndKind Kind, double V, string? Op = null);

/// <summary>
/// 程序的一步（样稿 API.md §3.1 Step，压力一律 MPa）。可空字段按样稿：没有就是 null。
/// <see cref="EstMin"/> 是预估分钟数（0 = 未知，计时按 60 s 计，同样稿 stepPlanS 的 <c>estMin || 1</c>）。
/// 另外两个字段只在由 HMI 程序（<see cref="ProgramDocs"/>）转换来的步骤上有：<see cref="HeatCurve"/>（升温方式 = 曲线）、
/// <see cref="Regulate"/>（升压后稳压开 / 关）；样稿的步骤上都是默认值，步骤卡片按样稿写法显示。
/// </summary>
public sealed record ProgramStep(StepKind Type)
{
    /// <summary>温度设定值 ℃（升温 / 降温目标；其它步骤 = 保持的温度）。</summary>
    public double? Sp { get; init; }
    /// <summary>目标压力 MPa（充压 / 反应补压 / 吹扫充至；气密 = 试验压力，置换 = 每次充到，保持 = 压力条件，放空 = 放到）。</summary>
    public double? Target { get; init; }
    /// <summary>气路：型号表里的键（'a' / 'h2' / 'n2' / 'inert' …），显示名用 ModelProfile.GasName。</summary>
    public string? Gas { get; init; }
    /// <summary>步骤时长（分钟）：反应（最长时间）/ 等待 / 保持（按时间）/ 旧的定时搅拌 / 气密（静置 + 保压）。</summary>
    public double? Minutes { get; init; }
    /// <summary>转速设定 rpm（样稿每步都有；真机程序只有搅拌步骤有）。</summary>
    public double? Rpm { get; init; }
    /// <summary>结束步骤：先降温至 ℃。</summary>
    public double? CoolTo { get; init; }
    /// <summary>结束步骤：是否泄压（排空）。null = 没写。</summary>
    public bool? Vent { get; init; }
    /// <summary>吹扫：置换次数；置换：次数（0 = 只吹总管）；放空：之后惰性置换的次数。</summary>
    public int? Cycles { get; init; }
    /// <summary>结束条件；null =「立即」。</summary>
    public StepEnd? End { get; init; }
    /// <summary>预估分钟数（时间轴分段、剩余时间）；0 = 未知。</summary>
    public double EstMin { get; init; }
    /// <summary>步骤名（融合版：程序库里用户起的名字「恒温反应」、HMI 程序步骤的 name）；null = 按类型（StepText.Name）。</summary>
    public string? Name { get; init; }
    /// <summary>HMI heat.mode = curve（曲线升温）。</summary>
    public bool HeatCurve { get; init; }
    /// <summary>HMI press.regulate：true = 稳压保持，false = 到目标后保持；null = 样稿步骤（写「到压后保持」）。</summary>
    public bool? Regulate { get; init; }
    /// <summary>预计时长的标注（v0.8：保持按条件 =「约」，反应 =「≤」）。</summary>
    public StepMark Mark { get; init; }
    /// <summary>保持（按温度 / 压力）：最长等待 min；没有为 null。</summary>
    public double? MaxMin { get; init; }
    /// <summary>反应（v0.8）另外的结束条件：「吸气停止」「吸气量 ≥ 5.00 mmol」；没有为 null。</summary>
    public string? Ends { get; init; }
    /// <summary>置换：先吹总管的次数（lineFlush）；放空：之后惰性置换的次数用 <see cref="Cycles"/>。</summary>
    public int? LineFlush { get; init; }
}

/// <summary>已完成的一步：步骤序号（0 起）+ 开始 / 结束时刻（样稿 Program.log 的 { i, t0, t1 }）。</summary>
public readonly record struct StepLogEntry(int I, DateTime T0, DateTime T1);

/// <summary>程序库里的一条（样稿 ProgramDef：id、名称、说明、步骤）。</summary>
public sealed record ProgramDef(string Id, string Name, string Note, IReadOnlyList<ProgramStep> Steps);

/// <summary>
/// 程序记录的来源：演示数据 / PC 端程序台账（本机下发并匹配上的程序，M5）/ 设备上的程序（融合版 F3：不是本机下发的，
/// 步骤取自 HMI 的 GET /channels/{id}/program/run —— 下发时冻结的步骤，与帧里这一次执行的启动号 + 开始时刻对得上才用，见 HmiRunPrograms）。
/// </summary>
public enum ProgramSource { Demo, Ledger, Hmi }

/// <summary>
/// 一个反应釜上的程序及其进度（样稿 API.md §3.1 Program）：程序 + 步骤 + 当前步 + 计时。不可变：状态变化时整条换掉（<c>with</c>）。
/// 演示数据由 DemoFleet 生成；真机由 PC 端的程序台账 + 步骤计时给出（M5：只对本机下发并匹配上的程序，见 PORT-PLAN §3.2 第 4–6 条）。
/// 读数文字、剩余时间、时间轴分段一律经 <see cref="Progress"/> / <see cref="StepText"/> 计算，不在界面里各算各的。
/// 时间（StartedAt 等）是本地时间（DateTimeKind.Local / Unspecified）或 UTC，同一条记录内一致即可；Progress 的 now 要与它同一种。
/// </summary>
public sealed record ProgramRecord
{
    /// <summary>程序库 id（'h2-std' …）；真机台账里可能没有。</summary>
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Note { get; init; }
    public IReadOnlyList<ProgramStep> Steps { get; init; } = Array.Empty<ProgramStep>();
    /// <summary>当前步（0 起）。已结束时是最后一步。</summary>
    public int StepIndex { get; init; }
    public ProgramStatus Status { get; init; }
    /// <summary>程序开始时刻；已下发未运行为 null。</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>当前步开始时刻；已下发未运行为 null。</summary>
    public DateTime? StepStartedAt { get; init; }
    /// <summary>当前步的预计时长（秒）。</summary>
    public double StepDurS { get; init; }
    /// <summary>结束（或中止）时刻。</summary>
    public DateTime? EndedAt { get; init; }
    /// <summary>已完成的步骤（不含当前步）。</summary>
    public IReadOnlyList<StepLogEntry> Log { get; init; } = Array.Empty<StepLogEntry>();
    public ProgramSource Source { get; init; } = ProgramSource.Demo;
    /// <summary>
    /// 真机：这次执行是在本机下发之后由触屏启动的（台账照规则认它，见 ProgramLedger 的「剩余的风险」）—— 程序面板的状态行写明「触屏启动 · 按本机下发的程序显示」。
    /// 演示数据与本机启动的为 false。
    /// </summary>
    public bool StartedByTouch { get; init; }
    /// <summary>
    /// 真机：本机下发之后触屏端接管过控制（ProgramLedger 的 LedgerEntry.LocalControl），这次执行是在那之后开始的、或还没跑（已下发 · 待运行）——
    /// 设备上的程序可能已修改：程序面板的状态行写「触屏接管过控制」，悬停写全（ProgramPanelText）。触屏启动的照旧写「触屏启动」（两样不同时写）。演示数据为 false。
    /// </summary>
    public bool AfterLocalControl { get; init; }

    /// <summary>
    /// 反应步骤的结束原因（v0.8，STEPS-SPEC §1.7：「最长时间到」「吸气量达到 5.00 mmol」「吸气停止（30 min &lt; 0.005 mmol/min）」）：
    /// 真机取自 GET …/program/run 的 endReason（设备程序，见 HmiRunPrograms）；没有为 null。已结束的程序在状态行写出来。
    /// </summary>
    public string? EndReason { get; init; }

    /// <summary>当前步；越界为 null。</summary>
    public ProgramStep? CurrentStep => StepIndex >= 0 && StepIndex < Steps.Count ? Steps[StepIndex] : null;

    /// <summary>正在执行（运行中或报警暂停）：占用反应釜，停止 / 中止才能下发新程序。</summary>
    public bool IsActive => Status is ProgramStatus.Running or ProgramStatus.Held;
}
