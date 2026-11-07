using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ParallelReactor.Commands;

/// <summary>租约状态（设计稿 §5.3 状态机）。</summary>
public enum LeaseState { Local, Requested, Remote, RemoteLost }

/// <summary>状态机的每次迁移（控制服务据此记 <c>control.*</c> 事件、推送 WS <c>control</c> 消息、执行 onRemoteLost）。</summary>
public enum LeaseEventKind
{
    /// <summary>远程申请已登记（等待触屏允许 / 拒绝）。</summary>
    Requested,
    /// <summary>已授权（触屏允许或 autoGrant），进入 Remote。</summary>
    Granted,
    /// <summary>触屏拒绝。</summary>
    Denied,
    /// <summary>申请在超时内未被处理（= 拒绝）。</summary>
    RequestExpired,
    /// <summary>远程主动交还控制权。</summary>
    Released,
    /// <summary>本地夺回（切回本地）。</summary>
    TakenBack,
    /// <summary>心跳超时，进入 RemoteLost（设备照常运行）。</summary>
    Lost,
    /// <summary>宽限期内凭同一 leaseId 恢复为 Remote。</summary>
    Resumed,
    /// <summary>宽限期满：租约作废、回到本地，调用方按 onRemoteLost 处理。</summary>
    Expired,
    /// <summary>仲裁器被外部收回（急停）或同一客户端在宽限期内重新申请：旧租约随之作废。</summary>
    Invalidated,
}

/// <summary>一次迁移。<see cref="State"/> 为这一批迁移结束后的状态（同一次调用里的多条迁移取同一个终态）。</summary>
public sealed record LeaseEvent(LeaseEventKind Kind, string TokenName, string ClientName, string? RequestId, string? LeaseId, string Detail, TimeSpan At)
{
    public LeaseState State { get; init; } = LeaseState.Local;
}

/// <summary>一张有效租约（只发给申请方）。ExpiresAt / HardExpiresAt 为单调时刻（调用方换算成墙钟）。</summary>
public sealed record LeaseInfo(string LeaseId, string TokenName, string ClientName, TimeSpan ExpiresAt, TimeSpan HardExpiresAt);

public enum RequestResult { Unknown, Pending, Granted, Denied, Expired }

/// <summary>GET /control/requests/{id} 的内容。Lease 只对申请令牌、带申请凭据（<see cref="RequestOutcome.Secret"/>）且租约仍有效时给出。</summary>
public sealed record RequestStatus(RequestResult Result, string RequestId, string TokenName, string ClientName, TimeSpan? Deadline, LeaseInfo? Lease);

/// <summary>申请结果。<see cref="Secret"/>（128 位随机）只随这次 HTTP 响应返回一次：之后轮询 GET /control/requests/{id} 须带它才能拿到 leaseId。</summary>
public readonly record struct RequestOutcome(string? Code, string? Message, string RequestId, TimeSpan? Deadline, LeaseInfo? Lease, string? Secret = null)
{
    public bool Ok => Code == null;
}

public readonly record struct LeaseCheck(bool Ok, string? Code, string? Message, string TokenName, string ClientName)
{
    public static readonly LeaseCheck Pass = new(true, null, null, "", "");
}

public readonly record struct HeartbeatResult(bool Ok, string? Code, string? Message, bool Resumed, TimeSpan? ExpiresAt, TimeSpan? HardExpiresAt);

/// <summary>GET /control 用的只读快照（单调时刻）。</summary>
public sealed record LeaseSnapshot(LeaseState State, string TokenName, string ClientName, string? RequestId, TimeSpan? RequestDeadline,
    TimeSpan? LeaseExpiresAt, TimeSpan? HardExpiresAt, bool HasLease);

/// <summary>
/// 远程控制租约状态机（设计稿 §5.3 / §5.5；纯逻辑，时间注入，测试工程直接链接）。
/// 持有 <see cref="ControlArbiter"/>：授权时 <c>EnterRemote(令牌名, 客户端名)</c>，释放 / 夺回 / 宽限期满时 <c>ReturnToLocal</c>——
/// 这两步都在本状态机的锁内完成，所以仲裁器模式与租约状态任何时刻都一致（不会出现「刚授权的租约被上一次过期的回本地动作作废」）；
/// 仲裁器被外部收回（急停快速路径）时租约随之作废（<see cref="LeaseEventKind.Invalidated"/>）。
/// <para>身份：<c>Arbiter.HolderClientId</c> = 令牌名（会话归属等按 ClientId 比较，租约重领后仍是同一客户端）；
/// <c>leaseId</c>（128 位随机）只在 HTTP 层核对（<see cref="Validate"/>），同一令牌两台 PC 只有拿到 leaseId 的那台能写：leaseId 只出现在申请方那次 HTTP 响应
/// 与带申请凭据的 GET /control/requests/{id} 里，不进 WS 广播。</para>
/// <para>时刻一律用单调时间（<c>MainViewModel.MonoNow</c>，Stopwatch 流逝量）。所有公开方法都先按 now 推进超时（<see cref="Tick"/> 的逻辑），
/// 所以即使计时器没跑，Validate / Heartbeat 也不会放过已过期的租约。</para>
/// <para>线程：任意线程可调（Kestrel 线程申请 / 心跳、UI 线程允许 / 拒绝、计时器线程 Tick）。状态在锁内改；
/// <see cref="Transition"/> / <see cref="Changed"/> 在锁外触发，经一个按状态变化顺序排队的单消费者队列（<see cref="Drain"/>）：
/// 并发调用产生的迁移严格按发生顺序送达，订阅者回调里再调本状态机也不会嵌套触发；订阅方自己切回 UI 线程。</para>
/// </summary>
public sealed class ControlLease
{
    public const string MsgNoLease = "远程模式下受控写命令须带 X-Control-Lease";
    public const string MsgLeaseExpired = "租约已过期或已作废（宽限期满 / 本地夺回 / 急停）· 请重新申请控制权";
    public const string MsgNotCurrentLease = "带的不是当前租约";
    public const string MsgRequestPending = "已有待处理的远程申请";
    public const int ClientNameMax = ParallelReactor.Api.ClientNames.Max;
    /// <summary>曾发出过的 leaseId 的记忆上限（用于区分 CONTROL_LEASE_EXPIRED 与 NOT_CONTROL_HOLDER）。</summary>
    public const int IssuedLeaseHistory = 256;

    private readonly object _lock = new();
    private readonly ControlArbiter _arbiter;
    private readonly Func<TimeSpan> _clock;

    private LeaseState _state = LeaseState.Local;
    private string _requestId = "", _leaseId = "", _token = "", _client = "";
    private TimeSpan _deadline;    // Requested：申请截止；Remote：心跳截止；RemoteLost：宽限截止
    private TimeSpan _lastHb;
    private readonly Dictionary<string, Rec> _requests = new(StringComparer.Ordinal);
    private readonly Queue<string> _requestOrder = new();
    private readonly HashSet<string> _issuedLeases = new(StringComparer.Ordinal);
    private readonly Queue<string> _issuedOrder = new();
    /// <summary>被拒绝 / 申请超时的令牌 → 可再次申请的时刻（§5.3 防刷弹窗）。</summary>
    private readonly Dictionary<string, TimeSpan> _cooldown = new(StringComparer.Ordinal);
    private const int RequestHistory = 50;

    private sealed class Rec
    {
        public RequestResult Result;
        public string Token = "", Client = "", Secret = "";
        public TimeSpan Deadline;
        public string? LeaseId;
    }

    public ControlArbiter Arbiter => _arbiter;
    public TimeSpan HeartbeatTimeout { get; private set; } = TimeSpan.FromSeconds(10);
    public TimeSpan Grace { get; private set; } = TimeSpan.FromSeconds(60);
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(60);
    /// <summary>触屏拒绝 / 申请超时后，同一令牌多久内不得再申请（防止弹窗被刷；0 = 不限）。</summary>
    public TimeSpan DenyCooldown { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>状态 / 持有者变化（给触屏刷新顶栏、开关弹窗）。调用方线程、锁外。</summary>
    public event Action? Changed;
    /// <summary>每次迁移一条（审计 / 推送 / onRemoteLost）。调用方线程、锁外，按发生顺序。</summary>
    public event Action<LeaseEvent>? Transition;

    public ControlLease(ControlArbiter arbiter, Func<TimeSpan> clock)
    {
        _arbiter = arbiter;
        _clock = clock;
        _arbiter.Changed += OnArbiterChanged;
    }

    /// <summary>按 api.json 配置超时（接口启动时调用；不改状态）。</summary>
    public void Configure(TimeSpan heartbeatTimeout, TimeSpan grace, TimeSpan requestTimeout, TimeSpan? denyCooldown = null)
    {
        lock (_lock)
        {
            if (heartbeatTimeout > TimeSpan.Zero) HeartbeatTimeout = heartbeatTimeout;
            if (grace >= TimeSpan.Zero) Grace = grace;
            if (requestTimeout > TimeSpan.Zero) RequestTimeout = requestTimeout;
            if (denyCooldown is { } d && d >= TimeSpan.Zero) DenyCooldown = d;
        }
    }

    public LeaseState State { get { lock (_lock) return _state; } }

    public LeaseSnapshot Snapshot(TimeSpan now)
    {
        Tick(now);
        lock (_lock)
        {
            return new LeaseSnapshot(_state, _token, _client,
                _state == LeaseState.Requested ? _requestId : null,
                _state == LeaseState.Requested ? _deadline : null,
                _state == LeaseState.Remote ? _deadline : _state == LeaseState.RemoteLost ? _lastHb + HeartbeatTimeout : null,
                HardExpiryLocked(), _state is LeaseState.Remote or LeaseState.RemoteLost);
        }
    }

    private TimeSpan? HardExpiryLocked() => _state switch
    {
        LeaseState.Remote => _deadline + Grace,
        LeaseState.RemoteLost => _deadline,
        _ => null,
    };

    private LeaseInfo LeaseLocked() => new(_leaseId, _token, _client,
        _state == LeaseState.Remote ? _deadline : _lastHb + HeartbeatTimeout, HardExpiryLocked() ?? _deadline);

    /// <summary>
    /// 客户端名清洗（§4.2 防伪装弹窗）：去掉控制字符 / 格式字符（含零宽、双向控制符）与行分隔符，空白折叠为一个空格，截到 <see cref="ClientNameMax"/>。
    /// 结果为空时返回 ""（调用方用令牌名代替）。
    /// </summary>
    public static string SanitizeClientName(string? name) => ParallelReactor.Api.ClientNames.Sanitize(name);   // 实现搬到 Contracts，PC 客户端复用同一规则

    // ===================== 申请 / 允许 / 拒绝 =====================

    /// <summary>
    /// 远程申请控制权。本地模式：登记申请（autoGrant 时立即授权并返回租约）；已有待处理申请：同一令牌 + 客户端名返回原申请，
    /// 其他 → CONTROL_REQUEST_PENDING；已是远程 → NOT_CONTROL_HOLDER；宽限期内同一令牌 + 客户端名再申请 → 旧租约作废、按新申请处理
    /// （客户端崩溃丢了 leaseId 不必等宽限期满）；刚被拒绝 / 超时的令牌在 <see cref="DenyCooldown"/> 内 → RATE_LIMITED。
    /// clientName 经 <see cref="SanitizeClientName"/>。
    /// </summary>
    public RequestOutcome Request(string tokenName, string clientName, TimeSpan now, bool autoGrant)
    {
        var events = new List<LeaseEvent>();
        RequestOutcome outcome;
        bool changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            tokenName ??= "";
            clientName = SanitizeClientName(clientName);
            if (clientName.Length == 0) clientName = SanitizeClientName(tokenName);
            if (clientName.Length == 0) clientName = "远程客户端";

            if (_state == LeaseState.RemoteLost && _token == tokenName && _client == clientName)
            {
                events.Add(Ev(LeaseEventKind.Invalidated, $"{_client} 在宽限期内重新申请 · 旧租约作废", now));
                ToLocalLocked("重新申请");
                changed = true;
            }
            switch (_state)
            {
                case LeaseState.Remote or LeaseState.RemoteLost:
                    outcome = new RequestOutcome(OpCodes.NotControlHolder, $"当前控制权属于「{_client}」", "", null, null);
                    break;
                case LeaseState.Requested when _token == tokenName && _client == clientName:
                    outcome = new RequestOutcome(null, null, _requestId, _deadline, null, _requests.TryGetValue(_requestId, out var same) ? same.Secret : null);
                    break;
                case LeaseState.Requested:
                    outcome = new RequestOutcome(OpCodes.ControlRequestPending, $"{MsgRequestPending}（{_client}）", "", null, null);
                    break;
                case LeaseState.Local when !autoGrant && _cooldown.TryGetValue(tokenName, out var until) && now < until:
                    outcome = new RequestOutcome(OpCodes.RateLimited, $"触屏刚拒绝了该令牌的申请（或申请超时）· {Math.Ceiling((until - now).TotalSeconds):0} s 后可再申请", "", null, null);
                    break;
                default:
                    _cooldown.Remove(tokenName);
                    _requestId = NewId(8);
                    _token = tokenName;
                    _client = clientName;
                    _deadline = now + RequestTimeout;
                    _state = LeaseState.Requested;
                    string secret = NewId(16);
                    Remember(new Rec { Result = RequestResult.Pending, Token = tokenName, Client = clientName, Deadline = _deadline, Secret = secret }, _requestId);
                    events.Add(Ev(LeaseEventKind.Requested, $"{clientName}（令牌 {tokenName}）申请远程控制 · {(autoGrant ? "自动授权" : $"等待触屏确认（{RequestTimeout.TotalSeconds:0} s）")}", now));
                    changed = true;
                    if (autoGrant)
                    {
                        var lease = GrantLocked(now, events);
                        outcome = new RequestOutcome(null, null, _requestId, null, lease, secret);
                    }
                    else outcome = new RequestOutcome(null, null, _requestId, _deadline, null, secret);
                    break;
            }
            Commit(events, changed);
        }
        Drain();
        return outcome;
    }

    /// <summary>触屏允许（或 autoGrant）：生成 leaseId，仲裁器进入远程。申请不存在 / 已过期返回 null。</summary>
    public LeaseInfo? Grant(string requestId, TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        LeaseInfo? lease = null;
        bool changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            if (_state == LeaseState.Requested && _requestId == requestId)
            {
                lease = GrantLocked(now, events);
                changed = true;
            }
            Commit(events, changed);
        }
        Drain();
        return lease;
    }

    private LeaseInfo GrantLocked(TimeSpan now, List<LeaseEvent> events)
    {
        _leaseId = NewId(16);
        _issuedLeases.Add(_leaseId);
        _issuedOrder.Enqueue(_leaseId);
        while (_issuedOrder.Count > IssuedLeaseHistory) _issuedLeases.Remove(_issuedOrder.Dequeue());
        _lastHb = now;
        _deadline = now + HeartbeatTimeout;
        _state = LeaseState.Remote;
        if (_requests.TryGetValue(_requestId, out var rec)) { rec.Result = RequestResult.Granted; rec.LeaseId = _leaseId; }
        events.Add(Ev(LeaseEventKind.Granted, $"已授权 {_client}（令牌 {_token}）远程控制 · 心跳超时 {HeartbeatTimeout.TotalSeconds:0} s · 宽限 {Grace.TotalSeconds:0} s", now));
        _arbiter.EnterRemote(_token, _client);   // 锁内：仲裁器模式与租约状态同时切换
        return LeaseLocked();
    }

    /// <summary>触屏拒绝。申请不存在返回 false。被拒令牌进入 <see cref="DenyCooldown"/>。</summary>
    public bool Deny(string requestId, TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        bool ok = false, changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            if (_state == LeaseState.Requested && _requestId == requestId)
            {
                if (_requests.TryGetValue(_requestId, out var rec)) rec.Result = RequestResult.Denied;
                events.Add(Ev(LeaseEventKind.Denied, $"触屏拒绝 {_client}（令牌 {_token}）的远程控制申请", now));
                CooldownLocked(now);
                ToLocalLocked("拒绝申请");
                ok = changed = true;
            }
            Commit(events, changed);
        }
        Drain();
        return ok;
    }

    private void CooldownLocked(TimeSpan now)
    {
        if (DenyCooldown > TimeSpan.Zero && _token.Length > 0) _cooldown[_token] = now + DenyCooldown;
    }

    /// <summary>
    /// 申请方查询结果。leaseId 只对申请令牌、带申请凭据（<see cref="RequestOutcome.Secret"/>）、且租约仍是当前有效租约时给出；
    /// 其他调用方只看到结果字样。
    /// </summary>
    public RequestStatus? QueryRequest(string requestId, string tokenName, string? secret, TimeSpan now)
    {
        Tick(now);
        lock (_lock)
        {
            if (!_requests.TryGetValue(requestId, out var rec)) return null;
            var result = rec.Result;
            LeaseInfo? lease = null;
            if (result == RequestResult.Granted)
            {
                bool current = _state is LeaseState.Remote or LeaseState.RemoteLost && _leaseId == rec.LeaseId;
                if (!current) result = RequestResult.Expired;
                else if (rec.Token == tokenName && !string.IsNullOrEmpty(secret) && FixedEquals(secret, rec.Secret)) lease = LeaseLocked();
            }
            return new RequestStatus(result, requestId, rec.Token, rec.Client, result == RequestResult.Pending ? rec.Deadline : null, lease);
        }
    }

    private static bool FixedEquals(string a, string b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ===================== 校验 / 心跳 / 释放 / 夺回 =====================

    /// <summary>
    /// 受控写命令的租约校验（HTTP 层，§7.1）：本地模式 → MODE_LOCAL；缺头 → NOT_CONTROL_HOLDER；
    /// 曾发出但已作废的 leaseId → CONTROL_LEASE_EXPIRED；不是当前租约 / 令牌不符 → NOT_CONTROL_HOLDER。
    /// 通过时视为一次心跳（宽限期内的命令也能让连接恢复）。
    /// </summary>
    public LeaseCheck Validate(string? leaseHeader, string tokenName, TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        LeaseCheck r;
        bool changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            r = ValidateLocked(leaseHeader, tokenName);
            if (r.Ok) changed |= TouchLocked(now, events);
            Commit(events, changed);
        }
        Drain();
        return r;
    }

    private LeaseCheck ValidateLocked(string? leaseHeader, string tokenName)
    {
        if (_state is not (LeaseState.Remote or LeaseState.RemoteLost) || _arbiter.Mode != ControlMode.Remote)
            return new LeaseCheck(false, OpCodes.ModeLocal, ControlArbiter.MsgModeLocal, "", "");
        var lease = leaseHeader?.Trim();
        if (string.IsNullOrEmpty(lease)) return new LeaseCheck(false, OpCodes.NotControlHolder, MsgNoLease, "", "");
        if (lease != _leaseId)
            return _issuedLeases.Contains(lease)
                ? new LeaseCheck(false, OpCodes.ControlLeaseExpired, MsgLeaseExpired, "", "")
                : new LeaseCheck(false, OpCodes.NotControlHolder, $"{MsgNotCurrentLease} · 当前控制权属于「{_client}」", "", "");
        if (tokenName != _token)
            return new LeaseCheck(false, OpCodes.NotControlHolder, $"租约属于令牌「{_token}」", "", "");
        return new LeaseCheck(true, null, null, _token, _client);
    }

    /// <summary>心跳 / 续租（REST 或 WS hb 带 lease）。宽限期内凭同一 leaseId 恢复为 Remote（Resumed = true）。</summary>
    public HeartbeatResult Heartbeat(string? leaseId, string tokenName, TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        HeartbeatResult r;
        bool changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            var c = ValidateLocked(leaseId, tokenName);
            if (!c.Ok) r = new HeartbeatResult(false, c.Code, c.Message, false, null, null);
            else
            {
                bool resumed = TouchLocked(now, events);
                changed |= resumed;
                r = new HeartbeatResult(true, null, null, resumed, _deadline, HardExpiryLocked());
            }
            Commit(events, changed);
        }
        Drain();
        return r;
    }

    /// <summary>刷新心跳；RemoteLost → Remote 时返回 true 并追加 Resumed 事件。</summary>
    private bool TouchLocked(TimeSpan now, List<LeaseEvent> events)
    {
        _lastHb = now;
        _deadline = now + HeartbeatTimeout;
        if (_state != LeaseState.RemoteLost) return false;
        _state = LeaseState.Remote;
        events.Add(Ev(LeaseEventKind.Resumed, $"{_client} 在宽限期内恢复心跳 · 继续远程控制", now));
        return true;
    }

    /// <summary>远程交还控制权（须带当前租约）。</summary>
    public LeaseCheck Release(string? leaseId, string tokenName, TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        LeaseCheck r;
        bool changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            r = ValidateLocked(leaseId, tokenName);
            if (r.Ok)
            {
                events.Add(Ev(LeaseEventKind.Released, $"{_client} 交还控制权 · 回到本地", now));
                ToLocalLocked("远程释放");
                changed = true;
            }
            Commit(events, changed);
        }
        Drain();
        return r;
    }

    /// <summary>本地夺回：待处理申请按拒绝、远程 / 宽限中按作废，回到本地。返回之前是否不在本地。</summary>
    public bool TakeBack(TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        bool was = false, changed;
        lock (_lock)
        {
            changed = AdvanceLocked(now, events);
            if (_state == LeaseState.Requested)
            {
                if (_requests.TryGetValue(_requestId, out var rec)) rec.Result = RequestResult.Denied;
                events.Add(Ev(LeaseEventKind.Denied, $"触屏切回本地 · {_client} 的申请按拒绝处理", now));
                CooldownLocked(now);
                ToLocalLocked("本地切回");
                was = changed = true;
            }
            else if (_state is LeaseState.Remote or LeaseState.RemoteLost)
            {
                events.Add(Ev(LeaseEventKind.TakenBack, $"触屏切回本地 · {_client} 的租约作废", now));
                ToLocalLocked("本地切回");
                was = changed = true;
            }
            Commit(events, changed);
        }
        Drain();
        return was;
    }

    /// <summary>推进超时（计时器每秒调用；其他公开方法也会先调用它）。返回本次产生的迁移。</summary>
    public IReadOnlyList<LeaseEvent> Tick(TimeSpan now)
    {
        var events = new List<LeaseEvent>();
        lock (_lock)
        {
            bool changed = AdvanceLocked(now, events);
            Commit(events, changed);
        }
        Drain();
        return events;
    }

    /// <summary>锁内推进：申请超时、心跳超时 → 宽限、宽限期满 → 本地、仲裁器已被外部收回 → 作废。返回状态是否变化。</summary>
    private bool AdvanceLocked(TimeSpan now, List<LeaseEvent> events)
    {
        bool changed = false;
        if (_state is LeaseState.Remote or LeaseState.RemoteLost && _arbiter.Mode != ControlMode.Remote)
        {
            events.Add(Ev(LeaseEventKind.Invalidated, $"控制权已被收回（{_arbiter.LastReturnReason}）· {_client} 的租约作废", now));
            ToLocalLocked(null);
            return true;
        }
        switch (_state)
        {
            case LeaseState.Requested when now >= _deadline:
                if (_requests.TryGetValue(_requestId, out var rec)) rec.Result = RequestResult.Expired;
                events.Add(Ev(LeaseEventKind.RequestExpired, $"{_client} 的远程控制申请 {RequestTimeout.TotalSeconds:0} s 内未处理 · 按拒绝", now));
                CooldownLocked(now);
                ToLocalLocked("申请超时");
                changed = true;
                break;
            case LeaseState.Remote when now >= _deadline:
                _state = LeaseState.RemoteLost;
                _deadline = now + Grace;
                events.Add(Ev(LeaseEventKind.Lost, $"{_client} 心跳超时（{HeartbeatTimeout.TotalSeconds:0} s）· 宽限 {Grace.TotalSeconds:0} s 内凭同一租约可恢复 · 设备继续运行", now));
                changed = true;
                if (now >= _deadline)   // grace = 0：立即作废
                {
                    events.Add(Ev(LeaseEventKind.Expired, $"{_client} 宽限期满 · 租约作废、回到本地控制", now));
                    ToLocalLocked("心跳丢失");
                }
                break;
            case LeaseState.RemoteLost when now >= _deadline:
                events.Add(Ev(LeaseEventKind.Expired, $"{_client} 宽限期满 · 租约作废、回到本地控制", now));
                ToLocalLocked("心跳丢失");
                changed = true;
                break;
        }
        return changed;
    }

    /// <summary>锁内回到本地：清持有者 / 租约，并（reason 不为 null 时）让仲裁器回到本地——同一把锁内完成，状态与模式不会分叉。</summary>
    private void ToLocalLocked(string? reason)
    {
        _state = LeaseState.Local;
        _leaseId = "";
        _requestId = "";
        _token = "";
        _client = "";
        if (reason != null) _arbiter.ReturnToLocal(reason);
    }

    // ===================== 迁移送达（锁外、按序、不嵌套）=====================

    private readonly ConcurrentQueue<(LeaseEvent[] Events, bool Changed)> _pending = new();
    private readonly object _drainLock = new();

    /// <summary>锁内：给这一批迁移盖上终态并入队（入队顺序 = 状态变化顺序）。</summary>
    private void Commit(List<LeaseEvent> events, bool changed)
    {
        if (events.Count == 0 && !changed) return;
        var arr = new LeaseEvent[events.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = events[i] with { State = _state };
        _pending.Enqueue((arr, changed));
    }

    /// <summary>
    /// 锁外：单消费者排空队列并触发 Transition / Changed。已在排空中的线程（订阅者回调里再调本状态机）直接返回，由外层循环接着送；
    /// 其他线程若抢不到排空权也直接返回——持有者会把它们入队的批次一起送完。
    /// </summary>
    private void Drain()
    {
        if (Monitor.IsEntered(_drainLock)) return;
        while (!_pending.IsEmpty)
        {
            if (!Monitor.TryEnter(_drainLock)) return;
            try
            {
                while (_pending.TryDequeue(out var batch))
                {
                    foreach (var e in batch.Events) { try { Transition?.Invoke(e); } catch { /* 订阅者异常不影响状态机 */ } }
                    if (batch.Changed) { try { Changed?.Invoke(); } catch { } }
                }
            }
            finally { Monitor.Exit(_drainLock); }
        }
    }

    /// <summary>仲裁器被外部收回（急停快速路径，任意线程）：立刻作废租约并通知触屏 / 接口。锁内由本类自己触发的 ReturnToLocal 到这里时状态已是 Local，不会重入。</summary>
    private void OnArbiterChanged()
    {
        if (_arbiter.Mode == ControlMode.Remote) return;
        bool holding;
        lock (_lock) holding = _state is LeaseState.Remote or LeaseState.RemoteLost;
        if (holding) Tick(_clock());
    }

    private LeaseEvent Ev(LeaseEventKind kind, string detail, TimeSpan now)
        => new(kind, _token, _client, _requestId.Length > 0 ? _requestId : null, _leaseId.Length > 0 ? _leaseId : null, detail, now);

    private void Remember(Rec rec, string id)
    {
        _requests[id] = rec;
        _requestOrder.Enqueue(id);
        while (_requestOrder.Count > RequestHistory) _requests.Remove(_requestOrder.Dequeue());
    }

    private static string NewId(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
