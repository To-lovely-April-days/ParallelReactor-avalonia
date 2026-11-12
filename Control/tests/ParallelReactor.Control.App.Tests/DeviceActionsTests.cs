using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;
using DeviceActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// App 的操作接线（Fleet/DeviceActions；Core 的 GuardTests 已覆盖原因文字本身）：守卫给的原因怎么到达界面（warn toast、不改状态）、
/// <b>停止类不看控制权</b>、执行时<b>再查一次守卫</b>（界面状态过期）、停止类在本机「以为」没在运行时照样发送（info 语气）、
/// 断线只剩 none 类、异常与命令警告（HW_UNCONFIRMED）翻译成文字。演示数据（冻结）+ 实时源（假连接 + 脚本化的 REST）。
/// </summary>
public class DeviceActionsTests
{
    private static (FleetVm Fleet, List<(string Text, ToastKind Kind)> Toasts) Demo(string query = "freeze=1")
    {
        var f = FleetVm.CreateDemo(DemoParams.Parse(query));
        var toasts = new List<(string, ToastKind)>();
        f.Toast += (t, k) => toasts.Add((t, k));
        return (f, toasts);
    }

    [Fact]
    public void LocalLease_BlocksControl_ButNeverStopClass()
    {
        var (f, _) = Demo();
        var d2 = f.Device(2)!;
        Assert.Equal(LeaseStatus.Local, d2.LeaseStatus);
        Assert.Equal("2号机为本地只读 · 先申请控制权", d2.Actions.Why(DeviceAction.HeatStart, ActionPayload.Ch(1)));
        Assert.Equal("2号机为本地只读 · 先申请控制权", d2.Why("setSp"));
        foreach (var a in new[] { DeviceAction.Stop, DeviceAction.HeatStop, DeviceAction.ProgAbort, DeviceAction.Disable })
            Assert.Equal("", d2.Actions.Why(a, ActionPayload.Ch(1)));
        Assert.Equal("", d2.Actions.Why(DeviceAction.StopAll));
        Assert.Equal("", d2.Actions.Why(DeviceAction.Estop));
        Assert.Equal("", d2.Why("leaseRequest"));
        Assert.Equal("2号机为本地只读 · 先申请控制权", d2.Actions.GuardText(GuardKind.Control));
        Assert.Equal("", d2.Actions.GuardText(GuardKind.Safe));
    }

    [Fact]
    public async Task Blocked_ToastsReason_AndChangesNothing()
    {
        var (f, toasts) = Demo();
        var d2 = f.Device(2)!;
        var before = d2.Reactor(1)!.SpC;
        var o = await d2.Actions.RunAsync(DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 99));
        Assert.False(o.Ok);
        Assert.Equal(("2号机为本地只读 · 先申请控制权", ToastKind.Warn), toasts.Single());
        f.RefreshNow();
        Assert.Equal(before, d2.Reactor(1)!.SpC);
    }

    [Fact]
    public async Task StopClass_RunsWithoutLease_AndUpdatesInPlace()
    {
        var (f, toasts) = Demo();
        var d2 = f.Device(2)!;
        var rv1 = d2.Reactor(1)!;
        Assert.Equal(StateKey.React, rv1.Key);
        var o = await d2.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        Assert.True(o.Ok);
        Assert.Contains(("2号机 · RV1 已停止", ToastKind.Warn), toasts);
        Assert.Same(rv1, d2.Reactor(1));                                   // 同一个实例，就地更新
        Assert.Equal(StateKey.Stopped, rv1.Key);
        Assert.Equal("已停止", rv1.Word);
    }

    [Fact]
    public async Task StopClass_WhenNothingRuns_StillSent_InfoTone()
    {
        var (f, toasts) = Demo();
        var d3 = f.Device(3)!;
        var idle = d3.Reactors.First(r => r.Installed && r.Own == StateKey.Idle);
        var o = await d3.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(idle.N));
        Assert.True(o.Ok && o.Info);
        Assert.Equal(ToastKind.Info, toasts.Last().Kind);
        Assert.Contains("未在运行 · 已发送停止", toasts.Last().Text);
    }

    [Fact]
    public void Offline_OnlyNoneClassLeft()
    {
        var (f, _) = Demo("devs=4&freeze=1");
        var d4 = f.Device(4)!;
        Assert.True(d4.IsOffline);
        Assert.StartsWith("4号机连接中断 · 重连中 ", d4.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.StartsWith("4号机连接中断", d4.Actions.Why(DeviceAction.Estop));
        Assert.StartsWith("4号机连接中断", d4.Why("leaseRequest"));
        Assert.Equal("", d4.Why("reconnect"));
        Assert.Equal("", d4.Why("disconnect"));
        Assert.Equal("", d4.Why("remove"));
    }

    [Fact]
    public async Task Command_RechecksGuard_WhenExecuted()
    {
        var (f, toasts) = Demo();
        var d1 = f.Device(1)!;
        Assert.Equal(LeaseStatus.Remote, d1.LeaseStatus);
        var cmd = (IAsyncRelayCommand)d1.Actions.Command(DeviceAction.SetSp, () => new ActionPayload(N: 1, Sp: 121));
        Assert.Equal("", d1.Actions.Why(DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 121)));   // 现在可以
        f.ApplyDemoParams(DemoParams.Parse("freeze=1&lease=1:local"));                            // 按钮还在、状态已经变了
        await cmd.ExecuteAsync(null);
        Assert.Equal(("1号机为本地只读 · 先申请控制权", ToastKind.Warn), toasts.Last());
        Assert.Equal(120, d1.Reactor(1)!.SpC);
    }

    [Fact]
    public async Task DeviceMenuAction_GoesThroughGuard()
    {
        var (f, toasts) = Demo();
        Assert.Null(f.Act(1, "leaseRelease"));
        await Task.Yield();
        f.RefreshNow();
        Assert.Equal(LeaseStatus.Local, f.Device(1)!.LeaseStatus);
        Assert.Contains(toasts, t => t.Text == "1号机 · 控制权已释放 · 回到本地只读");
        Assert.Equal("未知操作 fly", f.Act(1, "fly"));
        Assert.Equal("设备不存在", f.Act(9, "stopAll"));
    }

    [Fact]
    public void Describe_TranslatesErrors()
    {
        Assert.Equal("409 非控制权持有者：请先申请远程控制", DeviceActions.Describe(new ApiException(OpCodes.NotControlHolder, 409, "x")));
        Assert.Equal("409 本地模式：触屏未开放远程控制，请先申请控制权", DeviceActions.Describe(new ApiException(OpCodes.ModeLocal, 409, "x")));
        Assert.Equal("409 控制权已过期：请重新申请", DeviceActions.Describe(new ApiException(OpCodes.ControlLeaseExpired, 409, "x")));
        Assert.Equal(DeviceActions.HwUnconfirmedText, DeviceActions.Describe(new ApiException(OpCodes.HwUnconfirmed, 202, "x")));
        Assert.Equal("422 CHANNEL_ALARM：通道报警", DeviceActions.Describe(new ApiException(OpCodes.ChannelAlarm, 422, "通道报警")));
        Assert.Equal("网络错误：断了", DeviceActions.Describe(new HttpRequestException("断了")));
        Assert.Equal("请求超时", DeviceActions.Describe(new TaskCanceledException()));
        Assert.Null(DeviceActions.WarningText(Array.Empty<string>()));
        Assert.Equal(DeviceActions.HwUnconfirmedText, DeviceActions.WarningText(new[] { OpCodes.HwUnconfirmed }));
        Assert.Equal(DeviceActions.HwUnconfirmedText + "；搅拌未停", DeviceActions.WarningText(new[] { OpCodes.HwUnconfirmed, "搅拌未停" }));
    }

    // ———————————————————— 实时源（假连接） ————————————————————

    private static async Task<(FleetVm Fleet, DeviceVm Device, FakeReactorConnection Fake, List<(string, ToastKind)> Toasts)> Live(Action<ScriptedHttpHandler>? script = null, ControlStateDto? control = null)
    {
        // M3：设备经 DeviceRegistry 添加（内存配置，不写文件），添加后马上连接（假连接同步完成）
        var (f, d, fake) = await LiveFleet.OneAsync(script, control);
        var toasts = new List<(string, ToastKind)>();
        f.Toast += (t, k) => { lock (toasts) toasts.Add((t, k)); };
        return (f, d, fake, toasts);
    }

    [Fact]
    public async Task Live_StopClass_WithoutLease_SendsAndReportsHwUnconfirmed()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/1/stop$", c => ScriptedHttpHandler.Command(c, true, null, 5, false, OpCodes.HwUnconfirmed)));
        Assert.Equal(LeaseStatus.Local, d.LeaseStatus);
        var o = await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        Assert.True(o.Ok);
        Assert.Equal(1, fake.Http.Count("POST", "^/channels/1/stop$"));
        // M6：硬件未确认时只提示一条 warn（代替 info 结果 + 警告两条）
        Assert.Contains(toasts, t => t.Item1 == "1号机 · RV1 停止 · " + DeviceActions.HwUnconfirmedText && t.Item2 == ToastKind.Warn);
        Assert.DoesNotContain(toasts, t => t.Item1 == "1号机 · RV1 未在运行 · 已发送停止");
        await f.DisposeAsync();
        Assert.Equal(0, fake.TotalHandlers);
    }

    [Fact]
    public async Task Live_ControlClass_WithoutLease_NeverReachesTheDevice()
    {
        var (f, d, fake, toasts) = await Live();
        var o = await d.Actions.RunAsync(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 60));
        Assert.False(o.Ok);
        Assert.Equal("1号机为本地只读 · 先申请控制权", o.Message);
        Assert.Empty(fake.Http.Of("POST", "heat"));
        Assert.Contains(toasts, t => t.Item1 == "1号机为本地只读 · 先申请控制权");
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_LeaseHeldByAnotherClient_IsTheFifthState()
    {
        var other = new ControlStateDto("remote", "PC-别人", null, "remote", null, null, null, false, 5, 5, 60, "keep");
        var (f, d, _, _) = await Live(control: other);
        Assert.Equal(LeaseStatus.Other, d.LeaseStatus);
        Assert.Equal("远程控制中（其他客户端 PC-别人）", d.LeaseText);
        Assert.Equal("1号机远程控制中（其他客户端 PC-别人）", d.Actions.Why(DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));
        Assert.Equal("", d.Actions.Why(DeviceAction.StopAll));                 // 停止类照样可以
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_DisconnectThenReconnect_KeepsTheDevice()
    {
        var (f, d, first, toasts) = await Live();
        var o = await d.Actions.RunAsync(DeviceAction.Disconnect);
        Assert.True(o.Ok);
        Assert.Equal("1号机 · 已断开连接", o.Message);
        Assert.Equal(0, first.TotalHandlers);
        f.RefreshNow();
        Assert.Equal(Controls.Pr.ConnState.Disconnected, d.Conn);
        Assert.Same(d, f.Device(1));                                           // 设备留在列表里
        Assert.Equal("", d.Why("reconnect"));
        var r = await d.Actions.RunAsync(DeviceAction.Reconnect);
        Assert.True(r.Ok);
        Assert.Equal("1号机 · 正在重连…", r.Message);
        for (int i = 0; i < 50 && d.Source is LiveDeviceSource { Connection: null }; i++) await Task.Delay(20);
        f.RefreshNow();
        Assert.Equal(Controls.Pr.ConnState.Online, d.Conn);
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_Facts_FromTelemetryAndModel()
    {
        var (f, d, fake, _) = await Live();
        var facts = d.Source.Facts();
        Assert.Equal(8, facts.Channels.Count);                                  // HT-PR8：8 个位置
        Assert.Null(facts.Channel(1)!.Program);                                 // 没跑过程序、本机也没下发过
        Assert.Equal(ProgramStatusOf(facts, 2), Core.Programs.ProgramStatus.Aborted);   // RV2 的程序被中止过
        Assert.Equal("1号机为本地只读 · 先申请控制权", d.Actions.Why(DeviceAction.ProgRun, ActionPayload.Ch(1)));
        Assert.True(facts.SharedStir);
        await f.DisposeAsync();
        Assert.True(fake.Disposed);
    }

    private static Core.Programs.ProgramStatus? ProgramStatusOf(GuardFacts f, int n) => f.Channel(n)!.Program;

    // ———————————————————— WS 重连中：停止类照样能发（安全） ————————————————————

    /// <summary>
    /// 只是 WebSocket 断了在重连（1008 慢消费者、心跳超时都会这样；REST 客户端还在）：急停、全部停止、停止类照样可用、照样发出去；
    /// 控制类和申请控制权照样「正在重连 · 稍候」；全局停止格和急停 / 全部停止对话框都算这台。用户断开之后（没有连接对象）才只剩 none 类。
    /// 旧界面（b1a4c11 MainViewModel.StopClass）也是只要有连接对象就能停。
    /// </summary>
    [Fact]
    public async Task Live_WsReconnecting_StopClassStillSent_ControlLocked()
    {
        var (f, d, fake, _) = await Live(h =>
        {
            h.On("POST", "/stop-all", c => ScriptedHttpHandler.Command(c, true, null));
            h.On("POST", "/estop", c => ScriptedHttpHandler.Command(c, true, null));
        });
        var shell = new ShellVm(f);
        shell.Toasts.Frozen = true;
        fake.RaiseConnection(ConnectionState.Reconnecting);
        f.RefreshNow();
        Assert.Equal(Controls.Pr.ConnState.Connecting, d.Conn);
        Assert.True(d.IsOffline);
        Assert.True(d.CanStop);
        Assert.Equal("", d.Actions.Why(DeviceAction.Estop));
        Assert.Equal("", d.Actions.Why(DeviceAction.StopAll));
        Assert.Equal("", d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.Equal("", d.Why("stopAll"));                                        // 设备菜单「全部停止」
        Assert.Equal("1号机正在重连 · 稍候", d.Actions.Why(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 60)));
        Assert.Equal("1号机正在重连 · 稍候", d.Why("leaseRequest"));
        shell.RefreshChrome();
        Assert.Null(shell.StopCell.Reason);                                        // 全局停止格照常可点
        shell.Select(1);
        var insp = Assert.IsType<DeviceInspectorVm>(shell.Inspector.Body);
        Assert.Null(insp.StopAll.Reason);                                          // 检查器的全部停止、急停照常可用
        Assert.Null(insp.Estop.Reason);
        Assert.Equal("1号机正在重连 · 稍候", insp.RunAll.Reason);
        Assert.EndsWith("重连前只能查看；停止类操作随时可用", insp.Lease.Note);       // 不写「不能操作」
        shell.OpenDialog("estop");
        var dlg = Assert.IsType<EstopDialogVm>(shell.Dialogs.Current);
        var pick = Assert.Single(dlg.Rows);
        Assert.Equal("1号机", pick.Alias);
        Assert.True(pick.IsOn);                                                    // 只是 WebSocket 在重连：照样预选
        Assert.Null(dlg.GoButton.Reason);
        shell.Dialogs.Close();
        var o = await d.Actions.RunAsync(DeviceAction.StopAll);
        Assert.True(o.Ok, o.Message);
        Assert.Equal(1, fake.Http.Count("POST", "/stop-all"));
        // 用户断开之后：没有连接对象了 —— 只剩 none 类
        await d.Actions.RunAsync(DeviceAction.Disconnect);
        f.RefreshNow();
        Assert.False(d.CanStop);
        Assert.Equal("1号机已断开 · 先连接", d.Actions.Why(DeviceAction.Estop));
        shell.RefreshChrome();
        Assert.Equal("没有已连接的设备", shell.StopCell.Reason);
        Assert.EndsWith("断线时只能查看，不能操作", insp.Lease.Note);
        shell.Dispose();
        await f.DisposeAsync();
    }

    // ———————————————————— D6：「启动」「全部运行」绝不调 POST /run ————————————————————

    /// <summary>写命令的脚本：控制权直接批准；/channels/{n}/start、PUT program、program/run 成功；POST /run 也给了脚本（被调用的话能看见）。</summary>
    private static void ScriptWrites(ScriptedHttpHandler h)
    {
        FakeReactorConnection.ScriptGrantedLease(h);
        h.On("POST", @"/channels/\d+/start", c => ScriptedHttpHandler.Command(c, true, null));
        h.On("PUT", @"/channels/\d+/program", c => ScriptedHttpHandler.Command(c, false, null));
        h.On("POST", @"/channels/\d+/program/run", c => ScriptedHttpHandler.Command(c, true, new ProgramRunResult(1, 4)));
        h.On("POST", "/run", c => ScriptedHttpHandler.Command(c, true, null));
    }

    private static async Task HoldLease(FleetVm f, DeviceVm d, FakeReactorConnection fake)
    {
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        for (int i = 0; i < 200 && fake.Lease.State != ParallelReactor.Client.LeaseState.Holding; i++) await Task.Delay(10);
        Assert.Equal(ParallelReactor.Client.LeaseState.Holding, fake.Lease.State);
        f.RefreshNow();
        Assert.Equal(LeaseStatus.Remote, d.LeaseStatus);
    }

    /// <summary>「启动」（D6）：本机没下发过程序 → POST /channels/{n}/start（启用 + 加热到 SP）；下发之后 → program/run。两种都带租约头，都不碰 POST /run。</summary>
    [Fact]
    public async Task Live_Start_ChannelStartUntilDownloaded_ThenProgramRun_NeverPostRun()
    {
        var (f, d, fake, _) = await Live(ScriptWrites);
        await HoldLease(f, d, fake);
        var live = (LiveDeviceSource)d.Source;
        var o = await d.Actions.RunAsync(DeviceAction.Start, ActionPayload.Ch(1));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 已启动 · 加热至 70 ℃", o.Message);
        var start = fake.Http.Of("POST", "/channels/1/start").Single();
        Assert.Equal("L-test", start.Lease);
        Assert.Equal(70, start.Json.GetProperty("tempC").GetDouble());              // SP 取自快照
        Assert.Equal(0, fake.Http.Count("POST", "/channels/1/program/run"));
        await live.UploadProgramAsync(1, ProgramDraftStore.Template("加氢测试-01"), System.Threading.CancellationToken.None);
        Assert.Equal(1, fake.Http.Count("PUT", "/channels/1/program"));
        o = await d.Actions.RunAsync(DeviceAction.Start, ActionPayload.Ch(1));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 已启动 · 程序「加氢测试-01」", o.Message);
        Assert.Equal("L-test", fake.Http.Of("POST", "/channels/1/program/run").Single().Lease);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/start"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 「全部运行」（D6）：只运行本机这次下发的程序 —— 快照里 RV2 有触屏放的程序（已中止），不算；守卫与执行用同一个判断，
    /// 所以没有本机下发的程序时按钮带原因（不再出现「已启动 0 个反应釜」）。HMI 重启后本机下发的记录清空。全程不调 POST /run。
    /// </summary>
    [Fact]
    public async Task Live_RunAll_OnlyProgramsThisPcDownloaded_NeverPostRun()
    {
        var (f, d, fake, _) = await Live(ScriptWrites);
        await HoldLease(f, d, fake);
        var live = (LiveDeviceSource)d.Source;
        Assert.Equal(Core.Programs.ProgramStatus.Aborted, ProgramStatusOf(d.Source.Facts(), 2));   // 触屏的程序
        Assert.Equal("没有可运行的反应釜", d.Actions.Why(DeviceAction.RunAll));
        Assert.Empty(live.RunAllTargets());
        Assert.False((await d.Actions.RunAsync(DeviceAction.RunAll)).Ok);
        Assert.Empty(fake.Http.Of("POST", ".*/program/run"));
        await live.UploadProgramAsync(1, ProgramDraftStore.Template("加氢测试-01"), System.Threading.CancellationToken.None);
        Assert.Equal("", d.Actions.Why(DeviceAction.RunAll));
        Assert.Equal(new[] { 1 }, live.RunAllTargets());
        var o = await d.Actions.RunAsync(DeviceAction.RunAll);
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · 已启动 1 个反应釜", o.Message);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/2/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        // HMI 重启：它手里的程序不一定还是本机下发的那份 → 「启动」回到启用 + 加热，「全部运行」没有对象
        fake.RaiseRestarted();
        f.RefreshNow();
        Assert.Null(live.Downloaded(1));
        Assert.Equal("没有可运行的反应釜", d.Actions.Why(DeviceAction.RunAll));
        Assert.True((await d.Actions.RunAsync(DeviceAction.Start, ActionPayload.Ch(1))).Ok);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/start"));
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 检查器「全部运行」在真机上先列清单确认（D6）：打开清单不发任何命令；清单 = 反应釜 · 程序名 · 程序状态；确认后正好运行清单里的反应釜。
    /// </summary>
    [Fact]
    public async Task Live_RunAll_FromInspector_ConfirmsListFirst()
    {
        var (f, d, fake, _) = await Live(ScriptWrites);
        var shell = new ShellVm(f);
        shell.Toasts.Frozen = true;
        await HoldLease(f, d, fake);
        await ((LiveDeviceSource)d.Source).UploadProgramAsync(1, ProgramDraftStore.Template("加氢测试-01"), System.Threading.CancellationToken.None);
        f.RefreshNow();
        shell.Select(1);
        var insp = Assert.IsType<DeviceInspectorVm>(shell.Inspector.Body);
        Assert.Null(insp.RunAll.Reason);
        insp.RunAll.Command.Execute(null);
        var dlg = Assert.IsType<RunAllDialogVm>(shell.Dialogs.Current);
        Assert.Equal(("全部运行", "运行1号机上已下发程序的反应釜"), (dlg.Title, dlg.Sub));
        var row = Assert.Single(dlg.Rows);
        Assert.Equal(("1号机 · RV1", "程序「加氢测试-01」", "已下发 · 待运行"), (row.Name, row.Sub, row.Status));
        Assert.Equal("已选 1 个反应釜", dlg.FooterLeft);
        Assert.Empty(fake.Http.Of("POST", ".*/program/run"));                         // 清单打开时什么都没发
        var go = ((FootBar)dlg.Footer!).Buttons[1];
        Assert.Equal(("全部运行", FootKind.Primary), (go.Label, go.Kind));
        await ((IAsyncRelayCommand)go.Command).ExecuteAsync(null);
        Assert.False(shell.Dialogs.IsOpen);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        Assert.Contains(shell.Toasts.Items, t => t.Text == "1号机 · 已启动 1 个反应釜");
        shell.Dispose();
        await f.DisposeAsync();
    }
}
