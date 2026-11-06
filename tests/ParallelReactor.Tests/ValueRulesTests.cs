using System;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C5：命令层数值校验（范围与今天的键盘 / 步进器限幅一致）。</summary>
public class ValueRulesTests
{
    private const double Eps = 1e-6;

    public static TheoryData<string, double, double> Ranges => new()
    {
        // 名称, min, max（TMaxSp = 200, VolMax = 20, RpmMax = 1500）
        { "TempSp", 10, 200 },
        { "Volume", 1, 20 },
        { "Rpm", 0, 1500 },
        { "SegCount", 1, 16 },
        { "SegTemp", 0, 200 },
        { "SegMinutes", 0.1, 6000 },
        { "AutotuneSp", 40, 200 },
        { "PidP", 0, 3200 },
        { "PidI", 0, 3200 },
        { "PidD", 0, 327 },
        { "Bound1", 10, 140 },    // 中温上限 150 − 10
        { "Bound2", 110, 390 },   // 低温上限 100 + 10
    };

    private static RuleViolation? Check(string rule, double v) => rule switch
    {
        "TempSp" => ValueRules.TempSp(v, 200),
        "Volume" => ValueRules.Volume(v, 20),
        "Rpm" => ValueRules.Rpm(v, 1500),
        "SegCount" => ValueRules.ProfileSegCount(v),
        "SegTemp" => ValueRules.ProfileSegment(v, null, 200),
        "SegMinutes" => ValueRules.ProfileSegment(null, v, 200),
        "AutotuneSp" => ValueRules.AutotuneSp(v, 200),
        "PidP" => ValueRules.Pid(0, v, 100, 10),
        "PidI" => ValueRules.Pid(0, 50, v, 10),
        "PidD" => ValueRules.Pid(0, 50, 100, v),
        "Bound1" => ValueRules.BandBound(1, v, 100, 150),
        "Bound2" => ValueRules.BandBound(2, v, 100, 150),
        _ => throw new ArgumentException(rule),
    };

    [Theory, MemberData(nameof(Ranges))]
    public void Boundaries(string rule, double min, double max)
    {
        Assert.Null(Check(rule, min));
        Assert.Null(Check(rule, max));
        Assert.Null(Check(rule, (min + max) / 2));
        foreach (var bad in new[] { min - Eps, max + Eps, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var v = Check(rule, bad);
            Assert.NotNull(v);
            Assert.Equal(OpCodes.ParamOutOfRange, v!.Code);
            Assert.Equal(min, v.Min);
            Assert.Equal(max, v.Max);
        }
    }

    [Fact]
    public void Message_CarriesRangeAndUnit()
    {
        Assert.Equal("温度 SP 须在 10–200 °C 之间", ValueRules.TempSp(5, 200)!.Message);
        Assert.Equal("溶液体积须在 1–20 mL 之间", ValueRules.Volume(0, 20)!.Message);
        Assert.Equal("微分时间 D 须在 0–327 s 之间", ValueRules.Pid(0, 1, 1, 400)!.Message);
        Assert.Equal("段时长须在 0.1–6000 分钟 之间", ValueRules.ProfileSegment(null, 0, 200)!.Message);
    }

    [Fact]
    public void Detail_HasMinMaxValue()
    {
        var d = ValueRules.Rpm(2000, 1500)!.Detail;
        var t = d.GetType();
        Assert.Equal(0.0, t.GetProperty("min")!.GetValue(d));
        Assert.Equal(1500.0, t.GetProperty("max")!.GetValue(d));
        Assert.Equal(2000.0, t.GetProperty("value")!.GetValue(d));
    }

    [Fact]
    public void Pid_BandOutOfRange_ReportedFirst()
    {
        Assert.Contains("PID 档位", ValueRules.Pid(3, 99999, 1, 1)!.Message);
        Assert.NotNull(ValueRules.Pid(-1, 1, 1, 1));
        Assert.Null(ValueRules.Pid(2, 1, 1, 1));
    }

    [Fact]
    public void ProfileSegment_BothNull_IsValid() => Assert.Null(ValueRules.ProfileSegment(null, null, 200));

    [Fact]
    public void StrictTempSp_MustBeBelowOverTemp()
    {
        Assert.Null(ValueRules.StrictTempSp(199.9, 200));
        Assert.NotNull(ValueRules.StrictTempSp(200, 200));
        Assert.NotNull(ValueRules.StrictTempSp(double.NaN, 200));
        Assert.Contains("超温报警值 200", ValueRules.StrictTempSp(250, 200)!.Message);
    }

    [Fact]
    public void BandBound_UnknownIndexRejected() => Assert.NotNull(ValueRules.BandBound(3, 100, 100, 150));
}
