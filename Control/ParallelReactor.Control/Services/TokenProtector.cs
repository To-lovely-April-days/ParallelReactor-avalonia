using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ParallelReactor.Control.Services;

/// <summary>令牌怎么落盘（D7）：Windows = DPAPI 加密后写进 devices.json；macOS / Linux = 仅本用户可读（0600）的 tokens.json（<b>明文</b>）。</summary>
public enum TokenStoreMode { Dpapi, File }

/// <summary>
/// 访问令牌的保护（PORT-PLAN D7、R18）。只有用户在「添加设备 / 编辑设置」里勾了「记住令牌」的设备才落盘；没勾的令牌只在内存里，
/// 下次启动要重新输入（「启动时自动连接」因此要求已经记住了令牌，见 ControlConfig / DeviceRegistry）。
/// <list type="bullet">
/// <item><b>Windows</b>：DPAPI（CryptProtectData，CurrentUser 范围、不弹界面、带本程序的附加熵），密文 base64 写在 devices.json 这台设备的 token 字段
///   （前缀「dpapi:」）。只有同一个 Windows 用户在同一台电脑上能解开；换电脑 / 换用户时解不开 → 当作没记住（要重新输入）。
///   直接 P/Invoke crypt32.dll（System.Security.Cryptography.ProtectedData 是额外的 NuGet 包，本工程不引入新包）。</item>
/// <item><b>macOS / Linux</b>：<b>不加密</b>。令牌以明文写在 devices.json 旁边的 tokens.json（{ 设备 Id: 令牌 }），文件权限 0600（只有本用户可读写，
///   先以 0600 新建临时文件再改名替换，任何时刻都不会出现别人可读的副本）。系统钥匙串需要额外依赖，本轮不做；
///   这台电脑的管理员和能以本用户身份运行程序的人都能读到它 —— 共用账户的电脑上不要勾「记住令牌」。</item>
/// </list>
/// 两种方式都不把令牌写进日志、截图或 devices.json 的明文字段。
/// </summary>
public static class TokenProtector
{
    /// <summary>macOS / Linux 的令牌文件名（与 devices.json 同目录）。</summary>
    public const string FileName = "tokens.json";

    /// <summary>devices.json 里 DPAPI 密文的前缀。</summary>
    public const string DpapiPrefix = "dpapi:";

    /// <summary>本机用的方式：Windows = DPAPI，其它 = 0600 文件。</summary>
    public static TokenStoreMode CurrentMode => OperatingSystem.IsWindows() ? TokenStoreMode.Dpapi : TokenStoreMode.File;

    // ———————————————————— DPAPI（Windows） ————————————————————

    /// <summary>附加熵：别的程序用同一个用户的 DPAPI 也解不开（不是秘密，只是把用途分开）。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ParallelReactor.Control/token/v2");

    /// <summary>加密（Windows）：返回「dpapi:」+ base64 密文。</summary>
    [SupportedOSPlatform("windows")]
    public static string Protect(string token)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = Crypt(plain, protect: true);
        return DpapiPrefix + Convert.ToBase64String(cipher);
    }

    /// <summary>解密（Windows）：格式不对、换了用户 / 电脑解不开时返回 null（当作没记住）。</summary>
    [SupportedOSPlatform("windows")]
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            var plain = Crypt(cipher, protect: false);
            var s = Encoding.UTF8.GetString(plain);
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [SupportedOSPlatform("windows")]
    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var dataIn = new DataBlob { Size = input.Length, Data = inH.AddrOfPinnedObject() };
        var entropy = new DataBlob { Size = Entropy.Length, Data = enH.AddrOfPinnedObject() };
        var dataOut = new DataBlob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref dataIn, "ParallelReactor.Control", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref dataOut);
            if (!ok) throw new InvalidOperationException("DPAPI 失败（" + Marshal.GetLastWin32Error() + "）");
            var result = new byte[dataOut.Size];
            Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
            return result;
        }
        finally
        {
            if (dataOut.Data != IntPtr.Zero) LocalFree(dataOut.Data);
            inH.Free();
            enH.Free();
        }
    }

    // ———————————————————— 0600 文件（macOS / Linux） ————————————————————

    /// <summary>读 tokens.json（{ "设备 Id": "令牌" }）。没有文件、读不了、格式坏了都返回空表（当作没记住，不抛）。</summary>
    public static Dictionary<Guid, string> ReadFile(string path)
    {
        var map = new Dictionary<Guid, string>();
        try
        {
            if (!System.IO.File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path), ControlConfig.TolerantJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (Guid.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } t)
                    map[id] = t;
        }
        catch { }
        return map;
    }

    /// <summary>
    /// 写 tokens.json：先以 0600 新建临时文件写完再改名替换（原子；不会出现半个文件或别人可读的副本），没有要记住的令牌时删掉这个文件。
    /// </summary>
    public static void WriteFile(string path, IReadOnlyDictionary<Guid, string> tokens)
    {
        if (tokens.Count == 0)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return;
        }
        var sb = new StringBuilder("{\n");
        int i = 0;
        foreach (var kv in tokens)
        {
            sb.Append("  ").Append(JsonSerializer.Serialize(kv.Key.ToString("D"))).Append(": ").Append(JsonSerializer.Serialize(kv.Value));
            sb.Append(++i < tokens.Count ? ",\n" : "\n");
        }
        sb.Append("}\n");
        ControlConfig.WriteAtomic(path, sb.ToString(), ownerOnly: true);
    }

    /// <summary>文件只有本用户能读写（0600）。Windows 上恒为 true（那边不用这个文件）。</summary>
    public static bool IsOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var m = System.IO.File.GetUnixFileMode(path);
        return (m & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }
}
