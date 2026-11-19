namespace ParallelReactor.Models;

/// <summary>
/// 程序步骤类型（独立成文件：纯逻辑单测可直接链接，不拉入 Avalonia）。
/// v0.8（research/fusion-port/notes/STEPS-SPEC.md，用户 2026-11-19 批准）：新程序只用
/// Leak / Exchange / Stir / Heat / Press / Hold / React / Vent / End 九种（<see cref="StepTypeCodes.All"/>）；
/// Purge / Wait 是旧格式，只在输入时认，读进来立即转换（STEPS-SPEC §3），引擎不执行。追加在末尾，保持旧值的序号不变。
/// </summary>
public enum StepType { Purge, Heat, Press, Stir, Wait, React, End, Leak, Exchange, Hold, Vent }
