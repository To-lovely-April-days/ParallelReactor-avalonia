using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 程序步骤 v0.8（research/fusion-port/notes/STEPS-SPEC.md）在 PC 端的其余部分：显示用的步骤（ProgramDocs → StepText）、剩余时间（EtaEstimator）、
/// 旧格式转换时的收紧与说明（同 HMI 的 PUT）、HMI schema 的 visibleWhen / defaultCode、条件矩阵方案里的旧步骤、反应的结束原因。
/// </summary>
public class StepsV08Tests
{
    private static readonly ModelProfile Pr8 = PcV3Models.Get("PR8");
    private static readonly SchemaLimits Lim = new(200, 220, 450, 4.14 * 145.0377, 3.5 * 145.0377);
    private static ProgramSchema Schema => ProgramSchema.Default(Lim);

    private static IReadOnlyList<ProgramStep> Display(PcProgram p) => ProgramDocs.ToSteps(p.ToDoc(), Pr8);

    // ———————————————————— 显示 ————————————————————

    [Fact]
    public void Display_NewKinds_SummaryCondSections()
    {
        var s = Display(ProgramTemplates.Hydrogenation("T1", "PR8"));
        Assert.Equal(new[] { StepKind.Leak, StepKind.Exchange, StepKind.Exchange, StepKind.Stir, StepKind.Heat, StepKind.Press, StepKind.React, StepKind.Vent, StepKind.End }, s.Select(x => x.Type));
        // 气密：惰性气体、试验压力、静置 + 保压
        Assert.Equal("3.000 MPa · 保压 35 min", StepText.Summary(Pr8, s[0]));
        Assert.Equal(new StepCond("步骤时间", "> 35 min"), StepText.Cond(s[0]));
        Assert.Equal(("惰性气体 · 保压检查", "试验压力", "3.000 MPa"), (StepText.Sections(Pr8, 1, s[0])[1].Mode, StepText.Sections(Pr8, 1, s[0])[1].Label, StepText.Sections(Pr8, 1, s[0])[1].Value));
        Assert.Equal(36, s[0].EstMin);
        // 置换
        Assert.Equal("惰性气体 × 3", StepText.Summary(Pr8, s[1]));
        Assert.Equal("气体 A × 3", StepText.Summary(Pr8, s[2]));
        Assert.Equal(new StepCond("结束条件", "置换 3 次完成"), StepText.Cond(s[1]));
        Assert.Equal("惰性气体 · 置换 ×3", StepText.Sections(Pr8, 1, s[1])[1].Mode);
        Assert.Equal(1.3, s[1].EstMin);
        // 搅拌：立即进入下一步
        Assert.Equal(("400 rpm", "立即", 0.0), (StepText.Summary(Pr8, s[3]), StepText.Cond(s[3]).Value, s[3].EstMin));
        // 反应：「≤」+ 吸气停止
        Assert.Equal(StepMark.UpTo, s[6].Mark);
        Assert.Equal("≤ 8 h 00 min", StepText.Summary(Pr8, s[6]));
        Assert.Equal(new StepCond("结束条件", "≤ 480 min · 吸气停止"), StepText.Cond(s[6]));
        Assert.Equal(2.0, s[6].Target!.Value, 2);                                 // 补压到充压的目标
        // 放空
        Assert.Equal("放到 0.020 MPa · 惰性置换 × 3", StepText.Summary(Pr8, s[7]));
        Assert.Equal("压力 ≤ 0.020 MPa", StepText.Cond(s[7], Pr8).Value);
        Assert.Equal(("排空阀", "打开 · 放空 · 惰性置换 ×3", "放到"), (StepText.Sections(Pr8, 1, s[7])[1].Dev, StepText.Sections(Pr8, 1, s[7])[1].Mode, StepText.Sections(Pr8, 1, s[7])[1].Label));
        Assert.Equal(2.0, s[7].EstMin);
        // 结束：固定收尾
        Assert.Equal(("关加热 · 阀全关", "立即"), (StepText.Summary(Pr8, s[8]), StepText.Cond(s[8]).Value));
        Assert.Equal(("关闭", "全关"), (StepText.Sections(Pr8, 1, s[8])[0].Mode, StepText.Sections(Pr8, 1, s[8])[1].Mode));
        // 只吹总管的置换
        var ex0 = ProgramTemplates.Step(StepType.Exchange, null, ("cycles", 0), ("lineFlush", 2));
        var d0 = ProgramDocs.ToSteps(new PcProgram { Steps = { ex0 } }.ToDoc(), Pr8)[0];
        Assert.Equal(("惰性气体 · 只吹总管 × 2", "吹总管 2 次完成", 0.3), (StepText.Summary(Pr8, d0), StepText.Cond(d0).Value, d0.EstMin));
    }

    [Fact]
    public void Display_Hold_TimeAndConditions()
    {
        var p = new PcProgram();
        p.Steps.Add(ProgramTemplates.HoldTime(10));
        p.Steps.Add(ProgramTemplates.Cool(40, maxWait: 30));
        p.Steps.Add(ProgramTemplates.Step(StepType.Hold, null, ("until", "pressAbove"), ("pres", 0.5), ("maxWait", 90)));
        var s = Display(p);
        Assert.Equal(("10 min", new StepCond("步骤时间", "> 10 min"), StepMark.None, 10.0), (StepText.Summary(Pr8, s[0]), StepText.Cond(s[0]), s[0].Mark, s[0].EstMin));
        Assert.Equal(("温度 ≤ 40 ℃", "温度 ≤ 40 ℃ · 最长 30 min", StepMark.About, 15.0), (StepText.Summary(Pr8, s[1]), StepText.Cond(s[1]).Value, s[1].Mark, s[1].EstMin));
        Assert.Equal(("压力 ≥ 0.500 MPa", StepMark.About, 15.0), (StepText.Summary(Pr8, s[2]), s[2].Mark, s[2].EstMin));
        Assert.Equal(90, s[2].MaxMin);
    }

    // ———————————————————— 剩余时间 ————————————————————

    [Fact]
    public void Eta_NewKinds()
    {
        var t0 = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var s = Display(ProgramTemplates.Hydrogenation("T1", "PR8"));
        // 搅拌：立即完成
        Assert.Equal(0, EtaEstimator.Current(s[3], t0, t0, t0, null).RemainS);
        // 气密：定时（静置 + 保压 35 min），按开始时刻
        var leak = EtaEstimator.Current(s[0], t0.AddMinutes(5), t0, t0, null);
        Assert.Equal((EtaBasis.Timer, 30 * 60.0), (leak.Basis, leak.RemainS));
        // 置换 / 放空：按计划时长
        Assert.Equal(1.3 * 60, EtaEstimator.Current(s[1], t0, t0, t0, null).RemainS, 6);
        // 保持 · 温度 ≤ 40：按读数与降温速率（1 ℃/min），不超过最长等待
        var cool = Display(new PcProgram { Steps = { ProgramTemplates.Cool(40, maxWait: 30) } })[0];
        var e = EtaEstimator.Current(cool, t0.AddMinutes(10), t0, t0, null, new EtaReading(TempC: 60));
        Assert.Equal((EtaBasis.Estimate, 20 * 60.0), (e.Basis, e.RemainS));            // min(20 min, 30 − 10)
        Assert.Equal(0, EtaEstimator.Current(cool, t0, t0, t0, null, new EtaReading(TempC: 35)).RemainS);
        // 计划：v0.8 的结束为 0；保持 ≤ 按前一步留下的温度
        var plan = EtaEstimator.Plan(Display(new PcProgram { Steps = { ProgramTemplates.Heat(80), ProgramTemplates.Cool(40), ProgramTemplates.End() } }));
        Assert.Equal((40.0, 0.0), (plan[1].EstMin, plan[2].EstMin));
        Assert.Equal(60, Progress.StepPlanS(plan[2]));                                    // 时间轴上至少 1 min
        Assert.Equal(10 * 60, Progress.StepPlanS(s.Count > 0 ? Display(new PcProgram { Steps = { ProgramTemplates.HoldTime(10) } })[0] : null));
    }

    // ———————————————————— 旧格式转换：收紧 + 说明（同 HMI） ————————————————————

    [Fact]
    public void Migration_LegacyOutOfRange_ClampedWithNotes_NewFormatNot()
    {
        var doc = new ProgramDoc { Name = "旧" };
        doc.Add(StepType.Purge, new { gas = "inert", cycles = 50, fill = 3, vent = 5 });
        doc.Add(StepType.React, new { endBy = "time", time = 0 });
        doc.Add(StepType.Stir, new { rpm = 1000, dur = 0 });
        var p = PcProgram.FromDoc(doc, "PR8", "x");
        Assert.True(p.Migrated);
        Assert.Equal((10.0, 1.0, 1000.0), (p.Steps[0].N("lineFlush"), p.Steps[1].N("time"), p.Steps[2].N("rpm")));   // 转速不在这里收紧（同 HMI）
        Assert.Equal(new[] { "第 01 步 置换：先吹总管 50 次 超出新范围，已改为 10 次", "第 02 步 反应：最长时间 0 min 超出新范围，已改为 1 min" }, p.MigrationNotes);
        Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Key == "rpm");                               // 超出设备上限 → 待改项
        // 新格式的越界值不收紧（由待改项拒绝，同 HMI 的 422）
        var nd = new ProgramDoc { Name = "新" };
        nd.Add(StepType.Exchange, new { gas = "inert", cycles = 50, lineFlush = 2 });
        var q = PcProgram.FromDoc(nd, "PR8", "y");
        Assert.False(q.Migrated);
        Assert.Equal(50.0, q.Steps[0].N("cycles"));
        Assert.Empty(q.MigrationNotes);
        Assert.Contains(ProgramIssues.Of(q.Steps, Schema, "HT-PR8"), x => x.Key == "cycles");
    }

    [Fact]
    public void Migration_StirZeroWithDur_KeepsTheHold_DuplicateEndsFlagged()
    {
        // Contracts 的 StepMigration（HMI 修过的两处）：stir {rpm 0, dur} 照样加保持；多余的 end 去掉时标转换
        var doc = new ProgramDoc { Name = "x" };
        doc.Add(StepType.Stir, new { rpm = 0, dur = 5 });
        var p = PcProgram.FromDoc(doc, "PR8", "a");
        Assert.Equal(new[] { StepType.Stir, StepType.Hold, StepType.End }, p.Steps.Select(s => s.Type));
        var two = new ProgramDoc { Name = "y" };
        two.Add(StepType.End, new { });
        two.Add(StepType.Hold, new { until = "time", time = 1 });
        two.Add(StepType.End, new { });
        var q = PcProgram.FromDoc(two, "PR8", "b");
        Assert.True(q.Migrated);
        Assert.Equal(new[] { StepType.Hold, StepType.End }, q.Steps.Select(s => s.Type));
    }

    [Fact]
    public void MatrixJson_LegacyPlanSteps_MigratedBeforeCompare()
    {
        var tpl = new PcProgram { Id = "T", Name = "t", Model = "PR8" };
        tpl.Steps.AddRange(new[] { ProgramTemplates.Exchange("inert", 0), ProgramTemplates.Heat(80), ProgramTemplates.End() });
        tpl.Steps[0].Num["lineFlush"] = 4;
        var chs = Enumerable.Range(1, 4).Select(n => new MxChannel(n, false, false, null, 5)).ToArray();
        // 旧版本存的方案：吹扫 4 次 → 升温（共用参数 wait 改成 20）→ 结束 · 密封（没有 cool）
        const string json = """
            { "template": "T", "name": "旧方案", "include": [1, 2], "vol": {}, "per": { "1:temp": { "1": 90 } },
              "steps": [ { "type": "purge", "name": "吹扫", "params": { "gas": "inert", "cycles": 4 } },
                         { "type": "heat", "name": "升温", "params": { "mode": "fixed", "temp": 80, "wait": 20 } },
                         { "type": "end", "name": "结束", "params": { "mode": "seal" } } ] }
            """;
        using var d = JsonDocument.Parse(json);
        var m = MatrixJson.Read(d.RootElement, tpl, 1, chs);
        Assert.Equal(new[] { StepType.Exchange, StepType.Heat, StepType.End }, m.Steps.Select(s => s.Type));
        Assert.Equal(20, m.Steps[1].N("wait"));                       // 转换后和模板对得上：共用参数、每路参数都接着用
        Assert.Equal(90, m.PerValue(1, "temp", 1));
        Assert.Equal(new[] { 1, 2 }, m.Include);
    }

    // ———————————————————— HMI v0.8 的 schema ————————————————————

    [Fact]
    public void HmiSchema_VisibleWhenAndDefaultCode_FromTheDto()
    {
        static FieldSchemaDto Sel(string k, string def, params string[] codes) =>
            new(k, k, "sel", null, null, null, null, null, null, codes.Select(c => new SchemaOption(c, c)).ToArray(), true, null, null, null, null, null, def);
        var dto = new ProgramSchemaDto("0.8", 1, new[] { "press" }, new[]
        {
            new StepSchemaDto("press", "充压", "", "", new[]
            {
                Sel("gas", "B", "inert", "A", "B"),
                Sel("regulate", "on", "on", "off"),
                new FieldSchemaDto("refill", "补气阈值", "num", "%", 3, 1, 10, 1, 10, null, true, null, null, null, null, new VisibleWhenDto("regulate", new[] { "on" })),
                new FieldSchemaDto("maxSec", "最长充气", "num", "秒", 180, 30, 600, 30, 600, null, true, null, null, null, null, new VisibleWhenDto("gas", new[] { "A" })),
            }),
        });
        var s = ProgramSchema.FromDto(dto);
        var st = PcStep.New(StepType.Press, s);
        Assert.Equal("B", st.S("gas"));                                // HMI 的 defaultCode
        Assert.True(s.Field(StepType.Press, "refill")!.Visible(st));
        Assert.False(s.Field(StepType.Press, "maxSec")!.Visible(st));  // 按 HMI 给的 visibleWhen（不是本机的表）
        st.Sel["gas"] = "A";
        Assert.True(s.Field(StepType.Press, "maxSec")!.Visible(st));
    }

    // ———————————————————— 先充压后升温：和 HMI 同一个结论 ————————————————————

    [Fact]
    public void PressBeforeHeat_SameVerdictAsHmi()
    {
        // 下一次换气（再充压）之后的升温不算在前一次充压上
        var p = new PcProgram();
        p.Steps.AddRange(new[] { ProgramTemplates.Press("A", 2.0), ProgramTemplates.Press("A", 2.2), ProgramTemplates.Heat(80), ProgramTemplates.Vent(), ProgramTemplates.End() });
        var c = Assert.Single(ProgramChecks.Of(p.Steps, Schema));
        Assert.Equal(1, c.I);
        // 上限按 HMI 的 psi 上限比（3.50 × 0.9 − 0.1 = 456.77 psi），不按界面取整后的 3.14 MPa：3.145 MPa 不拒绝
        double est = ProgramChecks.PressAfterHeat(2.64, 25, 80);
        Assert.InRange(Units.MpaToPsi(est), 455.5, 456.7);
        var near = new PcProgram();
        near.Steps.AddRange(new[] { ProgramTemplates.Press("A", 2.64), ProgramTemplates.Heat(80), ProgramTemplates.Vent(), ProgramTemplates.End() });
        Assert.Equal(CheckLevel.Warn, Assert.Single(ProgramChecks.Of(near.Steps, Schema)).Level);
    }

    // ———————————————————— 反应的结束原因 ————————————————————

    [Fact]
    public void EndReason_ShownOnTheFinishedProgram()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Local);
        var steps = Display(ProgramTemplates.LeakOnly("T2", "PR8"));
        var rec = new ProgramRecord { Name = "x", Steps = steps, StepIndex = 1, Status = ProgramStatus.Done, StartedAt = now.AddHours(-1), EndedAt = now, EndReason = "吸气停止（30 min < 0.005 mmol/min）" };
        var pr = Progress.Of(rec, new ProgressContext(now, Model: Pr8))!;
        var line = ProgramPanelText.Status(rec, pr, true, "", now)!;
        Assert.EndsWith("· 反应结束：吸气停止（30 min < 0.005 mmol/min）", line.Full);
        var none = ProgramPanelText.Status(rec with { EndReason = null }, pr, true, "", now)!;
        Assert.DoesNotContain("反应结束", none.Full);
    }
}

/// <summary>压力单位是全局的（PressureDisplay）：改单位的测试放进不并行的集合（同 PressureUnitTests）。</summary>
[Collection(PressureUnitCollection.Name)]
public sealed class StepsWireUnitTests
{
    private static ProgramSchema Schema => ProgramSchema.Default(new SchemaLimits(200, 220, 450, 4.14 * 145.0377, 3.5 * 145.0377));

    /// <summary>压力 MPa ↔ psi 来回（下发 psi 1 位小数）：MPa 2 位的值、psi 整数的值、bar 1 位的值都原样回来。</summary>
    [Theory]
    [InlineData(PressureUnit.MPa, 2.0)]
    [InlineData(PressureUnit.MPa, 0.02)]
    [InlineData(PressureUnit.MPa, 3.14)]
    [InlineData(PressureUnit.Psi, 290.0)]
    [InlineData(PressureUnit.Psi, 3.0)]
    [InlineData(PressureUnit.Bar, 20.0)]
    [InlineData(PressureUnit.Bar, 0.2)]
    public void WireRoundTrip_PressurePrecision(PressureUnit u, double shown)
    {
        PressureDisplay.Set(u);
        try
        {
            foreach (var (t, k) in new[] { (StepType.Leak, "pres"), (StepType.Exchange, "release"), (StepType.Press, "pres"), (StepType.Hold, "pres"), (StepType.Vent, "to") })
            {
                var st = PcStep.New(t, Schema);
                st.Num[k] = PressureUnits.ToMpa(shown, u);
                var p = new PcProgram { Name = "x" };
                p.Steps.Add(st);
                p.Steps.Add(PcStep.New(StepType.End, Schema));
                var back = PcProgram.FromDoc(ParallelReactor.Client.ProgramDoc.FromJson(p.ToDoc().ToJson(), "x"), "PR8", "x");
                Assert.Equal(shown, PressureUnits.FromMpa(back.Steps[0].N(k), u), 9);
                Assert.Equal(Math.Round(Units.MpaToPsi(st.N(k)), 1), p.ToDoc().Steps[0].Params[k].GetDouble());
            }
        }
        finally { PressureDisplay.Set(PressureUnit.MPa); }
    }

    /// <summary>跨字段（端到端核对发现：HMI 会拒绝，PC 以前不查）：置换「放到」须比「充到」低 1 psi 以上；反应「最短时间」不超过「最长时间」。</summary>
    [Fact]
    public void Checks_CrossField_SameVerdictAsHmi()
    {
        var ex = PcStep.New(StepType.Exchange, Schema);
        ex.Num["fill"] = 0.5; ex.Num["release"] = 0.5;
        var re = PcStep.New(StepType.React, Schema);
        re.Num["time"] = 60; re.Num["minTime"] = 90;
        var steps = new[] { ex, re, PcStep.New(StepType.End, Schema) };
        var c = ProgramChecks.Of(steps, Schema);
        Assert.Contains(c, x => x.Code == "release" && x.Level == CheckLevel.Reject && x.I == 0);
        Assert.Contains(c, x => x.Code == "mintime" && x.Level == CheckLevel.Reject && x.I == 1);
        ex.Num["release"] = 0.02; re.Num["minTime"] = 30;
        c = ProgramChecks.Of(steps, Schema);
        Assert.DoesNotContain(c, x => x.Code is "release" or "mintime");
    }
}
