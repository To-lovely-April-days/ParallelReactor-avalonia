using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 令牌保护（Services/TokenProtector，D7）：macOS / Linux 的 0600 文件往返、权限、容错；Windows 的 DPAPI 往返（只在 Windows 上跑得到）；
/// 「启动时自动连接」要求令牌已经存在本机（没存时不自动连接、设备管理里勾不上）。
/// </summary>
public class TokenProtectorTests
{
    [Fact]
    public void File_RoundTrip_IsOwnerOnly_AndEmptyDeletesTheFile()
    {
        using var t = new TestDir();
        var path = t.File(TokenProtector.FileName);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        TokenProtector.WriteFile(path, new Dictionary<Guid, string> { [a] = "tok-a", [b] = "带\"引号\"的令牌" });
        var back = TokenProtector.ReadFile(path);
        Assert.Equal(2, back.Count);
        Assert.Equal("tok-a", back[a]);
        Assert.Equal("带\"引号\"的令牌", back[b]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));   // 0600：只有本用户可读写
            Assert.True(TokenProtector.IsOwnerOnly(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.False(TokenProtector.IsOwnerOnly(path));
            TokenProtector.WriteFile(path, new Dictionary<Guid, string> { [a] = "tok-a2" });              // 重写：又是 0600
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        TokenProtector.WriteFile(path, new Dictionary<Guid, string>());                                  // 没有要记住的令牌：文件删掉
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void File_Tolerant_MissingOrCorruptIsEmpty()
    {
        using var t = new TestDir();
        Assert.Empty(TokenProtector.ReadFile(t.File("none.json")));
        File.WriteAllText(t.File("bad.json"), "{ \"not-a-guid\": \"x\", ");
        Assert.Empty(TokenProtector.ReadFile(t.File("bad.json")));
        var id = Guid.NewGuid();
        File.WriteAllText(t.File("mixed.json"), "{ \"not-a-guid\": \"x\", \"" + id + "\": 5, \"" + Guid.NewGuid() + "\": \"ok\" }");
        Assert.Equal("ok", Assert.Single(TokenProtector.ReadFile(t.File("mixed.json"))).Value);
    }

    [Fact]
    public void Dpapi_RoundTrip_OnWindowsOnly()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? TokenStoreMode.Dpapi : TokenStoreMode.File, TokenProtector.CurrentMode);
        if (!OperatingSystem.IsWindows()) return;   // DPAPI 只有 Windows 有（这台 Mac 上测的是 0600 文件）
        var stored = TokenProtector.Protect("tok-secret");
        Assert.StartsWith(TokenProtector.DpapiPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-secret", stored);
        Assert.Equal("tok-secret", TokenProtector.Unprotect(stored));
        Assert.Null(TokenProtector.Unprotect("dpapi:AAAA"));
        Assert.Null(TokenProtector.Unprotect("plain"));
    }

    [Fact]
    public async Task AutoConnect_RequiresAStoredToken()
    {
        // 1. 添加时没勾「记住令牌」：自动连接一律不生效
        var cfg = ControlConfig.InMemory();
        var f = LiveFleet.Create(config: cfg);
        var d = f.AddDevice(new Fleet.NewDevice("10.0.0.1", 8080, "tok", "1号机", AutoConnect: true, RememberToken: false))!;
        var e = f.Registry!.ByK(d.K)!.Entry;
        Assert.False(e.AutoConnect);
        Assert.False(e.CanAutoConnect);
        Assert.Equal(DeviceRegistry.AutoConnectNeedsToken, f.AutoConnectReason(d.K));
        Assert.Equal(DeviceRegistry.AutoConnectNeedsToken, f.SetAutoConnect(d.K, true));   // 设备管理里勾不上
        Assert.False(e.AutoConnect);
        await f.DisposeAsync();

        // 2. 配置里勾了自动连接、也说记住了令牌，但令牌读不出来（tokens.json 没了）：启动时不连接这台
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.FileName),
            "{\"devices\":[{\"alias\":\"甲\",\"host\":\"10.0.0.1\",\"rememberToken\":true,\"autoConnect\":true}," +
            "{\"alias\":\"乙\",\"host\":\"10.0.0.2\",\"rememberToken\":false,\"autoConnect\":true}]}");
        var cfg2 = ControlConfig.Load(t.Path, t.File(ControlConfig.FileName), false, TokenStoreMode.File);
        Assert.All(cfg2.Devices, x => Assert.False(x.CanAutoConnect));
        var made = new List<FakeReactorConnection>();
        var f2 = LiveFleet.Create(made: made.Add, config: cfg2);
        await f2.StartAsync();
        Assert.Empty(made);                                                         // 一个连接都没建
        Assert.All(f2.Devices, x => Assert.Equal(Core.State.ConnStatus.Disconnected, x.Source.Conn.State));   // 没开始连：「已断开」，按钮「连接」
        await f2.DisposeAsync();
    }
}
