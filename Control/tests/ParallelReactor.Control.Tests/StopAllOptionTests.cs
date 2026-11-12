using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 「全部停止」停不停搅拌（Core 的 ControlOptions.StopAllStopsStirring，用户已确认默认连搅拌一起停）：两种取值下的文字与演示数据的效果。
/// 除了 Default_IsStopStirring_AsTheUserConfirmed 钉住用户的决定，其余测试不假定默认值是哪一个（改默认只改 ControlOptions 那一行，再改这一个测试），只核对三件事：默认值取自那一行；两种取值各自的效果；
/// 连搅拌一起停时文字逐字同样稿（样稿 2026-11-09 已同步），不停搅拌时是样稿第一版的文字。实时设备与对话框的接线在 ParallelReactor.Control.App.Tests 的 StopAllStirringTests。
/// </summary>
public class StopAllOptionTests
{
    // 样稿原文：js/dialogs.js 的 stopall 对话框（副标题、说明，粗体去掉）—— 连搅拌一起停（用户 2026-11-09 确认，样稿已同步）
    private const string MockSub = "停止所选设备上全部反应釜：中止程序、停止加热、关闭进气、停止搅拌";
    private const string MockNote = "全部停止不需要控制权，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。搅拌也会一并停止；需要立即切断并锁定请用急停。";
    // 样稿第一版（搅拌保持当前状态）：开关为 false 时的文字
    private const string FirstSub = "停止所选设备上全部反应釜：中止程序、停止加热、关闭进气";
    private const string FirstNote = "全部停止不需要控制权，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。搅拌保持当前状态；需要彻底切断请用急停。";

    [Fact]
    public void Texts_StopStirring_AreTheMockWording()
    {
        Assert.Equal(MockSub, StopAllText.Sub(true));
        Assert.Equal(MockNote, StopAllText.Note(true));
        Assert.Equal("搅拌也会一并停止；需要立即切断并锁定请用急停。", StopAllText.StirNote(true));
        Assert.DoesNotContain("搅拌保持当前状态", StopAllText.Note(true));
        Assert.Equal(" · 搅拌一并停止", StopAllText.StirSuffix(true));
        Assert.Equal("全部停止已发送到 2 台：1号机、2号机 · 搅拌一并停止", StopAllText.Sent(new[] { "1号机", "2号机" }, true));   // dialogs.js stopall 的 toast
        Assert.Equal("已停止 3 个反应釜 · 搅拌一并停止", StopAllText.Stopped(3, true));                                          // sim.js RUN.stopAll
        Assert.Equal("没有运行中的反应釜 · 已发送全部停止 · 搅拌一并停止", StopAllText.NothingRunning(true));
    }

    [Fact]
    public void Texts_KeepStirring_AreTheFirstMockWording()
    {
        Assert.Equal(FirstSub, StopAllText.Sub(false));
        Assert.Equal(FirstNote, StopAllText.Note(false));
        Assert.Equal("搅拌保持当前状态；需要彻底切断请用急停。", StopAllText.StirNote(false));
        Assert.Equal("", StopAllText.StirSuffix(false));
        Assert.Equal("全部停止已发送到 1 台：3号机", StopAllText.Sent(new[] { "3号机" }, false));
        Assert.Equal("已停止 3 个反应釜", StopAllText.Stopped(3, false));
        Assert.Equal("没有运行中的反应釜 · 已发送全部停止", StopAllText.NothingRunning(false));
    }

    [Fact]
    public void Default_IsStopStirring_AsTheUserConfirmed()
        => Assert.True(ControlOptions.StopAllStopsStirring);   // 用户 2026-11-09：「把开关改成搅拌一起停止」

    [Fact]
    public void DemoFleet_DefaultsToTheOption()
        => Assert.Equal(ControlOptions.StopAllStopsStirring, new DemoFleet(DemoParams.Parse("freeze=1")).StopAllStopsStirring);

    /// <summary>
    /// 默认 3 台：1号机（HT-PR8 · 现场，共用搅拌 300 rpm 在转）、2号机（HT-PR4，独立搅拌，RV1–RV3 在转）。全部停止都不需要控制权（2号机是本地只读）。
    /// 不停搅拌：反应釜全停、搅拌照转（样稿）；停搅拌：共用搅拌和各釜搅拌也停。结果与提示的文字跟着设置。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DemoFleet_StopAll_StirringFollowsTheSetting(bool stopsStirring)
    {
        var fleet = new DemoFleet(DemoParams.Parse("freeze=1")) { StopAllStopsStirring = stopsStirring };
        var toasts = new List<string>();
        fleet.Toast += (_, e) => toasts.Add(e.Text);
        var d1 = fleet.Device(1)!;
        var d2 = fleet.Device(2)!;
        Assert.True(d1.Stir!.On);
        Assert.Null(d2.Stir);                                                               // 独立搅拌的型号没有共用搅拌
        var stirring2 = d2.Channels.Where(c => c.Installed && c.StirOn).Select(c => c.N).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, stirring2);
        int run1 = d1.Channels.Count(c => c.Installed && (c.IsRunning || c.Heating));
        Assert.True(run1 > 0);

        var r1 = fleet.Act(d1, DeviceAction.StopAll);
        var r2 = fleet.Act(d2, DeviceAction.StopAll);

        Assert.True(r1.Ok && r2.Ok);
        Assert.Equal(StopAllText.Stopped(run1, stopsStirring), r1.Message);
        Assert.Equal("1号机 · " + StopAllText.Stopped(run1, stopsStirring), toasts[0]);
        Assert.Equal(stopsStirring, r1.Message.EndsWith(" · 搅拌一并停止", StringComparison.Ordinal));
        Assert.All(d1.Channels.Concat(d2.Channels).Where(c => c.Installed), c => Assert.False(c.IsRunning || c.Heating));   // 反应釜照样全停
        Assert.Equal(!stopsStirring, d1.Stir.On);
        Assert.Equal(stopsStirring ? Array.Empty<int>() : stirring2, d2.Channels.Where(c => c.Installed && c.StirOn).Select(c => c.N).ToArray());
    }
}
