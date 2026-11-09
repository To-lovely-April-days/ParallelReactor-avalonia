using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 「全部停止」停不停搅拌的接线：Core 的 ControlOptions.StopAllStopsStirring → FleetVm.StopAllStopsStirring → 实时数据源、演示数据、全部停止对话框。
/// 两种取值都走一遍：实时设备发的 POST /stop-all 正文里 stopStir 与设置一致；单台的结果、对话框的副标题 / 说明 / 发送后的提示都跟着设置。
/// 测试不假定默认值是哪一个（改默认只改 ControlOptions 那一行）。文字本身在 Control.Tests 的 StopAllOptionTests 里对照样稿。
/// </summary>
public class StopAllStirringTests
{
    /// <summary>一台假 HMI（实测样本：8 路都没在运行），/stop-all 成功；连上之前先把设置换成 <paramref name="stopsStirring"/>。</summary>
    private static async Task<(FleetVm Fleet, DeviceVm Device, FakeReactorConnection Fake)> Live(bool stopsStirring)
    {
        // 设置在添加设备之前换（FleetVm 新建数据源时把自己的值交给它）
        return await LiveFleet.OneAsync(h => h.On("POST", "/stop-all", c => ScriptedHttpHandler.Command(c, true, null)),
            before: f => f.StopAllStopsStirring = stopsStirring);
    }

    [Fact]
    public async Task Defaults_ComeFromControlOptions()
    {
        Assert.Equal(ControlOptions.StopAllStopsStirring, FleetVm.CreateLive(ControlConfig.InMemory()).StopAllStopsStirring);
        await using var src = new LiveDeviceSource(new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080), "PC-测试", a => a());
        Assert.Equal(ControlOptions.StopAllStopsStirring, src.StopAllStopsStirring);
        var demo = FleetVm.CreateDemo(DemoParams.Parse("freeze=1"));
        Assert.Equal(ControlOptions.StopAllStopsStirring, demo.StopAllStopsStirring);
        Assert.Equal(ControlOptions.StopAllStopsStirring, demo.DemoData!.StopAllStopsStirring);
        await demo.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_StopAll_SendsStopStirAndTextsFollowTheSetting(bool stopsStirring)
    {
        var (f, d, fake) = await Live(stopsStirring);
        Assert.Equal(stopsStirring, ((LiveDeviceSource)d.Source).StopAllStopsStirring);   // FleetVm 把自己的值交给新连的设备

        // 单台的「全部停止」（检查器 / 设备菜单 / 时间轴组头）：样本里没有运行中的反应釜 → info 语气，照样发送
        var o = await d.Actions.RunAsync(DeviceAction.StopAll);
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · " + StopAllText.NothingRunning(stopsStirring), o.Message);
        Assert.Equal(stopsStirring, fake.Http.Of("POST", "/stop-all").Single().Json.GetProperty("stopStir").GetBoolean());

        // 全部停止对话框（全局停止格 / 菜单「全部停止…」）
        var shell = new ShellVm(f);
        shell.Toasts.Frozen = true;
        shell.OpenDialog("stopall");
        var dlg = Assert.IsType<StopAllDialogVm>(shell.Dialogs.Current);
        Assert.Equal(stopsStirring, dlg.StopsStirring);
        Assert.Equal(StopAllText.Sub(stopsStirring), dlg.Sub);
        var note = string.Concat(dlg.Note.Select(s => s.Text));
        Assert.Equal(StopAllText.Note(stopsStirring), note);                          // 粗体段拼起来就是 StopAllText.Note
        Assert.Equal(!stopsStirring, note.Contains("搅拌保持当前状态", StringComparison.Ordinal));
        Assert.Equal("不需要控制权", Assert.Single(dlg.Note, s => s.Bold).Text);
        await ((IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.False(shell.Dialogs.IsOpen);
        var calls = fake.Http.Of("POST", "/stop-all").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(stopsStirring, calls[1].Json.GetProperty("stopStir").GetBoolean());
        Assert.Contains(shell.Toasts.Items, t => t.Text == StopAllText.Sent(new[] { "1号机" }, stopsStirring));
        shell.Dispose();
        await f.DisposeAsync();
    }

    /// <summary>演示数据：FleetVm 的设置交给 DemoFleet（建好之后再换也跟着换）；1号机的共用搅拌照转 / 停下。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Demo_StopAll_FollowsTheFleetSetting(bool stopsStirring)
    {
        var f = FleetVm.CreateDemo(DemoParams.Parse("freeze=1"));
        f.StopAllStopsStirring = stopsStirring;
        Assert.Equal(stopsStirring, f.DemoData!.StopAllStopsStirring);
        Assert.True(f.DemoData.Device(1)!.Stir!.On);
        var o = await f.Device(1)!.Actions.RunAsync(DeviceAction.StopAll);
        Assert.True(o.Ok, o.Message);
        Assert.StartsWith("1号机 · 已停止 ", o.Message, StringComparison.Ordinal);
        Assert.Equal(stopsStirring, o.Message.EndsWith(StopAllText.StirSuffix(true), StringComparison.Ordinal));
        Assert.Equal(!stopsStirring, f.DemoData.Device(1)!.Stir!.On);
        await f.DisposeAsync();
    }
}
