using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>GET /files 的一项。from / to 为文件首末行时间（UTC；解析不到为 null）。</summary>
public sealed record HistoryFileInfo(string Name, string Type, long Bytes, DateTime ModifiedAt, DateTime? From, DateTime? To, string? Day);

/// <summary>
/// 历史文件的枚举与按时间段读取（设计稿 §9）。全部只读；打开文件一律 <c>FileShare.ReadWrite</c>（DataLogger 正在追加当天文件）；
/// 文件末尾可能有半行（进程被杀时留下），读取时跳过未以换行结束的最后一行。
/// <c>/files</c> 的首末行时间只读文件头两行与末尾一块（不整文件扫），并按（长度, 修改时间）缓存：当天文件每 1.2 s 变一次，历史文件命中缓存。
/// <c>/telemetry</c> 跨表头不同的文件（P0 表头变化另起 -1 / -2 文件）时按列名映射到第一个表头（缺列留空、多出的列丢弃），不按列位置拼接。
/// 纯逻辑，测试工程直接链接。
/// </summary>
public static class HistoryFiles
{
    private static readonly Regex DailyName = new(@"^(telemetry|events)-(\d{8})(?:-(\d{1,3}))?\.(csv|jsonl)\z", RegexOptions.CultureInvariant);

    /// <summary>telemetry-20261106-1.csv → (20261106, 1)；不匹配为 null。</summary>
    public static (string Day, int Suffix)? DayOf(string name)
    {
        var m = DailyName.Match(name);
        if (!m.Success) return null;
        int suffix = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return (m.Groups[2].Value, suffix);
    }

    /// <summary>列出某类文件（名称按 ordinal 升序）。</summary>
    public static List<HistoryFileInfo> List(string dataDir, string type, bool parseRange = true)
    {
        var list = new List<HistoryFileInfo>();
        IEnumerable<string> files = type switch
        {
            SafePath.Telemetry => Enum(dataDir, "telemetry-*.csv"),
            SafePath.Events => Enum(dataDir, "events-*.csv").Concat(Enum(Path.Combine(dataDir, "events"), "events-*.jsonl")),
            SafePath.Sessions => Enum(Path.Combine(dataDir, "sessions"), "*.csv"),
            SafePath.Leak => Enum(Path.Combine(dataDir, "leak"), "leak-*"),
            _ => Array.Empty<string>(),
        };
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (!SafePath.IsValidName(type, name)) continue;
            FileInfo fi;
            try { fi = new FileInfo(f); if (!fi.Exists) continue; } catch { continue; }
            DateTime? from = null, to = null;
            if (parseRange && name.EndsWith(".csv", StringComparison.Ordinal) && type is SafePath.Telemetry or SafePath.Events or SafePath.Sessions)
            {
                try { (from, to) = FirstLastTimestamp(f); } catch { }
            }
            list.Add(new HistoryFileInfo(name, type, fi.Length, fi.LastWriteTimeUtc, from, to, DayOf(name)?.Day));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static IEnumerable<string> Enum(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 选出覆盖 [from, to) 的日文件（按本地日期滚动，见 DataLogger）：日期 ∈ [from 的本地日期, (to − 1 tick) 的本地日期]，含 -1 / -2 后缀，
    /// 按日期、后缀升序。
    /// </summary>
    public static List<string> SelectDailyFiles(string dataDir, DateTimeOffset from, DateTimeOffset to, string prefix = "telemetry")
    {
        var result = new List<(string Day, int Suffix, string Path)>();
        if (to <= from || !Directory.Exists(dataDir)) return new List<string>();
        string dFrom = from.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string dTo = to.AddTicks(-1).ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var f in Enum(dataDir, prefix + "-*.csv"))
        {
            var d = DayOf(Path.GetFileName(f));
            if (d == null) continue;
            if (string.CompareOrdinal(d.Value.Day, dFrom) < 0 || string.CompareOrdinal(d.Value.Day, dTo) > 0) continue;
            result.Add((d.Value.Day, d.Value.Suffix, f));
        }
        return result.OrderBy(x => x.Day, StringComparer.Ordinal).ThenBy(x => x.Suffix).Select(x => x.Path).ToList();
    }

    /// <summary>解析 CSV 行首列的时间（Csv.TimeFormat，本地 + 偏移）。</summary>
    public static DateTimeOffset? ParseRowTime(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int comma = line.IndexOf(',');
        var s = comma >= 0 ? line.AsSpan(0, comma) : line.AsSpan();
        if (s.Length > 0 && s[0] == '"') s = s.Trim('"');
        if (s.Length < 19) return null;
        if (DateTimeOffset.TryParseExact(s, Csv.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t)) return t;
        return null;
    }

    /// <summary>查询参数 from / to：必须带时区偏移或 Z（不接受无时区的时间）。</summary>
    public static bool TryParseQueryTime(string? s, out DateTimeOffset t)
    {
        t = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        // 无时区：末尾不是 Z、也没有 +hh:mm / -hh:mm
        bool hasZone = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}\z");
        if (!hasZone) return false;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
    }

    private static readonly ConcurrentDictionary<string, (long Len, long Ticks, DateTime? From, DateTime? To)> RangeCache = new();
    public const int TailChunk = 64 * 1024;

    /// <summary>首行数据与最后一条完整行的时间（表头跳过；文件为空或只有表头时为 (null, null)）。按（长度, 修改时间）缓存。</summary>
    public static (DateTime? From, DateTime? To) FirstLastTimestamp(string path)
    {
        var fi = new FileInfo(path);
        long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
        if (RangeCache.TryGetValue(path, out var c) && c.Len == len && c.Ticks == ticks) return (c.From, c.To);
        var r = ComputeFirstLast(path);
        if (RangeCache.Count > 2000) RangeCache.Clear();
        RangeCache[path] = (len, ticks, r.From, r.To);
        return r;
    }

    /// <summary>不走缓存：头两行 + 末尾一块（<see cref="TailChunk"/>）。</summary>
    public static (DateTime? From, DateTime? To) ComputeFirstLast(string path)
    {
        DateTime? first = null, last = null;
        string? header;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        {
            header = r.ReadLine();
            var firstRow = r.ReadLine();
            if (firstRow != null) first = ParseRowTime(firstRow)?.UtcDateTime;
        }
        var lastLine = LastCompleteLine(path);
        if (lastLine != null && lastLine != header) last = ParseRowTime(lastLine)?.UtcDateTime;
        return (first, last ?? first);
    }

    /// <summary>
    /// 文件最后一条完整行（以换行结束；末尾半行跳过）：从文件尾倒读一块。只有表头 / 空文件返回表头或 null；
    /// 末两行都长过一块（极端情况）时回退为整文件扫描。
    /// </summary>
    public static string? LastCompleteLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return null;
        int take = (int)Math.Min(TailChunk, fs.Length);
        fs.Seek(-take, SeekOrigin.End);
        var buf = new byte[take];
        int got = 0;
        while (got < take) { int n = fs.Read(buf, got, take - got); if (n <= 0) break; got += n; }
        bool wholeFile = take == fs.Length;
        int end = got;
        if (end > 0 && buf[end - 1] == (byte)'\n') end--;          // 以换行结束：末行完整
        else
        {
            // 末行是半行：丢掉它，取其前面那一行
            int cut = Array.LastIndexOf(buf, (byte)'\n', end - 1);
            if (cut < 0) return wholeFile ? null : LastCompleteLineSlow(path);
            end = cut;
        }
        int start = end > 0 ? Array.LastIndexOf(buf, (byte)'\n', end - 1) + 1 : 0;
        if (start == 0 && !wholeFile) return LastCompleteLineSlow(path);   // 这一行可能被块边界截断
        if (wholeFile && start == 0 && got >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) start = 3;   // 去 BOM（只有一行时）
        var line = Encoding.UTF8.GetString(buf, start, end - start);
        return line.TrimEnd('\r');
    }

    private static string? LastCompleteLineSlow(string path)
    {
        bool complete = EndsWithNewline(path);
        string? prev = null, prevPrev = null;
        foreach (var line in ReadLines(path)) { prevPrev = prev; prev = line; }
        return complete ? prev : prevPrev;
    }

    /// <summary>
    /// 把多个日文件里时间 ∈ [from, to) 的行按序写出（表头只写第一个文件的；半行与解析失败的行跳过）。
    /// 返回写出的数据行数。
    /// </summary>
    public static async Task<long> WriteRangeAsync(IReadOnlyList<string> files, DateTimeOffset from, DateTimeOffset to, Stream output, CancellationToken ct)
    {
        long rows = 0;
        bool headerDone = false;
        string[]? header = null;   // 第一个文件的表头（列名）
        var enc = new UTF8Encoding(true);
        var bom = enc.GetPreamble();
        var nl = Encoding.UTF8.GetBytes("\n");
        foreach (var f in files)
        {
            bool complete = EndsWithNewline(f);
            string? pending = null;   // 延后一行写出：最后一行可能是半行
            int[]? map = null;        // 本文件表头与第一个表头不同：按列名映射
            int n = 0;
            foreach (var line in ReadLines(f))
            {
                ct.ThrowIfCancellationRequested();
                n++;
                if (n == 1)
                {
                    if (!headerDone)
                    {
                        await output.WriteAsync(bom, ct).ConfigureAwait(false);
                        await WriteLine(output, line, nl, ct).ConfigureAwait(false);
                        headerDone = true;
                        header = SplitCsv(line);
                    }
                    else map = ColumnMap(header!, SplitCsv(line));
                    continue;
                }
                if (pending != null) rows += await EmitAsync(pending, map, from, to, output, nl, ct).ConfigureAwait(false);
                pending = line;
            }
            if (pending != null && complete) rows += await EmitAsync(pending, map, from, to, output, nl, ct).ConfigureAwait(false);
        }
        if (!headerDone) await output.WriteAsync(bom, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private static async Task<int> EmitAsync(string line, int[]? map, DateTimeOffset from, DateTimeOffset to, Stream output, byte[] nl, CancellationToken ct)
    {
        var t = ParseRowTime(line);
        if (t == null || t < from || t >= to) return 0;
        if (map != null) line = Remap(line, map);
        await WriteLine(output, line, nl, ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>目标表头每一列在源表头里的下标（没有为 -1）；两表头完全相同返回 null（不用映射）。</summary>
    public static int[]? ColumnMap(string[] target, string[] source)
    {
        if (target.Length == source.Length && target.AsSpan().SequenceEqual(source)) return null;
        var map = new int[target.Length];
        for (int i = 0; i < target.Length; i++) map[i] = Array.IndexOf(source, target[i]);
        return map;
    }

    /// <summary>按映射把一行重排到目标表头的列序（缺列留空、多出的列丢弃），再按 Csv 规则重新转义。</summary>
    public static string Remap(string line, int[] map)
    {
        var src = SplitCsv(line);
        var outFields = new string?[map.Length];
        for (int i = 0; i < map.Length; i++) outFields[i] = map[i] >= 0 && map[i] < src.Length ? src[map[i]] : "";
        return Csv.Line(outFields);
    }

    /// <summary>RFC 4180 风格拆分一行（引号包裹、"" 转义）。</summary>
    public static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    private static async Task WriteLine(Stream output, string line, byte[] nl, CancellationToken ct)
    {
        var b = Encoding.UTF8.GetBytes(line);
        await output.WriteAsync(b, ct).ConfigureAwait(false);
        await output.WriteAsync(nl, ct).ConfigureAwait(false);
    }

    /// <summary>非空文件的最后一个字节是否为换行（否则末行是半行）。</summary>
    public static bool EndsWithNewline(string p)
    {
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return true;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
        catch { return false; }
    }

    /// <summary>共享读（写入方正在追加）。</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = r.ReadLine()) != null) yield return line;
    }

    /// <summary>sessions/&lt;testId&gt;-&lt;yyyyMMdd-HHmmss&gt;.csv → (testId, 开始时刻本地)。</summary>
    public static (string TestId, DateTime? StartedLocal)? ParseSessionName(string name)
    {
        var m = Regex.Match(name, @"^(.+)-(\d{8}-\d{6})\.csv\z");
        if (!m.Success) return null;
        DateTime? t = DateTime.TryParseExact(m.Groups[2].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
        return (m.Groups[1].Value, t);
    }
}
