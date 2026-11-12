using System;

namespace ParallelReactor.Client;

// ============================================================================
// M3 增补（PORT-PLAN §5 M3「Client」，只加不改）：控制权的「事实」类型。
// 由 ControlLeaseClient 维护，界面（LeaseView / 守卫）只读。
// ============================================================================

/// <summary>
/// 本地取消的控制权申请最后的结局（R7：HMI 没有取消端点 A5，所以本地取消后在后台继续查这条申请，触屏之后批准就立即释放）。
/// <see cref="Result"/> 取下面的常量之一；<see cref="LeaseId"/> 只在触屏批准过时有值；<see cref="At"/> 为本机时钟（<see cref="IClock.UtcNow"/>）。
/// <see cref="RequestId"/> 为空串 = 在 POST /control/request 途中取消、而 POST 失败了（没拿到申请 id：HMI 回了错误 → <see cref="Gone"/>；网络 / 超时 → <see cref="Timeout"/>）。
/// </summary>
public sealed record CancelledRequestOutcome(string RequestId, string Result, string? LeaseId, DateTime At)
{
    /// <summary>触屏在取消之后批准了，已立即 POST /control/release（或 HMI 回答这张租约已不是当前租约：同样没有遗留）。</summary>
    public const string Released = "released";
    /// <summary>触屏在取消之后批准了，但释放一直失败到截止时刻：设备上可能留着一张没人心跳的租约，宽限期满后 HMI 按 onRemoteLost 处理。</summary>
    public const string ReleaseFailed = "releaseFailed";
    /// <summary>触屏批准了，但 HMI 没给 leaseId（缺申请凭据），无法释放。</summary>
    public const string NoLeaseId = "noLeaseId";
    /// <summary>触屏拒绝（HMI 对该令牌冷却 30 s：<see cref="ControlLeaseClient.DenyUntil"/> 已设）。</summary>
    public const string Denied = "denied";
    /// <summary>申请在 HMI 上超时（没人处理）。</summary>
    public const string Expired = "expired";
    /// <summary>HMI 不认识这条申请了（404：重启 / 记录被清理），或 POST 途中取消而 HMI 回了错误（没有建出申请）。</summary>
    public const string Gone = "gone";
    /// <summary>用户又申请了一次，HMI 返回的是同一条申请：由新的 <see cref="ControlLeaseClient.AcquireAsync"/> 接着等结果（不释放）。</summary>
    public const string Adopted = "adopted";
    /// <summary>到本地截止时刻仍没有结果（HMI 一直连不上），或 POST 途中取消而 POST 网络失败（申请建没建出来不知道）。</summary>
    public const string Timeout = "timeout";
    /// <summary>连接被停止 / 释放（<see cref="ControlLeaseClient.DisposeAsync"/>）时这条申请还没有结果。</summary>
    public const string Stopped = "stopped";

    /// <summary>触屏在本地取消之后批准了这条申请。</summary>
    public bool ApprovedAfterCancel => Result is Released or ReleaseFailed or NoLeaseId;

    /// <summary>设备上可能留着一张没人心跳的租约（释放失败 / 拿不到 leaseId / 结果未知）：界面应提示到触屏确认。</summary>
    public bool OrphanPossible => Result is ReleaseFailed or NoLeaseId or Timeout or Stopped;
}

/// <summary>
/// HMI 上一条等待触屏处理的控制权申请（R8：可能是别的客户端的）。
/// 来自 GET /control 的 <c>pending</c> 时各字段准确；只来自 WS <c>control</c> 的 <c>requested</c> 消息时，
/// <see cref="ClientName"/> / <see cref="TokenName"/> 从消息的 detail 文字解析（解析不了为 null），<see cref="ExpiresAt"/> = 消息时刻 + HMI 的申请超时（估算）。
/// <see cref="ExpiresAt"/> 为服务端 UTC。
/// </summary>
public sealed record ControlPendingInfo(string RequestId, string? ClientName, string? TokenName, DateTime? ExpiresAt);

/// <summary>
/// HMI 报告的控制权现状（GET /control 与 WS <c>control</c> 消息合并；R8）。
/// <see cref="Mode"/>：<c>local</c> / <c>remote</c>（仲裁器）；<see cref="State"/>：<c>local</c> / <c>requested</c> / <c>remote</c> / <c>remoteLost</c>（租约）；
/// <see cref="Holder"/>：持有者的客户端名（本地时为 null）；<see cref="Pending"/>：等待触屏处理的申请（没有为 null）。
/// 断线期间保持最后一次收到的内容（不代表现在）。
/// </summary>
public sealed record ServerControlView(string Mode, string State, string? Holder, ControlPendingInfo? Pending);
