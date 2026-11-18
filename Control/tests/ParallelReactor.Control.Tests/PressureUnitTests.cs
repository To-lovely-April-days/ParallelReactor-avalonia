using System;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.Text;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 改 <see cref="PressureDisplay"/> 的单位是进程级的（同 App 的设置）：这些测试放在不并行的集合里（其它测试都跑完再跑），每条结束改回 MPa。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PressureUnitCollection
{
    public const string Name = "压力单位（进程级）";
}

/// <summary>
/// 压力显示单位（设置 → 常规「压力单位」MPa / bar / psi）：Core 的换算与格式化（<see cref="PressureUnits"/> / <see cref="PressureDisplay"/>）、
/// 程序页的参数（<see cref="FieldSpec"/>：范围、输入、默认值、超范围、HMI 程序导入）、曲线（分带、曲线页纵轴、反应釜曲线）、报警页的值 / 限值、断行保护。
/// MPa 的写法与改单位之前逐字节相同（现有的测试照旧在 MPa 下跑）。
/// </summary>
[Collection(PressureUnitCollection.Name)]
public sealed class PressureUnitTests : IDisposable
{
    private static readonly ModelProfile Pr8 = PcV3Models.Get("PR8");     // 量程 4.14 MPa → 3 位小数
    private static readonly ModelProfile Pr4 = PcV3Models.Get("PR4");     // 量程 ≥ 5 MPa → 2 位小数

    public PressureUnitTests() => PressureDisplay.Set(PressureUnit.MPa);

    public void Dispose() => PressureDisplay.Set(PressureUnit.MPa);

    // ———————————————————— 换算 ————————————————————

    [Fact]
    public void Conversions_MatchTheHmiConstants()
    {
        // HMI Models/Units.cs：1 MPa = 145.037738 psi，1 bar = 14.5037738 psi
        Assert.Equal(145.037738, PressureUnits.FromMpa(1, PressureUnit.Psi), 9);
        Assert.Equal(10, PressureUnits.FromMpa(1, PressureUnit.Bar), 12);
        Assert.Equal(1, PressureUnits.FromMpa(1, PressureUnit.MPa));
        Assert.Equal(14.5037738, PressureUnits.FromMpa(PressureUnits.ToMpa(1, PressureUnit.Bar), PressureUnit.Psi), 9);
        Assert.Equal(0.35, PressureUnits.ToMpa(PressureUnits.FromMpa(0.35, PressureUnit.Psi), PressureUnit.Psi), 12);
        Assert.Null(PressureUnits.FromMpa((double?)null, PressureUnit.Bar));
        // psi 进出不经 MPa 来回换算
        Assert.Equal(50, PressureUnits.FromPsi(50, PressureUnit.Psi));
        Assert.Equal(50, PressureUnits.ToPsi(50, PressureUnit.Psi));
        Assert.Equal(50 / 14.5037738, PressureUnits.FromPsi(50, PressureUnit.Bar), 9);
        Assert.Equal(50, PressureUnits.ToPsi(PressureUnits.FromPsi(50, PressureUnit.Bar), PressureUnit.Bar), 9);
    }

    [Fact]
    public void Keys_ParseNormalizeAndDecimals()
    {
        Assert.Equal(new[] { "MPa", "bar", "psi" }, PressureUnits.All.Select(PressureUnits.Text));
        Assert.Equal(PressureUnit.Bar, PressureUnits.Parse("BAR"));
        Assert.Equal(PressureUnit.Psi, PressureUnits.Parse("psi"));
        Assert.Equal(PressureUnit.MPa, PressureUnits.Parse("kPa"));
        Assert.Equal(PressureUnit.MPa, PressureUnits.Parse(null));
        Assert.True(PressureUnits.IsKnown("Psi"));
        Assert.False(PressureUnits.IsKnown("kPa"));
        Assert.Equal("MPa", PressureUnits.Normalize("mpa"));
        Assert.Equal((3, 2, 1), (PressureUnits.ValueDecimals(PressureUnit.MPa, 3), PressureUnits.ValueDecimals(PressureUnit.Bar, 3), PressureUnits.ValueDecimals(PressureUnit.Psi, 3)));
        Assert.Equal((2, 1, 0), (PressureUnits.InputDecimals(PressureUnit.MPa), PressureUnits.InputDecimals(PressureUnit.Bar), PressureUnits.InputDecimals(PressureUnit.Psi)));
        Assert.Equal((0.01, 0.1, 1.0), (PressureUnits.InputStep(PressureUnit.MPa), PressureUnits.InputStep(PressureUnit.Bar), PressureUnits.InputStep(PressureUnit.Psi)));
    }

    [Fact]
    public void Set_RaisesChangedOnceAndBumpsVersion()
    {
        int calls = 0;
        void On() => calls++;
        PressureDisplay.Changed += On;
        try
        {
            int v0 = PressureDisplay.Version;
            Assert.True(PressureDisplay.Set("psi"));
            Assert.False(PressureDisplay.Set(PressureUnit.Psi));          // 没变：不发
            Assert.Equal((PressureUnit.Psi, "psi", "MPa", 1, v0 + 1), (PressureDisplay.Primary, PressureDisplay.Unit, PressureDisplay.SecondaryUnit, calls, PressureDisplay.Version));
            Assert.True(PressureDisplay.Set("whatever"));                 // 认不出 = MPa
            Assert.Equal((PressureUnit.MPa, "psi", 2), (PressureDisplay.Primary, PressureDisplay.SecondaryUnit, calls));
        }
        finally { PressureDisplay.Changed -= On; }
    }

    // ———————————————————— 格式化 ————————————————————

    [Fact]
    public void Mpa_IsByteIdenticalToTheOldFormatting()
    {
        Assert.Equal("0.350 MPa", PressureDisplay.WithUnit(Pr8, 0.35));
        Assert.Equal("5.20 MPa", PressureDisplay.WithUnit(Pr4, 5.2));
        Assert.Equal("2.35", PressureDisplay.Fixed(2.349, 2));
        Assert.Equal("3.12 MPa", PressureDisplay.FixedU(3.1234, 2));
        Assert.Equal("0.000", PressureDisplay.Value(Pr8, -0.0004));
        Assert.Equal(Fmt.Dash, PressureDisplay.Fixed(null, 2));
        Assert.Equal(Fmt.Dash, PressureDisplay.FixedU(double.NaN, 2));
        Assert.Equal("4.14 MPa", PressureDisplay.LimitU(4.14));
        Assert.Equal("0.35 MPa", PressureDisplay.InputU(0.35));
        Assert.Equal("压力 MPa", PressureDisplay.Label("压力"));
        Assert.Equal("压力 (MPa)", PressureDisplay.LabelParen("压力"));
        Assert.Equal("50.8 psi", PressureDisplay.SecondaryWithUnit(Pr8, 0.35));
        Assert.Equal("≤ 200 ℃ · ≤ 4.14 MPa", Pr8.RangeNote);
    }

    [Fact]
    public void Bar_TwoDecimals_Psi_OneDecimal()
    {
        PressureDisplay.Set(PressureUnit.Bar);
        Assert.Equal("3.50 bar", PressureDisplay.WithUnit(Pr8, 0.35));          // 型号 3 位小数在 bar 下 2 位
        Assert.Equal("52.00 bar", PressureDisplay.WithUnit(Pr4, 5.2));
        Assert.Equal("23.50", PressureDisplay.Fixed(2.35, 2));
        Assert.Equal("41.4 bar", PressureDisplay.LimitU(4.14));
        Assert.Equal("3.5 bar", PressureDisplay.InputU(0.35));
        Assert.Equal("50.8 psi", PressureDisplay.SecondaryWithUnit(Pr8, 0.35));
        Assert.Equal("0.00", PressureDisplay.Value(Pr8, -0.0002));               // 零点漂移不写负号
        Assert.Equal("≤ 200 ℃ · ≤ 41.4 bar", Pr8.RangeNote);

        PressureDisplay.Set(PressureUnit.Psi);
        Assert.Equal("50.8 psi", PressureDisplay.WithUnit(Pr8, 0.35));
        Assert.Equal("340.8", PressureDisplay.Fixed(2.35, 2));                   // 2.35 × 145.037738 = 340.84
        Assert.Equal("514.9 psi", PressureDisplay.FixedU(3.55, 2));
        Assert.Equal("600 psi", PressureDisplay.LimitU(4.14));                   // 600.46 取整
        Assert.Equal("51 psi", PressureDisplay.InputU(0.35));
        Assert.Equal("0.350 MPa", PressureDisplay.SecondaryWithUnit(Pr8, 0.35)); // psi 为主时次单位是 MPa
        Assert.Equal("0.0", PressureDisplay.Value(Pr8, -0.0002));
        Assert.Equal("-0.1", PressureDisplay.Value(Pr8, -0.001034));             // −0.150 psi
        Assert.Equal("压力 psi", PressureDisplay.Label("压力"));
        Assert.Equal(50.0, PressureDisplay.ToUnit(PressureDisplay.FromUnit(50)), 9);
    }

    // ———————————————————— 程序页的参数 ————————————————————

    private static ProgramSchema Schema => ProgramSchema.Default(new SchemaLimits(200, 250, 450, 500, 510));   // 升压上限 min(493.1, 500, 510 × 0.9 − 0.1) = 458.9 psi

    [Fact]
    public void FieldSpec_Mpa_Unchanged()
    {
        var p = Schema.Field(StepType.Press, "pres")!;
        Assert.True(p.IsPressure);
        Assert.Equal(("MPa", "MPa", 2, 2), (p.Unit, p.ShowUnit, p.Dp, p.ShowDp));
        Assert.Equal((0.01, 3.16), (p.Min, p.Max));
        Assert.Equal("0.01 – 3.16", p.RangeText);
        Assert.Equal("0.35", p.Num(0.35));
        Assert.Equal(0.35, p.FromInput(0.351));
        Assert.Equal(0.34, p.NewValue());                 // 50 psi = 0.3447 MPa，取到 0.01
        Assert.False(p.Bad(3.16));
        Assert.True(p.Bad(3.17));
        var temp = Schema.Field(StepType.Heat, "temp")!;
        Assert.False(temp.IsPressure);
        Assert.Equal(("°C", "35 – 200", "150"), (temp.ShowUnit, temp.RangeText, temp.Num(150)));
    }

    [Fact]
    public void FieldSpec_Psi_RangeFromTheHmiPsiLimits_InputWholePsi()
    {
        PressureDisplay.Set(PressureUnit.Psi);
        var p = Schema.Field(StepType.Press, "pres")!;
        Assert.Equal(("MPa", "psi", 0), (p.Unit, p.ShowUnit, p.ShowDp));       // 内部照旧 MPa
        Assert.Equal("1 – 458", p.RangeText);                                    // HMI 的 1 psi 下限原样，不经 0.01 MPa 变成 2
        Assert.Equal(50, p.Shown(p.NewValue()), 9);                              // 新步骤默认正好 50 psi
        Assert.Equal("51", p.Num(0.35));
        double v = p.FromInput(50.4);                                            // 输入按整数 psi
        Assert.Equal(50, PressureUnits.FromMpa(v, PressureUnit.Psi), 9);
        Assert.False(p.Bad(p.FromInput(1)));
        Assert.False(p.Bad(p.FromInput(458)));
        Assert.True(p.Bad(p.FromInput(459)));
        Assert.True(p.Bad(double.NaN));
        // 下发：MPa → psi（1 位小数），输入的 50 psi 下发仍是 50.0
        var st = new PcStep(StepType.Press, "升压");
        st.Num["pres"] = v;
        Assert.Equal(50.0, PcProgram.StepDoc(st).Params["pres"].GetDouble());
        // 矩阵等差在 psi 里取整
        Assert.Equal(73, p.Shown(p.RoundShown(PressureUnits.ToMpa(72.6, PressureUnit.Psi))), 9);
    }

    [Fact]
    public void FieldSpec_Bar_OneDecimal()
    {
        PressureDisplay.Set(PressureUnit.Bar);
        var p = Schema.Field(StepType.Press, "pres")!;
        Assert.Equal(("bar", 1), (p.ShowUnit, p.ShowDp));
        Assert.Equal("0.1 – 31.6", p.RangeText);                                 // 1 psi = 0.069 bar 向上、458.9 psi = 31.64 bar 向下
        Assert.Equal("3.5", p.Num(0.35));
        Assert.Equal(3.4, p.Shown(p.NewValue()), 9);                              // 50 psi = 3.447 bar → 3.4
        Assert.Equal(0.25, p.FromInput(2.5), 12);
    }

    [Fact]
    public void FromDoc_RoundsInTheDisplayUnit()
    {
        var doc = new ProgramDoc { Name = "x" };
        var press = new ProgramStepDoc("press");
        press.Params["pres"] = JsonSerializer.SerializeToElement(50.0);
        doc.Steps.Add(press);
        Assert.Equal(0.34, PcProgram.FromDoc(doc, "PR8", "a").Steps[0].N("pres"));               // MPa：同改单位之前
        PressureDisplay.Set(PressureUnit.Psi);
        var back = PcProgram.FromDoc(doc, "PR8", "b");
        Assert.Equal(50, PressureUnits.FromMpa(back.Steps[0].N("pres"), PressureUnit.Psi), 9);   // psi：仍是 50 psi
        Assert.Equal(50.0, PcProgram.StepDoc(back.Steps[0]).Params["pres"].GetDouble());
    }

    [Fact]
    public void PlannerSummary_FollowsTheUnit()
    {
        var st = new PcStep(StepType.Press, "升压");
        st.Num["pres"] = 0.35;
        st.Sel["regulate"] = "on";
        Assert.Equal("0.35 MPa", ProgramPlanner.Summary(st, Schema)[0].V);
        PressureDisplay.Set(PressureUnit.Bar);
        Assert.Equal("3.5 bar", ProgramPlanner.Summary(st, Schema)[0].V);
        PressureDisplay.Set(PressureUnit.Psi);
        Assert.Equal("51 psi", ProgramPlanner.Summary(st, Schema)[0].V);
    }

    // ———————————————————— 曲线 ————————————————————

    [Fact]
    public void ReactorChart_ScalesPressureSeriesAxisAndLimit()
    {
        var now = new DateTime(2026, 11, 15, 14, 30, 0, DateTimeKind.Local);
        var pts = Enumerable.Range(0, 30).Select(i => ChartPoint.At(now.AddMinutes(-29 + i), 0.35 + i * 0.01)).ToArray();
        var mpa = ReactorChartOptions.Build(Pr8, 1, now, 30, Array.Empty<ChartPoint>(), Array.Empty<ChartPoint>(), pts, Array.Empty<ChartPoint>(), null, 4.0, alarm: false);
        PressureDisplay.Set(PressureUnit.Psi);
        var psi = ReactorChartOptions.Build(Pr8, 1, now, 30, Array.Empty<ChartPoint>(), Array.Empty<ChartPoint>(), pts, Array.Empty<ChartPoint>(), null, 4.0, alarm: false);
        double k = PressureUnits.PerMpa(PressureUnit.Psi);
        Assert.Equal(("压力 MPa", "MPa"), (mpa.YAxes[1].Label, mpa.Series[2].Unit));
        Assert.Equal(("压力 psi", "psi", 1), (psi.YAxes[1].Label, psi.Series[2].Unit, psi.YAxes[1].Dec));
        Assert.Equal(pts[5].V * k, psi.Series[2].Points[5].V, 9);
        // 分带的范围与上限线按同一个倍数换算（形状不变）
        Assert.Equal(mpa.YAxes[1].Min!.Value * k, psi.YAxes[1].Min!.Value, 6);
        Assert.Equal(mpa.YAxes[1].Max!.Value * k, psi.YAxes[1].Max!.Value, 6);
        Assert.Equal(mpa.RefLines[0].V * k, psi.RefLines[0].V, 9);
        Assert.Equal("上限 600.5 psi", psi.RefLines[0].Label);
        Assert.Equal("上限 4.140 MPa", mpa.RefLines[0].Label);
    }

    [Fact]
    public void CurveAxis_NiceStepsInBarAndPsi()
    {
        // MPa：同改单位之前（0 – 4，每 1）
        Assert.Equal(new CurveAxis(0, 4, 1), CurveStats.Axis(HistoryField.PressureMPa, new[] { 2.35, 3.55 }, 3.5));
        double k = PressureUnits.PerMpa(PressureUnit.Psi);
        var psi = CurveStats.Axis(HistoryField.PressureMPa, new[] { 2.35 * k, 3.55 * k }, 3.5 * k, k);
        Assert.Equal((0.0, 600.0, 200.0), (psi.Lo, psi.Hi, psi.Step));
        var bar = CurveStats.Axis(HistoryField.PressureMPa, new[] { 23.5, 35.5 }, 35, 10);
        Assert.Equal((0.0, 40.0, 10.0), (bar.Lo, bar.Hi, bar.Step));
        // 没有数据：至少 1 MPa（145.04 psi）× 1.04，按 50 psi 取整（MPa 时是 1.5，每 0.5）
        Assert.Equal(new CurveAxis(0, 1.5, 0.5), CurveStats.Axis(HistoryField.PressureMPa, Array.Empty<double>()));
        Assert.Equal(new CurveAxis(0, 200, 50), CurveStats.Axis(HistoryField.PressureMPa, Array.Empty<double>(), null, k));
    }

    // ———————————————————— 报警页 / 断行 ————————————————————

    [Fact]
    public void AlarmSplitValue_ConvertsPressureReadings()
    {
        const string text = "原状态 反应中 · 压力 3.55 MPa ≥ 超压报警值 3.50 MPa";
        Assert.Equal(("3.55 MPa", "上限 3.50 MPa", "原状态 反应中"), AlarmLog.SplitValue(text));
        Assert.Equal(("51.5 psi", "上限 50.8 psi"), Vl(AlarmLog.SplitValue("压力 51.5 psi ≥ 50.8 psi")));   // MPa 时原文照写
        PressureDisplay.Set(PressureUnit.Bar);
        Assert.Equal(("35.50 bar", "上限 35.00 bar", "原状态 反应中"), AlarmLog.SplitValue(text));
        PressureDisplay.Set(PressureUnit.Psi);
        Assert.Equal(("514.9 psi", "上限 507.6 psi"), Vl(AlarmLog.SplitValue(text)));
        Assert.Equal(("51.5 psi", "上限 50.8 psi"), Vl(AlarmLog.SplitValue("压力 51.5 psi ≥ 50.8 psi")));   // 已经是 psi：原样
        Assert.Equal(("205.3 °C", "上限 200 °C"), Vl(AlarmLog.SplitValue("PV 205.3℃ ≥ 超温限值 200℃")));  // 其它单位不动
        static (string?, string?) Vl((string? V, string? L, string O) x) => (x.V, x.L);
    }

    [Fact]
    public void Keep_DoesNotBreakBetweenNumberAndBar()
    {
        Assert.Equal("35.50\u00A0bar", Keep.Nbsp("35.50 bar"));
        Assert.Equal("3.12\u00A0MPa", Keep.Nbsp("3.12 MPa"));
    }
}
