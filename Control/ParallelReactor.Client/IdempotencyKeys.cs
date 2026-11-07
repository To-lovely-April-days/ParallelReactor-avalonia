using System;
using System.Globalization;

namespace ParallelReactor.Client;

/// <summary>幂等键（§13）：每条写命令一个新键；同一命令重试必须复用同一个键。服务端把键当 opId（≤ 128 字符）。</summary>
public static class IdempotencyKeys
{
    /// <summary>随机键（32 位 hex）。</summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>带前缀的键（便于在事件流里辨认：如 "pc-run-rv1-20261107T061030Z-3f2a"），总长 ≤ 128。</summary>
    public static string New(string prefix)
    {
        var p = (prefix ?? "").Trim();
        foreach (var ch in p) if (char.IsControl(ch) || char.IsWhiteSpace(ch)) throw new ArgumentException("前缀不能含空白 / 控制字符", nameof(prefix));
        var k = (p.Length > 0 ? p + "-" : "") + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        return k.Length <= 128 ? k : k[^128..];
    }

    public static bool IsValid(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 128;
}
