using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// §6.9 程序 JSON（<c>steps[{ type, params{…} }]</c>，sel 字段用英文枚举码）→ 下发快照 <see cref="StepSnapshot"/>。
/// 放在 Api/Hmi 下：要用 <see cref="StepDef.Registry"/>（Models/ProgramStep.cs 含 Avalonia 类型，测试工程不链接）。
/// 规则：枚举码经 <see cref="ProgramSchemaCodes"/> 转成界面 label 存入参数（程序引擎按 label 判定）；不可用选项（react.endBy 的 gasConsumed / either、react.mmol）
/// → PROGRAM_INVALID；数值按 FieldDef 的声明范围 → PARAM_OUT_OF_RANGE（全局限值由 TryReplaceProgram 的 ProgramLimits.Validate 再查一遍）；
/// heat.mode = curve 时取 params.segments[{temp, rate, hold}]（1..16 段，段字段范围同曲线编辑器）。缺省字段取 FieldDef.Default。
/// </summary>
public static class ProgramJson
{
    /// <summary>转换；失败返回 error（§11 错误码 + detail），成功 steps 非空。</summary>
    public static (IReadOnlyList<StepSnapshot>? Steps, OpResult? Error) Parse(JsonElement steps)
    {
        if (steps.ValueKind != JsonValueKind.Array) return (null, OpResult.Fail(OpCodes.BadRequest, "steps 必须是数组"));
        var list = new List<StepSnapshot>();
        int i = 0;
        foreach (var s in steps.EnumerateArray())
        {
            i++;
            var (snap, err) = ParseStep(s, i);
            if (err != null) return (null, err);
            list.Add(snap!);
        }
        if (list.Count == 0) return (null, OpResult.Fail(OpCodes.ProgramInvalid, "程序为空"));
        return (list, null);
    }

    private static (StepSnapshot?, OpResult?) ParseStep(JsonElement s, int ordinal)
    {
        OpResult Bad(string code, string msg, object? detail = null) => OpResult.Fail(code, $"第 {ordinal} 步：{msg}", "err", detail ?? new { step = ordinal });
        if (s.ValueKind != JsonValueKind.Object) return (null, Bad(OpCodes.BadRequest, "每步须为对象 { type, params }"));
        if (!s.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String) return (null, Bad(OpCodes.BadRequest, "缺少 type"));
        string tname = (tEl.GetString() ?? "").Trim().ToLowerInvariant();
        if (!Enum.TryParse<StepType>(tname, true, out var type) || !StepDef.Registry.TryGetValue(type, out var def))
            return (null, Bad(OpCodes.ProgramInvalid, $"未知步骤类型「{tname}」（purge / heat / press / stir / wait / react / end）", new { step = ordinal, type = tname }));
        JsonElement? p = s.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object ? pEl : null;

        var step = ProgramStep.New(type);
        foreach (var f in def.Fields)
        {
            string key = $"{tname}.{f.Key}";
            JsonElement? v = p is { } pp && pp.TryGetProperty(f.Key, out var ve) && ve.ValueKind != JsonValueKind.Null ? ve : null;
            if (ProgramSchemaCodes.UnavailableFields.Contains(key))
            {
                if (v != null) return (null, Bad(OpCodes.ProgramInvalid, $"字段 {f.Key} 当前不可用（{ProgramSchemaCodes.GasUnavailableReason}）", new { step = ordinal, field = f.Key }));
                continue;
            }
            if (f.Kind == "sel")
            {
                if (v == null) continue;   // 缺省：New 已填 Options[0]
                if (v.Value.ValueKind != JsonValueKind.String) return (null, Bad(OpCodes.BadRequest, $"{f.Key} 须为枚举码字符串"));
                string code = v.Value.GetString()!;
                var opts = ProgramSchemaCodes.Options.TryGetValue(key, out var o) ? o : null;
                SchemaOption? opt = null;
                if (opts != null) foreach (var x in opts) if (x.Code == code || x.Label == code) opt = x;
                if (opt == null) return (null, Bad(OpCodes.ProgramInvalid, $"{f.Key} 的值「{code}」不在枚举里", new { step = ordinal, field = f.Key, value = code }));
                if (!opt.Available) return (null, Bad(OpCodes.ProgramInvalid, $"{f.Key} = {code} 当前不可用（{opt.Reason}）", new { step = ordinal, field = f.Key, value = code }));
                step.With(f.Key, opt.Label);
                continue;
            }
            if (v == null) continue;   // 缺省：Default
            if (v.Value.ValueKind != JsonValueKind.Number || !v.Value.TryGetDouble(out var num) || !double.IsFinite(num))
                return (null, Bad(OpCodes.BadRequest, $"{f.Key} 须为数值"));
            if (num < f.Min || num > f.Max)
                return (null, Bad(OpCodes.ParamOutOfRange, $"{f.Label} {N(num)}{f.Unit} 超出 {N(f.Min)}–{N(f.Max)}{f.Unit}",
                    new { step = ordinal, field = f.Key, value = num, min = f.Min, max = f.Max }));
            step.With(f.Key, num);
        }

        if (type == StepType.Heat)
        {
            string mode = "fixed";
            if (p is { } pp && pp.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
            {
                mode = (m.GetString() ?? "").Trim();
                if (mode is not ("fixed" or "curve"))
                    return (null, Bad(OpCodes.ProgramInvalid, $"heat.mode「{mode}」须为 fixed / curve", new { step = ordinal, field = "mode", value = mode }));
            }
            step.With("mode", mode);
            if (mode == "curve")
            {
                if (p is not { } pq || !pq.TryGetProperty("segments", out var segs) || segs.ValueKind != JsonValueKind.Array)
                    return (null, Bad(OpCodes.ProgramInvalid, "曲线模式须给 segments[{temp, rate, hold}]", new { step = ordinal, field = "segments" }));
                int n = 0;
                foreach (var g in segs.EnumerateArray())
                {
                    n++;
                    if (n > ProgramSchemaCodes.SegmentsMax)
                        return (null, Bad(OpCodes.ParamOutOfRange, $"曲线最多 {ProgramSchemaCodes.SegmentsMax} 段", new { step = ordinal, field = "segments", max = ProgramSchemaCodes.SegmentsMax }));
                    var seg = new HeatSeg();
                    foreach (var (sk, label, unit, lo, hi) in ProgramSchemaCodes.SegmentFields)
                    {
                        if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty(sk, out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetDouble(out var d) || !double.IsFinite(d))
                            return (null, Bad(OpCodes.BadRequest, $"第 {n} 段缺少数值字段 {sk}"));
                        if (d < lo || d > hi)
                            return (null, Bad(OpCodes.ParamOutOfRange, $"第 {n} 段 {label} {N(d)}{unit} 超出 {N(lo)}–{N(hi)}{unit}",
                                new { step = ordinal, segment = n, field = sk, value = d, min = lo, max = hi }));
                        switch (sk) { case "temp": seg.Temp = d; break; case "rate": seg.Rate = d; break; default: seg.Hold = d; break; }
                    }
                    step.Segments.Add(seg);
                }
                if (n < ProgramSchemaCodes.SegmentsMin)
                    return (null, Bad(OpCodes.ProgramInvalid, "曲线至少 1 段", new { step = ordinal, field = "segments" }));
            }
        }
        step.Ordinal = ordinal;
        return (step.ToSnapshot(), null);
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
