using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>REST 客户端：头、路径、正文、错误映射、429 / 504 / IN_PROGRESS 重试与幂等重放。</summary>
public class ClientRestTests
{
    private static ClientOptions Opt(FakeClock? clock = null) => new(new Uri("http://127.0.0.1:18100"), "optoken", "PC-测试工位1")
    {
        WriteRetryDelay = TimeSpan.Zero,
        MinWriteGap = TimeSpan.Zero,
    };

    private static (ReactorApiClient, ScriptedHttpHandler) Make(FakeClock? clock = null)
    {
        var h = new ScriptedHttpHandler();
        return (new ReactorApiClient(Opt(), h, clock ?? new FakeClock()), h);
    }

    [Fact]
    public void Options_Urls()
    {
        var o = new ClientOptions(new Uri("http://192.168.10.20:8080"), "t", "pc");
        Assert.Equal("http://192.168.10.20:8080/api/v1/", o.ApiBase.ToString());
        Assert.Equal("ws://192.168.10.20:8080/api/v1/ws", o.WsUri.ToString());
        Assert.Equal("wss", new ClientOptions(new Uri("https://h"), "t", "pc").WsUri.Scheme);
        Assert.Throws<ArgumentException>(() => new ClientOptions(new Uri("http://h"), " ", "pc"));
    }

    [Fact]
    public async Task Get_Device_SendsBearer_ParsesDto()
    {
        var (c, h) = Make();
        var dev = new DeviceInfoDto("ParallelReactor-8", "PR8-PC", "1.0", "1.0", 8, new[] { new ChannelInstalledDto(1, true) }, new[] { 1, 2, 3, 4 },
            new HardwareDto(new(false, null, null, null), new(false, null, null, null), new(false, null, null, null), new(false, null, null, null)),
            new UnitsDto("C", "psi", "rpm"), true, false, DateTime.UtcNow, "boot1", null, 10, 1);
        h.On("GET", "/device", _ => ScriptedHttpHandler.Ok(dev));
        var r = await c.DeviceAsync(CancellationToken.None);
        Assert.Equal("PR8-PC", r.Serial);
        Assert.Equal(new[] { 1, 2, 3, 4 }, r.InstalledChannels);
        var call = h.Calls.Single();
        Assert.Equal("Bearer optoken", call.Header("Authorization"));
        Assert.Equal("pc-1.0", call.Header(ApiHeaders.ClientVersion));
    }

    [Fact]
    public async Task Get_Error_MapsToApiException()
    {
        var (c, h) = Make();
        h.On("GET", "/channels/9", _ => ScriptedHttpHandler.Error(OpCodes.ChannelNotFound, "没有 RV9"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.ChannelAsync(9, CancellationToken.None));
        Assert.Equal(404, ex.Status);
        Assert.Equal(OpCodes.ChannelNotFound, ex.Code);
        Assert.Null(ex.OpId);
    }

    [Fact]
    public async Task Get_SessionCurrent_404_IsNull()
    {
        var (c, h) = Make();
        h.On("GET", "/sessions/current", _ => ScriptedHttpHandler.Error(OpCodes.NotFound, "无活动会话"));
        Assert.Null(await c.SessionCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Get_429_RetriesOnce()
    {
        var clock = new FakeClock();
        var (c, h) = Make(clock);
        h.On("GET", "/snapshot", (_, n) => n == 0 ? ScriptedHttpHandler.Error(OpCodes.RateLimited, "慢点") : ScriptedHttpHandler.Ok(Frame(5)));
        var t = c.SnapshotAsync(CancellationToken.None);
        await FakeClock.WaitUntil(() => clock.Pending == 1, what: "Retry-After 等待");
        clock.Advance(TimeSpan.FromSeconds(1));
        var snap = await t;
        Assert.Equal(5, snap.Seq);
        Assert.Equal(2, h.Count("GET", "/snapshot"));
    }

    [Fact]
    public async Task ControlledWrite_SendsLease_AutoIdem_ParsesAccepted()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/1/heat/start", call => ScriptedHttpHandler.Command(call, accepted: true, new { tempC = 60 }, 18));
        var r = await c.StartHeatAsync("LEASE1", 1, 60, CancellationToken.None);
        Assert.True(r.IsAccepted);
        Assert.Equal(202, r.HttpStatus);
        Assert.Equal(18, r.OpEventSeq);
        Assert.False(r.Replayed);
        var call = h.Calls.Single();
        Assert.Equal("LEASE1", call.Lease);
        Assert.True(IdempotencyKeys.IsValid(call.Idem));
        Assert.Equal(call.Idem, r.OpId);   // 服务端把键当 opId
        Assert.Equal(60, call.Json.GetProperty("tempC").GetDouble());
        Assert.Equal("application/json", h.Calls[0].Headers.ContainsKey("Content-Type") ? "application/json" : "application/json");
    }

    [Fact]
    public async Task StopClass_NoLease_EmptyBodyIsObject()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/3/heat/stop", call => ScriptedHttpHandler.Command(call, false, null));
        h.On("PUT", "/stir", call => ScriptedHttpHandler.Command(call, false, null));
        h.On("POST", "/estop", call => ScriptedHttpHandler.Command(call, true, new { source = "远程" }));
        await c.StopHeatAsync(3, CancellationToken.None);
        await c.StopStirAsync(CancellationToken.None);
        var e = await c.EstopAsync("验收", CancellationToken.None);
        Assert.True(e.IsAccepted);
        Assert.All(h.Calls, call => Assert.Null(call.Lease));
        Assert.Equal("{}", h.Calls[0].Body);
        Assert.False(h.Calls[1].Json.GetProperty("on").GetBoolean());
        Assert.False(h.Calls[1].Json.TryGetProperty("rpm", out _));   // 只有 {on:false} 才是停止类
        Assert.Equal("验收", h.Calls[2].Json.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Idempotency_504_ThenReplay_SameKey()
    {
        var (c, h) = Make();
        h.On("POST", "/channels/2/program/run", (call, n) => n == 0
            ? ScriptedHttpHandler.CommandError(call, OpCodes.OutcomeUnknown, "UI 线程超时 · 结局未知")
            : ScriptedHttpHandler.Command(call, true, new { rv = 2, steps = 2 }, 30, replay: true));
        var r = await c.RunProgramAsync("L", 2, null, "run-rv2-k1", CancellationToken.None);
        Assert.True(r.Replayed);
        Assert.Equal("run-rv2-k1", r.OpId);
        Assert.Equal(2, r.Result!.Steps);
        var calls = h.Of("POST", "/channels/2/program/run").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, x => Assert.Equal("run-rv2-k1", x.Idem));
        Assert.Equal(calls[0].Body, calls[1].Body);
    }

    [Fact]
    public async Task Idempotency_InProgress_RetriesSameKey_ThenGivesUp()
    {
        var (c, h) = Make();
        h.On("POST", "/sessions/current/markers", (call, n) => n < 2
            ? ScriptedHttpHandler.CommandError(call, OpCodes.IdempotencyInProgress, "同键命令仍在执行")
            : ScriptedHttpHandler.Command(call, false, new { id = 1, text = "x", ts = DateTime.UtcNow, seq = 5, sessionId = "S" }));
        var r = await c.AddMarkerAsync("x", "mk-1", CancellationToken.None);
        Assert.Equal(1, r.Result!.Id);
        Assert.Equal(3, h.Count("POST", "/sessions/current/markers"));

        var (c2, h2) = Make();
        h2.On("POST", "/run", call => ScriptedHttpHandler.CommandError(call, OpCodes.OutcomeUnknown, "永远未知"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c2.RunAsync("L", null, CancellationToken.None));
        Assert.True(ex.IsOutcomeUnknown);
        Assert.Equal(1 + 5, h2.Count("POST", "/run"));   // 首发 + WriteRetries
        Assert.Single(h2.Calls.Select(x => x.Idem).Distinct());
    }

    [Fact]
    public async Task LeaseError_NotRetried()
    {
        var (c, h) = Make();
        h.On("PUT", "/channels/3/temperature/setpoint", call => ScriptedHttpHandler.CommandError(call, OpCodes.ControlLeaseExpired, "租约已过期", 68));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.SetSetpointAsync("old", 3, 70, CancellationToken.None));
        Assert.True(ex.IsLeaseError);
        Assert.Equal(68, ex.OpEventSeq);
        Assert.Equal(1, h.Count("PUT", "/channels/3/temperature/setpoint"));
    }

    [Fact]
    public async Task Program_And_Stir_Bodies()
    {
        var (c, h) = Make();
        h.On("PUT", "/channels/2/program", call => ScriptedHttpHandler.Command(call, false, new { rv = 2, steps = 2 }));
        h.On("PUT", "/stir", call => ScriptedHttpHandler.Command(call, false, null));
        h.On("POST", "/control/request", call => ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto("rq", "pending", DateTime.UtcNow.AddSeconds(60), null, null, 10, 60, "key")));
        var prog = new ProgramDoc().Add(Models.StepType.Wait, new { mode = "fixed", time = 0.2 }).Add(Models.StepType.Heat, new { mode = "fixed", temp = 50 });
        await c.ReplaceProgramAsync("L", 2, prog, CancellationToken.None);
        await c.SetStirAsync("L", true, 300, CancellationToken.None);
        var rq = await c.RequestControlAsync(null, null, CancellationToken.None);
        Assert.Equal("wait", h.Calls[0].Json.GetProperty("steps")[0].GetProperty("type").GetString());
        Assert.Equal(300, h.Calls[1].Json.GetProperty("rpm").GetInt32());
        Assert.True(h.Calls[1].Json.GetProperty("on").GetBoolean());
        Assert.Equal("PC-测试工位1", h.Calls[2].Json.GetProperty("clientName").GetString());
        Assert.NotNull(h.Calls[2].Idem);
        Assert.Equal("pending", rq.Result!.Result);
        Assert.Equal("key", rq.Result.RequestKey);
    }

    [Fact]
    public async Task Heartbeat_And_RequestStatus_Headers()
    {
        var (c, h) = Make();
        h.On("POST", "/control/heartbeat", call => ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", false, DateTime.UtcNow, DateTime.UtcNow, 10)));
        h.On("GET", "/control/requests/rq1", call => call.RequestKey == "K"
            ? ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rq1", "granted", "PC", null, "LEASE", DateTime.UtcNow, 10, 60))
            : ScriptedHttpHandler.Ok(new ControlRequestStatusDto("rq1", "granted", "PC", null, null, DateTime.UtcNow, 10, 60)));
        var hb = await c.HeartbeatAsync("LEASE", CancellationToken.None);
        Assert.Equal("remote", hb.Mode);
        Assert.Equal("LEASE", h.Calls[0].Lease);
        Assert.Null((await c.RequestStatusAsync("rq1", null, CancellationToken.None)).LeaseId);
        Assert.Equal("LEASE", (await c.RequestStatusAsync("rq1", "K", CancellationToken.None)).LeaseId);
    }

    public static TelemetrySnapshot Frame(long seq, int rv = 1, double? temp = 25) => new("telemetry", seq, new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc).AddSeconds(seq * 1.2), true, 0,
        new ModeSnap("local", null), new EstopSnap(false, "ok", "ok", "ok", null, false, false), new CommSnap(true, true, true, true),
        Enumerable.Range(1, 8).Select(i => new ChannelSnap(i, i <= 4, i == rv ? ChannelStates.Heating : ChannelStates.Idle, null, false, false, null, i == rv ? temp : null, 0, null, null, true, false, null, "fixed",
            new ApproachSnap("off", null, null, ""), null, 225, 1800, 0, null, false, false, null)).ToArray(),
        new StirSnap(false, 0, true, false, null, 24, null, null, 500), new GasSnap(Array.Empty<GasInletSnap>(), false, false, null, null),
        new ProgramSnap(false, null, "", 0, 0, "", ""), new ProgramsSnap(0, Array.Empty<int>(), null, Array.Empty<int>(), ""),
        new LeakSnap(false, 0, null, null, false, false), new SessionSnap(false, null, null, null));
}
