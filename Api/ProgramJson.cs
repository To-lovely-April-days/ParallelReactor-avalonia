using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.Api;

/// <summary>PUT 程序的解析结果：转换后的步骤（失败为 null）、是否从旧格式转换过、提示（旧值超出新范围被收紧等）、错误。</summary>
public sealed record ProgramParseResult(IReadOnlyList<StepSnapshot>? Steps, bool Migrated, IReadOnlyList<string> Notes, OpResult? Error);

/// <summary>
/// §6.9 程序 JSON → 下发快照 <see cref="StepSnapshot"/>（v0.8，STEPS-SPEC §6）。纯逻辑（只用 <see cref="StepSpecs"/>），测试工程直接链接。
/// <list type="number">
/// <item>每步 <c>{ type, params{…} }</c>（也接受参数直接写在步骤对象里）；选项传英文码（也容忍中文 label），数值传 JSON 数字，压力 psi；</item>
/// <item>新格式与旧格式都收：先整体换成 <see cref="MigStep"/>，按 <see cref="StepMigration.Migrate"/>（STEPS-SPEC §3）转换——
///   旧码 purge / wait、带 dur 的 stir、带 mode / cool 的 end、带 endBy 的 react；末尾保证只有一个 end（没有就补）；</item>
/// <item>转换后逐步校验：未知类型 / 选项 → PROGRAM_INVALID；数值超出声明范围 → PARAM_OUT_OF_RANGE（<c>detail{ step, field, value, min, max }</c>）；
///   旧格式转换来的程序则把超出新范围的值收紧到范围内并写进 <see cref="ProgramParseResult.Notes"/>（例如旧搅拌 1000 rpm、旧吹扫 50 次）；
///   全局限值由 TryReplaceProgram 的 <see cref="ProgramLimits.Validate"/> 再查一遍；</item>
/// <item>heat.mode = curve 时取 segments[{temp, rate, hold}]（1..16 段，段字段范围同曲线编辑器）；缺省字段取默认值。</item>
/// </list>
/// </summary>
public static class ProgramJson
{
    /// <summary>旧格式选项的 label → 码（purge.gas / wait.mode / react.endBy / end.mode 只在输入时认）。</summary>
    private static string LegacyCode(string type, string key, string v)
        => ProgramSchemaCodes.Options.TryGetValue($"{type}.{key}", out var o) ? (o.FirstOrDefault(x => x.Code == v || x.Label == v)?.Code ?? v) : v;

    private const string TypeList = "leak / exchange / stir / heat / press / hold / react / vent / end";

    /// <param name="leakRate">气密检查没给 maxDrop 时的默认值（设备设置 LeakRate，psi/h）。</param>
    public static ProgramParseResult Parse(JsonElement steps, double leakRate = StepSpecs.LeakRateDefault)
    {
        ProgramParseResult Fail(OpResult e) => new(null, false, Array.Empty<string>(), e);
        if (steps.ValueKind != JsonValueKind.Array) return Fail(OpResult.Fail(OpCodes.BadRequest, "steps 必须是数组"));

        // —— 1. JSON → MigStep（输入序号的错误：缺 type / 类型未知 / 段格式）——
        var input = new List<MigStep>();
        int i = 0;
        foreach (var s in steps.EnumerateArray())
        {
            i++;
            var (m, err) = ToMig(s, i);
            if (err != null) return Fail(err);
            input.Add(m!);
        }
        if (input.Count == 0) return Fail(OpResult.Fail(OpCodes.ProgramInvalid, "程序为空"));

        // —— 2. 旧格式 → v0.8（§3）——
        var mig = StepMigration.Migrate(input);
        bool migrated = mig.Migrated;

        // —— 3. 逐步校验 → 快照（转换后的序号）——
        var notes = new List<string>();
        var list = new List<StepSnapshot>();
        int n = 0;
        foreach (var m in mig.Steps)
        {
            n++;
            var (snap, err) = Build(m, n, migrated, leakRate, notes);
            if (err != null) return Fail(err);
            list.Add(snap!);
        }
        return new ProgramParseResult(list, migrated, notes, null);
    }

    private static (MigStep?, OpResult?) ToMig(JsonElement s, int ordinal)
    {
        OpResult Bad(string code, string msg, object? detail = null) => OpResult.Fail(code, $"第 {ordinal} 步：{msg}", "err", detail ?? new { step = ordinal });
        if (s.ValueKind != JsonValueKind.Object) return (null, Bad(OpCodes.BadRequest, "每步须为对象 { type, params }"));
        if (!s.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String) return (null, Bad(OpCodes.BadRequest, "缺少 type"));
        string type = (tEl.GetString() ?? "").Trim().ToLowerInvariant();
        if (!StepTypeCodes.TryParse(type, out var st))
            return (null, Bad(OpCodes.ProgramInvalid, $"未知步骤类型「{type}」（{TypeList}；旧码 purge / wait 自动转换）", new { step = ordinal, type }));
        JsonElement p = s.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object ? pEl : s;

        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        var segs = new List<(double, double, double)>();
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Name is "type" or "name" or "detail" or "params") continue;
            if (prop.Name == "segments")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) return (null, Bad(OpCodes.ProgramInvalid, "segments 须为数组 [{temp, rate, hold}]", new { step = ordinal, field = "segments" }));
                int k = 0;
                foreach (var g in prop.Value.EnumerateArray())
                {
                    k++;
                    if (k > ProgramSchemaCodes.SegmentsMax)
                        return (null, Bad(OpCodes.ParamOutOfRange, $"曲线最多 {ProgramSchemaCodes.SegmentsMax} 段", new { step = ordinal, field = "segments", max = ProgramSchemaCodes.SegmentsMax }));
                    var vals = new double[3];
                    for (int f = 0; f < 3; f++)
                    {
                        var (sk, label, unit, lo, hi) = ProgramSchemaCodes.SegmentFields[f];
                        if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty(sk, out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetDouble(out var x) || !double.IsFinite(x))
                            return (null, Bad(OpCodes.BadRequest, $"第 {k} 段缺少数值字段 {sk}"));
                        if (x < lo || x > hi)
                            return (null, Bad(OpCodes.ParamOutOfRange, $"第 {k} 段 {label} {N(x)}{unit} 超出 {N(lo)}–{N(hi)}{unit}",
                                new { step = ordinal, segment = k, field = sk, value = x, min = lo, max = hi }));
                        vals[f] = x;
                    }
                    segs.Add((vals[0], vals[1], vals[2]));
                }
                continue;
            }
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                {
                    string v = prop.Value.GetString() ?? "";
                    // 旧格式的选项先换成码（StepMigration 按码转换）
                    d[prop.Name] = st.IsLegacy() || (type, prop.Name) is ("react", "endBy") or ("end", "mode") ? LegacyCode(type, prop.Name, v) : v;
                    break;
                }
                case JsonValueKind.Number:
                    if (!prop.Value.TryGetDouble(out var num) || !double.IsFinite(num)) return (null, Bad(OpCodes.BadRequest, $"{prop.Name} 须为数值"));
                    d[prop.Name] = num;
                    break;
                case JsonValueKind.Null: break;
                default: return (null, Bad(OpCodes.BadRequest, $"{prop.Name} 须为数值或枚举码字符串"));
            }
        }
        return (new MigStep(type, d, segs), null);
    }

    private static (StepSnapshot?, OpResult?) Build(MigStep m, int ordinal, bool migrated, double leakRate, List<string> notes)
    {
        string where = migrated ? $"第 {ordinal} 步（转换后）" : $"第 {ordinal} 步";
        OpResult Bad(string code, string msg, object? detail = null) => OpResult.Fail(code, $"{where}：{msg}", "err", detail ?? new { step = ordinal });
        var type = StepTypeCodes.Parse(m.Type);
        if (!StepSpecs.TryGet(type, out var spec))
            return (null, Bad(OpCodes.ProgramInvalid, $"未知步骤类型「{m.Type}」（{TypeList}）", new { step = ordinal, type = m.Type }));
        var p = StepSpecs.Defaults(type);
        if (type == StepType.Leak) p["maxDrop"] = leakRate;
        foreach (var f in spec.Fields)
        {
            if (!m.P.TryGetValue(f.Key, out var raw)) continue;   // 缺省：默认值
            if (ProgramSchemaCodes.UnavailableFields.Contains($"{m.Type}.{f.Key}"))
                return (null, Bad(OpCodes.ProgramInvalid, $"字段 {f.Key} 当前不可用（{ProgramSchemaCodes.GasUnavailableReason}）", new { step = ordinal, field = f.Key }));
            if (f.IsSel)
            {
                if (raw is not string code) return (null, Bad(OpCodes.BadRequest, $"{f.Key} 须为枚举码字符串"));
                var opt = f.Options?.FirstOrDefault(o => o.Code == code || o.Label == code);
                if (opt == null) return (null, Bad(OpCodes.ProgramInvalid, $"{f.Key} 的值「{code}」不在枚举里", new { step = ordinal, field = f.Key, value = code }));
                if (!opt.Available) return (null, Bad(OpCodes.ProgramInvalid, $"{f.Key} = {code} 当前不可用（{opt.Reason}）", new { step = ordinal, field = f.Key, value = code }));
                p[f.Key] = opt.Code;
                continue;
            }
            if (m.Num(f.Key) is not double num || raw is string) return (null, Bad(OpCodes.BadRequest, $"{f.Key} 须为数值"));
            double lo = f.Min, hi = f.Max;
            if (num < lo || num > hi || (f.Integer && Math.Abs(num - Math.Round(num)) > 1e-9))
            {
                if (!migrated)
                {
                    if (f.Integer && num >= lo && num <= hi) return (null, Bad(OpCodes.BadRequest, $"{f.Label} 须为整数"));
                    return (null, Bad(OpCodes.ParamOutOfRange, $"{f.Label} {N(num)}{f.Unit} 超出 {N(lo)}–{N(hi)}{f.Unit}",
                        new { step = ordinal, field = f.Key, value = num, min = lo, max = hi }));
                }
                double c = Math.Clamp(f.Integer ? Math.Round(num) : num, lo, hi);
                notes.Add($"{where} {spec.Name}：{f.Label} {N(num)}{f.Unit} 超出新范围，已改为 {N(c)}{f.Unit}");
                num = c;
            }
            p[f.Key] = num;
        }
        var segs = m.Segments;
        if (type == StepType.Heat && StepParams.Str(p, "mode", "fixed") == "curve")
        {
            if (segs.Count < ProgramSchemaCodes.SegmentsMin)
                return (null, Bad(OpCodes.ProgramInvalid, "曲线模式须给 segments[{temp, rate, hold}]（至少 1 段）", new { step = ordinal, field = "segments" }));
        }
        else segs = new List<(double, double, double)>();
        return (StepSnapshot.Create(type, ordinal, spec.Name, StepSpecs.Detail(type, p, segs), p, segs), null);
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
