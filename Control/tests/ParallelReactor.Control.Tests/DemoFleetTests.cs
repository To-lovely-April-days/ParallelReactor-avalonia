using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Demo.DemoFleet 对照样稿 js/sim.js：MockVectors/DemoMockVectors.cs 里每个场景（devs=1 / 3 / 4、型号列表、stress=1 / 2、各种覆盖参数、
/// 每秒推进、操作脚本）都用同样的参数和脚本在 C# 里跑一遍，整段规范文字逐行相同（摘要 = 样稿 JS 的 FNV）。另有 DESIGN-BRIEF §8 的读数、
/// 多设备读取接口、变化通知、冻结、集合实例稳定等直接检查。
/// </summary>
public class DemoFleetTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(DemoMockVectors.Json));

    private static JsonElement Scenario(string name) => Doc.Value.RootElement.GetProperty("scenarios").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    public static IEnumerable<object[]> ScenarioNames() =>
        Doc.Value.RootElement.GetProperty("scenarios").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void MatchesMock(string name)
    {
        var sc = Scenario(name);
        var expected = sc.GetProperty("lines").EnumerateArray().Select(x => x.GetString()!).ToList();
        var actual = DemoDump.Replay(sc, out var fleet);
        Assert.True(expected.Count > 100 && fleet.Devices.Count > 0, $"{name}：向量只有 {expected.Count} 行");
        AssertLines(expected, actual, name);
        Assert.Equal(sc.GetProperty("digest").GetString(), DemoDump.Fnv(actual));
    }

    /// <summary>逐行比较；不同时给出第一处差异的上下文，便于定位。</summary>
    internal static void AssertLines(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
    {
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
        {
            if (expected[i] == actual[i]) continue;
            var sb = new StringBuilder();
            sb.AppendLine($"{what}：第 {i + 1} 行不同");
            for (int j = Math.Max(0, i - 3); j < i; j++) sb.AppendLine("  = " + expected[j]);
            sb.AppendLine("  样稿: " + expected[i]);
            sb.AppendLine("  C#  : " + actual[i]);
            Assert.Fail(sb.ToString());
        }
        if (expected.Count != actual.Count)
            Assert.Fail($"{what}：行数不同（样稿 {expected.Count}，C# {actual.Count}）；第一处多出：" + (expected.Count > n ? expected[n] : actual[n]));
    }

    [Fact]
    public void VectorsCoverRequiredCombos()
    {
        var names = ScenarioNames().Select(o => (string)o[0]).ToHashSet();
        foreach (var n in new[] { "devs1", "devs3", "devs4", "list", "stress1", "stress2", "tick-live", "actions" })
            Assert.Contains(n, names);
    }

    // ———————————————————— DESIGN-BRIEF §8 的演示读数 ————————————————————

    [Fact]
    public void Device1_ReadingsMatchDesignBrief()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        Assert.Equal(DemoFleet.T0, f.Now);
        Assert.Equal(new DateTime(2026, 11, 8, 10, 12, 0), f.Now);
        Assert.True(f.Frozen);
        var d = f.Device(1)!;
        Assert.Equal("1号机", d.Alias);
        Assert.Equal("HT-PR8 · 现场", d.Model.Label);
        Assert.Equal("192.168.1.20", d.Host);
        Assert.Equal("PR8-2026-0001", d.Sn);
        Assert.Equal(LeaseStatus.Remote, d.Lease.State);
        Assert.Equal(DemoFleet.Client, d.Lease.Holder);
        Assert.Equal("PT-20261108-0930", d.Session!.Id);
        Assert.True(d.Session.Active);
        Assert.True(d.Stir!.On);
        Assert.Equal(300, d.Stir.Rpm);
        Assert.True(d.GasOpen("a"));
        Assert.False(d.GasOpen("inert"));
        Assert.Equal(8, d.Channels.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, d.Channels.Where(c => c.Installed).Select(c => c.N));

        var rv1 = d.Channel(1)!;
        Assert.Equal(StateKey.React, rv1.State);
        Assert.Equal("反应中", d.StateText(rv1));
        Assert.Equal("119.8", Fmt.Temp(d.Reading(rv1, DemoReading.TempC)));
        Assert.Equal("120.0", Fmt.Temp(d.Reading(rv1, DemoReading.SpC)));
        Assert.Equal("0.349", Units.FmtP(d.Model, d.Reading(rv1, DemoReading.PressureMPa)));
        Assert.Equal("38", Fmt.Pct(d.Reading(rv1, DemoReading.HeaterPct)));
        Assert.Equal("1.82", Fmt.Mmol(d.Reading(rv1, DemoReading.Mmol)));
        var pr = d.Progress(rv1)!;
        Assert.Equal("加氢-标准", pr.Name);
        Assert.Equal((3, 4), (pr.K, pr.N));
        Assert.Equal("反应", pr.StepName);
        Assert.Equal(42, pr.StepRemainMin);
        Assert.Equal("第 3/4 步 · 剩 42 min", d.ProgText(rv1));

        var rv2 = d.Channel(2)!;
        Assert.Equal(StateKey.React, rv2.State);
        Assert.Equal("120.3", Fmt.Temp(rv2.TempC));
        Assert.Equal("0.346", Units.FmtP(d.Model, rv2.PressureMPa));

        var rv3 = d.Channel(3)!;
        Assert.Equal("升温中", d.StateText(rv3));
        Assert.Equal("86.4", Fmt.Temp(rv3.TempC));
        Assert.Equal(120, rv3.SpC);
        Assert.Equal(100, rv3.HeaterPct);
        Assert.Equal((1, 4), (d.Progress(rv3)!.K, d.Progress(rv3)!.N));

        var rv4 = d.Channel(4)!;
        Assert.Equal("升压中", d.StateText(rv4));
        Assert.Equal("0.217", Units.FmtP(d.Model, rv4.PressureMPa));
        Assert.Equal(0.35, rv4.PTargetMPa);
        Assert.Equal("充压", d.Progress(rv4)!.StepName);
        Assert.Equal(2, d.Progress(rv4)!.K);

        var a = Assert.Single(d.Alarms);
        Assert.Equal(AlarmLevel.Warn, a.Level);
        Assert.Equal("RV3 升温较慢 · 已用 18 min", a.Text);
        Assert.Equal("提醒 1", d.AlarmText());
        Assert.Equal(StateKey.Uninstalled, d.StateKeyOf(d.Channel(5)));
        Assert.Equal("未安装", d.StateWord(d.Channel(5)));
    }

    [Fact]
    public void Devices2And3_MatchDesignBrief()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        Assert.Equal(3, f.Devices.Count);
        var d2 = f.Device(2)!;
        Assert.Equal(("2号机", "HT-PR4", "192.168.1.24", "PR4-2026-0003"), (d2.Alias, d2.Model.Label, d2.Host, d2.Sn));
        Assert.Equal(LeaseStatus.Local, d2.Lease.State);
        Assert.Equal("本地 · 只读", d2.LeaseInfo.Text);
        Assert.Null(d2.Stir);   // 每釜独立搅拌
        var r1 = d2.Channel(1)!;
        Assert.Equal(("反应中", 150.0, 5.2, 800.0), (d2.StateText(r1), r1.TempC, r1.PressureMPa, d2.RpmOf(r1)));
        Assert.Equal("5.20", Units.FmtP(d2.Model, r1.PressureMPa));
        Assert.Equal("高压加氢-150", r1.Program!.Name);
        Assert.Equal((3, 5), (d2.Progress(r1)!.K, d2.Progress(r1)!.N));
        Assert.Equal(("升温中", 112.6), (d2.StateText(d2.Channel(2)), d2.Channel(2)!.TempC));
        Assert.Equal(("降温中", 68.3, true), (d2.StateText(d2.Channel(3)), d2.Channel(3)!.TempC, d2.Channel(3)!.Cooling));
        Assert.Equal(("已结束", 31.6), (d2.StateText(d2.Channel(4)), d2.Channel(4)!.TempC));

        var d3 = f.Device(3)!;
        Assert.Equal(("3号机", "HT-PR16", "192.168.1.36", "PR16-2026-0002"), (d3.Alias, d3.Model.Label, d3.Host, d3.Sn));
        Assert.Equal(16, d3.Channels.Count);
        Assert.All(Enumerable.Range(1, 6), n => Assert.Equal(StateKey.React, d3.Channel(n)!.State));
        Assert.All(Enumerable.Range(1, 6), n => Assert.Equal("筛选-80 ℃", d3.Channel(n)!.Program!.Name));
        Assert.Equal(new[] { StateKey.Heat, StateKey.Heat, StateKey.Press, StateKey.Press, StateKey.Alarm, StateKey.Done, StateKey.Ready, StateKey.Ready, StateKey.Idle, StateKey.Idle },
            Enumerable.Range(7, 10).Select(n => d3.Channel(n)!.State));
        var rv11 = d3.Channel(11)!;
        Assert.Equal("超压报警", d3.StateText(rv11));
        Assert.False(rv11.Sv);
        Assert.Equal(ProgramStatus.Held, rv11.Program!.Status);
        Assert.Equal("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", Assert.Single(d3.Alarms).Text);
        Assert.Equal(400, d3.Stir!.Rpm);
        Assert.Equal(DeviceHealth.Err, d3.Health);
    }

    [Fact]
    public void Devs4_AddsOfflineDevice4()
    {
        var f = new DemoFleet(DemoParams.Parse("devs=4&freeze=1"));
        Assert.Equal(new[] { "PR8-4", "PR4", "PR16", "PR8" }, f.Devices.Select(d => d.ModelKey));
        var d4 = f.Device(4)!;
        Assert.False(d4.Online);
        Assert.Equal(ConnStatus.Offline, d4.Conn.State);
        Assert.Equal("数据停在 10:11:40", d4.DataText);
        Assert.Equal("连接中断 · 重连中 12 s", d4.ConnInfo.Text);
        Assert.Equal(StateKey.Offline, d4.StateKeyOf(d4.Channel(1)));
        Assert.Equal("离线", d4.StateWord(d4.Channel(1)));
        Assert.Equal("反应中", d4.StateText(d4.Channel(1)));   // 最后的状态放在提示里
        Assert.Equal(100.2, d4.Reading(d4.Channel(1), DemoReading.TempC));   // 断线时是最后的值
        Assert.True(d4.Progress(d4.Channel(1))!.Offline);
        Assert.Equal("3/4 · 离线", d4.ProgText(d4.Channel(1), shortForm: true));
        Assert.Equal("4号机连接中断 · 重连中 12 s", d4.Why(DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.Equal("", d4.Why(DeviceAction.Reconnect));
        var t = f.Totals();
        Assert.Equal((4, 3, 1, 0), (t.Devices, t.Connected, t.Remote, t.Grace));
    }

    [Fact]
    public void Stress_AliasesAndWorstValues()
    {
        var f1 = new DemoFleet(DemoParams.Parse("stress=1&freeze=1"));
        Assert.Equal(new[] { "高压加氢测试一号", "高压加氢测试二号", "高压加氢测试三号" }, f1.Devices.Select(d => d.Alias));
        Assert.All(f1.Devices, d => Assert.Equal(ActionGuard.AliasMax, ActionGuard.AliasUnits(d.Alias)));
        var f2 = new DemoFleet(DemoParams.Parse("devs=4&stress=2&freeze=1"));
        Assert.Equal(new[] { "ReactorLab-PR8-A", "ReactorLab-PR4-B", "ReactorLab-P16-C", "ReactorLab-PR8-D" }, f2.Devices.Select(d => d.Alias));
        var pr4 = f2.Device(2)!;
        var v = StressValues.Of(pr4.Model);
        Assert.Equal((249.9, 250.0, 9.99, 1500.0, 60.0, 1234.5), (v.T, v.Sp, v.P, v.Rpm, v.Vol, v.Mmol));
        Assert.Equal("高压加氢-250-长时保温", pr4.Channel(1)!.Program!.Name);
        Assert.Equal("剩 999 min", pr4.Progress(pr4.Channel(1))!.RemainText);
        var rv16 = f2.Device(3)!.Channel(16)!;
        Assert.Equal(StateKey.Alarm, rv16.State);
        Assert.Equal(ProgramStatus.Held, rv16.Program!.Status);
        Assert.Equal(2.997, StressValues.Of(f2.Device(3)!.Model).P);
        Assert.Equal(4.136, StressValues.Of(f2.Device(1)!.Model).P);
    }

    // ———————————————————— 读取接口、变化通知、推进 ————————————————————

    [Fact]
    public void Tick_AdvancesClock_RaisesChanged_AndSamplesHistoryEvery10s()
    {
        var f = new DemoFleet(DemoParams.Parse("devs=4&offline=none"));
        Assert.False(f.Frozen);
        var d = f.Device(1)!;
        var hist = d.Channel(1)!.History;
        Assert.Equal(361, hist.Count);   // 60 min，每 10 s 一个
        Assert.Equal(DemoFleet.T0.AddMinutes(-60), hist[0].T);
        var kinds = new List<DemoChangeKind>();
        f.Changed += (_, e) => kinds.Add(e.Kind);
        for (int i = 0; i < 9; i++) Assert.True(f.Tick());
        Assert.Equal(361, hist.Count);
        Assert.True(f.Tick());
        Assert.Equal(362, hist.Count);   // 第 10 秒追加
        Assert.Equal(DemoFleet.T0.AddSeconds(10), f.Now);
        Assert.Equal(10, f.TickCount);
        Assert.Equal(Enumerable.Repeat(DemoChangeKind.Tick, 10), kinds);
        Assert.Same(hist, f.Device(1)!.Channel(1)!.History);   // 集合实例不变，可以长期持有
        Assert.Equal(f.Now, d.DataAt);
    }

    [Fact]
    public void Frozen_TickDoesNothing()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        int raised = 0;
        f.Changed += (_, _) => raised++;
        Assert.False(f.Tick());
        Assert.Equal(DemoFleet.T0, f.Now);
        Assert.Equal(0, raised);
        f.Apply(DemoParams.Parse(""));   // 解冻
        Assert.False(f.Frozen);
        Assert.Equal(1, raised);          // Params
        Assert.True(f.Tick());
        Assert.Equal(DemoFleet.T0.AddSeconds(1), f.Now);
    }

    [Fact]
    public void Apply_ChangingDevsRebuilds_OtherwiseDiff()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d1 = f.Device(1)!;
        var kinds = new List<DemoChangeKind>();
        f.Changed += (_, e) => kinds.Add(e.Kind);
        f.Apply(DemoParams.Parse("freeze=1&lease=2:requested"));
        Assert.Same(d1, f.Device(1));
        Assert.Equal(LeaseStatus.Requested, f.Device(2)!.Lease.State);
        Assert.Equal("申请中 · 45 s", f.Device(2)!.LeaseInfo.Text);
        Assert.Equal("2号机等待触屏确认 · 剩 45 s", f.Device(2)!.GuardText(GuardKind.Control));
        f.Apply(DemoParams.Parse("freeze=1&devs=4&lease=2:requested"));
        Assert.NotSame(d1, f.Device(1));
        Assert.Equal(4, f.Devices.Count);
        Assert.Equal(LeaseStatus.Requested, f.Device(2)!.Lease.State);   // 重建后覆盖参数重新套上
        Assert.Equal(new[] { DemoChangeKind.Params, DemoChangeKind.Devices }, kinds);
    }

    [Fact]
    public void LeaseRequest_AutoConfirmsAfter5s_WithToast()
    {
        var f = new DemoFleet(DemoParams.Parse(""));
        var toasts = new List<string>();
        f.Toast += (_, e) => toasts.Add(e.Text);
        var d2 = f.Device(2)!;
        Assert.Equal("2号机为本地只读 · 先申请控制权", d2.Why(DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 150)));
        var r = f.Act(d2, DeviceAction.LeaseRequest);
        Assert.True(r.Ok);
        Assert.Equal("2号机 · 已申请控制权 · 等待触屏确认", toasts[^1]);
        for (int i = 0; i < 5; i++) f.Tick();
        Assert.Equal(LeaseStatus.Remote, d2.Lease.State);
        Assert.Equal("2号机触屏已确认 · 远程控制中", toasts[^1]);
        Assert.Equal("RV1 已在运行", d2.Why(DeviceAction.Start, ActionPayload.Ch(1)));
    }

    [Fact]
    public void Act_BlockedReturnsReason_StopClassAlwaysSends()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d3 = f.Device(3)!;   // 本地只读
        var r = f.Act(d3, DeviceAction.HeatStart, new ActionPayload(N: 13, Sp: 80), quiet: true);
        Assert.False(r.Ok);
        Assert.Equal("3号机为本地只读 · 先申请控制权", r.Message);
        var s = f.Act(d3, DeviceAction.Stop, ActionPayload.Ch(13), quiet: true);   // 停止类：不看控制权，没在运行也照样发送
        Assert.True(s.Ok);
        Assert.True(s.Info);
        Assert.Equal("RV13 未在运行 · 已发送停止", s.Message);
        Assert.Equal("RV13 未在运行 · 已发送停止", d3.Events[0].Text);
        var stop11 = f.Act(d3, DeviceAction.Stop, ActionPayload.Ch(11), quiet: true);
        Assert.True(stop11.Ok);
        Assert.Equal(StateKey.Stopped, d3.Channel(11)!.State);
        Assert.Equal(ProgramStatus.Aborted, d3.Channel(11)!.Program!.Status);
    }

    [Fact]
    public void AddDevice_NeverConnected_ThenOnline()
    {
        var f = new DemoFleet(DemoParams.Parse(""));
        var d = f.AddDevice(new DemoNewDevice(Host: "192.168.1.99", ModelKey: "PR16"));
        Assert.Equal((4, "4号机"), (d.K, d.Alias));
        Assert.False(d.Conn.EverOnline);
        Assert.Equal("正在连接…", d.ConnInfo.Text);
        Assert.Equal("尚未收到数据", d.DataText);
        Assert.Equal("未连接", d.StateWord(d.Channel(1)));
        Assert.Null(d.Reading(d.Channel(1), DemoReading.TempC));
        Assert.Empty(d.Channel(1)!.History);
        Assert.Equal("4号机正在连接 · 稍候", d.Why(DeviceAction.Stop, ActionPayload.Ch(1)));
        f.Tick();
        f.Tick();
        Assert.True(d.Online);
        Assert.True(d.Conn.EverOnline);
        Assert.Equal(361, d.Channel(1)!.History.Count);   // 首次连上：平稳的室温历史
        Assert.Equal("5号机", f.FreeAlias());
        Assert.Equal("名称已被其他设备使用（192.168.1.24）", f.AliasError("2号机", d));
    }

    [Fact]
    public void Facts_ReflectDevice()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1&estop=1"));
        var facts = f.Device(1)!.Facts();
        Assert.True(facts.EstopActive);
        Assert.True(facts.SharedStir);
        Assert.Equal(8, facts.Channels.Count);
        Assert.Contains("h2-std", facts.ProgramIds!);
        Assert.Equal(new[] { "2号机", "3号机" }, facts.Peers.Select(p => p.Alias));
        Assert.Equal("1号机急停中 · 需在触屏复位", ActionGuard.Why(facts, DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 100)));
        Assert.Equal("", ActionGuard.Why(facts, DeviceAction.StopAll));
        Assert.Equal("1号机已在急停 · 需在触屏复位", ActionGuard.Why(facts, DeviceAction.Estop));
        Assert.Equal("急停中", f.Device(1)!.EstopText);
        Assert.Equal("报警 1 · 提醒 1", f.Device(1)!.AlarmText(all: true));
        Assert.Equal("提醒 1", f.Device(1)!.AlarmText());   // 急停那条不计
    }
}
