using System;
using ParallelReactor.Control.Core.Format;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Format.NumInput（NumBox 的解析与限幅）：只接受有限值 —— M1 复核发现输入「NaN」会把设定值变成 NaN、
/// 无上下限时「1e400」变成 +∞；设定值最终下发给 HMI，坏值必须被拒绝（输入框恢复原值）。
/// </summary>
public class NumInputTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("∞")]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("abc")]
    [InlineData("12,5")]
    [InlineData("1 2")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0x10")]
    public void TryParse_RejectsNonFiniteAndJunk(string? text)
    {
        Assert.False(NumInput.TryParse(text, out var v));
        Assert.Equal(0, v);
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData(" 120.5 ", 120.5)]
    [InlineData("-5", -5)]
    [InlineData("+3", 3)]
    [InlineData(".5", 0.5)]
    [InlineData("1e2", 100)]
    [InlineData("１２３", 123)]          // 全角数字（中文输入法）
    [InlineData("１２．５", 12.5)]
    [InlineData("－２", -2)]
    [InlineData("−2", -2)]               // U+2212 减号
    [InlineData("1.7976931348623157e308", double.MaxValue)]
    public void TryParse_AcceptsFiniteNumbers(string text, double expected)
    {
        Assert.True(NumInput.TryParse(text, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Clamp_NonFinite_ReturnsFallback(double bad)
    {
        Assert.Equal(120, NumInput.Clamp(bad, 0, 200, 1, fallback: 120));
        Assert.Equal(7, NumInput.Clamp(bad, double.NaN, double.NaN, 0, fallback: 7));   // 无上下限也不放过 ∞
    }

    [Theory]
    [InlineData(250, 0, 200, 1, 200)]
    [InlineData(-5, 0, 200, 1, 0)]
    [InlineData(199.94, 0, 200, 1, 199.9)]
    [InlineData(199.96, 0, 200, 1, 200)]
    [InlineData(0.25, 0, 1, 1, 0.3)]                  // 远离零
    [InlineData(-0.25, -1, 1, 1, -0.3)]
    [InlineData(199.97, 0, 199.95, 1, 199.9)]        // 取整不越过上限
    [InlineData(0.01, 0.04, 1, 1, 0.1)]              // 取整不越过下限（0.04 → 0.0 < 下限 → 0.1）
    [InlineData(1e12, double.NaN, double.NaN, 0, 1e12)]
    public void Clamp_LimitsThenRounds(double v, double min, double max, int dec, double expected)
        => Assert.Equal(expected, NumInput.Clamp(v, min, max, dec, fallback: -1), 10);

    [Fact]
    public void StepBase_UsesFiniteCurrentElseMinElseZero()
    {
        Assert.Equal(12, NumInput.StepBase(12, 0));
        Assert.Equal(5, NumInput.StepBase(null, 5));
        Assert.Equal(5, NumInput.StepBase(double.NaN, 5));
        Assert.Equal(0, NumInput.StepBase(double.PositiveInfinity, double.NaN));
        Assert.Equal(0, NumInput.StepBase(null, double.NegativeInfinity));
    }

    [Fact]
    public void Normalize_OnlyTouchesFullWidthDigitsAndSigns()
    {
        Assert.Equal("123.5", NumInput.Normalize("１２３．５"));
        Assert.Equal("-1", NumInput.Normalize("－１"));
        Assert.Equal("abc", NumInput.Normalize("abc"));
        Assert.Equal("1,5", NumInput.Normalize("1,5"));
    }
}
