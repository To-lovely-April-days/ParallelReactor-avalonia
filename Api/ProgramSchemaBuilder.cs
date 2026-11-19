using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Models;

namespace ParallelReactor.Api;

/// <summary>
/// GET /program-schema（v0.8，STEPS-SPEC §6）：只列新的九种步骤（<see cref="StepTypeCodes.All"/> 的顺序），字段取自 <see cref="StepSpecs"/>。
/// 每个字段带生效范围 min / max（按设备设置收紧的「上限」）与声明范围 declaredMin / declaredMax、默认值、单位（压力一律 "psi"）、
/// 选项（英文码 + label）、visibleWhen（同一步骤的选项字段 key 的码在 in 里才显示）；heat 另有 segments（曲线段，mode = curve 时用）。
/// 纯逻辑（测试工程直接链接）；HmiApiSources.ProgramSchema 按设置修订号缓存它的结果。
/// </summary>
public static class ProgramSchemaBuilder
{
    public static ProgramSchemaDto Build(ProgramLimitSettings limits, long revision, double leakRate = StepSpecs.LeakRateDefault)
    {
        var steps = new List<StepSchemaDto>();
        foreach (var type in StepTypeCodes.All)
        {
            var spec = StepSpecs.Of(type);
            var fields = new List<FieldSchemaDto>();
            foreach (var f in spec.Fields)
            {
                string key = $"{type.ToCode()}.{f.Key}";
                bool available = !ProgramSchemaCodes.UnavailableFields.Contains(key);
                var vis = f.VisibleWhen is { } w ? new VisibleWhenDto(w.Key, w.In) : null;
                if (f.IsSel)
                {
                    fields.Add(new FieldSchemaDto(f.Key, f.Label, "sel", null, null, null, null, null, null, f.Options, true, null, null, null, null,
                        vis, f.DefaultSel));
                    continue;
                }
                var (lo, hi) = ProgramLimits.FieldRange(type, f.Key, f.Min, f.Max, limits);
                double? dflt = type == StepType.Leak && f.Key == "maxDrop" ? leakRate : f.Default;
                fields.Add(new FieldSchemaDto(f.Key, f.Label, "num", f.Unit, dflt, lo, hi, f.Min, f.Max, null,
                    available, available ? null : ProgramSchemaCodes.GasUnavailableReason, null, null, null, vis));
            }
            if (type == StepType.Heat)
            {
                // 曲线段：ProgramView 曲线编辑器的键盘范围；段温度同样受 TMaxSp / OverTemp 封顶
                var segFields = ProgramSchemaCodes.SegmentFields.Select(sf =>
                {
                    double hi = sf.Key == "temp" ? Math.Min(sf.Max, ProgramLimits.HeatTempMax(limits)) : sf.Max;
                    return new FieldSchemaDto(sf.Key, sf.Label, "num", sf.Unit, null, sf.Min, hi, sf.Min, sf.Max, null, true, null, null, null, null);
                }).ToArray();
                fields.Add(new FieldSchemaDto("segments", "曲线段（mode=curve 时使用）", "array", null, null, null, null, null, null, null, true, null,
                    segFields, ProgramSchemaCodes.SegmentsMin, ProgramSchemaCodes.SegmentsMax, new VisibleWhenDto("mode", new[] { "curve" })));
            }
            steps.Add(new StepSchemaDto(type.ToCode(), spec.Name, spec.Chip, spec.Help, fields.ToArray()));
        }
        return new ProgramSchemaDto(ApiVersionInfo.ApiVersion, revision, steps.Select(x => x.Type).ToArray(), steps.ToArray());
    }
}
