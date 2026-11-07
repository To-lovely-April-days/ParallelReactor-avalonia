using System;
using System.Collections.Generic;
using System.IO;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>api.json 读取与环境变量覆盖（设计稿 §3.3）。</summary>
public class ApiConfigTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "pr-apicfg-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_DisabledAndLoopbackOnly()
    {
        var c = ApiConfig.Load(Path.Combine(Tmp(), "api.json"), out var err);
        Assert.Null(err);
        Assert.False(c.Enabled);
        Assert.False(c.FileFound);
        Assert.Equal("0.0.0.0", c.Bind);
        Assert.Equal(8080, c.HttpPort);
        Assert.Empty(c.AllowedCidrs);
        Assert.Empty(c.Tokens);
        Assert.Equal(4, c.MaxClients);
        Assert.Null(c.Serial);
    }

    [Fact]
    public void Load_ParsesSchema()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "api.json");
        File.WriteAllText(path, """
            {
              // 注释与尾随逗号都允许
              "enabled": true, "bind": "0.0.0.0", "httpPort": 18080,
              "allowedCidrs": ["192.168.10.0/24"],
              "tokens": [ { "name": "PC-测试工位1", "sha256": "9F2C", "role": "Operator", "estop": true } ],
              "maxClients": 2, "serial": "PR8-2026-0001",
            }
            """);
        var c = ApiConfig.Load(path, out var err);
        Assert.Null(err);
        Assert.True(c.Enabled);
        Assert.True(c.FileFound);
        Assert.Equal(path, c.SourcePath);
        Assert.Equal(18080, c.HttpPort);
        Assert.Equal(new List<string> { "192.168.10.0/24" }, c.AllowedCidrs);
        Assert.Single(c.Tokens);
        Assert.Equal("operator", c.Tokens[0].Role);
        Assert.True(c.Tokens[0].Estop);
        Assert.Equal(2, c.MaxClients);
        Assert.Equal("PR8-2026-0001", c.Serial);
    }

    [Fact]
    public void Load_CorruptJson_DefaultsWithError()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "api.json");
        File.WriteAllText(path, "{ not json");
        var c = ApiConfig.Load(path, out var err);
        Assert.NotNull(err);
        Assert.Contains("api.json", err);
        Assert.DoesNotContain(dir, err);   // 事件文字只给文件名，不把路径暴露给接口客户端
        Assert.False(c.Enabled);
    }

    [Fact]
    public void ApplyEnv_OverridesEnabledAndPort()
    {
        var env = new Dictionary<string, string?> { ["PR_API"] = "1", ["PR_API_PORT"] = "18080" };
        var c = new ApiConfig().ApplyEnv(k => env.TryGetValue(k, out var v) ? v : null);
        Assert.True(c.Enabled);
        Assert.Equal(18080, c.HttpPort);

        env = new Dictionary<string, string?> { ["PR_API"] = "0", ["PR_API_PORT"] = "notaport" };
        c = new ApiConfig { Enabled = true, HttpPort = 9000 }.ApplyEnv(k => env.TryGetValue(k, out var v) ? v : null);
        Assert.False(c.Enabled);
        Assert.Equal(9000, c.HttpPort);

        c = new ApiConfig { Enabled = true }.ApplyEnv(_ => null);
        Assert.True(c.Enabled);
    }

    [Fact]
    public void Example_RoundTrips()
    {
        var dir = Tmp();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "api.json");
        File.WriteAllText(path, ApiConfig.Example);
        var c = ApiConfig.Load(path, out var err);
        Assert.Null(err);
        Assert.True(c.Enabled);
        Assert.Single(c.Tokens);
    }
}
