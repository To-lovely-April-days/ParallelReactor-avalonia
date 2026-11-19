using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F3 的实时数据源（LiveDeviceSource + 假连接 FakeReactorConnection）：
/// <list type="bullet">
/// <item><see cref="LiveDeviceSource.Plant"/>：设置（报警上限 psi → MPa、超温、容积 + 标定时刻转本地、传动比）+ 帧（搅拌电流、气路占用 / 排队）+ 型号（釜转速上限）+ GET /control（断线策略，断开后仍是最近一次知道的）；按引用缓存。</item>
/// <item>「溶液体积」：PUT /channels/{id}/volume（带租约、0.1 mL）、结果提示「1号机 · RV1 溶液体积 → 5.1 mL」、超过釜容积本机先拦下。</item>
/// <item>设备上的程序：不是本机下发的程序也有真实步骤（GET /channels/{id}/program/run，每次执行 / 换步一次）；台账对得上时台账优先、不去取；
///   帧里换了一次执行而设备回的还是上一次的：不显示（R5）。</item>
/// </list>
/// </summary>
public class F3LiveTests
{
    private const string Me = "远程（PC-测试）";
    private static readonly DateTime CalAt = new(2026, 11, 1, 1, 2, 3, DateTimeKind.Utc);

    private static SettingsDto Settings()
    {
        var s = ApiJson.Deserialize<SettingsDto>(Samples.Read("live-settings.json"))!;
        var eff = new double?[8];
        var at = new DateTime?[8];
        eff[0] = 48.2;
        at[0] = CalAt;
        return s with { Stir = s.Stir with { StirGearRatio = 3 }, Volume = new VolumeSettingsDto(50, 2.5, 10.2, eff, at) };
    }

    private static ControlStateDto Control(string onLost = "stopHeat") =>
        ApiJson.Deserialize<ControlStateDto>(Samples.Read("live-control.json"))! with { OnRemoteLost = onLost };

    private static async Task Until(Func<bool> done, string what)
    {
        for (int i = 0; i < 300 && !done(); i++) await Task.Delay(10);
        Assert.True(done(), what);
    }

    private static async Task<(FleetVm F, DeviceVm D, FakeReactorConnection Fake, LiveDeviceSource Live)> Connected(Action<ScriptedHttpHandler>? more = null)
    {
        var (f, d, fake) = await LiveFleet.OneAsync(h =>
        {
            FakeReactorConnection.ScriptGrantedLease(h);
            h.On("GET", "/settings", _ => ScriptedHttpHandler.Ok(Settings()));
            h.On("PUT", @"/channels/\d+/volume", c => ScriptedHttpHandler.Command(c, false, new { channel = 1, ml = 5.1, gasVolumeMl = 47.4 }));
            h.On("PUT", @"/channels/\d+/program", c => ScriptedHttpHandler.Command(c, false, null, opEventSeq: 97));
            more?.Invoke(h);
        }, control: Control());
        var live = (LiveDeviceSource)d.Source;
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        await Until(() => fake.Lease.State == LeaseState.Holding, "持有控制权");
        await Until(() => live.Settings != null, "取到设置");
        f.RefreshNow();
        return (f, d, fake, live);
    }

    /// <summary>第 1、2 路的一帧（其它通道空闲）。</summary>
    private static TelemetrySnapshot Frame(DateTime ts, ChannelProgramSnap? p1 = null, ChannelProgramSnap? p2 = null) =>
        LiveFrames.Frame(ts, LiveFrames.Ch(1, 60, 80, 30, 0, 50, "heating", program: p1), LiveFrames.Ch(2, program: p2), LiveFrames.Ch(3), LiveFrames.Ch(4));

    private static ProgramStepDto S(string type, string json, string name) => new(type, JsonDocument.Parse(json).RootElement.Clone(), name, "");

    private static ProgramRunDto Run(int n, DateTime startedAt, int step = 0, string by = "local") => new(5, DateTime.UtcNow, new ProgramRunSnap(n, true, "running", step, 4, "", "", by, startedAt, false, new[]
    {
        S("heat", "{\"mode\":\"fixed\",\"temp\":80,\"wait\":10}", "升温"),
        S("press", "{\"gas\":\"A\",\"pres\":150,\"regulate\":\"on\"}", "升压"),
        S("react", "{\"endBy\":\"time\",\"time\":90}", "恒温反应"),
        S("end", "{\"mode\":\"seal\",\"cool\":40}", "结束"),
    }));

    // ———————————————————— 整机数据 ————————————————————

    [Fact]
    public async Task Plant_FromSettingsFrameModelControl_Cached()
    {
        var (f, d, fake, live) = await Connected();
        var snap = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        var frame = snap with
        {
            Stir = snap.Stir with { MotorCurrentA = 0.8 },
            Programs = snap.Programs with { GasHolder = 3, WaitingGas = new[] { 5, 2 } },
        };
        fake.RaiseTelemetry(frame);
        f.RefreshNow();
        var p = live.Plant;
        Assert.Equal(Units.PsiToMpa(510), p.OverPressureMPa!.Value, 9);
        Assert.Equal(250, p.OverTempC);
        Assert.Equal(live.Model.Limits.RpmMax, p.RpmMax);
        Assert.Equal(0.8, p.StirCurrentA);
        Assert.Equal(3, p.GasHolder);
        Assert.Equal(new[] { 5, 2 }, p.GasQueue);
        Assert.Equal(3, p.StirGearRatio);
        Assert.Equal("stopHeat", p.OnRemoteLost);
        var v = p.Volume!;
        Assert.Equal((50.0, 2.5, (double?)10.2), (v.VesselMl, v.LineMl, v.ManifoldMl));
        Assert.Equal(48.2, v.EffectiveMl[0]);
        Assert.Equal(CalAt.ToLocalTime(), v.CalibratedAt[0]);
        Assert.Equal(DateTimeKind.Local, v.CalibratedAt[0]!.Value.Kind);
        Assert.Null(v.CalibratedAt[1]);
        Assert.True(v.Calibrated(1));
        Assert.Equal(48.2 - 5, v.GasVolume(1, 5)!.Value, 9);

        // 同一帧、同一份设置、同一个控制权现状：同一个对象（每帧刷新不分配）；新的一帧才重建，容积只跟设置走
        Assert.Same(p, live.Plant);
        fake.RaiseTelemetry(frame with { Seq = frame.Seq + 1, Stir = frame.Stir with { MotorCurrentA = 0.9 } });
        f.RefreshNow();
        var p2 = live.Plant;
        Assert.NotSame(p, p2);
        Assert.Equal(0.9, p2.StirCurrentA);
        Assert.Same(v, p2.Volume);

        // 断开之后：断线策略仍是最近一次知道的（断线横条正要用它）
        await live.DisconnectAsync();
        Assert.Null(live.Connection);
        Assert.Equal("stopHeat", live.Plant.OnRemoteLost);
        await f.DisposeAsync();
    }

    [Fact]
    public void PlantOf_Pure_Defaults()
    {
        var e = LiveDeviceSource.PlantOf(null, null, null, null);
        Assert.Equal(PlantInfo.Empty with { RpmMax = LiveDeviceSource.DefaultRpmMax }, e);
        Assert.Null(LiveDeviceSource.VolumeOf(null));
        // 旧 HMI 的设置（没有 volume 节、传动比默认 1）
        var s = ApiJson.Deserialize<SettingsDto>(Samples.Read("live-settings.json"))!;
        var p = LiveDeviceSource.PlantOf(s, null, "", Core.Models.ModelProfiles.Embedded.Get("PR8"));
        Assert.Null(p.Volume);
        Assert.Equal(1, p.StirGearRatio);
        Assert.Null(p.OnRemoteLost);
        Assert.Equal(Core.Models.ModelProfiles.Embedded.Get("PR8")!.Limits.RpmMax, p.RpmMax);
    }

    // ———————————————————— 溶液体积 ————————————————————

    [Fact]
    public async Task SetVolume_PutsWithLease_ToastsResult_ChecksVesselVolume()
    {
        var (f, d, fake, live) = await Connected();
        var o = await d.Actions.RunAsync(DeviceAction.SetVolume, new ActionPayload(N: 1, Ml: 5.07));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 溶液体积 → 5.1 mL", o.Message);
        var call = fake.Http.Of("PUT", "/channels/1/volume").Single();
        Assert.Equal("L-test", call.Lease);
        Assert.Equal(5.1, call.Json.GetProperty("ml").GetDouble());
        Assert.NotNull(call.Idem);

        // 超过釜容积（设置 volume.vesselVolume = 50 mL）：本机先拦下，不发
        var big = await d.Actions.RunAsync(DeviceAction.SetVolume, new ActionPayload(N: 1, Ml: 60));
        Assert.False(big.Ok);
        Assert.Equal("1号机 · RV1 溶液体积超出釜容积 · ≤ 50 mL", big.Message);
        // 守卫：没有数 / 负数
        Assert.Equal("请输入溶液体积", d.Actions.Why(DeviceAction.SetVolume, ActionPayload.Ch(1)));
        Assert.False((await d.Actions.RunAsync(DeviceAction.SetVolume, new ActionPayload(N: 1, Ml: -1))).Ok);
        Assert.Equal(1, fake.Http.Count("PUT", @"/channels/\d+/volume"));
        await f.DisposeAsync();
    }

    // ———————————————————— 设备上的程序 ————————————————————

    [Fact]
    public async Task DeviceProgram_StepsFromProgramRun_FetchedOncePerRunAndStep()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-5);
        var serve = t0;
        var (f, d, fake, live) = await Connected(h => h.On("GET", "/channels/1/program/run", _ => ScriptedHttpHandler.Ok(Run(1, serve))));
        // 触屏放上去、触屏启动的程序（本机没下发过）
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "local", message: "升温至 80 ℃")));
        f.RefreshNow();
        await Until(() => live.Program(1) != null, "取到设备上的程序");
        var p = live.Program(1)!;
        Assert.Equal((ProgramSource.Hmi, "设备程序", ProgramStatus.Running, 0, 4), (p.Source, p.Name, p.Status, p.StepIndex, p.Steps.Count));
        Assert.True(p.StartedByTouch);
        Assert.Equal(new[] { "升温", "升压", "恒温反应", "结束" }, p.Steps.Select(s => s.Name));
        Assert.Equal(t0.ToLocalTime(), p.StartedAt);
        Assert.Equal(t0.ToLocalTime(), p.StepStartedAt);               // 第 0 步 = 开始时刻（步骤计时同台账）
        Assert.True(p.StepDurS > 0);
        Assert.Equal(1, fake.Http.Count("GET", "/channels/1/program/run"));

        // 同一步的后续帧、多次刷新：不再取
        for (int i = 2; i < 6; i++)
        {
            fake.RaiseTelemetry(Frame(t0.AddSeconds(i), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "local")));
            f.RefreshNow();
            Assert.NotNull(live.Program(1));
        }
        Assert.Equal(1, fake.Http.Count("GET", "/channels/1/program/run"));
        // 换步：再取一次；记录跟着帧走
        fake.RaiseTelemetry(Frame(t0.AddSeconds(6.2), LiveFrames.Prog(1, 4, startedAt: t0, startedBy: "local", message: "充压")));
        f.RefreshNow();
        Assert.Equal(1, live.Program(1)!.StepIndex);
        await Until(() => fake.Http.Count("GET", "/channels/1/program/run") == 2, "换步再取一次");
        Assert.True(Math.Abs((t0.AddSeconds(5.6).ToLocalTime() - live.Program(1)!.StepStartedAt!.Value).TotalMilliseconds) < 1);   // 换步：两帧中点
        // 没有程序的反应釜：不取
        Assert.Null(live.Program(3));
        Assert.Equal(0, fake.Http.Count("GET", "/channels/3/program/run"));

        // 同一路开始了新的一次执行，而设备回的还是上一次的：不显示旧步骤（R5）
        var t1 = t0.AddMinutes(3);
        fake.RaiseTelemetry(Frame(t1.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t1, startedBy: "local")));
        f.RefreshNow();
        Assert.Null(live.Program(1));
        await Until(() => fake.Http.Count("GET", "/channels/1/program/run") == 3, "新的执行再取");
        await Task.Delay(50);
        f.RefreshNow();
        Assert.Null(live.Program(1));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Ledger_Wins_OverDeviceProgram_MismatchFallsBackToDevice()
    {
        var t0 = DateTime.UtcNow.AddSeconds(-30);
        var (f, d, fake, live) = await Connected(h =>
        {
            h.On("GET", "/channels/1/program/run", _ => ScriptedHttpHandler.Ok(Run(1, t0, by: "远程（PC-别人）")));
            h.On("GET", "/channels/2/program/run", _ => ScriptedHttpHandler.Ok(Run(2, t0, by: Me)));
        });
        var doc = LegacyDocs.Four("加氢测试-01");
        Assert.True((await live.UploadProgramAsync(1, doc, CancellationToken.None)).Ok);
        Assert.True((await live.UploadProgramAsync(2, doc, CancellationToken.None)).Ok);
        // RV2：本机下发、本机启动 → 台账的程序（名字、来源），不去取设备上的
        // RV1：别的客户端启动 → 台账作废（R5），改按设备上的程序显示（真实步骤）
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1),
            LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "远程（PC-别人）"),
            LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me)));
        f.RefreshNow();
        // 连上时样本帧里 RV2 有一次旧的执行（触发过一次取数）：从这一帧起 RV2 由台账显示，不再取
        int c2 = fake.Http.Count("GET", "/channels/2/program/run");
        var p2 = live.Program(2)!;
        Assert.Equal((ProgramSource.Ledger, "加氢测试-01"), (p2.Source, p2.Name));
        Assert.Equal("由其他客户端启动（远程（PC-别人））", live.LedgerReason(1));
        await Until(() => live.Program(1) != null, "RV1 改按设备上的程序");
        var p1 = live.Program(1)!;
        Assert.Equal((ProgramSource.Hmi, "设备程序", 4), (p1.Source, p1.Name, p1.Steps.Count));
        Assert.False(p1.StartedByTouch);
        for (int i = 2; i < 5; i++)
        {
            fake.RaiseTelemetry(Frame(t0.AddSeconds(i), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "远程（PC-别人）"), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me)));
            f.RefreshNow();
            Assert.Equal(ProgramSource.Ledger, live.Program(2)!.Source);
        }
        Assert.Equal(c2, fake.Http.Count("GET", "/channels/2/program/run"));
        Assert.Equal(1, fake.Http.Count("GET", "/channels/1/program/run"));
        // HMI 重启：旧的运行记录全部作废（假连接的启动号不变、帧里还是这一次执行：重新取一次）
        int c1 = fake.Http.Count("GET", "/channels/1/program/run");
        fake.RaiseRestarted();
        f.RefreshNow();
        live.Program(1);
        await Until(() => fake.Http.Count("GET", "/channels/1/program/run") == c1 + 1, "重启后重新取");
        await f.DisposeAsync();
    }
}
