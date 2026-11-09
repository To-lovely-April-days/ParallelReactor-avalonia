using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 步骤类型（样稿 PR.STEP 的键，API.md §3.1 Step.type）：设备的 7 种 + 显示旧程序 / 有冷却水型号用的 cool。
/// HMI 的 schema 没有 cool（冷却写在 end.cool），真机程序不会出现 Cool（PORT-PLAN §3.5）。
/// </summary>
public enum StepKind { Heat, Press, React, Cool, End, Purge, Wait, Stir }

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
    /// <summary>目标压力 MPa（充压 / 反应补压 / 吹扫充至）。</summary>
    public double? Target { get; init; }
    /// <summary>气路：型号表里的键（'a' / 'h2' / 'n2' / 'inert' …），显示名用 ModelProfile.GasName。</summary>
    public string? Gas { get; init; }
    /// <summary>步骤时长（分钟）：反应 / 等待 / 搅拌。</summary>
    public double? Minutes { get; init; }
    /// <summary>转速设定 rpm（样稿每步都有；真机程序只有搅拌步骤有）。</summary>
    public double? Rpm { get; init; }
    /// <summary>结束步骤：先降温至 ℃。</summary>
    public double? CoolTo { get; init; }
    /// <summary>结束步骤：是否泄压（排空）。null = 没写。</summary>
    public bool? Vent { get; init; }
    /// <summary>吹扫：置换次数。</summary>
    public int? Cycles { get; init; }
    /// <summary>结束条件；null =「立即」。</summary>
    public StepEnd? End { get; init; }
    /// <summary>预估分钟数（时间轴分段、剩余时间）；0 = 未知。</summary>
    public double EstMin { get; init; }
    /// <summary>HMI heat.mode = curve（曲线升温）。</summary>
    public bool HeatCurve { get; init; }
    /// <summary>HMI press.regulate：true = 稳压保持，false = 到目标后保持；null = 样稿步骤（写「到压后保持」）。</summary>
    public bool? Regulate { get; init; }
}

/// <summary>已完成的一步：步骤序号（0 起）+ 开始 / 结束时刻（样稿 Program.log 的 { i, t0, t1 }）。</summary>
public readonly record struct StepLogEntry(int I, DateTime T0, DateTime T1);

/// <summary>程序库里的一条（样稿 ProgramDef：id、名称、说明、步骤）。</summary>
public sealed record ProgramDef(string Id, string Name, string Note, IReadOnlyList<ProgramStep> Steps);

/// <summary>程序记录的来源：演示数据 / PC 端程序台账（本机下发并匹配上的程序，M5）。</summary>
public enum ProgramSource { Demo, Ledger }

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

    /// <summary>当前步；越界为 null。</summary>
    public ProgramStep? CurrentStep => StepIndex >= 0 && StepIndex < Steps.Count ? Steps[StepIndex] : null;

    /// <summary>正在执行（运行中或报警暂停）：占用反应釜，停止 / 中止才能下发新程序。</summary>
    public bool IsActive => Status is ProgramStatus.Running or ProgramStatus.Held;
}
