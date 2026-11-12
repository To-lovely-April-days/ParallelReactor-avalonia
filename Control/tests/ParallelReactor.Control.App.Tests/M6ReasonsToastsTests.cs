using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;
using AlarmDto = ParallelReactor.Services.AlarmDto;
using DeviceActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 3 组（禁用原因、提示、HW_UNCONFIRMED）在实时数据源上（假连接 + 脚本化的 REST，不用 HMI 模拟器）：
/// <list type="bullet">
/// <item>HW_UNCONFIRMED：单台只提示一条 warn（代替 ✓ + 警告，急停保持 err）；急停 / 全部停止对话框里几台的警告合成一条，跟在汇总后面；
///   汇总之后才回来的（真机 HMI 等硬件 2 s 才回 202，总比汇总晚）按批合成；硬件未确认的设备 / 反应釜只写在合成的那一条里。
///   HMI 之后推来的最终结果（opEvent「&lt;action&gt;.result」，同 opId）再提示「硬件已执行完」（info，中性：事件不带 warnings）/「硬件执行失败：…」；
///   同一台上一起等的几次合成一条；最终结果比 HTTP 的 202 先到时直接按它说（不再说「未确认」）。</item>
/// <item>报警出现时提示一次（带设备名，错误报警 err、提醒 warn），确认 / 解除不再提示。</item>
/// <item>急停 / 全部停止对话框：令牌没有急停权限 / 只能读取又没有急停权限的设备不能选，行上写明原因。</item>
/// <item>没有原因的灰按钮：确认报警、「测试连接」在请求回来之前照样可以点（命令的 CanExecute 一直是 true）；
///   测的时候再点「测试连接」、或关掉对话框，被取消的那一次照常结束（不把取消异常抛回 UI 线程）。</item>
/// </list>
/// </summary>
public class M6ReasonsToastsTests
{
    private static readonly string Hw = DeviceActions.HwUnconfirmedText;

    /// <summary>202 + warnings [HW_UNCONFIRMED]（opId = 请求的 Idempotency-Key，同 ScriptedHttpHandler.Command）。</summary>
    private static HttpResponseMessage Unconfirmed(ScriptedHttpHandler.Call c) => ScriptedHttpHandler.Command(c, true, null, 5, false, OpCodes.HwUnconfirmed);

    /// <summary>假连接上推一条 WS opEvent（FakeReactorConnection 没有现成的 Raise 方法：取它的事件字段）。</summary>
    private static void RaiseOpEvent(FakeReactorConnection f, OpEventMsg m)
    {
        var field = typeof(FakeReactorConnection).GetField("_opEvent", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("FakeReactorConnection 没有 _opEvent 字段");
        ((Action<OpEventMsg>?)field.GetValue(f))?.Invoke(m);
    }

    /// <summary>HMI 的最终结果（REPO Api/Endpoints.Write.cs：action + ".result"，同 opId）。</summary>
    private static OpEventMsg Result(string opId, string action, int status = 200, string code = OpCodes.Ok, string message = "") =>
        new("opEvent", opId, action + ".result", "PC-测试", "remote", status, status is >= 200 and < 300 ? "confirmed" : "rejected", code, message, 9, DateTime.UtcNow);

    private static async Task<(FleetVm Fleet, DeviceVm Device, FakeReactorConnection Fake, List<(string Text, ToastKind Kind)> Toasts)> Live(Action<ScriptedHttpHandler>? script = null)
    {
        var (f, d, fake) = await LiveFleet.OneAsync(script);
        var toasts = new List<(string, ToastKind)>();
        f.Toast += (t, k) => { lock (toasts) toasts.Add((t, k)); };
        return (f, d, fake, toasts);
    }

    private static List<(string Text, ToastKind Kind)> Snapshot(List<(string Text, ToastKind Kind)> toasts) { lock (toasts) return toasts.ToList(); }

    // ———————————————————— HW_UNCONFIRMED ————————————————————

    [Fact]
    public async Task HwUnconfirmed_OneWarnToast_ThenFinalResultDone()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/1/stop$", Unconfirmed));
        var o = await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        Assert.True(o.Ok);
        Assert.Equal(new[] { ("1号机 · RV1 停止 · " + Hw, ToastKind.Warn) }, Snapshot(toasts));   // 一条，没有「未在运行 · 已发送停止」
        var src = (LiveDeviceSource)d.Source;
        Assert.Equal(1, src.HwPending);
        var opId = fake.Http.Of("POST", "^/channels/1/stop$").Single().Idem!;
        RaiseOpEvent(fake, Result("other-op", "channel.stop"));                        // 别的命令的结果：不算
        RaiseOpEvent(fake, new OpEventMsg("opEvent", opId, "channel.stop", "PC-测试", "remote", 202, "accepted", OpCodes.Ok, "", 8, DateTime.UtcNow));   // 第一条（202 本身）不是结果
        Assert.Single(Snapshot(toasts));
        RaiseOpEvent(fake, Result(opId, "channel.stop"));
        Assert.Equal(("1号机 · RV1 停止 · " + LiveDeviceSource.HwDoneText, ToastKind.Info), Snapshot(toasts).Last());   // 中性：事件不带最终的 warnings
        Assert.Equal(0, src.HwPending);
        RaiseOpEvent(fake, Result(opId, "channel.stop"));                              // 重复的结果：不再提示
        Assert.Equal(2, Snapshot(toasts).Count);
        await f.DisposeAsync();
        Assert.Equal(0, fake.TotalHandlers);
    }

    [Fact]
    public async Task HwUnconfirmed_FinalFailure_IsAnErrToast()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/2/heat/stop$", Unconfirmed));
        await d.Actions.RunAsync(DeviceAction.HeatStop, ActionPayload.Ch(2));
        Assert.Equal(("1号机 · RV2 停止加热 · " + Hw, ToastKind.Warn), Snapshot(toasts).Single());
        var opId = fake.Http.Of("POST", "^/channels/2/heat/stop$").Single().Idem!;
        RaiseOpEvent(fake, Result(opId, "channel.heat.stop", 500, OpCodes.Internal, "命令执行异常 · 加热模块无响应"));
        Assert.Equal(("1号机 · RV2 停止加热 · " + LiveDeviceSource.HwFailedText + "：命令执行异常 · 加热模块无响应（" + OpCodes.Internal + "）", ToastKind.Err),
            Snapshot(toasts).Last());
        await f.DisposeAsync();
    }

    [Fact]
    public async Task HwUnconfirmed_Estop_KeepsTheErrTone()
    {
        var (f, d, _, toasts) = await Live(h => h.On("POST", "^/estop$", Unconfirmed));
        await d.Actions.RunAsync(DeviceAction.Estop);
        Assert.Equal(("1号机 · 急停 · " + Hw, ToastKind.Err), Snapshot(toasts).Single());
        await f.DisposeAsync();
    }

    /// <summary>同一台上一起等的两次停止：两条结果到齐后合成一条「RV1、RV2 停止 · 硬件已执行完」。</summary>
    [Fact]
    public async Task HwUnconfirmed_ResultsOfOneDevice_MergedIntoOneToast()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/[12]/stop$", Unconfirmed));
        await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(2));
        var ids = fake.Http.Of("POST", "^/channels/[12]/stop$").Select(c => c.Idem!).ToList();
        Assert.Equal(2, ids.Count);
        RaiseOpEvent(fake, Result(ids[0], "channel.stop"));
        Assert.Equal(2, Snapshot(toasts).Count);                                       // 第一条结果先留着（RV2 还在等）
        RaiseOpEvent(fake, Result(ids[1], "channel.stop"));
        Assert.Equal(("1号机 · RV1、RV2 停止 · " + LiveDeviceSource.HwDoneText, ToastKind.Info), Snapshot(toasts).Last());
        Assert.Equal(3, Snapshot(toasts).Count);
        await f.DisposeAsync();
    }

    /// <summary>另一次还在等：先到的结果最多留 1 s（Tick 放出去），不被一直挡着。</summary>
    [Fact]
    public async Task HwUnconfirmed_SettledResultReleasedByTick()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/[12]/stop$", Unconfirmed));
        await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(2));
        var first = fake.Http.Of("POST", "^/channels/1/stop$").Single().Idem!;
        RaiseOpEvent(fake, Result(first, "channel.stop"));
        var src = (LiveDeviceSource)d.Source;
        src.Tick();
        Assert.Equal(2, Snapshot(toasts).Count);
        await Task.Delay(LiveDeviceSource.HwHold + TimeSpan.FromMilliseconds(150));
        src.Tick();
        Assert.Equal(("1号机 · RV1 停止 · " + LiveDeviceSource.HwDoneText, ToastKind.Info), Snapshot(toasts).Last());
        Assert.Equal(1, src.HwPending);                                                 // RV2 还在等
        await f.DisposeAsync();
    }

    /// <summary>WS 的最终结果比 HTTP 的 202 先处理完：不再说「硬件未确认」，照常提示结果（没有第二条）。</summary>
    [Fact]
    public async Task HwUnconfirmed_ResultArrivedFirst_NormalToastOnly()
    {
        var (f, d, fake, toasts) = await Live(h => h.On("POST", "^/channels/1/stop$", Unconfirmed));
        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate = req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/channels/1/stop", StringComparison.Ordinal)) return null;
            arrived.TrySetResult(req.Headers.TryGetValues(ApiHeaders.Idempotency, out var v) ? v.First() : "");
            return release.Task;
        };
        var run = d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(1));
        var opId = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RaiseOpEvent(fake, Result(opId, "channel.stop"));
        release.SetResult();
        var o = await run;
        Assert.True(o.Ok);
        Assert.Empty(o.Warnings);
        Assert.Equal(new[] { ("1号机 · RV1 未在运行 · 已发送停止", ToastKind.Info) }, Snapshot(toasts));
        Assert.Equal(0, ((LiveDeviceSource)d.Source).HwPending);
        await f.DisposeAsync();
    }

    /// <summary>
    /// 急停对话框发给三台、都马上回 HW_UNCONFIRMED（现在的 HMI 急停是同步的、不会这样回；这里核对汇总的写法）：只一条合成的警告，
    /// 硬件未确认的设备不再列进「急停已发送到…」，语气保持 err。
    /// </summary>
    [Fact]
    public async Task HwUnconfirmed_EstopDialog_MergesTheWarnings()
    {
        var f = LiveFleet.Create(x => x.Http.On("POST", "^/estop$", Unconfirmed));
        var ds = new[] { "1号机", "2号机", "3号机" }.Select((a, i) => f.AddDevice(new NewDevice("127.0.0." + (i + 1), 8080, "tok", a, false, false))!).ToList();
        foreach (var d in ds) await LiveFleet.WaitOnline(d);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("estop");
        var dlg = Assert.IsType<EstopDialogVm>(s.Dialogs.Current);
        Assert.True(dlg.GoButton.Command.CanExecute(null));
        await ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        var texts = s.Toasts.Items.Select(t => (t.Text, t.Kind)).ToList();
        Assert.Equal(new[] { ("1号机、2号机、3号机 · 急停 · " + Hw, ToastKind.Err) }, texts);
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>全部停止对话框里的 N 台设备一个个加进来（按创建顺序给假连接定脚本），返回外壳和收到的提示（按出现顺序，线程安全）。</summary>
    private static async Task<(FleetVm Fleet, ShellVm Shell, System.Collections.Concurrent.ConcurrentQueue<(string Text, ToastKind Kind)> Toasts)> StopAllFleet(
        int n, Action<int, FakeReactorConnection> script)
    {
        int made = 0;
        var f = LiveFleet.Create(x => script(made++, x));
        var ds = Enumerable.Range(1, n).Select(i => f.AddDevice(new NewDevice("127.0.0." + i, 8080, "tok", i + "号机", false, false))!).ToList();
        foreach (var d in ds) await LiveFleet.WaitOnline(d);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var toasts = new System.Collections.Concurrent.ConcurrentQueue<(string, ToastKind)>();
        s.Toasts.Items.CollectionChanged += (_, e) => { if (e.NewItems != null) foreach (ToastItem t in e.NewItems) toasts.Enqueue((t.Text, t.Kind)); };
        return (f, s, toasts);
    }

    private static async Task WaitFor(Func<bool> done, int ms = 5000)
    {
        for (int i = 0; i < ms / 10 && !done(); i++) await Task.Delay(10);
    }

    /// <summary>
    /// 真机的时序（审查 stopall.fsx）：HMI 等硬件 2 s 才回 202 + HW_UNCONFIRMED（UiCommandBridge.HwWait），总比汇总（1.5 s）晚。
    /// 四台都晚到、都没确认：只一条合成的警告（以前每台一条，最多 3 条，第 4 台被挤掉），没有「全部停止已发送到…」。
    /// </summary>
    [Fact]
    public async Task HwUnconfirmed_StopAllDialog_LateResultsMergedIntoOneToast()
    {
        var (f, s, toasts) = await StopAllFleet(4, (_, x) =>
        {
            x.Http.On("POST", "^/stop-all$", Unconfirmed);
            x.Delays.Add(("POST", "/stop-all", TimeSpan.FromMilliseconds(400)));
        });
        s.OpenDialog("stopall");
        var dlg = Assert.IsType<StopAllDialogVm>(s.Dialogs.Current);
        dlg.SummaryWait = TimeSpan.FromMilliseconds(100);
        await ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.Empty(toasts);                                                            // 汇总时还没有结果
        await WaitFor(() => !toasts.IsEmpty);
        await Task.Delay(dlg.LateBatch + TimeSpan.FromMilliseconds(300));                 // 不会再来别的
        Assert.Equal(new[] { ("1号机、2号机、3号机、4号机 · 全部停止 · " + Hw, ToastKind.Warn) }, toasts.ToArray());
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>
    /// 先回来的进汇总；晚到的同一时间那几台合成一批（硬件未确认的只写在警告里）；更晚的（不通、重试中）自己一批，不拖住前面的。
    /// </summary>
    [Fact]
    public async Task StopAllDialog_LateResultsReportedInBatches()
    {
        var (f, s, toasts) = await StopAllFleet(4, (i, x) =>
        {
            if (i is 1 or 2) x.Http.On("POST", "^/stop-all$", Unconfirmed);
            else x.Http.On("POST", "^/stop-all$", c => ScriptedHttpHandler.Command(c, true, null));
            if (i is 1 or 2) x.Delays.Add(("POST", "/stop-all", TimeSpan.FromMilliseconds(800)));
            if (i == 3) x.Delays.Add(("POST", "/stop-all", TimeSpan.FromMilliseconds(2000)));
        });
        s.OpenDialog("stopall");
        var dlg = Assert.IsType<StopAllDialogVm>(s.Dialogs.Current);
        dlg.SummaryWait = TimeSpan.FromMilliseconds(300);
        dlg.LateBatch = TimeSpan.FromMilliseconds(400);
        bool stir = dlg.StopsStirring;
        await ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.Equal(new[] { (StopAllText.Sent(new[] { "1号机" }, stir), ToastKind.Warn) }, toasts.ToArray());
        await WaitFor(() => toasts.Count >= 3);
        Assert.Equal(new[]
        {
            (StopAllText.Sent(new[] { "1号机" }, stir), ToastKind.Warn),
            ("2号机、3号机 · 全部停止 · " + Hw, ToastKind.Warn),
            (StopAllText.Sent(new[] { "4号机" }, stir), ToastKind.Warn),
        }, toasts.ToArray());
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>真机「全部运行」的清单对话框：逐个反应釜运行，硬件未确认的几个合成一条警告；都没确认时没有「已启动 N 个反应釜」。</summary>
    [Fact]
    public async Task HwUnconfirmed_RunAllDialog_MergesTheChannels()
    {
        var (f, d, fake, _) = await Live(h =>
        {
            FakeReactorConnection.ScriptGrantedLease(h);
            h.On("PUT", @"/channels/\d+/program", c => ScriptedHttpHandler.Command(c, false, null));
            h.On("POST", @"/channels/\d+/program/run", Unconfirmed);
        });
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        for (int i = 0; i < 200 && fake.Lease.State != ParallelReactor.Client.LeaseState.Holding; i++) await Task.Delay(10);
        var live = (LiveDeviceSource)d.Source;
        for (int n = 1; n <= 4; n++) await live.UploadProgramAsync(n, ProgramDraftStore.Template("加氢测试-0" + n), CancellationToken.None);
        f.RefreshNow();
        var targets = live.RunAllTargets();
        Assert.NotEmpty(targets);
        s.Toasts.Clear();
        s.OpenDialog("runall", "1");
        var dlg = Assert.IsType<RunAllDialogVm>(s.Dialogs.Current);
        var go = ((FootBar)dlg.Footer!).Buttons[1];
        Assert.True(go.Command.CanExecute(null));
        await ((IAsyncRelayCommand)go.Command).ExecuteAsync(null);
        var texts = s.Toasts.Items.Select(t => (t.Text, t.Kind)).ToList();
        Assert.Equal(new[] { ("1号机 · RV" + string.Join("、RV", targets) + " 运行程序 · " + Hw, ToastKind.Warn) }, texts);
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public void WarningText_DedupesAndWarnLineNamesTheAction()
    {
        Assert.Equal(Hw, DeviceActions.WarningText(new[] { OpCodes.HwUnconfirmed, OpCodes.HwUnconfirmed }));
        Assert.True(DeviceActions.HasHwUnconfirmed(new[] { "x", OpCodes.HwUnconfirmed }));
        Assert.False(DeviceActions.HasHwUnconfirmed(Array.Empty<string>()));
        Assert.Equal("RV3 停止", DeviceActions.What(DeviceAction.Stop, ActionPayload.Ch(3)));
        Assert.Equal("急停", DeviceActions.What(DeviceAction.Estop, ActionPayload.Ch(3)));        // 整台的操作不写 RV
        Assert.Equal("1号机、2号机 · 全部停止 · " + Hw + "；搅拌未停",
            DeviceActions.WarnLine("1号机、2号机", "全部停止", new[] { OpCodes.HwUnconfirmed, "搅拌未停" }));
    }

    // ———————————————————— 报警出现 ————————————————————

    [Fact]
    public async Task LiveAlarm_ToastedOnceWhenRaised_WithTheDevice()
    {
        var (f, d, fake, toasts) = await Live();
        var t = DateTime.UtcNow;
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("71", "err", "RV1", 1, "超压 · 联锁动作", "3.12 MPa", t, "active", null)));
        Assert.Equal(("1号机 · 报警 RV1：超压 · 联锁动作", ToastKind.Err), Snapshot(toasts).Single());
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("71", "err", "RV1", 1, "超压 · 联锁动作", "3.12 MPa", t, "active", null)));   // 同一条再推一次
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("71", "err", "RV1", 1, "超压 · 联锁动作", "3.12 MPa", t, "ack", null)));
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("71", "err", "RV1", 1, "超压 · 联锁动作", "", t, "done", t.AddMinutes(1))));
        Assert.Single(Snapshot(toasts));                                                 // 确认、解除不再提示
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("72", "warn", "RV3", 3, "RV3 升温较慢", "已用 18 min", t, "active", null)));
        Assert.Equal(("1号机 · 提醒：RV3 升温较慢", ToastKind.Warn), Snapshot(toasts).Last());   // 标题自己带 RV 号：不重复
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("73", "info", "系统", null, "测试会话开始", "", t, "done", null)));
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("74", "err", "RV2", 2, "超温", "", t, "cleared", t)));   // 来的时候已经解除：不提示
        Assert.Equal(2, Snapshot(toasts).Count);
        await f.DisposeAsync();
    }

    // ———————————————————— 急停 / 全部停止：令牌权限 ————————————————————

    [Fact]
    public async Task PickDialogs_ExcludeDevicesTheTokenCannotStop()
    {
        var f = LiveFleet.Create();
        var d1 = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", false, false))!;
        var d2 = f.AddDevice(new NewDevice("127.0.0.2", 8080, "tok", "2号机", false, false))!;
        await LiveFleet.WaitOnline(d1);
        await LiveFleet.WaitOnline(d2);
        ((LiveDeviceSource)d1.Source).LearnForbidden(DeviceAction.Estop);           // 1号机：没有急停权限位
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("estop");
        var estop = Assert.IsType<EstopDialogVm>(s.Dialogs.Current);
        var r1 = estop.Rows.Single(r => r.K == d1.K);
        Assert.Equal(TokenRole.NoEstopText("1号机"), r1.Reason);
        Assert.Equal("1号机的令牌没有急停权限", r1.RunText);
        Assert.False(r1.IsOn);
        Assert.Equal(new[] { "2号机" }, estop.Picked.Select(d => d.Alias));
        estop.AllCommand.Execute(null);
        Assert.Equal(new[] { "2号机" }, estop.Picked.Select(d => d.Alias));                // 「全选」也不选它
        s.Dialogs.Close();
        // 全部停止：只是没有急停权限照样能停；只读令牌又没有急停权限的不能选
        s.OpenDialog("stopall");
        var stop = Assert.IsType<StopAllDialogVm>(s.Dialogs.Current);
        Assert.Null(stop.Rows.Single(r => r.K == d1.K).Reason);
        s.Dialogs.Close();
        ((LiveDeviceSource)d1.Source).LearnForbidden(DeviceAction.StopAll);         // 停止类也被拒：只读且没有急停权限
        f.RefreshNow();
        s.OpenDialog("stopall");
        stop = Assert.IsType<StopAllDialogVm>(s.Dialogs.Current);
        Assert.Equal(TokenRole.NoStopText("1号机"), stop.Rows.Single(r => r.K == d1.K).Reason);
        Assert.Equal(new[] { "2号机" }, stop.Picked.Select(d => d.Alias));
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>
    /// 「急停 1号机…」（estop:1）：1号机的令牌没有急停权限时，勾选条不叫用户去勾它（那一行勾不了）——
    /// 打开前就知道、打开之后才学到（另一处急停的 403）都一样。
    /// </summary>
    [Fact]
    public async Task EstopDialog_ForOne_CaptionNamesTheRoleWhenTheRowIsLocked()
    {
        var f = LiveFleet.Create();
        var d1 = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", false, false))!;
        var d2 = f.AddDevice(new NewDevice("127.0.0.2", 8080, "tok", "2号机", false, false))!;
        await LiveFleet.WaitOnline(d1);
        await LiveFleet.WaitOnline(d2);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("estop", "1");
        var dlg = Assert.IsType<EstopDialogVm>(s.Dialogs.Current);
        Assert.Equal("已预选 1号机 · 可再勾选其它设备", dlg.Caption);
        ((LiveDeviceSource)d1.Source).LearnForbidden(DeviceAction.Estop);              // 对话框开着的时候学到
        f.RefreshNow();
        dlg.Refresh();
        Assert.Equal("1号机 令牌没有急停权限 · 可勾选其它设备", dlg.Caption);
        Assert.Empty(dlg.Picked);
        Assert.NotNull(dlg.GoButton.Reason);
        s.Dialogs.Close();
        s.OpenDialog("estop", "1");                                                     // 打开前就知道
        dlg = Assert.IsType<EstopDialogVm>(s.Dialogs.Current);
        Assert.Equal("1号机 令牌没有急停权限 · 可勾选其它设备", dlg.Caption);
        Assert.Equal(TokenRole.NoEstopText("1号机"), dlg.Rows.Single(r => r.K == d1.K).Reason);
        s.Dispose();
        await f.DisposeAsync();
    }

    // ———————————————————— 没有原因的灰按钮 ————————————————————

    [Fact]
    public async Task AlarmAck_StaysClickableWhileTheRequestRuns()
    {
        var (f, d, fake, _) = await Live(h => h.On("POST", "^/alarms/71/ack$", c => ScriptedHttpHandler.Command(c, false, null)));
        fake.RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("71", "err", "RV1", 1, "超压", "", DateTime.UtcNow, "active", null)));
        f.RefreshNow();
        var a = d.Alarms.Single(x => x.Id == "71");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate = req => req.RequestUri!.AbsolutePath.EndsWith("/alarms/71/ack", StringComparison.Ordinal) ? release.Task : null;
        Assert.True(a.AckCommand.CanExecute(null));
        a.AckCommand.Execute(null);
        await Task.Delay(50);
        Assert.True(a.AckCommand.CanExecute(null));                                   // 请求还没回来：按钮不变灰（以前 AsyncRelayCommand 在这里是 false）
        release.SetResult();
        for (int i = 0; i < 200 && fake.Http.Count("POST", "^/alarms/71/ack$") == 0; i++) await Task.Delay(10);
        Assert.Equal(1, fake.Http.Count("POST", "^/alarms/71/ack$"));
        await f.DisposeAsync();
    }

    /// <summary>添加设备的「测试连接」：测的时候按钮照样能点（再点 = 重新测），「正在连接…」照常显示。</summary>
    [Fact]
    public async Task TestConnection_ButtonStaysClickableWhileTesting()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new ScriptedHttpHandler();
        h.On("GET", "/health", _ => ScriptedHttpHandler.Json(200, "{\"ok\":true}"));
        var f = LiveFleet.Create(http: () => new GateHandler(h, gate.Task));
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("add");
        var add = Assert.IsType<AddDeviceDialogVm>(s.Dialogs.Current);
        add.Host = "127.0.0.9";
        add.Token = "tok";
        var cmd = add.Test.Button.Command;
        cmd.Execute(null);
        await Task.Delay(50);
        Assert.Equal("正在连接…", add.Test.Text);
        Assert.True(cmd.CanExecute(null));
        gate.SetResult();
        for (int i = 0; i < 300 && add.Test.IsBusy; i++) await Task.Delay(10);
        Assert.False(add.Test.IsBusy);
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>
    /// 测的时候再点一次「测试连接」= 重新测：第一次被取消、照常结束（审查 testconn.fsx：以前取消异常经 AsyncRelayCommand 抛回 UI 线程，应用没有处理器，就崩了），
    /// 界面上还是第二次的「正在连接…」，结果出来时只写第二次的。
    /// </summary>
    [Fact]
    public async Task TestConnection_ClickAgainWhileTesting_RestartsWithoutThrowing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new ScriptedHttpHandler();
        h.On("GET", "/health", _ => ScriptedHttpHandler.Json(200, "{\"ok\":true}"));
        var f = LiveFleet.Create(http: () => new GateHandler(h, gate.Task));
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("add");
        var add = Assert.IsType<AddDeviceDialogVm>(s.Dialogs.Current);
        add.Host = "127.0.0.9";
        add.Token = "tok";
        var cmd = (IAsyncRelayCommand)add.Test.Button.Command;
        var first = cmd.ExecuteAsync(null);
        await Task.Delay(50);
        var second = cmd.ExecuteAsync(null);
        await Task.WhenAny(first, Task.Delay(5000));
        Assert.True(first.IsCompletedSuccessfully, "第一次测试应当照常结束（" + first.Status + "）");
        Assert.Equal("正在连接…", add.Test.Text);
        Assert.True(add.Test.IsBusy);
        gate.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(add.Test.IsBusy);
        Assert.StartsWith("连接失败", add.Test.Text);                                     // 只有 /health：读 /device 失败（第二次的结果）
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>测的时候关掉对话框：这一次被取消、照常结束（审查 testclose.fsx）。</summary>
    [Fact]
    public async Task TestConnection_CloseWhileTesting_EndsWithoutThrowing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new ScriptedHttpHandler();
        h.On("GET", "/health", _ => ScriptedHttpHandler.Json(200, "{\"ok\":true}"));
        var f = LiveFleet.Create(http: () => new GateHandler(h, gate.Task));
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.OpenDialog("add");
        var add = Assert.IsType<AddDeviceDialogVm>(s.Dialogs.Current);
        add.Host = "127.0.0.9";
        add.Token = "tok";
        var run = ((IAsyncRelayCommand)add.Test.Button.Command).ExecuteAsync(null);
        await Task.Delay(50);
        Assert.Equal("正在连接…", add.Test.Text);
        s.Dialogs.Close();
        await Task.WhenAny(run, Task.Delay(5000));
        Assert.True(run.IsCompletedSuccessfully, "关掉对话框后测试应当照常结束（" + run.Status + "）");
        s.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>请求先等一个任务再交给内层（测试连接的「正在连接…」期间）。</summary>
    private sealed class GateHandler : DelegatingHandler
    {
        private readonly Task _gate;
        public GateHandler(HttpMessageHandler inner, Task gate) : base(inner) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            return await base.SendAsync(request, ct);
        }
    }

    // ———————————————————— 提示的时长 ————————————————————

    [Fact]
    public void ToastDuration_LongTextsAndErrorsStayLonger()
    {
        Assert.Equal(ToastService.LongFor, ToastService.DurationOf("1号机 · RV1 停止 · " + Hw, ToastKind.Warn));
        Assert.Equal(ToastService.LongFor, ToastService.DurationOf("连接失败", ToastKind.Err));
        Assert.Equal(ToastService.ShortFor, ToastService.DurationOf("布局已重置", ToastKind.Info));
        Assert.Equal(ToastService.ShortFor, ToastService.DurationOf(new string('字', ToastService.LongTextChars), ToastKind.Ok));
        Assert.Equal(ToastService.LongFor, ToastService.DurationOf(new string('字', ToastService.LongTextChars + 1), ToastKind.Ok));
        // 连接符不算字数（Keep 过的文字）
        Assert.Equal(ToastService.ShortFor, ToastService.DurationOf(string.Join("⁠", Enumerable.Repeat("字", ToastService.LongTextChars)), ToastKind.Ok));
    }
}
