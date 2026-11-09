using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 样稿的对话框（Shell/Dialogs，PORT-PLAN §5 M3；样稿 js/dialogs.js）：文字、预选、说明跟着勾选状态、计数与按钮的原因、每秒刷新、执行的结果与提示。
/// 演示数据（冻结，同样稿的地址参数）+ 实时设备（假连接；「测试连接」走脚本化的 HTTP：/health 不带令牌、/device 带令牌）。
/// </summary>
public class DialogTests
{
    private static ShellVm Shell(string query = "freeze=1")
    {
        var f = FleetVm.CreateDemo(DemoParams.Parse(query));
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        return s;
    }

    private static T Open<T>(ShellVm s, string name, string? arg = null) where T : DialogVm
    {
        s.OpenDialog(name, arg);
        return Assert.IsType<T>(s.Dialogs.Current);
    }

    private static string Text(IReadOnlyList<Controls.Pr.TextSeg> segs) => string.Concat(segs.Select(x => x.Text));

    private static string? LastToast(ShellVm s) => s.Toasts.Items.LastOrDefault()?.Text;

    // ———————————————————— 急停 ————————————————————

    [Fact]
    public async Task Estop_AllConnectedPreselected_GoSendsToEachAndToasts()
    {
        var s = Shell();
        var d = Open<EstopDialogVm>(s, "estop");
        Assert.Equal(("急停", "立即切断所选设备的加热、进气与搅拌，运行中的程序全部中止", "estop", true, 640d), (d.Title, d.Sub, d.Icon, d.IsDanger, d.Width));
        Assert.Equal("选择设备（已连接的默认全选）", d.Caption);
        Assert.Equal(new[] { "1号机", "2号机", "3号机" }, d.Rows.Select(r => r.Alias));
        Assert.All(d.Rows, r => Assert.True(r.IsOn));
        Assert.Equal(("HT-PR8 · 现场 · 192.168.1.20", "运行 4/4 · 提醒 1", "远程控制"), (d.Rows[0].Sub, d.Rows[0].RunText, d.Rows[0].Badge.LeaseText));
        Assert.Equal("远程控制中", d.Rows[0].Badge.LeaseTip);
        Assert.Equal("运行 11/16 · 报警 1", d.Rows[2].RunText);
        Assert.Equal(("已选 3 台", "急停所选 3 台", FootKind.Danger, (string?)null), ((string)d.FooterLeft!, d.GoButton.Label, d.GoButton.Kind, d.GoButton.Reason));
        Assert.Equal("急停不需要控制权，连接正常就会发送。急停复位只能在设备触屏上操作，本机不能复位。", Text(d.Note));
        Assert.Equal(new[] { "不需要控制权", "复位只能在设备触屏上操作" }, d.Note.Where(x => x.Bold).Select(x => x.Text));
        // 全不选 → 原因；全选 → 回来
        d.NoneCommand.Execute(null);
        Assert.Equal(("已选 0 台", "急停所选 0 台", "至少选择一台已连接的设备"), ((string)d.FooterLeft!, d.GoButton.Label, d.GoButton.Reason));
        d.AllCommand.Execute(null);
        d.Rows[1].ToggleCommand.Execute(null);                                     // 去掉 2号机
        Assert.Equal("已选 2 台", d.FooterLeft);
        await ((IAsyncRelayCommand)d.GoButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("急停已发送到 2 台：1号机、3号机 · 复位需在触屏操作", LastToast(s));
        Assert.Equal(ToastKind.Err, s.Toasts.Items.Last().Kind);
        var demo = s.Fleet.DemoData!;
        Assert.True(demo.Device(1)!.Estop.Active);
        Assert.False(demo.Device(2)!.Estop.Active);
        Assert.True(demo.Device(3)!.Estop.Active);
        // 再打开：已在急停的不能选
        var again = Open<EstopDialogVm>(s, "estop");
        Assert.Equal(("已在急停 · 需在触屏复位", "已在急停 · 需在触屏复位"), (again.Rows[0].Reason, again.Rows[0].RunText));
        Assert.False(again.Rows[0].IsOn);
        again.Rows[0].ToggleCommand.Execute(null);                                 // 点了也勾不上
        Assert.False(again.Rows[0].IsOn);
        Assert.Equal("已选 1 台", again.FooterLeft);
        s.Dispose();
    }

    [Fact]
    public void EstopOne_CaptionFollowsTheRealTickState()
    {
        var s = Shell();
        var d = Open<EstopDialogVm>(s, "estop", "2");
        Assert.Equal(new[] { false, true, false }, d.Rows.Select(r => r.IsOn));
        Assert.Equal("已预选 2号机 · 可再勾选其它设备", d.Caption);
        d.Rows[1].ToggleCommand.Execute(null);
        Assert.Equal("2号机 未勾选 · 勾选后才会急停", d.Caption);
        Assert.Equal("至少选择一台已连接的设备", d.GoButton.Reason);
        d.AllCommand.Execute(null);
        Assert.Equal("已预选 2号机 · 可再勾选其它设备", d.Caption);
        Assert.Equal("急停所选 3 台", d.GoButton.Label);
        s.Dispose();

        // 只预选的那一台断线了 / 已在急停
        var off = Shell("devs=4&freeze=1");
        var e = Open<EstopDialogVm>(off, "estop", "4");
        Assert.Equal("4号机 连接中断 · 无法预选 · 可勾选其它设备", e.Caption);
        Assert.Equal(("连接中断 · 无法发送", "连接中断 · 无法发送", false), (e.Rows[3].Reason, e.Rows[3].RunText, e.Rows[3].IsOn));
        Assert.False(e.Rows[3].Badge.ShowLease);
        Assert.Equal("连接中断 · 重连中 12 s", e.Rows[3].Badge.ConnText);
        Assert.Equal("已选 0 台", e.FooterLeft);
        off.Dispose();
        var es = Shell("estop=1&freeze=1");
        Assert.Equal("1号机 已在急停 · 可勾选其它设备", Open<EstopDialogVm>(es, "estop", "1").Caption);
        es.Dispose();
    }

    [Fact]
    public void Estop_LiveEverySecond_DeviceGoesOffline()
    {
        var s = Shell();
        var d = Open<EstopDialogVm>(s, "estop", "1");
        Assert.Equal("已预选 1号机 · 可再勾选其它设备", d.Caption);
        s.ApplyDemoParams(DemoParams.Parse("offline=1&freeze=1"));                 // 开着对话框时 1号机断线（样稿每秒重画）
        Assert.Same(d, s.Dialogs.Current);
        Assert.Equal("1号机 连接中断 · 无法预选 · 可勾选其它设备", d.Caption);
        Assert.Equal("已选 0 台", d.FooterLeft);
        s.ApplyDemoParams(DemoParams.Parse("offline=none&freeze=1"));              // 重新连上：勾选还记着
        Assert.Equal("已预选 1号机 · 可再勾选其它设备", d.Caption);
        s.Dispose();
    }

    // ———————————————————— 全部停止 ————————————————————

    [Fact]
    public async Task StopAll_PrimaryWithStopSquare_CountsRunningReactors()
    {
        var s = Shell();
        var d = Open<StopAllDialogVm>(s, "stopall");
        Assert.Equal(("全部停止", StopAllText.Sub(ControlOptions.StopAllStopsStirring), "stop", false), (d.Title, d.Sub, d.Icon, d.IsDanger));
        Assert.Equal("选择设备（已连接的默认全选）", d.Caption);
        Assert.Equal("已选 3 台 · 共 18 个运行中的反应釜", d.FooterLeft);
        Assert.Equal(("停止所选 3 台", FootKind.PrimaryStop, "stop", true), (d.GoButton.Label, d.GoButton.Kind, d.GoButton.Icon, d.GoButton.Large));
        Assert.Equal(StopAllText.Note(ControlOptions.StopAllStopsStirring), Text(d.Note));
        d.Rows[0].ToggleCommand.Execute(null);
        Assert.Equal("已选 2 台 · 共 14 个运行中的反应釜", d.FooterLeft);
        await ((IAsyncRelayCommand)d.GoButton.Command).ExecuteAsync(null);
        Assert.Equal(StopAllText.Sent(new[] { "2号机", "3号机" }, ControlOptions.StopAllStopsStirring), LastToast(s));
        Assert.Equal(ToastKind.Warn, s.Toasts.Items.Last().Kind);
        var again = Open<StopAllDialogVm>(s, "stopall");
        Assert.Equal("无运行中 · 仍会发送", again.Rows[1].RunText);                 // 停下来了也照样能选、能发
        Assert.True(again.Rows[1].IsOn);
        s.Dispose();
    }

    // ———————————————————— 申请控制权 ————————————————————

    [Fact]
    public async Task Lease_StatesFollowTheDevice()
    {
        var s = Shell("");                                                         // 不冻结：测试自己推进时钟（OnTick）
        var d = Open<LeaseDialogVm>(s, "lease", "2");
        Assert.Equal(("申请控制权 · 2号机", "控制权按设备独立：每台一个，互不影响", "key", 600d, false), (d.Title, d.Sub, d.Icon, d.Width, d.IsDanger));
        Assert.True(d.IsLocal);
        Assert.Equal(("2号机", "HT-PR4 · 192.168.1.24 · SN PR4-2026-0003", "本地 · 只读"), (d.Alias, d.Ident, d.Badge.LeaseText));
        Assert.Equal("请求会显示在2号机触屏上，现场人员确认后，本机（PC-测试工位1）才能对这台设备下发启用、启动、设定温度、程序等操作。", Text(d.Explain));
        Assert.Equal("2号机触屏", Assert.Single(d.Explain, x => x.Bold).Text);
        Assert.Equal("只影响2号机，其它设备的控制权不变", d.Point1);
        var foot = ((FootBar)d.Footer!).Buttons;
        Assert.Equal(new[] { "取消", "发送申请" }, foot.Select(b => b.Label));
        Assert.Null(foot[1].Reason);
        await ((IAsyncRelayCommand)d.SendButton.Command).ExecuteAsync(null);     // 样稿：申请后对话框留着，换成倒计时
        Assert.Same(d, s.Dialogs.Current);
        Assert.True(d.IsRequested);
        Assert.Equal("等待2号机触屏确认", d.Sub);
        Assert.Equal((60d, "60"), (d.Remain, d.RemainText));
        Assert.Equal(new[] { "本机 PC-测试工位1 → 2号机", "请现场人员在2号机触屏上点「允许」", "确认后本机可下发启动、设定、程序等操作" }, d.Steps.Select(x => x.Text));
        Assert.Equal(new[] { "取消申请", "后台等待" }, ((FootBar)d.Footer!).Buttons.Select(b => b.Label));
        Assert.Equal(new FootNote("clock", "倒计时结束自动取消"), d.FooterLeft);
        Assert.Equal("2号机 · 已申请控制权 · 等待触屏确认", LastToast(s));
        // 每秒：倒计时走；5 s 后演示的触屏确认 → 远程控制中
        s.Fleet.OnTick(true);
        s.Fleet.RefreshNow();
        Assert.Equal("59", d.RemainText);
        for (int i = 0; i < 5; i++) { s.Fleet.OnTick(true); s.Fleet.RefreshNow(); }
        Assert.True(d.IsRemote);
        Assert.Equal(("2号机 远程控制中", "本机 PC-测试工位1 持有控制权 · 心跳正常"), (d.DoneTitle, d.DoneText));
        Assert.Equal(new[] { "释放控制权", "完成" }, ((FootBar)d.Footer!).Buttons.Select(b => b.Label));
        await ((IAsyncRelayCommand)d.ReleaseButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("2号机 · 控制权已释放 · 回到本地只读", LastToast(s));
        s.Dispose();
    }

    [Fact]
    public void Lease_LostAndOffline_AreRed()
    {
        var s = Shell("lease=1:lost&freeze=1");
        var d = Open<LeaseDialogVm>(s, "lease", "1");
        Assert.True(d.IsLost && d.IsDanger);
        Assert.Equal("控制权心跳中断，1号机在宽限期（剩 18 s）内保留本机的控制权；心跳恢复后自动继续，超时则交回触屏。", d.ErrNote);
        Assert.Equal(new[] { "释放控制权", "关闭" }, ((FootBar)d.Footer!).Buttons.Select(b => b.Label));
        s.Dispose();
        var o = Shell("devs=4&freeze=1");
        var e = Open<LeaseDialogVm>(o, "lease", "4");
        Assert.True(e.IsOffline && e.IsDanger);
        Assert.Equal("4号机连接中断 · 重连中 12 s。连接恢复后才能申请。", e.ErrNote);
        Assert.Equal(new[] { "关闭", "立即重连" }, ((FootBar)e.Footer!).Buttons.Select(b => b.Label));
        Assert.Equal("连接中断 · 重连中 12 s", e.Badge.ConnText);
        o.Dispose();
        Assert.Null(LeaseDialogVm.Create(Shell(), "9"));                            // 没有这台：不打开
    }

    // ———————————————————— 添加 / 编辑 / 设备管理 / 重命名 / 移除 / 断开 ————————————————————

    [Fact]
    public void Add_Demo_DefaultsValidationAndAdd()
    {
        var s = Shell();
        var d = Open<AddDeviceDialogVm>(s, "add");
        Assert.Equal(("添加设备", "同时连多台：每台一个接口地址、一个访问令牌、一个控制权", "plus", 660d), (d.Title, d.Sub, d.Icon, d.Width));
        Assert.Equal(("192.168.1.50", "8080", "ht-7f3c-pr8-b210", "4号机", true), (d.Host, d.Port, d.Token, d.Alias.Alias, d.AutoConnect));
        Assert.Equal(("5/16", AliasFieldVm.HintText), (d.Alias.Count, d.Alias.Hint));
        Assert.Equal(("型号（原型演示）", "HT-PR8 · 8 路", "真机连接后从设备 /device 读取"), (d.ModelLabel, d.Model!.Label, d.ModelHint));
        Assert.Equal("本机客户端名全局一个：PC-测试工位1（在 设置 中修改）。示例型号 · 参数为假设。", Text(d.Note!));
        Assert.Null(d.AddButton.Reason);
        d.Alias.Alias = "1号机";
        Assert.Equal("名称已被其他设备使用（192.168.1.20）", d.AddButton.Reason);
        Assert.True(d.Alias.HasError);
        d.Alias.Alias = "非常非常长的设备名称啊";
        Assert.Equal(("22/16", true, "名称太长 · 最多 8 个汉字或 16 个字母"), (d.Alias.Count, d.Alias.CountOver, d.Alias.Hint));
        d.Alias.Alias = "4号机";
        d.Host = " ";
        Assert.Equal("请填写地址", d.AddButton.Reason);
        d.Host = "192.168.1.50";
        d.Test.Button.Command.Execute(null);
        Assert.Equal(("连接成功 · 读取到 HT-PR8", true), (d.Test.Text, d.Test.IsOk));
        d.Model = d.Models.First(m => m.Key == "PR4");
        Assert.False(d.Test.HasText);                                              // 换了型号，结果作废
        d.AddButton.Command.Execute(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("已添加 4号机 · 正在连接 192.168.1.50:8080", LastToast(s));
        var dev = s.Fleet.Device(4)!;
        Assert.Equal(("4号机", "HT-PR4", false), (dev.Alias, dev.ModelLabel, dev.EverOnline));
        Assert.Equal(4, s.Selection.K);                                            // 选中新设备
        Assert.Equal("5号机", Open<AddDeviceDialogVm>(s, "add").Alias.Alias);
        s.Dispose();
    }

    [Fact]
    public async Task Edit_Demo_MovedNoteAndSave()
    {
        var s = Shell();
        var d = Open<EditDeviceDialogVm>(s, "edit", "1");
        Assert.Equal(("编辑 1号机", "HT-PR8 · 现场 · SN PR8-2026-0001", "edit"), (d.Title, d.Sub, d.Icon));
        Assert.Equal(("型号", "HT-PR8 · 现场 · 8 路", "连接后从设备 /device 读取，不能修改", false), (d.ModelLabel, d.ModelText, d.ModelHint, d.ModelSelectable));
        Assert.False(d.HasWarnNote);
        d.Host = "192.168.1.99";
        Assert.Equal("地址或端口改变后会断开并按新地址重新连接；本机的控制权会先释放。", d.WarnNote);
        d.Host = "192.168.1.20";
        Assert.False(d.HasWarnNote);
        d.Alias.Alias = "主反应台";
        await ((IAsyncRelayCommand)d.SaveButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("已保存 主反应台 的设置", LastToast(s));
        Assert.Equal("主反应台", s.Fleet.Device(1)!.Alias);
        s.Dispose();
    }

    [Fact]
    public void Devices_Demo_TableAndAutoConnect()
    {
        var s = Shell("devs=4&freeze=1");
        var d = Open<DevicesDialogVm>(s, "devices");
        Assert.Equal(("设备管理", "已添加 4 台 · 已连接 3 台", "list", 1060d), (d.Title, d.Sub, d.Icon, d.Width));
        Assert.Equal(new[] { "1号机", "2号机", "3号机", "4号机" }, d.Rows.Select(r => r.Alias));
        var r1 = d.Rows[0];
        Assert.Equal(("HT-PR8", " · 现场", "PR8-2026-0001", "192.168.1.20:8080", "已连接", "远程控制", "断开", "unplug"),
            (r1.ModelName, r1.ModelVariant, r1.Sn, r1.Address, r1.Badge.ConnText, r1.Badge.LeaseText, r1.ConnLabel, r1.ConnIcon));
        var r4 = d.Rows[3];
        Assert.Equal(("连接中断", "连接中断 · 重连中 12 s", false, "连接", "link"), (r4.Badge.ConnText, r4.Badge.ConnTip, r4.Online, r4.ConnLabel, r4.ConnIcon));
        Assert.Null(r4.ConnReason);
        Assert.True(r1.AutoConnect);
        Assert.Null(r1.AutoReason);                                                 // 演示：同样稿，直接切换
        r1.AutoConnect = false;
        Assert.Equal("1号机 启动时不自动连接", LastToast(s));
        Assert.False(s.Fleet.DemoData!.Device(1)!.AutoConnect);
        Assert.Equal("添加设备…", d.AddButton.Label);
        d.AddButton.Command.Execute(null);
        Assert.IsType<AddDeviceDialogVm>(s.Dialogs.Current);                         // 样稿：新对话框替换设备管理
        s.Dispose();
        var st = Shell("stress=2&freeze=1");                                         // 16 个字母的长别名：对话框加宽
        Assert.Equal(1120d, Open<DevicesDialogVm>(st, "devices").Width);
        st.Dispose();
    }

    [Fact]
    public async Task RenameRemoveDisconnect_Demo()
    {
        var s = Shell();
        var rn = Open<RenameDialogVm>(s, "rename", "2");
        Assert.Equal(("重命名 2号机", "HT-PR4 · 192.168.1.24", 480d), (rn.Title, rn.Sub, rn.Width));
        rn.Field.Alias = "3号机";
        Assert.Equal("名称已被其他设备使用（192.168.1.36）", rn.OkButton.Reason);
        rn.Field.Alias = "反应台B";
        await ((IAsyncRelayCommand)rn.OkButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("反应台B · 已重命名（原名 2号机）", LastToast(s));

        var rm = Open<RemoveDialogVm>(s, "remove", "1");
        Assert.Equal(("移除 1号机？", "HT-PR8 · 现场 · 192.168.1.20"), (rm.Title, rm.Sub));
        Assert.Equal("会断开连接并从本机列表中删除1号机，相关页签一起关闭。设备上的程序、数据和控制权设置不受影响，之后可重新添加。", rm.Text);
        Assert.Equal("本机正持有1号机的控制权，移除前会先释放。", rm.Warn);

        var dc = Open<DisconnectDialogVm>(s, "disconnect", "1");
        Assert.Equal(("断开 1号机？", "HT-PR8 · 现场 · 192.168.1.20:8080", "释放并断开"), (dc.Title, dc.Sub, dc.OkButton.Label));
        Assert.Equal("本机正持有1号机的控制权，断开前会先释放，设备回到触屏本地控制。", dc.Warn);
        await ((IAsyncRelayCommand)dc.OkButton.Command).ExecuteAsync(null);
        Assert.Equal("1号机 · 已断开连接 · 控制权已释放", LastToast(s));
        Assert.Equal(ConnStatus.Disconnected, s.Fleet.Device(1)!.ConnStatus);

        var rm3 = Open<RemoveDialogVm>(s, "remove", "3");
        Assert.False(rm3.HasWarn);
        await ((IAsyncRelayCommand)((FootBar)rm3.Footer!).Buttons[1].Command).ExecuteAsync(null);
        Assert.Null(s.Fleet.Device(3));
        Assert.Equal("已移除 3号机", LastToast(s));
        s.Dispose();
    }

    [Fact]
    public void DeviceRemovedWhileItsDialogIsOpen_Closes()
    {
        var s = Shell();
        Open<LeaseDialogVm>(s, "lease", "3");
        s.Fleet.DemoData!.Act(s.Fleet.DemoData.Device(3)!, DeviceAction.Remove, null, quiet: true);
        s.Fleet.RefreshNow();
        Assert.False(s.Dialogs.IsOpen);                                             // 样稿 onDevices
        s.Dispose();
    }

    // ———————————————————— 真机：添加 / 测试连接 / 自动连接 ————————————————————

    /// <summary>「测试连接」的假 HMI：/health 免鉴权；/device 只认 Bearer good。</summary>
    private static ScriptedHttpHandler TestHmi()
    {
        var h = new ScriptedHttpHandler();
        h.On("GET", "/health", c => ScriptedHttpHandler.Ok(new HealthDto(true, "1.0", true, "boot-1", DateTime.UtcNow)));
        h.On("GET", "/device", c => c.Header("Authorization") == "Bearer good"
            ? ScriptedHttpHandler.Json(200, Samples.Read("live-device.json"))
            : ScriptedHttpHandler.Error(OpCodes.Unauthorized, "缺少或无效的 Bearer 令牌"));
        return h;
    }

    [Fact]
    public async Task Add_Live_TestConnectionUsesHealthWithoutTokenThenDevice()
    {
        var h = TestHmi();
        var f = LiveFleet.Create(http: () => h);
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var d = Open<AddDeviceDialogVm>(s, "add");
        Assert.Equal(("", "8080", "", "1号机", false, false), (d.Host, d.Port, d.Token, d.Alias.Alias, d.RememberToken, d.AutoConnect));
        Assert.Equal(("型号", "—", false), (d.ModelLabel, d.ModelText, d.ModelSelectable));
        Assert.Equal("请填写地址", d.AddButton.Reason);
        d.Host = "10.0.0.5";
        Assert.Equal("请输入访问令牌", d.AddButton.Reason);
        d.Port = "abc";
        d.Token = "bad";
        Assert.Equal("端口无效", d.AddButton.Reason);
        d.Port = "8080";
        Assert.Null(d.AddButton.Reason);
        Assert.Equal(DeviceRegistry.AutoConnectNeedsToken, d.AutoReason);           // D7：没勾「记住令牌」不能自动连接
        await ((IAsyncRelayCommand)d.Test.Button.Command).ExecuteAsync(null);
        Assert.Equal(("连接失败：401 UNAUTHORIZED：缺少或无效的 Bearer 令牌", true), (d.Test.Text, d.Test.IsErr));
        d.Token = "good";
        await ((IAsyncRelayCommand)d.Test.Button.Command).ExecuteAsync(null);
        Assert.Equal(("连接成功 · 读取到 HT-PR8", true), (d.Test.Text, d.Test.IsOk));
        Assert.Equal("HT-PR8 · 8 路", d.ModelText);
        var health = h.Of("GET", "/health").ToList();
        Assert.All(health, c => Assert.Null(c.Header("Authorization")));          // /health 不带令牌
        Assert.Equal("Bearer good", h.Of("GET", "/device").Last().Header("Authorization"));
        d.RememberToken = true;
        Assert.Null(d.AutoReason);
        d.AutoConnect = true;
        d.AddButton.Command.Execute(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("已添加 1号机 · 正在连接 10.0.0.5:8080", LastToast(s));
        var e = f.Registry!.ByK(1)!.Entry;
        Assert.Equal(("1号机", "10.0.0.5", "good", true, true), (e.Alias, e.Host, e.Token, e.RememberToken, e.CanAutoConnect));
        await f.DisposeAsync();
        s.Dispose();
    }

    [Fact]
    public async Task Devices_Live_AutoConnectNeedsRememberedToken()
    {
        var f = LiveFleet.Create();
        f.AddDevice(new NewDevice("10.0.0.1", 8080, "tok", "1号机", AutoConnect: false, RememberToken: false));
        f.AddDevice(new NewDevice("10.0.0.2", 8080, "tok", "2号机", AutoConnect: true, RememberToken: true));
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var d = Open<DevicesDialogVm>(s, "devices");
        Assert.Equal(DeviceRegistry.AutoConnectNeedsToken, d.Rows[0].AutoReason);
        Assert.Null(d.Rows[1].AutoReason);
        Assert.True(d.Rows[1].AutoConnect);
        d.Rows[1].AutoConnect = false;
        Assert.Equal("2号机 启动时不自动连接", LastToast(s));
        Assert.False(f.Registry!.ByK(2)!.Entry.AutoConnect);
        var serial = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!.Serial;
        Assert.All(d.Rows, r => Assert.Equal(serial, r.Sn));                     // 连上之后从 /device 读到的序列号
        await f.DisposeAsync();
        s.Dispose();
    }

    // ———————————————————— M3 复核 ————————————————————

    /// <summary>
    /// 急停对话框发出即关（样稿 actMany → closeDialog → toast）：一台慢的设备不拖住对话框；最多等 SummaryWait 汇总已经成功的，慢的那台结果出来时再单独提示。
    /// </summary>
    [Fact]
    public async Task Estop_Live_ClosesAtOnce_SlowDeviceReportedLater()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x =>
        {
            x.Http.On("POST", "/estop", c => ScriptedHttpHandler.Command(c, true, null));
            if (made.Count == 1) x.Delays.Add(("POST", "/estop", TimeSpan.FromSeconds(1.2)));
        }, x => made.Add(x));
        var d1 = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", false, false))!;
        var d2 = f.AddDevice(new NewDevice("127.0.0.2", 8080, "tok", "2号机", false, false))!;
        await LiveFleet.WaitOnline(d1);
        await LiveFleet.WaitOnline(d2);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var toasts = new System.Collections.Concurrent.ConcurrentQueue<(string, ToastKind)>();
        s.Toasts.Items.CollectionChanged += (_, e) => { if (e.NewItems != null) foreach (ToastItem t in e.NewItems) toasts.Enqueue((t.Text, t.Kind)); };
        var dlg = Open<EstopDialogVm>(s, "estop");
        dlg.SummaryWait = TimeSpan.FromMilliseconds(250);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);                                            // 发出即关
        Assert.Equal(1, made[0].Http.Count("POST", "/estop"));                    // 快的那台已经发出
        await run;
        Assert.True(sw.ElapsedMilliseconds < 1000, "汇总不等慢的那台（" + sw.ElapsedMilliseconds + " ms）");
        Assert.Equal(new[] { ("急停已发送到 1 台：1号机 · 复位需在触屏操作", ToastKind.Err) }, toasts.ToArray());
        for (int i = 0; i < 300 && toasts.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(("急停已发送到 1 台：2号机 · 复位需在触屏操作", ToastKind.Err), toasts.ToArray()[1]);
        Assert.Equal(1, made[1].Http.Count("POST", "/estop"));
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task StopAll_Live_FailureReportedPerDevice_SuccessSummarised()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x =>
        {
            if (made.Count == 0) x.Http.On("POST", "/stop-all", c => ScriptedHttpHandler.Command(c, true, new { stopped = 0 }));
            else x.Http.On("POST", "/stop-all", _ => ScriptedHttpHandler.Json(409, "{\"ok\":false,\"error\":{\"code\":\"BUSY\",\"message\":\"忙\"}}"));
        }, x => made.Add(x));
        var d1 = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", false, false))!;
        var d2 = f.AddDevice(new NewDevice("127.0.0.2", 8080, "tok", "2号机", false, false))!;
        await LiveFleet.WaitOnline(d1);
        await LiveFleet.WaitOnline(d2);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var dlg = Open<StopAllDialogVm>(s, "stopall");
        await ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        var texts = s.Toasts.Items.Select(t => (t.Text, t.Kind)).ToList();
        Assert.Contains((StopAllText.Sent(new[] { "1号机" }, ControlOptions.StopAllStopsStirring), ToastKind.Warn), texts);
        Assert.Contains(texts, t => t.Text.StartsWith("2号机 · 409 BUSY", StringComparison.Ordinal) && t.Kind == ToastKind.Err);
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task ClientName_Live_MenuDialogValidateAndSave()
    {
        var f = LiveFleet.Create();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        Assert.Contains(MenuModel.Items(s, "device"), m => m.Label == ClientNameDialogVm.MenuLabel);
        var d = Open<ClientNameDialogVm>(s, "client");
        Assert.Equal(("本机客户端名", "PC-测试", "monitor", ClientNameDialogVm.HintText), (d.Title, d.Value, d.Icon!, d.Hint));
        d.Value = "  ";
        Assert.Equal(("客户端名不能为空", true), (d.SaveButton.Reason!, d.HasError));
        d.Value = new string('甲', 65);
        Assert.Equal("客户端名太长 · 最多 64 个字符", d.SaveButton.Reason);
        d.Value = " PC-工位9 ";
        Assert.Null(d.SaveButton.Reason);
        d.SaveButton.Command.Execute(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("本机客户端名已改为 PC-工位9", LastToast(s));
        Assert.Equal(("PC-工位9", "本机：PC-工位9", "PC-工位9"), (f.ClientName, s.ClientText, f.Config!.ClientName));
        // 添加设备的说明：真机写改名的入口（演示照样稿「在 设置 中修改」）
        var add = Open<AddDeviceDialogVm>(s, "add");
        Assert.Equal("本机客户端名全局一个：PC-工位9（在「设备 › 本机客户端名…」中修改）。示例型号 · 参数为假设。", Text(add.Note!));
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public void ClientName_Demo_NoMenuItemNoDialog()
    {
        var s = Shell();
        Assert.DoesNotContain(MenuModel.Items(s, "device"), m => m.Label == ClientNameDialogVm.MenuLabel);
        s.OpenDialog("client");
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("演示数据不能改本机客户端名", s.Fleet.SetClientName("PC-x"));
    }

    [Fact]
    public async Task Edit_Live_TokenChangeWarns_RememberNeedsAToken_SavesWithoutWaiting()
    {
        var slow = false;
        var gate = new TaskCompletionSource();
        var f = LiveFleet.Create(x => { if (slow) x.StartGate = gate.Task; });       // 改地址之后的新连接：第一次连接尝试一直挂着
        var dv = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", AutoConnect: false, RememberToken: false))!;
        await LiveFleet.WaitOnline(dv);
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var d = Open<EditDeviceDialogVm>(s, "edit", "1");
        Assert.False(d.HasWarnNote);
        d.Token = "tok-2";
        Assert.Equal("令牌改变后会断开并用新令牌重新连接。", d.WarnNote);   // 真机换令牌同样会断开重连（句式同样稿的地址说明）
        d.Token = "tok";
        Assert.False(d.HasWarnNote);
        // 勾「记住令牌」：本机有这台的令牌（这次输入过的）→ 可以存
        d.RememberToken = true;
        Assert.Null(d.SaveButton.Reason);
        // 第二台：没有令牌（编辑时令牌留空 = 不改），勾「记住令牌」→ 先要令牌（不然存下的是「记住了、却没有令牌」）
        var reg = f.Registry!;
        var e2 = reg.Add(new DeviceEntry { Alias = "2号机", Host = "127.0.0.2" });
        f.RefreshNow();
        var d2 = Open<EditDeviceDialogVm>(s, "edit", e2.K.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(d2.SaveButton.Reason);
        d2.RememberToken = true;
        Assert.Equal("请输入访问令牌", d2.SaveButton.Reason);
        d2.Token = "tok-b";
        Assert.Null(d2.SaveButton.Reason);
        // 保存：不等重新连接（释放 + 按新地址的第一次连接尝试在后台），对话框立即关
        slow = true;
        var dd = Open<EditDeviceDialogVm>(s, "edit", "1");
        dd.Host = "127.0.0.9";
        var save = ((IAsyncRelayCommand)dd.SaveButton.Command).ExecuteAsync(null);
        Assert.Same(save, await Task.WhenAny(save, Task.Delay(3000)));               // 新连接还挂着，保存已经完成
        Assert.False(s.Dialogs.IsOpen);
        Assert.Equal("已保存 1号机 的设置", LastToast(s));
        Assert.Equal("127.0.0.9", reg.ByK(1)!.Entry.Host);
        Assert.Equal(ConnStatus.Connecting, dv.Source.Conn.State);                // 正在按新地址连接
        gate.TrySetResult();
        await LiveFleet.WaitOnline(dv);
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Devices_Live_RememberedButNoToken_SaysTheTokenIsMissing()
    {
        var f = LiveFleet.Create();
        var rd = f.Registry!.Add(new DeviceEntry { Alias = "1号机", Host = "10.0.0.1", RememberToken = true, AutoConnect = true });   // 记住的令牌读不出来
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var d = Open<DevicesDialogVm>(s, "devices");
        Assert.False(d.Rows[0].AutoConnect);                                    // 启动时不会自动连接：不打勾
        Assert.Equal(DeviceRegistry.AutoConnectTokenMissing, d.Rows[0].AutoReason);
        Assert.Equal("令牌未保存 · 在「编辑设置」里输入", f.SetAutoConnect(rd.K, true));
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Lease_Live_RingMaximumIsTheHmiRequestTimeout()
    {
        var control = new ControlStateDto("local", null, null, "local", null, null, null, false, 10, 60, 90, "keep");
        var (f, dev, _) = await LiveFleet.OneAsync(control: control);
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        var d = Open<LeaseDialogVm>(s, "lease", "1");
        Assert.Equal(90d, d.RemainMax);
        Assert.Equal(DemoFleet.LeaseWaitS, Open<LeaseDialogVm>(Shell(), "lease", "2").RemainMax);   // 演示：样稿 60
        s.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task ConfigSaveFailure_AtRuntime_IsToasted()
    {
        using var t = new TestDir();
        var blocker = t.File("blocker");
        System.IO.File.WriteAllText(blocker, "x");                               // 配置目录是一个文件：建不了目录、写不进去
        var cfg = ControlConfig.Load(blocker, System.IO.Path.Combine(blocker, ControlConfig.FileName), true, TokenStoreMode.File);
        var f = LiveFleet.Create(config: cfg);
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        f.AddDevice(new NewDevice("10.0.0.1", 8080, "tok", "1号机", false, false));
        Assert.Contains(s.Toasts.Items, x => x.Text.StartsWith("配置保存失败：", StringComparison.Ordinal) && x.Kind == ToastKind.Warn);
        s.Dispose();
        await f.DisposeAsync();
    }
}
