using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ParallelReactor.PerfTest.Model;

/// <summary>加载嵌入 / 文件方案并校验。</summary>
public static class PlanLoader
{
    public const string BaselinePlan = "rv1-4-baseline";
    public const string SmokePlan = "smoke";

    /// <summary>嵌入资源名列表（不含 plans/ 前缀与扩展名）。</summary>
    public static IReadOnlyList<string> EmbeddedNames()
        => typeof(PlanLoader).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("plans/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n.Substring(6, n.Length - 11)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>加载嵌入方案并校验（校验失败抛 <see cref="PlanValidationException"/>）。</summary>
    public static TestPlan LoadEmbedded(string name = BaselinePlan)
    {
        var asm = typeof(PlanLoader).Assembly;
        using var s = asm.GetManifestResourceStream("plans/" + name + ".json") ?? throw new FileNotFoundException("嵌入方案不存在：" + name);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return Parse(r.ReadToEnd());
    }

    public static TestPlan LoadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>解析 + 规范化（Group 缺省 = 阶段名；二期阶段的项 Enabled=false）+ 校验。</summary>
    public static TestPlan Parse(string json)
    {
        var plan = JsonSerializer.Deserialize<TestPlan>(json, PlanJson.Options) ?? throw new PlanValidationException(new[] { "方案 JSON 为空" });
        Normalize(plan);
        var errors = PlanValidator.Validate(plan);
        if (errors.Count > 0) throw new PlanValidationException(errors);
        return plan;
    }

    public static string ToJson(TestPlan plan) => JsonSerializer.Serialize(plan, PlanJson.Options);

    /// <summary>
    /// 把多份方案合成一份（界面清单用：正式方案 + 快速自检附录）。第一份提供 planId / 标题 / 版本；
    /// 后续方案的阶段依次追加并重新编号（index 从首份最大值 +1 起）。合并结果再做一次校验（ID 不得重复）。
    /// </summary>
    public static TestPlan Merge(params TestPlan[] plans)
    {
        if (plans.Length == 0) throw new ArgumentException("至少一份方案", nameof(plans));
        var first = plans[0];
        var merged = new TestPlan { PlanId = first.PlanId, Title = first.Title, Version = first.Version, Source = first.Source };
        int next = 0;
        foreach (var s in first.Stages) { merged.Stages.Add(s); next = Math.Max(next, s.Index + 1); }
        foreach (var p in plans.Skip(1))
            foreach (var s in p.Stages)
            {
                merged.Stages.Add(new TestStage { Index = next++, Name = s.Name, Phase2 = s.Phase2, Items = s.Items });
                if (!string.IsNullOrEmpty(p.Source)) merged.Source = merged.Source + "；" + p.Source;
            }
        var errors = PlanValidator.Validate(merged);
        if (errors.Count > 0) throw new PlanValidationException(errors);
        return merged;
    }

    private static void Normalize(TestPlan plan)
    {
        foreach (var st in plan.Stages)
            foreach (var it in st.Items)
            {
                if (string.IsNullOrEmpty(it.Group)) it.Group = st.Name;
                if (st.Phase2 && it.Enabled) { it.Enabled = false; it.DisabledReason ??= "二期"; }
                foreach (var step in it.Steps)
                    if (string.IsNullOrEmpty(step.Id)) step.Id = it.Id + "-" + (it.Steps.IndexOf(step) + 1);
            }
    }
}

public sealed class PlanValidationException : Exception
{
    public PlanValidationException(IReadOnlyList<string> errors) : base("方案校验失败：\n" + string.Join("\n", errors)) => Errors = errors;
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>结构校验（「schema 式」）：ID 唯一、通道 1..8、步骤种类所需字段、公式 / 命令 / 设置键已知、引用的步骤 / 字段存在。</summary>
public static class PlanValidator
{
    public static readonly string[] Signals = { "tempC", "pressurePsi", "pressureRaw", "heatOutputPct", "rpm", "busVoltageV" };

    public static List<string> Validate(TestPlan plan)
    {
        var errors = new List<string>();
        void Err(string s) => errors.Add(s);
        if (string.IsNullOrWhiteSpace(plan.PlanId)) Err("planId 为空");
        if (string.IsNullOrWhiteSpace(plan.Title)) Err("title 为空");
        if (plan.Stages.Count == 0) Err("stages 为空");

        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stageIdx = new HashSet<int>();
        foreach (var st in plan.Stages)
        {
            if (!stageIdx.Add(st.Index)) Err($"阶段序号重复：{st.Index}");
            if (string.IsNullOrWhiteSpace(st.Name)) Err($"阶段 {st.Index} 无名称");
            foreach (var it in st.Items)
            {
                var p = $"[{it.Id}]";
                if (string.IsNullOrWhiteSpace(it.Id)) { Err($"阶段 {st.Index} 有测试项无 id"); continue; }
                if (!itemIds.Add(it.Id)) Err($"{p} id 重复");
                if (string.IsNullOrWhiteSpace(it.Title)) Err($"{p} title 为空");
                if (string.IsNullOrWhiteSpace(it.SourceRef)) Err($"{p} sourceRef 为空（须标注方案出处）");
                if (it.TargetChannels.Count == 0) Err($"{p} targetChannels 为空");
                foreach (var c in it.TargetChannels) if (c < 1 || c > 8) Err($"{p} targetChannels 含非法通道 {c}");
                if (it.TargetChannels.Distinct().Count() != it.TargetChannels.Count) Err($"{p} targetChannels 有重复");
                if (it.EstimatedMinutes <= 0) Err($"{p} estimatedMinutes 须 > 0");
                if (it.Enabled && it.Steps.Count == 0) Err($"{p} 启用项无步骤");
                if (it.Repeat is { } rp)
                {
                    if (rp.Values is { Count: 0 }) Err($"{p} repeat.values 为空数组");
                    if (rp.Values == null && rp.Count < 1) Err($"{p} repeat.count 须 ≥ 1");
                }
                ValidateSteps(it, Err);
                foreach (var rf in it.RecordFields)
                    if (string.IsNullOrWhiteSpace(rf.Key) || string.IsNullOrWhiteSpace(rf.Label)) Err($"{p} recordFields 有空 key/label");
            }
        }
        // 前置条件里引用的项 ID 必须存在（纯文字条件允许：不含空格且形如 ID 的才检查）
        foreach (var it in plan.AllItems())
            foreach (var pre in it.Preconditions)
                if (pre.Length <= 4 && pre.All(ch => char.IsLetterOrDigit(ch) || ch == '-') && !itemIds.Contains(pre) && !pre.Equals("D0", StringComparison.OrdinalIgnoreCase))
                    Err($"[{it.Id}] 前置条件引用了不存在的项 {pre}");
        return errors;
    }

    /// <summary>接口错误码全集（Contracts OpCodes 的全部常量）：allowCodes 只能引用它们。</summary>
    public static readonly HashSet<string> KnownOpCodes = new(
        typeof(ParallelReactor.Commands.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!), StringComparer.Ordinal);

    public static readonly string[] EnvKeys = { "ambientTempC", "humidityPct", "atmPressureKpa", "supplyVoltageV", "assemblyNo", "mediumAndMassG" };

    /// <summary>programPut 正文校验（HMI ProgramJson 的规则）：type 为已知步骤码；sel 字段必须是枚举码字符串（区分大小写，不接受 bool）。</summary>
    private static void ValidateProgramSteps(string q, System.Text.Json.JsonElement steps, Action<string> Err)
    {
        if (steps.ValueKind != System.Text.Json.JsonValueKind.Array) { Err($"{q} programPut.args.steps 须为数组"); return; }
        int i = 0;
        foreach (var st in steps.EnumerateArray())
        {
            i++;
            var type = st.TryGetProperty("type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()! : "";
            if (!ParallelReactor.Models.StepTypeCodes.TryParse(type, out _)) { Err($"{q} 程序第 {i} 步 type 未知：{type}"); continue; }
            if (!st.TryGetProperty("params", out var ps) || ps.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            foreach (var p in ps.EnumerateObject())
            {
                if (!ParallelReactor.Api.ProgramSchemaCodes.Options.TryGetValue($"{type}.{p.Name}", out var opts)) continue;
                if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) { Err($"{q} 程序第 {i} 步 {p.Name} 须为枚举码字符串（{string.Join("/", opts.Select(o => o.Code))}）"); continue; }
                var code = p.Value.GetString()!;
                if (code.Contains('{')) continue;   // 模板值运行时解析
                if (!opts.Any(o => o.Code == code)) Err($"{q} 程序第 {i} 步 {p.Name} 的值「{code}」不在枚举里（{string.Join("/", opts.Select(o => o.Code))}）");
            }
        }
    }

    private static void ValidateSteps(TestItem it, Action<string> Err)
    {
        var p = $"[{it.Id}]";
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var manualKeys = new HashSet<string>(StringComparer.Ordinal);
        var metricKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in it.Steps)
        {
            var q = $"{p} 步骤 {s.Id}";
            if (!stepIds.Add(s.Id)) Err($"{q} id 重复");
            if (string.IsNullOrWhiteSpace(s.Title)) Err($"{q} title 为空");
            if (s.Channels != null) foreach (var c in s.Channels) if (c < 1 || c > 8) Err($"{q} channels 含非法通道 {c}");
            if (s.OnFail is not ("abortItem" or "continue" or "skipItem")) Err($"{q} onFail 非法：{s.OnFail}");
            switch (s.Kind)
            {
                case StepKind.Instruction:
                    if (string.IsNullOrWhiteSpace(s.Text)) Err($"{q} instruction 缺 text");
                    break;
                case StepKind.Checklist:
                    if (s.ChecklistItems is not { Count: > 0 }) Err($"{q} checklist 缺 checklistItems");
                    break;
                case StepKind.ManualEntry:
                    if (s.Fields is not { Count: > 0 }) { Err($"{q} manualEntry 缺 fields"); break; }
                    foreach (var f in s.Fields)
                    {
                        if (string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Label)) Err($"{q} 字段缺 key/label");
                        else if (!manualKeys.Add(f.Key)) Err($"{q} 录入字段 key 重复：{f.Key}");
                        if (f.Repeat < 1) Err($"{q} 字段 {f.Key} repeat 须 ≥ 1");
                        if (f.Min.HasValue && f.Max.HasValue && f.Min > f.Max) Err($"{q} 字段 {f.Key} min > max");
                        if (f.EnvKey != null && !EnvKeys.Contains(f.EnvKey)) Err($"{q} 字段 {f.Key} envKey 非法：{f.EnvKey}");
                    }
                    break;
                case StepKind.Command:
                    if (s.Command == null) { Err($"{q} command 缺 command"); break; }
                    if (!CommandOps.All.Contains(s.Command.Op)) Err($"{q} 未知命令 op：{s.Command.Op}");
                    else
                    {
                        var a = s.Command.Args;
                        bool Has(string k) => a != null && a.ContainsKey(k);
                        switch (s.Command.Op)
                        {
                            case CommandOps.HeatStart: case CommandOps.Setpoint: case CommandOps.AutotuneStart:
                                if (!Has("tempC")) Err($"{q} {s.Command.Op} 缺 args.tempC"); break;
                            case CommandOps.Stir: if (!Has("rpm")) Err($"{q} stir 缺 args.rpm"); break;
                            case CommandOps.ProgramPut: if (!Has("steps")) Err($"{q} programPut 缺 args.steps"); break;
                        }
                        if (s.Command.ExpectStatus != null)
                            foreach (var e in s.Command.ExpectStatus) if (e is not ("confirmed" or "accepted")) Err($"{q} expectStatus 非法：{e}");
                        if (s.Command.AllowCodes != null)
                            foreach (var c in s.Command.AllowCodes) if (!KnownOpCodes.Contains(c)) Err($"{q} allowCodes 含接口不存在的错误码：{c}");
                        if (s.Command.Op == CommandOps.ProgramPut && Has("steps")) ValidateProgramSteps(q, a!["steps"], Err);
                    }
                    break;
                case StepKind.WaitStable:
                    if (s.Signal == null || !Signals.Contains(s.Signal)) Err($"{q} waitStable signal 非法：{s.Signal}");
                    if (!s.Band.HasValue && !s.MaxSlopePerMin.HasValue) Err($"{q} waitStable 需 band 或 maxSlopePerMin");
                    if (!(s.WindowSec > 0)) Err($"{q} waitStable 缺 windowSec");
                    if (!(s.TimeoutSec > 0)) Err($"{q} waitStable 缺 timeoutSec");
                    if (s.Band.HasValue && s.Reference == null) Err($"{q} waitStable 有 band 需 reference");
                    if (s.Reference == "value" && !s.Value.HasValue) Err($"{q} reference=value 需 value");
                    if (s.Reference is { } r && r.StartsWith("manual:", StringComparison.Ordinal) && !manualKeys.Contains(r.Substring(7))) Err($"{q} reference 引用未定义录入 {r}");
                    break;
                case StepKind.Hold:
                    if (!(s.DurationSec > 0)) Err($"{q} hold 缺 durationSec");
                    if (s.Signals != null) foreach (var sg in s.Signals) if (!Signals.Contains(sg)) Err($"{q} hold signals 非法：{sg}");
                    break;
                case StepKind.Measure:
                    if (s.Metrics is not { Count: > 0 }) { Err($"{q} measure 缺 metrics"); break; }
                    foreach (var m in s.Metrics)
                    {
                        var mq = $"{q} 指标 {m.Key}";
                        if (string.IsNullOrWhiteSpace(m.Key)) { Err($"{q} 指标缺 key"); continue; }
                        if (!metricKeys.Add(m.Key)) Err($"{mq} key 重复");
                        if (!MetricFormulas.All.Contains(m.Formula)) Err($"{mq} 未知公式 {m.Formula}");
                        if (m.Formula is MetricFormulas.Duration or MetricFormulas.Count) { }
                        else if (m.Signal == null) Err($"{mq} 缺 signal");
                        else if (m.Signal.StartsWith("manual:", StringComparison.Ordinal)) { if (!manualKeys.Contains(m.Signal.Substring(7))) Err($"{mq} signal 引用未定义录入 {m.Signal}"); }
                        else if (!Signals.Contains(m.Signal)) Err($"{mq} signal 非法 {m.Signal}");
                        if (m.Window is { } w && w.StartsWith("step:", StringComparison.Ordinal) && !it.Steps.Any(x => x.Id == w.Substring(5))) Err($"{mq} window 引用未定义步骤 {w}");
                        if (m.Window is { } w2 && w2.StartsWith("from:", StringComparison.Ordinal) && !it.Steps.Any(x => x.Id == w2.Substring(5))) Err($"{mq} window 引用未定义步骤 {w2}");
                        if (m.BandPct is { } bp && bp <= 0) Err($"{mq} bandPct 须 > 0");
                        if (m.Reference is { } rr && rr.StartsWith("manual:", StringComparison.Ordinal) && !manualKeys.Contains(rr.Substring(7))) Err($"{mq} reference 引用未定义录入 {rr}");
                        if (m.Formula is MetricFormulas.Overshoot or MetricFormulas.TimeToBand or MetricFormulas.SettlingTime or MetricFormulas.MeanDeviation or MetricFormulas.MaxDeviation or MetricFormulas.MinDeviation or MetricFormulas.RpmError && m.Reference == null) Err($"{mq} 公式 {m.Formula} 需 reference");
                        if (m.Formula is MetricFormulas.TimeToBand or MetricFormulas.SettlingTime && !m.Band.HasValue) Err($"{mq} 公式 {m.Formula} 需 band");
                        if (m.Formula is MetricFormulas.TimeToValue && !m.Value.HasValue && m.Reference != "iteration") Err($"{mq} timeToValue 需 value 或 reference=iteration");
                        if (m.Direction is not ("lowerBetter" or "higherBetter" or "twoSided" or "none")) Err($"{mq} direction 非法 {m.Direction}");
                    }
                    break;
                case StepKind.Compare:
                    if (s.Compare == null) { Err($"{q} compare 缺 compare"); break; }
                    if (!SettingsKeys.All.Contains(s.Compare.Key)) Err($"{q} compare 未知设置键 {s.Compare.Key}");
                    if (s.Compare.IterationAs is not ("expect" or "min" or "max")) Err($"{q} compare.iterationAs 非法：{s.Compare.IterationAs}");
                    if (!s.Compare.Min.HasValue && !s.Compare.Max.HasValue && !s.Compare.Expect.HasValue && s.Reference != "iteration") Err($"{q} compare 需 min/max/expect 之一");
                    break;
                case StepKind.Marker:
                    if (string.IsNullOrWhiteSpace(s.Template) && string.IsNullOrWhiteSpace(s.Title)) Err($"{q} marker 缺 template");
                    break;
                case StepKind.SessionBegin:
                case StepKind.SessionEnd:
                    break;
                default:
                    Err($"{q} 未知 kind {s.Kind}");
                    break;
            }
        }
        foreach (var rf in it.RecordFields)
        {
            if (rf.Source.StartsWith("metric:", StringComparison.Ordinal) && !metricKeys.Contains(rf.Source.Substring(7))) Err($"{p} 记录字段 {rf.Key} 引用未定义指标 {rf.Source}");
            if (rf.Source.StartsWith("manual:", StringComparison.Ordinal) && !manualKeys.Contains(rf.Source.Substring(7))) Err($"{p} 记录字段 {rf.Key} 引用未定义录入 {rf.Source}");
        }
    }
}
