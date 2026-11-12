using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M3 的 Client 增补（PORT-PLAN §5 M3「Client」，只加不改；R7 / R8 / R9 / R19）：
/// 租约的时刻（RequestExpiresAt / LostAt / LeaseHardExpiresAt / DenyUntil）、取消申请 + 后台守卫（迟到的批准 → 自动释放）、HMI 报告的持有者与待处理申请；
/// WS 的 EverConnected / NextRetryAt / LastError（503 TOO_MANY_CLIENTS 归类，含真 ClientWebSocket 对进程内假服务端）/ ReconnectNow；
/// 连接门面的 MessageQueueCapacity、403 FORBIDDEN_ROLE 报告、新事件经 UiContext 投递、接口默认实现。全部进程内假对象（ScriptedHttpHandler / FakeWsServer / FakeClock）。
/// </summary>
public class ClientAdditionsTests
{
    private const string Me = "PC-测试工位1";

    private sealed class Flag
    {
        private int _v;
        public bool On { get => Volatile.Read(ref _v) != 0; set => Volatile.Write(ref _v, value ? 1 : 0); }
    }

    /// <summary>小步推进假时钟（每步让续体跑一下），直到条件成立。等待挂在哪个 Delay 上不确定时用它，不靠「Pending ≥ 1」。</summary>
    private static async Task AdvanceUntil(FakeClock clock, Func<bool> cond, string what, double stepSec = 0.5, int maxSteps = 400)
    {
        for (int i = 0; i < maxSteps && !cond(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(stepSec));
            await Task.Delay(10);
        }
        await FakeClock.WaitUntil(cond, what: what);
    }

    // ====================================================================== 租约 rig

    private sealed class LeaseRig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly ScriptedHttpHandler Http = new();
        public readonly ClientOptions Opt;
        public readonly ReactorApiClient Rest;
        public readonly ControlLeaseClient Lease;
        private readonly List<LeaseTransition> _transitions = new();
        private readonly List<CancelledRequestOutcome> _settled = new();
        private int _viewChanges;

        public LeaseRig(bool autoReacquire = false)
        {
            Opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", Me) { AutoReacquireLease = autoReacquire, MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
            Rest = new ReactorApiClient(Opt, Http, Clock);
            Lease = new ControlLeaseClient(Rest, Opt, Clock);
            Lease.Transitioned += t => { lock (_transitions) _transitions.Add(t); };
            Lease.CancelledRequestSettled += o => { lock (_settled) _settled.Add(o); };
            Lease.ServerViewChanged += () => Interlocked.Increment(ref _viewChanges);
        }

        public int ViewChanges => Volatile.Read(ref _viewChanges);
        public string Reasons() { lock (_transitions) return string.Join(" ", _transitions.Select(t => $"{t.From}->{t.To}:{t.Reason}")); }
        public CancelledRequestOutcome? Outcome { get { lock (_settled) return _settled.LastOrDefault(); } }
        public int SettledCount { get { lock (_settled) return _settled.Count; } }
        public CancelledRequestOutcome[] Outcomes { get { lock (_settled) return _settled.ToArray(); } }

        /// <summary>POST /control/request → pending（每次都是同一条申请：HMI 对同令牌 + 同客户端名返回原申请）。</summary>
        public void RequestPending(string rid, string key, DateTime expires)
            => Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto(rid, "pending", expires, null, null, 10, 60, key)));

        public void AutoGrant(int hbTimeout = 10, int grace = 60)
        {
            int n = 0;
            Http.On("POST", "/control/request", call => { int k = Interlocked.Increment(ref n); return ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq" + k, "granted", null, "L" + k, Clock.UtcNow.AddSeconds(hbTimeout), hbTimeout, grace, "key" + k)); });
        }

        public void HeartbeatOk(DateTime? hard = null)
            => Http.On("POST", "/control/heartbeat", call => ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, Clock.UtcNow.AddSeconds(10), hard ?? Clock.UtcNow.AddSeconds(70), 10)));

        public void ReleaseOk() => Http.On("POST", "/control/release", call => ScriptedHttpHandler.Command(call, false, new { mode = "local" }));

        public ControlRequestStatusDto Status(string rid, string result, DateTime? expires = null, string? lease = null)
            => new(rid, result, Me, expires, lease, lease == null ? null : Clock.UtcNow.AddSeconds(10), 10, 60);

        public ControlMsg Msg(string ev, string? requestId = null, string mode = "remote", string? holder = Me, string state = "remote", string detail = "")
            => new("control", ev, mode, holder, state, requestId, null, null, null, 10, detail, Clock.UtcNow);

        public async ValueTask DisposeAsync() { await Lease.DisposeAsync(); Rest.Dispose(); }
    }

    // ====================================================================== R7：取消申请 + 后台守卫

    [Fact]
    public async Task Cancel_WhilePending_NoneAtOnce_LateApproval_IsReleasedAutomatically()
    {
        await using var r = new LeaseRig();
        var t0 = r.Clock.UtcNow;
        r.RequestPending("rqX", "KX", t0.AddSeconds(60));
        var approved = new Flag();
        r.Http.On("GET", "/control/requests/rqX", call => approved.On
            ? ScriptedHttpHandler.Ok(r.Status("rqX", "granted", lease: call.RequestKey == "KX" ? "LX" : null))
            : ScriptedHttpHandler.Ok(r.Status("rqX", "pending", expires: t0.AddSeconds(58))));
        r.ReleaseOk();

        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Lease.RequestExpiresAt == t0.AddSeconds(58), what: "首次轮询更新申请到期（服务端 UTC）");
        Assert.Equal(LeaseState.Requesting, r.Lease.State);

        Assert.True(await r.Lease.CancelRequestAsync());
        // 界面立刻回到本地只读：None + 原因 cancelled；原来的 AcquireAsync 不等时钟就返回 false
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Null(r.Lease.RequestExpiresAt);
        Assert.Contains("Requesting->None:cancelled", r.Reasons());
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(r.Lease.CancelledRequestPending);
        Assert.Equal("rqX", r.Lease.CancelledRequestId);

        // HMI 上申请还在：守卫在后台继续查
        int polls = r.Http.Count("GET", "/control/requests/rqX");
        await AdvanceUntil(r.Clock, () => r.Http.Count("GET", "/control/requests/rqX") >= polls + 2, "守卫继续轮询");
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Equal(0, r.Http.Count("POST", "/control/release"));
        Assert.Null(r.Outcome);

        // 触屏这时才批准 → 守卫拿到 leaseId 立刻释放；本地从不进入 Holding、不发心跳
        approved.On = true;
        await AdvanceUntil(r.Clock, () => r.Outcome != null, "守卫结束");
        var o = r.Outcome!;
        Assert.Equal(CancelledRequestOutcome.Released, o.Result);
        Assert.Equal("rqX", o.RequestId);
        Assert.Equal("LX", o.LeaseId);
        Assert.True(o.ApprovedAfterCancel);
        Assert.False(o.OrphanPossible);
        Assert.Equal("LX", r.Http.Of("POST", "/control/release").Single().Lease);
        Assert.All(r.Http.Of("GET", "/control/requests/rqX"), c => Assert.Equal("KX", c.RequestKey));
        Assert.DoesNotContain("Holding", r.Reasons());
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Null(r.Lease.LeaseId);
        Assert.False(r.Lease.CancelledRequestPending);
        Assert.Null(r.Lease.CancelledRequestId);
        Assert.Equal(0, r.Http.Count("POST", "/control/heartbeat"));
        Assert.Equal(1, r.SettledCount);
    }

    [Fact]
    public async Task Cancel_ThenTouchScreenDenies_SettlesDenied_AndDenyWindowBlocksNewRequests()
    {
        await using var r = new LeaseRig();
        r.RequestPending("rqD", "KD", r.Clock.UtcNow.AddSeconds(60));
        var denied = new Flag();
        r.Http.On("GET", "/control/requests/rqD", _ => ScriptedHttpHandler.Ok(r.Status("rqD", denied.On ? "denied" : "pending")));
        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqD") == 1, what: "首次轮询");
        Assert.Null(r.Lease.DenyUntil);
        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));

        denied.On = true;
        await AdvanceUntil(r.Clock, () => r.Outcome != null, "守卫结束");
        Assert.Equal(CancelledRequestOutcome.Denied, r.Outcome!.Result);
        Assert.Null(r.Outcome.LeaseId);
        Assert.False(r.Outcome.ApprovedAfterCancel);
        Assert.False(r.Outcome.OrphanPossible);
        // HMI 对该令牌冷却 30 s：本地同样记下，期间 AcquireAsync 直接 false、不发请求；守卫本身不产生迁移
        var until = r.Lease.DenyUntil;
        Assert.NotNull(until);
        Assert.InRange((until!.Value - r.Clock.UtcNow).TotalSeconds, 25, 30);
        int posts = r.Http.Count("POST", "/control/request");
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(posts, r.Http.Count("POST", "/control/request"));
        Assert.DoesNotContain("None->None", r.Reasons());
        Assert.Equal(0, r.Http.Count("POST", "/control/release"));
        // 冷却过后照常申请（现有流程：轮询 denied → 原因 denied 不变）
        r.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(posts + 1, r.Http.Count("POST", "/control/request"));
        Assert.EndsWith("Requesting->None:denied", r.Reasons());
    }

    [Fact]
    public async Task Cancel_WhilePostInFlight_AutoGrant_IsReleasedWithoutEverHolding()
    {
        await using var r = new LeaseRig();
        using var gate = new ManualResetEventSlim(false);
        r.Http.On("POST", "/control/request", call =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqG", "granted", null, "LG", r.Clock.UtcNow.AddSeconds(10), 10, 60, "KG"));
        });
        r.ReleaseOk();
        var acquire = Task.Run(() => r.Lease.AcquireAsync(CancellationToken.None));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/request") == 1, what: "POST 已发出");
        Assert.Equal(LeaseState.Requesting, r.Lease.State);

        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.True(r.Lease.CancelledRequestPending);        // 守卫已在等 POST 的结果
        Assert.Null(r.Lease.CancelledRequestId);              // id 还不知道
        gate.Set();                                           // autoGrant：POST 直接带回租约
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        await FakeClock.WaitUntil(() => r.Outcome != null, what: "守卫释放");
        Assert.Equal(CancelledRequestOutcome.Released, r.Outcome!.Result);
        Assert.Equal("rqG", r.Outcome.RequestId);
        Assert.Equal("LG", r.Outcome.LeaseId);
        Assert.Equal("LG", r.Http.Of("POST", "/control/release").Single().Lease);
        Assert.DoesNotContain("Holding", r.Reasons());
        Assert.Null(r.Lease.LeaseId);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Equal(0, r.Http.Count("POST", "/control/heartbeat"));
    }

    [Fact]
    public async Task Cancel_Twice_WhileBothPostsInFlight_NeitherAttemptEverHolds()
    {
        // 取消 → 又申请 → 又取消，两次 POST 都还没回来（第二次排在写信号量后面）：两次都要交给各自的守卫，绝不进入 Holding
        await using var r = new LeaseRig();
        using var gate = new ManualResetEventSlim(false);
        r.Http.On("POST", "/control/request", (call, n) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return n == 0
                ? ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq1", "granted", null, "LG1", r.Clock.UtcNow.AddSeconds(10), 10, 60, "K1"))
                : ScriptedHttpHandler.CommandError(call, OpCodes.NotControlHolder, "当前控制权属于「" + Me + "」");
        });
        r.ReleaseOk();
        var first = Task.Run(() => r.Lease.AcquireAsync(CancellationToken.None));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/request") == 1, what: "第一次 POST 已发出");
        Assert.True(await r.Lease.CancelRequestAsync());
        var second = Task.Run(() => r.Lease.AcquireAsync(CancellationToken.None));
        await FakeClock.WaitUntil(() => r.Lease.State == LeaseState.Requesting, what: "第二次申请");
        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.Equal(LeaseState.None, r.Lease.State);
        gate.Set();
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        await FakeClock.WaitUntil(() => r.SettledCount == 2, what: "两个守卫都结束");
        Assert.Equal(new[] { CancelledRequestOutcome.Gone, CancelledRequestOutcome.Released }, r.Outcomes.Select(o => o.Result).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("LG1", r.Http.Of("POST", "/control/release").Single().Lease);
        Assert.DoesNotContain("Holding", r.Reasons());
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.False(r.Lease.CancelledRequestPending);
    }

    [Fact]
    public async Task Cancel_WhilePostInFlight_PostRejected_SettlesGone_NoError()
    {
        await using var r = new LeaseRig();
        using var gate = new ManualResetEventSlim(false);
        r.Http.On("POST", "/control/request", call => { gate.Wait(TimeSpan.FromSeconds(10)); return ScriptedHttpHandler.CommandError(call, OpCodes.ControlRequestPending, "已有待处理的远程申请（PC-B）"); });
        var errors = new List<Exception>();
        r.Lease.Error += e => { lock (errors) errors.Add(e); };
        var acquire = Task.Run(() => r.Lease.AcquireAsync(CancellationToken.None));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/request") == 1);
        Assert.True(await r.Lease.CancelRequestAsync());
        gate.Set();
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(5)));   // 已取消：不再抛 409
        await FakeClock.WaitUntil(() => r.Outcome != null, what: "守卫结束");
        Assert.Equal(CancelledRequestOutcome.Gone, r.Outcome!.Result);
        Assert.Equal("", r.Outcome.RequestId);
        Assert.False(r.Outcome.OrphanPossible);
        Assert.Equal("None->Requesting:request Requesting->None:cancelled", r.Reasons());
        lock (errors) Assert.Empty(errors);
    }

    [Fact]
    public async Task Cancel_WsMessageForCancelledRequest_WakesGuardWithoutClock()
    {
        await using var r = new LeaseRig();
        r.RequestPending("rqW", "KW", r.Clock.UtcNow.AddSeconds(60));
        var approved = new Flag();
        r.Http.On("GET", "/control/requests/rqW", call => ScriptedHttpHandler.Ok(approved.On ? r.Status("rqW", "granted", lease: "LW") : r.Status("rqW", "pending")));
        r.ReleaseOk();
        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqW") == 1);
        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqW") == 2, what: "守卫第一次查（pending）");
        await Task.Delay(50);
        var clockBefore = r.Clock.UtcNow;
        // 触屏批准：HMI 推 WS control granted（requestId = 已取消的那条）→ 守卫立刻去查、释放；时钟不动
        approved.On = true;
        r.Lease.OnControlMessage(r.Msg("granted", requestId: "rqW"));
        await FakeClock.WaitUntil(() => r.Outcome != null, what: "WS 唤醒守卫");
        Assert.Equal(CancelledRequestOutcome.Released, r.Outcome!.Result);
        Assert.Equal(clockBefore, r.Clock.UtcNow);
        Assert.Equal(LeaseState.None, r.Lease.State);   // 别的 WS 消息照常不影响 None
        Assert.DoesNotContain("Holding", r.Reasons());
    }

    [Fact]
    public async Task Cancel_ThenRequestAgain_SameRequestId_IsAdopted_NotReleased()
    {
        await using var r = new LeaseRig();
        r.RequestPending("rqA", "KA", r.Clock.UtcNow.AddSeconds(60));
        var approved = new Flag();
        r.Http.On("GET", "/control/requests/rqA", call => ScriptedHttpHandler.Ok(approved.On ? r.Status("rqA", "granted", lease: call.RequestKey == "KA" ? "LA" : null) : r.Status("rqA", "pending")));
        r.HeartbeatOk();
        r.ReleaseOk();
        var first = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqA") == 1);
        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqA") == 2, what: "守卫第一次查");

        // 用户改主意又申请：HMI 返回同一条申请 → 守卫让出（不释放），由新的申请等结果
        var second = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Outcome != null, what: "守卫让出");
        Assert.Equal(CancelledRequestOutcome.Adopted, r.Outcome!.Result);
        Assert.False(r.Lease.CancelledRequestPending);
        Assert.Equal(LeaseState.Requesting, r.Lease.State);
        approved.On = true;
        await AdvanceUntil(r.Clock, () => second.IsCompleted, "新的申请拿到批准");
        Assert.True(await second);
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        Assert.Equal("LA", r.Lease.LeaseId);
        Assert.Equal(0, r.Http.Count("POST", "/control/release"));
        Assert.Contains("Requesting->Holding:granted", r.Reasons());
    }

    [Fact]
    public async Task Cancel_NothingInProgress_ReturnsFalse_AndChangesNothing()
    {
        await using var r = new LeaseRig();
        Assert.False(await r.Lease.CancelRequestAsync());
        Assert.Equal("", r.Reasons());
        r.AutoGrant();
        r.HeartbeatOk();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.False(await r.Lease.CancelRequestAsync());   // 已持有：取消申请不等于释放
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        Assert.False(r.Lease.CancelledRequestPending);
    }

    [Fact]
    public async Task Cancel_DisposeStopsGuard_SettlesStopped_OrphanPossible()
    {
        await using var r = new LeaseRig();   // 下面先显式释放租约客户端；rig 的释放再调一次（可重复）
        r.RequestPending("rqS", "KS", r.Clock.UtcNow.AddSeconds(60));
        r.Http.On("GET", "/control/requests/rqS", _ => ScriptedHttpHandler.Ok(r.Status("rqS", "pending")));
        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqS") == 1);
        Assert.True(await r.Lease.CancelRequestAsync());
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqS") == 2, what: "守卫第一次查");
        var waiting = r.Lease.WaitCancelledRequestsAsync();
        Assert.False(waiting.IsCompleted);
        await r.Lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));   // 不卡住退出
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(CancelledRequestOutcome.Stopped, r.Outcome!.Result);
        Assert.True(r.Outcome.OrphanPossible);
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Cancel_RequestExpiresOnHmi_WaitCancelledRequestsCompletes()
    {
        await using var r = new LeaseRig();
        r.RequestPending("rqE", "KE", r.Clock.UtcNow.AddSeconds(60));
        var expired = new Flag();
        r.Http.On("GET", "/control/requests/rqE", _ => ScriptedHttpHandler.Ok(r.Status("rqE", expired.On ? "expired" : "pending")));
        _ = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqE") == 1);
        Assert.True(await r.Lease.CancelRequestAsync());
        var waiting = r.Lease.WaitCancelledRequestsAsync();
        Assert.False(waiting.IsCompleted);
        expired.On = true;
        await AdvanceUntil(r.Clock, () => waiting.IsCompleted, "申请在 HMI 上超时");
        Assert.Equal(CancelledRequestOutcome.Expired, r.Outcome!.Result);
        Assert.Null(r.Lease.DenyUntil);   // 与原来的轮询一致：超时不设冷却
        Assert.True(r.Lease.WaitCancelledRequestsAsync().IsCompleted);
    }

    // ====================================================================== 时刻：RequestExpiresAt / LostAt / LeaseHardExpiresAt

    [Fact]
    public async Task RequestExpiresAt_FromPostThenPolls_ClearedOnGrant()
    {
        await using var r = new LeaseRig();
        var t0 = r.Clock.UtcNow;
        r.RequestPending("rqT", "KT", t0.AddSeconds(60));
        r.HeartbeatOk();
        var granted = new Flag();
        r.Http.On("GET", "/control/requests/rqT", _ => ScriptedHttpHandler.Ok(granted.On ? r.Status("rqT", "granted", lease: "LT") : r.Status("rqT", "pending", expires: t0.AddSeconds(59))));
        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Lease.RequestExpiresAt == t0.AddSeconds(59), what: "轮询更新到期");
        granted.On = true;
        await AdvanceUntil(r.Clock, () => acquire.IsCompleted, "批准");
        Assert.True(await acquire);
        Assert.Null(r.Lease.RequestExpiresAt);
        Assert.Null(r.Lease.LeaseHardExpiresAt);   // 新租约：等第一次 REST 心跳
    }

    [Fact]
    public async Task LostAt_SetOnLost_ClearedOnResume_HardExpiryFromRestHeartbeat()
    {
        await using var r = new LeaseRig();
        r.AutoGrant(hbTimeout: 10, grace: 60);
        var hard = r.Clock.UtcNow.AddSeconds(77);
        r.HeartbeatOk(hard);
        r.ReleaseOk();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Null(r.Lease.LostAt);
        await AdvanceUntil(r.Clock, () => r.Lease.LeaseHardExpiresAt != null, "第一次 REST 心跳");
        Assert.Equal(hard, r.Lease.LeaseHardExpiresAt);

        r.Lease.OnControlMessage(r.Msg("lost", state: "remoteLost"));
        Assert.Equal(LeaseState.Lost, r.Lease.State);
        Assert.Equal(r.Clock.UtcNow, r.Lease.LostAt);      // 本机时钟：宽限截止 ≈ LostAt + LeaseGraceSec
        Assert.Equal(60, r.Lease.LeaseGraceSec);
        var lostAt = r.Lease.LostAt;
        r.Clock.Advance(TimeSpan.FromMilliseconds(100));
        r.Lease.OnControlMessage(r.Msg("lost", state: "remoteLost"));   // 重复的 lost 不改丢失时刻
        Assert.Equal(lostAt, r.Lease.LostAt);
        r.Lease.OnControlMessage(r.Msg("resumed"));
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        Assert.Null(r.Lease.LostAt);
        Assert.Contains("Holding->Lost:lost", r.Reasons());

        await r.Lease.ReleaseAsync(CancellationToken.None);
        Assert.Null(r.Lease.LeaseHardExpiresAt);
        Assert.Null(r.Lease.LostAt);
    }

    [Fact]
    public async Task LostAt_FromControlStateAfterReconnect_BackComputedFromServerHardExpiry()
    {
        await using var r = new LeaseRig();
        r.AutoGrant(hbTimeout: 10, grace: 60);
        r.HeartbeatOk();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.ServerClockOffset = TimeSpan.FromSeconds(5);   // 服务端时钟快 5 s
        var now = r.Clock.UtcNow;
        var hardServer = now + TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(20);   // 服务端说：宽限还剩 20 s
        r.Lease.OnControlState(new ControlStateDto("remote", Me, "optoken", "remoteLost", null, hardServer, null, false, 10, 60, 60, "keep"));
        Assert.Equal(LeaseState.Lost, r.Lease.State);
        Assert.Contains("Holding->Lost:state:remoteLost", r.Reasons());   // 原因字符串不变
        Assert.Equal(hardServer, r.Lease.LeaseHardExpiresAt);                // 原样保存（服务端 UTC）
        Assert.Equal(now - TimeSpan.FromSeconds(40), r.Lease.LostAt);        // 本机时钟：LostAt + 60 s = 现在 + 20 s
        Assert.Equal(now + TimeSpan.FromSeconds(20), r.Lease.LostAt!.Value.AddSeconds(r.Lease.LeaseGraceSec));
    }

    [Fact]
    public async Task HeartbeatFailures_CountsConsecutiveNetworkFailures_ResetsOnSuccess()
    {
        await using var r = new LeaseRig();
        r.AutoGrant(hbTimeout: 10);
        int calls = 0;
        r.Http.On("POST", "/control/heartbeat", call =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new HttpRequestException("网络断了（脚本）");
            return ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, r.Clock.UtcNow.AddSeconds(10), r.Clock.UtcNow.AddSeconds(70), 10));
        });
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(0, r.Lease.HeartbeatFailures);
        await AdvanceUntil(r.Clock, () => r.Lease.HeartbeatFailures == 2, "两次心跳网络失败");
        Assert.True(r.Lease.HeartbeatDegraded);
        Assert.Equal(LeaseState.Holding, r.Lease.State);   // 网络失败不丢租约（由 HMI 判 lost）
        await AdvanceUntil(r.Clock, () => Volatile.Read(ref calls) >= 3 && r.Lease.HeartbeatFailures == 0, "心跳恢复");
        Assert.False(r.Lease.HeartbeatDegraded);
    }

    [Fact]
    public async Task ControlStateLocal_WithAutoReacquire_SettlesOnNewLease()
    {
        // ControlState_Sync_AfterReconnect 关掉了自动重申请；这里开着它，等状态稳定再断言
        await using var r = new LeaseRig(autoReacquire: true);
        r.AutoGrant();
        r.HeartbeatOk();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal("L1", r.Lease.LeaseId);
        r.Lease.OnControlState(new ControlStateDto("local", null, null, "local", null, null, null, true, 10, 60, 60, "keep"));
        await FakeClock.WaitUntil(() => r.Lease.State == LeaseState.Holding && r.Lease.LeaseId == "L2", what: "自动重申请稳定在 L2");
        Assert.Contains("Holding->None:state:local", r.Reasons());
        Assert.Contains("None->Requesting:request", r.Reasons());
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
    }

    // ====================================================================== R8：HMI 报告的持有者 / 待处理申请

    [Fact]
    public async Task ServerView_HolderAndPendingByOthers_FromControlStateAndWs()
    {
        await using var r = new LeaseRig();
        Assert.Null(r.Lease.Server);
        Assert.False(r.Lease.HeldByOther);

        // GET /control：别的客户端持有
        r.Lease.ObserveControlState(new ControlStateDto("remote", "PC-其他", "op2", "remote", null, null, null, false, 10, 60, 45, "keep"));
        Assert.Equal(1, r.ViewChanges);
        Assert.Equal("PC-其他", r.Lease.Holder);
        Assert.True(r.Lease.HeldByOther);
        Assert.False(r.Lease.HolderIsMe);
        Assert.Null(r.Lease.PendingRequest);
        Assert.Equal(LeaseState.None, r.Lease.State);   // 只记录，不动租约状态
        r.Lease.ObserveControlState(new ControlStateDto("remote", "PC-其他", "op2", "remote", null, null, null, false, 10, 60, 45, "keep"));
        Assert.Equal(1, r.ViewChanges);                  // 没变化不发事件

        // WS：交还 → 本地；别人的申请 → 待处理（申请人从 detail 解析，到期 = 消息时刻 + HMI 的申请超时 45 s）
        r.Lease.OnControlMessage(r.Msg("released", mode: "local", holder: null, state: "local"));
        Assert.False(r.Lease.HeldByOther);
        Assert.Null(r.Lease.Holder);
        r.Lease.OnControlMessage(r.Msg("requested", requestId: "rqO", mode: "local", holder: null, state: "requested", detail: "PC-B（令牌 op2）申请远程控制 · 等待触屏确认（45 s）"));
        var p = r.Lease.PendingRequest!;
        Assert.Equal(new ControlPendingInfo("rqO", "PC-B", "op2", r.Clock.UtcNow.AddSeconds(45)), p);
        Assert.True(r.Lease.PendingByOther);
        r.Lease.OnControlMessage(r.Msg("denied", requestId: "rqO", mode: "local", holder: null, state: "local"));
        Assert.Null(r.Lease.PendingRequest);
        Assert.False(r.Lease.PendingByOther);

        // GET /control 给出的待处理申请是准确的；之后同一条的 WS 消息不覆盖它
        var exact = new ControlPendingDto("rqP", "PC-C", "op3", r.Clock.UtcNow.AddSeconds(30));
        r.Lease.OnControlState(new ControlStateDto("local", null, null, "requested", null, null, exact, false, 10, 60, 45, "keep"));
        Assert.Equal(new ControlPendingInfo("rqP", "PC-C", "op3", exact.ExpiresAt), r.Lease.PendingRequest);
        r.Lease.OnControlMessage(r.Msg("requested", requestId: "rqP", mode: "local", holder: null, state: "requested", detail: "格式不认识"));
        Assert.Equal("PC-C", r.Lease.PendingRequest!.ClientName);
        // 解析不了申请人的新申请：名字为 null，仍算别人的
        r.Lease.OnControlMessage(r.Msg("requested", requestId: "rqQ", mode: "local", holder: null, state: "requested", detail: "格式不认识"));
        Assert.Equal(new ControlPendingInfo("rqQ", null, null, r.Clock.UtcNow.AddSeconds(45)), r.Lease.PendingRequest);
        Assert.True(r.Lease.PendingByOther);

        // 持有者就是本机名
        r.Lease.OnControlMessage(r.Msg("granted", requestId: "rqQ", mode: "remote", holder: Me, state: "remote"));
        Assert.True(r.Lease.HolderIsMe);
        Assert.False(r.Lease.HeldByOther);
        Assert.Null(r.Lease.PendingRequest);
    }

    [Fact]
    public async Task ServerView_OwnPendingRequest_IsNotByOther_EvenAfterCancel()
    {
        await using var r = new LeaseRig();
        r.RequestPending("rqM", "KM", r.Clock.UtcNow.AddSeconds(60));
        var expired = new Flag();
        r.Http.On("GET", "/control/requests/rqM", _ => ScriptedHttpHandler.Ok(r.Status("rqM", expired.On ? "expired" : "pending")));
        var acquire = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqM") == 1);
        r.Lease.OnControlMessage(r.Msg("requested", requestId: "rqM", mode: "local", holder: null, state: "requested", detail: Me + "（令牌 optoken）申请远程控制 · 等待触屏确认（60 s）"));
        Assert.Equal(Me, r.Lease.PendingRequest!.ClientName);
        Assert.False(r.Lease.PendingByOther);
        Assert.True(await r.Lease.CancelRequestAsync());
        Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(r.Lease.PendingByOther);              // 已取消、还在触屏上等的仍是本机的
        expired.On = true;
        await AdvanceUntil(r.Clock, () => r.Outcome != null, "守卫结束");
        Assert.Equal(CancelledRequestOutcome.Expired, r.Outcome!.Result);
    }

    // ====================================================================== R9 / R19：403 / 503 的类型

    [Fact]
    public async Task Rest_403ForbiddenRole_IsTypedAndReported_OtherErrorsAreNot()
    {
        var h = new ScriptedHttpHandler();
        var clock = new FakeClock();
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "viewtoken", Me) { MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
        using var c = new ReactorApiClient(opt, h, clock);
        var seen = new List<ForbiddenNotice>();
        c.Forbidden += n => { lock (seen) seen.Add(n); };
        const string viewerMsg = "viewer 令牌只能读取 · 写命令需要 operator 及以上角色";
        h.On("POST", "/channels/1/stop", call => ScriptedHttpHandler.CommandError(call, OpCodes.ForbiddenRole, viewerMsg));
        h.On("GET", "/settings", _ => ScriptedHttpHandler.Error(OpCodes.ForbiddenRole, "该操作需要 admin 角色"));
        h.On("GET", "/device", _ => ScriptedHttpHandler.Error(OpCodes.IpNotAllowed, "10.0.0.9 不在允许的网段内"));
        h.On("GET", "/snapshot", _ => ScriptedHttpHandler.Error(OpCodes.Unauthorized, "缺少或无效的 Bearer 令牌"));
        h.On("GET", "/channels", _ => ScriptedHttpHandler.Error(OpCodes.TooManyClients, "WebSocket 连接数已达上限 4"));

        Assert.Null(c.LastForbidden);
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.StopChannelAsync(1, CancellationToken.None));
        Assert.True(ex.IsForbiddenRole);
        Assert.True(ex.IsForbidden);
        Assert.Equal(403, ex.Status);
        Assert.Equal(viewerMsg, ex.Message);
        Assert.Equal(new ForbiddenNotice("POST", "channels/1/stop", OpCodes.ForbiddenRole, viewerMsg, clock.UtcNow), c.LastForbidden);
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => c.SettingsAsync(CancellationToken.None));
        Assert.True(ex2.IsForbiddenRole);
        Assert.Equal("settings", c.LastForbidden!.Path);
        Assert.Equal("GET", c.LastForbidden.Method);

        var ip = await Assert.ThrowsAsync<ApiException>(() => c.DeviceAsync(CancellationToken.None));
        Assert.True(ip.IsForbidden);
        Assert.False(ip.IsForbiddenRole);
        var unauth = await Assert.ThrowsAsync<ApiException>(() => c.SnapshotAsync(CancellationToken.None));
        Assert.True(unauth.IsUnauthorized);
        Assert.False(unauth.IsForbidden);
        var busy = await Assert.ThrowsAsync<ApiException>(() => c.ChannelsAsync(CancellationToken.None));
        Assert.True(busy.IsTooManyClients);
        Assert.Equal(503, busy.Status);
        lock (seen) Assert.Equal(new[] { "POST channels/1/stop", "GET settings" }, seen.Select(n => n.Method + " " + n.Path));
    }

    [Fact]
    public async Task Lease_Request403_ThrowsTyped_ReasonUnchanged_AndConnectionForwardsNotice()
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "viewtoken", Me) { AutoReacquireLease = false, MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
        http.On("POST", "/control/request", call => ScriptedHttpHandler.CommandError(call, OpCodes.ForbiddenRole, "viewer 令牌只能读取 · 写命令需要 operator 及以上角色"));
        await using var conn = new ReactorConnection(opt, http, clock, () => new FakeWsTransport());
        var notices = new List<ForbiddenNotice>();
        conn.ForbiddenRole += n => { lock (notices) notices.Add(n); };
        var reasons = new List<string>();
        conn.LeaseTransitioned += t => { lock (reasons) reasons.Add($"{t.From}->{t.To}:{t.Reason}"); };
        var ex = await Assert.ThrowsAsync<ApiException>(() => conn.Lease.AcquireAsync(CancellationToken.None));
        Assert.True(ex.IsForbiddenRole);
        lock (reasons) Assert.Equal(new[] { "None->Requesting:request", "Requesting->None:request:FORBIDDEN_ROLE" }, reasons);
        lock (notices) Assert.Equal("control/request", Assert.Single(notices).Path);
        Assert.Null(conn.Lease.DenyUntil);   // 403 不是冷却
    }

    [Fact]
    public void WsConnectException_From_MapsHandshakeStatusAndNetworkFailures()
    {
        var inner = new WebSocketException("The server returned status code '503' when status code '101' was expected.");
        var busy = WsConnectException.From(inner, 503);
        Assert.Equal(503, busy.HttpStatus);
        Assert.Equal(OpCodes.TooManyClients, busy.Code);
        Assert.True(busy.IsTooManyClients);
        Assert.False(busy.IsNetwork);
        Assert.Same(inner, busy.InnerException);
        Assert.Contains("503", busy.Message);
        Assert.Contains(OpCodes.TooManyClients, busy.Message);

        Assert.Equal(OpCodes.Unauthorized, WsConnectException.From(inner, 401).Code);
        Assert.True(WsConnectException.From(inner, 401).IsUnauthorized);
        Assert.Equal(OpCodes.IpNotAllowed, WsConnectException.From(inner, 403).Code);
        Assert.True(WsConnectException.From(inner, 403).IsForbidden);
        Assert.Equal(OpCodes.RateLimited, WsConnectException.From(inner, 429).Code);
        Assert.Equal(OpCodes.NotFound, WsConnectException.From(inner, 404).Code);
        Assert.Equal(OpCodes.ClientTooOld, WsConnectException.From(inner, 426).Code);
        Assert.Equal(OpCodes.BadRequest, WsConnectException.From(inner, 400).Code);
        Assert.Equal("HTTP_418", WsConnectException.From(inner, 418).Code);

        var refused = new WebSocketException("Unable to connect to the remote server", new HttpRequestException("Connection refused (127.0.0.1:1)", new SocketException((int)SocketError.ConnectionRefused)));
        foreach (int? s in new int?[] { null, 0, 101 })
        {
            var n = WsConnectException.From(refused, s);
            Assert.True(n.IsNetwork);
            Assert.Null(n.HttpStatus);
            Assert.Equal(WsConnectException.NetworkCode, n.Code);
            Assert.Contains("Connection refused (127.0.0.1:1)", n.Message);   // 带地址的那一层
        }
    }

    // ====================================================================== WS：EverConnected / NextRetryAt / LastError / ReconnectNow

    private sealed class WsRig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly ScriptedHttpHandler Http = new();
        public readonly FakeWsServer Server = new();
        public readonly ClientOptions Opt;
        public readonly ReactorApiClient Rest;
        public readonly TelemetryWsClient Ws;
        public readonly List<Exception> Errors = new();
        public readonly List<DateTime?> Retries = new();
        public readonly CancellationTokenSource Cts = new();
        public Task? Run;

        public WsRig()
        {
            Opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", Me);
            Rest = new ReactorApiClient(Opt, Http, Clock);
            Ws = new TelemetryWsClient(Opt, Rest, Clock, Server.Next);
            Ws.Error += e => { lock (Errors) Errors.Add(e); };
            Ws.NextRetryChanged += at => { lock (Retries) Retries.Add(at); };
        }

        public static void AutoSubscribe(FakeWsTransport t) => t.OnSent = (tr, m) =>
        {
            if (m.GetProperty("type").GetString() == "subscribe") tr.Push(new SubscribedMsg("subscribed", new[] { "telemetry", "events", "alarms" }, 0, 10));
        };

        public static string Hello(DateTime serverTime) => ApiJson.Serialize(new HelloMsg("hello", "1.0", serverTime, null, "boot1", true, 1, 10, 50, new ModeSnap("local", null), 5000));

        public void Start() => Run = Task.Run(() => Ws.RunAsync(Cts.Token));

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            foreach (var t in Server.Transports.ToArray()) t.ServerClose();
            if (Run != null) { try { await Run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
            Rest.Dispose();
        }
    }

    [Fact]
    public async Task Ws_503TooManyClients_LastErrorTyped_NextRetryAt_FailedAttempts_ClearedOnHello()
    {
        await using var r = new WsRig();
        r.Server.OnConnect = t => { if (r.Server.Count <= 2) { t.FailConnect = true; t.FailStatus = 503; } else WsRig.AutoSubscribe(t); };
        var t0 = r.Clock.UtcNow;
        Assert.False(r.Ws.EverConnected);
        Assert.Null(r.Ws.NextRetryAt);
        Assert.Null(r.Ws.LastError);
        r.Start();
        await FakeClock.WaitUntil(() => r.Ws.NextRetryAt != null && r.Clock.Pending == 1, what: "第一次失败后退避");

        var le = Assert.IsType<WsConnectException>(r.Ws.LastError);
        Assert.True(le.IsTooManyClients);
        Assert.Equal(503, le.HttpStatus);
        Assert.IsType<WebSocketException>(le.InnerException);
        Assert.Equal(t0, r.Ws.LastErrorAt);
        lock (r.Errors) Assert.IsType<WebSocketException>(Assert.Single(r.Errors));   // Error 事件仍是原始异常（行为不变）
        Assert.Equal(1, r.Ws.FailedAttempts);
        Assert.InRange((r.Ws.NextRetryAt!.Value - t0).TotalSeconds, 0.4, 0.6);   // 0.5 s ± 20%
        lock (r.Retries) Assert.Equal(r.Ws.NextRetryAt, r.Retries.Last());
        Assert.False(r.Ws.EverConnected);

        r.Clock.Advance(TimeSpan.FromSeconds(1));
        await FakeClock.WaitUntil(() => r.Server.Count == 2 && r.Ws.FailedAttempts == 2 && r.Ws.NextRetryAt != null, what: "第二次失败");
        var t1 = r.Clock.UtcNow;
        Assert.InRange((r.Ws.NextRetryAt!.Value - t1).TotalSeconds, 0.8, 1.2);    // 退避翻倍
        lock (r.Retries) Assert.Contains(null, r.Retries);                          // 等待结束时发过 null

        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await FakeClock.WaitUntil(() => r.Server.Count == 3 && r.Server[2].Connected, what: "第三次连上");
        Assert.Null(r.Ws.NextRetryAt);
        var serverTime = r.Clock.UtcNow.AddSeconds(-3);   // 服务端时钟慢 3 s
        r.Server[2].Push(WsRig.Hello(serverTime));
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        Assert.True(r.Ws.EverConnected);
        Assert.Null(r.Ws.LastError);
        Assert.Null(r.Ws.LastErrorAt);
        Assert.Equal(0, r.Ws.FailedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(-3), r.Ws.ServerClockOffset);
        Assert.False(r.Ws.ReconnectNow());   // 已连接：没有等待可截断

        // 断线：仍是「连上过」
        r.Server[2].ServerClose();
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Reconnecting && r.Ws.NextRetryAt != null, what: "断线后退避");
        Assert.True(r.Ws.EverConnected);
    }

    [Fact]
    public async Task Ws_ReconnectNow_CutsTheBackoff_WithoutAddingAClockDelay()
    {
        await using var r = new WsRig();
        Assert.False(r.Ws.ReconnectNow());   // 未启动
        r.Server.OnConnect = t => t.FailConnect = true;
        r.Start();
        await FakeClock.WaitUntil(() => r.Ws.NextRetryAt != null && r.Clock.Pending == 1, what: "退避中");
        var le = Assert.IsType<WsConnectException>(r.Ws.LastError);
        Assert.True(le.IsNetwork);   // 没有 HTTP 响应
        var firstWait = r.Ws.NextRetryAt!.Value - r.Clock.UtcNow;

        int before = r.Server.Count;
        Assert.True(r.Ws.ReconnectNow());
        // 时钟不动：立刻发起下一次连接；被截断的那个 Delay 撤掉了 —— 再次退避时假时钟上仍只有一个等待
        await FakeClock.WaitUntil(() => r.Server.Count == before + 1 && r.Ws.NextRetryAt != null && r.Ws.FailedAttempts == 2, what: "立即重连");
        await FakeClock.WaitUntil(() => r.Clock.Pending == 1, what: "只剩新的退避等待");
        Assert.Equal(1, r.Clock.Pending);
        var secondWait = r.Ws.NextRetryAt!.Value - r.Clock.UtcNow;
        Assert.InRange(secondWait.TotalSeconds, 0.8, 1.2);   // 退避照常翻倍
        Assert.True(firstWait < secondWait);

        // 正常推进也照旧
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await FakeClock.WaitUntil(() => r.Server.Count == before + 2, what: "按时重试");
    }

    /// <summary>进程内的假 HMI WS 端点：对任何请求回 503 TOO_MANY_CLIENTS（HMI Api/WsHub.cs 的写法），然后关闭。真 ClientWebSocket 连它。</summary>
    private sealed class Http503Server : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private int _hits;

        public Http503Server()
        {
            _l.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public int Hits => Volatile.Read(ref _hits);

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            var head = new StringBuilder();
                            while (!head.ToString().Contains("\r\n\r\n"))
                            {
                                int n = await s.ReadAsync(buf, _cts.Token);
                                if (n == 0) return;
                                head.Append(Encoding.ASCII.GetString(buf, 0, n));
                            }
                            Interlocked.Increment(ref _hits);
                            var body = Encoding.UTF8.GetBytes(ApiJson.Serialize(new ErrorDto(false, new ErrorBody(OpCodes.TooManyClients, "WebSocket 连接数已达上限 4", null))));
                            var resp = $"HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                            await s.WriteAsync(Encoding.ASCII.GetBytes(resp), _cts.Token);
                            await s.WriteAsync(body, _cts.Token);
                            await s.FlushAsync(_cts.Token);
                        }
                        catch { }
                    }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _l.Stop();
        }
    }

    [Fact]
    public async Task RealClientWebSocket_503Handshake_IsReportedAsTooManyClients()
    {
        using var hmi = new Http503Server();
        var uri = new Uri($"http://127.0.0.1:{hmi.Port}");
        // 传输本身：握手被拒的状态码保留下来（CollectHttpResponseDetails）
        await using (var raw = new ClientWebSocketTransport())
        {
            var opt0 = new ClientOptions(uri, "optoken", Me);
            await Assert.ThrowsAsync<WebSocketException>(() => raw.ConnectAsync(opt0.WsUri, new Dictionary<string, string> { ["Authorization"] = "Bearer optoken" }, CancellationToken.None));
            Assert.Equal(503, raw.HandshakeStatusCode);
            Assert.Equal(503, ((IWsTransport)raw).HandshakeStatusCode);
        }

        // 经 TelemetryWsClient（默认传输工厂 = 真 ClientWebSocket）：LastError 归类为 TOO_MANY_CLIENTS；退避挂在假时钟上，不会忙等重试
        var clock = new FakeClock();
        var opt = new ClientOptions(uri, "optoken", Me);
        using var rest = new ReactorApiClient(opt, new ScriptedHttpHandler(), clock);
        var ws = new TelemetryWsClient(opt, rest, clock);
        ws.Start();
        try
        {
            await FakeClock.WaitUntil(() => ws.LastError != null && ws.NextRetryAt != null, timeoutMs: 10000, what: "真握手失败");
            var le = Assert.IsType<WsConnectException>(ws.LastError);
            Assert.True(le.IsTooManyClients);
            Assert.Equal(503, le.HttpStatus);
            Assert.Equal(1, ws.FailedAttempts);
            Assert.Equal(2, hmi.Hits);
        }
        finally { await ws.StopAsync(); }
        Assert.Equal(ConnectionState.Stopped, ws.State);
        Assert.Null(ws.NextRetryAt);
        Assert.False(ws.ReconnectNow());
    }

    // ====================================================================== 连接门面：队列容量、新事件、接口默认实现

    private static (ReactorConnection Conn, ScriptedHttpHandler Http, FakeWsServer Server, FakeClock Clock) Facade(ClientOptions? opt = null, ControlStateDto? control = null, SynchronizationContext? ui = null, int? queue = null)
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var server = new FakeWsServer { OnConnect = WsRig.AutoSubscribe };
        var o = opt ?? new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", Me) { AutoReacquireLease = false, MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero, UiContext = ui, MessageQueueCapacity = queue ?? 2000 };
        http.On("GET", "/device", _ => ScriptedHttpHandler.Ok(new DeviceInfoDto("ParallelReactor-8", null, "1", "1.0", 8, Array.Empty<ChannelInstalledDto>(), new[] { 1, 2, 3, 4 },
            new HardwareDto(new(false, null, null, null), new(false, null, null, null), new(false, null, null, null), new(false, null, null, null)), new UnitsDto("C", "psi", "rpm"), true, false, DateTime.UtcNow, "b", null, 10, 1)));
        http.On("GET", "/control", _ => ScriptedHttpHandler.Ok(control ?? new ControlStateDto("local", null, null, "local", null, null, null, false, 10, 60, 60, "keep")));
        http.On("GET", "/snapshot", _ => ScriptedHttpHandler.Ok(ClientRestTests.Frame(1)));
        return (new ReactorConnection(o, http, clock, server.Next), http, server, clock);
    }

    [Fact]
    public void MessageQueueCapacity_DefaultsTo2000_RejectsNegative()
    {
        var o = new ClientOptions(new Uri("http://127.0.0.1:1"), "t", "pc");
        Assert.Equal(2000, o.MessageQueueCapacity);
        Assert.Equal(0, new ClientOptions(new Uri("http://127.0.0.1:1"), "t", "pc") { MessageQueueCapacity = 0 }.MessageQueueCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientOptions(new Uri("http://127.0.0.1:1"), "t", "pc") { MessageQueueCapacity = -1 });
    }

    [Fact]
    public async Task MessageQueueCapacity_Zero_QueuesNothing_ButEventsAndStoreStillWork()
    {
        var (conn, _, server, _) = Facade(queue: 0);
        await using (conn)
        {
            int frames = 0;
            conn.TelemetryReceived += _ => Interlocked.Increment(ref frames);
            await conn.StartAsync(CancellationToken.None);
            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            server[0].Push(WsRig.Hello(DateTime.UtcNow));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            for (int s = 2; s <= 6; s++) server[0].Push(ApiJson.Serialize(ClientRestTests.Frame(s)));
            server[0].Push(ApiJson.Serialize(new ControlMsg("control", "requested", "local", null, "requested", "rqZ", "pending", null, null, 10, "x", DateTime.UtcNow)));
            await FakeClock.WaitUntil(() => conn.Telemetry.Latest?.Seq == 6 && conn.Lease.PendingRequest != null, what: "帧与 control 都到了");
            Assert.Equal(6, Volatile.Read(ref frames));        // 1（StartAsync 的快照）+ 5
            Assert.Equal(6, conn.Telemetry.Count);
            Assert.False(conn.Messages.TryRead(out _));         // 不入队
            await conn.StopAsync();
            Assert.True(conn.Messages.Completion.IsCompleted);
        }
    }

    [Fact]
    public async Task MessageQueueCapacity_Small_KeepsNewest()
    {
        var (conn, _, server, _) = Facade(queue: 3);
        await using (conn)
        {
            long last = 0;
            conn.TelemetryReceived += f => Interlocked.Exchange(ref last, f.Seq);   // 在入队之后触发（UiContext 为 null：同一线程）
            await conn.StartAsync(CancellationToken.None);
            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            server[0].Push(WsRig.Hello(DateTime.UtcNow));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            for (int s = 2; s <= 6; s++) server[0].Push(ApiJson.Serialize(ClientRestTests.Frame(s)));
            await FakeClock.WaitUntil(() => Interlocked.Read(ref last) == 6, what: "第 6 帧入队");
            var queued = new List<long>();
            while (conn.Messages.TryRead(out var m)) queued.Add(((TelemetrySnapshot)m).Seq);
            Assert.Equal(new long[] { 4, 5, 6 }, queued);       // 满了丢最旧
        }
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref _posts); d(state); }
    }

    [Fact]
    public async Task Connection_ObservesControlStateAtStart_SetsClockOffsetOnHello_ForwardsNewEventsViaUiContext()
    {
        var ui = new CountingContext();
        var (conn, http, server, clock) = Facade(ui: ui, control: new ControlStateDto("remote", "PC-其他", "op2", "remote", null, null, null, false, 10, 60, 60, "keep"));
        await using (conn)
        {
            int views = 0, settledViaConn = 0;
            var scheduled = new List<DateTime?>();
            conn.LeaseServerViewChanged += () => Interlocked.Increment(ref views);
            conn.LeaseCancelledRequestSettled += _ => Interlocked.Increment(ref settledViaConn);
            conn.ReconnectScheduled += at => { lock (scheduled) scheduled.Add(at); };
            int ScheduledCount() { lock (scheduled) return scheduled.Count; }

            await conn.StartAsync(CancellationToken.None);
            Assert.True(conn.Lease.HeldByOther);                 // 首次 GET /control 已记录（R8）
            Assert.Equal("PC-其他", conn.Lease.Holder);
            Assert.Equal(LeaseState.None, conn.Lease.State);
            Assert.Equal(1, Volatile.Read(ref views));
            Assert.True(ui.Posts >= 2);                          // 快照 + 现状变化都经 UiContext

            await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
            var serverTime = clock.UtcNow.AddSeconds(7);
            server[0].Push(WsRig.Hello(serverTime));
            await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
            await FakeClock.WaitUntil(() => conn.Lease.ServerClockOffset != null, what: "hello → 租约的时钟偏差");
            Assert.Equal(TimeSpan.FromSeconds(7), conn.Lease.ServerClockOffset);
            Assert.Equal(conn.Ws.ServerClockOffset, conn.Lease.ServerClockOffset);

            // 断线 → 退避：ReconnectScheduled 带下一次时刻；ReconnectNow（具体类与接口）截断等待
            Assert.False(conn.ReconnectNow());
            server[0].ServerClose();
            await FakeClock.WaitUntil(() => conn.Ws.NextRetryAt != null && ScheduledCount() > 0, what: "退避");
            lock (scheduled) Assert.Equal(conn.Ws.NextRetryAt, scheduled.Last());
            Assert.True(((IReactorConnection)conn).ReconnectNow());
            await FakeClock.WaitUntil(() => server.Count == 2 && server[1].Connected, what: "立即重连");
            lock (scheduled) Assert.Null(scheduled.Last());

            // 取消申请的结局经连接转出
            http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqC", "pending", clock.UtcNow.AddSeconds(60), null, null, 10, 60, "KC")));
            var expired = new Flag();
            http.On("GET", "/control/requests/rqC", _ => ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqC", expired.On ? "expired" : "pending", Me, null, null, null, 10, 60)));
            var acquire = conn.Lease.AcquireAsync(CancellationToken.None);
            await FakeClock.WaitUntil(() => http.Count("GET", "/control/requests/rqC") == 1);
            Assert.True(await conn.Lease.CancelRequestAsync());
            Assert.False(await acquire.WaitAsync(TimeSpan.FromSeconds(3)));
            expired.On = true;
            await AdvanceUntil(clock, () => Volatile.Read(ref settledViaConn) == 1, "LeaseCancelledRequestSettled");
        }
    }

    /// <summary>只实现原有成员的 IReactorConnection（像测试里的假连接）：M3 新成员走接口默认实现，不用改。</summary>
    private sealed class MinimalConn : IReactorConnection
    {
        public ClientOptions Options => null!; public ReactorApiClient Rest => null!; public ControlLeaseClient Lease => null!; public TelemetryWsClient Ws => null!;
        public TelemetryStore Telemetry { get; } = new(); public ControlledWrites Control => null!; public ChannelReader<object> Messages => null!;
        public DeviceInfoDto? Device => null; public ControlStateDto? ControlState => null;
        public event Action<TelemetrySnapshot>? TelemetryReceived { add { } remove { } }
        public event Action<EventMsg>? EventReceived { add { } remove { } }
        public event Action<AlarmMsg>? AlarmChanged { add { } remove { } }
        public event Action<ControlMsg>? ControlChanged { add { } remove { } }
        public event Action<OpEventMsg>? OpEventReceived { add { } remove { } }
        public event Action<LeaseTransition>? LeaseTransitioned { add { } remove { } }
        public event Action<ConnectionState>? ConnectionStateChanged { add { } remove { } }
        public event Action<Exception>? Error { add { } remove { } }
        public event Action? ServerRestarted { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Interface_NewMembers_HaveSafeDefaults_ForOldImplementations()
    {
        IReactorConnection c = new MinimalConn();
        Assert.False(c.ReconnectNow());
        c.LeaseCancelledRequestSettled += _ => { };
        c.LeaseServerViewChanged += () => { };
        c.ForbiddenRole += _ => { };
        c.ReconnectScheduled += _ => { };
        c.LeaseCancelledRequestSettled -= _ => { };
    }
}
