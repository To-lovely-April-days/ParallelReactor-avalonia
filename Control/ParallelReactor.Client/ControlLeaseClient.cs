using System;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;

namespace ParallelReactor.Client;

/// <summary>PC 侧的租约状态。</summary>
public enum LeaseState
{
    /// <summary>未持有（本地模式 / 只读）。</summary>
    None,
    /// <summary>已发申请，等待触屏确认（轮询 GET /control/requests/{id}）。</summary>
    Requesting,
    /// <summary>持有有效租约，心跳进行中。</summary>
    Holding,
    /// <summary>服务端判定心跳超时（remoteLost）：宽限期内同租约继续心跳可恢复。</summary>
    Lost,
}

/// <summary>一次状态迁移（Reason：granted / denied / requestExpired / released / lost / resumed / expired / takenBack / invalidated / heartbeat:CODE / write:CODE …）。</summary>
public sealed record LeaseTransition(LeaseState From, LeaseState To, string Reason, string? LeaseId, DateTime At);

/// <summary>
/// 控制权租约状态机（设计稿 §5.3–§5.5、§14.2）：申请 → 轮询 → 持有 → 心跳（优先 WS hb，WS 不在线走 REST）→ 丢失 / 恢复 / 过期 → 自动重申请。
/// 时间经 <see cref="IClock"/> 注入（单测手动推进）。所有带租约的写命令由调用方经 <see cref="LeaseId"/> 取当前租约；写命令被 409 租约类拒绝时调 <see cref="OnWriteFailed"/>。
/// WS <c>control</c> 消息不带 leaseId：它只用来触发提前轮询与 lost / resumed / revoked 的状态同步；leaseId 一律从 HTTP 拿。
/// </summary>
public sealed class ControlLeaseClient : IAsyncDisposable
{
    private readonly ReactorApiClient _rest;
    private readonly ClientOptions _opt;
    private readonly IClock _clock;
    private readonly object _lock = new();
    private LeaseState _state = LeaseState.None;
    private string? _leaseId, _requestId, _requestKey;
    private DateTime? _leaseExpiresAt;
    private int _hbTimeoutSec = 10, _graceSec = 60;
    private CancellationTokenSource? _hbCts;
    private Task? _hbTask;
    private TaskCompletionSource<bool> _pollNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _hbFailures;
    private DateTime? _denyUntil;

    public ControlLeaseClient(ReactorApiClient rest, ClientOptions options, IClock? clock = null)
    {
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>优先走 WS 的心跳发送器：返回 true 表示已经由 WS 发出（<see cref="TelemetryWsClient.SendHeartbeatAsync"/>）；null / false → REST。</summary>
    public Func<string, CancellationToken, Task<bool>>? WsHeartbeat { get; set; }

    public LeaseState State { get { lock (_lock) return _state; } }
    public string? LeaseId { get { lock (_lock) return _leaseId; } }
    public DateTime? LeaseExpiresAt { get { lock (_lock) return _leaseExpiresAt; } }
    public string? RequestId { get { lock (_lock) return _requestId; } }
    public int HeartbeatTimeoutSec { get { lock (_lock) return _hbTimeoutSec; } }
    public int LeaseGraceSec { get { lock (_lock) return _graceSec; } }
    /// <summary>连续 ≥ 2 次心跳网络失败。</summary>
    public bool HeartbeatDegraded => Volatile.Read(ref _hbFailures) >= 2;
    /// <summary>可发受控写命令。</summary>
    public bool CanControl => State == LeaseState.Holding;
    /// <summary>心跳间隔（heartbeatTimeoutSec / 2，夹到 [0.5 s, MaxHeartbeatInterval]）。</summary>
    public TimeSpan HeartbeatInterval
    {
        get
        {
            var half = TimeSpan.FromSeconds(HeartbeatTimeoutSec / 2.0);
            if (half > _opt.MaxHeartbeatInterval) half = _opt.MaxHeartbeatInterval;
            return half < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : half;
        }
    }

    public event Action<LeaseTransition>? Transitioned;
    /// <summary>心跳 / 自动重申请中的异常（UI 提示用）。</summary>
    public event Action<Exception>? Error;

    // ======================================================================
    // 申请
    // ======================================================================

    /// <summary>
    /// 申请控制权并等到终态：granted → true（已 Holding、心跳已启动）；denied / 过期 / 404 / 超时 → false。
    /// 已是 Holding 直接返回 true；Requesting 中再次调用等待同一轮结果。
    /// </summary>
    public async Task<bool> AcquireAsync(CancellationToken ct)
    {
        bool joinPoll = false;
        lock (_lock)
        {
            if (_state == LeaseState.Holding) return true;
            if (_state == LeaseState.Requesting) joinPoll = true;
            else if (_denyUntil is { } du && _clock.UtcNow < du) return false;
            else Set(LeaseState.Requesting, "request", null);
        }
        if (joinPoll) return await PollAsync(ct).ConfigureAwait(false);
        CommandResult<ControlRequestResultDto> r;
        try
        {
            r = await _rest.RequestControlAsync(_opt.ClientName, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            Fail("request:" + ex.Code, ex);
            if (ex.Status == 429) _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);
            throw;
        }
        catch (Exception ex) { Fail("request:error", ex); throw; }
        var res = r.Result ?? throw Fail("request:badResponse", new ApiException("BAD_RESPONSE", r.HttpStatus, "申请响应缺少 result"));
        lock (_lock)
        {
            _requestId = res.RequestId;
            _requestKey = res.RequestKey;
            if (res.Result == "granted" && !string.IsNullOrEmpty(res.LeaseId))
            {
                Grant(res.LeaseId!, res.LeaseExpiresAt, res.HeartbeatTimeoutSec, res.LeaseGraceSec, "granted");
                return true;
            }
            if (res.Result is "denied" or "expired") { Set(LeaseState.None, res.Result, null); return false; }
        }
        return await PollAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> PollAsync(CancellationToken ct)
    {
        var deadline = _clock.UtcNow + _opt.RequestTimeout;
        while (true)
        {
            string? rid, key;
            lock (_lock)
            {
                if (_state == LeaseState.Holding) return true;
                if (_state != LeaseState.Requesting) return false;
                rid = _requestId; key = _requestKey;
            }
            if (rid == null) { Set(LeaseState.None, "request:noId", null); return false; }
            ControlRequestStatusDto st;
            try { st = await _rest.RequestStatusAsync(rid, key, ct).ConfigureAwait(false); }
            catch (ApiException ex) when (ex.Status == 404) { Set(LeaseState.None, "requestExpired", null); return false; }
            catch (ApiException ex) { Fail("poll:" + ex.Code, ex); return false; }
            catch (OperationCanceledException) { Set(LeaseState.None, "request:cancelled", null); throw; }
            catch (Exception ex) { Error?.Invoke(ex); st = null!; }
            if (st != null)
            {
                switch (st.Result)
                {
                    case "granted":
                        if (string.IsNullOrEmpty(st.LeaseId)) { Fail("poll:noLeaseId", new ApiException("BAD_RESPONSE", 200, "granted 但未给 leaseId（缺 X-Control-Request-Key？）")); return false; }
                        lock (_lock) { if (_state != LeaseState.Requesting) return _state == LeaseState.Holding; Grant(st.LeaseId!, st.LeaseExpiresAt, st.HeartbeatTimeoutSec, st.LeaseGraceSec, "granted"); }
                        return true;
                    case "denied":
                        _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);
                        Set(LeaseState.None, "denied", null);
                        return false;
                    case "expired":
                        Set(LeaseState.None, "requestExpired", null);
                        return false;
                }
            }
            if (_clock.UtcNow >= deadline) { Set(LeaseState.None, "request:timeout", null); return false; }
            // 等一个轮询间隔，或 WS control 消息触发的提前轮询
            var wake = Volatile.Read(ref _pollNow);
            var delay = _clock.Delay(_opt.RequestPollInterval, ct);
            await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
            if (wake.Task.IsCompleted) Interlocked.Exchange(ref _pollNow, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            ct.ThrowIfCancellationRequested();
        }
    }

    private void Grant(string leaseId, DateTime? expires, int hbTimeout, int grace, string reason)
    {
        // 调用方持锁
        _leaseId = leaseId;
        _leaseExpiresAt = expires;
        if (hbTimeout > 0) _hbTimeoutSec = hbTimeout;
        if (grace > 0) _graceSec = grace;
        _hbFailures = 0;
        Set(LeaseState.Holding, reason, leaseId);
        StartHeartbeat();
    }

    // ======================================================================
    // 释放 / 外部事件
    // ======================================================================

    /// <summary>POST /control/release 并回到 None（没有租约时直接返回）。</summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        string? lease;
        lock (_lock) { lease = _leaseId; }
        StopHeartbeat();
        if (lease != null)
        {
            try { await _rest.ReleaseControlAsync(lease, ct).ConfigureAwait(false); }
            catch (ApiException ex) when (ex.IsLeaseError) { /* 已经不是我们的租约 */ }
        }
        lock (_lock) { if (_state == LeaseState.None && _leaseId == null) return; }   // WS control:released 可能已先到
        Set(LeaseState.None, "released", null);
    }

    /// <summary>WS <c>control</c> 消息（§8.2）：按 event 同步状态；申请相关事件触发提前轮询。</summary>
    public void OnControlMessage(ControlMsg m)
    {
        if (m == null) return;
        bool poke = false;
        lock (_lock)
        {
            bool mine = m.RequestId != null && m.RequestId == _requestId;
            bool holderIsMe = string.Equals(m.Holder, _opt.ClientName, StringComparison.Ordinal);
            switch (m.Event)
            {
                case "granted" or "denied" or "requestExpired":
                    if (_state == LeaseState.Requesting && mine) poke = true;
                    break;
                case "lost":
                    if (_state == LeaseState.Holding && (holderIsMe || mine)) Set(LeaseState.Lost, "lost", _leaseId);
                    break;
                case "resumed":
                    if (_state == LeaseState.Lost && (holderIsMe || mine)) Set(LeaseState.Holding, "resumed", _leaseId);
                    break;
                case "expired" or "takenBack" or "invalidated" or "released":
                    if (_state is LeaseState.Holding or LeaseState.Lost && (holderIsMe || mine || m.Mode == ControlModes.Local))
                    {
                        Revoke(m.Event);
                        if (m.Event == "expired" && _opt.AutoReacquireLease) _ = ReacquireAsync();
                    }
                    break;
            }
        }
        if (poke) Volatile.Read(ref _pollNow).TrySetResult(true);
    }

    /// <summary>GET /control 的状态同步（启动时 / 重连后）：holder 是本客户端但本地没有租约 → 不能恢复（leaseId 不可得），保持 None。</summary>
    public void OnControlState(ControlStateDto s)
    {
        if (s == null) return;
        lock (_lock)
        {
            if (_state is LeaseState.Holding or LeaseState.Lost)
            {
                bool holderIsMe = string.Equals(s.Holder, _opt.ClientName, StringComparison.Ordinal);
                if (s.Mode == ControlModes.Local || !holderIsMe)
                {
                    Revoke("state:" + s.Mode);
                    // 重连 / HMI 重启期间租约在服务端过期而回到本地：曾持有 → 同令牌 + 同 clientName 自动重申请（别人持有则不抢）
                    if (s.Mode == ControlModes.Local && _opt.AutoReacquireLease) _ = ReacquireAsync();
                }
                else if (s.State == ControlModes.RemoteLost && _state == LeaseState.Holding) Set(LeaseState.Lost, "state:remoteLost", _leaseId);
                else if (s.State == ControlModes.Remote && _state == LeaseState.Lost) Set(LeaseState.Holding, "state:remote", _leaseId);
            }
        }
    }

    /// <summary>受控写命令被拒：租约类 409 → 回到 None（并按配置自动重申请）。其他错误忽略。</summary>
    public void OnWriteFailed(ApiException ex)
    {
        if (ex == null || !ex.IsLeaseError) return;
        lock (_lock)
        {
            if (_state is LeaseState.Holding or LeaseState.Lost)
            {
                Revoke("write:" + ex.Code);
                if (ex.Code == OpCodes.ControlLeaseExpired && _opt.AutoReacquireLease) _ = ReacquireAsync();
            }
        }
    }

    private void Revoke(string reason)
    {
        // 调用方持锁
        _leaseId = null;
        _leaseExpiresAt = null;
        StopHeartbeat();
        Set(LeaseState.None, reason, null);
    }

    private async Task ReacquireAsync()
    {
        try
        {
            await Task.Yield();
            await AcquireAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    // ======================================================================
    // 心跳
    // ======================================================================

    private void StartHeartbeat()
    {
        StopHeartbeat();
        var cts = new CancellationTokenSource();
        _hbCts = cts;
        _hbTask = Task.Run(() => HeartbeatLoopAsync(cts.Token));
    }

    private void StopHeartbeat()
    {
        var cts = _hbCts;
        _hbCts = null;
        if (cts != null) { try { cts.Cancel(); } catch { } cts.Dispose(); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _clock.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            string? lease; LeaseState st;
            lock (_lock) { lease = _leaseId; st = _state; }
            if (lease == null || st is not (LeaseState.Holding or LeaseState.Lost)) return;
            try
            {
                // Lost 时一律走 REST：响应里的 resumed 立刻把状态拉回 Holding
                bool viaWs = st == LeaseState.Holding && WsHeartbeat != null && await WsHeartbeat(lease, ct).ConfigureAwait(false);
                if (!viaWs)
                {
                    var r = await _rest.HeartbeatAsync(lease, ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_leaseId != lease) continue;
                        _leaseExpiresAt = r.LeaseExpiresAt;
                        if (r.HeartbeatTimeoutSec > 0) _hbTimeoutSec = r.HeartbeatTimeoutSec;
                        if (_state == LeaseState.Lost && (r.Resumed || r.Mode == ControlModes.Remote)) Set(LeaseState.Holding, "resumed", lease);
                        else if (_state == LeaseState.Holding && r.Mode == ControlModes.RemoteLost) Set(LeaseState.Lost, "heartbeat:remoteLost", lease);
                    }
                }
                Interlocked.Exchange(ref _hbFailures, 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiException ex) when (ex.IsLeaseError || ex.Status == 403)
            {
                bool reacquire;
                lock (_lock)
                {
                    if (_leaseId != lease) continue;
                    _leaseId = null; _leaseExpiresAt = null;
                    Set(LeaseState.None, "heartbeat:" + ex.Code, null);
                    reacquire = ex.Code == OpCodes.ControlLeaseExpired && _opt.AutoReacquireLease;
                }
                if (reacquire) _ = ReacquireAsync();
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _hbFailures);
                Error?.Invoke(ex);
            }
        }
    }

    // ======================================================================
    // 内部
    // ======================================================================

    private void Set(LeaseState to, string reason, string? leaseId)
    {
        LeaseState from;
        lock (_lock)
        {
            from = _state;
            _state = to;
            if (to == LeaseState.None) { _leaseId = null; _leaseExpiresAt = null; }
        }
        var t = new LeaseTransition(from, to, reason, leaseId ?? (to == LeaseState.None ? null : _leaseId), _clock.UtcNow);
        try { Transitioned?.Invoke(t); } catch { }
    }

    private Exception Fail(string reason, Exception ex)
    {
        Set(LeaseState.None, reason, null);
        try { Error?.Invoke(ex); } catch { }
        return ex;
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
        var t = _hbTask;
        if (t != null) { try { await t.ConfigureAwait(false); } catch { } }
    }
}
