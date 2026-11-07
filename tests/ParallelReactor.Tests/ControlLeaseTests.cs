using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Commands;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>§5.3 租约状态机（纯逻辑，时间注入）：申请 / 允许 / 拒绝 / 超时、心跳 / 丢失 / 宽限恢复 / 期满、释放 / 夺回、急停作废、校验码。</summary>
public class ControlLeaseTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static (ControlLease Lease, ControlArbiter Arb, List<LeaseEvent> Ev) New(double hb = 10, double grace = 60, double req = 60, double cooldown = 30)
    {
        var arb = new ControlArbiter();
        var ev = new List<LeaseEvent>();
        var l = new ControlLease(arb, () => S(0));
        l.Configure(S(hb), S(grace), S(req), S(cooldown));
        l.Transition += ev.Add;
        return (l, arb, ev);
    }

    [Fact]
    public void Request_Grant_Validate_Release()
    {
        var (l, arb, ev) = New();
        var r = l.Request("tokA", "PC-1", S(0), autoGrant: false);
        Assert.True(r.Ok);
        Assert.Equal(LeaseState.Requested, l.State);
        Assert.Equal(ControlMode.Local, arb.Mode);
        Assert.Equal(S(60), r.Deadline);
        Assert.Equal(LeaseEventKind.Requested, ev.Single().Kind);

        // 等待期间：远程写被拒 MODE_LOCAL；另一令牌申请 → CONTROL_REQUEST_PENDING；同令牌同客户端重复申请返回原申请
        Assert.Equal(OpCodes.ModeLocal, l.Validate(null, "tokA", S(1)).Code);
        Assert.Equal(OpCodes.ControlRequestPending, l.Request("tokB", "PC-2", S(1), false).Code);
        Assert.Equal(r.RequestId, l.Request("tokA", "PC-1", S(1), false).RequestId);
        Assert.Equal(RequestResult.Pending, l.QueryRequest(r.RequestId, "tokA", r.Secret, S(1))!.Result);

        var lease = l.Grant(r.RequestId, S(2));
        Assert.NotNull(lease);
        Assert.Equal(32, lease!.LeaseId.Length);
        Assert.Equal(LeaseState.Remote, l.State);
        Assert.Equal(ControlMode.Remote, arb.Mode);
        Assert.Equal("tokA", arb.HolderClientId);
        Assert.Equal("PC-1", arb.HolderName);
        Assert.Equal(S(12), lease.ExpiresAt);
        Assert.Equal(S(72), lease.HardExpiresAt);

        // leaseId 只给申请令牌
        Assert.Equal(lease.LeaseId, l.QueryRequest(r.RequestId, "tokA", r.Secret, S(3))!.Lease!.LeaseId);
        Assert.Null(l.QueryRequest(r.RequestId, "tokB", r.Secret, S(3))!.Lease);
        Assert.Equal(RequestResult.Granted, l.QueryRequest(r.RequestId, "tokB", r.Secret, S(3))!.Result);

        // 校验：无头 / 错头 / 他人令牌 / 正确
        Assert.Equal(OpCodes.NotControlHolder, l.Validate(null, "tokA", S(3)).Code);
        Assert.Equal(OpCodes.NotControlHolder, l.Validate("deadbeef", "tokA", S(3)).Code);
        Assert.Equal(OpCodes.NotControlHolder, l.Validate(lease.LeaseId, "tokB", S(3)).Code);
        var ok = l.Validate(lease.LeaseId, "tokA", S(3));
        Assert.True(ok.Ok);
        Assert.Equal("PC-1", ok.ClientName);
        // 已是远程时再申请 → NOT_CONTROL_HOLDER
        Assert.Equal(OpCodes.NotControlHolder, l.Request("tokB", "PC-2", S(3), false).Code);

        Assert.True(l.Release(lease.LeaseId, "tokA", S(4)).Ok);
        Assert.Equal(LeaseState.Local, l.State);
        Assert.Equal(ControlMode.Local, arb.Mode);
        Assert.Equal("远程释放", arb.LastReturnReason);
        // 旧租约 → CONTROL_LEASE_EXPIRED（本地模式下先报 MODE_LOCAL；再授权后旧 id 才区分得出）
        Assert.Equal(OpCodes.ModeLocal, l.Validate(lease.LeaseId, "tokA", S(5)).Code);
        var r2 = l.Request("tokA", "PC-1", S(6), autoGrant: true);
        Assert.NotNull(r2.Lease);
        Assert.Equal(OpCodes.ControlLeaseExpired, l.Validate(lease.LeaseId, "tokA", S(7)).Code);
        Assert.Equal(new[] { LeaseEventKind.Requested, LeaseEventKind.Granted, LeaseEventKind.Released, LeaseEventKind.Requested, LeaseEventKind.Granted },
            ev.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void AutoGrant_GrantsImmediately()
    {
        var (l, arb, ev) = New();
        var r = l.Request("tokA", "PC-1", S(0), autoGrant: true);
        Assert.True(r.Ok);
        Assert.NotNull(r.Lease);
        Assert.Equal(LeaseState.Remote, l.State);
        Assert.Equal(ControlMode.Remote, arb.Mode);
        Assert.Equal(new[] { LeaseEventKind.Requested, LeaseEventKind.Granted }, ev.Select(e => e.Kind).ToArray());
        Assert.Contains("自动授权", ev[0].Detail);
    }

    [Fact]
    public void Deny_And_RequestTimeout()
    {
        var (l, arb, ev) = New(req: 60);
        var r = l.Request("tokA", "PC-1", S(0), false);
        Assert.True(l.Deny(r.RequestId, S(1)));
        Assert.Equal(LeaseState.Local, l.State);
        Assert.Equal(RequestResult.Denied, l.QueryRequest(r.RequestId, "tokA", r.Secret, S(1))!.Result);
        Assert.False(l.Deny(r.RequestId, S(1)));
        Assert.Null(l.Grant(r.RequestId, S(1)));

        // 被拒后 30 s 冷却：同令牌再申请 → RATE_LIMITED（别的令牌不受影响）；冷却过后可再申请
        Assert.Equal(OpCodes.RateLimited, l.Request("tokA", "PC-1", S(10), false).Code);
        Assert.Equal(LeaseState.Local, l.State);
        var r2 = l.Request("tokA", "PC-1", S(31), false);
        Assert.True(r2.Ok);
        Assert.Empty(l.Tick(S(90.9)));
        var evs = l.Tick(S(91));
        Assert.Equal(LeaseEventKind.RequestExpired, evs.Single().Kind);
        Assert.Equal(LeaseState.Local, l.State);
        Assert.Equal(RequestResult.Expired, l.QueryRequest(r2.RequestId, "tokA", r2.Secret, S(92))!.Result);
        Assert.Null(l.Grant(r2.RequestId, S(92)));   // 60 s 后触屏点允许：已超时
        Assert.Equal(ControlMode.Local, arb.Mode);
        // 申请超时同样进入冷却
        Assert.Equal(OpCodes.RateLimited, l.Request("tokA", "PC-1", S(93), false).Code);
        Assert.True(l.Request("tokB", "PC-2", S(93), false).Ok);
    }

    [Fact]
    public void Heartbeat_Lost_Resume_Expire()
    {
        var (l, arb, ev) = New(hb: 10, grace: 60);
        var lease = l.Request("tokA", "PC-1", S(0), true).Lease!;
        var hb = l.Heartbeat(lease.LeaseId, "tokA", S(5));
        Assert.True(hb.Ok);
        Assert.False(hb.Resumed);
        Assert.Equal(S(15), hb.ExpiresAt);

        // 15 s 无心跳 → RemoteLost（仲裁仍为 Remote，设备照常；写命令仍可带租约）
        Assert.Empty(l.Tick(S(14.9)));
        Assert.Equal(LeaseEventKind.Lost, l.Tick(S(15)).Single().Kind);
        Assert.Equal(LeaseState.RemoteLost, l.State);
        Assert.Equal(ControlMode.Remote, arb.Mode);
        // 宽限内心跳 → 恢复
        var back = l.Heartbeat(lease.LeaseId, "tokA", S(30));
        Assert.True(back.Ok && back.Resumed);
        Assert.Equal(LeaseState.Remote, l.State);
        Assert.Contains(ev, e => e.Kind == LeaseEventKind.Resumed);

        // 再丢：40 s 超时 → Lost，宽限 60 s 后（100 s）期满 → 本地
        l.Tick(S(40));
        Assert.Equal(LeaseState.RemoteLost, l.State);
        Assert.Empty(l.Tick(S(99.9)));
        var exp = l.Tick(S(100));
        Assert.Equal(LeaseEventKind.Expired, exp.Single().Kind);
        Assert.Equal(LeaseState.Local, l.State);
        Assert.Equal(ControlMode.Local, arb.Mode);
        Assert.Equal("心跳丢失", arb.LastReturnReason);
        // 过期租约再心跳：本地模式 → MODE_LOCAL
        Assert.Equal(OpCodes.ModeLocal, l.Heartbeat(lease.LeaseId, "tokA", S(101)).Code);
    }

    [Fact]
    public void Validate_CountsAsHeartbeat_AndResumesInGrace()
    {
        var (l, _, ev) = New(hb: 10, grace: 60);
        var lease = l.Request("tokA", "PC-1", S(0), true).Lease!;
        l.Tick(S(20));
        Assert.Equal(LeaseState.RemoteLost, l.State);
        Assert.True(l.Validate(lease.LeaseId, "tokA", S(25)).Ok);   // 宽限内带租约的写命令 = 心跳
        Assert.Equal(LeaseState.Remote, l.State);
        Assert.Contains(ev, e => e.Kind == LeaseEventKind.Resumed);
        Assert.Equal(S(35), l.Snapshot(S(26)).LeaseExpiresAt);
    }

    [Fact]
    public void ZeroGrace_ExpiresAtHeartbeatTimeout()
    {
        var (l, arb, _) = New(hb: 3, grace: 0);
        var lease = l.Request("tokA", "PC-1", S(0), true).Lease!;
        var evs = l.Tick(S(3));
        Assert.Equal(new[] { LeaseEventKind.Lost, LeaseEventKind.Expired }, evs.Select(e => e.Kind).ToArray());
        Assert.Equal(LeaseState.Local, l.State);
        Assert.Equal(ControlMode.Local, arb.Mode);
    }

    [Fact]
    public void TakeBack_FromRequested_AndRemote()
    {
        var (l, arb, ev) = New();
        l.Request("tokA", "PC-1", S(0), false);
        Assert.True(l.TakeBack(S(1)));
        Assert.Equal(LeaseEventKind.Denied, ev.Last().Kind);
        Assert.False(l.TakeBack(S(1)));

        var lease = l.Request("tokA", "PC-1", S(2), true).Lease!;
        Assert.True(l.TakeBack(S(3)));
        Assert.Equal(LeaseEventKind.TakenBack, ev.Last().Kind);
        Assert.Equal(ControlMode.Local, arb.Mode);
        Assert.Equal("本地切回", arb.LastReturnReason);
        // 夺回后再授权，旧 leaseId → CONTROL_LEASE_EXPIRED
        l.Request("tokA", "PC-1", S(4), true);
        Assert.Equal(OpCodes.ControlLeaseExpired, l.Validate(lease.LeaseId, "tokA", S(5)).Code);
    }

    [Fact]
    public void Estop_ReturnToLocal_InvalidatesLease()
    {
        var (l, arb, ev) = New();
        var lease = l.Request("tokA", "PC-1", S(0), true).Lease!;
        bool changed = false;
        l.Changed += () => changed = true;
        // 急停快速路径（任意线程）直接收回仲裁器 → 租约立刻作废并通知
        arb.ReturnToLocal("急停");
        Assert.Equal(LeaseState.Local, l.State);
        Assert.True(changed);
        Assert.Equal(LeaseEventKind.Invalidated, ev.Last().Kind);
        Assert.Contains("急停", ev.Last().Detail);
        Assert.Equal(OpCodes.ModeLocal, l.Validate(lease.LeaseId, "tokA", S(1)).Code);
        Assert.False(l.Snapshot(S(1)).HasLease);
    }

    [Fact]
    public void Snapshot_ReportsStateAndDeadlines()
    {
        var (l, _, _) = New(hb: 10, grace: 60, req: 30);
        var r = l.Request("tokA", "PC-1", S(0), false);
        var s = l.Snapshot(S(1));
        Assert.Equal(LeaseState.Requested, s.State);
        Assert.Equal(r.RequestId, s.RequestId);
        Assert.Equal(S(30), s.RequestDeadline);
        Assert.False(s.HasLease);
        l.Grant(r.RequestId, S(2));
        s = l.Snapshot(S(3));
        Assert.Equal(LeaseState.Remote, s.State);
        Assert.True(s.HasLease);
        Assert.Equal(S(12), s.LeaseExpiresAt);
        Assert.Equal(S(72), s.HardExpiresAt);
        Assert.Equal("PC-1", s.ClientName);
        Assert.Equal("tokA", s.TokenName);
    }

    [Fact]
    public void RequestHistory_IsBounded_AndUnknownIsNull()
    {
        var (l, _, _) = New(cooldown: 0);
        Assert.Null(l.QueryRequest("nope", "tokA", null, S(0)));
        string? first = null;
        for (int i = 0; i < 60; i++)
        {
            var r = l.Request("tokA", "PC-1", S(i), false);
            first ??= r.RequestId;
            l.Deny(r.RequestId, S(i));
        }
        Assert.Null(l.QueryRequest(first!, "tokA", null, S(61)));   // 只保留最近 50 条
    }

    [Fact]
    public void LeaseId_OnlyWithRequestSecret()
    {
        var (l, _, _) = New();
        var r = l.Request("tokA", "PC-1", S(0), true);
        Assert.NotNull(r.Lease);
        Assert.Equal(32, r.Secret!.Length);
        // 同令牌不带 / 带错凭据（同一令牌的另一台 PC）：只看到 granted，没有 leaseId
        Assert.Null(l.QueryRequest(r.RequestId, "tokA", null, S(1))!.Lease);
        Assert.Null(l.QueryRequest(r.RequestId, "tokA", "deadbeef", S(1))!.Lease);
        Assert.Equal(r.Lease!.LeaseId, l.QueryRequest(r.RequestId, "tokA", r.Secret, S(1))!.Lease!.LeaseId);
        Assert.Null(l.QueryRequest(r.RequestId, "tokB", r.Secret, S(1))!.Lease);
        // 同令牌同客户端重复申请（Requested 中）返回同一申请与同一凭据
        var (l2, _, _) = New();
        var p = l2.Request("tokA", "PC-1", S(0), false);
        var again = l2.Request("tokA", "PC-1", S(1), false);
        Assert.Equal(p.RequestId, again.RequestId);
        Assert.Equal(p.Secret, again.Secret);
    }

    [Fact]
    public void RemoteLost_SameClient_CanRerequest_OldLeaseInvalidated()
    {
        var (l, arb, ev) = New(hb: 10, grace: 60);
        var old = l.Request("tokA", "PC-1", S(0), true).Lease!;
        l.Tick(S(11));
        Assert.Equal(LeaseState.RemoteLost, l.State);
        // 另一令牌 / 同令牌不同客户端名：仍是 NOT_CONTROL_HOLDER
        Assert.Equal(OpCodes.NotControlHolder, l.Request("tokB", "PC-2", S(12), true).Code);
        Assert.Equal(OpCodes.NotControlHolder, l.Request("tokA", "PC-9", S(12), true).Code);
        // 同令牌同客户端（丢了 leaseId 的那台）：旧租约作废、按新申请处理
        var r = l.Request("tokA", "PC-1", S(12), true);
        Assert.True(r.Ok);
        Assert.NotEqual(old.LeaseId, r.Lease!.LeaseId);
        Assert.Equal(LeaseState.Remote, l.State);
        Assert.Equal(ControlMode.Remote, arb.Mode);
        var kinds = ev.Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { LeaseEventKind.Requested, LeaseEventKind.Granted, LeaseEventKind.Lost, LeaseEventKind.Invalidated, LeaseEventKind.Requested, LeaseEventKind.Granted }, kinds);
        Assert.Equal(OpCodes.ControlLeaseExpired, l.Validate(old.LeaseId, "tokA", S(13)).Code);
        Assert.True(l.Validate(r.Lease.LeaseId, "tokA", S(13)).Ok);
        // 事件带终态：同一批里 Invalidated / Requested / Granted 都是这次调用结束时的 Remote
        Assert.All(ev.Skip(3), e => Assert.Equal(LeaseState.Remote, e.State));
        Assert.Equal(LeaseState.RemoteLost, ev[2].State);
    }

    [Theory]
    [InlineData("PC-1", "PC-1")]
    [InlineData("  PC  1\t\n2 ", "PC 1 2")]
    [InlineData("维护\u202e工程师\u200b（已批准）\r\n请点允许", "维护工程师（已批准） 请点允许")]
    [InlineData("\u0007\u001b[31m", "[31m")]
    [InlineData("\u200b\u0007", "")]
    public void SanitizeClientName_StripsControlAndFormatChars(string input, string expected)
        => Assert.Equal(expected, ControlLease.SanitizeClientName(input));

    [Fact]
    public void SanitizeClientName_Truncates_AndRequestUsesTokenWhenEmpty()
    {
        Assert.Equal(ControlLease.ClientNameMax, ControlLease.SanitizeClientName(new string('x', 200)).Length);
        var (l, arb, _) = New();
        var r = l.Request("tokA", "\u200b\u0007", S(0), true);
        Assert.Equal("tokA", r.Lease!.ClientName);
        Assert.Equal("tokA", arb.HolderName);
    }

    [Fact]
    public void ConcurrentExpireAndRequest_NeverInvalidatesFreshLease()
    {
        // 过期回本地与新授权并发：仲裁器在锁内同步切换，新租约不会被上一次的 ReturnToLocal 作废；迁移按状态变化顺序送达
        for (int round = 0; round < 50; round++)
        {
            var arb = new ControlArbiter();
            var l = new ControlLease(arb, () => S(0));
            l.Configure(S(1), S(0), S(60), S(0));
            var ev = new System.Collections.Concurrent.ConcurrentQueue<LeaseEvent>();
            l.Transition += ev.Enqueue;
            l.Request("tokA", "PC-1", S(0), true);
            RequestOutcome r2 = default;
            using var gate = new System.Threading.ManualResetEventSlim(false);
            var t1 = System.Threading.Tasks.Task.Run(() => { gate.Wait(); l.Tick(S(5)); });
            var t2 = System.Threading.Tasks.Task.Run(() => { gate.Wait(); r2 = l.Request("tokB", "PC-2", S(5), true); });
            gate.Set();
            System.Threading.Tasks.Task.WaitAll(t1, t2);
            // 两种合法交错：tokB 先到（NOT_CONTROL_HOLDER，之后过期回本地）或过期先到（tokB 授权成功且仍有效）
            if (r2.Ok)
            {
                Assert.Equal(LeaseState.Remote, l.State);
                Assert.Equal(ControlMode.Remote, arb.Mode);
                Assert.Equal("tokB", arb.HolderClientId);
                Assert.True(l.Validate(r2.Lease!.LeaseId, "tokB", S(5)).Ok);
            }
            else
            {
                Assert.Equal(OpCodes.NotControlHolder, r2.Code);
                Assert.Equal(LeaseState.Local, l.State);
                Assert.Equal(ControlMode.Local, arb.Mode);
            }
            var kinds = ev.Select(e => e.Kind).ToList();
            Assert.DoesNotContain(LeaseEventKind.Invalidated, kinds);
            // 终态标记与实际一致
            Assert.Equal(l.State, ev.Last().State);
        }
    }
}
