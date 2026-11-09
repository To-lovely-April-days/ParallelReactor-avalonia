using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.StepText（样稿 PR.sim.stepName / stepText / condText、ui.stepSections）：每个型号的程序库（含 stress 的最坏值程序）逐步对照样稿 JS
/// （DemoMockVectors 的 steps 表），外加关键文字的直接检查；ProgramDocs：HMI 程序（psi）→ 样稿步骤（MPa），沿用旧 PlanEditorVm 的参数与默认值。
/// </summary>
public class StepTextTests
{
    private static readonly ModelProfile Pr84 = ModelProfiles.Embedded.Get("PR8-4")!;
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;
    private static readonly ModelProfile Pr4 = ModelProfiles.Embedded.Get("PR4")!;
    private static readonly ModelProfile Pr16 = ModelProfiles.Embedded.Get("PR16")!;

    [Fact]
    public void ProgramLibraries_MatchMock()
    {
        using var doc = JsonDocument.Parse(DemoMockVectors.Json);
        var expected = doc.RootElement.GetProperty("steps").EnumerateArray().Select(x => x.GetString()!).ToList();
        var actual = new List<string>();
        foreach (var stress in new[] { "", "&stress=1" })
        {
            var f = new DemoFleet(DemoParams.Parse("devs=PR8-4,PR4,PR16,PR8&freeze=1" + stress));
            foreach (var d in f.Devices) DemoDump.Steps(d, actual);
        }
        Assert.True(expected.Count > 80);
        DemoFleetTests.AssertLines(expected, actual, "步骤文字");
    }

    private static ProgramStep LibStep(ModelProfile m, string id, int i) => DemoFleet.ProgramLibrary(m).First(p => p.Id == id).Steps[i];

    [Fact]
    public void KeyTexts()
    {
        var press = LibStep(Pr84, "h2-std", 1);
        Assert.Equal("0.350 MPa · 气体 A", StepText.Summary(Pr84, press));
        Assert.Equal(new StepCond("结束条件", "压力 ≥ 0.350 MPa"), StepText.Cond(press, Pr84));
        var react = LibStep(Pr84, "h2-std", 2);
        Assert.Equal("1 h 12 min", StepText.Summary(Pr84, react));
        Assert.Equal(new StepCond("步骤时间", "> 72 min"), StepText.Cond(react));
        Assert.Equal("步骤时间 > 72 min", StepText.Cond(react).ToString());
        var heat = LibStep(Pr84, "h2-std", 0);
        Assert.Equal("120 ℃", StepText.Summary(Pr84, heat));
        Assert.Equal("温度 ≥ 119.5 ℃", StepText.Cond(heat).Value);
        Assert.Equal("结束", StepText.Name(LibStep(Pr84, "h2-std", 3)));
        Assert.Equal("泄压 · 降温", StepText.Summary(Pr84, LibStep(Pr84, "h2-std", 3)));
        Assert.Equal("惰性气体 × 3", StepText.Summary(Pr84, LibStep(Pr84, "purge-3", 0)));
        Assert.Equal("保持密封", StepText.Summary(Pr84, LibStep(Pr84, "purge-3", 2)));
        Assert.Equal(new StepCond("步骤时间", "> 0 min"), StepText.Cond(LibStep(Pr84, "purge-3", 2)));
        Assert.Equal(new StepCond("结束条件", "立即"), StepText.Cond(new ProgramStep(StepKind.Wait)));
        Assert.Equal(Fmt.Dash, StepText.Name(null));
    }

    [Fact]
    public void Sections_FollowModel()
    {
        var shared = StepText.Sections(Pr84, 1, LibStep(Pr84, "h2-std", 1));
        Assert.Equal(new[] { "加热器", "进气阀 SV1", "搅拌电机（全局）" }, shared.Select(s => s.Dev));
        Assert.Equal("搅拌电机（全局 RV1–RV8 共用）", shared[2].DevTip);
        Assert.Equal(("保持", "设定值", "120.0 ℃"), (shared[0].Mode, shared[0].Label, shared[0].Value));
        Assert.Equal(("气体 A · 到压后保持", "目标压力", "0.350 MPa"), (shared[1].Mode, shared[1].Label, shared[1].Value));
        Assert.Equal(("共用转速", "300 rpm"), (shared[2].Mode, shared[2].Value));

        var pr4 = StepText.Sections(Pr4, 2, LibStep(Pr4, "h2-hp", 1));
        Assert.Equal("搅拌电机（本釜）", pr4[2].Dev);
        Assert.Equal("本釜转速", pr4[2].Mode);
        Assert.Equal("5.20 MPa", pr4[1].Value);                 // HT-PR4 量程 10 MPa：2 位小数
        Assert.Equal("H₂ · 到压后保持", pr4[1].Mode);
        Assert.Equal("5.20 MPa · H₂", StepText.Summary(Pr4, LibStep(Pr4, "h2-hp", 1)));

        var cool = StepText.Sections(Pr4, 3, LibStep(Pr4, "h2-hp", 3));
        Assert.Equal(("关闭 · 冷却水降温", "降至", "40.0 ℃"), (cool[0].Mode, cool[0].Label, cool[0].Value));
        var end = StepText.Sections(Pr4, 3, LibStep(Pr4, "h2-hp", 4));
        Assert.Equal(("排空阀", "打开 · 泄压", "0.00 MPa"), (end[1].Dev, end[1].Mode, end[1].Value));
        var end8 = StepText.Sections(Pr8, null, LibStep(Pr8, "cat-100", 3));
        Assert.Equal(("排空阀", "0.000 MPa"), (end8[1].Dev, end8[1].Value));   // 「排空 / 泄压」取第一个词
        var none = StepText.Sections(Pr16, null, LibStep(Pr16, "purge-3", 1));
        Assert.Equal(("进气阀 SV", "关闭", "—"), (none[1].Dev, none[1].Mode, none[1].Value));
        Assert.Equal(("不加热", "—"), (none[0].Mode, none[0].Value));
    }

    [Fact]
    public void KindNamesAndCodes()
    {
        var names = new Dictionary<StepKind, (string Code, string Zh, string Icon)>
        {
            [StepKind.Purge] = ("purge", "吹扫", "wind"), [StepKind.Heat] = ("heat", "升温", "thermo"), [StepKind.Press] = ("press", "充压", "gauge"),
            [StepKind.Stir] = ("stir", "搅拌", "fan"), [StepKind.Wait] = ("wait", "等待", "clock"), [StepKind.React] = ("react", "反应", "flask"),
            [StepKind.Cool] = ("cool", "降温", "snow"), [StepKind.End] = ("end", "结束", "flag"),
        };
        foreach (var (k, v) in names)
        {
            Assert.Equal(v, (StepText.Code(k), StepText.KindName(k), StepText.Icon(k)));
            Assert.True(StepText.TryParse(v.Code, out var back) && back == k);
        }
        Assert.False(StepText.TryParse("nope", out _));
        // 与 HMI 的步骤中文名一致（StepTypeCodes.Zh，「充压」而不是 schema 的「升压」）
        foreach (var t in StepTypeCodes.All)
            Assert.Equal(t.Zh(), StepText.KindName(names.First(kv => kv.Value.Code == t.ToCode()).Key));
    }

    [Fact]
    public void PressureUnit_DefaultsToMpa()
    {
        Assert.Equal(PressureUnit.MPa, PressureDisplay.Primary);
        Assert.Equal("MPa", PressureDisplay.Unit);
        Assert.Equal("psi", PressureDisplay.SecondaryUnit);
        Assert.Equal("0.350 MPa", PressureDisplay.WithUnit(Pr84, 0.35));
        Assert.Equal("5.20 MPa", PressureDisplay.WithUnit(Pr4, 5.2));
        Assert.Equal("50.8 psi", PressureDisplay.SecondaryWithUnit(Pr84, 0.35));
        Assert.Equal("50.8 psi", PressureDisplay.FormatU(PressureUnit.Psi, Pr84, 0.35));
        Assert.Equal(Fmt.Dash, PressureDisplay.WithUnit(Pr84, null));
    }

    // ———————————————————— HMI 程序 → 步骤 ————————————————————

    private static ProgramDoc Doc()
    {
        var doc = new ProgramDoc { Name = "加氢测试-01" };
        doc.Add(StepType.Purge, new { gas = "inert", cycles = 10, fill = 3, vent = 5 });
        doc.Add(StepType.Heat, new { mode = "fixed", temp = 150, wait = 15 });
        doc.Add(StepType.Press, new { gas = "A", pres = 50, regulate = "off" });
        doc.Add(StepType.Stir, new { rpm = 600, dur = 5 });
        doc.Add(StepType.React, new { endBy = "time", time = 120 });
        doc.Add(StepType.Wait, new { mode = "fixed", time = 30 });
        doc.Add(StepType.End, new { mode = "vent", cool = 40 });
        return doc;
    }

    [Fact]
    public void ProgramDocs_ConvertWithPlanEditorDefaults()
    {
        var steps = ProgramDocs.ToSteps(Doc(), Pr8);
        Assert.Equal(new[] { StepKind.Purge, StepKind.Heat, StepKind.Press, StepKind.Stir, StepKind.React, StepKind.Wait, StepKind.End }, steps.Select(s => s.Type));

        var purge = steps[0];
        Assert.Equal(("inert", 10, 1.5), (purge.Gas, purge.Cycles, purge.End!.V));   // 10 × (3 + 5 + 1) / 60
        Assert.Null(purge.Sp);                                                       // 还没升温：不加热
        Assert.Equal("惰性气体 × 10", StepText.Summary(Pr8, purge));

        var heat = steps[1];
        Assert.Equal(new StepCond("步骤时间", "> 15 min"), StepText.Cond(heat, Pr8));   // 等待平衡
        Assert.Equal("固定升温", StepText.Sections(Pr8, 1, heat)[0].Mode);

        var press = steps[2];
        Assert.Equal("a", press.Gas);                                                  // HMI 代码 A → 型号表的键
        Assert.Equal(150, press.Sp);                                                   // 保持上一步的温度
        Assert.Equal("0.345 MPa · 气体 A", StepText.Summary(Pr8, press));             // 50 psi
        Assert.Equal("压力 ≥ 0.345 MPa", StepText.Cond(press, Pr8).Value);
        Assert.Equal("气体 A · 目标后保持", StepText.Sections(Pr8, 1, press)[1].Mode);   // regulate = off
        Assert.Equal(Fmt.Dash, StepText.Sections(Pr8, 1, press)[2].Value);             // 真机程序只有搅拌步骤带转速

        Assert.Equal("600 rpm", StepText.Sections(Pr8, 1, steps[3])[2].Value);
        var react = steps[4];
        Assert.Equal(("a", 120.0), (react.Gas, react.Minutes!.Value));
        Assert.Equal(press.Target, react.Target);                                      // 补压到充压的目标
        Assert.Equal("补压保持 · 气体 A", StepText.Sections(Pr8, 1, react)[1].Mode);
        Assert.Equal("步骤时间 > 120 min", StepText.Cond(react).ToString());
        Assert.Equal("30 min", StepText.Summary(Pr8, steps[5]));
        var end = steps[6];
        Assert.Equal("泄压 · 降温", StepText.Summary(Pr8, end));
        Assert.Equal(new StepCond("结束条件", "温度 < 40 ℃"), StepText.Cond(end, Pr8));
        Assert.Equal(("排空阀", "打开 · 泄压"), (StepText.Sections(Pr8, 1, end)[1].Dev, StepText.Sections(Pr8, 1, end)[1].Mode));

        var def = ProgramDocs.ToDef(Doc(), Pr8, "local-1");
        Assert.Equal(("local-1", "加氢测试-01", 7), (def.Id, def.Name, def.Steps.Count));
    }

    [Fact]
    public void ProgramDocs_RoundTripsThroughJson_AndDefaults()
    {
        var doc = ProgramDoc.FromJson("{\"steps\":[{\"type\":\"heat\",\"params\":{\"mode\":\"curve\"}},{\"type\":\"press\",\"params\":{}},{\"type\":\"end\",\"params\":{}},{\"type\":\"nope\",\"params\":{}}]}");
        var steps = ProgramDocs.ToSteps(doc, Pr8);
        Assert.Equal(3, steps.Count);                                    // 不认识的类型跳过
        Assert.Equal(150, steps[0].Sp);                                  // 默认 150 ℃
        Assert.Equal("曲线升温", StepText.Sections(Pr8, 1, steps[0])[0].Mode);
        Assert.Equal("0.345 MPa · 气体 A", StepText.Summary(Pr8, steps[1]));   // 默认 50 psi、气体 A
        Assert.Equal("气体 A · 稳压保持", StepText.Sections(Pr8, 1, steps[1])[1].Mode);   // 默认 regulate = on
        Assert.Equal("保持密封", StepText.Summary(Pr8, steps[2]));       // 默认 seal
        Assert.Equal("温度 < 40 ℃", StepText.Cond(steps[2], Pr8).Value);
    }
}
