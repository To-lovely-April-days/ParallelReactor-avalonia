using System;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Programs;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.StepTracker：一次执行的身份（启动号 + 反应釜 + startedAt）、第 0 步 = startedAt、连续观察时换步取两帧中点、
/// 跳步 / 断线 / 中途连上时开始时刻不知道（只记第一次看到）、结束时刻、重跑同一个程序是新的执行、HMI 重启清空、没有变化不加版本号。
/// </summary>
public class StepTrackerTests
{
    private static readonly DateTime D0 = new(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);

    /// <summary>时刻相差不到 1 ms（AddSeconds(小数) 的舍入）。</summary>
    private static void Near(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs((expected - actual!.Value).TotalMilliseconds) < 1, $"{expected:O} ≠ {actual:O}");
    }

    private static ChannelProgramSnap P(int step, string state = "running", int count = 4, double startedAgoS = 0, string? by = ByMe) =>
        Prog(step, count, state, D0.AddSeconds(-startedAgoS), by);

    [Fact]
    public void NewRun_Step0StartsAtStartedAt()
    {
        var t = new StepTracker();
        Assert.True(t.Observe("b1", D0.AddSeconds(30), 1, P(0, startedAgoS: 0)));
        var r = t.Track(1)!;
        Assert.Equal(D0, r.StartedAt);
        Assert.Equal(0, r.Index);
        Assert.Equal(D0, r.StartOf(0));
        Assert.Null(r.StartOf(1));
        Assert.Equal(4, r.StepCount);
        Assert.Equal(ByMe, r.StartedBy);
        Assert.Equal("b1", r.BootId);
        Assert.False(r.Ended);
    }

    [Fact]
    public void ContinuousStepChange_MidpointOfTwoFrames()
    {
        var t = new StepTracker();
        t.Observe("b1", D0.AddSeconds(1), 1, P(0));
        int v = t.Version;
        for (int i = 1; i < 10; i++) Assert.False(t.Observe("b1", D0.AddSeconds(1 + i * 1.2), 1, P(0)));   // 没变化：不加版本号
        Assert.Equal(v, t.Version);
        Assert.True(t.Observe("b1", D0.AddSeconds(12.2), 1, P(1)));                                       // 上一帧 11.8 s
        Near(D0.AddSeconds(12.0), t.Track(1)!.StartOf(1));
        Near(D0.AddSeconds(12.2), t.Track(1)!.FirstSeenOf(1));
        Assert.Equal(1, t.Track(1)!.Index);
        Near(D0.AddSeconds(12.2), t.LastSeenOf(1));
    }

    [Fact]
    public void JumpGapOrMidRun_StartUnknown_FirstSeenKept()
    {
        var t = new StepTracker(maxGapSeconds: 10);
        t.Observe("b1", D0.AddSeconds(1), 1, P(0));
        t.Observe("b1", D0.AddSeconds(2.2), 1, P(2));                 // 一下跳了两步：第 1、2 步的开始都不知道
        var r = t.Track(1)!;
        Assert.Null(r.StartOf(1));
        Assert.Null(r.StartOf(2));
        Assert.Equal(D0.AddSeconds(2.2), r.FirstSeenOf(2));
        t.Observe("b1", D0.AddSeconds(30), 1, P(3));                  // 断了 27.8 s（> 10 s）：不知道
        Assert.Null(t.Track(1)!.StartOf(3));
        Assert.Equal(D0.AddSeconds(30), t.Track(1)!.FirstSeenOf(3));

        var mid = new StepTracker();
        mid.Observe("b1", D0.AddSeconds(3600), 2, P(2, startedAgoS: 0));  // PC 中途连上：第 2 步
        var m = mid.Track(2)!;
        Assert.Equal(D0, m.StartOf(0));                                   // 第 0 步仍知道（startedAt）
        Assert.Null(m.StartOf(1));
        Assert.Null(m.StartOf(2));
        Assert.Equal(D0.AddSeconds(3600), m.FirstSeenOf(2));
    }

    [Fact]
    public void End_ContinuousMidpoint_ElseUnknown()
    {
        var t = new StepTracker();
        t.Observe("b1", D0.AddSeconds(100), 1, P(3));
        t.Observe("b1", D0.AddSeconds(101.2), 1, P(3, "completed"));
        Assert.True(t.Track(1)!.Ended);
        Near(D0.AddSeconds(100.6), t.Track(1)!.EndedAt);
        t.Observe("b1", D0.AddSeconds(100), 2, P(1));
        t.Observe("b1", D0.AddSeconds(300), 2, P(1, "aborted"));       // 断线期间中止的：结束时刻不知道
        Assert.True(t.Track(2)!.Ended);
        Assert.Null(t.Track(2)!.EndedAt);
        Assert.Equal("aborted", t.Track(2)!.State);
    }

    [Fact]
    public void RerunIsANewRun_BootChangeResets_NoProgramIgnored()
    {
        var t = new StepTracker();
        t.Observe("b1", D0.AddSeconds(5), 1, P(3, "completed"));
        t.Observe("b1", D0.AddSeconds(600), 1, Prog(0, 4, "running", D0.AddSeconds(590)));
        Assert.Equal(D0.AddSeconds(590), t.Track(1)!.StartedAt);
        Assert.Equal(D0.AddSeconds(590), t.Track(1)!.StartOf(0));
        Assert.Equal(0, t.Track(1)!.Index);
        Assert.False(t.Observe("b1", D0.AddSeconds(601), 3, null));            // 没有程序：什么都不记
        Assert.False(t.Observe("b1", D0.AddSeconds(601), 3, Prog(0, 4, "running", startedAt: null)));
        Assert.Null(t.Track(3));
        Assert.NotNull(t.TrackOf(1, Prog(0, 4, "running", D0.AddSeconds(590)), "b1"));
        Assert.Null(t.TrackOf(1, Prog(0, 4, "running", D0.AddSeconds(591)), "b1"));
        Assert.Null(t.TrackOf(1, Prog(0, 4, "running", D0.AddSeconds(590)), "b2"));
        Assert.True(t.Observe("b2", D0.AddSeconds(700), 2, P(0)));              // HMI 重启：之前的全部作废
        Assert.Null(t.Track(1));
        Assert.NotNull(t.Track(2));
        Assert.Equal("b2", t.BootId);
    }

    [Fact]
    public void ObserveFrame_AllChannels_PreparingThenStep0()
    {
        var t = new StepTracker();
        var f = Frame(D0.AddSeconds(2), Ch(1, program: Prog(-1, 3, "preparing", D0)), Ch(2), Ch(3, program: Prog(1, 2, "running", D0.AddSeconds(-90))));
        Assert.True(t.Observe("b1", f));
        Assert.Equal(-1, t.Track(1)!.Index);
        Assert.Equal(D0, t.Track(1)!.StartOf(0));
        Assert.Null(t.Track(2));
        Assert.Equal(1, t.Track(3)!.Index);
        Assert.Equal(2, t.Runs.Count);
        t.Observe("b1", Frame(D0.AddSeconds(3.2), Ch(1, program: Prog(0, 3, "running", D0))));
        Assert.Equal(D0, t.Track(1)!.StartOf(0));                       // 第 0 步仍是 startedAt（不是中点）
        Assert.Equal(0, t.Track(1)!.Index);
        Assert.Equal("running", t.Track(1)!.State);
    }

    [Fact]
    public void Restore_FirstFrameAfterLongPause_IsNotContinuous()
    {
        var t = new StepTracker();
        t.Observe("b1", D0.AddSeconds(10), 1, P(1));
        var saved = t.Runs.ToArray();
        var t2 = new StepTracker();
        t2.Restore(saved, "b1");
        Assert.Equal(1, t2.Track(1)!.Index);
        t2.Observe("b1", D0.AddSeconds(4000), 1, P(2));                  // PC 关了一个多小时
        Assert.Null(t2.Track(1)!.StartOf(2));
        Assert.Equal(D0.AddSeconds(4000), t2.Track(1)!.FirstSeenOf(2));
        var t3 = new StepTracker();
        t3.Restore(saved, "b9");                                           // 不同启动号的不恢复
        Assert.Empty(t3.Runs);
    }
}
