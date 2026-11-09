namespace ParallelReactor.Control.Core.Guard;

/// <summary>
/// HMI 检查令牌权限时把操作分成哪一类（REPO/Api/Endpoints.Write.cs CommandAsync 的「角色 / 急停权限位」一步）：
/// <list type="bullet">
/// <item><see cref="Local"/>：只在本机（断开、重连、取消申请、改名、移除），不发给 HMI。</item>
/// <item><see cref="Control"/>：控制类写命令（启用、启动、加热、设定、下发 / 运行程序、搅拌开 / 设定、全部运行）—— operator 及以上。</item>
/// <item><see cref="Record"/>：申请 / 释放控制权、确认报警、会话（开始、打标记、结束）—— operator 及以上（急停权限位不算）。</item>
/// <item><see cref="Stop"/>：停止类（停止、停用、停止加热、中止程序、停止搅拌、全部停止）—— operator，<b>或</b>急停权限位。</item>
/// <item><see cref="Estop"/>：急停 —— 只看急停权限位。</item>
/// </list>
/// </summary>
public enum TokenPermission { Local, Control, Record, Stop, Estop }

/// <summary>
/// 本机这个令牌在这台 HMI 上的权限（HMI：角色 viewer / operator / admin + 独立的急停权限位，REPO/Api/TokenAuth.cs）。
/// A3（GET /whoami）之前没有接口直接问，只能在<b>第一个 403 FORBIDDEN_ROLE 之后学到</b>（PORT-PLAN §3.2 第 10 条、R9），按设备缓存在本机
/// （DeviceRegistry；换了令牌就清掉）。一次 403 能推出的（HMI 的规则见 <see cref="TokenPermission"/>）：
/// <list type="bullet">
/// <item>控制类 / 记录类被拒 → 角色低于 operator，即只读令牌（<see cref="ReadOnly"/>）；有没有急停权限位还不知道。</item>
/// <item>停止类被拒 → 只读令牌<b>且</b>没有急停权限位（<see cref="ReadOnly"/> + <see cref="NoEstop"/>）。</item>
/// <item>急停被拒 → 没有急停权限位（<see cref="NoEstop"/>；角色不知道）。</item>
/// </list>
/// 守卫（<see cref="ActionGuard"/>）照此禁用并写明原因；不知道的一律不禁用（停止类、急停宁可发出去被拒，也不能被本机猜错拦下）。
/// </summary>
public sealed record TokenRole(bool ReadOnly = false, bool NoEstop = false)
{
    /// <summary>还没有被拒过（不限制任何操作）。</summary>
    public static readonly TokenRole Unknown = new();

    /// <summary>已经知道有限制（至少被拒过一次）。</summary>
    public bool IsLimited => ReadOnly || NoEstop;

    /// <summary>停止类能发（operator 或有急停权限位；没学到「两者都没有」之前都算能发）。</summary>
    public bool CanStop => !(ReadOnly && NoEstop);

    /// <summary>这个操作要的权限类别。</summary>
    public static TokenPermission PermissionOf(DeviceAction action) => action switch
    {
        DeviceAction.Disable or DeviceAction.Stop or DeviceAction.HeatStop or DeviceAction.ProgAbort
            or DeviceAction.StirOff or DeviceAction.ChStirOff or DeviceAction.StopAll => TokenPermission.Stop,
        DeviceAction.Estop => TokenPermission.Estop,
        DeviceAction.SessionStart or DeviceAction.SessionMark or DeviceAction.SessionEnd or DeviceAction.AlarmAck
            or DeviceAction.LeaseRequest or DeviceAction.LeaseRelease => TokenPermission.Record,
        DeviceAction.LeaseCancel or DeviceAction.Disconnect or DeviceAction.Reconnect or DeviceAction.Rename or DeviceAction.Remove => TokenPermission.Local,
        _ => TokenPermission.Control,
    };

    /// <summary>这个操作被 HMI 以 403 FORBIDDEN_ROLE 拒绝之后学到的权限（只会收紧，不会放宽；本机操作被拒不改变什么）。</summary>
    public TokenRole Learn(DeviceAction action) => PermissionOf(action) switch
    {
        TokenPermission.Control or TokenPermission.Record => this with { ReadOnly = true },
        TokenPermission.Stop => new TokenRole(true, true),
        TokenPermission.Estop => this with { NoEstop = true },
        _ => this,
    };

    // 原因文字：样稿没有这一类（PORT-PLAN §3.5「角色已知后照实禁用并写明原因」），写法照 HMI 自己的 403 消息
    // （「viewer 令牌只能读取 · 写命令需要 operator 及以上角色」「停止类命令需要 operator 角色或 estop 权限位」「该令牌没有急停权限（estop）」），
    // 前面加别名（同样稿其它原因「2号机为本地只读 · 先申请控制权」）。

    /// <summary>只读令牌：控制类、申请控制权、确认报警、会话。</summary>
    public static string ReadOnlyText(string alias) => alias + "的令牌只能读取 · 需要 operator 令牌";

    /// <summary>只读令牌且没有急停权限位：停止类。</summary>
    public static string NoStopText(string alias) => alias + "的令牌只能读取 · 停止需要 operator 令牌或急停权限";

    /// <summary>没有急停权限位：急停。</summary>
    public static string NoEstopText(string alias) => alias + "的令牌没有急停权限";
}
