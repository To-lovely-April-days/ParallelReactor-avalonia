using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 设置 → 温控 / PID 页的 Client 增补（HMI v0.6）：GET /pid（PidDto 解析：档位定义、各档 P/I/D 与来历、自整定状态）、
/// POST /channels/{id}/autotune {tempC, band?}（控制类：租约头、幂等键、正文；422 映射）、DELETE /channels/{id}/autotune（停止类：无租约）。
/// </summary>
public class ClientPidTests
{
    private static (ReactorApiClient, ScriptedHttpHandler) Make()
    {
        var h = new ScriptedHttpHandler();
        var o = new ClientOptions(new Uri("http://127.0.0.1:18100"), "optoken", "PC-测试工位1") { WriteRetryDelay = TimeSpan.Zero, MinWriteGap = TimeSpan.Zero };
        return (new ReactorApiClient(o, h, new FakeClock()), h);
    }

    private static readonly DateTime TunedAt = new(2026, 11, 17, 6, 30, 0, DateTimeKind.Utc);

    private static PidDto Pid()
    {
        var bands = new[] { new PidBandSnap(0, "低温档", 0, 100), new PidBandSnap(1, "中温档", 100, 200), new PidBandSnap(2, "高温档", 200, 400) };
        PidChannelSnap Ch(int id) => new(id, id == 5, id == 5 ? 2 : null, id == 5 ? 260 : null,
            Enumerable.Range(0, 3).Select(b => id == 1 && b == 1
                ? new PidBandValueSnap(b, 62.5, 118, 21.4, 150, TunedAt, TunedAt.AddDays(1))
                : new PidBandValueSnap(b, 40 + 20 * b, 100 + 25 * b, 15 + 5 * b, null, null, null)).ToArray());
        return new PidDto(901, TunedAt.AddDays(1), bands, Enumerable.Range(1, 8).Select(Ch).ToArray());
    }

    [Fact]
    public async Task Pid_Get_ParsesDto()
    {
        var (c, h) = Make();
        h.On("GET", "/pid", _ => ScriptedHttpHandler.Ok(Pid()));
        var r = await c.PidAsync(CancellationToken.None);
        Assert.Equal(901, r.Seq);
        Assert.Equal(new[] { "低温档", "中温档", "高温档" }, r.Bands.Select(b => b.Name));
        Assert.Equal((100d, 200d), (r.Bands[1].LoC, r.Bands[1].HiC));
        Assert.Equal(8, r.Channels.Length);
        var mid = r.Channels[0].Bands[1];
        Assert.Equal((62.5, 118d, 21.4), (mid.P, mid.I, mid.D));
        Assert.Equal(150, mid.TunedSpC);
        Assert.Equal(TunedAt, mid.TunedAt!.Value.ToUniversalTime());
        Assert.Equal(TunedAt.AddDays(1), mid.ManualEditedAt!.Value.ToUniversalTime());
        Assert.Null(r.Channels[0].Bands[0].TunedAt);
        Assert.False(r.Channels[0].Autotuning);
        Assert.Null(r.Channels[0].TuneBand);
        var rv5 = r.Channels[4];
        Assert.Equal((true, (int?)2, (double?)260), (rv5.Autotuning, rv5.TuneBand, rv5.TuneSpC));
        var call = h.Calls.Single();
        Assert.Equal(("GET", "/pid"), (call.Method, call.Path));
        Assert.Null(call.Lease);   // 读：不带租约
    }

    [Fact]
    public async Task Pid_Errors_Throw()
    {
        var (c, h) = Make();
        h.On("GET", "/pid", _ => ScriptedHttpHandler.Error(OpCodes.Starting, "HMI 启动中"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.PidAsync(CancellationToken.None));
        Assert.Equal(OpCodes.Starting, ex.Code);
    }

    [Fact]
    public async Task StartAutotune_WithBand_LeaseIdemBody()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/3/autotune", call => ScriptedHttpHandler.Command(call, false, new { channel = 3, sp = 150.0, band = 1 }, 77));
        var r = await c.StartAutotuneAsync("LEASE1", 3, 150, 1, CancellationToken.None);
        Assert.True(r.IsConfirmed);
        Assert.Equal(77, r.OpEventSeq);
        Assert.Equal(1, r.Result.GetProperty("band").GetInt32());
        var call = h.Calls.Single();
        Assert.Equal("LEASE1", call.Lease);
        Assert.True(IdempotencyKeys.IsValid(call.Idem));
        Assert.Equal(150, call.Json.GetProperty("tempC").GetDouble());
        Assert.Equal(1, call.Json.GetProperty("band").GetInt32());
        Assert.Equal(2, call.Json.EnumerateObject().Count());
    }

    [Fact]
    public async Task StartAutotune_WithoutBand_OnlyTempC()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/[0-9]/autotune", call => ScriptedHttpHandler.Command(call, false, new { done = true }));
        await c.StartAutotuneAsync("L", 2, 80, null, CancellationToken.None);
        await c.StartAutotuneAsync("L", 4, 90, CancellationToken.None);   // 旧重载 = 不带 band
        foreach (var call in h.Calls)
        {
            Assert.Single(call.Json.EnumerateObject());
            Assert.True(call.Json.TryGetProperty("tempC", out _));
        }
    }

    [Fact]
    public async Task StartAutotune_BandMismatch_422_Mapped()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/1/autotune", call => ScriptedHttpHandler.CommandError(call, OpCodes.ParamOutOfRange, "整定目标温度 250 °C 不在档位 1 的温区内", 12));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.StartAutotuneAsync("L", 1, 250, 1, CancellationToken.None));
        Assert.Equal((422, OpCodes.ParamOutOfRange), (ex.Status, ex.Code));
        Assert.Equal(1, h.Count("POST", "/channels/1/autotune"));   // 422 不重试
    }

    [Fact]
    public async Task CancelAutotune_StopClass_NoLease()
    {
        var (c, h) = Make();
        h.On("DELETE", "/channels/6/autotune", call => ScriptedHttpHandler.Command(call, false, new { done = true }));
        var r = await c.CancelAutotuneAsync(6, CancellationToken.None);
        Assert.True(r.IsConfirmed);
        var call = h.Calls.Single();
        Assert.Null(call.Lease);
        Assert.True(IdempotencyKeys.IsValid(call.Idem));
    }
}
