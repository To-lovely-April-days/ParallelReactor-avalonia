using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 本机配置 v2（Services/ControlConfig：devices.json，PORT-PLAN §5 M3）：v1 的 control.json 迁移成「1号机」、容错读、原子写、
/// 截图 / 演示时不写文件、PR_CONTROL_CONFIG 的两种写法、令牌按 D7 只在「记住令牌」时落盘（这台 Mac 上是 0600 的 tokens.json）。
/// 文件都写在测试输出目录下的临时目录里（Fakes/TestDir）。
/// </summary>
public class ConfigMigrationTests
{
    private static ControlConfig Load(TestDir t, bool canWrite = true) =>
        ControlConfig.Load(t.Path, t.File(ControlConfig.FileName), canWrite, TokenStoreMode.File);

    private static string V1(string host = "192.168.1.20", int port = 8080, bool remember = true, string? token = "tok-v1", bool auto = true) =>
        JsonSerializer.Serialize(new { host, port, clientName = "PC-工位2", rememberToken = remember, token, autoConnect = auto, timelineMinutes = 30 });

    [Fact]
    public void V1_MigratesTo_1号机_AndScrubsTheLegacyToken()
    {
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.LegacyFileName), V1());
        var cfg = Load(t);
        Assert.True(cfg.Migrated);
        Assert.Equal("PC-工位2", cfg.ClientName);
        Assert.Equal(30, cfg.TimelineMinutes);
        var d = Assert.Single(cfg.Devices);
        Assert.Equal(("1号机", "192.168.1.20", 8080, "tok-v1", true, true), (d.Alias, d.Host, d.Port, d.Token, d.RememberToken, d.AutoConnect));
        Assert.True(d.CanAutoConnect);
        Assert.Contains(cfg.LoadNotes, n => n.StartsWith("已把 control.json 迁移为 devices.json", StringComparison.Ordinal));
        // 迁移后马上保存：devices.json（不含令牌）+ tokens.json（0600）
        Assert.True(File.Exists(t.File(ControlConfig.FileName)));
        Assert.DoesNotContain("tok-v1", File.ReadAllText(t.File(ControlConfig.FileName)));
        Assert.Equal("tok-v1", TokenProtector.ReadFile(t.File(TokenProtector.FileName))[d.Id]);
        Assert.True(TokenProtector.IsOwnerOnly(t.File(TokenProtector.FileName)));
        // v1 里的明文令牌抹掉了，其余字段还在
        using var v1 = JsonDocument.Parse(File.ReadAllText(t.File(ControlConfig.LegacyFileName)));
        Assert.Equal(JsonValueKind.Null, v1.RootElement.GetProperty("token").ValueKind);
        Assert.Equal("192.168.1.20", v1.RootElement.GetProperty("host").GetString());
        // 再读一次：读的是 devices.json，令牌从 tokens.json 回来
        var again = Load(t);
        Assert.False(again.Migrated);
        var d2 = Assert.Single(again.Devices);
        Assert.Equal((d.Id, "1号机", "tok-v1", true), (d2.Id, d2.Alias, d2.Token, d2.CanAutoConnect));
    }

    [Fact]
    public void V1_WithoutRememberedToken_MigratesWithoutTokenAndWithoutAutoConnect()
    {
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.LegacyFileName), V1(remember: false, token: "visible", auto: true));
        var d = Assert.Single(Load(t).Devices);
        Assert.Null(d.Token);
        Assert.False(d.RememberToken);
        Assert.False(d.AutoConnect);                    // D7：自动连接要求存了令牌
        Assert.False(File.Exists(t.File(TokenProtector.FileName)));
    }

    [Fact]
    public void ShotOrDemo_NeverWrites_EvenWhenMigrating()
    {
        using var t = new TestDir();
        var legacy = V1();
        File.WriteAllText(t.File(ControlConfig.LegacyFileName), legacy);
        var cfg = Load(t, canWrite: false);
        Assert.Single(cfg.Devices);                     // 迁移在内存里照做
        Assert.False(cfg.Save());
        Assert.False(File.Exists(t.File(ControlConfig.FileName)));
        Assert.False(File.Exists(t.File(TokenProtector.FileName)));
        Assert.Equal(legacy, File.ReadAllText(t.File(ControlConfig.LegacyFileName)));   // control.json 原样
        Assert.False(ControlConfig.InMemory().CanWrite);
        Assert.False(ControlConfig.InMemory().Save());
    }

    [Fact]
    public void TolerantRead_FixesOrSkipsBadEntries()
    {
        using var t = new TestDir();
        var id = Guid.NewGuid();
        File.WriteAllText(t.File(ControlConfig.FileName), $$"""
        // 用户手改过的文件：注释、多余的逗号、大小写不一、类型不对
        {
          "Version": 2,
          "ClientName": "PC-手改",
          "devices": [
            { "Id": "{{id}}", "Alias": "1号机", "Host": " 10.0.0.1 ", "Port": "9000", "RememberToken": "true", "AutoConnect": true, },
            { "id": "{{id}}", "alias": "1号机", "host": "10.0.0.2", "port": 99999 },
            { "alias": "没有地址" },
            "不是设备",
            { "host": "10.0.0.3", "alias": "非常非常长的设备别名超过八个汉字" },
            { "host": "10.0.0.4", "alias": "   ", "autoConnect": "yes" },
          ],
        }
        """);
        var cfg = Load(t);
        Assert.Equal("PC-手改", cfg.ClientName);
        Assert.Equal(4, cfg.Devices.Count);
        var a = cfg.Devices[0];
        Assert.Equal((id, "1号机", "10.0.0.1", 9000, true), (a.Id, a.Alias, a.Host, a.Port, a.RememberToken));
        Assert.Null(a.Token);                               // 说记住了，但 tokens.json 里没有 → 要重新输入
        Assert.False(a.CanAutoConnect);                     // 没有令牌就不自动连接（D7）
        var b = cfg.Devices[1];
        Assert.NotEqual(id, b.Id);                          // 重复的 Id 重新生成
        Assert.Equal(("2号机", 8080), (b.Alias, b.Port));    // 重复的别名换成没用过的「k号机」，端口越界用 8080
        Assert.Equal("3号机", cfg.Devices[2].Alias);         // 太长的别名
        Assert.Equal("4号机", cfg.Devices[3].Alias);         // 空别名
        Assert.False(cfg.Devices[3].AutoConnect);            // "yes" 不是布尔
        Assert.Contains(cfg.LoadNotes, n => n.Contains("没有地址"));
        Assert.Contains(cfg.LoadNotes, n => n.Contains("不是设备"));
        Assert.Contains(cfg.LoadNotes, n => n.StartsWith("1号机 记住的令牌读不出来", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptFile_StartsEmpty_AndKeepsACopyBeforeTheFirstSave()
    {
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.FileName), "{ not json");
        var cfg = Load(t);
        Assert.Empty(cfg.Devices);
        Assert.Contains(cfg.LoadNotes, n => n.StartsWith("devices.json 无法读取", StringComparison.Ordinal));
        cfg.Devices.Add(new DeviceEntry { Alias = "1号机", Host = "10.0.0.9" });
        Assert.True(cfg.Save());
        Assert.Equal("{ not json", File.ReadAllText(t.File(ControlConfig.FileName + ".bad")));
        Assert.Equal("10.0.0.9", Assert.Single(Load(t).Devices).Host);
    }

    [Fact]
    public void Save_RoundTrips_Atomically_TokensOnlyWhenRemembered()
    {
        using var t = new TestDir();
        var cfg = Load(t);
        cfg.ClientName = "PC-保存";
        cfg.Devices.Add(new DeviceEntry { Alias = "甲", Host = "10.0.0.1", Port = 8081, Token = "keep", RememberToken = true, AutoConnect = true, LastSerial = "SN-1", LastModel = "PR8" });
        cfg.Devices.Add(new DeviceEntry { Alias = "乙", Host = "10.0.0.2", Token = "forget", RememberToken = false, AutoConnect = true });
        Assert.True(cfg.Save());
        Assert.True(cfg.Save());                                                   // 覆盖已有文件
        Assert.Empty(Directory.GetFiles(t.Path, "*.tmp", SearchOption.AllDirectories).Concat(Directory.GetFiles(t.Path, ".*.tmp")));
        var json = File.ReadAllText(t.File(ControlConfig.FileName));
        Assert.DoesNotContain("keep", json);
        Assert.DoesNotContain("forget", json);
        Assert.Contains("\"tokenStore\": \"file\"", json);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(t.File(ControlConfig.FileName)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(t.File(TokenProtector.FileName)));
        }
        var back = Load(t);
        Assert.Equal("PC-保存", back.ClientName);
        Assert.Equal(new[] { "甲", "乙" }, back.Devices.Select(d => d.Alias));                  // 顺序 = 数组顺序
        var a = back.Devices[0];
        Assert.Equal((cfg.Devices[0].Id, 8081, "keep", true, "SN-1", "PR8"), (a.Id, a.Port, a.Token, a.CanAutoConnect, a.LastSerial, a.LastModel));
        var b = back.Devices[1];
        Assert.Null(b.Token);                                                      // 没勾「记住令牌」：不落盘
        Assert.False(b.AutoConnect);                                               // 写回时 autoConnect 跟着 rememberToken
        Assert.Single(TokenProtector.ReadFile(t.File(TokenProtector.FileName)));
    }

    [Fact]
    public void Locate_DirectoryOrFile()
    {
        using var t = new TestDir();
        Assert.Equal((t.Path, Path.Combine(t.Path, ControlConfig.FileName)), ControlConfig.Locate(t.Path));
        var file = t.File("my-devices.json");
        Assert.Equal((t.Path, file), ControlConfig.Locate(file));                  // 以 .json 结尾：文件本身
        var dirNamedJson = Path.Combine(t.Path, "config.json");
        Directory.CreateDirectory(dirNamedJson);                                   // 已经存在的目录（名字以 .json 结尾）：照样当目录
        Assert.Equal((dirNamedJson, Path.Combine(dirNamedJson, ControlConfig.FileName)), ControlConfig.Locate(dirNamedJson));
        var cfg = ControlConfig.Load(t.Path, file, true, TokenStoreMode.File);
        cfg.Devices.Add(new DeviceEntry { Alias = "1号机", Host = "h" });
        Assert.True(cfg.Save());
        Assert.True(File.Exists(file));
    }

    // ———————————————————— M3 复核：本机客户端名每台电脑默认不一样 ————————————————————

    [Fact]
    public void FirstRun_ClientNameIsThisComputer_AndIsSavedRightAway()
    {
        using var t = new TestDir();
        var cfg = Load(t);
        var expect = ControlConfig.MachineClientName();
        Assert.Equal(expect, cfg.ClientName);
        Assert.StartsWith("PC-", expect, StringComparison.Ordinal);
        Assert.True(expect.Length <= ParallelReactor.Api.ClientNames.Max);
        Assert.Equal(ParallelReactor.Api.ClientNames.Sanitize(expect), expect);
        Assert.True(File.Exists(t.File(ControlConfig.FileName)));               // 立即写进 devices.json
        Assert.Equal(expect, Load(t).ClientName);
        Assert.Empty(cfg.LoadNotes);
        // 截图 / 演示：照样不写
        using var t2 = new TestDir();
        Assert.Equal(expect, Load(t2, canWrite: false).ClientName);
        Assert.False(File.Exists(t2.File(ControlConfig.FileName)));
    }

    [Fact]
    public void V1_WithTheOldSharedDefaultName_GetsThisComputersName_AndANote()
    {
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.LegacyFileName), JsonSerializer.Serialize(new { host = "10.0.0.5", port = 8080, clientName = "PC-测试工位1" }));
        var cfg = Load(t);
        Assert.Equal(ControlConfig.MachineClientName(), cfg.ClientName);
        if (cfg.ClientName != ControlConfig.DefaultClientName)
            Assert.Contains("本机客户端名改为「" + cfg.ClientName + "」· 可在「设备 › 本机客户端名…」中修改", cfg.LoadNotes);
    }

    [Fact]
    public void V2_WithoutClientName_UsesThisComputersName()
    {
        using var t = new TestDir();
        File.WriteAllText(t.File(ControlConfig.FileName), "{ \"version\": 2, \"devices\": [] }");
        Assert.Equal(ControlConfig.MachineClientName(), Load(t).ClientName);
        File.WriteAllText(t.File(ControlConfig.FileName), "{ \"version\": 2, \"clientName\": \"  PC-\u200b手改  \", \"devices\": [] }");
        Assert.Equal("PC-手改", Load(t).ClientName);                              // 按 HMI 的规则清洗（零宽字符去掉）
    }

    [Theory]
    [InlineData("PC-工位1", "")]
    [InlineData("   ", "客户端名不能为空")]
    [InlineData("\u200b", "客户端名不能为空")]
    public void ClientNameError_Rules(string name, string error) => Assert.Equal(error, ControlConfig.ClientNameError(name));

    [Fact]
    public void ClientNameError_TooLong() => Assert.Equal("客户端名太长 · 最多 64 个字符", ControlConfig.ClientNameError(new string('x', 65)));

}

/// <summary>改环境变量的测试单独一组、不并行（别的测试读 PR_CONTROL_* 时不受影响）。</summary>
[CollectionDefinition("Env", DisableParallelization = true)]
public sealed class EnvCollection { }

[Collection("Env")]
public class ConfigEnvironmentTests
{
    [Theory]
    [InlineData("PR_CONTROL_SHOT")]
    [InlineData("PR_CONTROL_DEMO")]
    public void ShotOrDemoEnvironment_DisablesWrites(string name)
    {
        using var t = new TestDir();
        var keep = (Environment.GetEnvironmentVariable(name), Environment.GetEnvironmentVariable("PR_CONTROL_CONFIG"));
        try
        {
            Environment.SetEnvironmentVariable("PR_CONTROL_CONFIG", t.Path);
            Environment.SetEnvironmentVariable(name, name == "PR_CONTROL_SHOT" ? t.Path : "1");
            Assert.True(ControlConfig.WritesDisabledByEnvironment);
            File.WriteAllText(t.File(ControlConfig.LegacyFileName), "{\"host\":\"10.1.1.1\",\"port\":8080}");
            var cfg = ControlConfig.Load();
            Assert.False(cfg.CanWrite);
            Assert.Equal(t.Path, cfg.DirectoryPath);
            Assert.Single(cfg.Devices);
            Assert.False(cfg.Save());
            Assert.False(File.Exists(t.File(ControlConfig.FileName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, keep.Item1);
            Environment.SetEnvironmentVariable("PR_CONTROL_CONFIG", keep.Item2);
        }
    }

    [Fact]
    public void DemoEnvironment_ProgramDraftsAreNotWritten()
    {
        using var t = new TestDir();
        var keep = (Environment.GetEnvironmentVariable("PR_CONTROL_DEMO"), Environment.GetEnvironmentVariable("PR_CONTROL_CONFIG"));
        try
        {
            Environment.SetEnvironmentVariable("PR_CONTROL_CONFIG", t.Path);
            Environment.SetEnvironmentVariable("PR_CONTROL_DEMO", "1");
            var store = new ProgramDraftStore();
            store.Set(1, store.Get(1));
            Assert.Empty(Directory.GetFiles(t.Path));                            // 交互演示不写 drafts.json（以前只看 PR_CONTROL_SHOT）
        }
        finally
        {
            Environment.SetEnvironmentVariable("PR_CONTROL_DEMO", keep.Item1);
            Environment.SetEnvironmentVariable("PR_CONTROL_CONFIG", keep.Item2);
        }
    }
}
