using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>旧格式 → v0.8（STEPS-SPEC §3，Contracts/Models/StepMigration.cs）：表里每一行 + 末尾 end 的处理。</summary>
public class StepMigrationTests
{
    private static MigStep M(string type, params (string K, object V)[] kv)
        => new(type, kv.ToDictionary(x => x.K, x => x.V));

    private static IReadOnlyList<MigStep> Run(out bool migrated, params MigStep[] steps)
    {
        var r = StepMigration.Migrate(steps);
        migrated = r.Migrated;
        return r.Steps;
    }

    private static string[] Types(IReadOnlyList<MigStep> s) => s.Select(x => x.Type).ToArray();

    [Fact]
    public void Purge_BecomesLineFlushOnlyExchange()
    {
        var o = Run(out bool mig, M("purge", ("gas", "A"), ("cycles", 4.0), ("fill", 3.0), ("vent", 5.0)));
        Assert.True(mig);
        Assert.Equal(new[] { "exchange", "end" }, Types(o));
        Assert.Equal("A", o[0].Str("gas"));
        Assert.Equal(0, o[0].Num("cycles"));
        Assert.Equal(4, o[0].Num("lineFlush"));
        Assert.False(o[0].P.ContainsKey("fill"));   // 其余取默认
    }

    [Fact]
    public void HeatAndPress_PassThrough()
    {
        var heat = new MigStep("heat", new Dictionary<string, object> { ["mode"] = "curve", ["temp"] = 80.0, ["wait"] = 10.0 }, new[] { (60.0, 2.0, 5.0), (120.0, 1.0, 30.0) });
        var press = M("press", ("gas", "B"), ("pres", 100.0), ("regulate", "off"));
        var o = Run(out bool mig, heat, press, M("end"));
        Assert.False(mig);   // heat / press 没有旧参数
        Assert.Equal(new[] { "heat", "press", "end" }, Types(o));
        Assert.Same(heat, o[0]);
        Assert.Equal(2, o[0].Segments.Count);
        Assert.Equal("B", o[1].Str("gas"));
        Assert.Equal(100, o[1].Num("pres"));
    }

    [Fact]
    public void Stir_WithDuration_BecomesStirHoldStir()
    {
        var o = Run(out bool mig, M("stir", ("rpm", 600.0), ("dur", 12.0)));
        Assert.True(mig);
        Assert.Equal(new[] { "stir", "hold", "stir", "end" }, Types(o));
        Assert.Equal(600, o[0].Num("rpm"));
        Assert.False(o[0].P.ContainsKey("dur"));
        Assert.Equal("time", o[1].Str("until"));
        Assert.Equal(12, o[1].Num("time"));
        Assert.Equal(0, o[2].Num("rpm"));
    }

    [Fact]
    public void Stir_ZeroDuration_OnlyStir()
    {
        var o = Run(out bool mig, M("stir", ("rpm", 300.0), ("dur", 0.0)));
        Assert.True(mig);   // 带 dur 就是旧格式
        Assert.Equal(new[] { "stir", "end" }, Types(o));
        Assert.Equal(300, o[0].Num("rpm"));
    }

    [Fact]
    public void Stir_StopWithDuration_KeepsTheWait()
    {
        // 旧「搅拌 0 rpm · 5 min」：停搅拌后停留 5 分钟 → 搅拌 0 → 保持 5（不再多补一个搅拌 0）
        var o = Run(out _, M("stir", ("rpm", 0.0), ("dur", 5.0)));
        Assert.Equal(new[] { "stir", "hold", "end" }, Types(o));
        Assert.Equal(5, o[1].Num("time"));
    }

    [Fact]
    public void WaitFixed_BecomesHoldTime()
    {
        var o = Run(out bool mig, M("wait", ("mode", "fixed"), ("time", 25.0)));
        Assert.True(mig);
        Assert.Equal(new[] { "hold", "end" }, Types(o));
        Assert.Equal("time", o[0].Str("until"));
        Assert.Equal(25, o[0].Num("time"));
    }

    [Fact]
    public void WaitTemp_UsesPreviousHeatTarget_Or40()
    {
        var o = Run(out _, M("heat", ("mode", "fixed"), ("temp", 120.0)), M("wait", ("mode", "temp"), ("time", 45.0)));
        Assert.Equal("tempAbove", o[1].Str("until"));
        Assert.Equal(120, o[1].Num("temp"));
        Assert.Equal(45, o[1].Num("maxWait"));
        Assert.Equal("continue", o[1].Str("onTimeout"));

        var curve = new MigStep("heat", new Dictionary<string, object> { ["mode"] = "curve" }, new[] { (80.0, 2.0, 0.0), (150.0, 1.0, 30.0) });
        o = Run(out _, curve, M("wait", ("mode", "temp"), ("time", 10.0)));
        Assert.Equal(150, o[1].Num("temp"));   // 曲线取最后一段

        o = Run(out _, M("wait", ("mode", "temp"), ("time", 10.0)));
        Assert.Equal(40, o[0].Num("temp"));    // 前面没有升温
    }

    [Fact]
    public void WaitPressure_UsesPreviousPressTimes097_Or14_5()
    {
        var o = Run(out _, M("press", ("gas", "A"), ("pres", 100.0)), M("wait", ("mode", "pressure"), ("time", 30.0)));
        Assert.Equal("pressAbove", o[1].Str("until"));
        Assert.Equal(97, o[1].Num("pres"));
        Assert.Equal(30, o[1].Num("maxWait"));
        Assert.Equal("continue", o[1].Str("onTimeout"));

        o = Run(out _, M("wait", ("mode", "pressure"), ("time", 30.0)));
        Assert.Equal(14.5, o[0].Num("pres"));
    }

    [Theory]
    [InlineData("time", "off")]
    [InlineData("gasConsumed", "on")]
    [InlineData("either", "on")]
    public void React_EndBy_MapsToMmolOn(string endBy, string mmolOn)
    {
        var o = Run(out bool mig, M("react", ("endBy", endBy), ("time", 90.0), ("mmol", 2.5)));
        Assert.True(mig);
        Assert.Equal(new[] { "react", "end" }, Types(o));
        Assert.Equal(90, o[0].Num("time"));
        Assert.Equal(0, o[0].Num("minTime"));
        Assert.Equal(mmolOn, o[0].Str("mmolOn"));
        Assert.Equal(2.5, o[0].Num("mmol"));
        Assert.Equal("off", o[0].Str("stallOn"));
        Assert.False(o[0].P.ContainsKey("endBy"));
    }

    [Fact]
    public void EndSeal_BecomesHoldTempBelowThenEnd()
    {
        var o = Run(out bool mig, M("end", ("mode", "seal"), ("cool", 45.0)));
        Assert.True(mig);
        Assert.Equal(new[] { "hold", "end" }, Types(o));
        Assert.Equal("tempBelow", o[0].Str("until"));
        Assert.Equal(45, o[0].Num("temp"));
        Assert.Equal(60, o[0].Num("maxWait"));
        Assert.Equal("continue", o[0].Str("onTimeout"));
        Assert.Empty(o[1].P);   // 新的 end 没有参数
    }

    [Theory]
    [InlineData("vent")]
    [InlineData("quench")]
    public void EndVentOrQuench_BecomesHoldVentEnd(string mode)
    {
        var o = Run(out _, M("end", ("mode", mode), ("cool", 50.0)));
        Assert.Equal(new[] { "hold", "vent", "end" }, Types(o));
        Assert.Equal(50, o[0].Num("temp"));
        Assert.Equal(0, o[1].Num("flush"));
    }

    [Fact]
    public void EndPurge_BecomesHoldExchangeEnd()
    {
        var o = Run(out _, M("end", ("mode", "purge"), ("cool", 40.0)));
        Assert.Equal(new[] { "hold", "exchange", "end" }, Types(o));
        Assert.Equal("inert", o[1].Str("gas"));
        Assert.Equal(0, o[1].Num("cycles"));
        Assert.Equal(3, o[1].Num("lineFlush"));
    }

    [Fact]
    public void NewFormat_Unchanged_EndAppendedIfMissing()
    {
        var steps = new[] { M("exchange", ("gas", "inert")), M("stir", ("rpm", 400.0)), M("hold", ("until", "time"), ("time", 5.0)) };
        var o = Run(out bool mig, steps);
        Assert.False(mig);   // 只补 end 不算转换
        Assert.Equal(new[] { "exchange", "stir", "hold", "end" }, Types(o));
        Assert.Same(steps[1], o[1]);
    }

    [Fact]
    public void ExtraEnds_KeepOnlyTheLast_AndFlagMigrated()
    {
        var o = Run(out bool mig, M("end"), M("stir", ("rpm", 400.0)), M("end"));
        Assert.Equal(new[] { "stir", "end" }, Types(o));
        Assert.True(mig);
    }

    [Fact]
    public void EndInTheMiddle_MovedToLast()
    {
        var o = Run(out bool mig, M("stir", ("rpm", 400.0)), M("end"), M("hold", ("until", "time"), ("time", 1.0)));
        Assert.Equal(new[] { "stir", "hold", "end" }, Types(o));
        Assert.True(mig);
    }

    [Fact]
    public void LegacyRecognition()
    {
        Assert.True(StepMigration.IsLegacy(M("purge")));
        Assert.True(StepMigration.IsLegacy(M("wait")));
        Assert.True(StepMigration.IsLegacy(M("stir", ("rpm", 1.0), ("dur", 1.0))));
        Assert.True(StepMigration.IsLegacy(M("end", ("mode", "seal"))));
        Assert.True(StepMigration.IsLegacy(M("end", ("cool", 40.0))));
        Assert.True(StepMigration.IsLegacy(M("react", ("endBy", "time"))));
        Assert.False(StepMigration.IsLegacy(M("stir", ("rpm", 400.0))));
        Assert.False(StepMigration.IsLegacy(M("end")));
        Assert.False(StepMigration.IsLegacy(M("react", ("time", 60.0), ("mmolOn", "on"))));
    }

    [Fact]
    public void WholeLegacyProgram_ConvertsInOrder()
    {
        var o = Run(out bool mig,
            M("purge", ("gas", "inert"), ("cycles", 10.0)),
            M("heat", ("temp", 150.0), ("wait", 15.0)),
            M("press", ("gas", "A"), ("pres", 50.0), ("regulate", "on")),
            M("stir", ("rpm", 400.0), ("dur", 5.0)),
            M("react", ("endBy", "either"), ("time", 120.0), ("mmol", 5.0)),
            M("end", ("mode", "vent"), ("cool", 40.0)));
        Assert.True(mig);
        Assert.Equal(new[] { "exchange", "heat", "press", "stir", "hold", "stir", "react", "hold", "vent", "end" }, Types(o));
    }
}
