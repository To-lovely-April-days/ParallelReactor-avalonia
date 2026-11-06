using System.Collections.Generic;
using ParallelReactor.Commands;

namespace ParallelReactor.Models;

/// <summary>程序下发守卫的输入（下发时、确认时各取一次实时状态）。</summary>
public readonly record struct ProgramRunInput
{
    /// <summary>已选中目标通道。</summary>
    public bool HasChannel { get; init; }
    /// <summary>已有程序在执行（本机同一时间只执行一个程序）。</summary>
    public bool ProgramRunning { get; init; }
    public int StepCount { get; init; }
    /// <summary>目标通道状态。</summary>
    public ReactorState State { get; init; }
    public bool OverTempTripped { get; init; }
    public bool OverPressureTripped { get; init; }
    /// <summary>急停锁定（含快速路径已锁、UI 流程尚未跑到）。</summary>
    public bool EstopLocked { get; init; }
    /// <summary>气路被其他自动序列占用（泄漏测试等）。</summary>
    public bool GasBusy { get; init; }
    public string GasTask { get; init; }
    /// <summary>程序含气路步骤（吹扫 / 升压 / 结束收尾）。</summary>
    public bool NeedsGas { get; init; }
    /// <summary>气路公共检查的拒绝原因（GasBlockReason；仅 NeedsGas 时生效）。</summary>
    public string? GasBlock { get; init; }
    /// <summary><see cref="GasBlock"/> 的 §11 错误码（GasGuard 给出；为空时按 GAS_BUSY）。</summary>
    public string? GasBlockCode { get; init; }
    /// <summary>参数超出全局限值的违规项（ProgramLimits.Validate）。</summary>
    public IReadOnlyList<string>? LimitErrors { get; init; }
}

/// <summary>
/// 程序下发守卫（C3，纯逻辑）：「下发」打开确认框时与「确认运行」时都按同一顺序重判，任一项不满足即拒绝。
/// 顺序：未选通道 → 已有程序在执行 → 空程序 → 急停 → 报警 / 超温超压未解除 → 状态不是已启用 / 已结束 →
/// 气路被占用（不论程序是否含气路步骤）→ 气路公共检查（仅含气路步骤时）→ 参数超限（报第一条）。
/// </summary>
public static class ProgramRunGuard
{
    /// <summary>拒绝原因（中文）：null = 可运行。与 <see cref="Check"/> 同一判定。</summary>
    public static string? Reason(in ProgramRunInput i) => Check(i)?.Message;

    /// <summary>同 <see cref="Reason"/>，带 §11 错误码（命令层用）。</summary>
    public static (string Code, string Message)? Check(in ProgramRunInput i)
    {
        if (!i.HasChannel) return (OpCodes.ChannelNotFound, "当前未选中通道（先点上方通道条里的 RV）");
        if (i.ProgramRunning) return (OpCodes.ProgramRunning, "已有程序在执行 · 请等待完成或先中止");
        if (i.StepCount <= 0) return (OpCodes.ProgramInvalid, "空程序无法下发 · 请先从左侧步骤库添加步骤");
        if (i.EstopLocked) return (OpCodes.EstopActive, "急停状态下不可下发程序 · 请先复位");
        if (i.State == ReactorState.Alarm || i.OverTempTripped || i.OverPressureTripped)
            return (OpCodes.ChannelAlarm, "该通道存在报警或超温 / 超压未解除 · 请先处理并复位");
        if (i.State is not (ReactorState.Ready or ReactorState.Done))
            return i.State == ReactorState.Idle
                ? (OpCodes.ChannelStateInvalid, "该通道已停用 · 请先启用")
                : (OpCodes.ChannelBusy, "该通道正在运行，不能下发程序");
        if (i.GasBusy) return (OpCodes.GasBusy, $"{(string.IsNullOrEmpty(i.GasTask) ? "气路序列" : i.GasTask)}进行中，请等待完成或先停止");
        if (i.NeedsGas && i.GasBlock is { } g) return (i.GasBlockCode ?? OpCodes.GasBusy, g);
        if (i.LimitErrors is { Count: > 0 } e)
            return (OpCodes.ParamOutOfRange, e.Count == 1 ? e[0] : $"{e[0]}（另有 {e.Count - 1} 项超限）");
        return null;
    }
}
