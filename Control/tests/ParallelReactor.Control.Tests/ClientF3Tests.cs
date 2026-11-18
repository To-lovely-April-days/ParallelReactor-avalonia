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

/// <summary>
/// 融合版 F3 的 Client 增补（HMI v0.6）：PUT /channels/{id}/volume（控制类：租约头、幂等键、{ml}、result 解析、错误映射）、
/// GET /channels/{id}/program/run（ProgramRunDto 解析；「本次开机以来没有执行过程序」的 404 → null；通道不存在等其它错误照常抛）。
/// </summary>
public class ClientF3Tests
{
    private static (ReactorApiClient, ScriptedHttpHandler) Make()
    {
        var h = new ScriptedHttpHandler();
        var o = new ClientOptions(new Uri("http://127.0.0.1:18100"), "optoken", "PC-测试工位1") { WriteRetryDelay = TimeSpan.Zero, MinWriteGap = TimeSpan.Zero };
        return (new ReactorApiClient(o, h, new FakeClock()), h);
    }

    // ———————————————————— PUT /channels/{id}/volume ————————————————————

    [Fact]
    public async Task SetVolume_Put_LeaseIdemBody_ParsesResult()
    {
        var (c, h) = Make();
        h.On("PUT", "/channels/2/volume", call => ScriptedHttpHandler.Command(call, false, new { channel = 2, ml = 5.1, gasVolumeMl = 44.9 }, 31));
        var r = await c.SetVolumeAsync("LEASE1", 2, 5.1, CancellationToken.None);
        Assert.True(r.IsConfirmed);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(31, r.OpEventSeq);
        Assert.Equal(new VolumeResult(2, 5.1, 44.9), r.Result);
        var call = h.Calls.Single();
        Assert.Equal(("PUT", "/channels/2/volume"), (call.Method, call.Path));
        Assert.Equal("LEASE1", call.Lease);
        Assert.True(IdempotencyKeys.IsValid(call.Idem));
        Assert.Equal(call.Idem, r.OpId);
        Assert.Equal(5.1, call.Json.GetProperty("ml").GetDouble());
        Assert.Single(call.Json.EnumerateObject());   // 只有 ml
        Assert.Equal("Bearer optoken", call.Header("Authorization"));
    }

    [Fact]
    public async Task SetVolume_Errors_Mapped_LeaseErrorNotRetried()
    {
        var (c, h) = Make();
        h.On("PUT", "/channels/1/volume", call => ScriptedHttpHandler.CommandError(call, OpCodes.ChannelProgramOwned, "RV1 程序执行中", 40));
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.SetVolumeAsync("L", 1, 3, CancellationToken.None));
        Assert.Equal(OpCodes.ChannelProgramOwned, ex.Code);
        Assert.Equal(ApiErrorStatus.Of(OpCodes.ChannelProgramOwned), ex.Status);
        Assert.Equal(40, ex.OpEventSeq);
        Assert.NotNull(ex.OpId);

        h.On("PUT", "/channels/3/volume", call => ScriptedHttpHandler.CommandError(call, OpCodes.ControlLeaseExpired, "租约已过期"));
        var le = await Assert.ThrowsAsync<ApiException>(() => c.SetVolumeAsync("old", 3, 3, CancellationToken.None));
        Assert.True(le.IsLeaseError);
        Assert.Equal(1, h.Count("PUT", "/channels/3/volume"));

        h.On("PUT", "/channels/4/volume", call => ScriptedHttpHandler.CommandError(call, OpCodes.ParamOutOfRange, "溶液体积超出范围"));
        var bad = await Assert.ThrowsAsync<ApiException>(() => c.SetVolumeAsync("L", 4, 999, CancellationToken.None));
        Assert.Equal(OpCodes.ParamOutOfRange, bad.Code);
        Assert.Equal(ApiErrorStatus.Of(OpCodes.ParamOutOfRange), bad.Status);
    }

    [Fact]
    public async Task SetVolume_OutcomeUnknown_RetriedWithSameKey()
    {
        var (c, h) = Make();
        h.On("PUT", "/channels/1/volume", (call, n) => n == 0
            ? ScriptedHttpHandler.CommandError(call, OpCodes.OutcomeUnknown, "结局未知")
            : ScriptedHttpHandler.Command(call, false, new { channel = 1, ml = 2.0, gasVolumeMl = 10.0 }, replay: true));
        var r = await c.SetVolumeAsync("L", 1, 2, CancellationToken.None);
        Assert.True(r.Replayed);
        Assert.Equal(2, h.Count("PUT", "/channels/1/volume"));
        Assert.Single(h.Calls.Select(x => x.Idem).Distinct());
    }

    // ———————————————————— GET /channels/{id}/program/run ————————————————————

    private static ProgramRunDto Run(int ch = 3, string state = "running", int step = 1)
    {
        var started = new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);
        using var p1 = JsonDocument.Parse("{\"mode\":\"fixed\",\"temp\":120,\"wait\":10}");
        using var p2 = JsonDocument.Parse("{\"gas\":\"A\",\"pres\":50,\"regulate\":\"on\"}");
        var steps = new[]
        {
            new ProgramStepDto("heat", p1.RootElement.Clone(), "升温", "120 ℃ · 平衡 10 min"),
            new ProgramStepDto("press", p2.RootElement.Clone(), "升压", "气体 A · 50 psi"),
        };
        return new ProgramRunDto(812, started.AddMinutes(30), new ProgramRunSnap(ch, true, state, step, 2, "执行第 2 步 · 升压", "升压中", "local", started, false, steps));
    }

    [Fact]
    public async Task ProgramRun_Get_ParsesDto()
    {
        var (c, h) = Make();
        h.On("GET", "/channels/3/program/run", _ => ScriptedHttpHandler.Ok(Run()));
        var r = await c.ProgramRunAsync(3, CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal(812, r!.Seq);
        var run = r.Run;
        Assert.Equal((3, true, "running", 1, 2, "local"), (run.Channel, run.Running, run.State, run.StepIndex, run.StepCount, run.StartedBy));
        Assert.Equal(new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc), run.StartedAt.ToUniversalTime());
        Assert.Equal(new[] { "heat", "press" }, run.Steps.Select(s => s.Type));
        Assert.Equal("升温", run.Steps[0].Name);
        Assert.Equal(120, run.Steps[0].Params.GetProperty("temp").GetDouble());
        Assert.Equal("on", run.Steps[1].Params.GetProperty("regulate").GetString());
        var call = h.Calls.Single();
        Assert.Equal(("GET", "/channels/3/program/run"), (call.Method, call.Path));
        Assert.Null(call.Lease);   // 读：不带租约
        Assert.Equal("Bearer optoken", call.Header("Authorization"));
    }

    [Fact]
    public async Task ProgramRun_NeverRan_404_IsNull()
    {
        var (c, h) = Make();
        h.On("GET", "/channels/2/program/run", _ => ScriptedHttpHandler.Error(OpCodes.NotFound, "RV2 本次开机以来没有执行过程序"));
        Assert.Null(await c.ProgramRunAsync(2, CancellationToken.None));
    }

    [Fact]
    public async Task ProgramRun_OtherErrors_Throw()
    {
        var (c, h) = Make();
        h.On("GET", "/channels/9/program/run", _ => ScriptedHttpHandler.Error(OpCodes.ChannelNotFound, "通道 9 不存在（有效范围 1–8）"));
        var nf = await Assert.ThrowsAsync<ApiException>(() => c.ProgramRunAsync(9, CancellationToken.None));
        Assert.Equal((404, OpCodes.ChannelNotFound), (nf.Status, nf.Code));
        h.On("GET", "/channels/1/program/run", _ => ScriptedHttpHandler.Error(OpCodes.Starting, "HMI 启动中"));
        var st = await Assert.ThrowsAsync<ApiException>(() => c.ProgramRunAsync(1, CancellationToken.None));
        Assert.Equal(OpCodes.Starting, st.Code);
        h.On("GET", "/channels/4/program/run", _ => ScriptedHttpHandler.Error(OpCodes.ForbiddenRole, "viewer 令牌只能读取"));
        ForbiddenNotice? seen = null;
        c.Forbidden += n => seen = n;
        await Assert.ThrowsAsync<ApiException>(() => c.ProgramRunAsync(4, CancellationToken.None));
        Assert.Equal(("GET", "channels/4/program/run"), (seen!.Method, seen.Path));
    }
}
