using System;
using System.Linq;
using ParallelReactor.PerfTest.Model;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>方案加载与校验：嵌入 JSON 能加载、每项有出处、步骤引用合法、二期项禁用、往返稳定、校验器能抓错。</summary>
public class PerfPlanTests
{
    [Fact]
    public void Merge_BaselinePlusSmoke_AppendsStageAndValidates()
    {
        var merged = PlanLoader.Merge(PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan), PlanLoader.LoadEmbedded(PlanLoader.SmokePlan));
        Assert.Equal("RV1-4-baseline", merged.PlanId);
        Assert.Empty(PlanValidator.Validate(merged));
        Assert.Equal(29, merged.AllItems().Count());
        Assert.NotNull(merged.FindItem("S1Q"));
        Assert.NotNull(merged.FindItem("F4"));
        var last = merged.Stages[^1];
        Assert.Equal("快速自检", last.Name);
        Assert.True(last.Index > merged.Stages[0].Index);
        Assert.Equal(merged.Stages.Count, merged.Stages.Select(s => s.Index).Distinct().Count());
        // 同一方案合并两次 → ID 重复必须被校验器抓住
        Assert.Throws<PlanValidationException>(() => PlanLoader.Merge(PlanLoader.LoadEmbedded(PlanLoader.SmokePlan), PlanLoader.LoadEmbedded(PlanLoader.SmokePlan)));
    }

    [Fact]
    public void Baseline_LoadsAndValidates()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        Assert.Equal("RV1-4-baseline", plan.PlanId);
        Assert.Empty(PlanValidator.Validate(plan));
        var ids = plan.AllItems().Select(i => i.Id).ToList();
        foreach (var expected in new[] { "R0", "F1", "F2", "F3", "F4", "F5", "F6", "S1", "S2", "S3", "P1", "P2", "P3", "P4", "T0", "T1", "T2", "T4", "T5", "C2-a", "T3", "C1", "C2-b", "LT1", "M1", "LT2" })
            Assert.Contains(expected, ids);
        Assert.Equal(27, ids.Count);                       // 26 + F4H（硬件急停）
        Assert.Equal(21, plan.AllItems().Count(i => i.Enabled));
    }

    [Fact]
    public void Baseline_EveryItemTracesToPlanSectionAndHasHint()
    {
        var plan = PlanLoader.LoadEmbedded();
        foreach (var it in plan.AllItems())
        {
            Assert.StartsWith("§", it.SourceRef);
            Assert.False(string.IsNullOrWhiteSpace(it.Purpose), it.Id + " 缺 purpose");
            Assert.False(string.IsNullOrWhiteSpace(it.Hint), it.Id + " 缺 hint");
            Assert.True(it.EstimatedMinutes > 0);
            foreach (var c in it.TargetChannels) Assert.InRange(c, 1, 8);
        }
    }

    [Fact]
    public void Baseline_Phase2ItemsDisabledWithReason()
    {
        var plan = PlanLoader.LoadEmbedded();
        foreach (var id in new[] { "T3", "C1", "C2-b", "LT1", "M1", "LT2" })
        {
            var it = plan.FindItem(id)!;
            Assert.False(it.Enabled, id);
            Assert.Contains("二期", it.DisabledReason);
        }
        Assert.True(plan.Stages.Single(s => s.Index == 6).Phase2);
    }

    [Fact]
    public void Baseline_StepsReferenceValidChannelsFieldsAndMetrics()
    {
        var plan = PlanLoader.LoadEmbedded();
        foreach (var it in plan.AllItems().Where(i => i.Enabled))
        {
            Assert.NotEmpty(it.Steps);
            var manual = it.Steps.Where(s => s.Kind == StepKind.ManualEntry).SelectMany(s => s.Fields!).Select(f => f.Key).ToHashSet();
            var metrics = it.Steps.Where(s => s.Kind == StepKind.Measure).SelectMany(s => s.Metrics!).Select(m => m.Key).ToHashSet();
            foreach (var s in it.Steps)
            {
                if (s.Channels != null) foreach (var c in s.Channels) Assert.Contains(c, it.TargetChannels);
                if (s.Kind == StepKind.Command) Assert.Contains(s.Command!.Op, CommandOps.All);
                if (s.Kind == StepKind.Measure)
                    foreach (var m in s.Metrics!)
                    {
                        if (m.Signal is { } sig && sig.StartsWith("manual:")) Assert.Contains(sig.Substring(7), manual);
                        if (m.Window is { } w && w.StartsWith("step:")) Assert.Contains(it.Steps, x => x.Id == w.Substring(5));
                    }
            }
            foreach (var rf in it.RecordFields)
            {
                if (rf.Source.StartsWith("metric:")) Assert.Contains(rf.Source.Substring(7), metrics);
                if (rf.Source.StartsWith("manual:")) Assert.Contains(rf.Source.Substring(7), manual);
            }
        }
    }

    [Fact]
    public void Baseline_ItemStepCountsAreStable()
    {
        var plan = PlanLoader.LoadEmbedded();
        var table = plan.AllItems().Where(i => i.Enabled).Select(i => $"{i.Id}:{i.Steps.Count}").ToList();
        Assert.Contains("F4:11", table);   // + F4-11 重新申请控制权
        Assert.Contains("T1:9", table);
        Assert.Contains("S1:4", table);
        Assert.Equal(12, plan.FindItem("S1")!.Repeat!.Iterations);
        Assert.Equal(3, plan.FindItem("T1")!.Repeat!.Iterations);
    }

    [Fact]
    public void Smoke_Loads()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        Assert.Equal(2, plan.AllItems().Count());
        Assert.Equal(6, plan.FindItem("T0Q")!.Steps.Count);
        Assert.Contains("rv1-4-baseline", PlanLoader.EmbeddedNames());
        Assert.Contains("smoke", PlanLoader.EmbeddedNames());
    }

    [Fact]
    public void Plan_RoundTripsThroughJson()
    {
        var plan = PlanLoader.LoadEmbedded();
        var json = PlanLoader.ToJson(plan);
        var back = PlanLoader.Parse(json);
        Assert.Equal(plan.AllItems().Count(), back.AllItems().Count());
        Assert.Equal(plan.FindItem("T1")!.Steps.Count, back.FindItem("T1")!.Steps.Count);
        Assert.Equal(StepKind.WaitStable, back.FindItem("T1")!.Steps[5].Kind);
    }

    [Theory]
    [InlineData("\"targetChannels\": [1],", "\"targetChannels\": [1, 9],", "非法通道 9")]
    [InlineData("\"sourceRef\": \"自检（非方案项）\"", "\"sourceRef\": \"\"", "sourceRef 为空")]
    [InlineData("\"op\": \"heatStart\", \"args\": { \"tempC\": 60 }", "\"op\": \"heatStart\", \"args\": { }", "缺 args.tempC")]
    [InlineData("\"formula\": \"mean\", \"unit\": \"℃\", \"signal\": \"tempC\", \"window\": \"step:T0Q-3\"", "\"formula\": \"mean\", \"unit\": \"℃\", \"signal\": \"tempC\", \"window\": \"step:NOPE\"", "引用未定义步骤")]
    [InlineData("\"signal\": \"manual:tacho\"", "\"signal\": \"manual:ghost\"", "引用未定义录入")]
    public void Validator_CatchesBrokenPlans(string find, string replace, string expectedError)
    {
        var json = ReadSmoke();
        Assert.Contains(find, json);
        var broken = json.Replace(find, replace);
        var ex = Assert.Throws<PlanValidationException>(() => PlanLoader.Parse(broken));
        Assert.Contains(ex.Errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void Validator_RejectsDuplicateIdsAndUnknownOps()
    {
        var json = ReadSmoke().Replace("\"id\": \"S1Q\"", "\"id\": \"T0Q\"").Replace("\"op\": \"stirStop\"", "\"op\": \"teleport\"");
        var ex = Assert.Throws<PlanValidationException>(() => PlanLoader.Parse(json));
        Assert.Contains(ex.Errors, e => e.Contains("id 重复"));
        Assert.Contains(ex.Errors, e => e.Contains("未知命令 op：teleport"));
    }

    private static string ReadSmoke()
    {
        using var s = typeof(PlanLoader).Assembly.GetManifestResourceStream("plans/smoke.json")!;
        using var r = new System.IO.StreamReader(s);
        return r.ReadToEnd();
    }
}
