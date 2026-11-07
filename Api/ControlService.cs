using System;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>
/// 控制权服务（设计稿 §5.3 / §5.5 / §7.2）：把 <see cref="ControlLease"/> 的迁移接到审计（<c>control.*</c> 事件）、WebSocket <c>control</c> 推送，
/// 并把宽限期满（<see cref="LeaseEventKind.Expired"/>）交给 HMI 侧按 <c>onRemoteLost</c> 处理（<see cref="RemoteLost"/> 事件，Api/Hmi/ControlBridge 订阅）。
/// 单调时刻 ↔ 墙钟换算在这里做（响应里的 expiresAt 是 UTC）。纯逻辑，测试直接调用 <see cref="Tick"/>。
/// </summary>
public sealed class ControlService
{
    private readonly IApiSources _src;
    private readonly ApiConfig _cfg;
    private readonly WsHub _hub;

    public ControlLease Lease => _src.Lease;
    public ApiConfig Config => _cfg;

    /// <summary>宽限期满（租约已作废、仲裁器已回本地）：参数 = 策略（keep / stopHeat / estop）、令牌名、客户端名。调用方线程（计时器 / Kestrel）。</summary>
    public event Action<string, string, string>? RemoteLost;
    /// <summary>心跳超时进入 RemoteLost（设备照常运行）：HMI 侧提示「远程连接丢失，设备继续运行」。</summary>
    public event Action<string>? Lost;

    public ControlService(IApiSources src, ApiConfig cfg, WsHub hub)
    {
        _src = src;
        _cfg = cfg;
        _hub = hub;
        Lease.Configure(TimeSpan.FromSeconds(cfg.HeartbeatTimeoutSec), TimeSpan.FromSeconds(cfg.LeaseGraceSec), TimeSpan.FromSeconds(cfg.ControlRequestTimeoutSec));
        Lease.Transition += OnTransition;
    }

    public TimeSpan Now => _src.MonoNow;

    /// <summary>单调时刻 → UTC 墙钟（null 透传）。</summary>
    public DateTime? Utc(TimeSpan? mono) => mono is { } m ? DateTime.UtcNow + (m - _src.MonoNow) : null;

    /// <summary>计时器 / 测试：推进超时。</summary>
    public void Tick(TimeSpan now) => Lease.Tick(now);

    public ControlStateDto State()
    {
        var s = Lease.Snapshot(Now);
        var mode = _src.Control;
        return new ControlStateDto(mode.Control, mode.Holder, s.HasLease ? s.TokenName : null, StateWord(s.State),
            Utc(s.LeaseExpiresAt), Utc(s.HardExpiresAt),
            s.State == LeaseState.Requested ? new ControlPendingDto(s.RequestId!, s.ClientName, s.TokenName, Utc(s.RequestDeadline)) : null,
            _cfg.AutoGrantRemote, _cfg.HeartbeatTimeoutSec, _cfg.LeaseGraceSec, _cfg.ControlRequestTimeoutSec, _cfg.OnRemoteLost);
    }

    public static string StateWord(LeaseState s) => s switch
    {
        LeaseState.Requested => "requested",
        LeaseState.Remote => "remote",
        LeaseState.RemoteLost => "remoteLost",
        _ => "local",
    };

    public ControlRequestResultDto ToDto(RequestOutcome r) => r.Lease is { } l
        ? new ControlRequestResultDto(r.RequestId, "granted", null, l.LeaseId, Utc(l.ExpiresAt), _cfg.HeartbeatTimeoutSec, _cfg.LeaseGraceSec, r.Secret)
        : new ControlRequestResultDto(r.RequestId, "pending", Utc(r.Deadline), null, null, _cfg.HeartbeatTimeoutSec, _cfg.LeaseGraceSec, r.Secret);

    public ControlRequestStatusDto ToDto(RequestStatus s) => new(s.RequestId, s.Result.ToString().ToLowerInvariant(), s.ClientName,
        Utc(s.Deadline), s.Lease?.LeaseId, Utc(s.Lease?.ExpiresAt), _cfg.HeartbeatTimeoutSec, _cfg.LeaseGraceSec);

    public HeartbeatResultDto ToDto(HeartbeatResult h) => new(_src.Control.Control, h.Resumed, Utc(h.ExpiresAt), Utc(h.HardExpiresAt), _cfg.HeartbeatTimeoutSec);

    // ===================== 迁移 → 审计 / 推送 / 策略 =====================

    private void OnTransition(LeaseEvent e)
    {
        var (kind, level, title) = e.Kind switch
        {
            LeaseEventKind.Requested => ("control.requested", "info", "远程申请控制权"),
            LeaseEventKind.Granted => ("control.granted", "info", "已授权远程控制"),
            LeaseEventKind.Denied => ("control.denied", "info", "拒绝远程控制申请"),
            LeaseEventKind.RequestExpired => ("control.requestExpired", "warn", "远程控制申请超时"),
            LeaseEventKind.Released => ("control.released", "info", "远程释放控制权 · 回到本地"),
            LeaseEventKind.TakenBack => ("control.takenBack", "info", "本地切回 · 远程租约作废"),
            LeaseEventKind.Lost => ("control.lost", "warn", "远程心跳丢失 · 设备继续运行"),
            LeaseEventKind.Resumed => ("control.resumed", "info", "远程心跳恢复"),
            LeaseEventKind.Expired => ("control.expired", "warn", "远程宽限期满 · 租约作废、回到本地"),
            LeaseEventKind.Invalidated => ("control.mode", "warn", "控制权被收回 · 远程租约作废"),
            _ => ("control.mode", "info", "控制模式变化"),
        };
        string detail = e.Detail + (e.Kind == LeaseEventKind.Expired ? $" · onRemoteLost = {_cfg.OnRemoteLost}" : "");
        try { _src.Audit(level, title, detail + (e.TokenName.Length > 0 ? $" · 令牌 {e.TokenName}" : "") + (e.LeaseId != null ? $" · 租约 {e.LeaseId[..Math.Min(8, e.LeaseId.Length)]}…" : ""),
            "remote", e.ClientName.Length > 0 ? e.ClientName : null, null, kind); } catch { }

        // 不回调 Lease.Snapshot（会在 Transition 回调里再次推进状态机）：终态随事件带来
        var mode = _src.Control;
        string? result = e.Kind switch
        {
            LeaseEventKind.Granted or LeaseEventKind.Resumed => "granted",
            LeaseEventKind.Denied => "denied",
            LeaseEventKind.RequestExpired => "expired",
            LeaseEventKind.Requested => "pending",
            LeaseEventKind.Lost => "lost",
            LeaseEventKind.Expired or LeaseEventKind.TakenBack or LeaseEventKind.Released or LeaseEventKind.Invalidated => "revoked",
            _ => null,
        };
        var now = DateTime.UtcNow;
        var evName = char.ToLowerInvariant(e.Kind.ToString()[0]) + e.Kind.ToString()[1..];
        // leaseId 不进 WS（同一令牌的另一台 PC 也在听）：申请方从 POST /control/request 响应或带 X-Control-Request-Key 的 GET /control/requests/{id} 取（§14.2）
        DateTime? leaseExpires = e.State is LeaseState.Remote or LeaseState.RemoteLost ? Utc(e.At + Lease.HeartbeatTimeout) : null;
        var pub = new ControlMsg("control", evName, mode.Control, mode.Holder, StateWord(e.State), e.RequestId, result,
            null, leaseExpires, _cfg.HeartbeatTimeoutSec, e.Detail, now);
        _hub.Broadcast(ApiJson.Serialize(pub));

        if (e.Kind == LeaseEventKind.Lost) { try { Lost?.Invoke(e.ClientName); } catch { } }
        if (e.Kind == LeaseEventKind.Expired) { try { RemoteLost?.Invoke(_cfg.OnRemoteLost, e.TokenName, e.ClientName); } catch { } }
    }

    /// <summary>记一条 op.* 审计（返回 seq）。</summary>
    public long AuditOp(string level, string title, string detail, string client, string opId, string kind)
        => _src.Audit(level, title, detail, "remote", client, opId, kind);

    /// <summary>推送 opEvent。</summary>
    public void PublishOp(OpEventMsg msg) => _hub.Broadcast(ApiJson.Serialize(msg));
}
