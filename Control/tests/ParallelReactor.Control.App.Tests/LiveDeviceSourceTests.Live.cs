using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M3 重连监督与多台实时设备（PORT-PLAN §5 M3「LiveDeviceSourceTests」）：两台进程内的假 HMI（Fakes/ScriptedHmi：ScriptedHttpHandler + FakeWsServer）
/// 经<b>真 ReactorConnection</b>连接，时间用假时钟（FakeClock）推进；FleetVm / DeviceRegistry / LiveDeviceSource 都是真的，事件、刷新、监督循环的续体
/// 全部在一条测试 UI 线程上（Fakes/UiThread，同 App 的 AvaloniaSynchronizationContext）。不运行模拟 HMI（D9）。
/// 覆盖：一台连不上 → 倒计时重试 → 上线（另一台不受影响）；申请 → 批准 → 丢失 → 宽限 → 恢复；取消后迟到的批准 → 自动释放；
/// HMI 重启（bootId 变化）→ 重取 /device 与 /settings；403 → 只读令牌（缓存）；503 TOO_MANY_CLIENTS → 可读的失败原因；释放 → 摘掉每一个处理器、不再重试；
/// 一批事件只刷新一次、全部在 UI 线程上。
/// </summary>
public partial class LiveDeviceSourceTests
{
    private const string Me = "PC-测试工位1";

    /// <summary>两台（或几台）假 HMI + 实时 FleetVm。</summary>
    private sealed class LiveRig : IAsyncDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly UiThread Ui = new();
        public readonly Dictionary<string, ScriptedHmi> Hmis = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ReactorConnection> Conns = new();
        private readonly List<(string Text, ToastKind Kind)> _toasts = new();
        private int _offUi;
        public FleetVm Fleet = null!;

        public ScriptedHmi Hmi(string host, string serial)
        {
            var h = new ScriptedHmi(Clock, host, serial);
            Hmis[host] = h;
            return h;
        }

        /// <summary>LiveDeviceSource 的连接工厂：按地址接到对应的假 HMI（真 ReactorConnection），记下每一条连接。</summary>
        private IReactorConnection Factory(ClientOptions o)
        {
            var c = (ReactorConnection)Hmis[o.BaseUrl.Host].Connect(o);
            lock (Conns) Conns.Add(c);
            return c;
        }

        public ReactorConnection[] Connections { get { lock (Conns) return Conns.ToArray(); } }

        public (string Text, ToastKind Kind)[] Toasts { get { lock (_toasts) return _toasts.ToArray(); } }

        /// <summary>UI 线程以外触发的 Changed（应当是 0）。</summary>
        public int OffUi => Volatile.Read(ref _offUi);

        /// <summary>建实时 FleetVm（内存配置、不写文件），在 UI 线程上「添加并连接」这些设备。</summary>
        public async Task StartAsync(params (string Host, string Alias)[] devs)
        {
            Fleet = await Ui.Invoke(() => FleetVm.CreateLive(ControlConfig.InMemory(Me), Ui.Post, Ui, Factory, null, Clock));
            await Ui.Invoke(() =>
            {
                Fleet.Toast += (t, k) => { lock (_toasts) _toasts.Add((t, k)); };
                Fleet.Changed += (_, _) => { if (!Ui.IsCurrent) Interlocked.Increment(ref _offUi); };
                foreach (var (h, a) in devs)
                {
                    var d = Fleet.AddDevice(new NewDevice(h, 8080, "tok", a, AutoConnect: false, RememberToken: false)) ?? throw new InvalidOperationException("添加失败");
                    d.Source.Changed += _ => { if (!Ui.IsCurrent) Interlocked.Increment(ref _offUi); };
                }
            });
        }

        public DeviceVm D(int k) => Fleet.Device(k)!;

        /// <summary>
        /// 守卫的原因（先刷新一次，同 App 的 UiTicker 每秒刷新：倒计时只随时钟变、数据源没有变化时，DeviceVm 缓存的事实要靠刷新作废）。
        /// </summary>
        public Task<string> Why(int k, DeviceAction a, ActionPayload? p = null) => Ui.Invoke(() => { Fleet.RefreshNow(); return Fleet.Device(k)!.Actions.Why(a, p); });
        public LiveDeviceSource S(int k) => (LiveDeviceSource)Fleet.Device(k)!.Source;

        public Task<T> On<T>(Func<T> f) => Ui.Invoke(f);
        public Task On(Action a) => Ui.Invoke(a);

        /// <summary>等条件（在 UI 线程上求值）成立，不动时钟。</summary>
        public async Task Until(Func<bool> cond, string what, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (!await Ui.Invoke(cond))
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待超时：" + what + " · " + await Ui.Invoke(Diag));
                await Task.Delay(5);
            }
        }

        /// <summary>小步推进假时钟（每步让续体跑一下：WS 的 ping / pong、轮询、重试都挂在假时钟上），直到条件成立。</summary>
        public async Task AdvanceUntil(Func<bool> cond, string what, double stepSec = 0.5, int maxSteps = 400)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (await Ui.Invoke(cond)) return;
                Clock.Advance(TimeSpan.FromSeconds(stepSec));
                await Task.Delay(10);
            }
            await Until(cond, what);
        }

        /// <summary>推进 <paramref name="seconds"/>（小步）。</summary>
        public async Task Advance(double seconds, double stepSec = 0.5)
        {
            for (double t = 0; t < seconds - 1e-9; t += stepSec)
            {
                Clock.Advance(TimeSpan.FromSeconds(Math.Min(stepSec, seconds - t)));
                await Task.Delay(10);
            }
            await Task.Delay(30);
        }

        public string Diag() => string.Join(" | ", Fleet.Devices.Select(d => d.K + ":" + d.Source.Conn.State + "/" + d.Source.Conn.Display.Text + "/" + d.Source.Lease.State
            + "/" + ((LiveDeviceSource)d.Source).LastError));

        public async ValueTask DisposeAsync()
        {
            try { await Ui.InvokeAsync(async () => await Fleet.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            foreach (var h in Hmis.Values) foreach (var t in h.Server.Transports.ToArray()) t.ServerClose();
            Ui.Dispose();
        }
    }

    private static bool OnlineState(DeviceVm d) => d.Source.Conn.State == ConnStatus.Online;

    // ====================================================================== (a) 连不上 → 倒计时重试 → 上线

    [Fact]
    public async Task TwoDevices_OneCannotConnect_RetriesWithCountdown_ThenComesOnline()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        b.Down = true;
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));

        // 1号机：REST 起步 → WS hello → 已连接（「已连接」以 WS hello 为准）
        await r.Until(() => OnlineState(r.D(1)), "1号机上线");
        Assert.Equal(("已连接", true), await r.On(() => (r.S(1).Conn.Display.Text, r.S(1).Conn.EverOnline)));
        // 2号机：首次连接失败 → 断线倒计时（从未连上：「连接失败 · 2 s 后重试」），原因可读、提示一次
        await r.Until(() => r.S(2).Conn.State == ConnStatus.Offline, "2号机首次失败");
        var c2 = await r.On(() => r.S(2).Conn);
        Assert.Equal(("连接失败 · 2 s 后重试", false, 1), (c2.Display.Text, c2.EverOnline, c2.Attempts));
        Assert.Equal("网络错误：Connection refused (192.168.1.24:8080)", await r.On(() => r.S(2).LastError));
        Assert.Contains(r.Toasts, t => t.Text == "2号机 · 连接失败：网络错误：Connection refused (192.168.1.24:8080)" && t.Kind == ToastKind.Err);
        Assert.Equal("", await r.Ui.Invoke(() => { r.Fleet.RefreshNow(); return r.D(2).Why("reconnect"); }));            // 「立即重试」能点
        Assert.Equal("2号机连接失败 · 2 s 后重试", await r.Why(2, DeviceAction.StopAll));   // 从未连上：只剩 none 类操作
        await r.On(() => r.Fleet.RefreshNow());
        Assert.Equal(("连接失败 · 2 s 后重试", "link"), await r.On(() => (r.D(2).OffText, r.D(2).OffIcon)));
        Assert.Contains("网络错误：Connection refused", await r.On(() => r.D(2).HeadTip));   // 悬停提示里有原因

        // 倒计时跟着时钟走；到点再试（还是不通）→ 间隔翻倍、次数加一、同一原因不再提示
        await r.Advance(1);
        Assert.Equal(1, await r.On(() => r.S(2).Conn.RetryS));
        int before = b.GateAttempts;
        await r.AdvanceUntil(() => r.S(2).Conn is { State: ConnStatus.Offline, Attempts: 2 }, "第二次失败");
        Assert.True(b.GateAttempts > before);
        Assert.Equal("连接失败 · 4 s 后重试", await r.On(() => r.S(2).Conn.Display.Text));
        Assert.Single(r.Toasts, t => t.Text.StartsWith("2号机 · 连接失败", StringComparison.Ordinal));

        // 「立即重连」打断等待：不推进时钟就再试一次
        before = b.GateAttempts;
        var o = await r.Ui.InvokeAsync(() => r.D(2).Actions.RunAsync(DeviceAction.Reconnect));
        Assert.Equal("2号机 · 正在重连…", o.Message);
        await r.Until(() => r.S(2).Conn is { State: ConnStatus.Offline, Attempts: 3 }, "立即重试（仍然不通）");
        Assert.True(b.GateAttempts > before);
        Assert.Equal("连接失败 · 8 s 后重试", await r.On(() => r.S(2).Conn.Display.Text));

        // 主机通了：下一次尝试连上
        b.Down = false;
        await r.AdvanceUntil(() => OnlineState(r.D(2)), "2号机上线");
        var on = await r.On(() => (r.S(2).Conn, r.S(2).LastError, r.S(2).Info?.Serial));
        Assert.Equal((ConnStatus.Online, true, 0, (string?)null, "PR8-B"), (on.Conn.State, on.Conn.EverOnline, on.Conn.Attempts, on.LastError, on.Serial));

        // 1号机自始至终没受影响；一直只有一条连接
        Assert.True(await r.On(() => OnlineState(r.D(1))));
        Assert.Equal(1, r.Connections.Count(c => c.Options.BaseUrl.Host == "192.168.1.20"));
        Assert.DoesNotContain(r.Toasts, t => t.Text.StartsWith("1号机", StringComparison.Ordinal));
        Assert.Equal(0, r.OffUi);
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== (b) 申请 → 批准 → 丢失 → 宽限 → 恢复

    [Fact]
    public async Task Lease_RequestApprove_LostGraceCountdown_Resume_OtherDeviceUntouched()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");

        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        Assert.Equal("1号机 · 已申请控制权 · 等待触屏确认", o.Message);
        // 倒计时按 HMI 给的到期时刻（服务端时钟快 3 s，按 hello 的时钟偏差换算）：60 s 起
        await r.Until(() => r.S(1).Lease is { State: LeaseStatus.Requested } && r.Connections[0].Lease.RequestExpiresAt != null, "申请发出");
        Assert.Equal((LeaseStatus.Requested, 60, "申请中 · 60 s"), await r.On(() => (r.S(1).Lease.State, r.S(1).Lease.RemainS, r.S(1).Lease.Display.Text)));
        await r.Advance(5);
        Assert.Equal(55, await r.On(() => r.S(1).Lease.RemainS));
        Assert.Equal("1号机等待触屏确认 · 剩 55 s", await r.Why(1, DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));

        a.Approve = true;   // 触屏点「允许」
        await r.AdvanceUntil(() => r.S(1).Lease.State == LeaseStatus.Remote, "批准");
        Assert.Contains(r.Toasts, t => t.Text == "1号机触屏已确认 · 远程控制中" && t.Kind == ToastKind.Ok);   // 样稿 tick 的写法
        Assert.Equal("", await r.Why(1, DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));

        // 心跳中断：HMI 判 remoteLost（WS control lost）→ 宽限倒计时（Client 的 LostAt + 宽限 60 s）
        a.LeaseLost = true;
        a.PushControl("lost", "remoteLost", Me);
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Lost, "丢失");
        Assert.Equal((60, "控制权心跳中断 · 宽限 60 s"), await r.On(() => (r.S(1).Lease.GraceS, r.S(1).Lease.Display.Text)));
        Assert.Contains(r.Toasts, t => t.Text == "1号机 · 控制权连接丢失 · 宽限期内自动恢复");
        await r.Advance(3);   // 宽限期里 REST 心跳仍报 remoteLost
        Assert.Equal(57, await r.On(() => r.S(1).Lease.GraceS));
        Assert.Equal("1号机控制权心跳中断 · 宽限 57 s", await r.Why(1, DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));   // 停止类照样可用

        // 心跳恢复：下一次 REST 心跳 resumed → 远程控制中
        a.LeaseLost = false;
        await r.AdvanceUntil(() => r.S(1).Lease.State == LeaseStatus.Remote, "恢复");
        Assert.Contains(r.Toasts, t => t.Text == "1号机 · 控制权已恢复");

        // 2号机自始至终是本地只读，没有发过任何控制权请求
        Assert.Equal(LeaseStatus.Local, await r.On(() => r.S(2).Lease.State));
        Assert.Equal(0, b.Count("POST", "/control/request") + b.Count("POST", "/control/heartbeat"));
        Assert.Equal(0, r.OffUi);
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== (c) 取消后迟到的批准 → 自动释放（R7）

    [Fact]
    public async Task Lease_CancelThenLateApproval_IsReleasedAutomatically_NeverShownAsRemote()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        r.Hmi("192.168.1.24", "PR8-B");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");

        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.Connections[0].Lease.RequestId == "rq1", "申请已在 HMI 上");
        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseCancel));
        Assert.Equal("1号机 · 已取消控制权申请 · 若触屏已批准将自动释放", o.Message);   // R7 的说明
        Assert.Equal((LeaseStatus.Local, "本地 · 只读"), await r.On(() => (r.S(1).Lease.State, r.S(1).Lease.Display.Text)));   // 立即回到本地只读

        // 触屏之后才点「允许」：后台守卫查到批准 → 立即 POST /control/release（带那张租约），本机从没进入远程控制
        var seen = new List<LeaseStatus>();
        a.Approve = true;
        for (int i = 0; i < 200 && a.Released.Count == 0; i++)
        {
            seen.Add(await r.On(() => r.S(1).Lease.State));
            r.Clock.Advance(TimeSpan.FromSeconds(0.5));
            await Task.Delay(10);
        }
        await r.Until(() => r.Toasts.Any(t => t.Text.StartsWith("1号机 · 触屏在取消后批准了申请", StringComparison.Ordinal)), "守卫的结局");
        Assert.Equal(new string?[] { "L1" }, a.Released.ToArray());
        Assert.Contains(r.Toasts, t => t.Text == "1号机 · 触屏在取消后批准了申请 · 已自动释放" && t.Kind == ToastKind.Info);
        Assert.DoesNotContain(LeaseStatus.Remote, seen);
        Assert.Equal(LeaseStatus.Local, await r.On(() => r.S(1).Lease.State));
        Assert.Equal(0, a.Count("POST", "/control/heartbeat"));                 // 没人替这张租约心跳过
        Assert.Equal(1, a.Count("POST", "/control/request"));                   // 也没有自动重新申请
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== R8：被其他客户端持有、别人的申请在等

    [Fact]
    public async Task Lease_HeldByOtherClient_ThenOthersPendingRequest_FromTheMergedServerView()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.ControlMode = "remote";
        a.Holder = "PC-工位2";                                                  // 连上时 GET /control：别的客户端持有
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var l = await r.On(() => r.S(1).Lease);
        Assert.Equal((LeaseStatus.Other, "PC-工位2", "其他客户端 PC-工位2", "远程控制中（其他客户端 PC-工位2）"), (l.State, l.Holder, l.Display.BadgeShort, l.Display.Text));
        Assert.Equal("1号机远程控制中（其他客户端 PC-工位2）", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Equal("1号机远程控制中（其他客户端 PC-工位2）", await r.Why(1, DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));                 // 停止类不看控制权

        // 别人释放了（WS control）→ 本地只读，可以申请
        a.PushControl("released", "local", null, mode: "local");
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Local, "别人释放");
        Assert.Equal("", await r.Why(1, DeviceAction.LeaseRequest));

        // 别的客户端发了申请、在等触屏（WS control requested：申请人从 detail 解析）→ 本机的申请先禁用并写明是谁
        a.PushControl("requested", "requested", null, requestId: "rq-other", mode: "local", detail: "PC-工位3（令牌 op3）申请远程控制 · 等待触屏确认");
        await r.Until(() => r.Connections[0].Lease.PendingByOther, "别人的申请");
        Assert.Equal("1号机已有待处理的远程申请（PC-工位3）", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Equal(LeaseStatus.Local, await r.On(() => r.S(1).Lease.State));

        // 触屏拒绝了那条申请 → 又可以申请
        a.PushControl("denied", "local", null, requestId: "rq-other", mode: "local");
        await r.Until(() => !r.Connections[0].Lease.PendingByOther, "别人的申请结束");
        Assert.Equal("", await r.Why(1, DeviceAction.LeaseRequest));
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 触屏拒绝 → 冷却（守卫写明）

    [Fact]
    public async Task Lease_Denied_CooldownBlocksRequestWithCountdown()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Requested, "申请中");
        a.Deny = true;
        await r.AdvanceUntil(() => r.S(1).Lease.State == LeaseStatus.Local, "被拒");
        Assert.Contains(r.Toasts, t => t.Text == "1号机触屏已拒绝申请 · 30 s 后可再申请");
        Assert.Equal("1号机触屏已拒绝申请 · 30 s 后可再申请", await r.Why(1, DeviceAction.LeaseRequest));
        await r.Advance(10);
        Assert.Equal("1号机触屏已拒绝申请 · 20 s 后可再申请", await r.Why(1, DeviceAction.LeaseRequest));
        await r.Advance(21);
        Assert.Equal("", await r.Why(1, DeviceAction.LeaseRequest));   // 冷却过了
    }

    // ====================================================================== HMI 重启（bootId 变化）

    [Fact]
    public async Task BootIdChange_RefetchesDeviceAndSettings_ClearsDownloadedPrograms_ReconnectCountdownMeanwhile()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.AutoGrant = true;
        var b = r.Hmi("192.168.1.24", "PR8-B");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Remote, "autoGrant");
        var doc = new ProgramDoc { Name = "加氢测试-03" };
        doc.Add(ParallelReactor.Models.StepType.Wait, new { mode = "fixed", time = 0.3 });
        // 写命令之间有最小间隔（Client 的 MinWriteGap，挂在假时钟上）：边推进边等
        var upload = r.Ui.InvokeAsync(() => r.S(1).UploadProgramAsync(3, doc, CancellationToken.None));
        await r.AdvanceUntil(() => r.S(1).Downloaded(3) != null, "下发程序", stepSec: 0.05);
        Assert.True((await upload).Ok);
        await r.Until(() => a.Count("GET", "/settings") == 1, "连上后取过 /settings");
        int device0 = a.Count("GET", "/device");

        // 重启：WS 断开 → 连上过的设备「连接中断 · 重连中 N s」（Client 的退避倒计时），停止类仍尽力可发
        a.Restart("boot-2", serial: "PR8-A2");
        await r.Until(() => r.S(1).Conn.State == ConnStatus.Offline, "断线倒计时");
        var off = await r.On(() => r.S(1).Conn);
        Assert.True(off.EverOnline);
        Assert.Matches("^连接中断 · 重连中 [1-9] s$", off.Display.Text);
        Assert.Equal(1, off.Attempts);                                           // 样稿：断线时 attempts = 1
        Assert.True(off.StopReachable);
        Assert.Equal("", await r.Why(1, DeviceAction.StopAll));
        Assert.Matches("^1号机连接中断 · 重连中 [1-9] s$", await r.Why(1, DeviceAction.SetSp, new ActionPayload(N: 1, Sp: 50)));

        // 重连上：hello 带新的 bootId → 重取 /device 与 /settings、清掉本机下发记录、提示；控制权回到本地（不自动重新申请）
        await r.AdvanceUntil(() => OnlineState(r.D(1)) && r.S(1).Info?.BootId == "boot-2", "重启后重连并重取 /device");
        await r.Until(() => a.Count("GET", "/settings") == 2, "重取 /settings");
        Assert.True(a.Count("GET", "/device") > device0);
        Assert.Equal("PR8-A2", await r.On(() => r.D(1).Source.Sn));
        Assert.Null(await r.On(() => r.S(1).Downloaded(3)));
        Assert.Contains(r.Toasts, t => t.Text == "1号机 · 设备 HMI 已重启 · 曲线缓冲已清空");
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Local, "控制权回到本地");
        Assert.Contains(r.Toasts, t => t.Text == "1号机 · 重连后设备已回到本地模式 · 控制权失效");
        Assert.Equal(1, a.Count("POST", "/control/request"));                   // AutoReacquireLease = false
        Assert.Equal(1, b.Count("GET", "/device"));                             // 2号机没受影响
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 403 → 只读令牌（缓存）

    [Fact]
    public async Task Forbidden403_LearnsReadOnlyRole_PerDevice_KeptAcrossReconnect()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        a.Forbid.Add("POST /stop-all");
        b.Forbid.Add("POST /control/request");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");

        // 1号机：全部停止被 403（只读令牌、没有急停权限位）→ 停止类也禁用并写明
        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.StopAll));
        Assert.False(o.Ok);
        Assert.Equal("1号机 · 403 FORBIDDEN_ROLE：viewer 令牌只能读取 · 写命令需要 operator 及以上角色", o.Message);
        Assert.Equal((true, true), await r.On(() => (r.S(1).Role.ReadOnly, r.S(1).Role.NoEstop)));
        Assert.Equal(TokenRole.NoStopText("1号机"), await r.Why(1, DeviceAction.StopAll));
        Assert.Equal(TokenRole.ReadOnlyText("1号机"), await r.Why(1, DeviceAction.LeaseRequest));

        // 2号机：申请控制权被 403（Client 抛出、同时报 ForbiddenRole）→ 只读；停止类不受影响（没学到「没有急停权限位」）
        await r.Ui.InvokeAsync(() => r.D(2).Actions.RunAsync(DeviceAction.LeaseRequest));
        await r.Until(() => r.S(2).Role.ReadOnly, "学到只读");
        Assert.Contains(r.Toasts, t => t.Text == "2号机 · 403 FORBIDDEN_ROLE：viewer 令牌只能读取 · 写命令需要 operator 及以上角色" && t.Kind == ToastKind.Err);
        Assert.Equal(TokenRole.ReadOnlyText("2号机"), await r.Why(2, DeviceAction.LeaseRequest));
        Assert.Equal("", await r.Why(2, DeviceAction.StopAll));
        Assert.False(await r.On(() => r.S(2).Role.NoEstop));

        // 缓存到换令牌为止：断开再连，学到的照旧
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.Disconnect));
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.Reconnect));
        await r.Until(() => OnlineState(r.D(1)), "重新连上");
        Assert.True(await r.On(() => r.S(1).Role.ReadOnly && r.S(1).Role.NoEstop));
        Assert.Empty(r.Ui.Errors);
    }

    [Theory]
    [InlineData("POST", "control/heartbeat", DeviceAction.LeaseRequest)]
    [InlineData("POST", "control/request", DeviceAction.LeaseRequest)]
    [InlineData("POST", "estop", DeviceAction.Estop)]
    [InlineData("POST", "stop-all", DeviceAction.StopAll)]
    [InlineData("POST", "channels/3/stop", DeviceAction.Stop)]
    [InlineData("POST", "channels/3/heat/stop", DeviceAction.Stop)]
    [InlineData("DELETE", "channels/3/program/run", DeviceAction.ProgAbort)]
    [InlineData("POST", "channels/3/program/run", DeviceAction.Enable)]
    [InlineData("PUT", "channels/3/temperature/setpoint", DeviceAction.Enable)]
    [InlineData("POST", "alarms/12/ack", DeviceAction.AlarmAck)]
    [InlineData("POST", "sessions/current/markers", DeviceAction.AlarmAck)]
    [InlineData("POST", "run", DeviceAction.Enable)]
    public void ForbiddenRequest_MapsToTheRightPermissionClass(string method, string path, DeviceAction expected)
    {
        var a = LiveDeviceSource.ForbiddenAction(method, path);
        Assert.Equal(expected, a);
        Assert.Equal(TokenRole.PermissionOf(expected), TokenRole.PermissionOf(a!.Value));
    }

    [Fact]
    public void ForbiddenRequest_StirIsAmbiguous_NotLearned()
    {
        Assert.Null(LiveDeviceSource.ForbiddenAction("PUT", "stir"));   // 开 / 设定（控制类）与停止（停止类）同一个端点
        Assert.Null(LiveDeviceSource.ForbiddenAction("GET", "device"));
    }

    /// <summary>
    /// M3 复核（R9）：读请求不会因为角色被拒（viewer 就能读；HMI 的 GET /control/requests/{id} 也不查角色）—— 不学；只要 admin 的 PID 写入被拒（「该操作需要 admin 角色」）
    /// 说明不了 operator 的权限 —— 也不学（以前会把 operator 令牌当成只读）。
    /// </summary>
    [Fact]
    public void ForbiddenRequest_GetAndAdminOnly_AreNotLearned()
    {
        Assert.Null(LiveDeviceSource.ForbiddenAction("GET", "control/requests/rq1"));
        Assert.Null(LiveDeviceSource.ForbiddenAction("GET", "sessions/current"));
        Assert.Null(LiveDeviceSource.ForbiddenAction("GET", "alarms"));
        Assert.Null(LiveDeviceSource.ForbiddenAction("PUT", "channels/1/pid/0"));
        Assert.True(LiveDeviceSource.AdminOnly("该操作需要 admin 角色"));
        Assert.False(LiveDeviceSource.AdminOnly("viewer 令牌只能读取 · 写命令需要 operator 及以上角色"));
        Assert.False(LiveDeviceSource.AdminOnly(null));
    }

    // ====================================================================== 503 TOO_MANY_CLIENTS（R19）

    [Fact]
    public async Task Ws503TooManyClients_IsAReadableFailure_ThenOnlineWhenASlotFrees()
    {
        await using var r = new LiveRig();
        r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        b.WsFailStatus = 503;   // REST 通，WS 连接数已满
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)), "1号机上线");
        await r.Until(() => r.S(2).Conn.State == ConnStatus.Offline, "2号机：WS 握手被拒");
        const string why = "WebSocket 连接数已达上限 · 请关闭该设备上的其他客户端（HTTP 503 TOO_MANY_CLIENTS）";
        var c2 = await r.On(() => r.S(2).Conn);
        Assert.False(c2.EverOnline);                                            // REST 通了不算连上：实时数据没在流
        Assert.Matches("^连接失败 · 1 s 后重试$", c2.Display.Text);              // Client 的退避（0.5 s 起）
        // M3 复核：REST 起步成功、WS 握手被 HTTP 拒绝（主机答话了）—— REST 确实通，停止类照样能发（以前从未连上就只剩 none 类）；
        // 控制类、会话 / 确认报警照样不行
        Assert.True(c2.StopReachable);
        Assert.Equal("", await r.Why(2, DeviceAction.StopAll));
        Assert.Equal("", await r.Why(2, DeviceAction.Estop));
        Assert.Matches("^2号机连接失败 · [0-9]+ s 后重试$", await r.Why(2, DeviceAction.SessionStart));
        Assert.Matches("^2号机连接失败 · [0-9]+ s 后重试$", await r.Why(2, DeviceAction.HeatStart, new ActionPayload(N: 1, Sp: 50)));
        Assert.Equal(why, await r.On(() => r.S(2).LastError));
        Assert.Contains(r.Toasts, t => t.Text == "2号机 · 连接失败：" + why && t.Kind == ToastKind.Err);

        // 一直被拒：退避翻倍，同一原因只提示一次
        await r.AdvanceUntil(() => r.Connections[1].Ws.FailedAttempts >= 3, "又被拒了几次");
        Assert.Single(r.Toasts, t => t.Text.StartsWith("2号机 · 连接失败", StringComparison.Ordinal));
        Assert.Equal(1, r.Connections.Count(c => c.Options.BaseUrl.Host == "192.168.1.24"));   // 同一条连接（REST 起步只做了一次）、WS 自己重试

        // 腾出一个名额
        b.WsFailStatus = null;
        await r.AdvanceUntil(() => OnlineState(r.D(2)), "2号机上线");
        Assert.Null(await r.On(() => r.S(2).LastError));
        Assert.Empty(r.Ui.Errors);
    }

    // ====================================================================== 一批事件只刷新一次，全部在 UI 线程上

    [Fact]
    public async Task Frames_FromTwoDevices_AreCoalescedOnTheUiThread()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "两台上线");
        // 连上之后的读取（/settings、/alarms、/events、M4 的 /device/comm）与 hello 之后的 GET /control 都做完了，再开始数
        await FakeClock.WaitUntil(() => a.Count("GET", @"/events\?.*") == 1 && b.Count("GET", @"/events\?.*") == 1
            && a.Count("GET", "/device/comm") == 1 && b.Count("GET", "/device/comm") == 1
            && r.S(1).Comm != null && r.S(2).Comm != null
            && a.Count("GET", "/control") >= 2 && b.Count("GET", "/control") >= 2, what: "连上之后的读取");
        await Task.Delay(100);
        await r.On(() => { });
        int src1 = 0, src2 = 0, fleet = 0;
        await r.On(() =>
        {
            r.S(1).Changed += _ => src1++;
            r.S(2).Changed += _ => src2++;
            r.Fleet.Changed += (_, _) => fleet++;
        });
        using (r.Ui.Block())                                                    // UI 线程忙的时候来了一批帧
        {
            for (int i = 0; i < 20; i++) { a.PushFrame(5000 + i); b.PushFrame(7000 + i); }
            await FakeClock.WaitUntil(() => r.Connections[0].Telemetry.Latest?.Seq == 5019 && r.Connections[1].Telemetry.Latest?.Seq == 7019, what: "帧都到了");
        }
        await r.Until(() => r.S(1).Latest?.Seq == 5019 && r.S(2).Latest?.Seq == 7019, "应用最后一帧");
        await Task.Delay(50);
        Assert.Equal((1, 1, 1), await r.On(() => (src1, src2, fleet)));          // 每台一次、外框一次
        Assert.Equal(0, r.OffUi);
    }

    // ====================================================================== 释放：摘掉每一个处理器、不再重试

    [Fact]
    public async Task Dispose_DetachesEveryHandlerFromTheRealConnections_AndStopsRetrying()
    {
        await using var r = new LiveRig();
        r.Hmi("192.168.1.20", "PR8-A");
        var b = r.Hmi("192.168.1.24", "PR8-B");
        b.Down = true;
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.24", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && r.S(2).Conn.State == ConnStatus.Offline, "一台在线、一台在重试");
        var conn = r.Connections.Single(c => c.Options.BaseUrl.Host == "192.168.1.20");
        Assert.True(Handlers(conn) >= 12, "数据源挂了 12 个事件处理器");

        await r.Ui.InvokeAsync(async () => await r.Fleet.DisposeAsync());
        Assert.Equal(0, Handlers(conn));                                        // 每一个都摘掉了
        Assert.All(r.Connections, c => Assert.Equal(0, Handlers(c)));
        Assert.Equal(ConnectionState.Stopped, conn.Ws.State);
        Assert.False(await r.On(() => r.S(2).IsSupervising));
        int attempts = b.GateAttempts;
        await r.Advance(25);                                                     // 早过了重试时刻
        Assert.Equal(attempts, b.GateAttempts);
        Assert.Empty(r.Ui.Errors);
    }

    /// <summary>真 ReactorConnection 上挂着的事件处理器总数（字段式事件的后备字段）。</summary>
    private static int Handlers(ReactorConnection c)
    {
        int n = 0;
        foreach (var e in typeof(ReactorConnection).GetEvents(BindingFlags.Instance | BindingFlags.Public))
        {
            var f = typeof(ReactorConnection).GetField(e.Name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(c) is Delegate d) n += d.GetInvocationList().Length;
        }
        return n;
    }
}
