using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>租约状态机：授权 / 心跳 / 丢失 / 宽限恢复 / 过期重申请 / 拒绝 / 急停作废 / 释放。时间用假时钟推进。</summary>
public class LeaseStateMachineTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly ScriptedHttpHandler Http = new();
        public readonly ClientOptions Opt;
        public readonly ReactorApiClient Rest;
        public readonly ControlLeaseClient Lease;
        public readonly List<LeaseTransition> Transitions = new();
        public readonly List<Exception> Errors = new();
        public int LeaseNo;

        public Rig(bool autoReacquire = true)
        {
            Opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC-测试工位1") { AutoReacquireLease = autoReacquire, MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero };
            Rest = new ReactorApiClient(Opt, Http, Clock);
            Lease = new ControlLeaseClient(Rest, Opt, Clock);
            Lease.Transitioned += t => { lock (Transitions) Transitions.Add(t); };
            Lease.Error += e => { lock (Errors) Errors.Add(e); };
        }

        /// <summary>autoGrant：申请即授权，每次新租约 L1, L2…；心跳 200；释放 200。</summary>
        public Rig AutoGrant(int hbTimeout = 10, int grace = 60)
        {
            Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true,
                new ControlRequestResultDto("rq" + (LeaseNo + 1), "granted", null, "L" + (++LeaseNo), Clock.UtcNow.AddSeconds(hbTimeout), hbTimeout, grace, "key" + LeaseNo)));
            Http.On("POST", "/control/heartbeat", call => ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, Clock.UtcNow.AddSeconds(hbTimeout), Clock.UtcNow.AddSeconds(hbTimeout + grace), hbTimeout)));
            Http.On("POST", "/control/release", call => ScriptedHttpHandler.Command(call, false, new { mode = "local" }));
            return this;
        }

        public string Reasons() { lock (Transitions) return string.Join(" ", Transitions.Select(t => $"{t.From}->{t.To}:{t.Reason}")); }

        public ControlMsg Msg(string ev, string? requestId = null, string mode = "remote", string? holder = "PC-测试工位1", string state = "remote")
            => new("control", ev, mode, holder, state, requestId, null, null, null, 10, "", Clock.UtcNow);

        public async ValueTask DisposeAsync() { await Lease.DisposeAsync(); Rest.Dispose(); }
    }

    [Fact]
    public async Task AutoGrant_Holding_HeartbeatOverRest_WithLeaseHeader()
    {
        await using var r = new Rig().AutoGrant(hbTimeout: 10);
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        Assert.Equal("L1", r.Lease.LeaseId);
        Assert.Equal(TimeSpan.FromSeconds(4), r.Lease.HeartbeatInterval);   // 10/2=5 夹到上限 4
        Assert.Contains("None->Requesting:request", r.Reasons());
        Assert.Contains("Requesting->Holding:granted", r.Reasons());
        // 心跳循环已在等 Delay
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1, what: "心跳定时器");
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/heartbeat") == 1, what: "首次心跳");
        Assert.Equal("L1", r.Http.Of("POST", "/control/heartbeat").Single().Lease);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/heartbeat") == 2, what: "第二次心跳");
        Assert.False(r.Lease.HeartbeatDegraded);
    }

    [Fact]
    public async Task Heartbeat_PrefersWs_WhenAvailable()
    {
        await using var r = new Rig().AutoGrant(hbTimeout: 4);
        var wsHb = new List<string>();
        r.Lease.WsHeartbeat = (lease, _) => { wsHb.Add(lease); return Task.FromResult(true); };
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(2), r.Lease.HeartbeatInterval);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await FakeClock.WaitUntil(() => wsHb.Count == 1, what: "WS 心跳");
        Assert.Equal("L1", wsHb[0]);
        Assert.Equal(0, r.Http.Count("POST", "/control/heartbeat"));
        // WS 掉线（返回 false）→ 退回 REST
        r.Lease.WsHeartbeat = (_, _) => Task.FromResult(false);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await FakeClock.WaitUntil(() => r.Http.Count("POST", "/control/heartbeat") == 1, what: "REST 兜底心跳");
    }

    [Fact]
    public async Task Pending_Poll_GrantedOnSecondPoll_UsesRequestKey()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqA", "pending", r.Clock.UtcNow.AddSeconds(60), null, null, 10, 60, "KEY-A")));
        r.Http.On("GET", "/control/requests/rqA", (call, n) => n == 0
            ? ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqA", "pending", "PC-测试工位1", r.Clock.UtcNow.AddSeconds(59), null, null, 10, 60))
            : ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqA", "granted", "PC-测试工位1", null, call.RequestKey == "KEY-A" ? "LA" : null, r.Clock.UtcNow.AddSeconds(10), 10, 60)));
        var t = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Http.Count("GET", "/control/requests/rqA") == 1, what: "首次轮询");
        Assert.Equal(LeaseState.Requesting, r.Lease.State);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await t);
        Assert.Equal("LA", r.Lease.LeaseId);
        Assert.All(r.Http.Of("GET", "/control/requests/rqA"), c => Assert.Equal("KEY-A", c.RequestKey));
    }

    [Fact]
    public async Task Pending_WsGrantedMessage_PokesPollWithoutWaiting()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqB", "pending", r.Clock.UtcNow.AddSeconds(60), null, null, 10, 60, "KB")));
        int polls = 0;
        r.Http.On("GET", "/control/requests/rqB", (call, n) => { polls++; return n == 0
            ? ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqB", "pending", "PC", null, null, null, 10, 60))
            : ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqB", "granted", "PC", null, "LB", r.Clock.UtcNow.AddSeconds(10), 10, 60)); });
        var t = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => polls == 1);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Lease.OnControlMessage(r.Msg("granted", requestId: "rqB"));   // 不推进时钟
        Assert.True(await t.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("LB", r.Lease.LeaseId);
    }

    [Fact]
    public async Task Denied_ThenCooldown()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqC", "pending", r.Clock.UtcNow.AddSeconds(60), null, null, 10, 60, "KC")));
        r.Http.On("GET", "/control/requests/rqC", call => ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rqC", "denied", "PC", null, null, null, 10, 60)));
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Contains("Requesting->None:denied", r.Reasons());
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));   // 30 s 冷却：不再发请求
        Assert.Equal(1, r.Http.Count("POST", "/control/request"));
        r.Clock.Advance(TimeSpan.FromSeconds(31));
        await r.Lease.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
    }

    [Fact]
    public async Task RequestExpired_404_ReturnsFalse()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rqD", "pending", r.Clock.UtcNow.AddSeconds(60), null, null, 10, 60, "KD")));
        r.Http.On("GET", "/control/requests/rqD", call => ScriptedHttpHandler.Error(OpCodes.NotFound, "没有这个申请"));
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));
        Assert.Contains("Requesting->None:requestExpired", r.Reasons());
    }

    [Fact]
    public async Task Lost_ThenHeartbeatResumed_BackToHolding()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq", "granted", null, "L1", r.Clock.UtcNow.AddSeconds(4), 4, 4, "k")));
        int hb = 0;
        // 第一次 REST 心跳：服务端还在 remoteLost（没恢复）；第二次：resumed。以前第一次就回 "remote" —— 第一次心跳就回到 Holding，
        // 第二次推进时心跳循环已经走 WS 了，wsHb 是不是 0 要看断言和心跳循环谁先跑（偶发失败）
        r.Http.On("POST", "/control/heartbeat", call => { hb++; return ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto(hb == 1 ? "remoteLost" : "remote", hb >= 2, r.Clock.UtcNow.AddSeconds(4), r.Clock.UtcNow.AddSeconds(8), 4)); });
        var wsHb = 0;
        r.Lease.WsHeartbeat = (_, _) => { wsHb++; return Task.FromResult(true); };
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        // 服务端判定心跳超时
        r.Lease.OnControlMessage(r.Msg("lost", state: "remoteLost"));
        Assert.Equal(LeaseState.Lost, r.Lease.State);
        Assert.Equal("L1", r.Lease.LeaseId);   // 宽限期内仍持同一租约
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        // Lost 时不走 WS，走 REST 拿 resumed
        await FakeClock.WaitUntil(() => hb == 1, what: "REST 心跳");
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        Assert.Equal(LeaseState.Lost, r.Lease.State);                 // 服务端还没恢复：仍在宽限中
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await FakeClock.WaitUntil(() => hb == 2, what: "第二次 REST 心跳");
        await FakeClock.WaitUntil(() => r.Lease.State == LeaseState.Holding, what: "resumed → Holding");
        Assert.Equal(0, wsHb);
        Assert.Contains("Holding->Lost:lost", r.Reasons());
        Assert.Contains("Lost->Holding:resumed", r.Reasons());
    }

    [Fact]
    public async Task Lost_ThenWsResumed_Message()
    {
        await using var r = new Rig().AutoGrant();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnControlMessage(r.Msg("lost", state: "remoteLost"));
        r.Lease.OnControlMessage(r.Msg("resumed"));
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        // 别人的 lost 不影响我们
        r.Lease.OnControlMessage(r.Msg("lost", holder: "别的 PC", state: "remoteLost"));
        Assert.Equal(LeaseState.Holding, r.Lease.State);
    }

    [Fact]
    public async Task Heartbeat_Rejected_LeaseExpired_AutoReacquire_NewLease()
    {
        await using var r = new Rig();
        int leases = 0;
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq" + leases, "granted", null, "L" + (++leases), r.Clock.UtcNow.AddSeconds(10), 10, 60, "k")));
        r.Http.On("POST", "/control/heartbeat", (call, n) => n == 0
            ? ScriptedHttpHandler.CommandError(call, OpCodes.ControlLeaseExpired, "租约已过期")
            : ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, r.Clock.UtcNow.AddSeconds(10), r.Clock.UtcNow.AddSeconds(70), 10)));
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        await FakeClock.WaitUntil(() => r.Lease.LeaseId == "L2", what: "重申请拿到新租约");
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        Assert.Contains("Holding->None:heartbeat:CONTROL_LEASE_EXPIRED", r.Reasons());
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
        Assert.All(r.Http.Of("POST", "/control/request"), c => Assert.Equal("PC-测试工位1", c.Json.GetProperty("clientName").GetString()));   // 同令牌 + 同 clientName
    }

    [Fact]
    public async Task Expired_Message_Reacquires_But_Invalidated_DoesNot()
    {
        await using var r = new Rig().AutoGrant();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnControlMessage(r.Msg("expired", mode: "local", holder: null, state: "local"));
        await FakeClock.WaitUntil(() => r.Lease.LeaseId == "L2", what: "expired 后自动重申请");
        Assert.Contains("Holding->None:expired", r.Reasons());

        // 急停：租约作废（invalidated）→ None，不自动重申请
        r.Lease.OnControlMessage(r.Msg("invalidated", mode: "local", holder: null, state: "local"));
        await Task.Delay(50);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Null(r.Lease.LeaseId);
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
        Assert.Contains("Holding->None:invalidated", r.Reasons());

        // takenBack（触屏切回本地）同样不重申请
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnControlMessage(r.Msg("takenBack", mode: "local", holder: null, state: "local"));
        await Task.Delay(50);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Equal(3, r.Http.Count("POST", "/control/request"));
    }

    [Fact]
    public async Task NoAutoReacquire_Option()
    {
        await using var r = new Rig(autoReacquire: false).AutoGrant();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnControlMessage(r.Msg("expired", mode: "local", holder: null, state: "local"));
        await Task.Delay(50);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Equal(1, r.Http.Count("POST", "/control/request"));
    }

    [Fact]
    public async Task WriteFailed_NotControlHolder_DropsLease()
    {
        await using var r = new Rig().AutoGrant();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnWriteFailed(new ApiException(OpCodes.NotControlHolder, 409, "带的不是当前租约"));
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Contains("Holding->None:write:NOT_CONTROL_HOLDER", r.Reasons());
        r.Lease.OnWriteFailed(new ApiException(OpCodes.ChannelBusy, 409, "无关错误"));
        Assert.Equal(LeaseState.None, r.Lease.State);
    }

    [Fact]
    public async Task Release_PostsLease_ThenNone_NoMoreHeartbeats()
    {
        await using var r = new Rig().AutoGrant(hbTimeout: 4);
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        await r.Lease.ReleaseAsync(CancellationToken.None);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Null(r.Lease.LeaseId);
        Assert.Equal("L1", r.Http.Of("POST", "/control/release").Single().Lease);
        r.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.Equal(0, r.Http.Count("POST", "/control/heartbeat"));
        Assert.False(r.Lease.CanControl);
    }

    [Fact]
    public async Task ControlState_Sync_AfterReconnect()
    {
        // 关掉自动重申请（只改测试，PORT-PLAN §5 M3）：这里验的是 GET /control → 本地状态的映射。开着它时，最后一步 local → Revoke 之后
        // ReacquireAsync 经 Task.Yield 在线程池上立刻同步进入 Requesting，与下面「== None」的断言竞争（偶发 Requesting）。
        // 「回到本地后自动重申请」由 FixRegressionTests.F5 覆盖（等到 L2 再断言），ClientAdditionsTests 里还有一条等稳定后的版本。
        await using var r = new Rig(autoReacquire: false).AutoGrant();
        Assert.True(await r.Lease.AcquireAsync(CancellationToken.None));
        r.Lease.OnControlState(new ControlStateDto("remote", "PC-测试工位1", "PC-测试工位1", "remoteLost", null, null, null, true, 10, 60, 60, "keep"));
        Assert.Equal(LeaseState.Lost, r.Lease.State);
        r.Lease.OnControlState(new ControlStateDto("remote", "PC-测试工位1", "PC-测试工位1", "remote", null, null, null, true, 10, 60, 60, "keep"));
        Assert.Equal(LeaseState.Holding, r.Lease.State);
        r.Lease.OnControlState(new ControlStateDto("local", null, null, "local", null, null, null, true, 10, 60, 60, "keep"));
        Assert.Equal(LeaseState.None, r.Lease.State);
    }

    [Fact]
    public async Task Request_Rejected_429_Throws_AndSetsCooldown()
    {
        await using var r = new Rig();
        r.Http.On("POST", "/control/request", call => ScriptedHttpHandler.CommandError(call, OpCodes.RateLimited, "刚被拒绝 · 30 s 内不能再申请"));
        var t = r.Lease.AcquireAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1, what: "Retry-After 等待");
        r.Clock.Advance(TimeSpan.FromSeconds(1));   // 429 自带一次 Retry-After 重试
        var ex = await Assert.ThrowsAsync<ApiException>(() => t);
        Assert.True(ex.IsRateLimited);
        Assert.Equal(LeaseState.None, r.Lease.State);
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
        Assert.False(await r.Lease.AcquireAsync(CancellationToken.None));   // 30 s 冷却：不再发请求
        Assert.Equal(2, r.Http.Count("POST", "/control/request"));
    }

    [Fact]
    public async Task ControlledWrites_FailFast_WithoutLease_AndNotifyOnLeaseError()
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC-测试工位1") { MinWriteGap = TimeSpan.Zero, WriteRetryDelay = TimeSpan.Zero, AutoReacquireLease = false };
        await using var conn = new ReactorConnection(opt, http, clock, () => new FakeWsTransport());
        var ex = await Assert.ThrowsAsync<ApiException>(() => conn.Control.EnableChannelAsync(1, CancellationToken.None));
        Assert.Equal(OpCodes.NotControlHolder, ex.Code);
        Assert.Empty(http.Calls);

        http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq", "granted", null, "LX", clock.UtcNow.AddSeconds(10), 10, 60, "k")));
        http.On("POST", "/channels/1/enable", call => ScriptedHttpHandler.Command(call, false, null));
        http.On("POST", "/channels/2/enable", call => ScriptedHttpHandler.CommandError(call, OpCodes.ControlLeaseExpired, "过期"));
        Assert.True(await conn.Lease.AcquireAsync(CancellationToken.None));
        await conn.Control.EnableChannelAsync(1, CancellationToken.None);
        Assert.Equal("LX", http.Of("POST", "/channels/1/enable").Single().Lease);
        await Assert.ThrowsAsync<ApiException>(() => conn.Control.EnableChannelAsync(2, CancellationToken.None));
        Assert.Equal(LeaseState.None, conn.Lease.State);
    }
}
