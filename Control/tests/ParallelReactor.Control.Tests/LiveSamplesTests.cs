using System;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Samples/live-*.json 与 ws-*.jsonl 是 ClientSmoke 对真 HMI（PR_SIM，api-phase3 6a6a76c）跑一遍时抓的 REST 响应与 WS 原帧：
/// 客户端对每一种都必须能反序列化，且关键字段与协议一致（§6.3 / §8）。
/// </summary>
public class LiveSamplesTests
{
    [Fact]
    public void Live_Device_Snapshot_Settings_Control()
    {
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        Assert.Equal("ParallelReactor-8", dev.Model);
        Assert.Equal(8, dev.ChannelCount);
        Assert.Equal(8, dev.Channels.Length);
        Assert.True(dev.Simulated);
        Assert.NotEmpty(dev.BootId);

        var snap = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        Assert.Equal("telemetry", snap.Type);
        Assert.Equal(8, snap.Channels.Length);
        Assert.All(snap.Channels, c => Assert.InRange(c.Id, 1, 8));
        Assert.True(snap.Channels[0].PressurePsi is > 100 and < 400);   // sim 恒 ~225 psi
        Assert.NotNull(snap.Stir);
        Assert.NotNull(snap.Programs);
        Assert.Equal(ApiJson.Serialize(snap), ApiJson.Serialize(ApiJson.Deserialize<TelemetrySnapshot>(ApiJson.Serialize(snap))!));

        var settings = ApiJson.Deserialize<SettingsDto>(Samples.Read("live-settings.json"))!;
        Assert.True(settings.Limits.TempSpMaxC > 0);
        Assert.True(settings.Alarms.OverPressurePsi > 0);
        Assert.NotEmpty(settings.TempBands);

        var ctl = ApiJson.Deserialize<ControlStateDto>(Samples.Read("live-control.json"))!;
        Assert.Equal(5, ctl.HeartbeatTimeoutSec);
        Assert.Equal(5, ctl.LeaseGraceSec);
        Assert.True(ctl.AutoGrant);
    }

    [Fact]
    public void Live_Events_Sessions_Alarms_Schema_Comm()
    {
        var ev = ApiJson.Deserialize<EventsPageDto>(Samples.Read("live-events_afterSeq_0_limit_20.json"))!;
        Assert.Equal(20, ev.Count);
        Assert.True(ev.More);
        Assert.Equal(ev.Items.Select(e => e.Seq).OrderBy(s => s), ev.Items.Select(e => e.Seq));
        Assert.Contains(ev.Items, e => e.Kind == "control.granted");
        Assert.Contains(ev.Items, e => e.Kind.StartsWith("op.") && e.Source == "remote" && e.OpId != null);

        var ss = ApiJson.Deserialize<SessionsDto>(Samples.Read("live-sessions.json"))!;
        Assert.True(ss.Count >= 1);
        Assert.Contains(ss.Items, s => s.TestId == "PT-SMOKE");

        var al = ApiJson.Deserialize<AlarmsDto>(Samples.Read("live-alarms.json"))!;
        Assert.Equal(al.Count, al.Items.Length);
        Assert.All(al.Items, a => Assert.False(string.IsNullOrEmpty(a.Id)));

        var schema = ApiJson.Deserialize<ProgramSchemaDto>(Samples.Read("live-program-schema.json"))!;
        Assert.Equal(new[] { "purge", "heat", "press", "stir", "wait", "react", "end" }, schema.StepTypes);
        var heat = schema.Steps.Single(s => s.Type == "heat");
        Assert.Contains(heat.Fields, f => f.Key == "mode" && f.Options != null && f.Options.Any(o => o.Code == "curve"));

        var comm = ApiJson.Deserialize<CommDto>(Samples.Read("live-device_comm.json"))!;
        Assert.NotNull(comm.Api);
        Assert.True(comm.Api.MaxClients >= 1);
        Assert.NotNull(comm.Tick);
    }

    [Fact]
    public void Live_WsFrames_AllTypes()
    {
        var hello = ApiJson.Deserialize<HelloMsg>(Samples.Lines("ws-hello.jsonl")[0])!;
        Assert.Equal("hello", hello.Type);
        Assert.Equal(5000, hello.EventRingSize);
        Assert.Equal("local", hello.Control.Control);
        Assert.Equal(1, hello.SeqEpoch);

        var frames = Samples.Lines("ws-telemetry.jsonl").Select(l => ApiJson.Deserialize<TelemetrySnapshot>(l)!).ToList();
        Assert.True(frames.Count >= 2);
        Assert.True(frames[1].Seq > frames[0].Seq);
        Assert.True(frames[1].Ts > frames[0].Ts);
        Assert.Equal(DateTimeKind.Utc, frames[0].Ts.Kind);

        var events = Samples.Lines("ws-event.jsonl").Select(l => ApiJson.Deserialize<EventMsg>(l)!).ToList();
        Assert.All(events, e => Assert.Equal("event", e.Type));
        Assert.Equal(events.Select(e => e.Seq).OrderBy(s => s), events.Select(e => e.Seq));

        var sub = ApiJson.Deserialize<SubscribedMsg>(Samples.Lines("ws-subscribed.jsonl")[0])!;
        Assert.Equal(new[] { "telemetry", "events", "alarms" }, sub.Topics);

        var ctl = Samples.Lines("live-ws-control.jsonl").Select(l => ApiJson.Deserialize<ControlMsg>(l)!).ToList();
        Assert.Contains(ctl, c => c.Event == "granted" && c.Mode == "remote" && c.Holder == "PC-测试工位1");
        Assert.All(ctl, c => Assert.Null(c.LeaseId));
        Assert.Contains(ctl, c => c.Event == "released" && c.Mode == "local");

        var ops = Samples.Lines("live-ws-opevent.jsonl").Select(l => ApiJson.Deserialize<OpEventMsg>(l)!).ToList();
        Assert.Contains(ops, o => o.Action == "heat.start" && o.HttpStatus == 202);
        Assert.Contains(ops, o => o.Action == "temp.setpoint" && o.HttpStatus == 200 && o.Status == "confirmed" && o.Code == "OK");
        Assert.All(ops, o => Assert.Equal("remote", o.Source));
        Assert.All(ops, o => Assert.False(string.IsNullOrEmpty(o.OpId)));
    }
}
