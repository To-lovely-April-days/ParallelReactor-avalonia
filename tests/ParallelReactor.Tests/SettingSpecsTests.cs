using System;
using System.Linq;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>批 C：设置项表（界面行与命令层范围的唯一来源）与校验。</summary>
public class SettingSpecsTests
{
    [Fact]
    public void Table_CoversEveryKeyOnce()
    {
        var keys = SettingSpecs.All.Select(s => s.Key).ToList();
        Assert.Equal(Enum.GetValues<SettingKey>().Length, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(24, keys.Count);   // 设置页「参数与报警」的 24 个数值行
    }

    [Fact]
    public void EachSpec_HasSaneRangeAndStep()
    {
        foreach (var s in SettingSpecs.All)
        {
            Assert.True(s.Min < s.Max, $"{s.Key}: min < max");
            Assert.True(s.Step > 0, $"{s.Key}: step > 0");
            Assert.False(string.IsNullOrWhiteSpace(s.Label), $"{s.Key}: 标签");
        }
    }

    [Fact]
    public void Defaults_AreInsideRange_AndGetSetRoundTrip()
    {
        var st = new AppSettings();
        foreach (var s in SettingSpecs.All)
        {
            double v = s.Get(st);
            Assert.True(v >= s.Min && v <= s.Max, $"{s.Key} 默认值 {v} 不在 {s.Min}–{s.Max}");
            s.Set(st, s.Max);
            Assert.Equal(s.Max, s.Get(st));
        }
    }

    [Theory]
    [InlineData(SettingKey.StirCurrent)]
    [InlineData(SettingKey.StirStandbyPct)]
    [InlineData(SettingKey.StirRampMs)]
    [InlineData(SettingKey.StirMicrostep)]
    [InlineData(SettingKey.PressFullScaleMPa)]
    public void DeviceKeys_AreLocalOnly(SettingKey key)
    {
        var s = SettingSpecs.Of(key);
        Assert.True(s.WritesDevice);
        Assert.Equal(Exposure.LocalOnly, s.Exposure);
    }

    [Fact]
    public void NonDeviceKeys_AreRemoteExposed()
    {
        foreach (var s in SettingSpecs.All.Where(s => !s.WritesDevice))
            Assert.Equal(Exposure.Remote, s.Exposure);
    }

    [Theory]
    [InlineData(SettingKey.TMaxSp, 50, true)]
    [InlineData(SettingKey.TMaxSp, 300, true)]
    [InlineData(SettingKey.TMaxSp, 49.999, false)]
    [InlineData(SettingKey.TMaxSp, 300.001, false)]
    [InlineData(SettingKey.ApproachRate, 0.02, true)]
    [InlineData(SettingKey.ApproachRate, 0.01, false)]
    [InlineData(SettingKey.StirStandbyPct, 0, true)]
    [InlineData(SettingKey.StirStandbyPct, -1, false)]
    public void Validate_Boundaries(SettingKey key, double v, bool ok)
    {
        var r = SettingSpecs.Validate(key, v);
        Assert.Equal(ok, r == null);
        if (!ok)
        {
            Assert.Equal(OpCodes.ParamOutOfRange, r!.Code);
            Assert.Contains(SettingSpecs.Of(key).Label, r.Message);
        }
    }

    [Fact]
    public void Validate_RejectsNaNAndInfinity()
    {
        Assert.NotNull(SettingSpecs.Validate(SettingKey.OverTemp, double.NaN));
        Assert.NotNull(SettingSpecs.Validate(SettingKey.OverTemp, double.PositiveInfinity));
    }

    [Fact]
    public void Validate_MessageCarriesRange()
    {
        var r = SettingSpecs.Validate(SettingKey.TMaxSp, 10);
        Assert.Equal("温度 SP 上限须在 50–300 °C 之间", r!.Message);
    }

    [Fact]
    public void CrossCheck_TMaxSpMustBeBelowOverTemp()
    {
        var st = new AppSettings { TMaxSp = 200, OverTemp = 250 };
        Assert.Null(SettingSpecs.CrossCheck(st, SettingKey.TMaxSp, 245));
        Assert.Equal(OpCodes.ParamCrossCheck, SettingSpecs.CrossCheck(st, SettingKey.TMaxSp, 250)!.Code);
        Assert.Equal(OpCodes.ParamCrossCheck, SettingSpecs.CrossCheck(st, SettingKey.OverTemp, 200)!.Code);
        Assert.Null(SettingSpecs.CrossCheck(st, SettingKey.OverTemp, 201));
    }

    [Fact]
    public void CrossCheck_LeakTestPsiBelow90PercentOfOverPressure()
    {
        var st = new AppSettings { LeakTestPsi = 50, OverPressure = 100 };
        Assert.Null(SettingSpecs.CrossCheck(st, SettingKey.LeakTestPsi, 89));
        var r = SettingSpecs.CrossCheck(st, SettingKey.LeakTestPsi, 90);
        Assert.Equal(OpCodes.ParamCrossCheck, r!.Code);
        Assert.Contains("90%", r.Message);
        Assert.NotNull(SettingSpecs.CrossCheck(st, SettingKey.OverPressure, 55));   // 降低超压值同样触发
    }

    [Fact]
    public void CrossCheck_UnrelatedKey_Passes()
        => Assert.Null(SettingSpecs.CrossCheck(new AppSettings(), SettingKey.HeatTimeout, 30));
}
