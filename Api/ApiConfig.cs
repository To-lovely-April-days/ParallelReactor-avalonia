using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.Api;

/// <summary>一枚预共享令牌（设计稿 §3.3 / §4.2）：只存 SHA-256，不存明文。</summary>
public sealed class ApiToken
{
    public string Name { get; set; } = "";
    /// <summary>令牌明文的 SHA-256（64 位十六进制，大小写不限）。</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>viewer / operator / admin。P2 只读接口任何角色都可读。</summary>
    public string Role { get; set; } = "viewer";
    /// <summary>独立的急停权限位（§4.2）。null = 按角色默认（operator / admin 有、viewer 无）；显式 false 可收回 operator / admin 的急停权限。</summary>
    public bool? Estop { get; set; }
}

/// <summary>
/// 对外接口的部署参数 <c>api.json</c>（设计稿 §3.3），与 <c>settings.json</c> 同目录（模拟模式用 <c>api-sim.json</c>）。
/// 缺文件 / 文件损坏一律回到默认值（<see cref="Enabled"/> = false：完全不启动 Kestrel）。
/// 开发期可用环境变量覆盖：<c>PR_API=1</c> 强制启用，<c>PR_API_PORT</c> 覆盖端口。纯逻辑，测试工程直接链接。
/// </summary>
public sealed class ApiConfig
{
    public bool Enabled { get; set; }
    public string Bind { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 8080;
    /// <summary>P2 未实现 HTTPS：配置了只记一条 warn 事件，监听仍只有 HTTP。</summary>
    public int HttpsPort { get; set; }
    public string? CertPath { get; set; }
    /// <summary>白名单网段；为空时只允许本机回环地址。</summary>
    public List<string> AllowedCidrs { get; set; } = new();
    public List<ApiToken> Tokens { get; set; } = new();
    /// <summary>持有者心跳超时（§5.5）：超过此秒数未收到带租约的心跳进入 RemoteLost。</summary>
    public int HeartbeatTimeoutSec { get; set; } = 10;
    /// <summary>RemoteLost 宽限（§5.3）：期内凭同一 leaseId 心跳即恢复；期满租约作废、回到本地并执行 <see cref="OnRemoteLost"/>。</summary>
    public int LeaseGraceSec { get; set; } = 60;
    /// <summary>宽限期满后的动作（§5.4）：keep（默认）/ stopHeat / estop。非法值按 keep 并记 warn。</summary>
    public string OnRemoteLost { get; set; } = "keep";
    /// <summary>远程申请控制权时免触屏确认直接授权（无人值守 / 验收用；§15 Q2 的「本地预先授权」）。默认 false。</summary>
    public bool AutoGrantRemote { get; set; }
    /// <summary>触屏「允许 / 拒绝」弹窗的等待秒数，超时 = 拒绝（P3 定为 60 s，见 §14.1 偏差）。</summary>
    public int ControlRequestTimeoutSec { get; set; } = 60;
    /// <summary>写命令限流（每令牌每秒；§4.4）。/estop 不限流。</summary>
    public int WriteRateLimitPerSec { get; set; } = 10;

    public static readonly string[] RemoteLostPolicies = { "keep", "stopHeat", "estop" };
    /// <summary>WebSocket 并发连接上限。</summary>
    public int MaxClients { get; set; } = 4;
    /// <summary>设备序列号（/device 的 serial）；未配置为 null。</summary>
    public string? Serial { get; set; }

    /// <summary>读取时的文件路径（不序列化；启动事件里提示现场该改哪个文件）。</summary>
    [JsonIgnore] public string? SourcePath { get; set; }
    /// <summary>文件是否存在（不序列化）。</summary>
    [JsonIgnore] public bool FileFound { get; set; }
    /// <summary>读取时发现并已修正的非致命问题（启动时记 warn 事件；不序列化）。</summary>
    [JsonIgnore] public List<string> Warnings { get; } = new();
    /// <summary>配置文件名（不带目录；事件文字里用它，不把文件系统路径暴露给接口客户端）。</summary>
    [JsonIgnore] public string SourceFileName => SourcePath is { } p ? Path.GetFileName(p) : "api.json";

    /// <summary>读取配置：文件不存在 → 默认值（error = null）；JSON 损坏 → 默认值并给出错误文字。</summary>
    public static ApiConfig Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return new ApiConfig { SourcePath = path };
            var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), ApiConfigJsonContext.Default.ApiConfig);
            if (cfg == null) { error = $"{Path.GetFileName(path)}：内容为空"; return new ApiConfig { SourcePath = path }; }
            cfg.SourcePath = path;
            cfg.FileFound = true;
            cfg.AllowedCidrs ??= new();
            cfg.Tokens ??= new();
            foreach (var t in cfg.Tokens)
            {
                t.Name ??= "";
                t.Sha256 ??= "";
                t.Role = string.IsNullOrWhiteSpace(t.Role) ? "viewer" : t.Role.Trim().ToLowerInvariant();
            }
            if (cfg.MaxClients <= 0) cfg.MaxClients = 4;
            if (string.IsNullOrWhiteSpace(cfg.Bind)) cfg.Bind = "0.0.0.0";
            if (cfg.HeartbeatTimeoutSec <= 0) cfg.HeartbeatTimeoutSec = 10;
            if (cfg.LeaseGraceSec < 0) cfg.LeaseGraceSec = 60;
            if (cfg.ControlRequestTimeoutSec <= 0) cfg.ControlRequestTimeoutSec = 60;
            if (cfg.WriteRateLimitPerSec <= 0) cfg.WriteRateLimitPerSec = 10;
            var policy = Array.Find(RemoteLostPolicies, p => string.Equals(p, cfg.OnRemoteLost?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (policy == null)
            {
                cfg.Warnings.Add($"onRemoteLost「{cfg.OnRemoteLost}」不是 keep / stopHeat / estop · 按 keep");
                policy = "keep";
            }
            cfg.OnRemoteLost = policy;
            return cfg;
        }
        catch (Exception ex)
        {
            error = $"{Path.GetFileName(path)}：{ex.Message}";
            return new ApiConfig { SourcePath = path };
        }
    }

    /// <summary>叠加环境变量：PR_API=1 → Enabled；PR_API_PORT=n → HttpPort。</summary>
    public ApiConfig ApplyEnv(Func<string, string?> env)
    {
        var api = env("PR_API");
        if (!string.IsNullOrWhiteSpace(api) && api.Trim() is "1" or "true") Enabled = true;
        else if (!string.IsNullOrWhiteSpace(api) && api.Trim() is "0" or "false") Enabled = false;
        var port = env("PR_API_PORT");
        if (int.TryParse(port, out var p) && p >= 0 && p <= 65535) HttpPort = p;
        // 验收便利：PR_API_AUTOGRANT=1 → 远程申请免触屏确认（与 api.json 的 autoGrantRemote 等价）
        var ag = env("PR_API_AUTOGRANT");
        if (!string.IsNullOrWhiteSpace(ag) && ag.Trim() is "1" or "true") AutoGrantRemote = true;
        return this;
    }

    /// <summary>读文件 + 叠加进程环境变量。</summary>
    public static ApiConfig LoadWithEnv(string path, out string? error)
        => Load(path, out error).ApplyEnv(Environment.GetEnvironmentVariable);

    /// <summary>示例文件内容（文档 / 首次部署用）。</summary>
    public static string Example => JsonSerializer.Serialize(new ApiConfig
    {
        Enabled = true,
        AllowedCidrs = new() { "192.168.10.0/24" },
        Tokens = new() { new ApiToken { Name = "PC-测试工位1", Sha256 = new string('0', 64), Role = "viewer", Estop = false } },
        Serial = "PR8-2026-0001",
    }, ApiConfigJsonContext.Default.ApiConfig);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ApiConfig))]
[JsonSerializable(typeof(ApiToken))]
internal partial class ApiConfigJsonContext : JsonSerializerContext
{
}
