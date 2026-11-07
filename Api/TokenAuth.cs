using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ParallelReactor.Api;

/// <summary>鉴权结果：Ok 为 true 时带令牌名、角色、急停权限位。</summary>
public readonly record struct AuthResult(bool Ok, string Name, string Role, bool Estop)
{
    public static readonly AuthResult Denied = new(false, "", "", false);
}

/// <summary>
/// 预共享 Bearer 令牌校验（设计稿 §4.2）：请求头 <c>Authorization: Bearer &lt;token&gt;</c>，
/// 服务端只存 SHA-256；比较用 <see cref="CryptographicOperations.FixedTimeEquals"/>。纯逻辑，测试工程直接链接。
/// </summary>
public static class TokenAuth
{
    /// <summary>令牌明文 → SHA-256 小写十六进制。</summary>
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>从 Authorization 头取出 Bearer 令牌；格式不符返回 null。</summary>
    public static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var h = header.Trim();
        if (h.Length < 7 || !h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var tok = h[7..].Trim();
        return tok.Length == 0 ? null : tok;
    }

    /// <summary>按配置里的令牌表校验 Authorization 头。</summary>
    public static AuthResult Authenticate(string? authorizationHeader, ApiConfig cfg)
    {
        var tok = ExtractBearer(authorizationHeader);
        if (tok == null) return AuthResult.Denied;
        return AuthenticateToken(tok, cfg);
    }

    /// <summary>按配置里的令牌表校验令牌明文。所有条目都比较一遍（不因命中提前返回，时间与条目数无关）。</summary>
    public static AuthResult AuthenticateToken(string token, ApiConfig cfg)
    {
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        ApiToken? hit = null;
        foreach (var t in cfg.Tokens)
        {
            if (string.IsNullOrWhiteSpace(t.Sha256) || t.Sha256.Length != 64) continue;
            var want = Encoding.ASCII.GetBytes(t.Sha256.Trim().ToLowerInvariant());
            if (CryptographicOperations.FixedTimeEquals(hash, want)) hit ??= t;
        }
        if (hit == null) return AuthResult.Denied;
        var role = NormalizeRole(hit.Role);
        return new AuthResult(true, hit.Name, role, hit.Estop ?? role is "operator" or "admin");   // 未配置时 operator / admin 默认带急停权限（§4.2），显式 false 可收回
    }

    public static string NormalizeRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "operator" => "operator",
        "admin" => "admin",
        _ => "viewer",
    };

    /// <summary>角色等级：viewer &lt; operator &lt; admin。</summary>
    public static int Rank(string role) => NormalizeRole(role) switch { "admin" => 2, "operator" => 1, _ => 0 };

    /// <summary>该结果是否至少具备 required 角色。</summary>
    public static bool HasRole(AuthResult a, string required) => a.Ok && Rank(a.Role) >= Rank(required);
}

/// <summary>按键（如客户端 IP）限频：同一键在 <see cref="Window"/> 内只放行一次。鉴权失败的审计事件用，防刷日志。</summary>
public sealed class KeyedThrottle
{
    private readonly ConcurrentDictionary<string, long> _last = new();
    public TimeSpan Window { get; }

    public KeyedThrottle(TimeSpan window) => Window = window;

    /// <summary>该键现在是否放行（放行则记下时刻）。</summary>
    public bool Allow(string key, DateTime nowUtc)
    {
        long now = nowUtc.Ticks;
        long win = Window.Ticks;
        while (true)
        {
            if (!_last.TryGetValue(key, out var prev))
            {
                if (_last.TryAdd(key, now)) { Trim(now); return true; }
                continue;
            }
            if (now - prev < win) return false;
            if (_last.TryUpdate(key, now, prev)) return true;
        }
    }

    private void Trim(long now)
    {
        if (_last.Count < 1000) return;
        foreach (var kv in _last)
            if (now - kv.Value > Window.Ticks * 2) _last.TryRemove(kv.Key, out _);
    }
}

/// <summary>
/// 按键（客户端 IP）的固定窗口计数：同一键在 <see cref="Window"/> 内最多放行 <see cref="Limit"/> 次。
/// 用于鉴权失败的限速（§4.4）：未带 / 带错令牌的请求不进 ASP.NET 限流管线（那里按令牌分区），
/// 所以在网关里按 IP 给失败计数，超限的失败请求直接 429；有效令牌不受影响。纯逻辑。
/// </summary>
public sealed class FailureLimiter
{
    private readonly ConcurrentDictionary<string, (long WindowStart, int Count)> _m = new();
    public int Limit { get; }
    public TimeSpan Window { get; }

    public FailureLimiter(int limit, TimeSpan window) { Limit = limit; Window = window; }

    /// <summary>该键是否已超限（不计数；超限时本次请求应直接拒绝）。</summary>
    public bool IsBlocked(string key, DateTime nowUtc)
        => _m.TryGetValue(key, out var e) && nowUtc.Ticks - e.WindowStart < Window.Ticks && e.Count >= Limit;

    /// <summary>记一次失败；返回记完之后该键是否超限。</summary>
    public bool RecordFailure(string key, DateTime nowUtc)
    {
        long now = nowUtc.Ticks;
        var e = _m.AddOrUpdate(key, _ => (now, 1), (_, old) => now - old.WindowStart < Window.Ticks ? (old.WindowStart, old.Count + 1) : (now, 1));
        if (_m.Count > 1000) Trim(now);
        return e.Count >= Limit;
    }

    private void Trim(long now)
    {
        foreach (var kv in _m)
            if (now - kv.Value.WindowStart > Window.Ticks * 2) _m.TryRemove(kv.Key, out _);
    }
}
