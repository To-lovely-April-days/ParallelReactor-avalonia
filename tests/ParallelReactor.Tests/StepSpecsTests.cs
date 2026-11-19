using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>v0.8 字段表（STEPS-SPEC §1）、显示条件、时长估算（§5）、下发前检查（§4）、默认模板。</summary>
public class StepSpecsTests
{
    private static readonly ProgramLimitSettings Def = new(200, 250, 450, 500, 510);

    private static StepSnapshot S(StepType t, params (string K, object V)[] kv)
    {
        var p = StepSpecs.Defaults(t);
        foreach (var (k, v) in kv) p[k] = v;
        return StepSnapshot.Create(t, 1, t.Zh(), "", p);
    }

    [Fact]
    public void Registry_HasExactlyTheNineTypes_NoCool()
    {
        Assert.Equal(StepTypeCodes.All.OrderBy(t => t), StepSpecs.Registry.Keys.OrderBy(t => t));
        Assert.Equal(9, StepTypeCodes.All.Length);
        Assert.False(StepSpecs.Registry.ContainsKey(StepType.Purge));
        Assert.False(StepSpecs.Registry.ContainsKey(StepType.Wait));
        Assert.Empty(StepSpecs.Of(StepType.End).Fields);
    }

    [Theory]
    [InlineData(StepType.Leak, "pres", 435.1, 14.5)]
    [InlineData(StepType.Leak, "settle", 5, 0)]
    [InlineData(StepType.Leak, "hold", 30, 5)]
    [InlineData(StepType.Leak, "maxDrop", 2, 0.1)]
    [InlineData(StepType.Exchange, "fill", 72.5, 7.3)]
    [InlineData(StepType.Exchange, "release", 2.9, 0)]
    [InlineData(StepType.Exchange, "cycles", 3, 0)]
    [InlineData(StepType.Exchange, "lineFlush", 2, 0)]
    [InlineData(StepType.Exchange, "maxSec", 60, 10)]
    [InlineData(StepType.Stir, "rpm", 400, 0)]
    [InlineData(StepType.Heat, "temp", 80, 35)]
    [InlineData(StepType.Heat, "rate", 0, 0)]
    [InlineData(StepType.Heat, "band", 2, 0.5)]
    [InlineData(StepType.Heat, "wait", 15, 0)]
    [InlineData(StepType.Heat, "maxWait", 120, 10)]
    [InlineData(StepType.Press, "pres", 290.1, 7.3)]
    [InlineData(StepType.Press, "tol", 2, 0.5)]
    [InlineData(StepType.Press, "refill", 3, 1)]
    [InlineData(StepType.Press, "maxSec", 180, 30)]
    [InlineData(StepType.Hold, "time", 10, 0)]
    [InlineData(StepType.Hold, "temp", 40, 0)]
    [InlineData(StepType.Hold, "pres", 14.5, 0)]
    [InlineData(StepType.Hold, "maxWait", 60, 1)]
    [InlineData(StepType.React, "time", 480, 1)]
    [InlineData(StepType.React, "minTime", 30, 0)]
    [InlineData(StepType.React, "mmol", 5, 0.01)]
    [InlineData(StepType.React, "stallRate", 0.005, 0.001)]
    [InlineData(StepType.React, "stallMin", 30, 5)]
    [InlineData(StepType.Vent, "to", 2.9, 0)]
    [InlineData(StepType.Vent, "maxSec", 120, 10)]
    [InlineData(StepType.Vent, "flush", 3, 0)]
    [InlineData(StepType.Vent, "flushPres", 43.5, 7.3)]
    public void Defaults_AndMinimums_PerSpec(StepType t, string key, double def, double min)
    {
        var f = StepSpecs.Of(t).Field(key)!;
        Assert.Equal(def, f.Default);
        Assert.Equal(min, f.Min);
        Assert.Equal(def, StepSpecs.Defaults(t)[key]);
    }

    [Theory]
    [InlineData(StepType.Leak, "onFail", "stop")]
    [InlineData(StepType.Leak, "after", "vent")]
    [InlineData(StepType.Exchange, "gas", "inert")]
    [InlineData(StepType.Heat, "mode", "fixed")]
    [InlineData(StepType.Heat, "onTimeout", "stop")]
    [InlineData(StepType.Press, "gas", "A")]
    [InlineData(StepType.Press, "regulate", "on")]
    [InlineData(StepType.Hold, "until", "time")]
    [InlineData(StepType.Hold, "onTimeout", "continue")]
    [InlineData(StepType.React, "mmolOn", "off")]
    [InlineData(StepType.React, "stallOn", "on")]
    public void SelDefaults_PerSpec(StepType t, string key, string code) => Assert.Equal(code, StepSpecs.Defaults(t)[key]);

    [Fact]
    public void PressureFields_AreMarked()
    {
        var pressure = StepTypeCodes.All.SelectMany(t => StepSpecs.Of(t).Fields.Where(f => f.Pressure).Select(f => $"{t.ToCode()}.{f.Key}")).ToList();
        Assert.Equal(new[] { "leak.pres", "leak.maxDrop", "exchange.fill", "exchange.release", "press.pres", "hold.pres", "vent.to", "vent.flushPres" }, pressure);
    }

    [Fact]
    public void VisibleWhen()
    {
        var hold = StepSpecs.Of(StepType.Hold);
        var p = StepSpecs.Defaults(StepType.Hold);
        Assert.True(StepSpecs.IsVisible(StepType.Hold, hold.Field("time")!, p));
        Assert.False(StepSpecs.IsVisible(StepType.Hold, hold.Field("temp")!, p));
        Assert.False(StepSpecs.IsVisible(StepType.Hold, hold.Field("maxWait")!, p));
        p["until"] = "tempBelow";
        Assert.True(StepSpecs.IsVisible(StepType.Hold, hold.Field("temp")!, p));
        Assert.False(StepSpecs.IsVisible(StepType.Hold, hold.Field("pres")!, p));
        Assert.True(StepSpecs.IsVisible(StepType.Hold, hold.Field("onTimeout")!, p));
        // 控制字段没写：按默认码
        var react = StepSpecs.Of(StepType.React);
        Assert.False(StepSpecs.IsVisible(StepType.React, react.Field("mmol")!, new Dictionary<string, object>()));
        Assert.True(StepSpecs.IsVisible(StepType.React, react.Field("stallRate")!, new Dictionary<string, object>()));
        var heat = StepSpecs.Of(StepType.Heat);
        Assert.False(StepSpecs.IsVisible(StepType.Heat, heat.Field("temp")!, new Dictionary<string, object> { ["mode"] = "curve" }));
        Assert.True(StepSpecs.IsVisible(StepType.Heat, heat.Field("band")!, new Dictionary<string, object> { ["mode"] = "curve" }));
    }

    [Fact]
    public void Duration_PerSpec5()
    {
        Assert.Equal(36, StepDuration.Of(S(StepType.Leak)).Minutes);                                   // 5 + 30 + 1
        Assert.Equal((2 * 9 + 3 * 20) / 60.0, StepDuration.Of(S(StepType.Exchange)).Minutes, 9);
        Assert.Equal(0, StepDuration.Of(S(StepType.Stir)).Minutes);
        Assert.Equal(55 / 3.5 + 15, StepDuration.Of(S(StepType.Heat)).Minutes, 9);                     // |80−25| / 3.5 + 15
        Assert.Equal(55 / 5.0 + 15, StepDuration.Of(S(StepType.Heat, ("rate", 5.0))).Minutes, 9);
        Assert.Equal(3, StepDuration.Of(S(StepType.Press)).Minutes);
        Assert.Equal(10, StepDuration.Of(S(StepType.Hold)).Minutes);
        var cond = StepDuration.Of(S(StepType.Hold, ("until", "tempBelow"), ("maxWait", 60.0)));
        Assert.Equal(15, cond.Minutes);
        Assert.True(cond.Approx);
        Assert.Equal(5, StepDuration.Of(S(StepType.Hold, ("until", "pressAbove"), ("maxWait", 5.0))).Minutes);
        var react = StepDuration.Of(S(StepType.React));
        Assert.Equal(480, react.Minutes);
        Assert.True(react.UpperBound);
        Assert.Equal(2, StepDuration.Of(S(StepType.Vent)).Minutes);                                    // 1 + 3 × 20 / 60
        Assert.Equal(0, StepDuration.Of(S(StepType.End)).Minutes);
        var curve = StepSnapshot.Create(StepType.Heat, 1, "升温", "", new Dictionary<string, object> { ["mode"] = "curve" }, new[] { (80.0, 5.0, 10.0), (120.0, 2.0, 20.0) });
        Assert.Equal(11 + 10 + 20 + 20, StepDuration.Of(curve).Minutes, 9);
        var total = StepDuration.Total(new[] { S(StepType.Hold, ("until", "tempBelow")), S(StepType.React, ("time", 60.0)) });
        Assert.Equal("约 ≤ 1h 15m", total.Text);
    }

    [Fact]
    public void PreRun_Template_PassesWithoutWarnings()
    {
        var steps = StepTemplates.Hydrogenation().Select((t, i) => StepSnapshot.Create(t.Type, i + 1, t.Type.Zh(), "", t.Params)).ToList();
        var r = ProgramLimits.PreRun(steps, Def);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
        Assert.Equal(new[] { StepType.Leak, StepType.Exchange, StepType.Exchange, StepType.Stir, StepType.Heat, StepType.Press, StepType.React, StepType.Vent, StepType.End },
            steps.Select(s => s.Type));
        Assert.Equal("inert", steps[1].Str("gas"));
        Assert.Equal("A", steps[2].Str("gas"));
        Assert.DoesNotContain(steps, s => s.Type.IsLegacy());
    }

    [Fact]
    public void PreRun_EndRules()
    {
        var noEnd = ProgramLimits.PreRun(new[] { S(StepType.Stir) }, Def);
        Assert.Contains(noEnd.Errors, e => e.Contains("没有「结束」"));
        var two = ProgramLimits.PreRun(new[] { S(StepType.End), S(StepType.End) }, Def);
        Assert.Contains(two.Errors, e => e.Contains("2 个「结束」"));
        var mid = ProgramLimits.PreRun(new[] { S(StepType.End), S(StepType.Stir) }, Def);
        Assert.Contains(mid.Errors, e => e.Contains("必须是最后一步"));
    }

    [Fact]
    public void PreRun_Warnings()
    {
        // 反应前没有充压 + 没有放空
        var r = ProgramLimits.PreRun(new[] { S(StepType.Heat), S(StepType.React), S(StepType.End) }, Def);
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("吸气量不计量"));
        Assert.Contains(r.Warnings, w => w.Contains("结束时釜里可能带压"));
        // 充压之后放空再反应：同样不计量
        r = ProgramLimits.PreRun(new[] { S(StepType.Press), S(StepType.Vent), S(StepType.React), S(StepType.End) }, Def);
        Assert.Contains(r.Warnings, w => w.Contains("吸气量不计量"));
        // 先充压后升温：提示估算值
        r = ProgramLimits.PreRun(new[] { S(StepType.Press, ("pres", 100.0)), S(StepType.Heat, ("temp", 80.0)), S(StepType.Vent), S(StepType.End) }, Def);
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("升温后压力会高于目标") && w.Contains("稳压不会放气"));
        double est = ProgramLimits.EstimateAfterHeat(100, 25, 80);
        Assert.Equal((100 + 14.696) * 353.15 / 298.15 - 14.696, est, 6);
        // 估算超过压力上限 → 拒绝
        r = ProgramLimits.PreRun(new[] { S(StepType.Press, ("pres", 420.0)), S(StepType.Heat, ("temp", 150.0)), S(StepType.Vent), S(StepType.End) }, Def);
        Assert.Contains(r.Errors, e => e.Contains("超过压力上限"));
        // 先升温到位再充压：不提示
        r = ProgramLimits.PreRun(new[] { S(StepType.Heat, ("temp", 80.0)), S(StepType.Press, ("pres", 100.0)), S(StepType.Heat, ("temp", 80.0)), S(StepType.Vent), S(StepType.End) }, Def);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("升温后压力"));
    }

    [Fact]
    public void Detail_UsesPressureDisplayUnit()
    {
        Assert.Equal("MPa", Units.Pressure);
        Assert.Equal("气体 A 到 2.000 MPa · 稳压开", StepSpecs.Detail(StepType.Press, StepSpecs.Defaults(StepType.Press), new List<(double, double, double)>()));
        Assert.Equal("惰性气体 · 0.500 MPa → 0.020 MPa × 3 · 先吹总管 2 次",
            StepSpecs.Detail(StepType.Exchange, StepSpecs.Defaults(StepType.Exchange), new List<(double, double, double)>()));
        Assert.Equal("关加热、阀全关、停搅拌", StepSpecs.Detail(StepType.End, new Dictionary<string, object>(), new List<(double, double, double)>()));
    }
}
