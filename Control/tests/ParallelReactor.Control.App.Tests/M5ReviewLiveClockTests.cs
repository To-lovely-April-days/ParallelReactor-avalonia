using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（设备时钟，LiveDeviceSource.Clock.cs）：一台进程内的假 HMI（Fakes/ScriptedHmi：真 ReactorConnection + 脚本化的 WS，服务端时钟 = 假时钟 + Skew），
/// 事件与续体都在测试 UI 线程上（同 LiveDeviceSourceTests.Live）。不运行模拟 HMI（D9）。
/// <list type="bullet">
/// <item>c1-live-2：WS hello 量的时钟差过时了（hello 时 HMI 快 6 s，之后被校准）—— 下发时刻按最近的帧估，下发后 3 s 本机启动的执行对得上（以前整段「待运行」）。</item>
/// <item>c1-live-3 / c2-ui-7：HMI 的钟慢 8 h，用户断开（连接对象先没了）：曲线一个采样都不少（以前断开标记按本机时钟、把 4 h 全裁掉），
///   断开之后横轴的「现在」照样按设备时钟（以前退回本机时钟，窗口里一个点都没有）；换了设备地址才忘掉时钟差。</item>
/// </list>
/// </summary>
public class M5ReviewLiveClockTests
{
    private const string Me = "PC-测试工位1";

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly UiThread Ui = new();
        public readonly ScriptedHmi Hmi;
        public LiveDeviceSource Src = null!;

        public Rig(TimeSpan skew) => Hmi = new ScriptedHmi(Clock, "192.168.1.20", "PR8-A") { Skew = skew, AutoGrant = true };

        public async Task StartAsync()
        {
            Src = await Ui.Invoke(() => new LiveDeviceSource(new DeviceConfig(Guid.NewGuid(), "1号机", "192.168.1.20", 8080, "tok"), Me, Ui.Post, Hmi.Connect, Ui, clock: Clock));
            await Ui.InvokeAsync(() => Src.StartAsync());
            await Until(() => Src.Conn.State == ConnStatus.Online, "上线");
        }

        public Task<T> On<T>(Func<T> f) => Ui.Invoke(f);

        public async Task Until(Func<bool> cond, string what, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (!await Ui.Invoke(cond))
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待超时：" + what);
                await Task.Delay(5);
            }
        }

        /// <summary>推一帧（第 1 路可以带程序），等它进了曲线。</summary>
        public async Task Push(ChannelProgramSnap? rv1 = null, string state = "heating")
        {
            var f = Hmi.Frame();
            if (rv1 is not null) f = f with { Channels = f.Channels.Select(c => c.Id == 1 ? c with { State = state, Program = rv1, ProgramOwned = rv1.Running } : c).ToArray() };
            int v = await On(() => Src.History.Channel(1)?.Version ?? -1);
            Hmi.Open!.Push(ApiJson.Serialize(f));
            await Until(() => (Src.History.Channel(1)?.Version ?? -1) != v, "帧进了曲线");
        }

        public async ValueTask DisposeAsync()
        {
            try { await Ui.InvokeAsync(async () => await Src.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            foreach (var t in Hmi.Server.Transports.ToArray()) t.ServerClose();
            Ui.Dispose();
        }
    }

    // ———————————————————— c1-live-2 ————————————————————

    /// <summary>
    /// 复核的探针（实时）：hello 时 HMI 的钟快 6 s，之后被校准到与本机一样（hello 的偏差一直留着 —— 一条 WS 连接可以连几天）。
    /// 下发时刻按最近的帧估（= 本机时刻）；下发后 3 s 本机启动的执行：对上、按台账显示运行中。以前按 hello 的 +6 s，比下发早 3 s 的「这次执行」被当成下发之前的，整段「待运行」。
    /// </summary>
    [Fact]
    public async Task StaleHelloOffset_DownloadTimeFromFrames_RunMatches()
    {
        await using var r = new Rig(TimeSpan.FromSeconds(6));
        await r.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(6), await r.On(() => r.Src.Connection!.Ws.ServerClockOffset));
        r.Hmi.Skew = TimeSpan.Zero;
        for (int i = 0; i < LiveDeviceSource.ClockSamples + 2; i++) await r.Push();
        Assert.Equal(TimeSpan.Zero, await r.On(() => r.Src.FrameClockOffset));
        Assert.Equal(TimeSpan.FromSeconds(6), await r.On(() => r.Src.Connection!.Ws.ServerClockOffset));   // hello 的还是旧的

        await Bounded(r.Ui.InvokeAsync(() => r.Src.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None)), "申请控制权");
        await r.Until(() => r.Src.Lease.State == LeaseStatus.Remote, "拿到控制权");
        r.Clock.Advance(TimeSpan.FromSeconds(1));                                        // 写命令之间的最小间隔按（假）时钟走
        await Task.Delay(20);
        var d0 = r.Clock.UtcNow;   // 下发时刻（两边的钟现在一样）
        await Bounded(r.Ui.InvokeAsync(() => r.Src.UploadProgramAsync(1, ProgramDraftStore.Template("加氢测试-01"), CancellationToken.None)), "下发");
        var e = await r.On(() => r.Src.Ledger.Entry(1)!);
        Assert.Equal(d0, e.DownloadedAtDevice);                                         // 以前 d0 + 6 s

        // 下发后 3 s 本机启动
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        var run = new ChannelProgramSnap(true, 0, 4, "running", "升温至 80 ℃", "远程（" + Me + "）", d0.AddSeconds(3), false);
        await r.Push(run);
        Assert.Equal(LedgerMatchKind.Matched, await r.On(() => r.Src.Ledger.Match(1, run, r.Src.Ledger.BootId).Kind));
        Assert.Equal(ProgramStatus.Running, await r.On(() => r.Src.Program(1)?.Status));
        Assert.True(await r.On(() => r.Src.Ledger.Entry(1)!.Valid));
    }

    // ———————————————————— c1-live-3 / c2-ui-7 ————————————————————

    /// <summary>
    /// 复核的探针（实时）：HMI 的钟慢 8 h，3 min 的曲线；用户断开（CloseAsync 先把连接对象清掉，再离开「已连接」）：
    /// 断开标记按记下的时钟差（设备时钟）—— 一个采样都不少（以前按本机时钟，标记在 8 h 之后，4 h 的保留把曲线全裁掉）；
    /// 断开之后横轴的「现在」照样是设备时钟（窗口里的点都在）。换了设备地址（新地址还没 hello）才不再用旧的时钟差。
    /// </summary>
    [Fact]
    public async Task HmiEightHoursBehind_HistorySurvivesDisconnect_WindowStays()
    {
        await using var r = new Rig(TimeSpan.FromHours(-8));
        await r.StartAsync();
        for (int i = 0; i < 18; i++)
        {
            await r.Push();
            r.Clock.Advance(TimeSpan.FromSeconds(10));
        }
        int before = await r.On(() => r.Src.History.Channel(1)!.Count);
        Assert.True(before >= 18);
        var nowOnline = await r.On(() => r.Src.History.Now);
        int inWindow = await r.On(() => r.Src.History.Series(1, HistoryKey.TempC, 30).Count);
        Assert.Equal(before, inWindow);

        await Bounded(r.Ui.InvokeAsync(() => r.Src.DisconnectAsync()), "断开");
        Assert.Null(await r.On(() => r.Src.Connection));
        var ch = await r.On(() => r.Src.History.Channel(1)!);
        Assert.Equal(before + 1, await r.On(() => ch.Count));                          // 一个不少 + 断开标记
        Assert.True(await r.On(() => ch[ch.Count - 1].Gap));
        // 横轴的「现在」：仍按设备时钟（慢 8 h），窗口里的点都在
        Assert.Equal(nowOnline, await r.On(() => r.Src.History.Now));
        Assert.Equal(before + 1, await r.On(() => r.Src.History.Series(1, HistoryKey.TempC, 30).Count));
        Assert.True((r.Clock.UtcNow - (await r.On(() => r.Src.History.Now)).ToUniversalTime()).TotalHours > 7.9);

        // 只换令牌（同一台设备）：时钟差留着
        r.Hmi.WsHang = true;                                                             // 新连接的 WS 握手挂着：还没有 hello
        await Bounded(r.Ui.InvokeAsync(() => r.Src.StartAsync()), "重新连接");
        await Bounded(r.Ui.InvokeAsync(() => r.Src.UpdateConfigAsync(r.Src.Config with { Token = "tok2" })), "换令牌");
        Assert.True((r.Clock.UtcNow - (await r.On(() => r.Src.History.Now)).ToUniversalTime()).TotalHours > 7.9);
        // 换了地址（后面可能是另一台设备）：旧的时钟差不再用
        await Bounded(r.Ui.InvokeAsync(() => r.Src.UpdateConfigAsync(r.Src.Config with { Host = "192.168.1.21" })), "换地址");
        Assert.True(Math.Abs((r.Clock.UtcNow - (await r.On(() => r.Src.History.Now)).ToUniversalTime()).TotalSeconds) <= 1);
        r.Hmi.ReleaseWs();
    }

    /// <summary>等一个 UI 线程上的操作，最多 10 s（假时钟上的等待没人推进时测试失败，不会一直挂着）。</summary>
    private static async Task Bounded(Task t, string what)
    {
        try { await t.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new TimeoutException("等待超时：" + what); }
    }
}
