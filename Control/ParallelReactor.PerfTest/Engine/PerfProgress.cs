using System;
using System.Collections.Generic;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.PerfTest.Engine;

/// <summary>等待操作员的类别。</summary>
public enum WaitingKind { None, Confirm, Entry, Safety, Paused }

/// <summary>引擎每 1 s（以及每次状态变化）推送一次的进度（界面直接绑定）。</summary>
public sealed record PerfProgress(
    string RunId,
    RunStatus Status,
    int ItemIndex, int ItemCount, string? ItemId, string? ItemTitle,
    int StepIndex, int StepCount, string? StepId, string? StepTitle, string? StepKind,
    int Iteration, int IterationCount,
    /// <summary>阶段文字：升温中 / 稳定计时 / 保温记录 / 等待确认 / 等待录入 / 已暂停 / 安全暂停 …</summary>
    string Phase,
    /// <summary>当前步骤百分比 0..100。</summary>
    double StepPercent,
    /// <summary>总进度 0..100（按已完成步数 + 当前步比例；运行中单调不减）。</summary>
    double OverallPercent,
    TimeSpan Elapsed,
    TimeSpan? Eta,
    WaitingKind Waiting,
    /// <summary>等待时的提示（文字 / 仪器 / 单位）。</summary>
    string? Prompt,
    /// <summary>倒计时（Hold 剩余 / WaitStable 已稳定秒数），null 不显示。</summary>
    TimeSpan? Countdown,
    /// <summary>目标通道的实时值（顺序同 Channels）。</summary>
    IReadOnlyList<int> Channels,
    IReadOnlyList<double?> LiveTempC,
    IReadOnlyList<double?> LivePressurePsi,
    int LiveRpm,
    bool StirOn,
    DateTime At);
