using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// v0.6 档位 PID：
/// <list type="bullet">
/// <item><see cref="AutotuneBook"/>：整定完成并存档才记来历（目标温度 + 完成时刻 UTC），取消 / 急停 / 超温打断不记；手改保留整定记录只打标记；</item>
/// <item>来历持久化往返（与 SettingsStore 同一 System.Text.Json 默认选项；旧存档没有 BandTune 也能加载）；</item>
/// <item>GET /pid 的 JSON 形状（camelCase 金标准）与端点；POST /channels/{id}/autotune 的 band 参数（绑定 / 400）与 <see cref="ValueRules.AutotuneBand"/>（422）。</item>
/// </list>
/// </summary>
public class PidTuneTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);

    // ———————————————————— 来历：完成才记，打断不记 ————————————————————

    [Fact]
    public void Completion_RecordsSpAndTime_IntoBandChosenAtStart()
    {
        var b = new AutotuneBook(8, 3);
        b.ClearAbort(1);
        b.Begin(1, 1, 150);
        Assert.Equal((1, (double?)150), (b.TuneBand(1), b.TuneSp(1)));
        Assert.Null(b.Meta(1, 1));   // 进行中不记

        Assert.True(b.OnFinished(1));                  // 轮询看到结束：正常完成 → 应回读
        int band = b.Complete(1, fallbackBand: 0, T0); // 界面此时显示低温档，仍存回发起时的中温档
        Assert.Equal(1, band);
        Assert.Equal(new BandTuneMeta(150, T0, null), b.Meta(1, 1));
        Assert.Equal(DateTimeKind.Utc, b.Meta(1, 1)!.TunedAt!.Value.Kind);
        Assert.Equal((-1, (double?)null), (b.TuneBand(1), b.TuneSp(1)));   // 进行状态清空
        Assert.Null(b.Meta(1, 0));
        Assert.Null(b.Meta(0, 1));                     // 其它通道不受影响
    }

    [Fact]
    public void Abort_Cancel_Estop_RecordsNothing()
    {
        var b = new AutotuneBook(8, 3);
        b.Begin(2, 2, 250);
        b.Abort(2);                                    // 取消 / 急停 / 超温切断
        Assert.True(b.IsAborted(2));
        Assert.Equal(-1, b.TuneBand(2));
        Assert.False(b.OnFinished(2));                 // At 1→4：被打断，不回读
        Assert.False(b.IsAborted(2));                  // 标记已清
        Assert.All(Enumerable.Range(0, 3), i => Assert.Null(b.Meta(2, i)));

        // 已有的整定记录不会被一次打断的整定改动
        b.Begin(2, 2, 260);
        Assert.True(b.OnFinished(2));
        b.Complete(2, 0, T0);
        b.ClearAbort(2);
        b.Begin(2, 2, 300);
        b.Abort(2);
        Assert.False(b.OnFinished(2));
        Assert.Equal(new BandTuneMeta(260, T0, null), b.Meta(2, 2));
    }

    [Fact]
    public void OverTempStopWriteFailed_RestoreKeepsTuning()
    {
        // 超温切断的 At=4 写失败：撤销打断标记，仪表上的整定仍在进行，完成后照常记来历
        var b = new AutotuneBook(8, 3);
        b.Begin(0, 1, 180);
        int band = b.TuneBand(0); double? sp = b.TuneSp(0);
        b.Abort(0);
        b.Restore(0, band, sp);
        Assert.True(b.OnFinished(0));
        Assert.Equal(1, b.Complete(0, 2, T0));
        Assert.Equal(new BandTuneMeta(180, T0, null), b.Meta(0, 1));
    }

    [Fact]
    public void Completion_WithoutSoftwareStart_FallsBackToShownBand_SpUnknown()
    {
        // 仪表面板发起的整定（软件没有进行中的档位）：存入界面显示档，目标温度未知
        var b = new AutotuneBook(8, 3);
        Assert.True(b.OnFinished(4));
        Assert.Equal(2, b.Complete(4, 2, T0));
        Assert.Equal(new BandTuneMeta(null, T0, null), b.Meta(4, 2));
    }

    [Fact]
    public void ManualEdit_KeepsTuning_MarksTime_NextTuneClearsMark()
    {
        var b = new AutotuneBook(8, 3);
        b.Begin(0, 0, 80);
        b.OnFinished(0);
        b.Complete(0, 0, T0);
        b.MarkManual(0, 0, T0.AddHours(1));
        Assert.Equal(new BandTuneMeta(80, T0, T0.AddHours(1)), b.Meta(0, 0));

        // 从未整定过的档只手改：只有手改时刻
        b.MarkManual(0, 2, T0.AddHours(2));
        Assert.Equal(new BandTuneMeta(null, null, T0.AddHours(2)), b.Meta(0, 2));

        // 下一次整定完成：整条替换，手改标记清除
        b.Begin(0, 0, 90);
        b.OnFinished(0);
        b.Complete(0, 1, T0.AddDays(1));
        Assert.Equal(new BandTuneMeta(90, T0.AddDays(1), null), b.Meta(0, 0));

        // 越界档位忽略
        b.MarkManual(0, 7, T0);
        b.MarkManual(0, -1, T0);
    }

    // ———————————————————— 持久化往返 ————————————————————

    /// <summary>与 SettingsStore.PersistedState 中相关两项同形（SettingsStore 引用主工程类型，测试工程不链接）；序列化选项相同（默认 + 缩进）。</summary>
    private sealed class PersistedLike
    {
        public double[][][]? BandPids { get; set; }
        public BandTuneMeta?[][]? BandTune { get; set; }
    }

    [Fact]
    public void Persistence_RoundTrip_SameAsSettingsStoreJson()
    {
        var b = new AutotuneBook(8, 3);
        b.Begin(0, 1, 150); b.OnFinished(0); b.Complete(0, 0, T0);
        b.MarkManual(0, 1, T0.AddMinutes(5));
        b.Begin(7, 2, 333.5); b.OnFinished(7); b.Complete(7, 0, T0.AddDays(-3));
        b.MarkManual(3, 0, T0.AddSeconds(1));

        var json = JsonSerializer.Serialize(new PersistedLike { BandTune = b.Export() }, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"TunedSpC\": 150", json);
        Assert.Contains("\"TunedAt\": \"2026-11-18T02:00:00Z\"", json);   // UTC 带 Z
        var back = JsonSerializer.Deserialize<PersistedLike>(json)!;
        var c = new AutotuneBook(8, 3);
        c.Import(back.BandTune);
        for (int i = 0; i < 8; i++)
            for (int k = 0; k < 3; k++)
                Assert.Equal(b.Meta(i, k), c.Meta(i, k));
        Assert.Equal(DateTimeKind.Utc, c.Meta(0, 1)!.ManualEditedAt!.Value.Kind);
        // Export 是副本：之后的修改不影响已导出的数组
        var snap = c.Export();
        c.MarkManual(5, 0, T0);
        Assert.Null(snap[5][0]);
    }

    [Fact]
    public void Persistence_OldFileWithoutBandTune_AndPartialData_Load()
    {
        // 旧存档：只有 BandPids，没有 BandTune → null，来历保持为空（PID 照常恢复由 TempService 负责）
        var old = JsonSerializer.Deserialize<PersistedLike>("{\"BandPids\":[[[1,2,3],[4,5,6],[7,8,9]]]}")!;
        Assert.Null(old.BandTune);
        var b = new AutotuneBook(8, 3);
        b.Import(old.BandTune);
        Assert.All(b.Export(), row => Assert.All(row, Assert.Null));

        // 部分数据：通道 / 档位不足只恢复已有部分；多出的忽略；三项全空当作没有；不带时区的时间按 UTC
        var partial = JsonSerializer.Deserialize<PersistedLike>(
            "{\"BandTune\":[[null,{\"TunedSpC\":120,\"TunedAt\":\"2026-11-01T08:00:00\",\"ManualEditedAt\":null},{\"TunedSpC\":null,\"TunedAt\":null,\"ManualEditedAt\":null},{\"TunedSpC\":1}],null]}")!;
        b.Import(partial.BandTune);
        Assert.Null(b.Meta(0, 0));
        Assert.Equal(new BandTuneMeta(120, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), null), b.Meta(0, 1));
        Assert.Equal(DateTimeKind.Utc, b.Meta(0, 1)!.TunedAt!.Value.Kind);
        Assert.Null(b.Meta(0, 2));
        Assert.Null(b.Meta(1, 0));
    }

    // ———————————————————— 显式档位校验（422）————————————————————

    private static readonly (double Lo, double Hi)[] Bands3 = { (0, 100), (100, 200), (200, 400) };

    [Theory]
    [InlineData(0, 40, true)]
    [InlineData(0, 99.9, true)]
    [InlineData(0, 100, false)]     // 分界属于上一档 [lo, hi)
    [InlineData(1, 100, true)]
    [InlineData(1, 150, true)]
    [InlineData(1, 200, false)]
    [InlineData(1, 99, false)]
    [InlineData(2, 200, true)]
    [InlineData(2, 450, true)]      // 最高档：≥ lo 直到 TMaxSp
    [InlineData(2, 501, false)]
    [InlineData(2, 150, false)]
    [InlineData(3, 150, false)]     // 档位越界
    [InlineData(-1, 150, false)]
    public void AutotuneBand_TempMustLieInBand(int band, double tempC, bool ok)
    {
        var v = ValueRules.AutotuneBand(band, tempC, Bands3, tMaxSp: 500);
        Assert.Equal(ok, v == null);
        if (!ok) Assert.Equal(OpCodes.ParamOutOfRange, v!.Code);
        if (!ok) Assert.Equal(422, ApiErrorStatus.Of(v!.Code));
    }

    [Fact]
    public void AutotuneBand_Detail_And_Message()
    {
        var v = ValueRules.AutotuneBand(1, 250, Bands3, 500)!;
        Assert.Equal((100d, 200d, 250d), (v.Min, v.Max, v.Value));
        Assert.Contains("档位 1", v.Message);
        var top = ValueRules.AutotuneBand(2, 150, Bands3, 500)!;
        Assert.Equal((200d, 500d), (top.Min, top.Max));   // 最高档上限 = TMaxSp
        Assert.NotNull(ValueRules.AutotuneBand(1, double.NaN, Bands3, 500));
        // 40..TMaxSp 仍由 AutotuneSp 先查
        Assert.NotNull(ValueRules.AutotuneSp(30, 500));
    }

    // ———————————————————— GET /pid 的 JSON 形状 ————————————————————

    [Fact]
    public void PidDto_GoldenJsonShape()
    {
        var ts = new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);
        var p = TelemetrySnapshotJsonTests.SamplePid(ts);
        using var doc = JsonDocument.Parse(ApiJson.Serialize(new PidDto(42, ts, p.Bands, p.Channels)));
        var root = doc.RootElement;
        Assert.Equal(new[] { "seq", "ts", "bands", "channels" }, root.EnumerateObject().Select(x => x.Name));
        var band = root.GetProperty("bands")[1];
        Assert.Equal(new[] { "index", "name", "loC", "hiC" }, band.EnumerateObject().Select(x => x.Name));
        Assert.Equal((1, "中温档", 100d, 200d), (band.GetProperty("index").GetInt32(), band.GetProperty("name").GetString(), band.GetProperty("loC").GetDouble(), band.GetProperty("hiC").GetDouble()));

        var chs = root.GetProperty("channels");
        Assert.Equal(8, chs.GetArrayLength());
        var c1 = chs[0];
        Assert.Equal(new[] { "id", "autotuning", "tuneBand", "tuneSpC", "bands" }, c1.EnumerateObject().Select(x => x.Name));
        Assert.False(c1.GetProperty("autotuning").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c1.GetProperty("tuneBand").ValueKind);   // null 照常输出
        Assert.Equal(JsonValueKind.Null, c1.GetProperty("tuneSpC").ValueKind);
        var mid = c1.GetProperty("bands")[1];
        Assert.Equal(new[] { "index", "p", "i", "d", "tunedSpC", "tunedAt", "manualEditedAt" }, mid.EnumerateObject().Select(x => x.Name));
        Assert.Equal((62.5, 118d, 21.4, 150d), (mid.GetProperty("p").GetDouble(), mid.GetProperty("i").GetDouble(), mid.GetProperty("d").GetDouble(), mid.GetProperty("tunedSpC").GetDouble()));
        Assert.Equal("2026-11-17T06:30:00Z", mid.GetProperty("tunedAt").GetString());
        Assert.Equal("2026-11-18T01:57:00Z", mid.GetProperty("manualEditedAt").GetString());
        var low = c1.GetProperty("bands")[0];
        Assert.Equal(JsonValueKind.Null, low.GetProperty("tunedSpC").ValueKind);
        Assert.Equal(JsonValueKind.Null, low.GetProperty("tunedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, low.GetProperty("manualEditedAt").ValueKind);

        var c3 = chs[2];
        Assert.True(c3.GetProperty("autotuning").GetBoolean());
        Assert.Equal(2, c3.GetProperty("tuneBand").GetInt32());
        Assert.Equal(250, c3.GetProperty("tuneSpC").GetDouble());

        // 反序列化往返（PC 端用同一 Contracts 类型）
        var back = ApiJson.Deserialize<PidDto>(doc.RootElement.GetRawText())!;
        Assert.Equal(150, back.Channels[0].Bands[1].TunedSpC);
        Assert.Equal(2, back.Channels[2].TuneBand);
    }

    // ———————————————————— 端点：GET /pid、POST autotune {band} ————————————————————

    private const string Operator = "op-token", Viewer = "viewer-token";

    private static ApiConfig Cfg() => new()
    {
        Enabled = true, Bind = "127.0.0.1", HttpPort = 0, MaxClients = 4, AutoGrantRemote = true,
        HeartbeatTimeoutSec = 10, LeaseGraceSec = 60, ControlRequestTimeoutSec = 60, WriteRateLimitPerSec = 1000,
        Tokens = new List<ApiToken>
        {
            new() { Name = "it", Sha256 = TokenAuth.Hash(Viewer), Role = "viewer" },
            new() { Name = "op", Sha256 = TokenAuth.Hash(Operator), Role = "operator" },
        },
    };

    private static async Task<(ApiHost Host, FakeApiSources Src)> StartAsync(bool tick = true)
    {
        var src = new FakeApiSources(Path.Combine(Path.GetTempPath(), "pr-api-pid-" + Guid.NewGuid().ToString("N")), tick ? TimeSpan.FromMilliseconds(100) : null);
        var host = new ApiHost(src, Cfg());
        Assert.True(await host.StartOnceAsync(), string.Join("\n", src.Audits));
        return (host, src);
    }

    private static HttpClient Client(ApiHost host, string token)
    {
        var c = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task GetPid_FromSnapshot_AnyRole_503BeforeFirstTick()
    {
        var (host, src) = await StartAsync(tick: false);
        try
        {
            using var v = Client(host, Viewer);
            var r0 = await v.GetAsync("/api/v1/pid");
            Assert.Equal(503, (int)r0.StatusCode);   // STARTING：还没有快照
            src.Tick();
            var r = await v.GetAsync("/api/v1/pid");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("seq").GetInt64() > 0);
            Assert.Equal(3, doc.RootElement.GetProperty("bands").GetArrayLength());
            var c1 = doc.RootElement.GetProperty("channels")[0];
            Assert.Equal(150, c1.GetProperty("bands")[1].GetProperty("tunedSpC").GetDouble());
            Assert.True(doc.RootElement.GetProperty("channels")[2].GetProperty("autotuning").GetBoolean());
            Assert.Empty(src.Fake.Calls);   // 只读：不经命令层
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task PostAutotune_BandOptional_BoundOrRejected()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var op = Client(host, Operator);
            var acq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/control/request") { Content = new StringContent("{\"clientName\":\"PC-1\"}", Encoding.UTF8, "application/json") };
            acq.Headers.Add(ApiHost.IdempotencyHeader, Guid.NewGuid().ToString());
            using var ad = JsonDocument.Parse(await (await op.SendAsync(acq)).Content.ReadAsStringAsync());
            var lease = ad.RootElement.GetProperty("result").GetProperty("leaseId").GetString()!;

            async Task<(int Status, string Body)> Post(int ch, string json)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{ch}/autotune") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.Add(ApiHost.LeaseHeader, lease);
                var resp = await op.SendAsync(req);
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
            }

            Assert.Equal(200, (await Post(1, "{\"tempC\":150}")).Status);
            Assert.Equal(("StartAutotune", "1,150"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));   // 缺省 band：命令层按 tempC 定档
            Assert.Equal(200, (await Post(2, "{\"tempC\":250,\"band\":2}")).Status);
            Assert.Equal(("StartAutotune", "2,250,2"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal(200, (await Post(3, "{\"tempC\":80,\"band\":null}")).Status);   // null = 缺省
            Assert.Equal(("StartAutotune", "3,80"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));

            int n = src.Fake.Calls.Count;
            foreach (var bad in new[] { "{\"tempC\":150,\"band\":1.5}", "{\"tempC\":150,\"band\":\"1\"}", "{\"band\":1}" })
            {
                var (st, body) = await Post(1, bad);
                Assert.Equal(400, st);
                Assert.Contains("BAD_REQUEST", body);
            }
            Assert.Equal(n, src.Fake.Calls.Count);   // 类型错误在绑定阶段拒绝，不到命令层

            // 命令层的 422（档位 / 温区不符）原样映射
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Fail(OpCodes.ParamOutOfRange, "整定目标温度 250 °C 不在档位 1 的温区内", "err", new { min = 100, max = 200, value = 250 }));
            var (s422, b422) = await Post(4, "{\"tempC\":250,\"band\":1}");
            Assert.Equal(422, s422);
            Assert.Contains("PARAM_OUT_OF_RANGE", b422);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }
}
