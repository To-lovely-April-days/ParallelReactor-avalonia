using System;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Alarms;
using ParallelReactor.Control.Pages.Curves;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Pages.Records;
using ParallelReactor.Control.Pages.Settings;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>压力单位是进程级的（PressureDisplay）：切单位的测试放在不并行的集合里（其它测试都跑完再跑），每条结束改回 MPa。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PressureUnitAppCollection
{
    public const string Name = "压力单位（App · 进程级）";
}

/// <summary>
/// 设置 → 常规「压力单位」（MPa / bar / psi）：在设置页切换后全界面马上换算（不重启、不等保存）——主页运行表、报警弹窗、工具条量程、曲线页、报警页、
/// 程序页条件矩阵（单位、范围、超范围的说明、输入换回 MPa 再按 psi 下发）、设置页的设备参数、记录页保压类。
/// 数据 = 融合版演示数据（fusion=1，2026-11-15 14:30:07；1号机 RV1 2.35 / 2.50 MPa、RV4 超压 3.55 ≥ 3.50 MPa）。
/// </summary>
[Collection(PressureUnitAppCollection.Name)]
public sealed class PressureUnitAppTests : IDisposable
{
    private static readonly double Psi = PressureUnits.PerMpa(PressureUnit.Psi);

    public PressureUnitAppTests() => PressureDisplay.Set(PressureUnit.MPa);

    public void Dispose() => PressureDisplay.Set(PressureUnit.MPa);

    private static ShellVm Shell(string query)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("apop=0&" + query + "&" + p));
        return s;
    }

    [Fact]
    public void SettingsSwitch_UpdatesTheWholeUiLive()
    {
        var s = Shell("page=set&dev=1");
        var set = Assert.IsType<SettingsPageVm>(s.Page);
        Assert.EndsWith("≤ 4.14 MPa", s.Ribbon.ModelText);

        // 切到 bar：当前值马上生效（没保存也算），外框和主页跟着变
        set.UnitKey = "bar";
        Assert.Equal(("bar", PressureUnit.Bar, true), (s.Prefs.Current.PressureUnit, PressureDisplay.Primary, set.IsDirty));
        Assert.EndsWith("≤ 41.4 bar", s.Ribbon.ModelText);
        Assert.Equal("压力 bar", PressureText.Instance.Header);
        s.Nav.Set(AppPage.Home, 1);
        var h = Assert.IsType<HomePageVm>(s.Page);
        RunRowVm R(int n) => h.Rows.Single(r => r.N == n);
        Assert.Equal(("23.50", "25.00"), (R(1).PressText, R(1).PSpText));
        Assert.Equal(" bar", h.Tiles[0].BarPUnit);

        // 再切到 psi（设置页上切，主页不用重开）
        s.OpenSettings("gen");
        Assert.IsType<SettingsPageVm>(s.Page).UnitKey = "psi";
        s.Nav.Set(AppPage.Home, 1);
        Assert.Equal(("340.8", "362.6"), (R(1).PressText, R(1).PSpText));       // 2.35 / 2.50 MPa × 145.04
        Assert.EndsWith("≤ 600 psi", s.Ribbon.ModelText);
        s.AlarmPop.Reset(false);
        Assert.Equal(new[] { "当前压力 514.9 psi", "报警上限 507.6 psi", "设定压力 362.6 psi" }, s.AlarmPop.Tiles.Select(t => t.Label + " " + t.Value + " " + t.Unit));
        Assert.Equal("压力 psi · 近 60 min", s.AlarmPop.TrendCap);
        Assert.Equal("340.8", s.Fleet.Device(1)!.Reactor(1)!.PressText);              // 按型号的读数：psi 一律 1 位

        // 恢复默认 = MPa：全部回到原来的写法
        s.OpenSettings("gen");
        s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "恢复默认").Command.Execute(null);
        Assert.Equal(PressureUnit.MPa, PressureDisplay.Primary);
        s.Nav.Set(AppPage.Home, 1);
        Assert.Equal(("2.35", "2.50"), (R(1).PressText, R(1).PSpText));
        Assert.EndsWith("≤ 4.14 MPa", s.Ribbon.ModelText);
    }

    [Fact]
    public void Curves_InPsi_AxisReadoutStatsAndTip()
    {
        var s = Shell("punit=psi&page=curve&dev=1&cvcur=13:53");
        var c = Assert.IsType<CurvePageVm>(s.Page);
        var rv1 = c.Channels.Single(x => x.N == 1);
        Assert.Equal("340.8", rv1.Press);
        var p = Assert.IsType<CurvePanelData>(c.PData);
        Assert.Equal(("压力", "psi", 1), (p.Title, p.Unit, p.Decimals));
        Assert.Equal(3.5 * Psi, p.Limit!.Value, 6);
        Assert.Equal((0.0, 600.0, 200.0, 0), (p.Axis.Lo, p.Axis.Hi, p.Axis.Step, p.AxisDec));    // 数据 ≤ 514.9 psi + 上限 507.6
        Assert.All(p.Lines.SelectMany(l => l.Points.Take(l.Count)).Where(x => double.IsFinite(x.V)), x => Assert.InRange(x.V, 0, 600));
        var tip = Assert.IsType<CurveTipData>(c.Tip);
        Assert.Equal(new[] { "°C", "psi" }, tip.Units);
        Assert.Equal(c.Rows[0].P, tip.Rows[0].Values[1]);
        Assert.Matches(@"^\d+\.\d$", c.Rows[0].P);
        Assert.Matches(@"^\d+\.\d$", c.Rows[0].PMax);
    }

    [Fact]
    public void Curves_InBar_AxisSteps()
    {
        var c = Assert.IsType<CurvePageVm>(Shell("punit=bar&page=curve&dev=1").Page);
        Assert.Equal("23.50", c.Channels.Single(x => x.N == 1).Press);
        var p = c.PData!;
        Assert.Equal(("bar", 2, 40.0, 10.0), (p.Unit, p.Decimals, p.Axis.Hi, p.Axis.Step));
        Assert.Equal(35.0, p.Limit!.Value, 9);
    }

    [Fact]
    public void Alarms_ValuesAndLimits_FollowTheUnit()
    {
        var a = Assert.IsType<AlarmPageVm>(Shell("punit=bar&page=alarm&dev=1").Page);
        var sel = Assert.Single(a.Rows, r => r.IsSelected);
        Assert.Equal(("RV4", "35.50 bar", "上限 35.00 bar"), (sel.Src, sel.Val, sel.Lim));
        Assert.Equal("205.3 °C", a.Rows.Last().Val);                                     // 温度不动
        Assert.Equal("RV4 压力 (bar) · 报警前后", a.Spark?.Title);
    }

    [Fact]
    public void ProgramMatrix_InPsiAndBar_UnitsRangesAndInputs()
    {
        var s = Shell("punit=psi&page=prog&dev=1");
        var pg = Assert.IsType<ProgramPageVm>(s.Page);
        Assert.True(pg.IsMatrix);
        var pres = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "pres");
        Assert.Equal("psi", pres.Unit);
        Assert.True(pres.Cells[3].Bad);
        Assert.Equal("580", pres.Cells[3].Text);                                          // 4.00 MPa = 580.2 psi，整数 psi
        string range = pres.Spec.RangeText;
        Assert.StartsWith("8 – ", range);                                                  // HMI 的 7.3 psi 下限（整数 psi 向上）
        Assert.Equal("第 06 步目标压力：RV4 580 psi · 超出 HT-PR8 范围 " + range + " psi", pg.Mx.Issues[0].Msg);
        Assert.Equal("RV4：超出 HT-PR8 范围 " + range + " psi", pres.Cells[3].Tip);
        // 输入 435 psi：存 MPa，不再超范围，下发仍是 435.0 psi
        pres.Cells[3].Text = "435";
        Assert.False(pres.Cells[3].Bad);
        Assert.Equal(435, PressureUnits.FromMpa(pg.Mx.Plan!.PerValue(5, "pres", 4), PressureUnit.Psi), 9);
        var st = pg.Mx.Plan.StepsFor(4)[5];
        Assert.Equal(435.0, PcProgram.StepDoc(st).Params["pres"].GetDouble());

        // 同一个页面上切到 bar（设置页切换的路径）：行重建，单位 / 小数位跟着变
        s.OpenSettings("gen");
        Assert.IsType<SettingsPageVm>(s.Page).UnitKey = "bar";
        s.Nav.Set(AppPage.Program, 1);
        pres = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "pres");
        Assert.Equal("bar", pres.Unit);
        Assert.Equal("30.0", pres.Cells[3].Text);                                        // 435 psi = 29.99 bar
        Assert.StartsWith("0.6 – ", pres.Spec.RangeText);                                // 7.3 psi = 0.503 bar 向上
    }

    [Fact]
    public void ProgramOne_PressureField_InBar()
    {
        var s = Shell("punit=bar&page=prog&dev=1&pgm=one");
        var pg = Assert.IsType<ProgramPageVm>(s.Page);
        Assert.False(pg.IsMatrix);
        var press = pg.One.Steps.First(x => x.TypeLabel == "充压");
        Assert.Matches(@"^\d+\.\d bar$", press.K1 == "目标压力" ? press.V1 : press.V2);          // 步骤列表的摘要
        press.PickCommand.Execute(null);
        var f = pg.One.Fields.Single(x => x.Spec.Key == "pres");
        Assert.Equal("bar", f.Unit);
        Assert.Matches(@"^\d+\.\d$", f.Text);
        Assert.Matches(@"^0\.6 – \d+\.\d$", f.Range);                                // bar 不写「≈ psi」（放不下；MPa 照旧写）
    }

    [Fact]
    public void Settings_DeviceLimits_InPsi()
    {
        var v = Assert.IsType<SettingsPageVm>(Shell("punit=psi&page=set&dev=1&set=lim").Page);
        SetRoRow Row(string l) => v.LimGroups.SelectMany(g => g.Rows).Single(r => r.Label == l);
        Assert.Equal("508 psi（3.50 MPa）", Row("超压报警").Value);
        Assert.Equal("145 psi（1.00 MPa）", Row("测试压力").Value);
        Assert.Equal(("2.0", "psi/h"), (Row("泄漏率阈值").Value, Row("泄漏率阈值").Unit));     // 泄漏率照旧 psi/h
        Assert.Equal("psi", Row("变送器满量程").Unit);
        Assert.Contains("0.145 psi", PressureText.Instance.UptNote);
    }

    [Fact]
    public void Records_Hold_TestPressureInBar_DropsStayPsiPerHour()
    {
        var r = Assert.IsType<RecordsPageVm>(Shell("punit=bar&page=rec&dev=0&rc=LK-20261115-0912").Page);
        Assert.True(r.IsHold);
        Assert.Equal("bar", r.Kpis[1].Unit);
        Assert.Matches(@"^3\.\d\d$", r.Kpis[1].Value);
        Assert.Equal("psi/h", r.Kpis[3].Unit);
        Assert.Equal(("压力", "bar"), (r.Chart!.Title, r.Chart.Unit));
        Assert.Equal(10, r.Chart.Scale);
        Assert.EndsWith(" kPa/h", r.Rows[0].KpaText);
    }
}
