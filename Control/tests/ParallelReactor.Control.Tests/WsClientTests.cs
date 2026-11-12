using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>WS 客户端：hello/subscribe、seq 守卫 + resync、gap → REST 补齐、1008 重连续传、hb over WS、pong 超时。进程内假传输。</summary>
public class WsClientTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly ScriptedHttpHandler Http = new();
        public readonly FakeWsServer Server = new();
        public readonly ClientOptions Opt;
        public readonly ReactorApiClient Rest;
        public readonly TelemetryWsClient Ws;
        public readonly List<EventMsg> Events = new();
        public readonly List<TelemetrySnapshot> Frames = new();
        public readonly List<ControlMsg> Controls = new();
        public readonly List<ConnectionState> States = new();
        public readonly List<GapMsg> Gaps = new();
        public readonly List<Exception> Errors = new();
        public readonly CancellationTokenSource Cts = new();
        public Task? Run;

        public Rig()
        {
            Opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC-测试工位1");
            Rest = new ReactorApiClient(Opt, Http, Clock);
            Ws = new TelemetryWsClient(Opt, Rest, Clock, Server.Next);
            Ws.EventReceived += e => { lock (Events) Events.Add(e); };
            Ws.TelemetryReceived += f => { lock (Frames) Frames.Add(f); };
            Ws.ControlReceived += c => { lock (Controls) Controls.Add(c); };
            Ws.StateChanged += s => { lock (States) States.Add(s); };
            Ws.GapDetected += g => { lock (Gaps) Gaps.Add(g); };
            Ws.Error += e => { lock (Errors) Errors.Add(e); };
            // 假服务端：subscribe → subscribed（含 resumeAfterEventSeq 回放由测试自己 Push）
            Server.OnConnect = t => t.OnSent = (tr, msg) =>
            {
                var type = msg.GetProperty("type").GetString();
                if (type == "subscribe")
                    tr.Push(new SubscribedMsg("subscribed", new[] { "telemetry", "events", "alarms" }, 0, 10));
            };
        }

        public void Start() => Run = Task.Run(() => Ws.RunAsync(Cts.Token));

        public async Task<FakeWsTransport> ConnectedAsync(int index = 0)
        {
            await FakeClock.WaitUntil(() => Server.Count > index && Server[index].Connected, what: $"第 {index + 1} 次连接");
            return Server[index];
        }

        public static string Hello(string boot = "boot1", long head = 10, int epoch = 1)
            => ApiJson.Serialize(new HelloMsg("hello", "1.0", DateTime.UtcNow, null, boot, true, epoch, head, 50, new ModeSnap("local", null), 5000));

        public static string Event(long seq, string kind = "test", int epoch = 1)
            => ApiJson.Serialize(new EventMsg("event", seq, epoch, DateTime.UtcNow, "2026-11-07 14:00:00.000", "info", kind, null, "system", "系统", null, null, "t" + seq, ""));

        public static EventRecord Record(long seq) => new(seq, 1, DateTime.UtcNow, "x", "info", "rest", null, "system", "系统", null, null, "r" + seq, "");

        public int EventCount { get { lock (Events) return Events.Count; } }
        public long[] Seqs { get { lock (Events) return Events.Select(e => e.Seq).ToArray(); } }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            foreach (var t in Server.Transports) t.ServerClose();
            if (Run != null) { try { await Run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
            Rest.Dispose();
        }
    }

    [Fact]
    public async Task Hello_Subscribe_NoReplayFirstTime_ThenTelemetryAndEvents()
    {
        await using var r = new Rig();
        r.Start();
        var t = await r.ConnectedAsync();
        Assert.Equal("Bearer optoken", t.Headers!["Authorization"]);
        Assert.Equal("ws://127.0.0.1:1/api/v1/ws", t.Uri!.ToString());
        t.Push(Rig.Hello(head: 10));
        await FakeClock.WaitUntil(() => t.CountSent("subscribe") == 1, what: "subscribe");
        var sub = t.LastSent("subscribe")!.Value;
        Assert.False(sub.TryGetProperty("resumeAfterEventSeq", out _));   // 首次不回放历史
        Assert.Equal(10, r.Ws.LastEventSeq);
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        Assert.Equal("boot1", r.Ws.BootId);

        t.Push(ApiJson.Serialize(ClientRestTests.Frame(51)));
        t.Push(Rig.Event(11));
        t.Push(Rig.Event(12));
        t.Push(Rig.Event(12));   // 重复
        await FakeClock.WaitUntil(() => r.EventCount == 2, what: "两条事件");
        Assert.Equal(new long[] { 11, 12 }, r.Seqs);
        Assert.Single(r.Frames);
        Assert.Equal(1, r.Ws.MessageCounts["eventDup"]);
        Assert.Equal(12, r.Ws.LastEventSeq);
    }

    [Fact]
    public async Task SeqJump_SendsResync_DropsOutOfOrder_AcceptsReplay()
    {
        await using var r = new Rig();
        r.Start();
        var t = await r.ConnectedAsync();
        t.Push(Rig.Hello(head: 10));
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        t.Push(Rig.Event(11));
        t.Push(Rig.Event(14));   // 跳过 12、13
        await FakeClock.WaitUntil(() => t.CountSent("resync") == 1, what: "resync");
        Assert.Equal(11, t.LastSent("resync")!.Value.GetProperty("afterSeq").GetInt64());
        Assert.Equal(new long[] { 11 }, r.Seqs);
        t.Push(Rig.Event(15));   // 还没 resynced：再跳号只丢弃，不重复发 resync
        await Task.Delay(30);
        Assert.Equal(1, t.CountSent("resync"));
        // 服务端按序补发 12..15
        foreach (var s in new long[] { 12, 13, 14, 15 }) t.Push(Rig.Event(s));
        t.Push(ApiJson.Serialize(new SubscribedMsg("resynced", Array.Empty<string>(), 4, 15)));
        await FakeClock.WaitUntil(() => r.EventCount == 5, what: "补齐");
        Assert.Equal(new long[] { 11, 12, 13, 14, 15 }, r.Seqs);
        Assert.Equal(15, r.Ws.LastEventSeq);
    }

    [Fact]
    public async Task Gap_RestPagesEvents_Snapshot_Resubscribe()
    {
        await using var r = new Rig();
        // REST：两页事件 + 快照
        r.Http.On("GET", @"/events\?afterSeq=11&limit=1000", _ => ScriptedHttpHandler.Ok(new EventsPageDto(1, 2000, 11, 2, true, new[] { Rig.Record(12), Rig.Record(13) })));
        r.Http.On("GET", @"/events\?afterSeq=13&limit=1000", _ => ScriptedHttpHandler.Ok(new EventsPageDto(1, 2000, 13, 1, false, new[] { Rig.Record(2000) })));
        r.Http.On("GET", "/snapshot", _ => ScriptedHttpHandler.Ok(ClientRestTests.Frame(999)));
        r.Start();
        var t = await r.ConnectedAsync();
        t.Push(Rig.Hello(head: 10));
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        t.Push(Rig.Event(11));
        await FakeClock.WaitUntil(() => r.EventCount == 1);
        t.Push(ApiJson.Serialize(new GapMsg("gap", "gap_too_large", 2000, 1500)));
        await FakeClock.WaitUntil(() => t.CountSent("subscribe") == 2, what: "重订阅");
        Assert.Single(r.Gaps);
        Assert.Equal(new long[] { 11, 12, 13, 2000 }, r.Seqs);
        Assert.Equal(2000, r.Ws.LastEventSeq);
        Assert.Contains(r.Frames, f => f.Seq == 999);
        var sub = t.LastSent("subscribe")!.Value;
        Assert.Equal(2000, sub.GetProperty("resumeAfterEventSeq").GetInt64());
        Assert.Equal(1, sub.GetProperty("seqEpoch").GetInt32());
        Assert.Contains(ConnectionState.Resyncing, r.States);
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task Close1008_Reconnects_WithBackoff_ResumesAfterLastSeq()
    {
        await using var r = new Rig();
        r.Start();
        var t1 = await r.ConnectedAsync(0);
        t1.Push(Rig.Hello(head: 10));
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        t1.Push(Rig.Event(11));
        t1.Push(Rig.Event(12));
        await FakeClock.WaitUntil(() => r.EventCount == 2);
        t1.ServerClose(WebSocketCloseStatus.PolicyViolation);   // 慢消费者 1008
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Reconnecting, what: "Reconnecting");
        Assert.Equal(1, r.Server.Count);   // 退避期间还没重连
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);   // Reconnecting 之后 ping 定时器已取消，剩下的是退避
        r.Clock.Advance(TimeSpan.FromSeconds(1));   // ≥ 0.5 s × 1.2
        var t2 = await r.ConnectedAsync(1);
        t2.Push(Rig.Hello(head: 20));
        await FakeClock.WaitUntil(() => t2.CountSent("subscribe") == 1, what: "重连 subscribe");
        var sub = t2.LastSent("subscribe")!.Value;
        Assert.Equal(12, sub.GetProperty("resumeAfterEventSeq").GetInt64());
        Assert.Equal(1, sub.GetProperty("seqEpoch").GetInt32());
        Assert.Equal(1, r.Ws.ReconnectCount);
        Assert.Equal(1, r.Ws.MessageCounts["close1008"]);
        // 回放 13、14 后继续
        t2.Push(Rig.Event(13));
        await FakeClock.WaitUntil(() => r.EventCount == 3);
        Assert.Equal(13, r.Ws.LastEventSeq);
    }

    [Fact]
    public async Task ConnectFailure_Backoff_Doubles_UpToMax()
    {
        await using var r = new Rig();
        r.Server.OnConnect = t => t.FailConnect = true;
        r.Start();
        await FakeClock.WaitUntil(() => r.Server.Count == 1 && r.Clock.Pending == 1, what: "首次失败后退避");
        var delays = new List<TimeSpan>();
        for (int i = 0; i < 6; i++)
        {
            // 退避时长 = 0.5·2^i（±20%），上限 10 s：推进 13 s 一定越过
            var before = r.Server.Count;
            r.Clock.Advance(TimeSpan.FromSeconds(13));
            await FakeClock.WaitUntil(() => r.Server.Count == before + 1 && r.Clock.Pending == 1, what: $"第 {i + 2} 次重试");
        }
        Assert.Equal(7, r.Server.Count);
        Assert.True(r.Errors.Count >= 7);
        Assert.Equal(ConnectionState.Reconnecting, r.Ws.State);
    }

    [Fact]
    public async Task Heartbeat_OverWs_OnlyWhenConnected()
    {
        await using var r = new Rig();
        Assert.False(await r.Ws.SendHeartbeatAsync("L1", CancellationToken.None));
        r.Start();
        var t = await r.ConnectedAsync();
        Assert.False(await r.Ws.SendHeartbeatAsync("L1", CancellationToken.None));   // 还没 hello
        t.Push(Rig.Hello());
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        Assert.True(await r.Ws.SendHeartbeatAsync("L1", CancellationToken.None));
        var hb = t.LastSent("hb")!.Value;
        Assert.Equal("L1", hb.GetProperty("lease").GetString());
        // 服务端对无效租约回 error
        var errors = new List<WsErrorMsg>();
        r.Ws.ErrorReceived += e => errors.Add(e);
        t.Push(ApiJson.Serialize(new WsErrorMsg("error", "CONTROL_LEASE_EXPIRED", "租约已过期")));
        await FakeClock.WaitUntil(() => errors.Count == 1);
    }

    [Fact]
    public async Task Ping_Pong_Timeout_ClosesAndReconnects()
    {
        await using var r = new Rig();
        r.Start();
        var t = await r.ConnectedAsync();
        t.Push(Rig.Hello());
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1, what: "ping 定时器");
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        await FakeClock.WaitUntil(() => t.CountSent("ping") == 1, what: "ping");
        t.Push(ApiJson.Serialize(new PongMsg("pong", DateTime.UtcNow)));
        await FakeClock.WaitUntil(() => r.Ws.MessageCounts.TryGetValue("pong", out var n) && n == 1);
        // 之后不再回 pong：5 s 后 ping#2，10 s 后 ping#3 时距上次 pong 已 10 s → 关闭
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        await FakeClock.WaitUntil(() => t.CountSent("ping") == 2);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);
        r.Clock.Advance(TimeSpan.FromSeconds(5.1));
        await FakeClock.WaitUntil(() => t.Closed, what: "pong 超时关闭");
        Assert.Equal(1, r.Ws.MessageCounts["pongTimeout"]);
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Reconnecting);
    }

    [Fact]
    public async Task ServerRestart_BootIdChange_Raises()
    {
        await using var r = new Rig();
        var restarts = new List<(string, string)>();
        r.Ws.ServerRestarted += (a, b) => restarts.Add((a, b));
        r.Start();
        var t1 = await r.ConnectedAsync(0);
        t1.Push(Rig.Hello("bootA", head: 10, epoch: 1));
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Connected);
        t1.ServerClose();
        await FakeClock.WaitUntil(() => r.Ws.State == ConnectionState.Reconnecting);
        await FakeClock.WaitUntil(() => r.Clock.Pending >= 1);   // 此时 ping 定时器已取消，剩下的是退避
        r.Clock.Advance(TimeSpan.FromSeconds(1));
        var t2 = await r.ConnectedAsync(1);
        t2.Push(Rig.Hello("bootB", head: 3, epoch: 2));
        await FakeClock.WaitUntil(() => restarts.Count == 1, what: "ServerRestarted");
        Assert.Equal(("bootA", "bootB"), restarts[0]);
        await FakeClock.WaitUntil(() => t2.CountSent("subscribe") == 1);
        Assert.Equal(0, t2.LastSent("subscribe")!.Value.GetProperty("resumeAfterEventSeq").GetInt64());   // epoch 变了：清 seq
        Assert.Equal(2, r.Ws.SeqEpoch);
    }

    [Fact]
    public async Task Connection_Facade_WiresControlToLease_AndQueue()
    {
        var clock = new FakeClock();
        var http = new ScriptedHttpHandler();
        var server = new FakeWsServer();
        server.OnConnect = t => t.OnSent = (tr, m) => { if (m.GetProperty("type").GetString() == "subscribe") tr.Push(new SubscribedMsg("subscribed", new[] { "telemetry" }, 0, 10)); };
        var opt = new ClientOptions(new Uri("http://127.0.0.1:1"), "optoken", "PC-测试工位1") { AutoReacquireLease = false, MinWriteGap = TimeSpan.Zero };
        http.On("GET", "/device", _ => ScriptedHttpHandler.Ok(new DeviceInfoDto("m", null, "1", "1.0", 8, Array.Empty<ChannelInstalledDto>(), new[] { 1, 2, 3, 4 },
            new HardwareDto(new(false, null, null, null), new(false, null, null, null), new(false, null, null, null), new(false, null, null, null)), new UnitsDto("C", "psi", "rpm"), true, false, DateTime.UtcNow, "b", null, 10, 1)));
        http.On("GET", "/control", _ => ScriptedHttpHandler.Ok(new ControlStateDto("local", null, null, "local", null, null, null, true, 10, 60, 60, "keep")));
        http.On("GET", "/snapshot", _ => ScriptedHttpHandler.Ok(ClientRestTests.Frame(1)));
        http.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq", "granted", null, "LZ", clock.UtcNow.AddSeconds(10), 10, 60, "k")));
        await using var conn = new ReactorConnection(opt, http, clock, server.Next);
        var leaseEvents = new List<LeaseTransition>();
        conn.LeaseTransitioned += leaseEvents.Add;
        await conn.StartAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3, 4 }, conn.Device!.InstalledChannels);
        Assert.Equal(1, conn.Telemetry.Latest!.Seq);
        await FakeClock.WaitUntil(() => server.Count == 1 && server[0].Connected);
        server[0].Push(Rig.Hello());
        await FakeClock.WaitUntil(() => conn.Ws.State == ConnectionState.Connected);
        Assert.True(await conn.Lease.AcquireAsync(CancellationToken.None));
        // 持租约时心跳走 WS。假时钟上同时挂着别的等待（WS 客户端自己的定时），心跳循环的第一个 Delay 什么时候登记上不确定：
        // 以前「等到有任意一个等待就推进 4 s」—— 推进时心跳的 Delay 若还没登记，它就按推进后的时刻再等一整个间隔，hb 永远等不到（偶发失败，
        // 单独跑时必失败）。改成小步推进、每步让续体跑一下，直到 WS 上出现 hb（上限 20 × 0.5 s，远大于心跳间隔）。
        for (int i = 0; i < 20 && server[0].CountSent("hb") == 0; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(20);
        }
        await FakeClock.WaitUntil(() => server[0].CountSent("hb") == 1, what: "WS hb");
        Assert.Equal("LZ", server[0].LastSent("hb")!.Value.GetProperty("lease").GetString());
        // WS control: expired → 租约 None；消息入队
        server[0].Push(ApiJson.Serialize(new ControlMsg("control", "expired", "local", null, "local", null, "revoked", null, null, 10, "宽限期满", DateTime.UtcNow)));
        await FakeClock.WaitUntil(() => conn.Lease.State == LeaseState.None, what: "expired → None");
        Assert.Contains(leaseEvents, t => t.Reason == "expired");
        server[0].Push(ApiJson.Serialize(ClientRestTests.Frame(2)));
        await FakeClock.WaitUntil(() => conn.Telemetry.Count == 2);
        var queued = new List<object>();
        while (conn.Messages.TryRead(out var m)) queued.Add(m);
        Assert.Contains(queued, m => m is ControlMsg);
        Assert.Contains(queued, m => m is TelemetrySnapshot s && s.Seq == 2);
        Assert.Equal(2, conn.Telemetry.Snapshot().Length);
    }
}
