using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 遥测 → ReactorVm 的投影（PORT-PLAN §5 M2「ProjectionTests」；旧 ChannelVm.Apply 的去处）：实测样本 live-snapshot.json / ws-telemetry.jsonl
/// 与从 Contracts 记录造出的帧，经 Fleet/ChannelProjection（Core 的 StateMap + Units）喂给 ReactorVm —— psi → MPa 保留 3 位、heating → 升温中、
/// alarm + overPressure → 超压报警、断线「离线」/ 从未连上「未连接」，读数格的取舍同样稿 keyReading；就地更新（同一个实例、值不变不通知）。
/// 另外用演示数据（Core 的 DemoFleet）核对 DESIGN-BRIEF §8 的读数，用假连接核对实时源的整条路径。
/// </summary>
public class ProjectionTests
{
    private static ModelProfile Pr8 => ModelProfiles.Embedded.Get("PR8")!;

    private static TelemetrySnapshot Snap() => ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;

    private static TelemetrySnapshot With(TelemetrySnapshot f, int n, Func<ChannelSnap, ChannelSnap> change) =>
        f with { Channels = f.Channels.Select(c => c.Id == n ? change(c) : c).ToArray() };

    private static ReactorVm Project(TelemetrySnapshot? f, int n, bool online = true, bool ever = true, bool installed = true, ReactorVm? into = null)
    {
        var r = into ?? new ReactorVm(null, n);
        var rd = ChannelProjection.FromFrame(f, n, online, ever, installed);
        r.Apply(rd, Pr8, online, ever, "1号机", DeviceTexts.DataText(f?.Ts.ToLocalTime(), ever));
        return r;
    }

    [Fact]
    public void LiveSnapshot_PsiToMpa_ThreeDecimals()
    {
        var f = Snap();
        var r1 = Project(f, 1);
        Assert.Equal("1.477", r1.PressText);   // 214.171875 psi ÷ 145.037738
        Assert.False(r1.PressNa);
        Assert.Equal("24.9", r1.TempText);
        Assert.Equal(StateKey.Ready, r1.Key);
        Assert.Equal("已启用", r1.Word);
        Assert.Equal("MPa", r1.PressUnit);
        foreach (var ch in f.Channels)
        {
            var r = Project(f, ch.Id);
            Assert.Equal(Fmt.Mpa(ch.PressurePsi / Units.PsiPerMpa, 3), r.PressText);
            Assert.Equal(3, r.PressText.Length - r.PressText.IndexOf('.') - 1);
            Assert.Equal(ch.PressurePsi / Units.PsiPerMpa, r.PressureMPa);
        }
    }

    [Fact]
    public void LiveSnapshot_DoneAfterAbortedProgram_IsStopped()
    {
        var r2 = Project(Snap(), 2);
        Assert.Equal(StateKey.Stopped, r2.Key);
        Assert.Equal("已停止", r2.Word);
        Assert.Equal("aborted", r2.Hmi?.State);
        Assert.False(r2.IsRunning);
        var r3 = Project(Snap(), 3);
        Assert.Equal(StateKey.Idle, r3.Key);
        Assert.Equal("停用", r3.Word);
    }

    [Fact]
    public void WsTelemetry_UpdatesInPlace_AndOnlyNotifiesChanges()
    {
        var frames = Samples.Lines("ws-telemetry.jsonl").Select(l => ApiJson.Deserialize<TelemetrySnapshot>(l)!).ToList();
        Assert.True(frames.Count >= 2);
        var r = Project(frames[0], 1);
        var changed = new List<string>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        var same = Project(frames[0], 1, into: r);
        Assert.Same(r, same);
        Assert.Empty(changed);                              // 同一帧再来一次：不通知
        Project(frames[1], 1, into: r);
        Assert.Equal(Fmt.Mpa(frames[1].Channels[0].PressurePsi / Units.PsiPerMpa, 3), r.PressText);
        Assert.Contains(nameof(ReactorVm.PressText), changed);
        Assert.DoesNotContain(nameof(ReactorVm.Key), changed);   // 状态没变（ready）
    }

    [Fact]
    public void Heating_MapsToHeat_TemperatureColumnOnly()
    {
        var f = With(Snap(), 1, c => c with { State = ChannelStates.Heating, AtMode = 0, TempC = 86.4 });
        var r = Project(f, 1);
        Assert.Equal(StateKey.Heat, r.Key);
        Assert.Equal("升温中", r.Word);
        Assert.True(r.Heating);
        Assert.True(r.IsRunning);
        Assert.Equal("86.4", r.TempText);
        Assert.True(r.ShowTemp);
        Assert.False(r.ShowPress);                           // 样稿 keyReading：升温只写温度
    }

    [Fact]
    public void Pressing_ShowsPressureOnly()
    {
        var r = Project(With(Snap(), 1, c => c with { State = ChannelStates.Pressing }), 1);
        Assert.Equal(StateKey.Press, r.Key);
        Assert.Equal("升压中", r.Word);
        Assert.False(r.ShowTemp);
        Assert.True(r.ShowPress);
    }

    [Fact]
    public void AlarmOverPressure_IsOverPressureAlarm_WithNarrowTemperature()
    {
        var r = Project(With(Snap(), 1, c => c with { State = ChannelStates.Alarm, AlarmCause = "overPressure", TempC = 80.4 }), 1);
        Assert.Equal(StateKey.Alarm, r.Key);
        Assert.Equal("超压报警", r.Word);
        Assert.Equal(AlarmCause.OverPressure, r.Cause);
        Assert.True(r.IsAlarm);
        Assert.True(r.ShowTemp && r.NarrowTemp && r.ShowPress);   // 「80.4」4 位：窄温度格 + 压力
        var hot = Project(With(Snap(), 1, c => c with { State = ChannelStates.Alarm, AlarmCause = "overPressure", TempC = 119.8 }), 1);
        Assert.False(hot.ShowTemp);                                // 5 位放不下：只写压力（报警的量）
        Assert.True(hot.ShowPress);
        var temp = Project(With(Snap(), 1, c => c with { State = ChannelStates.Alarm, AlarmCause = "overTemp" }), 1);
        Assert.Equal("超温报警", temp.Word);
        var tripped = Project(With(Snap(), 1, c => c with { State = ChannelStates.Done, OverPressureTripped = true }), 1);
        Assert.Equal("超压报警", tripped.Word);                     // 切断未解除：仍是报警
    }

    [Fact]
    public void Offline_KeepsLastReadings_AndSaysOffline()
    {
        var r = Project(Snap(), 1, online: false, ever: true);
        Assert.Equal(StateKey.Offline, r.Key);
        Assert.Equal("离线", r.Word);
        Assert.Equal("离线", r.TreeWord);
        Assert.Equal(StateKey.Ready, r.Own);                // 断线前的状态
        Assert.Equal("24.9", r.TempText);                    // 最后的值
        Assert.Contains("1号机 · RV1 · 离线 · 断线前 已启用", r.Tip);
        Assert.Contains("数据停在", r.Tip);
    }

    [Fact]
    public void NeverConnected_SaysNotConnected_NoReadings()
    {
        var r = Project(null, 1, online: false, ever: false);
        Assert.Equal(StateKey.Offline, r.Key);
        Assert.Equal("未连接", r.Word);
        Assert.Equal("—", r.TreeWord);                       // 样稿 rvWord：从未连上写「—」
        Assert.True(r.TempNa && r.PressNa);
        Assert.Equal(Fmt.Dash, r.TempText);
        var online = Project(null, 1, online: true, ever: true);   // 连上了但还没有这一路的数据
        Assert.Equal("未连接", online.Word);
    }

    [Fact]
    public void Uninstalled_HasNoReadings()
    {
        var f = With(Snap(), 5, c => c with { Installed = false });
        var r = Project(f, 5);
        Assert.False(r.Installed);
        Assert.Equal(StateKey.Uninstalled, r.Key);
        Assert.False(r.ShowTemp || r.ShowPress);
        var hint = Project(null, 6, online: true, ever: true, installed: false);
        Assert.Equal(StateKey.Uninstalled, hint.Key);
    }

    [Fact]
    public void Demo_ReadingsMatchDesignBrief()
    {
        var fleet = FleetVm.CreateDemo(DemoParams.Parse("freeze=1"));
        var d1 = fleet.Device(1)!;
        Assert.Equal("1号机", d1.Alias);
        Assert.Equal("HT-PR8 · 现场", d1.ModelLabel);
        var rv1 = d1.Reactor(1)!;
        Assert.Equal(("119.8", "0.349", "反应中"), (rv1.TempText, rv1.PressText, rv1.Word));
        Assert.True(rv1.ShowTemp && rv1.ShowPress);
        var rv3 = d1.Reactor(3)!;
        Assert.Equal(("86.4", "升温中"), (rv3.TempText, rv3.Word));
        Assert.False(rv3.ShowPress);
        var rv4 = d1.Reactor(4)!;
        Assert.Equal(("0.217", "升压中"), (rv4.PressText, rv4.Word));
        Assert.False(rv4.ShowTemp);
        Assert.False(d1.Reactor(5)!.Installed);
        Assert.Equal("5.20", fleet.Device(2)!.Reactor(1)!.PressText);        // HT-PR4 量程 ≥ 5 MPa：2 位
        var rv11 = fleet.Device(3)!.Reactor(11)!;
        Assert.Equal(("超压报警", StateKey.Alarm), (rv11.Word, rv11.Key));
        Assert.Equal("运行 4/4", d1.RunText);
        Assert.Equal("运行 11/16", fleet.Device(3)!.RunText);
        Assert.Equal(AlarmMarker.Warn, d1.Marker);
        Assert.Equal(AlarmMarker.Err, fleet.Device(3)!.Marker);
        Assert.Equal("报警 1", fleet.Device(3)!.MarkerText);
        Assert.Equal("远程控制", d1.LeaseShort);
        Assert.Equal("本地只读", fleet.Device(2)!.LeaseShort);
        Assert.Contains("1号机 · RV1 · 反应中 · 119.8 ℃ · 0.349 MPa", rv1.Tip);
    }

    [Fact]
    public void Demo_ProgressAndSegments_SameAsCoreDemoDevice()
    {
        foreach (var q in new[] { "freeze=1", "devs=4&freeze=1", "stress=1&freeze=1" })
        {
            var fleet = FleetVm.CreateDemo(DemoParams.Parse(q));
            foreach (var d in fleet.Devices)
            {
                var dev = ((DemoDeviceSource)d.Source).Device;
                foreach (var r in d.Reactors.Where(x => x.Installed))
                {
                    var ch = dev.Channel(r.N);
                    Assert.Equal(dev.Segments(ch), d.Segments(r.N));
                    Assert.Equal(dev.ProgText(ch), r.Program?.ProgText ?? "无程序");
                    Assert.Equal(dev.ProgText(ch, shortForm: true), r.Program?.ProgShort ?? "无程序");
                    Assert.Equal(dev.StateKeyOf(ch), r.Key);
                    Assert.Equal(dev.StateWord(ch), r.Word);
                }
            }
        }
    }

    [Fact]
    public void Demo_OfflineDevice_FrozenReadingsAndDetailRow()
    {
        var fleet = FleetVm.CreateDemo(DemoParams.Parse("devs=4&freeze=1"));
        var d4 = fleet.Device(4)!;
        Assert.True(d4.IsOffline);
        Assert.Null(d4.RunText);
        Assert.StartsWith("重连中 ", d4.OffText);
        Assert.StartsWith("数据停在 ", d4.DataText);
        Assert.Equal("连接中断", d4.ConnShort);
        Assert.All(d4.Reactors.Where(r => r.Installed), r => Assert.Equal("离线", r.Word));
    }

    [Fact]
    public void Demo_Ticks_UpdateInPlace_FrozenDoesNot()
    {
        var live = FleetVm.CreateDemo(DemoParams.Parse(""));
        var d1 = live.Device(1)!;
        var rv3 = d1.Reactor(3)!;
        var t0 = live.Now;
        string temp0 = rv3.TempText;
        for (int i = 0; i < 30; i++) live.OnTick(advance: true);
        Assert.Equal(t0.AddSeconds(30), live.Now);
        Assert.Same(rv3, d1.Reactor(3));                                   // 同一个实例
        Assert.Same(d1, live.Device(1));
        Assert.NotEqual(temp0, rv3.TempText);                               // RV3 在升温：读数就地变了

        var frozen = FleetVm.CreateDemo(DemoParams.Parse("freeze=1"));
        var f0 = frozen.Device(1)!.Reactor(3)!.TempText;
        for (int i = 0; i < 5; i++) frozen.OnTick(advance: true);          // 冻结：时钟不走、读数不变
        Assert.Equal(Core.Demo.DemoFleet.T0, frozen.Now);
        Assert.Equal(f0, frozen.Device(1)!.Reactor(3)!.TempText);
        live.OnTick(advance: false);                                         // UiTicker 冻结时只渲染
        Assert.Equal(t0.AddSeconds(30), live.Now);
    }

    [Fact]
    public async Task LiveSource_FakeConnection_ProjectsFramesIntoReactorVm()
    {
        var post = new ManualPost();
        FakeReactorConnection? fake = null;
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "t");
        var src = new LiveDeviceSource(cfg, "PC-测试", post.Post, o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = Snap(),
            };
            return fake;
        });
        await src.ConnectAsync(CancellationToken.None);
        int changes = 0;
        src.Changed += _ => changes++;
        await Task.Delay(50);                                // 连上后的一次性读取（/settings、/alarms、/events：脚本里没有 → 404，忽略）
        post.Run();
        Assert.True(changes >= 1);
        Assert.Equal(ConnStatus.Online, src.Conn.State);
        Assert.Equal("PR8", src.Model.BaseKey);
        var r = new ReactorVm(null, 1);
        var rd = src.Read(1);
        r.Apply(rd, src.Model, true, true, "1号机", "");
        Assert.Equal(("1.477", StateKey.Ready), (r.PressText, r.Key));

        // 几帧连着来：只排一次刷新、只应用最后一帧
        var frames = Samples.Lines("ws-telemetry.jsonl").Select(l => ApiJson.Deserialize<TelemetrySnapshot>(l)!).ToList();
        foreach (var f in frames) fake!.RaiseTelemetry(f);
        Assert.Equal(1, post.Pending);
        changes = 0;
        post.Run();
        Assert.Equal(1, changes);
        Assert.Same(frames[^1], src.Latest);
        await src.DisposeAsync();
    }
}
