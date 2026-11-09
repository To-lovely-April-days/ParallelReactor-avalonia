using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>一次状态迁移（Reason：granted / denied / requestExpired / released / lost / resumed / expired / takenBack / invalidated / heartbeat:CODE / write:CODE / cancelled …）。</summary>
public sealed record LeaseTransition(LeaseState From, LeaseState To, string Reason, string? LeaseId, DateTime At);

/// <summary>
/// 控制权租约状态机（设计稿 §5.3–§5.5、§14.2）：申请 → 轮询 → 持有 → 心跳（优先 WS hb，WS 不在线走 REST）→ 丢失 / 恢复 / 过期 → 自动重申请。
/// 时间经 <see cref="IClock"/> 注入（单测手动推进）。所有带租约的写命令由调用方经 <see cref="LeaseId"/> 取当前租约；写命令被 409 租约类拒绝时调 <see cref="OnWriteFailed"/>。
/// WS <c>control</c> 消息不带 leaseId：它只用来触发提前轮询与 lost / resumed / revoked 的状态同步；leaseId 一律从 HTTP 拿。
/// <para>M3 增补（只加不改，原有原因字符串不变）：保存申请到期 / 丢失时刻 / 硬到期 / 冷却截止；<see cref="CancelRequestAsync"/>（R7：本地取消 + 后台守卫，
/// 触屏之后批准就立即释放，迁移原因 <c>cancelled</c>）；HMI 报告的持有者与待处理申请（R8：<see cref="Server"/>、<see cref="HeldByOther"/>、<see cref="PendingByOther"/>）。</para>
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

    // —— M3 增补 ——
    /// <summary>申请到期（服务端 UTC）、进入 Lost 的时刻（本机时钟）、租约硬到期（服务端 UTC）。</summary>
    private DateTime? _requestExpiresAt, _lostAt, _leaseHardExpiresAt;
    /// <summary>服务端时钟 − 本机时钟（ReactorConnection 在 WS hello 时设置）。</summary>
    private TimeSpan? _serverOffset;
    /// <summary>每发一次新的 POST /control/request 加一；_requestIdSeq：当前 _requestId 来自哪一次。</summary>
    private int _requestSeq, _requestIdSeq;
    /// <summary>在 POST 途中被取消、POST 还没回来的那几次申请的序号（POST 回来时交给对应的守卫并移除）。</summary>
    private readonly HashSet<int> _cancelledSeqs = new();
    /// <summary>后台守卫（每条被取消、还没结果的申请一个）。</summary>
    private readonly List<CancelGuard> _guards = new();
    /// <summary>本地取消过、没有被重新接手的申请 id（最近 16 条）：原来的轮询看到它就退出，不再改状态。</summary>
    private readonly HashSet<string> _cancelledIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _cancelledOrder = new();
    private ServerControlView? _server;
    private int _serverRequestTimeoutSec = 60;

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
    // M3 增补：时刻、取消守卫、HMI 报告的现状（只读）
    // ======================================================================

    /// <summary>
    /// 等待触屏确认的申请何时到期（<b>服务端 UTC</b>：POST /control/request 的 expiresAt，每次轮询更新）。只在 <see cref="LeaseState.Requesting"/> 时有值。
    /// 倒计时 = RequestExpiresAt − (<see cref="ServerClockOffset"/> ?? 0) − 本机 UtcNow。
    /// </summary>
    public DateTime? RequestExpiresAt { get { lock (_lock) return _requestExpiresAt; } }

    /// <summary>
    /// 进入 <see cref="LeaseState.Lost"/> 的时刻（<b>本机时钟</b>，<see cref="IClock.UtcNow"/>）；不在 Lost 时为 null。宽限截止 ≈ LostAt + <see cref="LeaseGraceSec"/>。
    /// 重连后由 GET /control 得知已丢失时，按 HMI 报告的硬到期（宽限截止）与 <see cref="ServerClockOffset"/> 反推（宽限截止仍 = LostAt + LeaseGraceSec）。
    /// </summary>
    public DateTime? LostAt { get { lock (_lock) return _lostAt; } }

    /// <summary>
    /// HMI 报告的租约硬到期（<b>服务端 UTC</b>；持有中 = 心跳截止 + 宽限，丢失中 = 宽限截止）：取自 REST 心跳响应与 GET /control（持有者是本机时）。
    /// WS 心跳没有应答，所以心跳走 WS 时它停在最后一次 REST 心跳 / GET /control 的值；授权时、回到 None 时清空。
    /// </summary>
    public DateTime? LeaseHardExpiresAt { get { lock (_lock) return _leaseHardExpiresAt; } }

    /// <summary>
    /// 触屏拒绝 / 429 后的本地冷却截止（<b>本机时钟</b>；HMI 对同一令牌冷却 30 s）。在此之前 <see cref="AcquireAsync"/> 直接返回 false、不发请求。
    /// 保存的是最后一次设置的值，可能已经过去（与 UtcNow 比较）。
    /// </summary>
    public DateTime? DenyUntil { get { lock (_lock) return _denyUntil; } }

    /// <summary>连续心跳网络失败的次数（成功一次清零；<see cref="HeartbeatDegraded"/> = ≥ 2）。</summary>
    public int HeartbeatFailures => Volatile.Read(ref _hbFailures);

    /// <summary>服务端时钟 − 本机时钟（null = 未知，按 0 算）。<see cref="ReactorConnection"/> 在每次 WS hello 时用 <see cref="TelemetryWsClient.ServerClockOffset"/> 设置。</summary>
    public TimeSpan? ServerClockOffset
    {
        get { lock (_lock) return _serverOffset; }
        set { lock (_lock) _serverOffset = value; }
    }

    /// <summary>
    /// 本地取消的申请有了结局（后台守卫结束；在线程池线程上触发）。<see cref="CancelledRequestOutcome.ApprovedAfterCancel"/> = 触屏在取消后批准、已自动释放（R7）。
    /// </summary>
    public event Action<CancelledRequestOutcome>? CancelledRequestSettled;

    /// <summary>还有被取消、等待结果的申请（后台守卫在跑）：界面可写「已取消；若触屏已批准将自动释放」。</summary>
    public bool CancelledRequestPending { get { lock (_lock) return _guards.Count > 0; } }

    /// <summary>最近一条还在等结果的已取消申请的 id（没有为 null）。</summary>
    public string? CancelledRequestId { get { lock (_lock) return _guards.Count > 0 ? _guards[^1].RequestId : null; } }

    /// <summary>等现在所有已取消申请的守卫结束（断开 / 移除设备前可以限时等它，保住 R7：守卫在 <see cref="DisposeAsync"/> 时会被停止）。</summary>
    public Task WaitCancelledRequestsAsync(CancellationToken ct = default)
    {
        Task[] ts;
        lock (_lock) ts = _guards.Select(g => g.Task).ToArray();
        return ts.Length == 0 ? Task.CompletedTask : Task.WhenAll(ts).WaitAsync(ct);
    }

    /// <summary>HMI 报告的控制权现状（GET /control + WS control 合并；还没收到为 null；断线时保持最后的内容）。</summary>
    public ServerControlView? Server { get { lock (_lock) return _server; } }
    /// <summary>HMI 报告的持有者客户端名（本地时为 null）。</summary>
    public string? Holder => Server?.Holder;
    /// <summary>持有者的名字就是本机的 <see cref="ClientOptions.ClientName"/>（注意：本地没有租约时，这是本机的孤儿租约或同名的另一台 PC）。</summary>
    public bool HolderIsMe { get { var h = Holder; return h != null && string.Equals(h, _opt.ClientName, StringComparison.Ordinal); } }
    /// <summary>R8：控制权被<b>其他</b>客户端持有（远程 / 宽限中，持有者不是本机名）：申请会被 409 NOT_CONTROL_HOLDER 拒绝。</summary>
    public bool HeldByOther
    {
        get
        {
            var v = Server;
            return v is { Holder.Length: > 0 }
                && !string.Equals(v.Holder, _opt.ClientName, StringComparison.Ordinal)
                && (v.Mode != ControlModes.Local || v.State is ControlModes.Remote or ControlModes.RemoteLost);
        }
    }
    /// <summary>HMI 上等待触屏处理的申请（可能是本机的，也可能是别人的）。</summary>
    public ControlPendingInfo? PendingRequest => Server?.Pending;
    /// <summary>R8：有<b>别的</b>客户端的申请在等触屏（本机此时申请会被 409 CONTROL_REQUEST_PENDING 拒绝）。本机的申请（含已取消、还在等结果的）不算。</summary>
    public bool PendingByOther { get { lock (_lock) return _server?.Pending is { } p && !IsMinePendingLocked(p); } }
    /// <summary><see cref="Server"/> 变了（在调用 <see cref="OnControlState"/> / <see cref="OnControlMessage"/> / <see cref="ObserveControlState"/> 的线程上、锁外触发）。</summary>
    public event Action? ServerViewChanged;

    // ======================================================================
    // 申请
    // ======================================================================

    /// <summary>
    /// 申请控制权并等到终态：granted → true（已 Holding、心跳已启动）；denied / 过期 / 404 / 超时 → false。
    /// 已是 Holding 直接返回 true；Requesting 中再次调用等待同一轮结果。
    /// M3：期间被 <see cref="CancelRequestAsync"/> 取消 → 返回 false（不抛异常）。
    /// </summary>
    public async Task<bool> AcquireAsync(CancellationToken ct)
    {
        bool joinPoll = false;
        int seq = 0;
        lock (_lock)
        {
            if (_state == LeaseState.Holding) return true;
            if (_state == LeaseState.Requesting) joinPoll = true;
            else if (_denyUntil is { } du && _clock.UtcNow < du) return false;
            else { seq = ++_requestSeq; Set(LeaseState.Requesting, "request", null); }
        }
        if (joinPoll) return await PollAsync(ct).ConfigureAwait(false);
        CommandResult<ControlRequestResultDto> r;
        try
        {
            r = await _rest.RequestControlAsync(_opt.ClientName, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            if (IsCancelledSeq(seq))   // M3：POST 途中已取消：不再报错，交给守卫收尾（HMI 回了错误 = 没有建出申请）
            {
                if (ex.Status == 429) lock (_lock) _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);
                HandOff(seq, null, answered: true);
                return false;
            }
            Fail("request:" + ex.Code, ex);
            if (ex.Status == 429) lock (_lock) _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);
            throw;
        }
        catch (Exception ex)
        {
            if (IsCancelledSeq(seq)) { HandOff(seq, null, answered: false); return false; }   // M3：网络 / 超时：申请建没建出来不知道
            Fail("request:error", ex); throw;
        }
        var res = r.Result;
        if (res == null)
        {
            if (IsCancelledSeq(seq)) { HandOff(seq, null, answered: true); return false; }   // M3
            throw Fail("request:badResponse", new ApiException("BAD_RESPONSE", r.HttpStatus, "申请响应缺少 result"));
        }
        bool cancelled;
        lock (_lock)
        {
            cancelled = _cancelledSeqs.Contains(seq);
            if (!cancelled)
            {
                _requestId = res.RequestId;
                _requestKey = res.RequestKey;
                _requestIdSeq = seq;                                   // M3
                if (res.Result == "pending") _requestExpiresAt = res.ExpiresAt;   // M3
                AdoptLocked(res.RequestId);                           // M3：HMI 返回的是刚取消的那条申请 → 守卫让出，由本次轮询接着等
                if (res.Result == "granted" && !string.IsNullOrEmpty(res.LeaseId))
                {
                    Grant(res.LeaseId!, res.LeaseExpiresAt, res.HeartbeatTimeoutSec, res.LeaseGraceSec, "granted");
                    return true;
                }
                if (res.Result is "denied" or "expired") { Set(LeaseState.None, res.Result, null); return false; }
            }
            else HandOffLocked(seq, res, answered: true);             // M3：POST 途中取消：pending → 守卫等结果；granted（autoGrant）→ 立即释放
        }
        if (cancelled) return false;
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
            catch (Exception) when (IsCancelledRequest(rid)) { return false; }   // M3：已取消：结果归后台守卫，这里不再改状态
            catch (ApiException ex) when (ex.Status == 404) { Set(LeaseState.None, "requestExpired", null); return false; }
            catch (ApiException ex) { Fail("poll:" + ex.Code, ex); return false; }
            catch (OperationCanceledException) { Set(LeaseState.None, "request:cancelled", null); throw; }
            catch (Exception ex) { Error?.Invoke(ex); st = null!; }
            if (IsCancelledRequest(rid)) return false;                           // M3：同上（取消发生在请求途中）
            if (st != null)
            {
                if (st.Result == "pending" && st.ExpiresAt != null) lock (_lock) { if (_requestId == rid) _requestExpiresAt = st.ExpiresAt; }   // M3
                switch (st.Result)
                {
                    case "granted":
                        if (string.IsNullOrEmpty(st.LeaseId)) { Fail("poll:noLeaseId", new ApiException("BAD_RESPONSE", 200, "granted 但未给 leaseId（缺 X-Control-Request-Key？）")); return false; }
                        lock (_lock) { if (_state != LeaseState.Requesting) return _state == LeaseState.Holding; Grant(st.LeaseId!, st.LeaseExpiresAt, st.HeartbeatTimeoutSec, st.LeaseGraceSec, "granted"); }
                        return true;
                    case "denied":
                        lock (_lock) _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);
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
        _leaseHardExpiresAt = null;   // M3：新租约，硬到期等第一次 REST 心跳 / GET /control
        if (hbTimeout > 0) _hbTimeoutSec = hbTimeout;
        if (grace > 0) _graceSec = grace;
        _hbFailures = 0;
        Set(LeaseState.Holding, reason, leaseId);
        StartHeartbeat();
    }

    // ======================================================================
    // M3：取消申请（R7）
    // ======================================================================

    /// <summary>
    /// 取消正在等待触屏确认的申请（HMI 没有取消端点，A5）：状态立刻回到 <see cref="LeaseState.None"/>（迁移原因 <c>cancelled</c>，界面显示「已取消」），
    /// 正在等的 <see cref="AcquireAsync"/> 随即返回 false。HMI 上这条申请仍在，所以后台守卫继续查它直到有结果（<see cref="CancelledRequestSettled"/>）：
    /// 触屏之后批准 → 立即 POST /control/release（不进入 Holding、不发心跳），不留没人心跳的孤儿租约（R7）；拒绝 → <see cref="DenyUntil"/>（HMI 冷却 30 s）。
    /// 再次 <see cref="AcquireAsync"/> 时 HMI 若返回同一条申请，守卫让出（<see cref="CancelledRequestOutcome.Adopted"/>），由新的申请接着等。
    /// 不在 Requesting 时什么也不做，返回 false。
    /// </summary>
    public Task<bool> CancelRequestAsync()
    {
        lock (_lock)
        {
            if (_state != LeaseState.Requesting) return Task.FromResult(false);
            if (_requestIdSeq == _requestSeq && _requestId != null)       // POST 已返回：这条申请的 id 已知，守卫直接查它
                StartGuardLocked(new CancelGuard(_requestId, _requestKey, 0));
            else                                                          // POST 还在途中：守卫先等 POST 的结果（HandOff）
            {
                _cancelledSeqs.Add(_requestSeq);
                StartGuardLocked(new CancelGuard(null, null, _requestSeq));
            }
            Set(LeaseState.None, "cancelled", null);
        }
        Volatile.Read(ref _pollNow).TrySetResult(true);                    // 叫醒原来的轮询：它看到 None 就返回 false
        return Task.FromResult(true);
    }

    private sealed class CancelGuard
    {
        public CancelGuard(string? requestId, string? requestKey, int seq)
        {
            RequestId = requestId;
            RequestKey = requestKey;
            Seq = seq;
        }

        /// <summary>申请 id（POST 途中取消时先为 null，POST 返回后由守卫填上；调用方持锁读写）。</summary>
        public string? RequestId;
        public string? RequestKey;
        /// <summary>POST 途中取消时是那一次申请的序号（等它的 HandOff）；0 = 取消时 id 已知。</summary>
        public readonly int Seq;
        /// <summary>POST 途中取消：POST 的结果（Res 为 null = 没有建出申请；Answered = HMI 回答了，false = 网络 / 超时，建没建出来不知道）。</summary>
        public readonly TaskCompletionSource<(ControlRequestResultDto? Res, bool Answered)> HandOff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Stop = new();
        public TaskCompletionSource<bool> Wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>新的一次申请拿到了同一条申请（调用方持锁读写）。</summary>
        public bool Adopted;
        public Task Task = Task.CompletedTask;
    }

    private bool IsCancelledSeq(int seq)
    {
        lock (_lock) return seq != 0 && _cancelledSeqs.Contains(seq);
    }

    private bool IsCancelledRequest(string rid)
    {
        lock (_lock) return _cancelledIds.Contains(rid);
    }

    private void RememberCancelledLocked(string rid)
    {
        if (!_cancelledIds.Add(rid)) return;
        _cancelledOrder.Enqueue(rid);
        while (_cancelledOrder.Count > 16) _cancelledIds.Remove(_cancelledOrder.Dequeue());
    }

    /// <summary>调用方持锁。新的 POST /control/request 拿到的是一条已取消的申请：它又是「本机正在等的申请」了。</summary>
    private void AdoptLocked(string requestId)
    {
        _cancelledIds.Remove(requestId);
        foreach (var g in _guards)
        {
            if (g.RequestId != requestId) continue;
            g.Adopted = true;
            g.Wake.TrySetResult(true);
        }
    }

    private void HandOff(int seq, ControlRequestResultDto? res, bool answered)
    {
        lock (_lock) HandOffLocked(seq, res, answered);
    }

    /// <summary>调用方持锁。POST 途中被取消的那次申请，POST 有结果了：交给等着它的守卫。</summary>
    private void HandOffLocked(int seq, ControlRequestResultDto? res, bool answered)
    {
        _cancelledSeqs.Remove(seq);
        foreach (var g in _guards)
            if (g.Seq == seq) g.HandOff.TrySetResult((res, answered));
    }

    /// <summary>调用方持锁。</summary>
    private void StartGuardLocked(CancelGuard g)
    {
        if (g.RequestId != null) RememberCancelledLocked(g.RequestId);
        _guards.Add(g);
        g.Task = Task.Run(() => GuardAsync(g));
    }

    /// <summary>后台守卫：查这条申请直到有结果；触屏批准了就立即释放。</summary>
    private async Task GuardAsync(CancelGuard g)
    {
        // 截止：本地申请上限 + 心跳超时 + 宽限（触屏批准后，没人心跳的租约在 HMI 上最多再活这么久；之后释放已没有意义）
        var deadline = _clock.UtcNow + _opt.RequestTimeout + TimeSpan.FromSeconds(HeartbeatTimeoutSec + LeaseGraceSec);
        string? result = null;
        string? lease = null;
        try
        {
            if (g.Seq != 0) (result, lease) = await TakeHandOffAsync(g).ConfigureAwait(false);
            while (result == null && lease == null)
            {
                if (IsAdopted(g)) { result = CancelledRequestOutcome.Adopted; break; }
                string rid; string? key;
                lock (_lock) { rid = g.RequestId!; key = g.RequestKey; }
                ControlRequestStatusDto? st = null;
                try { st = await _rest.RequestStatusAsync(rid, key, g.Stop.Token).ConfigureAwait(false); }
                catch (ApiException ex) when (ex.Status == 404) { result = CancelledRequestOutcome.Gone; break; }
                catch (OperationCanceledException) when (g.Stop.IsCancellationRequested) { result = CancelledRequestOutcome.Stopped; break; }
                catch (ObjectDisposedException) { result = CancelledRequestOutcome.Stopped; break; }
                catch (Exception ex) { RaiseError(ex); }   // 网络等：截止前继续查
                if (IsAdopted(g)) { result = CancelledRequestOutcome.Adopted; break; }
                if (st?.Result == "granted")
                {
                    if (string.IsNullOrEmpty(st.LeaseId)) result = CancelledRequestOutcome.NoLeaseId;
                    else lease = st.LeaseId;
                    break;
                }
                if (st?.Result == "denied")
                {
                    lock (_lock) _denyUntil = _clock.UtcNow + TimeSpan.FromSeconds(30);   // 与轮询相同：HMI 对该令牌冷却 30 s
                    result = CancelledRequestOutcome.Denied;
                    break;
                }
                if (st?.Result == "expired") { result = CancelledRequestOutcome.Expired; break; }
                if (_clock.UtcNow >= deadline) { result = CancelledRequestOutcome.Timeout; break; }
                if (!await WaitGuardAsync(g).ConfigureAwait(false)) { result = CancelledRequestOutcome.Stopped; break; }
            }
            if (result == null && lease != null)
                result = await ReleaseOrphanAsync(g, lease, deadline).ConfigureAwait(false) ? CancelledRequestOutcome.Released : CancelledRequestOutcome.ReleaseFailed;
        }
        catch (Exception ex) { RaiseError(ex); }
        finally
        {
            string rid;
            lock (_lock) { _guards.Remove(g); rid = g.RequestId ?? ""; }   // g.Stop 不释放：DisposeAsync 可能同时在 Cancel 它（没有计时器，交给 GC）
            RaiseSettled(new CancelledRequestOutcome(rid, result ?? CancelledRequestOutcome.Timeout, lease, _clock.UtcNow));
        }
    }

    /// <summary>
    /// POST 途中取消：等 POST 的结果。返回 (结局, null) = 不用再查；(null, leaseId) = 已授权（autoGrant），直接释放；(null, null) = pending，接着查。
    /// </summary>
    private async Task<(string? Result, string? Lease)> TakeHandOffAsync(CancelGuard g)
    {
        (ControlRequestResultDto? Res, bool Answered) h;
        try { h = await g.HandOff.Task.WaitAsync(g.Stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return (CancelledRequestOutcome.Stopped, null); }
        if (h.Res is not { } res) return (h.Answered ? CancelledRequestOutcome.Gone : CancelledRequestOutcome.Timeout, null);
        lock (_lock)
        {
            g.RequestId = res.RequestId;
            g.RequestKey = res.RequestKey;
            // 更新的一次申请已经拿到同一条申请（两次 POST 乱序返回）：归它，不守卫（否则会把用户要的租约释放掉）
            if (res.RequestId == _requestId && _requestIdSeq > g.Seq && _state is LeaseState.Requesting or LeaseState.Holding or LeaseState.Lost)
                return (CancelledRequestOutcome.Adopted, null);
            RememberCancelledLocked(res.RequestId);
        }
        if (res.Result == "granted") return string.IsNullOrEmpty(res.LeaseId) ? (CancelledRequestOutcome.NoLeaseId, null) : (null, res.LeaseId);
        if (res.Result == "denied") return (CancelledRequestOutcome.Denied, null);
        if (res.Result == "expired") return (CancelledRequestOutcome.Expired, null);
        return (null, null);
    }

    private bool IsAdopted(CancelGuard g)
    {
        lock (_lock) return g.Adopted;
    }

    /// <summary>等一个轮询间隔或 WS 提前唤醒；守卫被停止返回 false。被唤醒时撤掉那个等待（假时钟随即移除它）。</summary>
    private async Task<bool> WaitGuardAsync(CancelGuard g)
    {
        TaskCompletionSource<bool> wake;
        lock (_lock) wake = g.Wake;
        if (g.Stop.IsCancellationRequested) return false;
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(g.Stop.Token);
        var delay = _clock.Delay(_opt.RequestPollInterval, waitCts.Token);
        var first = await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
        if (first != delay)
        {
            waitCts.Cancel();
            lock (_lock) { if (g.Wake == wake) g.Wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        return !g.Stop.IsCancellationRequested;
    }

    /// <summary>释放触屏在取消之后批准的租约。租约类错误（已过期 / 已被收回）= 没有遗留，也算成功。释放本身不受停止信号打断（各次请求自带超时）。</summary>
    private async Task<bool> ReleaseOrphanAsync(CancelGuard g, string lease, DateTime deadline)
    {
        while (true)
        {
            try
            {
                using var cts = new CancellationTokenSource(_opt.HttpTimeout + TimeSpan.FromSeconds(1));
                await _rest.ReleaseControlAsync(lease, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.IsLeaseError) { return true; }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex) { RaiseError(ex); }
            if (_clock.UtcNow >= deadline) return false;
            if (!await WaitGuardAsync(g).ConfigureAwait(false)) return false;
        }
    }

    private void RaiseSettled(CancelledRequestOutcome o)
    {
        try { CancelledRequestSettled?.Invoke(o); } catch { }
    }

    private void RaiseError(Exception ex)
    {
        try { Error?.Invoke(ex); } catch { }
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
        List<CancelGuard>? pokeGuards = null;   // M3
        bool viewChanged;                        // M3
        lock (_lock)
        {
            viewChanged = ObserveLocked(m);      // M3：先记下 HMI 报告的现状（不改租约状态）
            if (m.RequestId != null)
                foreach (var g in _guards) if (g.RequestId == m.RequestId) (pokeGuards ??= new()).Add(g);
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
        if (pokeGuards != null) foreach (var g in pokeGuards) { TaskCompletionSource<bool> w; lock (_lock) w = g.Wake; w.TrySetResult(true); }   // M3：已取消申请的结果到了，守卫立刻去查
        if (viewChanged) RaiseViewChanged();
    }

    /// <summary>GET /control 的状态同步（启动时 / 重连后）：holder 是本客户端但本地没有租约 → 不能恢复（leaseId 不可得），保持 None。</summary>
    public void OnControlState(ControlStateDto s)
    {
        if (s == null) return;
        bool viewChanged;   // M3
        lock (_lock)
        {
            viewChanged = ObserveLocked(s);   // M3
            if (_state is LeaseState.Holding or LeaseState.Lost)
            {
                bool holderIsMe = string.Equals(s.Holder, _opt.ClientName, StringComparison.Ordinal);
                if (s.Mode == ControlModes.Local || !holderIsMe)
                {
                    Revoke("state:" + s.Mode);
                    // 重连 / HMI 重启期间租约在服务端过期而回到本地：曾持有 → 同令牌 + 同 clientName 自动重申请（别人持有则不抢）
                    if (s.Mode == ControlModes.Local && _opt.AutoReacquireLease) _ = ReacquireAsync();
                }
                else
                {
                    if (s.LeaseHardExpiresAt != null) _leaseHardExpiresAt = s.LeaseHardExpiresAt;   // M3
                    if (s.State == ControlModes.RemoteLost && _state == LeaseState.Holding) Set(LeaseState.Lost, "state:remoteLost", _leaseId, LostAtFromServerLocked(s));
                    else if (s.State == ControlModes.Remote && _state == LeaseState.Lost) Set(LeaseState.Holding, "state:remote", _leaseId);
                }
            }
        }
        if (viewChanged) RaiseViewChanged();
    }

    /// <summary>
    /// M3：只记录 GET /control 报告的现状（持有者、待处理申请、申请超时），不动租约状态。
    /// <see cref="ReactorConnection.StartAsync"/> 对首次 GET /control 调用它（之后每次 hello 走 <see cref="OnControlState"/>，同样会记录）。
    /// </summary>
    public void ObserveControlState(ControlStateDto s)
    {
        if (s == null) return;
        bool viewChanged;
        lock (_lock) viewChanged = ObserveLocked(s);
        if (viewChanged) RaiseViewChanged();
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
    // M3：HMI 报告的现状（R8）
    // ======================================================================

    /// <summary>调用方持锁。</summary>
    private bool ObserveLocked(ControlStateDto s)
    {
        if (s.RequestTimeoutSec > 0) _serverRequestTimeoutSec = s.RequestTimeoutSec;
        var p = s.Pending is { } d ? new ControlPendingInfo(d.RequestId, d.ClientName, d.TokenName, d.ExpiresAt) : null;
        return UpdateServerLocked(new ServerControlView(s.Mode ?? "", s.State ?? "", s.Holder, p));
    }

    /// <summary>调用方持锁。WS control 消息不带申请人：requested 时从 detail 解析，到期按「消息时刻 + 申请超时」估算；GET /control 已给出同一条申请时保留那份准确的。</summary>
    private bool ObserveLocked(ControlMsg m)
    {
        ControlPendingInfo? p = null;
        if (m.State == ControlModes.Requested)
        {
            var prev = _server?.Pending;
            if (m.RequestId != null && prev?.RequestId != m.RequestId && m.Event == "requested")
            {
                var (client, token) = ParseRequester(m.Detail);
                p = new ControlPendingInfo(m.RequestId, client, token, m.Ts.AddSeconds(_serverRequestTimeoutSec));
            }
            else p = prev;
        }
        return UpdateServerLocked(new ServerControlView(m.Mode ?? "", m.State ?? "", m.Holder, p));
    }

    private bool UpdateServerLocked(ServerControlView v)
    {
        if (Equals(_server, v)) return false;
        _server = v;
        return true;
    }

    /// <summary>
    /// HMI 的 requested 事件 detail（Commands/ControlLease.cs Request）：「{clientName}（令牌 {tokenName}）申请远程控制 · …」。格式不符返回 (null, null)。
    /// </summary>
    internal static (string? Client, string? Token) ParseRequester(string? detail)
    {
        const string mark = "（令牌 ";
        if (string.IsNullOrEmpty(detail)) return (null, null);
        int i = detail.IndexOf(mark, StringComparison.Ordinal);
        if (i <= 0) return (null, null);
        int j = detail.IndexOf('）', i + mark.Length);
        return (detail[..i], j > i + mark.Length ? detail[(i + mark.Length)..j] : null);
    }

    /// <summary>调用方持锁。这条待处理申请是不是本机的（当前申请 / 已取消还在等结果的 / 同名客户端 / 名字未知而本机有 POST 在途中）。</summary>
    private bool IsMinePendingLocked(ControlPendingInfo p)
    {
        if (p.RequestId == _requestId) return true;
        if (_cancelledIds.Contains(p.RequestId)) return true;
        if (p.ClientName != null) return string.Equals(p.ClientName, _opt.ClientName, StringComparison.Ordinal);
        return (_state == LeaseState.Requesting && _requestIdSeq != _requestSeq) || _cancelledSeqs.Count > 0;
    }

    /// <summary>调用方持锁。GET /control 说已丢失：HMI 的硬到期 = 宽限截止，反推本机时钟上的丢失时刻（宽限截止 = LostAt + LeaseGraceSec）。</summary>
    private DateTime? LostAtFromServerLocked(ControlStateDto s)
    {
        if (s.LeaseHardExpiresAt is not { } hard) return null;
        int grace = s.LeaseGraceSec > 0 ? s.LeaseGraceSec : _graceSec;
        return hard - (_serverOffset ?? TimeSpan.Zero) - TimeSpan.FromSeconds(grace);
    }

    private void RaiseViewChanged()
    {
        try { ServerViewChanged?.Invoke(); } catch { }
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
                        _leaseHardExpiresAt = r.LeaseHardExpiresAt;   // M3
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

    private void Set(LeaseState to, string reason, string? leaseId, DateTime? lostAt = null)
    {
        LeaseState from;
        var now = _clock.UtcNow;
        lock (_lock)
        {
            from = _state;
            _state = to;
            if (to == LeaseState.None) { _leaseId = null; _leaseExpiresAt = null; }
            // M3：申请到期只在 Requesting 有意义；丢失时刻只在 Lost 有意义（进入时记下，离开时清掉）；回到 None 时硬到期随租约清掉
            if (to != LeaseState.Requesting) _requestExpiresAt = null;
            if (to == LeaseState.Lost) { if (from != LeaseState.Lost || lostAt != null) _lostAt = lostAt is { } la && la < now ? la : now; }
            else _lostAt = null;
            if (to == LeaseState.None) _leaseHardExpiresAt = null;
        }
        var t = new LeaseTransition(from, to, reason, leaseId ?? (to == LeaseState.None ? null : _leaseId), now);
        try { Transitioned?.Invoke(t); } catch { }
    }

    private Exception Fail(string reason, Exception ex)
    {
        Set(LeaseState.None, reason, null);
        try { Error?.Invoke(ex); } catch { }
        return ex;
    }

    /// <summary>
    /// 停止心跳；M3：同时停止已取消申请的后台守卫并等它们结束（正在进行的释放请求会做完，最多约 <see cref="ClientOptions.HttpTimeout"/>）。
    /// 要在断开后仍保住 R7，先限时 await <see cref="WaitCancelledRequestsAsync"/> 再释放。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
        CancelGuard[] guards;
        lock (_lock) guards = _guards.ToArray();
        foreach (var g in guards) { try { g.Stop.Cancel(); } catch { } }
        var t = _hbTask;
        if (t != null) { try { await t.ConfigureAwait(false); } catch { } }
        foreach (var g in guards) { try { await g.Task.ConfigureAwait(false); } catch { } }
    }
}
