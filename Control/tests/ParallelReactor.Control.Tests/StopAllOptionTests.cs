using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 「全部停止」停不停搅拌（Core 的 ControlOptions.StopAllStopsStirring，等用户确认）：两种取值下的文字与演示数据的效果。
/// 测试不假定默认值是哪一个（改默认只改 ControlOptions 那一行，测试不用跟着改），只核对三件事：默认值取自那一行；两种取值各自的效果；
/// 不停搅拌时文字逐字同样稿。实时设备与对话框的接线在 ParallelReactor.Control.App.Tests 的 StopAllStirringTests。
/// </summary>
public class StopAllOptionTests
{
    // 样稿原文：js/dialogs.js 的 stopall 对话框（副标题、说明，粗体去掉）
    private const string MockSub = "停止所选设备上全部反应釜：中止程序、停止加热、关闭进气";
    private const string MockNote = "全部停止不需要控制权，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。搅拌保持当前状态；需要彻底切断请用急停。";

    [Fact]
    public void Texts_KeepStirring_AreTheMockWording()
    {
        Assert.Equal(MockSub, StopAllText.Sub(false));
        Assert.Equal(MockNote, StopAllText.Note(false));
        Assert.Equal("搅拌保持当前状态；需要彻底切断请用急停。", StopAllText.StirNote(false));
        Assert.Equal("", StopAllText.StirSuffix(false));
        Assert.Equal("全部停止已发送到 2 台：1号机、2号机", StopAllText.Sent(new[] { "1号机", "2号机" }, false));   // dialogs.js stopall 的 toast
        Assert.Equal("已停止 3 个反应釜", StopAllText.Stopped(3, false));                                          // sim.js RUN.stopAll
        Assert.Equal("没有运行中的反应釜 · 已发送全部停止", StopAllText.NothingRunning(false));
    }

    [Fact]
    public void Texts_StopStirring_SayItAndNeverSayStirringKeepsRunning()
    {
        Assert.Equal(MockSub + "、停止搅拌", StopAllText.Sub(true));
        Assert.Equal(MockNote.Replace("搅拌保持当前状态；需要彻底切断请用急停。", "搅拌也会一并停止。"), StopAllText.Note(true));
        Assert.DoesNotContain("搅拌保持当前状态", StopAllText.Note(true));
        Assert.Equal("全部停止已发送到 1 台：3号机 · 搅拌一并停止", StopAllText.Sent(new[] { "3号机" }, true));
        Assert.Equal("已停止 3 个反应釜 · 搅拌一并停止", StopAllText.Stopped(3, true));
        Assert.Equal("没有运行中的反应釜 · 已发送全部停止 · 搅拌一并停止", StopAllText.NothingRunning(true));
    }

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
