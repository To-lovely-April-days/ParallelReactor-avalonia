namespace ParallelReactor.Models;

/// <summary>程序步骤类型，对应 HTML STEP_DEFS 的 key（独立成文件：纯逻辑单测可直接链接，不拉入 Avalonia）。</summary>
public enum StepType { Purge, Heat, Press, Stir, Wait, React, End }
