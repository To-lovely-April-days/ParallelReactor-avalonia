using System;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M3 的守卫补充（Core.Guard，PORT-PLAN §5 M3「守卫补充」、§3.2 第 9 / 10 条、R8 / R9）：
/// <list type="bullet">
/// <item>只读令牌：第一个 403 FORBIDDEN_ROLE 之后学到（TokenRole.Learn），按 HMI 的规则禁用（控制类 / 申请控制权 / 确认报警 / 会话；只读又没有急停权限位时停止类；没有急停权限位时急停），
///   没学到的一律不禁用；真机经 DeviceActions 学到、缓存在数据源里。</item>
/// <item>控制权被其他客户端持有（第 5 种）、别人的申请待批：「申请控制权」禁用并写明是谁。</item>
/// <item>别名规则：唯一、最长 8 个汉字或 16 个字母，原因文字逐字同样稿；默认别名第一个没用过的「k号机」。</item>
/// </list>
/// 这些是 Core 的规则，放在 App 的测试工程里（Control.Tests 由另一条工作线在改）。
/// </summary>
public class GuardRoleTests
{
    private static GuardFacts F(LeaseStatus lease = LeaseStatus.Remote, TokenRole? role = null, string? holder = null, string? otherRequest = null, bool estop = false) => new()
    {
        Alias = "1号机",
        Conn = ConnStatus.Online,
        Lease = lease,
        LeaseHolder = holder,
        OtherRequest = otherRequest,
        Role = role ?? TokenRole.Unknown,
        EstopActive = estop,
        Model = ModelProfiles.Embedded.Get("PR8"),
        Channels = Enumerable.Range(1, 8).Select(n => new ChannelFacts(n, n <= 4, n == 1 ? StateKey.React : StateKey.Ready, Heating: n == 1, Sp: 100)).ToArray(),
        SessionActive = true,
        SessionId = "PT-1",
        Alarms = new[] { new AlarmFacts("a1", false) },
    };

    private static readonly DeviceAction[] StopActions = { DeviceAction.Disable, DeviceAction.Stop, DeviceAction.HeatStop, DeviceAction.ProgAbort, DeviceAction.StirOff, DeviceAction.StopAll };
    private static readonly DeviceAction[] RecordActions = { DeviceAction.SessionMark, DeviceAction.SessionEnd, DeviceAction.AlarmAck };

    private static ActionPayload P(DeviceAction a) => a == DeviceAction.AlarmAck ? new ActionPayload(Id: "a1") : new ActionPayload(N: 1, Sp: 80, Rpm: 300);

    [Theory]
    [InlineData(DeviceAction.HeatStart, true, false)]     // 控制类被拒 → 只读（急停权限位不知道）
    [InlineData(DeviceAction.LeaseRequest, true, false)]  // 申请控制权要 operator
    [InlineData(DeviceAction.AlarmAck, true, false)]      // 记录类要 operator
    [InlineData(DeviceAction.SessionStart, true, false)]
    [InlineData(DeviceAction.StopAll, true, true)]        // 停止类被拒 → 只读且没有急停权限位
    [InlineData(DeviceAction.HeatStop, true, true)]
    [InlineData(DeviceAction.Estop, false, true)]         // 急停被拒 → 只是没有急停权限位
    [InlineData(DeviceAction.Reconnect, false, false)]    // 本机操作：什么都学不到
    [InlineData(DeviceAction.Rename, false, false)]
    public void Learn_FromA403(DeviceAction a, bool readOnly, bool noEstop)
    {
        var r = TokenRole.Unknown.Learn(a);
        Assert.Equal((readOnly, noEstop), (r.ReadOnly, r.NoEstop));
        Assert.Equal(r, r.Learn(DeviceAction.Reconnect));                     // 只会收紧
        Assert.True(new TokenRole(true, false).Learn(DeviceAction.Estop) is { ReadOnly: true, NoEstop: true });
    }

    [Fact]
    public void ReadOnly_BlocksControlLeaseAndRecord_ButNotStopOrEstop()
    {
        var f = F(role: new TokenRole(ReadOnly: true));
        foreach (var s in DeviceActions.All.Where(s => s.Kind == GuardKind.Control))
            Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", ActionGuard.Why(f, s.Action, P(s.Action)));
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", ActionGuard.GuardText(f, GuardKind.Control));
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", ActionGuard.Why(F(LeaseStatus.Local, new TokenRole(true)), DeviceAction.LeaseRequest));
        foreach (var a in RecordActions) Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", ActionGuard.Why(f, a, P(a)));
        // 急停权限位还不知道：停止类、急停照样可用（宁可发出去被拒，也不能被本机猜错拦下）
        foreach (var a in StopActions) Assert.Equal("", ActionGuard.Why(f, a, P(a)));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.Estop));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.LeaseRelease));       // 释放 / 本机操作不受影响
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.Disconnect));
    }

    [Fact]
    public void ReadOnlyWithoutEstopBit_BlocksStopAndEstop()
    {
        var f = F(role: new TokenRole(true, true));
        foreach (var a in StopActions) Assert.Equal("1号机的令牌只能读取 · 停止需要 operator 令牌或急停权限", ActionGuard.Why(f, a, P(a)));
        Assert.Equal("1号机的令牌没有急停权限", ActionGuard.Why(f, DeviceAction.Estop));
        Assert.Equal("1号机已在急停 · 需在触屏复位", ActionGuard.Why(F(role: new TokenRole(true, true), estop: true), DeviceAction.Estop));   // 已在急停先说
        // 只是没有急停权限位（operator 收回了 estop）：停止类照样可以
        var g = F(role: new TokenRole(false, true));
        foreach (var a in StopActions) Assert.Equal("", ActionGuard.Why(g, a, P(a)));
        Assert.Equal("1号机的令牌没有急停权限", ActionGuard.Why(g, DeviceAction.Estop));
        Assert.DoesNotContain("令牌", ActionGuard.Why(g, DeviceAction.SetSp, P(DeviceAction.SetSp)));   // 角色不知道：控制类不因令牌禁用
    }

    [Fact]
    public void OtherHolder_AndOtherRequest_BlockLeaseRequest()
    {
        Assert.Equal("1号机远程控制中（其他客户端 PC-2）", ActionGuard.Why(F(LeaseStatus.Other, holder: "PC-2"), DeviceAction.LeaseRequest));
        Assert.Equal("1号机已有待处理的远程申请（PC-2）", ActionGuard.Why(F(LeaseStatus.Local, otherRequest: "PC-2"), DeviceAction.LeaseRequest));
        Assert.Equal("1号机为本地只读 · 先申请控制权", ActionGuard.Why(F(LeaseStatus.Local, otherRequest: "PC-2"), DeviceAction.HeatStart, P(DeviceAction.HeatStart)));
        Assert.Equal("", ActionGuard.Why(F(LeaseStatus.Other, holder: "PC-2"), DeviceAction.StopAll));   // 停止类永远不看控制权
        var d = DeviceTexts.Lease(LeaseStatus.Other, 0, 0, "PC-测试", "PC-2");
        Assert.Equal(("远程控制中（其他客户端 PC-2）", "其他客户端 PC-2", "其他客户端 PC-2", "PC-2"), (d.Text, d.Short, d.BadgeShort, d.Holder));
        Assert.Equal("其他客户端", DeviceTexts.OtherShort(null));
    }

    [Theory]
    [InlineData("新名字", "")]
    [InlineData(" 新名字 ", "")]
    [InlineData("", "名称不能为空")]
    [InlineData("　", "名称不能为空")]                                   // 全角空格也算空白（JS trim）
    [InlineData("八个汉字的别名啊", "")]                                   // 8 个汉字 = 16
    [InlineData("九个汉字的设备别名啊", "名称太长 · 最多 8 个汉字或 16 个字母")]
    [InlineData("ReactorLab-PR8-A", "")]                                  // 16 个字母
    [InlineData("ReactorLab-PR8-AB", "名称太长 · 最多 8 个汉字或 16 个字母")]
    [InlineData("反应台ReactorLab", "")]                                  // 汉字算 2：3 × 2 + 10 = 16
    [InlineData("反应台ReactorLab1", "名称太长 · 最多 8 个汉字或 16 个字母")]
    [InlineData("2号机", "名称已被其他设备使用（192.168.1.24）")]
    public void AliasRules_ExactlyTheMock(string alias, string error)
    {
        var peers = new[] { new PeerDevice("2号机", "192.168.1.24") };
        Assert.Equal(error, ActionGuard.AliasError(alias, peers));
        Assert.Equal(error, ActionGuard.Why(F() with { Peers = peers }, DeviceAction.Rename, new ActionPayload(Alias: alias)));
    }

    [Fact]
    public void AliasUnits_AndFreeAlias()
    {
        Assert.Equal(16, ActionGuard.AliasUnits("反应台ReactorLab"));
        Assert.Equal(17, ActionGuard.AliasUnits("反应台ReactorLab1"));
        Assert.Equal(2, ActionGuard.AliasUnits("𠀀"));                         // 辅助平面的汉字也算 2
        Assert.Equal("1号机", ActionGuard.FreeAlias(Array.Empty<string>()));
        Assert.Equal("2号机", ActionGuard.FreeAlias(new[] { "1号机", "3号机", "甲" }));
    }

    // ———————————————————— 真机：第一个 403 之后学到，缓存在这台设备上 ————————————————————

    [Fact]
    public async Task Live_First403_LearnsReadOnly_ThenGuardsWithoutAskingTheDevice()
    {
        var (f, d, fake) = await LiveFleet.OneAsync(h =>
        {
            FakeReactorConnection.ScriptGrantedLease(h);
            h.On("POST", @"/channels/\d+/heat/start", c => ScriptedHttpHandler.CommandError(c, OpCodes.ForbiddenRole, "viewer 令牌只能读取 · 写命令需要 operator 及以上角色"));
            h.On("POST", "/stop-all", c => ScriptedHttpHandler.CommandError(c, OpCodes.ForbiddenRole, "停止类命令需要 operator 角色或 estop 权限位"));
        });
        Assert.True(await fake.Lease.AcquireAsync(System.Threading.CancellationToken.None));
        f.RefreshNow();
        Assert.Equal("", d.Actions.Why(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 60)));
        var o = await d.Actions.RunAsync(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 60));
        Assert.False(o.Ok);
        Assert.Contains("FORBIDDEN_ROLE", o.Message);
        f.RefreshNow();
        Assert.True(d.Source.Role.ReadOnly);
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", d.Actions.Why(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 60)));
        int before = fake.Http.Calls.Count;
        Assert.False((await d.Actions.RunAsync(DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 70))).Ok);
        Assert.Equal(before, fake.Http.Calls.Count);                           // 学到之后不再发给设备
        Assert.Equal("", d.Actions.Why(DeviceAction.StopAll));                 // 急停权限位不知道：停止类照样可以
        // 停止类也被拒：只读且没有急停权限位 → 停止类、急停都禁用
        Assert.False((await d.Actions.RunAsync(DeviceAction.StopAll)).Ok);
        f.RefreshNow();
        Assert.Equal("1号机的令牌只能读取 · 停止需要 operator 令牌或急停权限", d.Actions.Why(DeviceAction.StopAll));
        Assert.Equal("1号机的令牌没有急停权限", d.Actions.Why(DeviceAction.Estop));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_OtherClientsPendingRequest_BlocksOurRequest()
    {
        var pending = new ControlStateDto("local", null, null, "requested", null, null,
            new ControlPendingDto("rq-9", "PC-别人", "tok-b", DateTime.UtcNow.AddSeconds(50)), false, 5, 5, 60, "keep");
        var (f, d, _) = await LiveFleet.OneAsync(control: pending);
        Assert.Equal(LeaseStatus.Local, d.LeaseStatus);
        Assert.Equal("1号机已有待处理的远程申请（PC-别人）", d.Actions.Why(DeviceAction.LeaseRequest));
        // 本机自己的申请（同一客户端名）不算别人的
        var mine = pending with { Pending = new ControlPendingDto("rq-1", "PC-测试", "tok", DateTime.UtcNow.AddSeconds(50)) };
        var (g, e, _) = await LiveFleet.OneAsync(control: mine);
        Assert.Equal("", e.Actions.Why(DeviceAction.LeaseRequest));
        await f.DisposeAsync();
        await g.DisposeAsync();
    }
}
