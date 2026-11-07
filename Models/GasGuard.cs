using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Commands;

namespace ParallelReactor.Models;

/// <summary>气路检查用的通道快照。</summary>
/// <param name="IdleOverTempAlarm">停用 / 已结束通道的超温切断报警（不阻塞气路，见 MainViewModel.IsIdleOverTempAlarm）。</param>
public readonly record struct GasChannel(int Id, ReactorState State, bool OverPressureTripped, bool IdleOverTempAlarm);

/// <summary>开始自动气路序列前公共检查的输入。</summary>
public readonly record struct GasGuardInput
{
    public bool EstopLocked { get; init; }
    /// <summary>阀门模块已配置串口（HardwareOptions.UseRealIo）。</summary>
    public bool IoConfigured { get; init; }
    /// <summary>序列是否依赖压力读数（泄漏测试要闭环充压与超压监视；吹扫不依赖）。</summary>
    public bool NeedPressure { get; init; }
    public bool AnalogConfigured { get; init; }
    public bool PressOnline { get; init; }
    /// <summary>当前占用气路的序列名，空 = 空闲。</summary>
    public string GasTask { get; init; }
    public bool ManualValve { get; init; }
    /// <summary>发起序列的程序自己的通道：它处于升压 / 反应不算「被占用」；报警仍然阻塞。</summary>
    public int? SelfId { get; init; }
    public IReadOnlyList<GasChannel> Channels { get; init; }
    /// <summary>当前气路占用者是程序（多程序并行：程序之间排队共享，不按 GAS_BUSY 拒绝）。</summary>
    public bool GasHeldByProgram { get; init; }
    /// <summary>发起方是程序的气路步骤：气路被**另一程序**占用时不拒绝（由 GasQueue 排队等待）；泄漏测试占用仍拒绝。</summary>
    public bool WaitInsteadOfBusy { get; init; }
    /// <summary>
    /// 其他正在执行程序的通道：它们处于升压 / 反应不算「正在加压 / 反应」阻塞（其 SV 由整帧写阀保证关闭，各路进气带单向阀）；
    /// 报警仍然阻塞。泄漏测试 / 手动路径不传 → 行为与原来逐字相同。
    /// </summary>
    public IReadOnlyList<int>? ProgramOwnedIds { get; init; }
    /// <summary>
    /// 程序方面的气路「隐性占用」（多程序并行）：有程序在稳压（脉冲间隙气路空闲，但随时会取用）或有程序排队等待气路时的说明文字
    /// （如「RV1 程序 · 稳压」「RV2 程序 · 等待气路」），空 = 没有。非程序请求方（泄漏测试 / 手动阀控）视为 GAS_BUSY；
    /// 程序请求方（WaitInsteadOfBusy）不受影响——程序之间经队列轮流。
    /// </summary>
    public string? ProgramGasPending { get; init; }
}

/// <summary>
/// 开始自动气路序列（程序吹扫 / 升压、泄漏测试）前的公共检查，从 MainViewModel.GasBlockReason 抽出。
/// 返回 null = 可以开始，否则为（§11 错误码, 与今天逐字相同的中文原因）。判断顺序与原实现一致。
/// </summary>
public static class GasGuard
{
    public static (string Code, string Message)? Check(in GasGuardInput s)
    {
        if (s.EstopLocked) return (OpCodes.EstopActive, "急停状态下禁止气路操作");
        if (!s.IoConfigured) return (OpCodes.HwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法执行气路操作");
        if (s.NeedPressure && !s.AnalogConfigured)
            return (OpCodes.HwNotConfigured, "压力采集未配置串口（PR_PRESS_PORT），无法闭环充压与超压监视");
        if (s.NeedPressure && !s.PressOnline) return (OpCodes.HwOffline, "压力采集离线（8AI 通讯失败），请检查接线与串口后再试");
        var task = s.GasTask ?? "";
        if (task.Length > 0 && !(s.WaitInsteadOfBusy && s.GasHeldByProgram))
            return (OpCodes.GasBusy, $"{task}进行中，请等待完成或先停止");
        // 程序稳压的脉冲间隙 / 程序排队等待期间，气路对泄漏测试与手动阀控仍视为占用（§7.12 第 1 条：优先级规则与 v0.4 一致）
        var pending = s.ProgramGasPending ?? "";
        if (pending.Length > 0 && !s.WaitInsteadOfBusy)
            return (OpCodes.GasBusy, $"{pending}进行中，请等待完成或先停止");
        if (s.ManualValve) return (OpCodes.ManualValveActive, "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行");
        var chans = s.Channels ?? [];
        var op = chans.Where(r => r.OverPressureTripped).ToList();
        if (op.Count > 0)
            return (OpCodes.ChannelAlarm,
                $"{string.Join("、", op.Select(r => "RV" + r.Id))} 超压未解除（需低于超压报警值的 98%），禁止气路操作");
        // 停用 / 已结束通道的超温切断报警不阻塞气路。
        // 程序气路步骤（WaitInsteadOfBusy）：其他通道的报警只要不是超压未解除（上面已判）就不阻塞——超温报警不涉及总管，
        // 只中止该通道自己的程序（§7.12 第 7 条联锁隔离）；本通道自己的报警仍阻塞。泄漏测试 / 手动路径逐字同原来。
        int? self = s.SelfId;
        var owned = s.ProgramOwnedIds ?? [];
        bool programWait = s.WaitInsteadOfBusy;
        var busy = chans.Where(r => ((r.State is ReactorState.Alarm && (!programWait || r.Id == self))
                                     || (r.State is ReactorState.Pressing or ReactorState.React && r.Id != self && !owned.Contains(r.Id)))
                                    && !r.IdleOverTempAlarm).ToList();
        if (busy.Count > 0)
            return (OpCodes.ChannelPressurized,
                $"{string.Join("、", busy.Select(r => "RV" + r.Id))} 正在加压 / 反应 / 报警，需先停止");
        return null;
    }
}
