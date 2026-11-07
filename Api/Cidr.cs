using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace ParallelReactor.Api;

/// <summary>
/// 网段白名单（设计稿 §4.2）：IPv4 / IPv6 CIDR；IPv4 映射的 IPv6 地址（::ffff:a.b.c.d）按 IPv4 判定。
/// 列表为空时只允许本机回环地址。写错的条目忽略（<see cref="Invalid"/> 里列出，启动时记事件）。纯逻辑。
/// </summary>
public sealed class CidrMatcher
{
    private readonly List<(byte[] Net, int Bits)> _nets = new();

    /// <summary>无法解析的条目。</summary>
    public IReadOnlyList<string> Invalid { get; }

    /// <summary>有效网段数（0 = 只允许回环）。</summary>
    public int Count => _nets.Count;

    public CidrMatcher(IEnumerable<string>? cidrs)
    {
        var bad = new List<string>();
        foreach (var raw in cidrs ?? Array.Empty<string>())
        {
            if (TryParse(raw, out var net, out var bits)) _nets.Add((net, bits));
            else bad.Add(raw);
        }
        Invalid = bad;
    }

    /// <summary>解析 "a.b.c.d/24"、"10.0.0.5"（= /32）、"fd00::/8"。</summary>
    public static bool TryParse(string? s, out byte[] net, out int bits)
    {
        net = Array.Empty<byte>();
        bits = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        string ipPart = s;
        int? prefix = null;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            ipPart = s[..slash];
            if (!int.TryParse(s[(slash + 1)..], out var p)) return false;
            prefix = p;
        }
        if (!IPAddress.TryParse(ipPart, out var ip)) return false;
        ip = Normalize(ip);
        var bytes = ip.GetAddressBytes();
        int max = bytes.Length * 8;
        bits = prefix ?? max;
        if (bits < 0 || bits > max) return false;
        net = bytes;
        return true;
    }

    /// <summary>IPv4 映射的 IPv6 地址 → IPv4；其余原样。</summary>
    public static IPAddress Normalize(IPAddress ip)
        => ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>地址是否允许。null（无远端地址，如本地管道）按回环处理。</summary>
    public bool IsAllowed(IPAddress? ip)
    {
        if (ip == null) return true;
        ip = Normalize(ip);
        if (_nets.Count == 0) return IPAddress.IsLoopback(ip);
        var b = ip.GetAddressBytes();
        foreach (var (net, bits) in _nets)
        {
            if (net.Length != b.Length) continue;
            if (Matches(b, net, bits)) return true;
        }
        return false;
    }

    private static bool Matches(byte[] addr, byte[] net, int bits)
    {
        int full = bits / 8, rem = bits % 8;
        for (int i = 0; i < full; i++) if (addr[i] != net[i]) return false;
        if (rem == 0) return true;
        int mask = 0xFF << (8 - rem) & 0xFF;
        return (addr[full] & mask) == (net[full] & mask);
    }
}
