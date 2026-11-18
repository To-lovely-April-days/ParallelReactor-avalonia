using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ParallelReactor.Control.Core.Records;

/// <summary>
/// 读 HMI 的运行记录 CSV（GET /telemetry?from&amp;to、sessions/&lt;id&gt;.csv；格式见 HMI Service/TelemetryCsv.cs）给记录页的 8 路对比：
/// 按表头的列名取「时间」「RVn_PV(℃)」「RVn_压力(MPa)」（没有时用「RVn_压力(psi)」换算）「RVn_吸气量(mmol)」，n 不限 8（列名里有几路就读几路）。
/// 列顺序不假设（HMI 只在末尾追加列，旧文件没有吸气量列 → 吸气量 NaN）。只取会话窗口 [开始, 结束] 里的行，
/// 按 <paramref name="minStepSeconds"/> 抽稀（每 1.2 s 一行的 4 h 会话约 1.2 万行 → 6 s 一点约 2400 点）。空单元格 = NaN。
/// RFC 4180：带引号的字段（标记、详情里的逗号 / 引号 / 换行）照规则拆。
/// </summary>
public static class TelemetryCsvReader
{
    private sealed class Cols
    {
        public int Time = -1;
        public readonly SortedDictionary<int, (int T, int Pm, int Ppsi, int G)> Ch = new();
    }

    /// <summary>读一份 CSV，返回每路的采样（RV 号 → 采样，M = 距 <paramref name="start"/> 的分钟）。</summary>
    public static SortedDictionary<int, List<RecPoint>> Read(TextReader reader, DateTime start, DateTime? end, double minStepSeconds = 6)
    {
        var output = new SortedDictionary<int, List<RecPoint>>();
        var header = ReadRecord(reader);
        if (header == null) return output;
        if (header.Count > 0 && header[0].Length > 0 && header[0][0] == '﻿') header[0] = header[0][1..];
        var cols = Map(header);
        if (cols.Time < 0 || cols.Ch.Count == 0) return output;
        foreach (var n in cols.Ch.Keys) output[n] = new List<RecPoint>();
        DateTime last = DateTime.MinValue;
        List<string>? rec;
        while ((rec = ReadRecord(reader)) != null)
        {
            if (cols.Time >= rec.Count) continue;
            if (!DateTimeOffset.TryParse(rec[cols.Time], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var ts)) continue;
            var t = ts.LocalDateTime;
            if (t < start) continue;
            if (end is DateTime e && t > e) break;
            if (last != DateTime.MinValue && (t - last).TotalSeconds < minStepSeconds - 1e-6) continue;
            last = t;
            double m = (t - start).TotalMinutes;
            foreach (var (n, c) in cols.Ch)
            {
                double p = Num(rec, c.Pm);
                if (double.IsNaN(p)) { double psi = Num(rec, c.Ppsi); if (!double.IsNaN(psi)) p = psi / RecCompute.PsiPerMPa; }
                output[n].Add(new RecPoint(m, Num(rec, c.T), p, Num(rec, c.G)));
            }
        }
        return output;
    }

    /// <summary>
    /// 补拉用（融合版 F8 无人值守）：读 GET /telemetry?from&amp;to 的 CSV，把 (<paramref name="fromUtc"/>, <paramref name="toUtc"/>) 之间（不含两端 —— 两端是本机已有的帧）
    /// 的每一行变成曲线采样（时刻 UTC；温度、压力 MPa（没有 MPa 列时由 psi 换算）、吸气量 mmol；加热输出、转速、设定值 CSV 里不全，留空）。
    /// 返回每路的采样和读到的行数（「已补拉 146 条」）。
    /// </summary>
    public static (Dictionary<int, List<History.HistorySample>> Samples, int Rows) ReadSamples(TextReader reader, DateTime fromUtc, DateTime toUtc)
    {
        var output = new Dictionary<int, List<History.HistorySample>>();
        var header = ReadRecord(reader);
        if (header == null) return (output, 0);
        if (header.Count > 0 && header[0].Length > 0 && header[0][0] == '\uFEFF') header[0] = header[0][1..];
        var cols = Map(header);
        if (cols.Time < 0 || cols.Ch.Count == 0) return (output, 0);
        foreach (var n in cols.Ch.Keys) output[n] = new List<History.HistorySample>();
        var from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();
        var to = toUtc.Kind == DateTimeKind.Utc ? toUtc : toUtc.ToUniversalTime();
        int rows = 0;
        List<string>? rec;
        while ((rec = ReadRecord(reader)) != null)
        {
            if (cols.Time >= rec.Count) continue;
            if (!DateTimeOffset.TryParse(rec[cols.Time], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var ts)) continue;
            var t = ts.UtcDateTime;
            if (t <= from) continue;
            if (t >= to) break;
            rows++;
            foreach (var (n, c) in cols.Ch)
            {
                double p = Num(rec, c.Pm);
                if (double.IsNaN(p)) { double psi = Num(rec, c.Ppsi); if (!double.IsNaN(psi)) p = psi / RecCompute.PsiPerMPa; }
                double temp = Num(rec, c.T), g = Num(rec, c.G);
                if (double.IsNaN(temp) && double.IsNaN(p)) continue;
                output[n].Add(new History.HistorySample(t, double.IsNaN(temp) ? null : temp, double.IsNaN(p) ? null : p, null, null, double.IsNaN(g) ? null : g, null, null));
            }
        }
        return (output, rows);
    }

    /// <summary>读一份 CSV 文本（测试用）。</summary>
    public static SortedDictionary<int, List<RecPoint>> ReadText(string csv, DateTime start, DateTime? end, double minStepSeconds = 0) =>
        Read(new StringReader(csv), start, end, minStepSeconds);

    private static Cols Map(List<string> header)
    {
        var c = new Cols();
        for (int i = 0; i < header.Count; i++)
        {
            var h = header[i].Trim();
            if (h == "时间") { c.Time = i; continue; }
            if (!h.StartsWith("RV", StringComparison.Ordinal)) continue;
            int u = h.IndexOf('_');
            if (u < 3 || !int.TryParse(h.AsSpan(2, u - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1) continue;
            var key = h[(u + 1)..];
            (int T, int Pm, int Ppsi, int G) cur = c.Ch.TryGetValue(n, out var x) ? x : (-1, -1, -1, -1);
            switch (key)
            {
                case "PV(℃)": cur.T = i; break;
                case "压力(MPa)": cur.Pm = i; break;
                case "压力(psi)": cur.Ppsi = i; break;
                case "吸气量(mmol)": cur.G = i; break;
                default: continue;
            }
            c.Ch[n] = cur;
        }
        return c;
    }

    private static double Num(List<string> rec, int i) =>
        i >= 0 && i < rec.Count && double.TryParse(rec[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : double.NaN;

    /// <summary>读一条记录（RFC 4180；引号里的换行属于字段）；读到末尾返回 null。空行跳过。</summary>
    public static List<string>? ReadRecord(TextReader r)
    {
        while (true)
        {
            int ch = r.Read();
            if (ch < 0) return null;
            if (ch == '\n' || ch == '\r') continue;
            var fields = new List<string>(64);
            var sb = new StringBuilder();
            bool quoted = false, inQuotes = false;
            while (true)
            {
                if (inQuotes)
                {
                    if (ch < 0) { fields.Add(sb.ToString()); return fields; }
                    if (ch == '"')
                    {
                        if (r.Peek() == '"') { r.Read(); sb.Append('"'); }
                        else inQuotes = false;
                    }
                    else sb.Append((char)ch);
                }
                else if (ch < 0 || ch == '\n' || ch == '\r')
                {
                    fields.Add(sb.ToString());
                    if (ch == '\r' && r.Peek() == '\n') r.Read();
                    return fields;
                }
                else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); quoted = false; }
                else if (ch == '"' && sb.Length == 0 && !quoted) { inQuotes = true; quoted = true; }
                else sb.Append((char)ch);
                ch = r.Read();
            }
        }
    }
}
