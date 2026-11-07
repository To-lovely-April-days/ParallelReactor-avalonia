using System.Threading.Tasks;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Commands;

/// <summary>
/// 控制权（设计稿 §5.1 / §5.3）：只有本地可以「允许远程申请」和「切回本地」（Record 类 · 只允许本地）。
/// 远程只能申请 / 心跳 / 释放——那些走接口层 <c>ControlLease</c>，不经命令层。三条命令都只改租约状态机（纯逻辑），不碰设备。
/// </summary>
public sealed partial class OperatorCommands
{
    /// <summary>触屏「允许」：授权待处理的远程申请（生成 leaseId，仲裁器进入远程）。申请已超时 / 不存在 → NOT_FOUND。</summary>
    public Task<OpResult> TryGrantControl(CommandSource src, string requestId)
    {
        if (Admit(src, ActionClass.Record, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        var lease = _m.ControlLease.Grant(requestId, MainViewModel.MonoNow);
        return (lease == null
            ? OpResult.Fail(OpCodes.NotFound, "该申请已过期或不存在", "warn")
            : OpResult.Done($"已允许 {lease.ClientName} 远程控制", "ok", new { requestId, client = lease.ClientName })).AsTask();
    }

    /// <summary>触屏「拒绝」。申请已超时 / 不存在 → Silent（幂等）。</summary>
    public Task<OpResult> TryDenyControl(CommandSource src, string requestId)
    {
        if (Admit(src, ActionClass.Record, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        bool ok = _m.ControlLease.Deny(requestId, MainViewModel.MonoNow);
        return (ok ? OpResult.Done("已拒绝远程控制申请", "warn", new { requestId }) : OpResult.Silent).AsTask();
    }

    /// <summary>触屏「切回本地」：远程租约作废（待处理申请按拒绝）。已是本地 → Silent（幂等）。</summary>
    public Task<OpResult> TryTakeBackControl(CommandSource src)
    {
        if (Admit(src, ActionClass.Record, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        bool was = _m.ControlLease.TakeBack(MainViewModel.MonoNow);
        return (was ? OpResult.Done("已切回本地控制 · 远程租约作废", "ok") : OpResult.Silent).AsTask();
    }
}
