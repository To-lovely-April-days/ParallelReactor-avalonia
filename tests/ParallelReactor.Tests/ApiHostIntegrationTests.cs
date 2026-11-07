using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>IApiSources 的假实现：定时发布固定形状的快照、内存事件存储（临时目录）。</summary>
public sealed class FakeApiSources : IApiSources, IDisposable
{
    private readonly Timer? _timer;
    private long _seq;
    public FakeApiSources(string dataDir, TimeSpan? period = null)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Events = new EventStore(dataDir);
        Snapshots = new SnapshotStore();
        Lease = new ParallelReactor.Commands.ControlLease(Arbiter, () => Clock);
        if (period is { } p) _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, p);
    }
    public void Tick() => Snapshots.Publish(TelemetrySnapshotJsonTests.Sample(Interlocked.Increment(ref _seq)));
    public SnapshotStore Snapshots { get; }
    public EventStore Events { get; }
    public string DataDir { get; }
    public bool Simulated => true;
    public string BootId => "test0001";
    public DateTime BootTime { get; } = DateTime.UtcNow;
    public bool? ClockSynced => null;
    public ModeSnap Control => new(Arbiter.Mode == ParallelReactor.Commands.ControlMode.Remote ? "remote" : "local",
        Arbiter.Mode == ParallelReactor.Commands.ControlMode.Remote ? Arbiter.HolderName : null);
    public List<string> Audits { get; } = new();
    public int PreflightDelayMs { get; set; }
    // —— P3：仲裁 / 租约（可注入时钟）/ 脚本化远程命令 ——
    public ParallelReactor.Commands.ControlArbiter Arbiter { get; } = new();
    public TimeSpan Clock = TimeSpan.FromSeconds(1000);
    public ParallelReactor.Commands.ControlLease Lease { get; }
    public TimeSpan MonoNow => Clock;
    public FakeRemoteCommands Fake { get; } = new();
    public IRemoteCommands Commands => Fake;
    public SessionManifest? CurrentSession { get; set; }
    public List<EventRecord> OpEvents { get; } = new();
    public long Audit(string level, string title, string detail, string source, string? client, string? opId, string kind)
    {
        var rec = Events.Append(DateTimeOffset.Now, level, "接口", title, detail, source, client, opId, kind);
        lock (Audits) { Audits.Add($"{level}|{title}|{detail}|{kind}"); OpEvents.Add(rec); }
        return rec.Seq;
    }
    public DeviceInfoDto Device(ApiConfig cfg) => new("ParallelReactor-8", cfg.Serial, "1.0.0+test", "1.0", 8,
        Enumerable.Range(1, 8).Select(i => new ChannelInstalledDto(i, i <= 4)).ToArray(), new[] { 1, 2, 3, 4 },
        new HardwareDto(new(true, "sim", 19200, null), new(true, "sim", 9600, 4.137), new(true, "sim", 38400, null), new(true, "sim", 9600, null)),
        new UnitsDto("C", "psi", "rpm"), true, false, BootTime, BootId, null, Events.Head, Events.SeqEpoch);
    public ProgramSchemaDto ProgramSchema() => new("1.0", 0, new[] { "purge" }, new[] { new StepSchemaDto("purge", "吹扫", "PURGE", "", Array.Empty<FieldSchemaDto>()) });
    public async Task<PreflightDto> PreflightAsync(string clientName, CancellationToken ct)
    {
        if (PreflightDelayMs > 0) { await Task.Delay(PreflightDelayMs, ct); throw new UiThreadTimeoutException(); }
        return new PreflightDto(true, "检查通过", new[] { new PreflightCheckDto("ok", "急停", "软件急停未触发") }, new[] { 1 });
    }
    public void Audit(string level, string title, string detail) { lock (Audits) Audits.Add($"{level}|{title}|{detail}"); }
    public void Dispose() { _timer?.Dispose(); Events.Stop(); }
}

/// <summary>进程内启动 Kestrel（随机端口），验证鉴权、白名单、REST 形状、WebSocket hello / telemetry / 重同步与慢客户端丢帧。</summary>
public class ApiHostIntegrationTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "pr-api-it-" + Guid.NewGuid().ToString("N"));
    private const string Token = "devtoken";

    private static ApiConfig Cfg(params string[] cidrs) => new()
    {
        Enabled = true, Bind = "127.0.0.1", HttpPort = 0, MaxClients = 2, Serial = "PR8-TEST",
        AllowedCidrs = cidrs.ToList(),
        Tokens = new List<ApiToken> { new() { Name = "it", Sha256 = TokenAuth.Hash(Token), Role = "viewer" } },
    };

    private static HttpClient Client(ApiHost host, bool auth = true)
    {
        var c = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        if (auth) c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return c;
    }

    private static async Task<(ApiHost Host, FakeApiSources Src)> StartAsync(ApiConfig cfg, TimeSpan? tick = null)
    {
        var src = new FakeApiSources(Tmp(), tick ?? TimeSpan.FromMilliseconds(100));
        var host = new ApiHost(src, cfg);
        Assert.True(await host.StartOnceAsync(), string.Join("\n", src.Audits));
        Assert.True(host.Port > 0);
        return (host, src);
    }

    [Fact]
    public async Task Health_NoAuth_Snapshot_RequiresAuth()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            using var anon = Client(host, auth: false);
            var h = await anon.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, h.StatusCode);
            using var hdoc = JsonDocument.Parse(await h.Content.ReadAsStringAsync());
            Assert.True(hdoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("test0001", hdoc.RootElement.GetProperty("bootId").GetString());

            var r = await anon.GetAsync("/api/v1/snapshot");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("Bearer", r.Headers.WwwAuthenticate.ToString());
            using var edoc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("UNAUTHORIZED", edoc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(src.Audits, a => a.Contains("鉴权失败"));
            Assert.Equal(1, host.Stats.AuthFailures);

            // ?access_token= 不支持（§4.2）
            var q = await anon.GetAsync("/api/v1/snapshot?access_token=" + Token);
            Assert.Equal(HttpStatusCode.Unauthorized, q.StatusCode);

            using var c = Client(host);
            await Task.Delay(250);   // 等第一帧
            var s = await c.GetAsync("/api/v1/snapshot");
            Assert.Equal(HttpStatusCode.OK, s.StatusCode);
            Assert.Equal("application/json", s.Content.Headers.ContentType!.MediaType);
            using var sdoc = JsonDocument.Parse(await s.Content.ReadAsStringAsync());
            Assert.Equal("telemetry", sdoc.RootElement.GetProperty("type").GetString());
            Assert.Equal(8, sdoc.RootElement.GetProperty("channels").GetArrayLength());

            var d = await c.GetStringAsync("/api/v1/device");
            using var ddoc = JsonDocument.Parse(d);
            Assert.Equal("PR8-TEST", ddoc.RootElement.GetProperty("serial").GetString());
            Assert.Equal(4, ddoc.RootElement.GetProperty("installedChannels").GetArrayLength());

            var ch = await c.GetAsync("/api/v1/channels/9");
            Assert.Equal(HttpStatusCode.NotFound, ch.StatusCode);
            using var cdoc = JsonDocument.Parse(await ch.Content.ReadAsStringAsync());
            Assert.Equal("CHANNEL_NOT_FOUND", cdoc.RootElement.GetProperty("error").GetProperty("code").GetString());

            var ch1 = await c.GetStringAsync("/api/v1/channels/1");
            using var c1doc = JsonDocument.Parse(ch1);
            Assert.Equal(JsonValueKind.Object, c1doc.RootElement.GetProperty("channel").GetProperty("profile").ValueKind);   // 详情总带曲线

            var pf = await c.GetStringAsync("/api/v1/preflight");
            Assert.Contains("\"canRun\":true", pf);

            var comm = await c.GetStringAsync("/api/v1/device/comm");
            using var mdoc = JsonDocument.Parse(comm);
            Assert.Equal(1200, mdoc.RootElement.GetProperty("tick").GetProperty("intervalMs").GetDouble());
            Assert.Equal(host.Port, mdoc.RootElement.GetProperty("api").GetProperty("port").GetInt32());

            var nf = await c.GetAsync("/api/v1/nothing-here");
            Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);
            Assert.Contains("NOT_FOUND", await nf.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Cidr_NotAllowed_Returns403_EvenForHealth()
    {
        var (host, src) = await StartAsync(Cfg("10.255.0.0/24"));
        try
        {
            using var c = Client(host);
            var r = await c.GetAsync("/api/v1/snapshot");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("IP_NOT_ALLOWED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            var h = await c.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.Forbidden, h.StatusCode);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Events_PagingAndFiles()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            for (int i = 0; i < 12; i++) src.Events.Append(DateTimeOffset.Now, "info", "系统", "标记", "e" + i);
            using var c = Client(host);
            var p1 = await c.GetStringAsync("/api/v1/events?afterSeq=0&limit=5");
            using var d1 = JsonDocument.Parse(p1);
            Assert.Equal(5, d1.RootElement.GetProperty("count").GetInt32());
            Assert.True(d1.RootElement.GetProperty("more").GetBoolean());
            Assert.Equal(1, d1.RootElement.GetProperty("items")[0].GetProperty("seq").GetInt64());
            var p2 = await c.GetStringAsync("/api/v1/events?afterSeq=10&limit=5");
            using var d2 = JsonDocument.Parse(p2);
            Assert.Equal(2, d2.RootElement.GetProperty("count").GetInt32());
            Assert.False(d2.RootElement.GetProperty("more").GetBoolean());
            Assert.Equal(12, d2.RootElement.GetProperty("eventSeqHead").GetInt64());

            // 文件列表 / 下载 / 路径穿越
            File.WriteAllText(Path.Combine(src.DataDir, "telemetry-20261106.csv"), "时间,标记\n2026-11-06T08:00:00.000+08:00,\n", new UTF8Encoding(true));
            var files = await c.GetStringAsync("/api/v1/files?type=telemetry");
            Assert.Contains("telemetry-20261106.csv", files);
            var dl = await c.GetAsync("/api/v1/files/telemetry/telemetry-20261106.csv");
            Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
            Assert.Equal("text/csv", dl.Content.Headers.ContentType!.MediaType);
            Assert.True(dl.Headers.AcceptRanges.Contains("bytes"));
            var bad = await c.GetAsync("/api/v1/files/telemetry/..%2F..%2Fetc%2Fpasswd");
            Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
            var badType = await c.GetAsync("/api/v1/files?type=secrets");
            Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

            var noZone = await c.GetAsync("/api/v1/telemetry?from=2026-11-06T00:00:00&to=2026-11-07T00:00:00");
            Assert.Equal(HttpStatusCode.BadRequest, noZone.StatusCode);
            var range = await c.GetAsync("/api/v1/telemetry?from=2026-11-05T00:00:00Z&to=2026-11-08T00:00:00Z");
            Assert.Equal(HttpStatusCode.OK, range.StatusCode);
            var csv = await range.Content.ReadAsStringAsync();
            Assert.StartsWith("﻿时间", csv);
            Assert.Contains("2026-11-06T08:00:00.000+08:00", csv);

            var alarms = await c.GetStringAsync("/api/v1/alarms");
            Assert.Contains("\"count\":0", alarms);
            var sessions = await c.GetStringAsync("/api/v1/sessions");
            Assert.Contains("\"current\"", sessions);
            var leak = await c.GetStringAsync("/api/v1/leak-tests");
            Assert.Contains("\"count\":0", leak);
            var settings = await c.GetStringAsync("/api/v1/settings");
            Assert.Contains("\"revision\":42", settings);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, ct); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed: " + r.CloseStatus + " " + r.CloseStatusDescription);
        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private static async Task<ClientWebSocket> ConnectAsync(ApiHost host, CancellationToken ct, string token = Token)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        await ws.ConnectAsync(new Uri(host.BaseUrl.Replace("http://", "ws://") + "/api/v1/ws"), ct);
        return ws;
    }

    [Fact]
    public async Task WebSocket_Hello_Telemetry_Events_Resync_Ping()
    {
        var (host, src) = await StartAsync(Cfg());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        try
        {
            for (int i = 0; i < 3; i++) src.Events.Append(DateTimeOffset.Now, "info", "系统", "标记", "before" + i);
            using var ws = await ConnectAsync(host, ct);
            using var hello = await ReceiveAsync(ws, ct);
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(3, hello.RootElement.GetProperty("eventSeqHead").GetInt64());
            Assert.Equal("test0001", hello.RootElement.GetProperty("bootId").GetString());
            Assert.Equal(1, hello.RootElement.GetProperty("seqEpoch").GetInt32());

            // 带 resumeAfterEventSeq=1 订阅：补发 seq 2、3
            await SendAsync(ws, "{\"type\":\"subscribe\",\"topics\":[\"telemetry\",\"events\",\"alarms\"],\"resumeAfterEventSeq\":1,\"seqEpoch\":1}", ct);
            await SendAsync(ws, "{\"type\":\"ping\"}", ct);

            int telemetry = 0; bool pong = false, subscribed = false, alarm = false, liveSent = false;
            var eventSeqs = new List<long>();
            long lastTel = -1; int telGaps = 0;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && (telemetry < 5 || !pong || !subscribed || eventSeqs.Count < 3 || !alarm))
            {
                if (subscribed && !liveSent)
                {
                    // 订阅（含补发）处理完之后再产生实时事件与报警，避免与补发竞争
                    liveSent = true;
                    src.Events.Append(DateTimeOffset.Now, "warn", "RV2", "超压 · 联锁动作", "live");
                    src.Events.PublishAlarms(Array.Empty<AlarmDto>(), new AlarmDto("1", "warn", "RV2", 2, "超压", "x", DateTime.UtcNow, "active", null));
                }
                using var m = await ReceiveAsync(ws, ct);
                switch (m.RootElement.GetProperty("type").GetString())
                {
                    case "telemetry":
                        telemetry++;
                        long s = m.RootElement.GetProperty("seq").GetInt64();
                        if (lastTel >= 0 && s != lastTel + 1) telGaps++;
                        lastTel = s;
                        break;
                    case "pong": pong = true; break;
                    case "subscribed": subscribed = true; Assert.Equal(2, m.RootElement.GetProperty("replayed").GetInt32()); break;
                    case "event": eventSeqs.Add(m.RootElement.GetProperty("seq").GetInt64()); break;
                    case "alarm": alarm = true; Assert.Equal("RV2", m.RootElement.GetProperty("alarm").GetProperty("rv").GetString()); break;
                }
            }
            Assert.True(telemetry >= 5, $"telemetry={telemetry}");
            Assert.Equal(0, telGaps);
            Assert.True(pong);
            Assert.True(subscribed);
            Assert.True(alarm);
            Assert.Equal(new long[] { 2, 3, 4 }, eventSeqs.Take(3));   // 补发按序，再接实时

            // 缺口太大：afterSeq 早于环里最旧（环从本次运行第一条开始；用一个新的 store 不好造，这里用 Resync 的边界：afterSeq=-5 不在环里）
            await SendAsync(ws, "{\"type\":\"resync\",\"afterSeq\":-5}", ct);
            bool gap = false;
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !gap)
            {
                using var m = await ReceiveAsync(ws, ct);
                if (m.RootElement.GetProperty("type").GetString() == "gap")
                {
                    gap = true;
                    Assert.Equal("gap_too_large", m.RootElement.GetProperty("reason").GetString());
                    Assert.Equal(4, m.RootElement.GetProperty("eventSeqHead").GetInt64());
                }
            }
            Assert.True(gap);

            // 第 3 个连接超过 maxClients=2 → 503
            using var ws2 = await ConnectAsync(host, ct);
            await ReceiveAsync(ws2, ct);
            var ex = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(host, ct));
            Assert.Contains("503", ex.Message);
            Assert.Equal(2, host.Stats.Clients);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            var t0 = DateTime.UtcNow;
            while (host.Stats.Clients > 0 && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            Assert.Equal(0, host.Stats.Clients);
            Assert.Contains(src.Audits, a => a.Contains("已连接"));
            Assert.Contains(src.Audits, a => a.Contains("已断开"));
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task WebSocket_BadToken_401_And_SlowConsumer_DropsTelemetryNotEvents()
    {
        // 快速发布（5 ms 一帧）但客户端不读：遥测队列 50 帧满后丢最旧并计数；连接不会被阻塞住发布者
        var (host, src) = await StartAsync(Cfg(), TimeSpan.FromMilliseconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        try
        {
            var ex = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(host, ct, "wrong"));
            Assert.Contains("401", ex.Message);

            using var ws = await ConnectAsync(host, ct);
            await Task.Delay(1500, ct);   // ~300 帧发布，客户端一帧未读（TCP 缓冲 + 50 帧队列之外全部丢弃）
            Assert.True(host.Stats.DroppedTelemetryFrames > 0, "应有丢帧计数");
            Assert.Equal(1, host.Stats.Clients);
            var ev = src.Events.Append(DateTimeOffset.Now, "info", "系统", "标记", "must-arrive");
            // 现在开始读：hello → 若干 telemetry → event 一定到达（事件队列独立、不丢）
            bool gotEvent = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !gotEvent)
            {
                using var m = await ReceiveAsync(ws, ct);
                if (m.RootElement.GetProperty("type").GetString() == "event" && m.RootElement.GetProperty("seq").GetInt64() == ev.Seq) gotEvent = true;
            }
            Assert.True(gotEvent);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task AuthFailures_AreRateLimitedPerIp_Before_Hashing()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            using var anon = Client(host, auth: false);
            anon.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "guess");
            var codes = new List<HttpStatusCode>();
            for (int i = 0; i < ApiHost.AuthFailuresPerSec + 5; i++) codes.Add((await anon.GetAsync("/api/v1/snapshot")).StatusCode);
            // 同一秒内：前 10 次 401，之后 429（时钟跨秒时 401 可能多于 10，但一定有 429 出现在最后几次之前）
            Assert.Contains(HttpStatusCode.TooManyRequests, codes);
            Assert.True(codes.Count(c => c == HttpStatusCode.Unauthorized) <= 2 * ApiHost.AuthFailuresPerSec);
            var last = await anon.GetAsync("/api/v1/snapshot");
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Contains("RATE_LIMITED", await last.Content.ReadAsStringAsync());
                Assert.Equal("1", last.Headers.RetryAfter!.ToString());
            }
            // 审计仍是每 IP 每分钟一条
            Assert.Equal(1, src.Audits.Count(a => a.Contains("鉴权失败")));
            // 同一地址的有效令牌不受猜测者影响
            using var good = Client(host);
            Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/api/v1/device")).StatusCode);
            // /health 不受影响
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/v1/health")).StatusCode);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Preflight_HasOwnRateLimit_And_CommExposesDroppedEvents()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            using var c = Client(host);
            var tasks = Enumerable.Range(0, 6).Select(_ => c.GetAsync("/api/v1/preflight")).ToArray();
            var rs = await Task.WhenAll(tasks);
            Assert.Contains(rs, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Contains(rs, r => r.StatusCode == HttpStatusCode.TooManyRequests);   // 每令牌 2 次/秒
            var comm = await c.GetStringAsync("/api/v1/device/comm");
            using var doc = JsonDocument.Parse(comm);
            var api = doc.RootElement.GetProperty("api");
            Assert.Equal(0, api.GetProperty("droppedEvents").GetInt64());
            Assert.Equal(0, api.GetProperty("droppedTelemetryFrames").GetInt64());
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Preflight_UiTimeout_Maps503()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            src.PreflightDelayMs = 10;
            using var c = Client(host);
            var r = await c.GetAsync("/api/v1/preflight");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("UI_THREAD_TIMEOUT", await r.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public void Disabled_TryStart_ReturnsNull_WithoutBuildingKestrel()
    {
        using var src = new FakeApiSources(Tmp());
        Assert.Null(ApiHost.TryStart(src, new ApiConfig { Enabled = false }, null));
        Assert.Null(ApiHost.TryStart(src, new ApiConfig { Enabled = false }, "api.json：损坏"));
        Assert.Contains(src.Audits, a => a.Contains("配置读取失败"));
    }

    [Fact]
    public async Task BindFailure_IsAuditedAndReturnsFalse()
    {
        var (host, src) = await StartAsync(Cfg());
        try
        {
            using var src2 = new FakeApiSources(Tmp());
            var cfg2 = Cfg();
            cfg2.HttpPort = host.Port;   // 已占用
            var host2 = new ApiHost(src2, cfg2);
            Assert.False(await host2.StartOnceAsync());
            Assert.Contains(src2.Audits, a => a.Contains("绑定失败"));
            await host2.StopAsync();
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }
}
