using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 实时数据源（Services/LiveDeviceSource，PORT-PLAN §5 M2）用假连接：释放 / 断开 / 连接失败时<b>摘掉挂上的每一个事件处理器</b>
/// （旧 MainViewModel 从不摘）、遥测帧合并成一次刷新、报警归一化（过滤 info、确认状态、通道号从标题解析、清除即移除）、
/// 连接状态与控制权的显示。多台、重连监督、取消申请后迟到批准的自动释放在 M3（LiveDeviceSourceTests 会扩展）。
/// </summary>
public partial class LiveDeviceSourceTests
{
    private static readonly string[] Events =
    {
        "TelemetryReceived", "ConnectionStateChanged", "LeaseTransitioned", "ControlChanged", "AlarmChanged", "EventReceived", "Error", "ServerRestarted",
        // M3：重连倒计时、HMI 报告的控制权现状、403、已取消申请的结局
        "ReconnectScheduled", "LeaseServerViewChanged", "ForbiddenRole", "LeaseCancelledRequestSettled",
    };

    private static (LiveDeviceSource Src, ManualPost Post, Func<FakeReactorConnection> Fake) Make(Action<FakeReactorConnection>? setup = null)
    {
        var post = new ManualPost();
        FakeReactorConnection? fake = null;
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var src = new LiveDeviceSource(cfg, "PC-测试", post.Post, o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            setup?.Invoke(fake);
            return fake;
        });
        return (src, post, () => fake!);
    }

    [Fact]
    public async Task Dispose_DetachesEveryHandler_AndDisposesConnection()
    {
        var (src, post, fake) = Make();
        await src.ConnectAsync(CancellationToken.None);
        var f = fake();
        foreach (var e in Events) Assert.True(f.Handlers.TryGetValue(e, out var n) && n == 1, e + " 应该挂了 1 个处理器");
        Assert.Same(f, src.Connection);
        await src.DisposeAsync();
        Assert.Equal(0, f.TotalHandlers);
        Assert.True(f.Disposed);
        Assert.Null(src.Connection);
        // 之后再来的事件：没有人接（不会排刷新）
        int before = post.Pending;
        f.RaiseTelemetry(ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))! with { Seq = 999999 });
        Assert.Equal(before, post.Pending);
        post.Run();
    }

    [Fact]
    public async Task Disconnect_Detaches_KeepsDeviceAsDisconnected_ThenReconnects()
    {
        var (src, post, fake) = Make();
        await src.ConnectAsync(CancellationToken.None);
        var first = fake();
        await src.DisconnectAsync();
        Assert.Equal(0, first.TotalHandlers);
        Assert.True(first.Disposed);
        Assert.Equal(ConnStatus.Disconnected, src.Conn.State);
        Assert.True(src.Conn.EverOnline);
        Assert.Equal("已断开", src.Conn.Display.Text);
        Assert.Equal(StateKey.Offline, src.Read(1).Key);
        Assert.Equal("离线", src.Read(1).Word);
        await src.ReconnectNowAsync();                                       // 用原配置重新连接：新的连接实例
        var second = fake();
        Assert.NotSame(first, second);
        Assert.Equal(ConnStatus.Online, src.Conn.State);
        Assert.Equal(Events.Length, second.TotalHandlers);
        await src.DisposeAsync();
        Assert.Equal(0, second.TotalHandlers);
        post.Run();
    }

    [Fact]
    public async Task ConnectFailure_DetachesAndDisposes_ThenThrows()
    {
        var (src, _, fake) = Make(f => f.StartError = new ApiException("UNAUTHORIZED", 401, "令牌无效"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => src.ConnectAsync(CancellationToken.None));
        Assert.Equal("UNAUTHORIZED", ex.Code);
        Assert.Equal(0, fake().TotalHandlers);
        Assert.True(fake().Disposed);
        Assert.Null(src.Connection);
        Assert.Equal(ConnStatus.Disconnected, src.Conn.State);
        Assert.False(src.Conn.EverOnline);
        await src.DisposeAsync();
    }

    [Fact]
    public async Task Frames_AreCoalesced_LastOneWins()
    {
        var (src, post, fake) = Make();
        await src.ConnectAsync(CancellationToken.None);
        await Task.Delay(50);
        post.Run();
        int calls = 0;
        SourceChange seen = SourceChange.None;
        src.Changed += c => { calls++; seen |= c; };
        var baseFrame = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        var frames = Enumerable.Range(1, 5).Select(i => baseFrame with { Seq = baseFrame.Seq + i }).ToList();
        foreach (var f in frames) fake().RaiseTelemetry(f);
        fake().RaiseConnection(ConnectionState.Connected);
        Assert.Equal(1, post.Pending);                                       // 一批只排一次
        post.Run();
        Assert.Equal(1, calls);
        Assert.True(seen.HasFlag(SourceChange.Frame));
        Assert.Same(frames[^1], src.Latest);
        await src.DisposeAsync();
    }

    [Fact]
    public async Task Alarms_Normalized()
    {
        var (src, post, fake) = Make();
        await src.ConnectAsync(CancellationToken.None);
        var t = new DateTime(2026, 11, 8, 2, 11, 30, DateTimeKind.Utc);
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("1", "info", "系统", null, "测试会话开始", "x", t, "done", null)));
        Assert.Empty(src.Alarms);                                            // info 不算报警
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("2", "warn", "RV3", null, "RV3 升温较慢", "已用 18 min", t, "active", null)));
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("3", "err", "RV1", 1, "RV1 超压", "3.12 MPa", t.AddSeconds(5), "active", null)));
        var a = src.Alarms;
        Assert.Equal(2, a.Count);
        Assert.Equal("3", a[0].Id);                                          // 最新在前
        Assert.Equal((AlarmLevel.Err, 1, "P-HIGH"), (a[0].Level, a[0].Ch!.Value, a[0].Code));
        Assert.Equal((AlarmLevel.Warn, 3, "RV3 升温较慢 · 已用 18 min"), (a[1].Level, a[1].Ch!.Value, a[1].Text));
        Assert.False(a[1].Acked);
        // 确认：条件型（超压：HMI 在条件消失时自己解除）照样列出；没有条件的（HMI 永远不解除）确认了就退出当前报警，留在报警簿的全部里
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("3", "err", "RV1", 1, "RV1 超压", "3.12 MPa", t.AddSeconds(5), "ack", null)));
        Assert.True(src.Alarms.Single(x => x.Id == "3").Acked);
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("2", "warn", "RV3", null, "RV3 升温较慢", "已用 18 min", t, "ack", null)));
        Assert.DoesNotContain(src.Alarms, x => x.Id == "2");
        Assert.True(src.AlarmBook.Find("2")!.Acked);
        Assert.Equal(2, src.AlarmBook.All.Count);
        fake().RaiseAlarm(new AlarmMsg("alarm", new AlarmDto("3", "err", "RV1", 1, "RV1 超压", "", t, "done", t.AddMinutes(1))));
        Assert.DoesNotContain(src.Alarms, x => x.Id == "3");                 // 清除了
        var facts = src.Facts();
        Assert.Empty(facts.Alarms);
        post.Run();
        await src.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionStates_MapToShellWords()
    {
        var (src, post, fake) = Make();
        Assert.Equal(ConnStatus.Disconnected, src.Conn.State);               // 还没开始连（M3：登记了、没勾自动连接）
        Assert.False(src.Conn.EverOnline);
        Assert.Equal("已断开", src.Conn.Display.Text);
        Assert.Equal("未连接", src.Read(1).Word);
        var connecting = src.ConnectAsync(CancellationToken.None);          // 假连接同步完成；连接中的样子见 RegistryTests 的慢连接
        await connecting;
        await Task.Delay(50);
        post.Run();                                                          // 第一帧（GET /snapshot）应用到 Latest
        Assert.Equal(("已连接", ConnStatus.Online), (src.Conn.Display.Text, src.Conn.State));
        fake().RaiseConnection(ConnectionState.Reconnecting);                // WS 掉线，Client 自己退避重连
        Assert.Equal(ConnStatus.Connecting, src.Conn.State);
        Assert.Equal("正在重连…", src.Conn.Display.Text);
        Assert.Equal("离线", src.Read(1).Word);
        Assert.Equal(StateKey.Ready, src.Read(1).Own);                       // 断线前的状态还在
        fake().RaiseConnection(ConnectionState.Connected);
        Assert.Equal(ConnStatus.Online, src.Conn.State);
        Assert.Equal(LeaseStatus.Local, src.Lease.State);
        Assert.Equal("本地 · 只读", src.Lease.Display.Text);
        post.Run();
        await src.DisposeAsync();
    }
}
