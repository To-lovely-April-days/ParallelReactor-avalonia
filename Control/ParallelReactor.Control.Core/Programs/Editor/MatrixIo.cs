using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>
/// 条件矩阵的 CSV（融合版 F6，样稿「导入 CSV / 导出 CSV」：每路一列）。UTF-8 带 BOM（Excel 直接打开不乱码）。
/// <code>
/// # 条件矩阵,催化剂 × 温度筛选 · 批次 8,模板,催化加氢筛选 · 120 ℃,HT-PR8
/// key,参数,RV1,RV2,…
/// include,参加,1,1,0,…
/// sample,样品 / 催化剂,Pd/C 5%,…
/// vol,溶液体积 mL,5.0,…
/// s2.heat.temp,02 升温 · 目标温度 °C,100,110,…      ← 每路可不同
/// s1.purge.gas,01 吹扫 · 气体,inert,inert,…           ← 共用参数每列写同一个值（导入时取第一个非空的）
/// </code>
/// 第一列是机器读的键（s 步序.类型.参数），第二列给人看；导入时按键对上模板的步骤，类型对不上就整份不导入。
/// </summary>
public static class MatrixCsv
{
    public static string Export(MatrixPlan m, IReadOnlyList<int> channels, ProgramSchema schema, string modelLabel)
    {
        var sb = new StringBuilder();
        void Row(IEnumerable<string> cells) => sb.Append(string.Join(",", cells.Select(Quote))).Append("\r\n");
        Row(new[] { "# 条件矩阵", m.Name, "模板", m.Template.Name, modelLabel });
        Row(new[] { "key", "参数" }.Concat(channels.Select(n => "RV" + n)));
        Row(new[] { "include", "参加" }.Concat(channels.Select(n => m.Include.Contains(n) ? "1" : "0")));
        Row(new[] { "sample", "样品 / 催化剂" }.Concat(channels.Select(n => m.Samples.TryGetValue(n, out var s) ? s : "")));
        Row(new[] { "vol", "溶液体积 mL" }.Concat(channels.Select(n => m.Vol.TryGetValue(n, out var v) && double.IsFinite(v) ? JsNumber.ToFixed(v, 1) : "")));
        for (int i = 0; i < m.Steps.Count; i++)
        {
            var s = m.Steps[i];
            foreach (var k in MatrixPlan.RowKeys(s.Type))
            {
                var f = schema.Field(s.Type, k);
                string label = (i + 1).ToString("00", CultureInfo.InvariantCulture) + " " + (schema.Of(s.Type)?.Label ?? s.Type.Zh()) + " · " + (f?.Label ?? k) + (f is { IsSelect: false, Unit.Length: > 0 } ? " " + f.Unit : "");
                string key = "s" + (i + 1).ToString(CultureInfo.InvariantCulture) + "." + s.Type.ToCode() + "." + k;
                IEnumerable<string> vals;
                if (f is { IsSelect: true }) vals = channels.Select(_ => s.S(k));
                else if (MatrixPlan.IsPer(s.Type, k)) vals = channels.Select(n => Num(m.PerValue(i, k, n), f?.Dp ?? 0));
                else vals = channels.Select(_ => Num(s.N(k), f?.Dp ?? 0));
                Row(new[] { key, label }.Concat(vals));
            }
        }
        return sb.ToString();
    }

    private static string Num(double v, int dp) => double.IsFinite(v) ? JsNumber.ToFixed(v, dp) : "";

    private static string Quote(string s) => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>
    /// 导入：改 <paramref name="m"/>（参加、样品、溶液体积、每路参数、共用参数）。返回提示文字；格式不对 / 步骤对不上时抛 FormatException（矩阵不变）。
    /// 停用的通道不会被勾成参加（<paramref name="off"/>）。
    /// </summary>
    public static string Import(MatrixPlan m, string text, IReadOnlyCollection<int> channels, IReadOnlyCollection<int> off)
    {
        var rows = Parse(text);
        int hi = rows.FindIndex(r => r.Count > 0 && r[0].Trim() == "key");
        if (hi < 0) throw new FormatException("没有表头行（第一列为 key）");
        var head = rows[hi];
        var col = new Dictionary<int, int>();
        for (int c = 2; c < head.Count; c++)
            if (head[c].Trim().StartsWith("RV", StringComparison.OrdinalIgnoreCase) && int.TryParse(head[c].Trim()[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && channels.Contains(n))
                col[c] = n;
        if (col.Count == 0) throw new FormatException("表头里没有这台设备的通道（RV1 …）");
        // 先全部检查，再写入（一处不对整份不导入）
        var edits = new List<Action>();
        int cells = 0;
        foreach (var r in rows.Skip(hi + 1))
        {
            if (r.Count == 0 || string.IsNullOrWhiteSpace(r[0]) || r[0].TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            string key = r[0].Trim();
            IEnumerable<(int N, string V)> Vals() => col.Where(kv => kv.Key < r.Count).Select(kv => (kv.Value, r[kv.Key].Trim()));
            switch (key)
            {
                case "include":
                    foreach (var (n, v) in Vals().ToList())
                        if (v.Length > 0) { bool on = v is "1" or "是" or "true" or "TRUE" or "√" or "✓"; edits.Add(() => { if (on && !off.Contains(n)) m.Include.Add(n); else if (!on) m.Include.Remove(n); }); cells++; }
                    continue;
                case "sample":
                    foreach (var (n, v) in Vals().ToList()) { edits.Add(() => m.Samples[n] = v); cells++; }
                    continue;
                case "vol":
                    foreach (var (n, v) in Vals().ToList()) { double x = ParseNum(v, key); edits.Add(() => m.Vol[n] = double.IsNaN(x) ? double.NaN : Math.Round(x, 1)); cells++; }
                    continue;
            }
            var parts = key.Split('.');
            if (parts.Length != 3 || parts[0].Length < 2 || parts[0][0] != 's' || !int.TryParse(parts[0][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var si))
                continue;   // 不认识的行跳过（人加的备注行等）
            int i = si - 1;
            if (i < 0 || i >= m.Steps.Count || !StepTypeCodes.TryParse(parts[1], out var t) || m.Steps[i].Type != t)
                throw new FormatException("第 " + si.ToString("00", CultureInfo.InvariantCulture) + " 步和模板的步骤对不上（CSV 是「" + parts[1] + "」）· 先选同一个模板");
            string k = parts[2];
            if (!MatrixPlan.RowKeys(t).Contains(k)) continue;
            var step = m.Steps[i];
            if (MatrixPlan.IsPer(t, k))
                foreach (var (n, v) in Vals().ToList()) { double x = ParseNum(v, key); int ii = i; edits.Add(() => m.SetPer(ii, k, n, x)); cells++; }
            else
            {
                var first = Vals().Select(x => x.V).FirstOrDefault(v => v.Length > 0);
                if (first is null) continue;
                bool isSel = step.Sel.ContainsKey(k) || !step.Num.ContainsKey(k);
                if (isSel && !double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) edits.Add(() => step.Sel[k] = first);
                else { double x = ParseNum(first, key); edits.Add(() => step.Num[k] = x); }
                cells++;
            }
        }
        foreach (var e in edits) e();
        return "已导入 " + cells + " 格";
    }

    private static double ParseNum(string v, string key)
    {
        if (v.Length == 0) return double.NaN;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return x;
        throw new FormatException("「" + key + "」里的「" + v + "」不是数字");
    }

    /// <summary>RFC 4180 的 CSV（引号、引号里的逗号与换行）。去掉开头的 BOM。</summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (; i < text.Length; i++)
        {
            char ch = text[i];
            if (q)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                else if (ch == '"') q = false;
                else cur.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': q = true; break;
                case ',': row.Add(cur.ToString()); cur.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cur.ToString()); cur.Clear(); rows.Add(row); row = new List<string>(); break;
                default: cur.Append(ch); break;
            }
        }
        if (cur.Length > 0 || row.Count > 0) { row.Add(cur.ToString()); rows.Add(row); }
        return rows;
    }
}

/// <summary>
/// 条件矩阵存进本机实验方案文件（experiments.json，见 App 的 ExperimentStore）的格式：
/// <code>{ "template": "H1", "name": "…", "record": true, "include": [1,2], "samples": {"1": "Pd/C 5%"}, "vol": {"1": 5.0},
///   "per": {"1:temp": {"1": 100}}, "steps": [模板步骤 + 共用参数的当前值], "savedAt": "2026-11-15T14:31:00" }</code>
/// 没填的数（NaN）写成 null。
/// </summary>
public static class MatrixJson
{
    public static void Write(Utf8JsonWriter w, MatrixPlan m, DateTime savedAt)
    {
        w.WriteStartObject();
        w.WriteString("template", m.TemplateId);
        w.WriteString("name", m.Name);
        w.WriteBoolean("record", m.Record);
        w.WriteString("savedAt", savedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        w.WritePropertyName("include");
        w.WriteStartArray();
        foreach (var n in m.Include) w.WriteNumberValue(n);
        w.WriteEndArray();
        w.WritePropertyName("samples");
        w.WriteStartObject();
        foreach (var (n, s) in m.Samples.OrderBy(x => x.Key)) if (!string.IsNullOrEmpty(s)) w.WriteString(n.ToString(CultureInfo.InvariantCulture), s);
        w.WriteEndObject();
        w.WritePropertyName("vol");
        WriteMap(w, m.Vol);
        w.WritePropertyName("per");
        w.WriteStartObject();
        foreach (var (k, row) in m.Per) { w.WritePropertyName(k); WriteMap(w, row); }
        w.WriteEndObject();
        w.WritePropertyName("steps");
        PcProgram.WriteSteps(w, m.Steps);
        w.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter w, IReadOnlyDictionary<int, double> map)
    {
        w.WriteStartObject();
        foreach (var (n, v) in map.OrderBy(x => x.Key))
        {
            w.WritePropertyName(n.ToString(CultureInfo.InvariantCulture));
            if (double.IsFinite(v)) w.WriteNumberValue(v); else w.WriteNullValue();
        }
        w.WriteEndObject();
    }

    /// <summary>读回（模板要已经在程序库里；旧格式的步骤先转换；步骤类型和模板对不上时只取参加 / 样品 / 体积，参数按模板重建）。</summary>
    public static MatrixPlan Read(JsonElement e, PcProgram template, int deviceK, IReadOnlyList<MxChannel> chs)
    {
        var m = MatrixPlan.Build(template, deviceK, chs, Str(e, "name") ?? "");
        m.Record = !e.TryGetProperty("record", out var rec) || rec.ValueKind != JsonValueKind.False;
        if (e.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
        {
            m.Include.Clear();
            foreach (var x in inc.EnumerateArray()) if (x.TryGetInt32(out var n) && chs.Any(c => c.N == n && !c.Off)) m.Include.Add(n);
        }
        if (e.TryGetProperty("samples", out var sm) && sm.ValueKind == JsonValueKind.Object)
            foreach (var p in sm.EnumerateObject()) if (int.TryParse(p.Name, out var n)) m.Samples[n] = p.Value.GetString() ?? "";
        if (e.TryGetProperty("vol", out var vol) && vol.ValueKind == JsonValueKind.Object)
            foreach (var p in vol.EnumerateObject()) if (int.TryParse(p.Name, out var n)) m.Vol[n] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : double.NaN;
        // 旧版本存的方案（旧格式的步骤）先按 STEPS-SPEC §3 转换，再和（已经转换过的）模板比
        var steps = e.TryGetProperty("steps", out var st) ? StepDefaults.Migrate(PcProgram.ReadSteps(st), out _) : new List<PcStep>();
        bool same = steps.Count == m.Steps.Count && steps.Select(s => s.Type).SequenceEqual(m.Steps.Select(s => s.Type));
        if (!same) return m;
        for (int i = 0; i < steps.Count; i++) m.Steps[i] = steps[i];
        if (e.TryGetProperty("per", out var per) && per.ValueKind == JsonValueKind.Object)
            foreach (var p in per.EnumerateObject())
            {
                var parts = p.Name.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var i) || i < 0 || i >= m.Steps.Count || !MatrixPlan.IsPer(m.Steps[i].Type, parts[1])) continue;
                foreach (var q in p.Value.EnumerateObject())
                    if (int.TryParse(q.Name, out var n)) m.SetPer(i, parts[1], n, q.Value.ValueKind == JsonValueKind.Number ? q.Value.GetDouble() : double.NaN);
            }
        return m;
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
