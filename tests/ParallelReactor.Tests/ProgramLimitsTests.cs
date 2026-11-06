using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C4：程序参数与全局限值（TMaxSp / OverTemp / RpmMax / PMaxSp / OverPressure）的校验。</summary>
public class ProgramLimitsTests
{
    private static readonly ProgramLimitSettings Def = new(TMaxSp: 200, OverTemp: 250, RpmMax: 1000, PMaxSp: 500, OverPressure: 510);

    private static StepSnapshot Heat(double t) => StepSnapshot.Create(StepType.Heat, 1, "升温", "",
        new Dictionary<string, object> { ["temp"] = t, ["wait"] = 15.0 });

    private static StepSnapshot Curve(params (double, double, double)[] segs) => StepSnapshot.Create(StepType.Heat, 1, "升温", "",
        new Dictionary<string, object> { ["mode"] = "curve" }, segs);

    private static StepSnapshot Stir(double rpm) => StepSnapshot.Create(StepType.Stir, 1, "搅拌", "",
        new Dictionary<string, object> { ["rpm"] = rpm, ["dur"] = 5.0 });

    private static StepSnapshot Press(double p) => StepSnapshot.Create(StepType.Press, 1, "升压", "",
        new Dictionary<string, object> { ["pres"] = p, ["gas"] = "气体 A" });

    [Fact]
    public void HeatFixed_Bounds()
    {
        Assert.NotNull(ProgramLimits.CheckStep(Heat(300), Def));   // > TMaxSp
        Assert.Null(ProgramLimits.CheckStep(Heat(200), Def));      // = TMaxSp
        Assert.Null(ProgramLimits.CheckStep(Heat(35), Def));       // 下限
        Assert.NotNull(ProgramLimits.CheckStep(Heat(30), Def));    // < 35
        Assert.NotNull(ProgramLimits.CheckStep(Heat(double.NaN), Def));
    }

    [Fact]
    public void HeatFixed_MustBeBelowOverTemp()
    {
        var lim = Def with { TMaxSp = 300, OverTemp = 250 };
        Assert.NotNull(ProgramLimits.CheckStep(Heat(250), lim));   // = OverTemp 拒绝
        Assert.Null(ProgramLimits.CheckStep(Heat(249.5), lim));
        Assert.Contains("超温", ProgramLimits.CheckStep(Heat(260), lim));
    }

    [Fact]
    public void HeatCurve_EmptyOrOverLimitRejected()
    {
        Assert.NotNull(ProgramLimits.CheckStep(Curve(), Def));
        Assert.Null(ProgramLimits.CheckStep(Curve((80, 5, 10), (150, 2, 30)), Def));
        var r = ProgramLimits.CheckStep(Curve((80, 5, 10), (220, 2, 30)), Def);
        Assert.NotNull(r);
        Assert.Contains("第 2 段", r);
        Assert.NotNull(ProgramLimits.CheckStep(Curve((80, 60, 10)), Def));   // 斜率 > 50
        Assert.NotNull(ProgramLimits.CheckStep(Curve((80, 5, -1)), Def));    // 保温 < 0
    }

    [Fact]
    public void Stir_Bounds()
    {
        Assert.NotNull(ProgramLimits.CheckStep(Stir(2000), Def));
        Assert.Null(ProgramLimits.CheckStep(Stir(1000), Def));
        Assert.Null(ProgramLimits.CheckStep(Stir(0), Def));
        Assert.NotNull(ProgramLimits.CheckStep(Stir(-1), Def));
    }

    [Fact]
    public void Press_Bounds()
    {
        Assert.Null(ProgramLimits.CheckStep(Press(50), Def));
        Assert.NotNull(ProgramLimits.CheckStep(Press(0), Def));
        Assert.NotNull(ProgramLimits.CheckStep(Press(0.5), Def));
        Assert.Null(ProgramLimits.CheckStep(Press(ProgramLimits.PressTargetMin), Def));
        // OverPressure 100 → 上限 90（不含）
        var lowOp = Def with { OverPressure = 100 };
        Assert.NotNull(ProgramLimits.CheckStep(Press(90), lowOp));
        Assert.Null(ProgramLimits.CheckStep(Press(89.9), lowOp));
        // PMaxSp 40 → 50 超过 SP 上限
        Assert.Contains("SP 上限", ProgramLimits.CheckStep(Press(50), Def with { PMaxSp = 40 }));
    }

    [Fact]
    public void OtherSteps_NotChecked()
    {
        var w = StepSnapshot.Create(StepType.Wait, 1, "等待", "", new Dictionary<string, object> { ["time"] = 30.0 });
        Assert.Null(ProgramLimits.CheckStep(w, Def));
    }

    [Fact]
    public void FieldRange_IntersectsLimits()
    {
        // 升压：FieldDef 0–200，PMaxSp 500，OP 510×0.9−0.1=458.9 → 200
        Assert.Equal((1.0, 200.0), ProgramLimits.FieldRange(StepType.Press, "pres", 0, 200, Def));   // 下限与 CheckStep 一致
        // OP 100 → 89.9
        var (_, pmax) = ProgramLimits.FieldRange(StepType.Press, "pres", 0, 200, Def with { OverPressure = 100 });
        Assert.Equal(89.9, pmax, 6);
        // PMaxSp 60 → 60
        Assert.Equal(60.0, ProgramLimits.FieldRange(StepType.Press, "pres", 0, 200, Def with { PMaxSp = 60 }).Max);
        // 升温：0–300 → 35–min(200, 249)
        Assert.Equal((35.0, 200.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def));
        Assert.Equal((35.0, 249.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def with { TMaxSp = 300 }));
        // 搅拌：0–2000 → 0–1000
        Assert.Equal((0.0, 1000.0), ProgramLimits.FieldRange(StepType.Stir, "rpm", 0, 2000, Def));
        // 结束降温：封顶 TMaxSp
        Assert.Equal((0.0, 200.0), ProgramLimits.FieldRange(StepType.End, "cool", 0, 300, Def));
        // 不相关字段不变
        Assert.Equal((0.0, 120.0), ProgramLimits.FieldRange(StepType.Heat, "wait", 0, 120, Def));
        // 交集为空：TMaxSp 低于 35 → (35, 35)
        Assert.Equal((35.0, 35.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def with { TMaxSp = 30 }));
    }

    [Fact]
    public void ClampField_ClampsDefaults()
    {
        Assert.Equal(1000, ProgramLimits.ClampField(StepType.Stir, "rpm", 1500, 0, 2000, Def));
        Assert.Equal(150, ProgramLimits.ClampField(StepType.Heat, "temp", 150, 0, 300, Def));
        Assert.Equal(100, ProgramLimits.ClampField(StepType.Heat, "temp", 150, 0, 300, Def with { TMaxSp = 100 }));
    }

    [Fact]
    public void Validate_MessageIncludesStepIndex()
    {
        var errs = ProgramLimits.Validate(new[] { Stir(500), Heat(300), Press(50) }, Def);
        Assert.Single(errs);
        Assert.StartsWith("第 02 步 升温：", errs[0]);
        Assert.Contains("300℃", errs[0]);
        Assert.Contains("200℃", errs[0]);
        Assert.Empty(ProgramLimits.Validate(new[] { Stir(500), Heat(150) }, Def));
    }
}
