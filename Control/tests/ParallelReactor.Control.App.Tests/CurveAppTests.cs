using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 曲线升温在界面上（设计稿 curve-design 方向 B）：检查器的摘要 / 运行中、曲线窗口（改段、提示、同时用到几路、保存并启动）、停止曲线先确认；
/// 真机按 升温方式 → 整条曲线 → 启动 的顺序发三个请求，快照里的 profile 投影成曲线现状。
/// </summary>
public class CurveAppTests
{
    private static ShellVm Shell(string query)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1&" + query);
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("page=main&dev=1&" + p));
        return s;
    }

    [Fact]
    public void Inspector_Idle_Summary_AndModeSwitch()
    {
        var s = Shell("apop=0&ch=5&cv=5&cvfree=2,6,8");
        var c = s.Ctl;
        Assert.Equal("RV5", c.Title);
        Assert.Equal(("curve", true, false, true, false), (c.ModeKey, c.IsCurve, c.IsFixed, c.CurveIdle, c.CurveRunning));
        Assert.Equal(("共 4 段 · 2 h 25 min", " · 最高 150 °C"), (c.CurveHead, c.CurveMax));
        Assert.StartsWith("从当前 ", c.CurveSub);
        Assert.Contains("预计 16:55 走完，之后保持 150 °C", c.CurveSub);          // 14:30 + 2 h 25 min
        Assert.Equal(new[] { "升到 80 °C", "保温 80 °C", "升到 150 °C", "保温 150 °C" }, c.CurveSteps.Select(x => x.Text));
        Assert.Equal(new[] { "20 min", "30 min", "35 min", "60 min" }, c.CurveSteps.Select(x => x.Min));
        Assert.Null(c.CurveStartReason);
        Assert.Equal(new[] { "定值", "曲线" }, c.ModeOptions.Select(o => o.Label));

        // 切回定值：发 HeatMode，下一帧显示定值的那一套
        c.ModeKey = "fixed";
        s.Fleet.RefreshNow();
        s.RefreshChrome();
        Assert.Equal(("fixed", true, false), (c.ModeKey, c.IsFixed, c.IsCurve));
        Assert.False(s.Fleet.DemoData!.Device(1)!.Channel(5)!.CurveMode);

        // 程序在跑的通道：不能切到曲线，写明原因
        s.Select(1, 1);
        s.RefreshChrome();
        Assert.Equal("fixed", c.ModeKey);
        Assert.Equal("RV1 程序运行中", c.ModeOptions[1].Reason);
    }

    [Fact]
    public async Task Dialog_Edit_Chips_SaveAndStart()
    {
        var s = Shell("apop=0&ch=5&cv=5&cvfree=2,6,8");
        s.Ctl.CurveEditCommand.Execute(null);
        var d = Assert.IsType<CurveDialogVm>(s.Dialogs.Current);
        Assert.Equal(("curve", "RV5 曲线升温", 980.0, true, false), (d.Name, d.Title, d.Width, d.IsEdit, d.IsRunning));
        Assert.StartsWith("1号机 · 当前 ", d.Sub);
        Assert.EndsWith(" °C · 上限 200 °C", d.Sub);
        Assert.Equal(new[] { "升 2.7 °C/min", "保温", "升 2.0 °C/min", "保温" }, d.Rows.Select(r => r.RateText));
        Assert.Equal("共 4 段 · 2 h 25 min · 最高 150 °C · 预计 16:55 走完", d.SumText);
        Assert.Equal("走完后保持 150 °C，直到停止加热", d.AfterText);
        Assert.False(d.HasWarnings);
        Assert.Equal((4, 145.0, false), (d.Plot!.Rows.Count, d.Plot.TotalMin, d.Plot.Running));
        var bar = Assert.IsType<FootBar>(d.Footer);
        Assert.Equal(new[] { "取消", "保存", "保存并启动" }, bar.Buttons.Select(b => b.Label));
        Assert.All(bar.Buttons, b => Assert.Null(b.Reason));
        Assert.Equal("只用到本路", d.FooterLeft);

        // 同时用到：本路固定选中；程序在跑 / 停用的不能选并写原因；空闲的显示当前温度
        Assert.Equal(8, d.Chips.Count);
        var self = d.Chips.Single(c => c.IsSelf);
        Assert.Equal((5, true, "本路"), (self.N, self.IsOn, self.Sub));
        Assert.Equal("程序运行中", d.Chips.Single(c => c.N == 1).Sub);
        Assert.True(d.Chips.Single(c => c.N == 1).IsOff);
        Assert.Equal("已停用", d.Chips.Single(c => c.N == 7).Sub);
        foreach (int n in new[] { 2, 6, 8 })
        {
            var chip = d.Chips.Single(c => c.N == n);
            Assert.Null(chip.Reason);
            Assert.EndsWith(" °C", chip.Sub);
        }
        d.Chips.Single(c => c.N == 1).ToggleCommand.Execute(null);        // 不能选的点了没用
        Assert.False(d.Chips.Single(c => c.N == 1).IsOn);
        d.Chips.Single(c => c.N == 6).ToggleCommand.Execute(null);
        d.Chips.Single(c => c.N == 8).ToggleCommand.Execute(null);
        Assert.Equal(("本路 + RV6、RV8", "保存并启动（3 路）"), (d.SelText, bar.Buttons[2].Label));
        d.SelectAllCommand.Execute(null);
        Assert.Equal("保存并启动（4 路）", bar.Buttons[2].Label);
        d.Chips.Single(c => c.N == 2).ToggleCommand.Execute(null);
        Assert.Equal("保存并启动（3 路）", bar.Buttons[2].Label);

        // 改段：升得太快 / 降温段给提示（不拦）；没填、超范围拦在按钮上
        d.Rows[2].Minutes = 10;
        Assert.Equal(("升 7.0 °C/min", true), (d.Rows[2].RateText, d.Rows[2].HasWarn));
        Assert.Equal(new[] { "升得太快，实际可能跟不上" }, d.Warnings);
        Assert.Null(bar.Buttons[2].Reason);
        d.Rows[2].Minutes = null;
        Assert.Equal("第 3 段：请输入用时", bar.Buttons[1].Reason);
        Assert.Equal("第 3 段：请输入用时", bar.Buttons[2].Reason);
        d.Rows[2].Minutes = 35;
        Assert.Null(bar.Buttons[2].Reason);
        Assert.False(d.HasWarnings);
        // 加一段（默认 = 上一段的温度、30 min → 保温）、删一段；最多 16 段、至少 1 段
        d.AddCommand.Execute(null);
        Assert.Equal((5, 150.0, 30.0, "保温"), (d.Rows.Count, d.Rows[4].Temp!.Value, d.Rows[4].Minutes!.Value, d.Rows[4].RateText));
        d.Rows[4].RemoveCommand.Execute(null);
        Assert.Equal(4, d.Rows.Count);
        for (int i = 0; i < 20; i++) d.AddCommand.Execute(null);
        Assert.Equal((16, "已到 16 段"), (d.Rows.Count, d.AddReason));
        while (d.Rows.Count > 4) d.Rows[^1].RemoveCommand.Execute(null);

        // 保存并启动：三路都切到曲线、放上同一条曲线、开始走；窗口关闭，一条提示
        await ((IAsyncRelayCommand)bar.Buttons[2].Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        var dev = s.Fleet.DemoData!.Device(1)!;
        foreach (int n in new[] { 5, 6, 8 })
        {
            var ch = dev.Channel(n)!;
            Assert.True(ch.CurveMode && ch.CurveRunning && ch.Heating, "RV" + n);
            Assert.Equal(new[] { 80.0, 80, 150, 150 }, ch.CurveSegs.Select(x => x.TempC));
        }
        Assert.False(dev.Channel(2)!.CurveRunning);
        Assert.Contains(s.Toasts.Items, t => t.Text == "1号机 · RV5、RV6、RV8 曲线升温已启动 · 共 4 段");
        s.Fleet.RefreshNow();
        s.RefreshChrome();
        Assert.True(s.Ctl.CurveRunning);
        Assert.Equal("第 1/4 段", s.Ctl.CurveRun!.Title);
    }

    [Fact]
    public async Task Dialog_PreselectFromArg_AndSaveOnly()
    {
        var s = Shell("apop=0&ch=5&cv=5:warn&cvfree=2,6,8&dialog=curve:1:5:6,8");
        var d = Assert.IsType<CurveDialogVm>(s.Dialogs.Current);
        Assert.Equal("本路 + RV6、RV8", d.FooterLeft);
        Assert.Equal(new[] { "升得太快，实际可能跟不上", "只能自然冷却，可能降不到" }, d.Warnings);
        Assert.True(d.HasWarnings);
        Assert.Equal("降 3.0 °C/min", d.Rows[4].RateText);
        var bar = (FootBar)d.Footer!;
        await ((IAsyncRelayCommand)bar.Buttons[1].Command).ExecuteAsync(null);      // 保存：不启动
        var dev = s.Fleet.DemoData!.Device(1)!;
        Assert.All(new[] { 5, 6, 8 }, n => Assert.True(dev.Channel(n)!.CurveMode && !dev.Channel(n)!.CurveRunning && dev.Channel(n)!.CurveSegs.Count == 5));
        Assert.Contains(s.Toasts.Items, t => t.Text == "1号机 · RV5、RV6、RV8 曲线已保存 · 共 5 段");
    }

    [Fact]
    public async Task Running_Inspector_Dialog_StopConfirms()
    {
        var s = Shell("apop=0&ch=5&cv=5:run");
        var c = s.Ctl;
        Assert.Equal((true, false, "curve"), (c.CurveRunning, c.CurveIdle, c.ModeKey));
        var run = c.CurveRun!;
        Assert.Equal(("第 3/4 段", "升到 150 °C", "本段剩 12 min", 3), (run.Title, run.Step, run.Left, run.Seg));
        Assert.Equal("给定 126.0 °C · 实测 124.8 °C · 预计 15:42 走完，之后保持 150 °C", run.Sub);
        Assert.Equal(new[] { (true, false), (true, false), (false, true), (false, false) }, run.Bars.Select(b => (b.Done, b.Current)));
        Assert.Equal(23.0 / 35, run.Bars[2].F, 3);
        Assert.Equal("RV5 曲线运行中 · 先停止曲线", c.ModeOptions[0].Reason);          // 运行中不能切回定值

        // 查看曲线：只读，图上有「现在」
        c.CurveEditCommand.Execute(null);
        var d = Assert.IsType<CurveDialogVm>(s.Dialogs.Current);
        Assert.Equal((true, false), (d.IsRunning, d.IsEdit));
        Assert.Equal(new[] { (true, false), (true, false), (false, true), (false, false) }, d.Rows.Select(r => (r.IsDone, r.IsCur)));
        Assert.Equal((true, 73.0, 25.6), (d.Plot!.Running, Math.Round(d.Plot.ElapsedMin, 3), d.Plot.StartC));
        Assert.Equal("共 4 段 · 2 h 25 min · 最高 150 °C · 预计 15:42 走完", d.SumText);
        var bar = (FootBar)d.Footer!;
        Assert.Equal(new[] { "关闭", "停止曲线" }, bar.Buttons.Select(b => b.Label));
        Assert.Null(d.FooterLeft);

        // 停止曲线：先确认，确认后曲线停、加热不停
        bar.Buttons[1].Command.Execute(null);
        var stop = Assert.IsType<StopConfirmDialogVm>(s.Dialogs.Current);
        Assert.Equal(("curvestop", "停止 1号机 RV5 曲线升温", "停止曲线"), (stop.Name, stop.Title, stop.GoButton.Label));
        Assert.Contains(stop.Rows, r => r.Key == "加热" && r.Value[0].Text == "不停");
        Assert.DoesNotContain(stop.StopNote, t => t.Text.Contains("放空"));
        var ch = s.Fleet.DemoData!.Device(1)!.Channel(5)!;
        Assert.True(ch.CurveRunning);
        await ((IAsyncRelayCommand)stop.GoButton.Command).ExecuteAsync(null);
        Assert.False(s.Dialogs.IsOpen);
        Assert.False(ch.CurveRunning);
        Assert.True(ch.Heating);
        s.Fleet.RefreshNow();
        s.RefreshChrome();
        Assert.Equal((false, true), (c.CurveRunning, c.CurveIdle));
        // 检查器上的「停止曲线」同样先确认
        s.Fleet.DemoData.Act(s.Fleet.DemoData.Device(1)!, DeviceAction.CurveStart, ActionPayload.Ch(5), quiet: true);
        s.Fleet.RefreshNow();
        s.RefreshChrome();
        c.CurveStopCommand.Execute(null);
        Assert.IsType<StopConfirmDialogVm>(s.Dialogs.Current);
        Assert.True(ch.CurveRunning);
    }

    // ———————————————————— 真机 ————————————————————

    [Fact]
    public void Projection_FromSnapshot()
    {
        var profile = new ProfileSnap(true, 3, new[]
        {
            new ProfileSegmentSnap(0, 80, 20, true, true), new ProfileSegmentSnap(1, 150, 60, true, false),
            new ProfileSegmentSnap(2, 150, 30, true, false), new ProfileSegmentSnap(3, 50, 10, false, false),
        }, 10, 110, false, "第 1/3 段");
        var c = ChannelProjection.CurveOf(LiveFrames.Ch(1, temp: 50, sp: 52.8, state: "heating", atMode: 0, profile: profile));
        Assert.Equal((true, true, 3, 10.0, 110.0, false), (c.CurveMode, c.Running, c.Segs.Count, c.ElapsedMin, c.TotalMin, c.Complete));   // 没启用的段不算
        Assert.Equal(25.6, c.StartC!.Value, 6);                                                                                   // 第 1 段时由给定值反推起点
        var fixedMode = ChannelProjection.CurveOf(LiveFrames.Ch(2));
        Assert.Equal((false, false, 0), (fixedMode.CurveMode, fixedMode.Running, fixedMode.Segs.Count));                              // 定值方式：HMI 不带 profile
        var r = ChannelProjection.FromSnap(LiveFrames.Ch(1, profile: profile), 1, true, true);
        Assert.True(r.Curve!.Running);
    }

    [Fact]
    public async Task Live_Apply_SendsModeProfileStart_InOrder()
    {
        var (f, d, fake) = await LiveFleet.OneAsync(h =>
        {
            FakeReactorConnection.ScriptGrantedLease(h);
            h.On("PUT", @"/channels/\d+/temperature/mode", c => ScriptedHttpHandler.Command(c, false, new { mode = "curve" }));
            h.On("PUT", @"/channels/\d+/profile", c => ScriptedHttpHandler.Command(c, false, new { segCount = 2 }));
            h.On("POST", @"/channels/\d+/profile/start", c => ScriptedHttpHandler.Command(c, false, null));
            h.On("POST", @"/channels/\d+/profile/stop", c => ScriptedHttpHandler.Command(c, false, null));
        });
        var shell = new ShellVm(f);
        shell.Toasts.Frozen = true;
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        for (int i = 0; i < 200 && fake.Lease.State != ParallelReactor.Client.LeaseState.Holding; i++) await Task.Delay(10);
        fake.RaiseTelemetry(LiveFrames.Frame(DateTime.UtcNow, true, 400, LiveFrames.Ch(1), LiveFrames.Ch(2, state: "ready"), LiveFrames.Ch(3), LiveFrames.Ch(4)));
        f.RefreshNow();

        var segs = new[] { new CurveSeg(80, 20), new CurveSeg(150.5, 30) };
        int n2 = await CurveOps.ApplyAsync(shell, d, new[] { 2 }, segs, start: true);
        Assert.True(n2 == 1, string.Join(" | ", shell.Toasts.Items.Select(t => t.Text)));
        var calls = fake.Http.Calls.Where(c => c.Path.Contains("/channels/2/")).Select(c => c.Method + " " + c.Path[c.Path.IndexOf("/channels", StringComparison.Ordinal)..]).ToArray();
        Assert.Equal(new[] { "PUT /channels/2/temperature/mode", "PUT /channels/2/profile", "POST /channels/2/profile/start" }, calls);
        Assert.Equal("curve", fake.Http.Of("PUT", "/channels/2/temperature/mode").Single().Json.GetProperty("spMode").GetString());
        var body = fake.Http.Of("PUT", "/channels/2/profile").Single();
        Assert.Equal("L-test", body.Lease);
        var arr = body.Json.GetProperty("segments");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal((150.5, 30.0), (arr[1].GetProperty("tempC").GetDouble(), arr[1].GetProperty("minutes").GetDouble()));
        Assert.Contains(shell.Toasts.Items, t => t.Text == "1号机 · RV2 曲线升温已启动 · 共 2 段");
        // 本机保存过的曲线：定值方式下 HMI 不上报，窗口再打开时还是这一条
        Assert.Equal(segs, CurveOps.SegsFor(d, d.Reactor(2)!));

        // 已经是曲线方式的通道：不再发升温方式
        var profile = new ProfileSnap(false, 1, new[] { new ProfileSegmentSnap(0, 60, 5, true, false) }, 0, 5, false, "未启动");
        fake.RaiseTelemetry(LiveFrames.Frame(DateTime.UtcNow, true, 400, LiveFrames.Ch(1), LiveFrames.Ch(2, state: "ready"), LiveFrames.Ch(3, state: "ready", profile: profile), LiveFrames.Ch(4)));
        f.RefreshNow();
        Assert.Equal(new[] { new CurveSeg(60, 5) }, CurveOps.SegsFor(d, d.Reactor(3)!));
        int n3 = await CurveOps.ApplyAsync(shell, d, new[] { 3 }, segs, start: false);
        Assert.True(n3 == 1, string.Join(" | ", shell.Toasts.Items.Select(t => t.Text)));
        Assert.Equal(0, fake.Http.Count("PUT", "/channels/3/temperature/mode"));
        Assert.Equal(1, fake.Http.Count("PUT", "/channels/3/profile"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/3/profile/start"));

        // 停止曲线：停止类，不带租约也能发
        var stop = await d.Actions.RunAsync(DeviceAction.CurveStop, ActionPayload.Ch(3));
        Assert.True(stop.Ok, stop.Message);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/3/profile/stop"));
        await f.DisposeAsync();
    }
}
