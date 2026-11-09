using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M3 复核的修正（两台进程内假 HMI 经真 ReactorConnection，同 LiveDeviceSourceTests.Live.cs）：
/// 停止类只在 REST 确实通时可用（主机真的不通 → 只剩 none 类；WS 卡住时 GET /health 探测）；本机旧租约；申请超时后的冷却；自动授权的提示；
/// HMI 重启后令牌权限作废；改本机客户端名；退出前控制权申请还在触屏上。
/// </summary>
public partial class LiveDeviceSourceTests
{
    // ====================================================================== 停止类要有证据：主机真的不通 → 只剩 none 类

    [Fact]
    public async Task HostDown_AfterOnline_OnlyNoneClass_DialogsAndStopCellFollow_ThenBack()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var shell = await r.On(() => { var s = new ShellVm(r.Fleet); s.Toasts.Frozen = true; return s; });
        Assert.Null(await r.On(() => shell.StopCell.Reason));

        // HMI 整个不通（拒绝连接）：WS 断了，重连时网络层失败 → REST 也到不了 —— 不再「尽力发送」
        a.Down = true;
        a.DropWs();
        await r.AdvanceUntil(() => r.S(1).Conn.State == ConnStatus.Offline && !r.S(1).StopReachable, "主机不通：断线、停止类不可发");
        var conn = await r.On(() => r.S(1).Conn);
        Assert.False(conn.StopReachable);
        Assert.Matches("^1号机连接中断 · 重连中 [0-9]+ s$", await r.Why(1, DeviceAction.Estop));
        Assert.Matches("^1号机连接中断 · 重连中 [0-9]+ s$", await r.Why(1, DeviceAction.StopAll));
        Assert.Matches("^1号机连接中断 · 重连中 [0-9]+ s$", await r.Why(1, DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.Equal("", await r.Why(1, DeviceAction.Reconnect));                 // none 类照旧
        // 急停 / 全部停止对话框：这一行灰、写「连接中断 · 无法发送」，没有可选的设备；全局停止格「没有已连接的设备」
        await r.On(() => { r.Fleet.RefreshNow(); shell.OpenDialog("estop"); });
        var es = await r.On(() => (EstopDialogVm)shell.Dialogs.Current!);
        Assert.Equal(("连接中断 · 无法发送", false, "至少选择一台已连接的设备"), await r.On(() => (es.Rows[0].Reason, es.Rows[0].IsOn, es.GoButton.Reason)));
        await r.On(() => shell.OpenDialog("stopall"));
        var sa = await r.On(() => (StopAllDialogVm)shell.Dialogs.Current!);
        Assert.Equal(("连接中断 · 无法发送", false), await r.On(() => (sa.Rows[0].Reason, sa.Rows[0].IsOn)));
        Assert.Equal("没有已连接的设备", await r.On(() => shell.StopCell.Reason));
        await r.On(() => shell.Dialogs.Close());

        // 主机回来：WS 重连上 → 已连接，停止类回来
        a.Down = false;
        await r.AdvanceUntil(() => OnlineState(r.D(1)), "重新连上");
        Assert.True(await r.On(() => r.S(1).StopReachable && r.D(1).CanStop));
        await r.On(() => r.Fleet.RefreshNow());
        Assert.Null(await r.On(() => shell.StopCell.Reason));
        await r.On(() => shell.Dispose());
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task WsOnlyOutage_RestReachable_StopClassOnly_SessionsAndAlarmAckWait()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        a.WsFailStatus = 503;   // 只有 WS 不通（连接数已满）：握手被 HTTP 拒绝 = 主机答话了
        a.DropWs();
        await r.AdvanceUntil(() => r.S(1).Conn.State == ConnStatus.Offline, "WS 断了、被 503 拒绝");
        Assert.True(await r.On(() => r.S(1).StopReachable));
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));
        Assert.Equal("", await r.Why(1, DeviceAction.Estop));
        Assert.Matches("^1号机连接中断 · 重连中 [0-9]+ s$", await r.Why(1, DeviceAction.SessionStart));
        Assert.Matches("^1号机连接中断 · 重连中 [0-9]+ s$", await r.Why(1, DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 50)));
        int before = a.Count("POST", "/stop-all");
        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.StopAll, quiet: true));
        Assert.True(o.Ok);
        Assert.Equal(1, a.Count("POST", "/stop-all") - before);                    // 经 REST 送到了
        // 探测（GET /health）也说通：照旧可发
        await r.Advance(LiveDeviceSource.ProbeInterval.TotalSeconds + 1);
        Assert.True(a.Count("GET", "/health") > 0);
        Assert.True(await r.On(() => r.S(1).StopReachable));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task WsHangs_RestDown_ProbeFindsTheHostUnreachable()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        // 拔线 / 主机不回包：WS 重连的握手一直挂着（没有网络层的失败可看），REST 也不通 —— 只能靠探测 GET /health
        a.WsHang = true;
        a.Down = true;
        a.DropWs();
        await r.AdvanceUntil(() => r.S(1).Conn.State != ConnStatus.Online, "WS 断了");
        await r.AdvanceUntil(() => r.Connections[0].Ws.State == ConnectionState.Reconnecting && r.Connections[0].Ws.NextRetryAt == null, "重连的握手挂着");
        Assert.True(await r.On(() => r.S(1).StopReachable));                        // 还没有证据说不通
        await r.AdvanceUntil(() => !r.S(1).StopReachable, "探测失败 → 不通", stepSec: 0.5, maxSteps: 40);
        Assert.Equal("1号机正在重连 · 稍候", await r.Why(1, DeviceAction.Estop));
        Assert.False(await r.On(() => r.D(1).CanStop));
        // 主机回来：探测先说通（WS 握手还挂着）
        a.Down = false;
        await r.AdvanceUntil(() => r.S(1).StopReachable, "探测成功 → 通", stepSec: 0.5, maxSteps: 40);
        Assert.Equal("", await r.Why(1, DeviceAction.Estop));
        a.ReleaseWs();
        await r.AdvanceUntil(() => OnlineState(r.D(1)), "重新连上");
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 本机旧租约（R8 的边角）

    [Fact]
    public async Task OrphanLeaseUnderOwnName_HasItsOwnBadge_RequestWaitsForRemoteLost()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.ControlMode = "remote";
        a.Holder = Me;                                                            // 上次运行崩溃留下的：HMI 还记着本机的租约
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var l = await r.On(() => r.S(1).Lease);
        Assert.Equal((LeaseStatus.Other, "本机旧租约", "远程控制中（本机旧租约）", Me), (l.State, l.Display.BadgeShort, l.Display.Text, l.Holder));
        Assert.Equal("1号机远程控制中（本机旧租约） · 心跳超时后可重新申请", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Equal("1号机远程控制中（本机旧租约）", await r.Why(1, DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 50)));
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));
        // 心跳超时 → remoteLost：同一令牌 + 客户端名的申请会作废旧租约 —— 可以申请了
        a.LeaseLost = true;
        a.PushControl("lost", "remoteLost", Me);
        await r.Until(() => r.Connections[0].Lease.Server?.State == "remoteLost", "remoteLost");
        Assert.Equal("", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Equal(LeaseStatus.Other, await r.On(() => r.S(1).Lease.State));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public void LeaseRequestRejected409_UsesTheHmiWords_NotTheWriteCommandText()
    {
        var ex = new ApiException(OpCodes.NotControlHolder, 409, "当前控制权属于「PC-测试工位1」");
        Assert.Equal("当前控制权属于「PC-测试工位1」", LiveDeviceSource.LeaseRequestError(ex));
        Assert.Equal("409 CONTROL_REQUEST_PENDING：已有待处理的远程申请（PC-2）",
            LiveDeviceSource.LeaseRequestError(new ApiException(OpCodes.ControlRequestPending, 409, "已有待处理的远程申请（PC-2）")));
    }

    // ====================================================================== 申请超时后的冷却（HMI 超时同样冷却 30 s）

    [Fact]
    public async Task RequestTimesOut_NeutralCooldownUntilTheHmiAcceptsAgain()
    {
        await using var r = new LiveRig();
        r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Requested, "申请中");
        // 触屏一直没处理：HMI 60 s 到期（同样冷却 30 s），Client 70 s 时自己放弃（request:timeout）
        await r.AdvanceUntil(() => r.S(1).Lease.State == LeaseStatus.Local, "申请超时", stepSec: 0.5, maxSteps: 200);
        Assert.Contains(r.Toasts, t => t.Text == "1号机控制权申请超时 · 触屏未确认");
        Assert.Matches("^1号机申请刚被拒绝或超时 · (19|20|21) s 后可再申请$", await r.Why(1, DeviceAction.LeaseRequest));
        await r.Advance(22);
        Assert.Equal("", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task AutoGrant_ToastSaysObtained_NotTouchScreenConfirmed()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.AutoGrant = true;
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Remote, "自动授权");
        Assert.Contains(r.Toasts, t => t.Text == "1号机已获得控制权 · 远程控制中" && t.Kind == ToastKind.Ok);
        Assert.DoesNotContain(r.Toasts, t => t.Text.Contains("触屏已确认", StringComparison.Ordinal));
    }

    // ====================================================================== HMI 重启：令牌权限作废（R9）

    [Fact]
    public async Task HmiRestart_ForgetsTheLearnedRole()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.Forbid.Add("POST /stop-all");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.StopAll, quiet: true));
        Assert.True(await r.On(() => r.S(1).Role.ReadOnly && r.S(1).Role.NoEstop));
        // 令牌的角色改在 HMI 的配置里、要重启 HMI 才生效：重启后学到的作废
        a.Forbid.Clear();
        a.Restart("boot-2");
        await r.AdvanceUntil(() => OnlineState(r.D(1)) && r.S(1).Info?.BootId == "boot-2", "重启后重连");
        Assert.Equal(TokenRole.Unknown, await r.On(() => r.S(1).Role));
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 本机客户端名：下一次新建连接时生效

    [Fact]
    public async Task ClientName_Changed_SavedAndUsedByTheNextConnection()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.AutoGrant = true;
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        Assert.Equal("客户端名不能为空", await r.On(() => r.Fleet.SetClientName("   ")));
        Assert.Equal("", await r.On(() => r.Fleet.SetClientName("  PC-工位7  ")));
        Assert.Equal(("PC-工位7", "PC-工位7", "PC-工位7"), await r.On(() => (r.Fleet.ClientName, r.Fleet.Config!.ClientName, r.S(1).ClientName)));
        Assert.Equal(Me, r.Connections.Single().Options.ClientName);              // 已经建好的连接照旧
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.Disconnect));
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.Reconnect));
        await r.Until(() => OnlineState(r.D(1)), "重新连上");
        Assert.Equal("PC-工位7", r.Connections.Last().Options.ClientName);
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Remote, "自动授权");
        Assert.Equal("PC-工位7", a.Holder);                                       // HMI 认的持有者是新名字
        Assert.Equal(LeaseStatus.Remote, await r.On(() => r.S(1).Lease.State));
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 退出：控制权申请还在触屏上时先问

    [Fact]
    public async Task Exit_WithARequestOnTheTouchScreen_AsksFirst_WaitsForTheOutcome()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        r.Hmi("192.168.1.24", "PR8-B");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");
        var shell = await r.On(() => { var s = new ShellVm(r.Fleet); s.Toasts.Frozen = true; return s; });
        int confirmed = 0;
        await r.On(() => shell.ExitConfirmed += (_, _) => confirmed++);
        Assert.True(await r.On(() => shell.TryExit()));                          // 没有申请在等：照常退出
        Assert.False(await r.On(() => shell.Dialogs.IsOpen));

        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.Connections[0].Lease.RequestId == "rq1", "申请在 HMI 上");
        Assert.False(await r.On(() => shell.TryExit()));
        var dlg = await r.On(() => Assert.IsType<ExitDialogVm>(shell.Dialogs.Current));
        Assert.Equal(("退出控制软件？", "1号机的控制权申请还在触屏上"), await r.On(() => (dlg.Title, dlg.Sub)));
        Assert.Equal(new[] { "仍然退出", "等结果再退出" }, await r.On(() => ((FootBar)dlg.Footer!).Buttons.Select(b => b.Label).ToArray()));
        Assert.False(await r.On(() => shell.TryExit()));                         // 再问一次：还是同一个对话框
        Assert.Same(dlg, await r.On(() => shell.Dialogs.Current));

        // 「等结果再退出」：先取消申请（后台守卫继续查），等触屏的结果
        await r.Ui.InvokeAsync(() => ((IAsyncRelayCommand)dlg.WaitButton.Command).ExecuteAsync(null));
        Assert.Equal(LeaseStatus.Local, await r.On(() => r.S(1).Lease.State));
        Assert.True(await r.On(() => dlg.IsWaiting && !dlg.WaitButton.IsVisible));
        await r.On(() => r.Fleet.RefreshNow());
        Assert.Matches("^已取消申请，正在等触屏的结果 · 剩 [0-9]+ s（触屏批准的会立即释放）$", await r.On(() => dlg.Warn));
        Assert.Equal(0, confirmed);
        a.Approve = true;                                                         // 触屏这时才批准 → 守卫立即释放 → 没有申请在等了 → 退出
        await r.AdvanceUntil(() => a.Released.Count > 0, "迟到的批准被释放");
        await r.AdvanceUntil(() => { r.Fleet.RefreshNow(); return confirmed == 1; }, "等到结果后退出");
        Assert.False(await r.On(() => shell.Dialogs.IsOpen));
        Assert.Equal("L1", a.Released.Single());
        await r.On(() => shell.Dispose());
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task Exit_AnywayExitsAtOnce_ClosingTheDialogStays()
    {
        await using var r = new LiveRig();
        r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var shell = await r.On(() => { var s = new ShellVm(r.Fleet); s.Toasts.Frozen = true; return s; });
        int confirmed = 0;
        await r.On(() => shell.ExitConfirmed += (_, _) => confirmed++);
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).LeaseRequestPending, "申请中");
        Assert.False(await r.On(() => shell.TryExit()));
        await r.On(() => shell.Dialogs.Close());                                  // 关掉 = 不退出
        Assert.Equal(0, confirmed);
        Assert.False(await r.On(() => shell.TryExit()));
        var dlg = await r.On(() => (ExitDialogVm)shell.Dialogs.Current!);
        await r.On(() => dlg.ExitButton.Command.Execute(null));                   // 「仍然退出」
        Assert.Equal(1, confirmed);
        Assert.False(await r.On(() => shell.Dialogs.IsOpen));
        await r.On(() => shell.Dispose());
    }
}
