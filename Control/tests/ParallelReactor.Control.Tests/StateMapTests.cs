using System;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.State：状态键与样稿 PR.ST 逐项一致（状态字、类别、字重、RUNNING）；真机遥测 → 状态的映射（PORT-PLAN §3.1 / §3.2 第 3 条 / §3.5），
/// 用手造的 ChannelSnap 覆盖每条规则，并用实测样本 live-snapshot.json / ws-telemetry.jsonl 走一遍。
/// </summary>
public class StateMapTests
{
    private static ChannelSnap Ch(string state, string? cause = null, bool overTemp = false, bool overPressure = false,
        ChannelProgramSnap? program = null, bool installed = true, int? atMode = 4, string phase = "off", ProfileSnap? profile = null) =>
        new(1, installed, state, cause, overTemp, overPressure, null, 25.0, 0, null, 0, true, false, atMode, "fixed",
            new ApproachSnap(phase, null, null, ""), profile, 200.0, 7000, 0, null, false, false, program);

    private static ChannelProgramSnap Prog(string state, bool running = false, bool waitingGas = false) =>
        new(running, 1, 4, state, "", "local", null, waitingGas);

    [Theory]
    [InlineData("idle", StateKey.Idle, "停用", StateKind.Neutral)]
    [InlineData("ready", StateKey.Ready, "已启用", StateKind.Neutral)]
    [InlineData("heating", StateKey.Heat, "升温中", StateKind.Run)]
    [InlineData("pressing", StateKey.Press, "升压中", StateKind.Run)]
    [InlineData("react", StateKey.React, "反应中", StateKind.Run)]
    [InlineData("wait", StateKey.Wait, "等待中", StateKind.Run)]
    [InlineData("stir", StateKey.Stir, "搅拌中", StateKind.Run)]
    [InlineData("cooling", StateKey.Cool, "降温中", StateKind.Run)]
    [InlineData("done", StateKey.Done, "已结束", StateKind.Done)]
    [InlineData("heat", StateKey.Heat, "升温中", StateKind.Run)]          // 样稿代码也认
    [InlineData("stopped", StateKey.Stopped, "已停止", StateKind.Neutral)]
    public void HmiStateCodes(string state, StateKey key, string word, StateKind kind)
    {
        var s = StateMap.Map(Ch(state));
        Assert.Equal(key, s.Key);
        Assert.Equal(word, s.Word);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(AlarmCause.None, s.Cause);
    }

    [Theory]
    [InlineData("alarm", "overPressure", false, false, "超压报警", AlarmCause.OverPressure)]
    [InlineData("alarm", "overTemp", false, false, "超温报警", AlarmCause.OverTemp)]
    [InlineData("alarm", null, false, false, "报警", AlarmCause.Unknown)]
    [InlineData("alarm", null, false, true, "超压报警", AlarmCause.OverPressure)]   // alarmCause 缺失时看切断标志
    [InlineData("alarm", null, true, false, "超温报警", AlarmCause.OverTemp)]
    [InlineData("alarm", "overTemp", false, true, "超温报警", AlarmCause.OverTemp)]   // alarmCause 优先
    [InlineData("done", null, true, false, "超温报警", AlarmCause.OverTemp)]         // 切断未解除：即使状态被复位也算报警
    [InlineData("idle", null, false, true, "超压报警", AlarmCause.OverPressure)]
    public void Alarms(string state, string? cause, bool overTemp, bool overPressure, string word, AlarmCause expectedCause)
    {
        var s = StateMap.Map(Ch(state, cause, overTemp, overPressure), estopActive: true);
        Assert.Equal(StateKey.Alarm, s.Key);
        Assert.Equal(word, s.Word);
        Assert.Equal(expectedCause, s.Cause);
        Assert.Equal(StateKind.Alarm, s.Kind);
        Assert.True(s.IsRunning);                      // 样稿 PR.ST.RUNNING 含 alarm（占用通道）
        Assert.True(StateKeys.IsBold(s.Key));
    }

    [Theory]
    [InlineData("aborted", false, StateKey.Stopped, "已停止")]
    [InlineData("failed", false, StateKey.Stopped, "已停止")]     // 报警中止后
    [InlineData("completed", false, StateKey.Done, "已结束")]
    [InlineData(null, false, StateKey.Done, "已结束")]            // 没有程序的手动停止：HMI 无法区分，显示「已结束」
    [InlineData("completed", true, StateKey.Stopped, "已停止")]   // 急停中
    [InlineData(null, true, StateKey.Stopped, "已停止")]
    public void Done_StoppedHeuristic(string? programState, bool estop, StateKey key, string word)
    {
        var s = StateMap.Map(Ch("done", program: programState is null ? null : Prog(programState)), estopActive: estop);
        Assert.Equal(key, s.Key);
        Assert.Equal(word, s.Word);
    }

    [Fact]
    public void WaitingGas_IsWaitWithOwnWord()
    {
        var s = StateMap.Map(Ch("heating", program: Prog("waitingGas", running: true, waitingGas: true)));
        Assert.Equal(StateKey.Wait, s.Key);
        Assert.Equal("等待气路", s.Word);              // 不是样稿的「等待进气」（那是进气口关闭）
        Assert.Equal(StateKind.Run, s.Kind);
        Assert.Equal(StateKey.Heat, StateMap.Map(Ch("heating", program: Prog("running", running: false, waitingGas: true))).Key);
        Assert.Equal("等待气路（RV3 占用中）", StateMap.WaitGasText(3));
        Assert.Equal("等待气路", StateMap.WaitGasText(null));
    }

    [Fact]
    public void Offline_Uninstalled_NoData()
    {
        Assert.Equal(new ChannelState(StateKey.Uninstalled, "未安装"), StateMap.Map(Ch("idle", installed: false)));
        Assert.Equal(new ChannelState(StateKey.Uninstalled, "未安装"), StateMap.Map(Ch("react", installed: false), online: false));   // 未安装优先
        Assert.Equal(new ChannelState(StateKey.Offline, "离线"), StateMap.Map(Ch("react"), online: false));
        Assert.Equal(new ChannelState(StateKey.Offline, "未连接"), StateMap.Map(null, online: false, everOnline: false));
        Assert.Equal(new ChannelState(StateKey.Offline, "未连接"), StateMap.Map(null));                                          // 在线但还没有数据
        Assert.Equal(new ChannelState(StateKey.Uninstalled, "未安装"), StateMap.Map(null, online: false, everOnline: false, installed: false));
        Assert.Equal(StateKind.Offline, StateMap.Map(Ch("alarm", "overPressure"), online: false).Kind);                           // 断线时报警也显示离线
        Assert.Equal(StateKey.React, StateMap.Map(Ch("react"), online: true).Key);                                                  // 「断线前 反应中」：按在线再算一次
    }

    [Fact]
    public void UnknownCode_IsNeutralWithRawWord()
    {
        var s = StateMap.Map(Ch("calibrating"));
        Assert.Equal(StateKey.Idle, s.Key);
        Assert.Equal("calibrating", s.Word);
        Assert.Equal("—", StateMap.Map(Ch("")).Word);
        Assert.Null(StateMap.KeyOfHmi("calibrating"));
        Assert.Null(StateMap.KeyOfHmi(null));
    }

    /// <summary>实测 /snapshot：RV1 已启用、RV2 done + 程序 aborted（远程中止过）→ 已停止、RV3–RV8 停用。</summary>
    [Fact]
    public void LiveSnapshotSample()
    {
        var snap = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        var words = Enumerable.Range(1, 8).Select(n => StateMap.FromSnapshot(snap, n).Word).ToArray();
        Assert.Equal(new[] { "已启用", "已停止", "停用", "停用", "停用", "停用", "停用", "停用" }, words);
        Assert.Equal(StateKey.Stopped, StateMap.FromSnapshot(snap, 2).Key);
        Assert.All(Enumerable.Range(1, 8), n => Assert.Equal(StateKey.Offline, StateMap.FromSnapshot(snap, n, online: false).Key));
        Assert.Equal("未连接", StateMap.FromSnapshot(null, 1, online: false, everOnline: false).Word);
        Assert.Equal("未安装", StateMap.FromSnapshot(snap, 9, installed: false).Word);   // 帧里没有的通道按型号表
        Assert.Equal("未连接", StateMap.FromSnapshot(snap, 9).Word);
    }

    [Fact]
    public void LiveWsFrames()
    {
        foreach (var line in Samples.Lines("ws-telemetry.jsonl"))
        {
            var f = ApiJson.Deserialize<TelemetrySnapshot>(line)!;
            Assert.Equal("已启用", StateMap.FromSnapshot(f, 1).Word);
            Assert.Equal("已停止", StateMap.FromSnapshot(f, 2).Word);
            Assert.All(f.Channels, c => Assert.False(StateMap.IsHeating(c)));   // 全部 At=4
        }
    }

    [Fact]
    public void EstopFromSnapshot_TurnsDoneIntoStopped()
    {
        var snap = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        var done = snap.Channels[1] with { Program = null };
        var withEstop = snap with { Estop = snap.Estop with { Active = true }, Channels = new[] { done } };
        var noEstop = snap with { Channels = new[] { done } };
        Assert.Equal("已停止", StateMap.FromSnapshot(withEstop, 2).Word);
        Assert.Equal("已结束", StateMap.FromSnapshot(noEstop, 2).Word);
    }

    [Fact]
    public void IsHeating_FromAtModeApproachProfile()
    {
        Assert.True(StateMap.IsHeating(Ch("heating", atMode: 0)));
        Assert.False(StateMap.IsHeating(Ch("heating", atMode: 4)));
        Assert.True(StateMap.IsHeating(Ch("heating", atMode: null, phase: "settle")));   // 温控离线：看逼近相位
        Assert.False(StateMap.IsHeating(Ch("heating", atMode: null, phase: "off")));
        Assert.True(StateMap.IsHeating(Ch("heating", atMode: null, profile: new ProfileSnap(true, 1, Array.Empty<ProfileSegmentSnap>(), 0, 10, false, ""))));
        Assert.False(StateMap.IsHeating(Ch("heating", atMode: 0, installed: false)));
        Assert.False(StateMap.IsHeating(null));
    }

    /// <summary>状态键与样稿 PR.ST 逐项相同：代码、状态字、类别、字重、顺序、RUNNING。</summary>
    [Fact]
    public void StateKeys_MatchMock()
    {
        var st = MockVectorData.Get("states").EnumerateArray().ToArray();
        Assert.Equal(st.Select(s => s.GetProperty("key").GetString()), StateKeys.All.Select(StateKeys.Code));
        foreach (var s in st)
        {
            Assert.True(StateKeys.TryParse(s.GetProperty("key").GetString(), out var k));
            Assert.Equal(s.GetProperty("zh").GetString(), StateKeys.Word(k));
            Assert.Equal(s.GetProperty("kind").GetString(), StateKeys.KindOf(k).ToString().ToLowerInvariant());
            Assert.Equal(s.GetProperty("weight").GetInt32() == 600, StateKeys.IsBold(k));
        }
        Assert.Equal(MockVectorData.Get("running").EnumerateArray().Select(x => x.GetString()), StateKeys.Running.Select(StateKeys.Code));
        Assert.All(StateKeys.All, k => Assert.Equal(StateKeys.Running.Contains(k), StateKeys.IsRunning(k)));
        Assert.False(StateKeys.TryParse("heating", out _));   // HMI 代码走 StateMap.KeyOfHmi
        Assert.Equal(11, StateKeys.GalleryOrder.Count);
        Assert.Equal("离线", StateKeys.OfflineText(true));
        Assert.Equal("未连接", StateKeys.OfflineText(false));
    }
}
