using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ParallelReactor.Api;

/// <summary>
/// 文件下载的路径安全（设计稿 §9.2）：按文件类型用白名单正则限定文件名，拒绝任何目录分隔符与 "..",
/// 最后再以 <see cref="Path.GetFullPath"/> 核对结果仍落在数据目录内。正则用 <c>\z</c> 收尾（<c>$</c> 会放过末尾的换行符）。纯逻辑，测试工程直接链接。
/// </summary>
public static class SafePath
{
    public const string Telemetry = "telemetry", Events = "events", Sessions = "sessions", Leak = "leak";

    public static readonly string[] Types = { Telemetry, Events, Sessions, Leak };

    private static readonly Regex TelemetryName = new(@"^telemetry-\d{8}(-\d{1,3})?\.csv\z", RegexOptions.CultureInvariant);
    private static readonly Regex EventsCsvName = new(@"^events-\d{8}(-\d{1,3})?\.csv\z", RegexOptions.CultureInvariant);
    private static readonly Regex EventsJsonlName = new(@"^events-\d{8}\.jsonl\z", RegexOptions.CultureInvariant);
    private static readonly Regex LeakName = new(@"^leak-\d{8}-\d{6}(\.csv|-summary\.csv|-summary\.json)\z", RegexOptions.CultureInvariant);
    // 会话文件名由 DataLogger.SafeName 生成（非法字符与空白已替换为下划线）：不含分隔符、不以点开头、长度受限
    private static readonly Regex SessionName = new(@"^(?!\.)[^/\\\0:*?""<>|\s]{1,200}\.csv\z", RegexOptions.CultureInvariant);

    /// <summary>泄漏测试 runId（Models/LeakRecord.cs：leak-yyyyMMdd-HHmmss）。</summary>
    public static readonly Regex LeakRunId = new(@"^leak-\d{8}-\d{6}\z", RegexOptions.CultureInvariant);

    /// <summary>文件名是否符合该类型的白名单。</summary>
    public static bool IsValidName(string type, string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains("..") || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) return false;
        if (name.IsNormalized(System.Text.NormalizationForm.FormC) == false) return false;
        return type switch
        {
            Telemetry => TelemetryName.IsMatch(name),
            Events => EventsCsvName.IsMatch(name) || EventsJsonlName.IsMatch(name),
            Sessions => SessionName.IsMatch(name),
            Leak => LeakName.IsMatch(name),
            _ => false,
        };
    }

    /// <summary>该类型文件所在的子目录（相对数据目录；根目录为 ""）。</summary>
    public static string SubDir(string type, string name) => type switch
    {
        Events => name.EndsWith(".jsonl", StringComparison.Ordinal) ? "events" : "",
        Sessions => "sessions",
        Leak => "leak",
        _ => "",
    };

    /// <summary>
    /// 解析为绝对路径：类型未知 / 文件名不合法 / 解析后不在数据目录内 → null。不检查文件是否存在。
    /// </summary>
    public static string? TryResolve(string root, string type, string? name)
    {
        if (!IsValidName(type, name)) return null;
        string rootFull;
        try { rootFull = Path.GetFullPath(root); } catch { return null; }
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar)) rootFull += Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootFull, SubDir(type, name!), name!)); } catch { return null; }
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }
}
