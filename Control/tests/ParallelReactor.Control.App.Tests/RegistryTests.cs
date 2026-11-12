using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 多台设备的登记表（Services/DeviceRegistry，PORT-PLAN §5 M3）与 FleetVm 的实时设备列表：增 / 删 / 改 / 排序、K 在会话里不变、
/// 别名规则、启动时并行连接勾了自动连接且存了令牌的设备、退出时并行释放控制权（每台 ≤ 3 s、总共 ≤ 5 s）、连上后记下序列号与型号。
/// 设备都是假连接（Fakes/FakeReactorConnection），配置是内存里的或测试临时目录里的。
/// </summary>
public class RegistryTests
{
    private static NewDevice Dev(string alias, string host = "10.0.0.1", bool auto = false, bool remember = false, string token = "tok") =>
        new(host, 8080, token, alias, auto, remember);

    [Fact]
    public async Task AddRemoveRename_StableK_AliasRules()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(made: made.Add);
        Assert.Equal("1号机", f.FreeAlias());
        var a = f.AddDevice(Dev(f.FreeAlias(), "10.0.0.1"))!;
        var b = f.AddDevice(Dev(f.FreeAlias(), "10.0.0.2"))!;
        var c = f.AddDevice(Dev("测试台", "10.0.0.3"))!;
        Assert.Equal(new[] { 1, 2, 3 }, f.Devices.Select(d => d.K));
        Assert.Equal(new[] { "1号机", "2号机", "测试台" }, f.Devices.Select(d => d.Alias));
        Assert.Equal(3, made.Count);                                         // 添加即连接
        Assert.All(f.Devices, d => Assert.Equal(ConnStatus.Online, d.Source.Conn.State));
        // 别名规则（样稿 aliasError，逐字）
        Assert.Equal("名称已被其他设备使用（10.0.0.1）", f.AliasError("1号机", null));
        Assert.Equal("", f.AliasError("1号机", 1));                          // 跟自己不比
        Assert.Equal("名称不能为空", f.AliasError("  ", null));
        Assert.Equal("名称太长 · 最多 8 个汉字或 16 个字母", f.AliasError("九个汉字的设备别名啊", null));
        Assert.Null(f.AddDevice(Dev("1号机")));                              // 重复的别名：不添加
        // 移除 2号机：其它设备的 K 不变；再添加的取最大 + 1，默认别名是空出来的「2号机」
        var o = await b.Actions.RunAsync(Core.Guard.DeviceAction.Remove);
        Assert.True(o.Ok, o.Message);
        Assert.Equal("已移除 2号机", o.Message);
        Assert.True(made[1].Disposed);
        Assert.Equal(0, made[1].TotalHandlers);
        Assert.Equal(new[] { 1, 3 }, f.Devices.Select(d => d.K));
        Assert.Same(c, f.Device(3));
        Assert.Equal("2号机", f.FreeAlias());
        var d4 = f.AddDevice(Dev(f.FreeAlias(), "10.0.0.4"))!;
        Assert.Equal(4, d4.K);
        Assert.Equal(new[] { "1号机", "测试台", "2号机" }, f.Devices.Select(d => d.Alias));
        // 改名：本机的事（不发给 HMI），配置跟着改
        var r = await c.Actions.RunAsync(Core.Guard.DeviceAction.Rename, new Core.Guard.ActionPayload(Alias: " 反应台 "));
        Assert.Equal("反应台 · 已重命名（原名 测试台）", r.Message);
        Assert.Equal("反应台", c.Alias);
        Assert.Equal("反应台", f.Registry!.ByK(3)!.Entry.Alias);
        Assert.Equal("名称已被其他设备使用（10.0.0.1）", (await c.Actions.RunAsync(Core.Guard.DeviceAction.Rename, new Core.Guard.ActionPayload(Alias: "1号机"))).Message);
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Reorder_ChangesOrderNotK()
    {
        var f = LiveFleet.Create();
        foreach (var n in new[] { "甲", "乙", "丙" }) f.AddDevice(Dev(n, "10.0.0." + n.Length));
        var reg = f.Registry!;
        var bing = reg.ByK(3)!;
        reg.Move(bing.Id, 0);
        Assert.Equal(new[] { "丙", "甲", "乙" }, f.Devices.Select(d => d.Alias));
        Assert.Equal(new[] { 3, 1, 2 }, f.Devices.Select(d => d.K));                // K 不变（页签、路由照旧指向同一台）
        Assert.Equal(new[] { "丙", "甲", "乙" }, reg.Config.Devices.Select(d => d.Alias));   // devices.json 的顺序
        reg.Move(bing.Id, 99);
        Assert.Equal(new[] { "甲", "乙", "丙" }, f.Devices.Select(d => d.Alias));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Edit_NewAddress_ReleasesLeaseAndReconnects_NewTokenForgetsRole()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x => FakeReactorConnection.ScriptGrantedLease(x.Http), made.Add);
        var d = f.AddDevice(Dev("1号机", "10.0.0.1"))!;
        var first = made.Single();
        Assert.True(await first.Lease.AcquireAsync(System.Threading.CancellationToken.None));
        var live = (LiveDeviceSource)d.Source;
        live.LearnForbidden(Core.Guard.DeviceAction.HeatStart);
        Assert.True(live.Role.ReadOnly);
        Assert.Equal("名称不能为空", await f.EditDeviceAsync(d.K, new DeviceSettingsEdit(" ", "10.0.0.9", 8080, null, false, false)));
        Assert.Single(made);                                                       // 别名不合规：什么都没改
        var r = await f.EditDeviceAsync(d.K, new DeviceSettingsEdit("一号", "10.0.0.9", 9000, "tok-new", true, true));
        Assert.Equal("", r);
        Assert.Equal(1, first.Http.Count("POST", "/control/release"));            // 先释放本机的控制权
        Assert.True(first.Disposed);
        Assert.Equal(2, made.Count);                                               // 按新地址重新连接
        Assert.Equal(new Uri("http://10.0.0.9:9000"), made[1].Options.BaseUrl);
        Assert.Equal("tok-new", made[1].Options.Token);
        Assert.False(live.Role.IsLimited);                                         // 换了令牌：学到的权限作废
        var e = f.Registry!.ByK(d.K)!.Entry;
        Assert.Equal(("一号", "10.0.0.9", 9000, "tok-new", true, true), (e.Alias, e.Host, e.Port, e.Token, e.RememberToken, e.CanAutoConnect));
        Assert.Equal("一号", d.Alias);
        // 只改别名：不重连
        Assert.Equal("", await f.EditDeviceAsync(d.K, new DeviceSettingsEdit("一号机", "10.0.0.9", 9000, null, true, true)));
        Assert.Equal(2, made.Count);
        Assert.Equal("tok-new", e.Token);                                          // 令牌留空 = 不改
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Start_ConnectsAutoConnectDevicesInParallel()
    {
        using var t = new TestDir();
        var cfg = ControlConfig.Load(t.Path, t.File(ControlConfig.FileName), true, TokenStoreMode.File);
        for (int i = 1; i <= 3; i++)
            cfg.Devices.Add(new DeviceEntry { Alias = i + "号机", Host = "10.0.0." + i, Token = "tok" + i, RememberToken = true, AutoConnect = true });
        cfg.Devices.Add(new DeviceEntry { Alias = "4号机", Host = "10.0.0.4", Token = "tok4", RememberToken = true, AutoConnect = false });   // 不自动连接
        Assert.True(cfg.Save());
        var loaded = ControlConfig.Load(t.Path, t.File(ControlConfig.FileName), true, TokenStoreMode.File);   // 令牌从 tokens.json 回来
        var gate = new TaskCompletionSource();
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x => x.StartGate = gate.Task, made.Add, loaded);
        Assert.Equal(new[] { "1号机", "2号机", "3号机", "4号机" }, f.Devices.Select(d => d.Alias));
        Assert.All(f.Devices, d => Assert.Equal(ConnStatus.Disconnected, d.Source.Conn.State));
        var start = f.StartAsync();
        Assert.Equal(3, made.Count);                                               // 三台同时开始连（第一台还没连上，后两台已经在连）
        Assert.All(made, m => Assert.Equal(1, m.StartCalls));
        Assert.Equal(new[] { "tok1", "tok2", "tok3" }, made.Select(m => m.Options.Token));
        f.RefreshNow();
        Assert.Equal(new[] { ConnStatus.Connecting, ConnStatus.Connecting, ConnStatus.Connecting, ConnStatus.Disconnected }, f.Devices.Select(d => d.Source.Conn.State));
        Assert.Equal("正在连接…", f.Device(1)!.ConnText);                        // 从未连上
        Assert.False(start.IsCompleted);
        gate.SetResult();
        await start;
        f.RefreshNow();
        Assert.Equal(new[] { ConnStatus.Online, ConnStatus.Online, ConnStatus.Online, ConnStatus.Disconnected }, f.Devices.Select(d => d.Source.Conn.State));
        // 连上之后记下序列号与型号（设备离线时设备管理表照样有 SN）
        var sample = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        Assert.All(loaded.Devices.Take(3), e => Assert.Equal((sample.Serial, "PR8"), (e.LastSerial, e.LastModel)));
        Assert.Null(loaded.Devices[3].LastSerial);
        Assert.Contains(sample.Serial!, File.ReadAllText(t.File(ControlConfig.FileName)));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Start_FailedConnection_StaysInTheList()
    {
        var cfg = ControlConfig.InMemory();
        cfg.Devices.Add(new DeviceEntry { Alias = "1号机", Host = "10.0.0.1", Token = "tok", RememberToken = true, AutoConnect = true });
        var toasts = new List<string>();
        var f = LiveFleet.Create(x => x.StartError = new System.Net.Http.HttpRequestException("拒绝连接"), config: cfg);
        f.Toast += (s, _) => toasts.Add(s);
        await f.StartAsync();                                                     // 等第一次尝试的结果
        f.RefreshNow();
        var d = Assert.Single(f.Devices);
        // M3 重连监督：首次连接失败 → 断线倒计时重试（样稿「连接失败 · 2 s 后重试」，从未连上），不再停在「已断开」
        Assert.Equal(ConnStatus.Offline, d.Source.Conn.State);
        Assert.InRange(d.Source.Conn.RetryS, 1, 2);                               // LiveDeviceSource.RetryMin = 2 s（系统时钟）
        Assert.Matches("^连接失败 · [12] s 后重试$", d.ConnText);
        Assert.True(((LiveDeviceSource)d.Source).IsSupervising);
        Assert.False(d.EverOnline);
        Assert.Equal("网络错误：拒绝连接", ((LiveDeviceSource)d.Source).LastError);
        Assert.Contains("1号机 · 连接失败：网络错误：拒绝连接", toasts);
        Assert.Single(toasts, x => x.Contains("连接失败"));                       // 同一原因只提示一次
        Assert.Equal("", d.Why("reconnect"));                                     // 「立即重试」照样能点
        await f.DisposeAsync();
        Assert.False(((LiveDeviceSource)d.Source).IsSupervising);                 // 释放 = 停止重试
    }

    [Fact]
    public async Task Shutdown_ReleasesAllLeasesInParallel_WithinBudget()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x =>
        {
            FakeReactorConnection.ScriptGrantedLease(x.Http);
            x.Delays.Add(("POST", "/control/release", TimeSpan.FromSeconds(1)));
        }, made.Add);
        for (int i = 1; i <= 3; i++) f.AddDevice(Dev(i + "号机", "10.0.0." + i));
        foreach (var m in made) Assert.True(await m.Lease.AcquireAsync(System.Threading.CancellationToken.None));
        var sw = Stopwatch.StartNew();
        await f.DisposeAsync();
        sw.Stop();
        Assert.All(made, m => Assert.Equal(1, m.Http.Count("POST", "/control/release")));
        Assert.All(made, m => Assert.True(m.Disposed));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"三台各 1 s 的释放应当并行（用了 {sw.Elapsed.TotalSeconds:0.0} s）");
    }

    [Fact]
    public async Task Shutdown_SlowRelease_GivesUpAfterThreeSeconds()
    {
        var made = new List<FakeReactorConnection>();
        var f = LiveFleet.Create(x =>
        {
            FakeReactorConnection.ScriptGrantedLease(x.Http);
            x.Delays.Add(("POST", "/control/release", TimeSpan.FromSeconds(30)));   // HMI 不回应
        }, made.Add);
        f.AddDevice(Dev("1号机"));
        f.AddDevice(Dev("2号机", "10.0.0.2"));
        foreach (var m in made) Assert.True(await m.Lease.AcquireAsync(System.Threading.CancellationToken.None));
        var sw = Stopwatch.StartNew();
        await f.DisposeAsync();
        sw.Stop();
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.5, 5.5);                          // 每台 ≤ 3 s、总共 ≤ 5 s（App.axaml.cs 的关闭预算）
    }

    [Fact]
    public void Shell_DeviceList_FollowsTheRegistry()
    {
        var f = LiveFleet.Create();
        var shell = new ShellVm(f);
        Assert.Empty(shell.Fleet.Devices);
        f.AddDevice(Dev("1号机"));
        f.AddDevice(Dev("2号机", "10.0.0.2"));
        shell.Navigate(Route.Device(2));
        Assert.Equal(PageKind.Device, shell.Route.Page);
        Assert.Contains(shell.Tabs.Tabs, x => x.Id == "device/2");
        _ = f.Device(2)!.Actions.RunAsync(Core.Guard.DeviceAction.Remove);
        Assert.Null(f.Device(2));
        Assert.Equal(PageKind.Overview, shell.Route.Page);                        // 设备没了：回到总览（样稿 onDevices）
        Assert.DoesNotContain(shell.Tabs.Tabs, x => x.Id == "device/2");
        shell.Dispose();
    }
}
