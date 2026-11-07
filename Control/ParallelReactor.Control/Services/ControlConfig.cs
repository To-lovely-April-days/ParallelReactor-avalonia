using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.Control.Services;

/// <summary>
/// PC 端本机配置 control.json：地址 / 端口 / 客户端名（令牌默认不落盘，RememberToken=true 才存）。
/// 目录：PR_CONTROL_CONFIG，否则 macOS ~/Library/Application Support/ParallelReactor.Control，其他平台 ApplicationData/ParallelReactor.Control。
/// 绝不写入触屏 HMI 的 ParallelReactor 目录。截图模式（PR_CONTROL_SHOT）不保存。
/// </summary>
public sealed class ControlConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string ClientName { get; set; } = "PC-测试工位1";
    public bool RememberToken { get; set; }
    public string? Token { get; set; }
    public bool AutoConnect { get; set; }
    /// <summary>时间轴窗口（分钟，默认 20）。</summary>
    public int TimelineMinutes { get; set; } = 20;

    [JsonIgnore] public string BaseUrl => $"http://{Host}:{Port}";

    public static string Directory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PR_CONTROL_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ParallelReactor.Control");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParallelReactor.Control");
        }
    }

    public static string FilePath => Path.Combine(Directory, "control.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static ControlConfig Load()
    {
        var cfg = new ControlConfig();
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<ControlConfig>(File.ReadAllText(FilePath), Json) ?? cfg;
        }
        catch { /* 配置损坏 → 默认值 */ }
        // 截图 / 验收用环境变量覆盖
        var url = Environment.GetEnvironmentVariable("PR_CONTROL_URL");
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u)) { cfg.Host = u.Host; cfg.Port = u.Port; }
        var tok = Environment.GetEnvironmentVariable("PR_CONTROL_TOKEN");
        if (!string.IsNullOrWhiteSpace(tok)) cfg.Token = tok;
        var name = Environment.GetEnvironmentVariable("PR_CONTROL_CLIENT");
        if (!string.IsNullOrWhiteSpace(name)) cfg.ClientName = name;
        return cfg;
    }

    public void Save()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT"))) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var copy = new ControlConfig { Host = Host, Port = Port, ClientName = ClientName, RememberToken = RememberToken, Token = RememberToken ? Token : null, AutoConnect = AutoConnect, TimelineMinutes = TimelineMinutes };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, Json));
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 令牌明文：仅本用户可读
        }
        catch { /* 只读目录等：忽略 */ }
    }
}
