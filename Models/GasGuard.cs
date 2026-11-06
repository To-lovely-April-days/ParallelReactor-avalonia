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
        if (task.Length > 0) return (OpCodes.GasBusy, $"{task}进行中，请等待完成或先停止");
        if (s.ManualValve) return (OpCodes.ManualValveActive, "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行");
        var chans = s.Channels ?? [];
        var op = chans.Where(r => r.OverPressureTripped).ToList();
        if (op.Count > 0)
            return (OpCodes.ChannelAlarm,
                $"{string.Join("、", op.Select(r => "RV" + r.Id))} 超压未解除（需低于超压报警值的 98%），禁止气路操作");
        // 停用 / 已结束通道的超温切断报警不阻塞气路
        int? self = s.SelfId;
        var busy = chans.Where(r => (r.State is ReactorState.Alarm
                                     || (r.State is ReactorState.Pressing or ReactorState.React && r.Id != self))
                                    && !r.IdleOverTempAlarm).ToList();
        if (busy.Count > 0)
            return (OpCodes.ChannelPressurized,
                $"{string.Join("、", busy.Select(r => "RV" + r.Id))} 正在加压 / 反应 / 报警，需先停止");
        return null;
    }
}
