using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Format.Fmt / JsNumber 对照样稿 PR.fmt（research/pc-v3 js/core.js）。下面手写的期望值都先在 JavaScriptCore 里用样稿函数算过；
/// 另有 MockVectors 里样稿函数算出的近 4000 条（边界值、负零、一半进位、≥ 1e21、随机读数），要求逐字节相同。
/// </summary>
public class FmtTests
{
    [Theory]
    [InlineData(119.85, null, "119.8")]   // 119.85 的 double 略小于 119.85
    [InlineData(25.0, null, "25.0")]
    [InlineData(-0.04, null, "-0.0")]     // JS toFixed：负号单独保留
    [InlineData(199.95, null, "199.9")]
    [InlineData(0.25, 1, "0.3")]
    [InlineData(null, null, "—")]
    [InlineData(double.NaN, null, "—")]
    [InlineData(double.PositiveInfinity, 2, "—")]
    public void Temp(double? v, int? d, string expected) => Assert.Equal(expected, Fmt.Temp(v, d));

    [Theory]
    [InlineData(119.8, null, "119.8 ℃")]
    [InlineData(25.0, 0, "25 ℃")]
    [InlineData(null, null, "—")]
    public void TempU(double? v, int? d, string expected) => Assert.Equal(expected, Fmt.TempU(v, d));

    [Theory]
    [InlineData(0.3495, null, "0.349")]
    [InlineData(0.35, null, "0.350")]
    [InlineData(5.2, null, "5.200")]       // 默认按数值：< 10 用 3 位
    [InlineData(5.2, 2, "5.20")]
    [InlineData(10.0, null, "10.00")]      // ≥ 10 用 2 位
    [InlineData(9.9995, null, "9.999")]
    [InlineData(-12.345, null, "-12.35")]
    [InlineData(0.0005, null, "0.001")]
    [InlineData(2.997, null, "2.997")]
    public void Mpa(double? v, int? d, string expected) => Assert.Equal(expected, Fmt.Mpa(v, d));

    [Theory]
    [InlineData(3.12, null, "3.120 MPa")]
    [InlineData(3.12, 2, "3.12 MPa")]
    [InlineData(null, null, "—")]
    public void MpaU(double? v, int? d, string expected) => Assert.Equal(expected, Fmt.MpaU(v, d));

    [Theory]
    [InlineData(0.35, null, "50.8")]
    [InlineData(1.0, null, "145.0")]
    [InlineData(3.12, null, "452.5")]
    [InlineData(0.35, 0, "51")]
    [InlineData(null, null, "—")]
    public void Psi(double? mpa, int? d, string expected) => Assert.Equal(expected, Fmt.Psi(mpa, d));

    [Fact]
    public void UnitSuffixes()
    {
        Assert.Equal("145.0 psi", Fmt.PsiU(1));
        Assert.Equal("300 rpm", Fmt.RpmU(300));
        Assert.Equal("63 %", Fmt.PctU(62.5));
        Assert.Equal("—", Fmt.PsiU(null));
        Assert.Equal("—", Fmt.RpmU(double.NaN));
        Assert.Equal("—", Fmt.PctU(null));
    }

    [Theory]
    [InlineData(299.5, "300")]
    [InlineData(-2.5, "-2")]   // JS Math.round：一半时向 +∞
    [InlineData(300.4, "300")]
    [InlineData(1500.0, "1500")]
    [InlineData(null, "—")]
    public void Rpm(double? v, string expected) => Assert.Equal(expected, Fmt.Rpm(v));

    [Theory]
    [InlineData(62.5, "63")]
    [InlineData(0.0, "0")]
    public void Pct(double? v, string expected) => Assert.Equal(expected, Fmt.Pct(v));

    [Theory]
    [InlineData(1234.5, null, "1234.50")]
    [InlineData(1.005, null, "1.00")]
    [InlineData(0.125, 1, "0.1")]
    public void Mmol(double? v, int? d, string expected) => Assert.Equal(expected, Fmt.Mmol(v, d));

    [Theory]
    [InlineData(19.5, "20")]
    [InlineData(20.4, "20")]
    [InlineData(null, "—")]
    public void Ml(double? v, string expected) => Assert.Equal(expected, Fmt.Ml(v));

    [Theory]
    [InlineData(42.0, "42 min")]
    [InlineData(72.0, "1 h 12 min")]
    [InlineData(65.0, "1 h 05 min")]
    [InlineData(59.5, "1 h 00 min")]
    [InlineData(-3.0, "0 min")]
    [InlineData(0.4, "0 min")]
    [InlineData(120.0, "2 h 00 min")]
    [InlineData(999.0, "16 h 39 min")]
    [InlineData(null, "—")]
    public void Minutes(double? m, string expected) => Assert.Equal(expected, Fmt.Minutes(m));

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(59.5, "01:00")]
    [InlineData(3599.5, "1:00:00")]
    [InlineData(3725.0, "1:02:05")]
    [InlineData(-1.0, "00:00")]
    [InlineData(45296.4, "12:34:56")]
    [InlineData(double.NaN, "—")]
    public void Dur(double? sec, string expected) => Assert.Equal(expected, Fmt.Dur(sec));

    [Theory]
    [InlineData(0.0, "刚刚")]
    [InlineData(4499.0, "刚刚")]
    [InlineData(4500.0, "5 s 前")]
    [InlineData(42000.0, "42 s 前")]
    [InlineData(60000.0, "1 min 前")]
    [InlineData(1500000.0, "25 min 前")]
    [InlineData(3900000.0, "1 h 05 min 前")]
    [InlineData(-5000.0, "刚刚")]
    [InlineData(86400000.0, "24 h 00 min 前")]
    [InlineData(null, "—")]
    public void Ago(double? ms, string expected) => Assert.Equal(expected, Fmt.Ago(ms));

    [Fact]
    public void Dates()
    {
        var d = new DateTime(2026, 11, 8, 10, 12, 0);   // 样稿演示时钟：2026-11-08 周日 10:12:00
        Assert.Equal("10:12:00", Fmt.Time(d));
        Assert.Equal("10:12", Fmt.Hm(d));
        Assert.Equal("2026-11-08", Fmt.Date(d));
        Assert.Equal("2026-11-08 10:12:00", Fmt.Datetime(d));
        Assert.Equal("周日", Fmt.Weekday(d));
        var d2 = new DateTime(2026, 1, 5, 7, 3, 9);
        Assert.Equal("07:03:09", Fmt.Time(d2));
        Assert.Equal("2026-01-05", Fmt.Date(d2));
        Assert.Equal("周一", Fmt.Weekday(d2));
        Assert.Equal("—", Fmt.Time(null));
        Assert.Equal("—", Fmt.Hm(null));
        Assert.Equal("—", Fmt.Date(null));
        Assert.Equal("—", Fmt.Datetime(null));
        Assert.Equal("—", Fmt.Weekday(null));
    }

    [Fact]
    public void Dates_UtcIsShownInLocalTime()
    {
        // HMI 的时间都是 UTC（Kind = Utc）：显示前转本地，同 JS Date 的 getHours；Unspecified / Local 按日历字段原样
        var utc = new DateTime(2026, 11, 7, 23, 12, 5, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        Assert.Equal(Fmt.Time(new DateTime(local.Ticks, DateTimeKind.Unspecified)), Fmt.Time(utc));
        Assert.Equal(Fmt.Datetime(new DateTime(local.Ticks, DateTimeKind.Unspecified)), Fmt.Datetime(utc));
        Assert.Equal("23:12:05", Fmt.Time(new DateTime(2026, 11, 7, 23, 12, 5, DateTimeKind.Unspecified)));
    }

    [Theory]
    [InlineData(5.0, "05")]
    [InlineData(12.0, "12")]
    [InlineData(0.0, "00")]
    [InlineData(123.0, "123")]
    public void Pad2(double n, string expected) => Assert.Equal(expected, Fmt.Pad2(n));

    [Theory]
    [InlineData(4.14, "4.14")]
    [InlineData(1e21, "1e+21")]
    [InlineData(2e21, "2e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(100.0, "100")]
    [InlineData(-4.137, "-4.137")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    public void JsNumber_ToString(double v, string expected) => Assert.Equal(expected, JsNumber.ToString(v));

    [Fact]
    public void JsNumber_NegativeZero()
    {
        double negZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal("0", JsNumber.ToString(negZero));
        Assert.Equal("0.0", JsNumber.ToFixed(negZero, 1));    // JS (-0).toFixed(1) = "0.0"
        Assert.Equal("-0.00", JsNumber.ToFixed(-0.0004, 2));  // 但很小的负数保留负号
        Assert.Equal("0", Fmt.Rpm(-0.4));                     // Math.round(-0.4) = -0 → "0"
    }

    [Theory]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(1.45, 1, "1.4")]
    [InlineData(2.5, 0, "3")]
    [InlineData(-2.5, 0, "-3")]
    [InlineData(0.5, 0, "1")]
    [InlineData(1e21, 2, "1e+21")]
    [InlineData(1.255, 2, "1.25")]
    [InlineData(8.345, 2, "8.35")]
    [InlineData(0.000001, 7, "0.0000010")]
    public void JsNumber_ToFixed(double v, int digits, string expected) => Assert.Equal(expected, JsNumber.ToFixed(v, digits));

    [Theory]
    [InlineData(2.5, 3.0)]
    [InlineData(-2.5, -2.0)]
    [InlineData(0.49999999999999994, 0.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 2.0)]
    public void JsNumber_Round(double v, double expected) => Assert.Equal(expected, JsNumber.Round(v));

    [Fact]
    public void Constants_MatchMock()
    {
        Assert.Equal(Fmt.Dash, MockVectorData.Get("dash").GetString());
        Assert.Equal(Fmt.MockPsiPerMpa, MockVectorData.Get("fmtPsi").GetDouble());
    }

    /// <summary>样稿函数在 JavaScriptCore 里算出的全部数值向量：num(d 0–4 / 默认)、mpa、psi、mmol、rpm、minutes、dur、ago、String(number)。</summary>
    [Fact]
    public void MatchesMock_NumberVectors()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in MockVectorData.Get("fmt").EnumerateArray())
        {
            string fn = row[0].GetString()!, vs = row[1].GetString()!, want = row[3].GetString()!;
            int d = row[2].GetInt32();
            double? v = MockVectorData.Num(vs);
            int? dd = d < 0 ? null : d;
            string got = fn switch
            {
                "num" => Fmt.Num(v, dd),
                "mpa" => Fmt.Mpa(v, dd),
                "psi" => Fmt.Psi(v, dd),
                "mmol" => Fmt.Mmol(v, dd),
                "rpm" => Fmt.Rpm(v),
                "minutes" => Fmt.Minutes(v),
                "dur" => Fmt.Dur(v),
                "ago" => Fmt.Ago(v),
                "str" => JsNumber.ToString(v!.Value),
                _ => throw new InvalidOperationException("未知函数 " + fn),
            };
            n++;
            if (got != want) bad.Add(fn + "(" + vs + (dd is null ? "" : ", " + dd) + ") = \"" + got + "\"，样稿 \"" + want + "\"");
        }
        Assert.True(n > 3000, "向量太少：" + n);
        Assert.True(bad.Count == 0, bad.Count + " 条与样稿不同：\n" + string.Join("\n", bad.Take(40)));
    }

    [Fact]
    public void MatchesMock_DateVectors()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var r in MockVectorData.Get("dates").EnumerateArray())
        {
            var d = new DateTime(r.GetProperty("y").GetInt32(), r.GetProperty("mo").GetInt32(), r.GetProperty("d").GetInt32(),
                r.GetProperty("h").GetInt32(), r.GetProperty("mi").GetInt32(), r.GetProperty("s").GetInt32(), DateTimeKind.Unspecified);
            void Check(string name, string got)
            {
                string want = r.GetProperty(name).GetString()!;
                if (got != want) bad.Add(name + "(" + d.ToString("s") + ") = \"" + got + "\"，样稿 \"" + want + "\"");
            }
            Check("time", Fmt.Time(d));
            Check("hm", Fmt.Hm(d));
            Check("date", Fmt.Date(d));
            Check("datetime", Fmt.Datetime(d));
            Check("weekday", Fmt.Weekday(d));
            n++;
        }
        Assert.True(n >= 30, "日期向量太少：" + n);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
