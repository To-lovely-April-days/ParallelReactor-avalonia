using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>一次程序执行的阶段（API 以小写首字母输出：preparing / running / waitingGas / finishing / completed / failed / aborted）。</summary>
public enum ProgramRunPhase { Preparing, Running, WaitingGas, Finishing, Completed, Failed, Aborted }

/// <summary>
/// 一次程序执行的纯数据（每通道一份，UI 线程读写；快照构建器 / 运行记录只读它）。
/// 引擎侧的 ViewModels/ProgramRun 持有它并在步骤推进、等待气路、收尾时更新。
/// </summary>
public sealed class ProgramRunInfo
{
    public ProgramRunInfo(int rv, int stepCount, string startedBy, DateTimeOffset startedAt)
    {
        Rv = rv;
        StepCount = stepCount;
        StartedBy = startedBy;
        StartedAt = startedAt;
    }

    public int Rv { get; }
    public int StepCount { get; }
    /// <summary>当前步骤下标（0 起；尚未开始为 −1）。</summary>
    public int Index { get; set; } = -1;
    public ProgramRunPhase Phase { get; set; } = ProgramRunPhase.Preparing;
    /// <summary>运行状态一行文字（RunStatus）。</summary>
    public string Status { get; set; } = "";
    /// <summary>监视详情（RunMonitorDetail）。</summary>
    public string Message { get; set; } = "";
    /// <summary>监视阶段文字（RunMonitorPhase：执行第 n 步 · 升温 / 程序执行完成 …）；API 旧字段 <c>program.phase</c> 过渡期沿用。</summary>
    public string PhaseText { get; set; } = "准备执行";
    /// <summary>local / 远程来源标签。</summary>
    public string StartedBy { get; }
    public DateTimeOffset StartedAt { get; }
    /// <summary>当前步骤正在排队等待气路。</summary>
    public bool WaitingGas => Phase == ProgramRunPhase.WaitingGas;
    /// <summary>等待气路时当前占用者的通道（泄漏测试占用时为 null）。</summary>
    public int? WaitingOn { get; set; }

    /// <summary>仍在执行（含准备 / 等待气路 / 收尾）。</summary>
    public bool IsActive => ProgramRunSet.IsActive(Phase);
}

/// <summary>并行程序集合的纯逻辑小件（通道 → 运行信息）。</summary>
public static class ProgramRunSet
{
    /// <summary>同一时间最多并行执行的程序数（= 通道数）。</summary>
    public const int MaxConcurrent = 8;

    public static bool IsActive(ProgramRunPhase p)
        => p is ProgramRunPhase.Preparing or ProgramRunPhase.Running or ProgramRunPhase.WaitingGas or ProgramRunPhase.Finishing;

    /// <summary>该通道是否被正在执行的程序占用。</summary>
    public static bool IsOwned(IReadOnlyDictionary<int, ProgramRunInfo> runs, int rv)
        => runs.TryGetValue(rv, out var r) && r.IsActive;

    /// <summary>正在执行的通道号（升序）。</summary>
    public static IReadOnlyList<int> RunningIds(IReadOnlyDictionary<int, ProgramRunInfo> runs)
        => runs.Values.Where(r => r.IsActive).Select(r => r.Rv).OrderBy(x => x).ToList();

    /// <summary>首个（通道号最小）正在执行的程序：旧版 API 顶层 <c>program</c> 字段用。</summary>
    public static ProgramRunInfo? FirstRunning(IReadOnlyDictionary<int, ProgramRunInfo> runs)
        => runs.Values.Where(r => r.IsActive).OrderBy(r => r.Rv).FirstOrDefault();

    /// <summary>「正在运行：RV1 RV3」；没有程序执行时为空串。</summary>
    public static string Summary(IReadOnlyDictionary<int, ProgramRunInfo> runs)
    {
        var ids = RunningIds(runs);
        return ids.Count == 0 ? "" : "正在运行：" + string.Join(" ", ids.Select(i => "RV" + i));
    }

    /// <summary>API 用的小写阶段名（preparing / waitingGas …）。</summary>
    public static string PhaseName(ProgramRunPhase p)
    {
        var s = p.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
