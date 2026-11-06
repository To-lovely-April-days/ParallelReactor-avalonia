using System;

namespace ParallelReactor.Commands;

/// <summary>控制模式（§5.1）：本地控制 / 远程控制。</summary>
public enum ControlMode { Local, Remote }

/// <summary>动作类别：Control = 启动 / 改设定 / 下发；Stop = 停止类；Record = 记录类；Reset = 急停复位。</summary>
public enum ActionClass { Control, Stop, Record, Reset }

/// <summary>是否对外开放：LocalOnly = 附录 B 中不对外的动作。</summary>
public enum Exposure { Remote, LocalOnly }

public readonly record struct ArbiterDecision(bool Allowed, string? Code, string? Message)
{
    public static readonly ArbiterDecision Allow = new(true, null, null);
    public static ArbiterDecision Deny(string code, string msg) => new(false, code, msg);
}

/// <summary>
/// 控制权仲裁（§5.1）。纯逻辑：判定只看 <see cref="Decide"/> 的参数。
/// 默认本地控制；本期没有切换界面，<see cref="EnterRemote"/> 只供测试与将来的 /control 授权调用；
/// <see cref="ReturnToLocal"/> 另由急停快速路径调用（§5.3 第 4 条、§7.3：急停一律收回控制权）。
/// <para>线程：模式与持有者成对放在锁里，任意线程可读可改（急停快速路径不在 UI 线程）。
/// <see cref="Changed"/> 在调用方线程、锁外触发，订阅方自己切回 UI 线程。</para>
/// 租约（X-Control-Lease、过期、grace）属于 HTTP 层，下一期在判定表第 5、6 行之前插入；角色属于认证层，不在这里判。
/// </summary>
public sealed class ControlArbiter
{
    private readonly object _lock = new();
    private ControlMode _mode = ControlMode.Local;
    private string _holderId = "", _holderName = "", _lastReason = "";

    public ControlMode Mode { get { lock (_lock) return _mode; } }
    public string HolderClientId { get { lock (_lock) return _holderId; } }
    public string HolderName { get { lock (_lock) return _holderName; } }
    /// <summary>最近一次回到本地的原因（诊断用）。</summary>
    public string LastReturnReason { get { lock (_lock) return _lastReason; } }
    /// <summary>模式或持有者变化。在调用方线程触发（可能不是 UI 线程）。</summary>
    public event Action? Changed;

    internal void EnterRemote(string clientId, string name)
    {
        lock (_lock)
        {
            _mode = ControlMode.Remote;
            _holderId = clientId ?? "";
            _holderName = name ?? "";
        }
        Changed?.Invoke();
    }

    /// <summary>回到本地控制（清空持有者）。返回之前是否处于远程模式；已是本地时只更新原因、不触发 Changed。</summary>
    internal bool ReturnToLocal(string reason)
    {
        bool was;
        lock (_lock)
        {
            was = _mode == ControlMode.Remote;
            _mode = ControlMode.Local;
            _holderId = "";
            _holderName = "";
            _lastReason = reason ?? "";
        }
        if (was) Changed?.Invoke();
        return was;
    }

    public ArbiterDecision Check(ActionClass cls, CommandSource src, Exposure exp = Exposure.Remote)
    {
        ControlMode mode; string id, name;
        lock (_lock) { mode = _mode; id = _holderId; name = _holderName; }
        return Decide(mode, id, name, cls, src, exp);
    }

    public const string MsgNotExposed = "该操作只允许在设备触屏上进行";
    public const string MsgModeLocal = "本地控制中 · 远程只能执行停止类和记录类操作";

    /// <summary>判定表（设计稿 §2.3，测试逐行覆盖）。</summary>
    public static ArbiterDecision Decide(ControlMode mode, string holderId, string holderName,
                                         ActionClass cls, CommandSource src, Exposure exp)
    {
        // 1. 远程来源：不对外的动作、急停复位一律拒绝
        if (src.IsRemote && (exp == Exposure.LocalOnly || cls == ActionClass.Reset))
            return ArbiterDecision.Deny(OpCodes.NotExposed, MsgNotExposed);
        // 2. 停止 / 记录 / 复位（经第 1 行后只剩本地复位）任何模式都允许
        if (cls != ActionClass.Control) return ArbiterDecision.Allow;
        if (mode == ControlMode.Local)
            // 3 / 4
            return src.IsRemote ? ArbiterDecision.Deny(OpCodes.ModeLocal, MsgModeLocal) : ArbiterDecision.Allow;
        // 远程模式
        if (src.IsRemote)
            // 5 / 6
            return src.ClientId == holderId && holderId.Length > 0
                ? ArbiterDecision.Allow
                : ArbiterDecision.Deny(OpCodes.NotControlHolder, $"当前控制权属于「{holderName}」");
        // 7. 远程控制中，触屏的启动 / 改设定被拒绝（Toast 级别 warn，由调用方按 Code 决定）
        return ArbiterDecision.Deny(OpCodes.ModeRemote, $"远程控制中（{holderName}）");
    }
}
