using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C4 + v0.8：程序参数与全局限值（TMaxSp / OverTemp / RpmMax / PMaxSp / OverPressure）的校验、字段声明范围、跨字段规则。</summary>
public class ProgramLimitsTests
{
    private static readonly ProgramLimitSettings Def = new(TMaxSp: 200, OverTemp: 250, RpmMax: 1000, PMaxSp: 500, OverPressure: 510);

    private static StepSnapshot Heat(double t) => StepSnapshot.Create(StepType.Heat, 1, "升温", "",
        new Dictionary<string, object> { ["temp"] = t, ["wait"] = 15.0 });

    private static StepSnapshot Curve(params (double, double, double)[] segs) => StepSnapshot.Create(StepType.Heat, 1, "升温", "",
        new Dictionary<string, object> { ["mode"] = "curve" }, segs);

    private static StepSnapshot Stir(double rpm) => StepSnapshot.Create(StepType.Stir, 1, "搅拌", "",
        new Dictionary<string, object> { ["rpm"] = rpm });

    private static StepSnapshot Press(double p) => StepSnapshot.Create(StepType.Press, 1, "升压", "",
        new Dictionary<string, object> { ["pres"] = p, ["gas"] = "A" });

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
        Assert.NotNull(ProgramLimits.CheckStep(Press(7.2), Def));   // 声明下限 7.3 psi（0.05 MPa）
        Assert.Null(ProgramLimits.CheckStep(Press(7.3), Def));
        // OverPressure 100 → 上限 90（不含）
        var lowOp = Def with { OverPressure = 100 };
        Assert.NotNull(ProgramLimits.CheckStep(Press(90), lowOp));
        Assert.Null(ProgramLimits.CheckStep(Press(89.9), lowOp));
        // PMaxSp 40 → 50 超过 SP 上限
        Assert.Contains("SP 上限", ProgramLimits.CheckStep(Press(50), Def with { PMaxSp = 40 }));
        // 选项必须是码
        var bad = StepSnapshot.Create(StepType.Press, 1, "充压", "", new Dictionary<string, object> { ["pres"] = 50.0, ["gas"] = "N2" });
        Assert.Contains("不是可选的值", ProgramLimits.CheckStep(bad, Def));
    }

    [Fact]
    public void LegacyTypes_Rejected()
    {
        var w = StepSnapshot.Create(StepType.Wait, 1, "等待", "", new Dictionary<string, object> { ["time"] = 30.0 });
        Assert.Contains("旧格式", ProgramLimits.CheckStep(w, Def));
    }

    [Fact]
    public void DeclaredRanges_AndCrossFieldRules()
    {
        StepSnapshot S(StepType t, params (string K, object V)[] kv)
        {
            var p = StepSpecs.Defaults(t);
            foreach (var (k, v) in kv) p[k] = v;
            return StepSnapshot.Create(t, 1, t.Zh(), "", p);
        }
        foreach (var t in StepTypeCodes.All) Assert.Null(ProgramLimits.CheckStep(S(t), Def with { RpmMax = 450 }));   // 默认值都合法
        Assert.NotNull(ProgramLimits.CheckStep(S(StepType.Exchange, ("cycles", 21.0)), Def));
        Assert.NotNull(ProgramLimits.CheckStep(S(StepType.Exchange, ("cycles", 2.5)), Def));   // 整数
        Assert.Contains("低 1 psi", ProgramLimits.CheckStep(S(StepType.Exchange, ("fill", 20.0), ("release", 19.5)), Def));
        Assert.Null(ProgramLimits.CheckStep(S(StepType.Exchange, ("fill", 20.0), ("release", 19.0)), Def));
        Assert.Contains("最短时间", ProgramLimits.CheckStep(S(StepType.React, ("time", 20.0), ("minTime", 30.0)), Def));
        Assert.NotNull(ProgramLimits.CheckStep(S(StepType.Leak, ("pres", 14.0)), Def));   // 下限 14.5
        Assert.NotNull(ProgramLimits.CheckStep(S(StepType.Vent, ("to", 80.0)), Def));     // 0–72.5
        // 不起作用的字段（visibleWhen 不成立）不校验：保持按时间时 temp 越界不拦
        Assert.Null(ProgramLimits.CheckStep(S(StepType.Hold, ("until", "time"), ("temp", 999.0)), Def));
        Assert.Contains("超温", ProgramLimits.CheckStep(S(StepType.Hold, ("until", "tempBelow"), ("temp", 260.0)), Def with { TMaxSp = 300 }));
        // 保持的目标温度不是加热给定值：0 ℃ 合法
        Assert.Null(ProgramLimits.CheckStep(S(StepType.Hold, ("until", "tempBelow"), ("temp", 0.0)), Def));
        // 稳压关时补气阈值不校验
        Assert.Null(ProgramLimits.CheckStep(S(StepType.Press, ("regulate", "off"), ("refill", 99.0)), Def));
        Assert.NotNull(ProgramLimits.CheckStep(S(StepType.Press, ("regulate", "on"), ("refill", 99.0)), Def));
    }

    [Fact]
    public void FieldRange_IntersectsLimits()
    {
        // 充压：声明 7.3–493.1，PMaxSp 500，OP 510×0.9−0.1=458.9 → 458.9
        var (plo, phi) = ProgramLimits.FieldRange(StepType.Press, "pres", 7.3, 493.1, Def);
        Assert.Equal(7.3, plo);
        Assert.Equal(458.9, phi, 6);
        // OP 100 → 89.9
        var (_, pmax) = ProgramLimits.FieldRange(StepType.Press, "pres", 7.3, 493.1, Def with { OverPressure = 100 });
        Assert.Equal(89.9, pmax, 6);
        // PMaxSp 60 → 60；其他「上限」类压力字段同一规则
        Assert.Equal(60.0, ProgramLimits.FieldRange(StepType.Press, "pres", 7.3, 493.1, Def with { PMaxSp = 60 }).Max);
        Assert.Equal(60.0, ProgramLimits.FieldRange(StepType.Leak, "pres", 14.5, 493.1, Def with { PMaxSp = 60 }).Max);
        Assert.Equal(60.0, ProgramLimits.FieldRange(StepType.Exchange, "fill", 7.3, 493.1, Def with { PMaxSp = 60 }).Max);
        Assert.Equal(60.0, ProgramLimits.FieldRange(StepType.Vent, "flushPres", 7.3, 493.1, Def with { PMaxSp = 60 }).Max);
        // 放到不受上限约束（0–72.5）
        Assert.Equal(72.5, ProgramLimits.FieldRange(StepType.Vent, "to", 0, 72.5, Def with { PMaxSp = 60 }).Max);
        // 升温：0–300 → 35–min(200, 249)
        Assert.Equal((35.0, 200.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def));
        Assert.Equal((35.0, 249.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def with { TMaxSp = 300 }));
        // 保持的温度：上限同，下限不收紧
        Assert.Equal((0.0, 200.0), ProgramLimits.FieldRange(StepType.Hold, "temp", 0, 300, Def));
        // 搅拌：0–3000 → 0–RpmMax
        Assert.Equal((0.0, 1000.0), ProgramLimits.FieldRange(StepType.Stir, "rpm", 0, 3000, Def));
        // 不相关字段不变
        Assert.Equal((0.0, 120.0), ProgramLimits.FieldRange(StepType.Heat, "wait", 0, 120, Def));
        // 交集为空：TMaxSp 低于 35 → (35, 35)
        Assert.Equal((35.0, 35.0), ProgramLimits.FieldRange(StepType.Heat, "temp", 0, 300, Def with { TMaxSp = 30 }));
        // 跨字段：放到 ≤ 充到 − 1，最短 ≤ 最长
        Assert.Equal(71.5, ProgramLimits.FieldRange(StepType.Exchange, "release", 0, 493.1, Def, k => k == "fill" ? 72.5 : 0).Max);
        Assert.Equal(20.0, ProgramLimits.FieldRange(StepType.React, "minTime", 0, 4320, Def, k => k == "time" ? 20 : 0).Max);
    }

    [Fact]
    public void ClampField_ClampsDefaults()
    {
        Assert.Equal(1000, ProgramLimits.ClampField(StepType.Stir, "rpm", 1500, 0, 3000, Def));
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
