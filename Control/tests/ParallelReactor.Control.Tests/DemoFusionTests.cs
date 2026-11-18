using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版演示数据（fusion=1，样稿 research/main-fusion.html）：时钟、设备与反应釜、程序库与运行表（本步剩余 / 预计结束 = 样稿 etaOf）、
/// 0–90 min 曲线（吸气量饱和、RV4 超压跳变）、报警与事件文字、整机参数、断线演示（offline=1）、参数解析；旧数据不受影响。
/// </summary>
public class DemoFusionTests
{
    private static DemoFleet Fleet(string extra = "") => new(DemoParams.Parse("fusion=1&freeze=1" + extra));

    private static DateTime At(int h, int m, int s = 0) => new(2026, 11, 15, h, m, s, DateTimeKind.Local);

    private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ———————————————————— 参数 ————————————————————

    [Fact]
    public void Params_ParsePrintRoundTrip()
    {
        Assert.False(DemoParams.Default.Fusion);
        Assert.False(DemoParams.Parse("fusion=0").Fusion);
        var p = DemoParams.Parse("freeze=1&fusion=1&offline=1&dialog=add");
        Assert.True(p.Fusion);
        Assert.Empty(p.Extra);
        Assert.Equal("freeze=1&offline=1&fusion=1&dialog=add", p.ToString());
        Assert.Equal(p, DemoParams.Parse(p.ToString()));
        Assert.NotEqual(p, p with { Fusion = false });
        Assert.DoesNotContain("fusion", DemoParams.Default.ToString());
    }

    [Fact]
    public void Apply_TogglingFusion_Rebuilds()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        Assert.Equal(DemoFleet.T0, f.Now);
        Assert.False(f.Fusion);
        f.Apply(DemoParams.Parse("freeze=1&fusion=1"));
        Assert.True(f.Fusion);
        Assert.Equal(DemoFleet.FusionT0, f.Now);
        Assert.Equal(new[] { "PR8", "PR4", "PR16" }, f.Devices.Select(d => d.ModelKey));
        f.Apply(DemoParams.Parse("freeze=1"));
        Assert.False(f.Fusion);
        Assert.Equal(DemoFleet.T0, f.Now);
        Assert.All(f.Devices, d => Assert.Null(d.Plant));
    }

    // ———————————————————— 时钟 ————————————————————

    [Fact]
    public void Clock_FrozenAt_20261115_143007()
    {
        var f = Fleet();
        Assert.Equal(new DateTime(2026, 11, 15, 14, 30, 7, DateTimeKind.Local), f.Now);
        Assert.True(f.Frozen);
        Assert.False(f.Tick());
        Assert.Equal(DemoFleet.FusionT0, f.Now);
    }

    [Fact]
    public void Clock_Ticks_WhenNotFrozen()
    {
        var f = new DemoFleet(DemoParams.Parse("fusion=1"));
        for (int i = 0; i < 300; i++) Assert.True(f.Tick());
        Assert.Equal(DemoFleet.FusionT0.AddSeconds(300), f.Now);
        var d1 = f.Device(1)!;
        Assert.All(d1.Channels.Where(c => !c.NoData), c => Assert.False(double.IsNaN(c.TempC) || double.IsNaN(c.PressureMPa)));
        Assert.Null(d1.Reading(d1.Channel(7), DemoReading.TempC));                    // 停用的仍然没有读数
        Assert.True(d1.Channel(1)!.History[^1].T > DemoFleet.FusionT0);               // 照常追加历史
        Assert.Equal(22, d1.Progress(d1.Channel(1))!.StepRemainMin);                  // 27 min − 5 min
    }

    // ———————————————————— 设备 ————————————————————

    [Fact]
    public void Devices_MatchMock()
    {
        var f = Fleet();
        Assert.Equal(3, f.Devices.Count);
        var (d1, d2, d3) = (f.Device(1)!, f.Device(2)!, f.Device(3)!);
        Assert.Equal(("1号机", "PR8", "HT-PR8", 8), (d1.Alias, d1.ModelKey, d1.Model.Label, d1.Channels.Count));
        Assert.Equal(("2号机", "PR4", "HT-PR4", 4), (d2.Alias, d2.ModelKey, d2.Model.Label, d2.Channels.Count));
        Assert.Equal(("3号机", "PR16", "HT-PR16", 16), (d3.Alias, d3.ModelKey, d3.Model.Label, d3.Channels.Count));
        Assert.All(f.Devices, d => Assert.All(d.Channels, c => Assert.True(c.Installed)));
        Assert.Equal(new[] { "192.168.1.21", "192.168.1.22", "192.168.1.35" }, f.Devices.Select(d => d.Host));
        Assert.All(f.Devices, d => Assert.Equal(8080, d.Port));
        Assert.All(f.Devices, d => Assert.True(d.Online));
        Assert.Equal(new[] { LeaseStatus.Remote, LeaseStatus.Local, LeaseStatus.Local }, f.Devices.Select(d => d.Lease.State));
        Assert.Equal(DemoFleet.Client, d1.Lease.Holder);
        Assert.Equal(("PT-20261115-1305", At(13, 5), true), (d1.Session!.Id, d1.Session.StartAt, d1.Session.Active));
        Assert.Equal(("PT-20261115-1250", At(12, 50), true), (d2.Session!.Id, d2.Session.StartAt, d2.Session.Active));
        Assert.Null(d3.Session);
        Assert.Equal(new[] { "RV3 加料", "换气源 · 气体 A", "RV4 超压" }, d1.Session.Markers.Select(m => m.Text));
        Assert.Equal((6, 8, 7), (d1.Counts.Running, d1.Counts.Installed, d1.Counts.Enabled));
        Assert.Equal(3, d2.Counts.Running);
        Assert.Equal((11, 16), (d3.Counts.Running, d3.Counts.Installed));
    }

    [Fact]
    public void Stir_And_Gas()
    {
        var f = Fleet();
        var (d1, d2, d3) = (f.Device(1)!, f.Device(2)!, f.Device(3)!);
        Assert.Equal((true, 400.0), (d1.Stir!.On, d1.Stir.Rpm));
        Assert.Equal((true, 300.0), (d3.Stir!.On, d3.Stir.Rpm));
        // 每台一台电机经齿轮带动全部釜（2026-11-18 用户确认）：HT-PR4 也是整台共用，不再各釜独立
        Assert.Equal((true, 400.0), (d2.Stir!.On, d2.Stir.Rpm));
        Assert.All(d2.Channels, c => Assert.Equal(400, d2.RpmOf(c)));
        Assert.All(d2.Channels, c => Assert.False(c.StirOn));
        Assert.Equal(400, d1.Reading(d1.Channel(1), DemoReading.Rpm));
        Assert.Equal(300, d3.Reading(d3.Channel(1), DemoReading.Rpm));
        // 气路只写 惰性气体 / 气体 A / 气体 B（接什么气由工艺决定）：三台都是气体 A 开（样稿 inlets）
        var want = new[] { ("inert", false), ("a", true), ("b", false), ("vent", false) };
        foreach (var d in new[] { d1, d2, d3 })
        {
            Assert.Equal(want, d.Gas.Select(g => (g.Key, g.Value)));
            Assert.Equal(new[] { "惰性气体", "气体 A", "气体 B" }, d.Model.GasInlets.Select(g => g.Name));
        }
    }

    // ———————————————————— 反应釜 ————————————————————

    [Fact]
    public void Channels_StatesMapped()
    {
        var f = Fleet();
        StateKey[] want1 = { StateKey.React, StateKey.Heat, StateKey.React, StateKey.Alarm, StateKey.Ready, StateKey.React, StateKey.Idle, StateKey.Cool };
        Assert.Equal(want1, f.Device(1)!.Channels.Select(c => c.State));
        Assert.Equal(new[] { StateKey.React, StateKey.Heat, StateKey.Cool, StateKey.Done }, f.Device(2)!.Channels.Select(c => c.State));
        StateKey[] want3 =
        {
            StateKey.React, StateKey.React, StateKey.React, StateKey.React, StateKey.React, StateKey.React, StateKey.Heat, StateKey.Heat,
            StateKey.Press, StateKey.Press, StateKey.Alarm, StateKey.Done, StateKey.Ready, StateKey.Ready, StateKey.Idle, StateKey.Idle,
        };
        Assert.Equal(want3, f.Device(3)!.Channels.Select(c => c.State));
        Assert.Equal(ProgramStatus.Done, f.Device(2)!.Channel(4)!.Program!.Status);
        Assert.Equal(ProgramStatus.Held, f.Device(1)!.Channel(4)!.Program!.Status);
        Assert.Null(f.Device(1)!.Channel(5)!.Program);
    }

    [Fact]
    public void Channels_Readings_MatchMock()
    {
        var d = Fleet().Device(1)!;
        double? R(int n, DemoReading k) => d.Reading(d.Channel(n), k);
        Assert.Equal(new double?[] { 145.2, 150, 2.35, 2.50, 45, 400, 18.4, .21, 5 },
            new[] { DemoReading.TempC, DemoReading.SpC, DemoReading.PressureMPa, DemoReading.PTargetMPa, DemoReading.HeaterPct, DemoReading.Rpm, DemoReading.Mmol, DemoReading.Rate, DemoReading.VolumeMl }.Select(k => R(1, k)));
        Assert.Equal(new double?[] { 96.8, 120, 1.10, 2.50, 88, 2.1, .03 },
            new[] { DemoReading.TempC, DemoReading.SpC, DemoReading.PressureMPa, DemoReading.PTargetMPa, DemoReading.HeaterPct, DemoReading.Mmol, DemoReading.Rate }.Select(k => R(2, k)));
        Assert.Equal((158.4, 3.55, 2.50, 15.9), (R(4, DemoReading.TempC), R(4, DemoReading.PressureMPa), R(4, DemoReading.PTargetMPa), R(4, DemoReading.Mmol)));
        Assert.Null(R(4, DemoReading.Rate));
        Assert.Null(R(5, DemoReading.PTargetMPa));                                     // 目标压力 0 → 无
        Assert.Equal((25.6, 0.12, 0.0), (R(5, DemoReading.TempC), R(5, DemoReading.PressureMPa), R(5, DemoReading.Mmol)));
        Assert.Null(R(8, DemoReading.PTargetMPa));
        Assert.Equal((121.5, 40.0, 4.0), (R(8, DemoReading.TempC), R(8, DemoReading.SpC), R(8, DemoReading.VolumeMl)));
        Assert.Equal(new double?[] { 5, 5, 6, 5, 5, 5, null, 4 }, d.Channels.Select(c => d.Reading(c, DemoReading.VolumeMl)));
        // 进气阀：升压中，或在运行且在耗气
        Assert.Equal(new[] { true, true, true, false, false, true, false, false }, d.Channels.Select(c => c.Sv));
        Assert.Equal(new[] { true, true, true, true, false, true, false, false }, d.Channels.Select(c => c.Heating));
    }

    [Fact]
    public void Channels_Off_HaveNoReadings_UntilEnabled()
    {
        var f = Fleet();
        var d = f.Device(1)!;
        var c7 = d.Channel(7)!;
        Assert.True(c7.Installed);
        Assert.True(c7.NoData);
        Assert.Equal(StateKey.Idle, d.StateKeyOf(c7));
        foreach (var k in Enum.GetValues<DemoReading>()) Assert.Null(d.Reading(c7, k));
        Assert.Empty(c7.History);
        Assert.Null(c7.Program);
        var d3 = f.Device(3)!;
        Assert.All(new[] { 15, 16 }, n => Assert.Null(d3.Reading(d3.Channel(n), DemoReading.TempC)));
        Assert.True(d.Channels.Where(c => c.N != 7).All(c => !c.NoData));
    }

    [Fact]
    public void Channels_Dev2_Dev3_Values()
    {
        var f = Fleet();
        var (d2, d3) = (f.Device(2)!, f.Device(3)!);
        Assert.All(d2.Channels, c => Assert.Equal(20, d2.Reading(c, DemoReading.VolumeMl)));
        Assert.Equal((5.20, 5.20, 34.6, .31), (d2.Reading(d2.Channel(1), DemoReading.PressureMPa), d2.Reading(d2.Channel(1), DemoReading.PTargetMPa),
            d2.Reading(d2.Channel(1), DemoReading.Mmol), d2.Reading(d2.Channel(1), DemoReading.Rate)));
        Assert.True(d2.Channel(3)!.Cooling);                                           // HT-PR4 有冷却水
        Assert.False(f.Device(1)!.Channel(8)!.Cooling);
        Assert.Equal(new double?[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, null, null }, d3.Channels.Select(c => d3.Reading(c, DemoReading.VolumeMl)));
        // 样稿：gas = step ≥ 2 ? 4 + i × 0.37（1 位小数）: step ? 0.1 : 0；rate = 运行且 step ≥ 2 ? 0.04 + (i % 4) × 0.01 : 0
        Assert.Equal(new double?[] { 4.0, 4.4, 4.7, 5.1, 5.5, 5.8, .1, .1, 7.0, 7.3, 7.7, 8.1, 0, 0, null, null }, d3.Channels.Select(c => d3.Reading(c, DemoReading.Mmol)));
        Assert.Equal(new double?[] { .04, .05, .06, .07, .04, .05, null, null, .04, .05, null, null, null, null, null, null }, d3.Channels.Select(c => d3.Reading(c, DemoReading.Rate)));
        Assert.Equal((80.4, 3.12, 1.2), (d3.Reading(d3.Channel(11), DemoReading.TempC), d3.Reading(d3.Channel(11), DemoReading.PressureMPa), d3.Reading(d3.Channel(11), DemoReading.PTargetMPa)));
        Assert.Equal(9, d3.GasHolder);
        Assert.Null(f.Device(1)!.GasHolder);
        Assert.Empty(d3.GasQueue);
    }

    // ———————————————————— 程序 ————————————————————

    [Fact]
    public void Library_StepsAndEstimates()
    {
        var f = Fleet();
        var a = f.Device(1)!.ProgramById("A")!;
        Assert.Equal("平行合成程序 A", a.Name);
        Assert.Equal(new[] { "升温", "升压", "恒温反应", "降温" }, a.Steps.Select(s => s.Name));
        Assert.Equal(new[] { "升温", "升压", "恒温反应", "降温" }, a.Steps.Select(StepText.Name));
        Assert.Equal(new[] { StepKind.Heat, StepKind.Press, StepKind.React, StepKind.End }, a.Steps.Select(s => s.Type));
        Assert.Equal((150 - 25) / 3.5 + 15, a.Steps[0].EstMin, 9);
        Assert.Equal(new[] { 3.0, 45, 22 }, a.Steps.Skip(1).Select(s => s.EstMin));
        Assert.Equal(45, a.Steps[2].Minutes);
        Assert.Equal((2.5, "a"), (a.Steps[1].Target!.Value, a.Steps[1].Gas));
        Assert.Equal(new[] { "A", "H1", "LK", "OX" }, f.Device(1)!.Programs.Select(p => p.Id));

        var b = f.Device(2)!.ProgramById("B")!;
        Assert.Equal(("高压加氢-150", "a"), (b.Name, b.Steps[1].Gas));
        Assert.Equal("150 ℃ · 5.20 MPa 气体 A · 反应 120 min", f.Device(2)!.Programs[0].Note);
        Assert.Equal(new[] { (150 - 25) / 3.5 + 10, 3, 120, 22 }, b.Steps.Select(s => s.EstMin));
        Assert.Equal(new[] { "升温", "升压", "反应", "降温" }, b.Steps.Select(s => s.Name));

        var c = f.Device(3)!.ProgramById("C")!;
        Assert.Equal(("筛选-80 ℃", "a"), (c.Name, c.Steps[1].Gas));
        Assert.Equal(new[] { 55 / 3.5 + 5, 3, 60, 11 }, c.Steps.Select(s => s.EstMin));
    }

    /// <summary>样稿运行表：本步剩余（left）与预计结束（etaOf = left + 后面各步的估计时长，钟点 = 14:30 + 分钟）。</summary>
    [Theory]
    [InlineData(1, 1, 3, 27, "15:19")]
    [InlineData(1, 2, 1, 7, "15:47")]
    [InlineData(1, 3, 3, 21, "15:13")]
    [InlineData(1, 6, 3, 21, "15:13")]
    [InlineData(1, 8, 4, 9, "14:39")]
    [InlineData(2, 1, 3, 90, "16:22")]
    [InlineData(2, 2, 1, 8, "17:03")]
    [InlineData(2, 3, 4, 12, "14:42")]
    [InlineData(3, 1, 3, 58, "15:39")]
    [InlineData(3, 6, 3, 63, "15:44")]
    [InlineData(3, 7, 1, 6, "15:50")]
    [InlineData(3, 8, 1, 4, "15:48")]
    [InlineData(3, 9, 2, 3, "15:44")]
    [InlineData(3, 10, 2, 2, "15:43")]
    public void RunTable_RemainAndEta(int k, int n, int step, int remain, string eta)
    {
        var f = Fleet();
        var d = f.Device(k)!;
        var pr = d.Progress(d.Channel(n))!;
        Assert.Equal(ProgramStatus.Running, pr.Status);
        Assert.Equal((step, remain), (pr.K, pr.StepRemainMin));
        Assert.Equal(eta, Hm(f.Now.AddMinutes(pr.TotalRemainMin)));
        Assert.Equal("第 " + step + "/4 步 · 剩 " + remain + " min", d.ProgText(d.Channel(n)));
    }

    [Fact]
    public void RunTable_EarliestLatest_PerDevice()
    {
        var f = Fleet();
        (string, string) Span(DemoDevice d)
        {
            var etas = d.Channels.Select(c => d.Progress(c)).Where(p => p is { Status: ProgramStatus.Running }).Select(p => f.Now.AddMinutes(p!.TotalRemainMin)).ToList();
            return (Hm(etas.Min()), Hm(etas.Max()));
        }
        Assert.Equal(("14:39", "15:47"), Span(f.Device(1)!));
        Assert.Equal(("14:42", "17:03"), Span(f.Device(2)!));
        Assert.Equal(("15:39", "15:50"), Span(f.Device(3)!));
    }

    [Fact]
    public void RunTable_AlarmHeld_DoneAndProgress()
    {
        var f = Fleet();
        var d1 = f.Device(1)!;
        var pr4 = d1.Progress(d1.Channel(4))!;
        Assert.Equal((ProgramStatus.Held, 3), (pr4.Status, pr4.K));
        Assert.Equal("第 3/4 步 · 报警暂停", d1.ProgText(d1.Channel(4)));
        var d3 = f.Device(3)!;
        Assert.Equal((ProgramStatus.Held, 3), (d3.Progress(d3.Channel(11))!.Status, d3.Progress(d3.Channel(11))!.K));
        Assert.Equal("已结束 · 共 4 步", f.Device(2)!.ProgText(f.Device(2)!.Channel(4)));
        Assert.Equal("已结束 · 共 4 步", d3.ProgText(d3.Channel(12)));
        // 步内进度 = 样稿 f（全程进度按各步估计时长）
        var p1 = d1.Progress(d1.Channel(1))!;
        Assert.Equal((125 / 3.5 + 15 + 3 + 0.40 * 45) / (125 / 3.5 + 15 + 3 + 45 + 22), p1.Frac, 3);
        // 已完成的步骤在 Log 里、首尾相接
        var prog = d1.Channel(1)!.Program!;
        Assert.Equal(new[] { 0, 1 }, prog.Log.Select(l => l.I));
        Assert.Equal(prog.StepStartedAt, prog.Log[^1].T1);
        Assert.Equal(prog.StartedAt, prog.Log[0].T0);
    }

    // ———————————————————— 曲线 ————————————————————

    private static ChannelHistory Hist(DemoDevice d, int n)
    {
        var h = new DeviceHistory();
        h.SyncDemo(d);
        return h.Channel(n);
    }

    private static List<(DateTime T, double? V)> Ser(DemoDevice d, int n, HistoryField k) => Hist(d, n).Series(k, DemoFleet.FusionT0, 120).ToList();

    [Fact]
    public void History_Spans_1300_To_Now()
    {
        var f = Fleet();
        var d = f.Device(1)!;
        var h = d.Channel(1)!.History;
        Assert.Equal(At(13, 0), h[0].T);
        Assert.Equal(f.Now, h[^1].T);
        Assert.Equal(541 + 1, h.Count);                                                // 13:00:00–14:30:00 每 10 s + 「现在」
        Assert.Equal(145.2, h[^1].TempC);
        var t = Ser(d, 1, HistoryField.TempC);
        Assert.Equal(At(13, 0).ToUniversalTime(), t[0].T.ToUniversalTime());
        Assert.All(t, p => Assert.NotNull(p.V));                                        // 没有断开
        Assert.Equal(25, t[0].V!.Value, 0);                                            // 从室温开始
        Assert.InRange(Ser(d, 1, HistoryField.PressureMPa).Last(p => p.T < At(14, 0)).V!.Value, 2.2, 2.5);
        Assert.Equal(150, Ser(d, 1, HistoryField.SpC).Last().V);
        Assert.Equal(2.5, Ser(d, 1, HistoryField.PTargetMPa).Last().V);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 6)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    public void History_Uptake_Saturates(int k, int n)
    {
        var d = Fleet().Device(k)!;
        var g = Ser(d, n, HistoryField.Mmol).Select(p => p.V!.Value).ToList();
        double final = d.Reading(d.Channel(n), DemoReading.Mmol)!.Value;
        Assert.Equal(0, g[0]);
        Assert.Equal(final, g[^1], 6);
        for (int i = 1; i < g.Count; i++) Assert.True(g[i] >= g[i - 1] - 1e-9, $"RV{n} 吸气量在 {i} 处下降");
        // 饱和：前半段涨得多、最后 10 min 涨得少
        int last10 = g.Count - 61, mid = g.Count / 2;
        double early = g[mid + 60] - g[mid];
        double late = g[^1] - g[last10];
        Assert.True(late < early, $"RV{n}：最后 10 min 涨 {late}，中段 10 min 涨 {early}");
        Assert.True(g[mid] > 0.3 * final);
    }

    [Fact]
    public void History_Rv4_PressureJumpsAboveLimit_At1403()
    {
        var d = Fleet().Device(1)!;
        var p = Ser(d, 4, HistoryField.PressureMPa);
        double At_(DateTime t) => p.Last(x => x.T <= t).V!.Value;
        Assert.InRange(At_(At(13, 50)), 2.4, 2.6);
        Assert.InRange(At_(At(14, 0)), 2.4, 2.6);
        Assert.True(At_(At(14, 3, 20)) > 3.5);
        Assert.True(At_(At(14, 20)) > 3.5);
        var limit = d.Plant!.OverPressureMPa;
        var cross = p.First(x => x.V > limit).T;
        Assert.InRange(cross, At(14, 2, 40), At(14, 3, 20));
        // 吸气量在报警（63.3 min）后不再增加
        var g = Ser(d, 4, HistoryField.Mmol);
        Assert.Equal(15.9, g.Last(x => x.T <= At(14, 4)).V!.Value, 2);
    }

    [Fact]
    public void History_HeatAndCool_Shapes()
    {
        var d = Fleet().Device(1)!;
        var t2 = Ser(d, 2, HistoryField.TempC);
        Assert.InRange(t2.Last(x => x.T <= At(14, 9)).V!.Value, 24, 26);             // RV2 70 min 前还在室温
        Assert.InRange(t2.Last(x => x.T <= At(14, 29, 50)).V!.Value, 94, 98);
        var t8 = Ser(d, 8, HistoryField.TempC);
        Assert.InRange(t8.Last(x => x.T <= At(13, 50)).V!.Value, 149, 151);          // RV8 30–72 min 保持 150 ℃
        Assert.Empty(Hist(d, 7).Series(HistoryField.TempC, DemoFleet.FusionT0, 120));      // 停用：没有曲线
    }

    // ———————————————————— 报警 / 事件 ————————————————————

    [Fact]
    public void Alarms_MatchMock()
    {
        var f = Fleet();
        var a1 = f.Device(1)!.Alarms;
        Assert.Equal(2, a1.Count);
        Assert.Equal((AlarmLevel.Err, 4, "P-HIGH", "压力超上限 · 已关闭进气阀 · 程序暂停", At(14, 3, 18), false), (a1[0].Level, a1[0].Ch!.Value, a1[0].Code, a1[0].Text, a1[0].At, a1[0].Acked));
        Assert.Equal((AlarmLevel.Warn, 2, "HEAT-SLOW", "升温偏慢 · 第 1 步已用 18 min", At(14, 1, 30), false), (a1[1].Level, a1[1].Ch!.Value, a1[1].Code, a1[1].Text, a1[1].At, a1[1].Acked));
        Assert.Empty(f.Device(2)!.Alarms);
        var a3 = Assert.Single(f.Device(3)!.Alarms);
        Assert.Equal((AlarmLevel.Err, 11, "P-HIGH", "压力超上限 · 已关闭 SV11 · 程序暂停", At(13, 58, 42), false), (a3.Level, a3.Ch!.Value, a3.Code, a3.Text, a3.At, a3.Acked));
        Assert.Equal("报警 1 · 提醒 1", f.Device(1)!.AlarmText());
        Assert.Equal(DeviceHealth.Err, f.Device(1)!.Health);
        Assert.Equal(DeviceHealth.Ok, f.Device(2)!.Health);
    }

    [Fact]
    public void Alarms_ClearAndRestore()
    {
        var f = Fleet();
        f.Apply(DemoParams.Parse("fusion=1&freeze=1&alarm=1:0"));
        var d = f.Device(1)!;
        Assert.Empty(d.Alarms);
        Assert.Equal(StateKey.React, d.Channel(4)!.State);
        f.Apply(DemoParams.Parse("fusion=1&freeze=1"));
        Assert.Equal(new[] { "P-HIGH", "HEAT-SLOW" }, d.Alarms.Select(a => a.Code));
        Assert.Equal((StateKey.Alarm, 3.55, ProgramStatus.Held), (d.Channel(4)!.State, d.Channel(4)!.PressureMPa, d.Channel(4)!.Program!.Status));
    }

    [Fact]
    public void Events_MatchMock_NewestFirst()
    {
        var f = Fleet();
        var e1 = f.Device(1)!.Events;
        Assert.Equal(new[]
        {
            ("14:05:00", (int?)null, "控制权 · PC-测试工位1 获得远程控制", EventLevel.Op),
            ("14:03:18", 4, "RV4 压力超上限", EventLevel.Err),
            ("14:01:30", 2, "RV2 升温偏慢", EventLevel.Warn),
            ("13:58:00", 2, "RV2 程序「平行合成程序 A」开始", EventLevel.Info),
            ("13:41:30", 8, "RV8 进入第 4 步 降温", EventLevel.Info),
            ("13:05:00", null, "会话 · 会话 PT-20261115-1305 开始", EventLevel.Op),
        }, e1.Take(6).Select(e => (e.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.N, e.Text, e.Level)));
        Assert.All(e1, e => Assert.Equal(At(0, 0).Date, e.At.Date));
        Assert.True(e1.Zip(e1.Skip(1)).All(x => x.First.At >= x.Second.At));
        Assert.Equal(e1.Count, e1.Select(e => e.Id).Distinct().Count());
        var e2 = f.Device(2)!.Events;
        Assert.Equal(("13:30:12", "RV1 程序「高压加氢-150」开始"), (e2[0].At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), e2[0].Text));
        Assert.Equal(new[] { "RV11 压力超上限", "搅拌 · 搅拌电机故障", "泄漏测试 · 泄漏测试通过（16/16）" }, f.Device(3)!.Events.Select(e => e.Text));
        Assert.Equal(new[] { At(13, 58, 42), At(10, 5, 33), At(9, 12) }, f.Device(3)!.Events.Select(e => e.At));
    }

    // ———————————————————— 整机参数 ————————————————————

    [Fact]
    public void Plant_MatchesMock()
    {
        var f = Fleet();
        var (p1, p2, p3) = (f.Device(1)!.Plant!, f.Device(2)!.Plant!, f.Device(3)!.Plant!);
        Assert.Equal((3.50, 200.0, 450.0, 1.2, 10.0, 0.7, "keep", "hmi-01"), (p1.OverPressureMPa, p1.OverTempC, p1.RpmMax, p1.StirCurrentA, p1.VesselMl, p1.LineMl, p1.OnRemoteLost, p1.HostName));
        Assert.Equal((9.5, 250.0, 450.0, 1.2, 100.0, "keep"), (p2.OverPressureMPa, p2.OverTempC, p2.RpmMax, p2.StirCurrentA, p2.VesselMl, p2.OnRemoteLost));
        Assert.Equal((3.0, 200.0, 0.9, 25.0, "stopHeat"), (p3.OverPressureMPa, p3.OverTempC, p3.StirCurrentA, p3.VesselMl, p3.OnRemoteLost));
        Assert.All(new[] { p1, p2, p3 }, p => Assert.Equal(1, p.StirGearRatio));
        Assert.Equal(8, p1.EffectiveMl.Count);
        Assert.All(p1.EffectiveMl, Assert.Null);
        Assert.All(p2.EffectiveMl, Assert.Null);
        // 3号机 RV1–14 已标定：有效容积 = 25 + DEADV（1.6 + ((21 + 3n) % 9) / 10），RV15–16 未标定
        Assert.Equal(new double?[] { 27.2, 26.6, 26.9, 27.2, 26.6, 26.9, 27.2, 26.6, 26.9, 27.2, 26.6, 26.9, 27.2, 26.6, null, null }, p3.EffectiveMl);
        Assert.Equal(new DateTime(2026, 10, 28), p3.CalibratedAt[0]!.Value.Date);
        Assert.Null(p3.CalibratedAt[15]);
        Assert.Null(new DemoFleet(DemoParams.Parse("freeze=1")).Device(1)!.Plant);
    }

    // ———————————————————— 断线演示 ————————————————————

    [Fact]
    public void Offline_Device1_FrozenAt142841()
    {
        var f = Fleet("&offline=1");
        var d = f.Device(1)!;
        Assert.Equal(ConnStatus.Offline, d.Conn.State);
        Assert.True(d.Conn.EverOnline);
        Assert.Equal(At(14, 28, 41), d.Conn.LostAt);
        Assert.Equal(At(14, 28, 41), d.DataAt);
        Assert.Equal(4, d.Conn.Attempts);
        Assert.Equal(LeaseStatus.Remote, d.Lease.State);                               // 样稿断线横条上仍是「远程」
        Assert.True(f.Device(2)!.Online);
        Assert.True(f.Device(3)!.Online);
        // 读数停在断线前（不清零）
        Assert.Equal((145.2, 2.35, 18.4), (d.Reading(d.Channel(1), DemoReading.TempC), d.Reading(d.Channel(1), DemoReading.PressureMPa), d.Reading(d.Channel(1), DemoReading.Mmol)));
        Assert.Equal(StateKey.Offline, d.StateKeyOf(d.Channel(1)));
        Assert.Equal(StateKey.React, d.Channel(1)!.State);
        // 断线时刻的进度 = 样稿的值
        var pr = d.Progress(d.Channel(1))!;
        Assert.True(pr.Offline);
        Assert.Equal((3, 27, 49), (pr.K, pr.StepRemainMin, pr.TotalRemainMin));
        Assert.Equal(7, d.Progress(d.Channel(2))!.StepRemainMin);
        // 历史停在断线时刻
        Assert.Equal(At(14, 28, 41), d.Channel(1)!.History[^1].T);
        Assert.Equal(145.2, d.Channel(1)!.History[^1].TempC);
        Assert.All(d.Comm, m => Assert.False(m.Online));
        // 每秒推进：读数不动，重连一直失败、次数增加
        var g = new DemoFleet(DemoParams.Parse("fusion=1&offline=1"));
        var gd = g.Device(1)!;
        for (int i = 0; i < 30; i++) g.Tick();
        Assert.Equal(145.2, gd.Reading(gd.Channel(1), DemoReading.TempC));
        Assert.Equal(At(14, 28, 41), gd.DataAt);
        Assert.NotEqual(ConnStatus.Online, gd.Conn.State);
        Assert.True(gd.Conn.Attempts > 4);
        // 恢复：照常上线
        g.Apply(DemoParams.Parse("fusion=1&offline=none"));
        for (int i = 0; i < 20 && !gd.Online; i++) g.Tick();
        Assert.True(gd.Online);
    }

    [Fact]
    public void Default_Dataset_Unchanged()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        Assert.False(f.Fusion);
        Assert.Equal(DemoFleet.T0, f.Now);
        Assert.All(f.Devices, d => Assert.All(d.Channels, c => Assert.False(c.NoData)));
        Assert.All(f.Devices, d => Assert.All(d.Channels, c => Assert.Null(c.Rate)));
    }
}
