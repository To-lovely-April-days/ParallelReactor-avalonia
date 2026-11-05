using System;
using System.Threading.Tasks;

namespace ParallelReactor.Services;

/// <summary>急停某一部分的下发结果。</summary>
public enum EstopPart
{
    Pending,        // 下发中
    Ok,             // 已确认写入
    Failed,         // 通讯失败 / 写入未确认（锁定期间会重发）
    NotConfigured,  // 该模块未配置串口：软件从未控制它，无可下发
}

/// <summary>一次急停的三部分结果：加热（AI-8）/ 搅拌（DM2C）/ 阀门（IO16R）。</summary>
public sealed record EstopResult(EstopPart Heat, EstopPart Stir, EstopPart Valves)
{
    public static readonly EstopResult AllPending = new(EstopPart.Pending, EstopPart.Pending, EstopPart.Pending);

    /// <summary>没有任何部分失败（未配置的模块不算失败）。</summary>
    public bool AllConfirmed => Heat != EstopPart.Failed && Stir != EstopPart.Failed && Valves != EstopPart.Failed;
}

/// <summary>
/// 急停编排：温控 / 搅拌 / 阀门是三条独立的 RS485 总线，三路停止指令并行下发、互不等待；
/// 任一路失败或抛异常只记为该路 Failed，不影响其余两路。传入 null 的部分记为 NotConfigured。
/// </summary>
public static class EmergencyStop
{
    public static async Task<EstopResult> RunAsync(
        Func<Task<bool>>? heat, Func<Task<bool>>? stir, Func<Task<bool>>? valves)
    {
        // 三个都先发出去再等，慢的那条总线不拖住另外两条
        var h = PartAsync(heat);
        var s = PartAsync(stir);
        var v = PartAsync(valves);
        return new EstopResult(await h, await s, await v);
    }

    /// <summary>下发一部分：op 返回 true = Ok，返回 false 或抛异常 = Failed，op 为 null = NotConfigured。</summary>
    public static async Task<EstopPart> PartAsync(Func<Task<bool>>? op)
    {
        if (op == null) return EstopPart.NotConfigured;
        try { return await op() ? EstopPart.Ok : EstopPart.Failed; }
        catch { return EstopPart.Failed; }
    }
}
