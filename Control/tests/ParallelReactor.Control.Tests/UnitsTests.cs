using System;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>Core.Format.Units：MPa ↔ psi（与 HMI 同一个常数）、按型号的压力小数位（样稿 PR.sim.pDec / fmtP）、单位写法。</summary>
public class UnitsTests
{
    [Fact]
    public void PsiPerMpa_SameAsHmi()
    {
        Assert.Equal(145.037738, Units.PsiPerMpa);   // HMI Models/Units.cs、Service/TelemetryCsv.cs
        Assert.Equal(145.037738, Units.MpaToPsi(1.0), 9);
        Assert.Equal(1.0, Units.PsiToMpa(145.037738), 12);
        Assert.Equal(0.35, Units.PsiToMpa(Units.MpaToPsi(0.35)), 12);
        Assert.Equal(500.0, Units.MpaToPsi(Units.PsiToMpa(500.0)), 9);
    }

    [Fact]
    public void Nullable_PassesNullThrough()
    {
        Assert.Null(Units.PsiToMpa((double?)null));
        Assert.Null(Units.MpaToPsi((double?)null));
        Assert.Equal(1.0, Units.PsiToMpa((double?)145.037738)!.Value, 12);
    }

    [Theory]
    [InlineData(4.14, 3)]
    [InlineData(4.137, 3)]
    [InlineData(4.999, 3)]
    [InlineData(3.0, 3)]
    [InlineData(5.0, 2)]
    [InlineData(10.0, 2)]
    public void PressureDecimals_ByRange(double maxMpa, int expected) => Assert.Equal(expected, Units.PressureDecimals(maxMpa));

    [Fact]
    public void FmtP_ByModel()
    {
        var t = ModelProfiles.Embedded;
        Assert.Equal("0.350", Units.FmtP(t.Get("PR8"), 0.35));
        Assert.Equal("5.20", Units.FmtP(t.Get("PR4"), 5.2));        // HT-PR4 量程 10 MPa → 2 位
        Assert.Equal("2.997", Units.FmtP(t.Get("PR16"), 2.997));
        Assert.Equal("0.350", Units.FmtP(null, 0.35));              // 型号未知 → 3 位（样稿 fmtP(null, v)）
        Assert.Equal("5.20 MPa", Units.FmtPU(t.Get("PR4"), 5.2));
        Assert.Equal("0.350 MPa", Units.FmtPU(t.Get("PR8-4"), 0.35));
        Assert.Equal("—", Units.FmtP(t.Get("PR8"), null));
        Assert.Equal("1.000", Units.FmtPFromPsi(t.Get("PR8"), 145.037738));
        Assert.Equal("5.00", Units.FmtPFromPsi(t.Get("PR4"), 5 * 145.037738));
        Assert.Equal("—", Units.FmtPFromPsi(t.Get("PR8"), null));
    }

    [Fact]
    public void PsiRaw_NoRoundTrip()
    {
        Assert.Equal("214.2", Units.PsiRaw(214.171875));   // live-snapshot.json RV1
        Assert.Equal("214", Units.PsiRaw(214.171875, 0));
        Assert.Equal("—", Units.PsiRaw(null));
    }

    [Fact]
    public void UnitSymbols_SameAsMock()
    {
        Assert.Equal("℃", Units.Celsius);
        Assert.Equal(0x2103, Units.Celsius[0]);                       // U+2103，不是「°C」
        Assert.EndsWith(" " + Units.Celsius, Fmt.TempU(20));
        Assert.EndsWith(" " + Units.Mpa, Fmt.MpaU(1));
        Assert.EndsWith(" " + Units.Psi, Fmt.PsiU(1));
        Assert.EndsWith(" " + Units.Rpm, Fmt.RpmU(1));
        Assert.EndsWith(" " + Units.Percent, Fmt.PctU(1));
    }

    [Fact]
    public void ModelDecimals_MatchMockPDec()
    {
        foreach (var m in MockVectorData.Get("models").EnumerateArray())
        {
            string key = m.GetProperty("key").GetString()!;
            Assert.Equal(m.GetProperty("pDec").GetInt32(), ModelProfiles.Embedded.Get(key)!.PressureDecimals);
        }
        Assert.Equal(4, ModelProfiles.Embedded.All.Count(p => p.PressureDecimals is 2 or 3));
    }
}
