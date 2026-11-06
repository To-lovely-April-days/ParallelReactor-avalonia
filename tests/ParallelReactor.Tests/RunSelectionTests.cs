using System;
using System.Linq;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>开始运行的通道筛选：只给已启用 / 已结束且无超温 / 超压未解除的通道加热；报警通道阻塞运行。</summary>
public class RunSelectionTests
{
    public static TheoryData<ReactorState> AllStates()
    {
        var d = new TheoryData<ReactorState>();
        foreach (var s in Enum.GetValues<ReactorState>()) d.Add(s);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void CanStartHeat_OnlyReadyOrDone_AndNotTripped(ReactorState s)
    {
        bool startable = s is ReactorState.Ready or ReactorState.Done;
        Assert.Equal(startable, RunSelection.CanStartHeat(s, false, false));
        Assert.False(RunSelection.CanStartHeat(s, true, false));
        Assert.False(RunSelection.CanStartHeat(s, false, true));
        Assert.False(RunSelection.CanStartHeat(s, true, true));
    }

    [Fact]
    public void Alarm_AndRunningStates_NeverHeated()
    {
        foreach (var s in new[] { ReactorState.Alarm, ReactorState.Heating, ReactorState.React, ReactorState.Pressing,
                     ReactorState.Wait, ReactorState.Stir, ReactorState.Cooling, ReactorState.Idle })
            Assert.False(RunSelection.CanStartHeat(s, false, false), s.ToString());
    }

    [Fact]
    public void Blocking_ListsAlarmAndTripped()
    {
        var chans = new[]
        {
            new RunChannel(1, ReactorState.Ready, false, false),
            new RunChannel(2, ReactorState.Alarm, false, false),     // 报警（已解除但未复位）
            new RunChannel(3, ReactorState.Done, true, false),       // 超温未解除
            new RunChannel(4, ReactorState.Idle, false, true),       // 停用但超压未解除
            new RunChannel(5, ReactorState.Heating, false, false),
        };
        Assert.Equal(new[] { 2, 3, 4 }, RunSelection.Blocking(chans));
        Assert.Equal(new[] { 1 }, RunSelection.ToHeat(chans));
    }

    [Fact]
    public void IdleChannelOverTemp_StillBlocks()
    {
        // 停用通道上的超温报警（可能未接热电偶）也阻塞：C19 要求任何报警 / 未解除的超温超压都阻塞运行
        var idleOt = new RunChannel(6, ReactorState.Alarm, true, false);
        Assert.True(RunSelection.IsBlocking(idleOt));
        Assert.False(RunSelection.CanStartHeat(idleOt));
        // 停用但超温未解除（报警已被复位）：同样阻塞
        Assert.True(RunSelection.IsBlocking(new RunChannel(7, ReactorState.Idle, true, false)));
    }

    [Fact]
    public void NoAlarm_NoBlocking()
    {
        var chans = Enumerable.Range(1, 8).Select(i => new RunChannel(i, i <= 4 ? ReactorState.Ready : ReactorState.Idle, false, false));
        Assert.Empty(RunSelection.Blocking(chans));
        Assert.Equal(new[] { 1, 2, 3, 4 }, RunSelection.ToHeat(chans));
    }
}
