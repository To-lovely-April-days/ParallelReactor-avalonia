using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F6 程序页的纯逻辑（程序步骤 v0.8，STEPS-SPEC.md）：程序 schema（HMI 的 / 本机生成：键、默认、范围、visibleWhen）、程序估计（§5）、待改项、
/// 下发前检查（§4）、HMI 程序 JSON 换算（MPa ↔ psi）、旧格式转换（§3，程序库 / 草稿）、推荐模板、
/// 条件矩阵（建、检查、统一 / 等差、升压排队、转速共用、CSV、方案 JSON）、演示设备下发自定义程序。
/// </summary>
public class F6EditorTests
{
    /// <summary>HT-PR8 演示：温度上限 200、超温 220、转速 450、压力 SP 上限 4.14 MPa、超压 3.50 MPa。</summary>
    private static readonly SchemaLimits Pr8 = new(200, 220, 450, 4.14 * 145.0377, 3.5 * 145.0377);

    private static ProgramSchema Schema => ProgramSchema.Default(Pr8);

    private static PcProgram Lib(string id) => DemoPrograms.Library().First(p => p.Id == id);

    // ———————————————————— schema ————————————————————

    [Fact]
    public void DefaultSchema_FollowsHmiRules()
    {
        var s = Schema;
        // 程序步骤 v0.8：九种，编辑器的顺序
        Assert.Equal(new[] { StepType.Leak, StepType.Exchange, StepType.Stir, StepType.Heat, StepType.Press, StepType.Hold, StepType.React, StepType.Vent, StepType.End }, s.Steps.Select(x => x.Type));
        var temp = s.Field(StepType.Heat, "temp")!;
        Assert.Equal((35.0, 200.0), (temp.Min, temp.Max));
        var p = s.Field(StepType.Press, "pres")!;
        Assert.Equal("MPa", p.Unit);
        Assert.Equal(2, p.Dp);
        Assert.Equal(0.06, p.Min);               // 7.3 psi，向上取到 0.01
        Assert.Equal(3.14, p.Max);               // 3.50 × 0.9 − 0.1 psi，向下取到 0.01（保证 HMI 收）
        Assert.Equal("0.06 – 3.14", p.RangeText);
        Assert.Equal(2.0, p.NewValue());         // 290.1 psi
        Assert.Equal(450, s.Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(new[] { "fixed", "curve" }, s.Field(StepType.Heat, "mode")!.Options.Select(o => o.Code));
        Assert.Equal("0.01 – 100", s.Field(StepType.React, "mmol")!.RangeText);
        Assert.Empty(s.Of(StepType.End)!.Fields);
        Assert.Null(s.Of(StepType.Purge));
        Assert.Null(s.Of(StepType.Wait));
    }

    /// <summary>STEPS-SPEC §1：每种步骤的键、默认值（新建步骤）、范围；压力字段（MPa）与压降速率（MPa/h）；visibleWhen。</summary>
    [Fact]
    public void DefaultSchema_KeysDefaultsAndRanges_PerType()
    {
        var s = Schema;
        string Keys(StepType t) => string.Join(",", s.Of(t)!.Fields.Select(f => f.Key));
        Assert.Equal("pres,settle,hold,maxDrop,onFail,after", Keys(StepType.Leak));
        Assert.Equal("gas,fill,release,cycles,lineFlush,maxSec", Keys(StepType.Exchange));
        Assert.Equal("rpm", Keys(StepType.Stir));
        Assert.Equal("mode,temp,rate,band,wait,maxWait,onTimeout", Keys(StepType.Heat));
        Assert.Equal("gas,pres,tol,regulate,refill,maxSec", Keys(StepType.Press));
        Assert.Equal("until,time,temp,pres,maxWait,onTimeout", Keys(StepType.Hold));
        Assert.Equal("time,minTime,mmolOn,mmol,stallOn,stallRate,stallMin", Keys(StepType.React));
        Assert.Equal("to,maxSec,flush,flushPres", Keys(StepType.Vent));
        // 压力字段：内部 MPa
        foreach (var (t, k) in new[] { (StepType.Leak, "pres"), (StepType.Exchange, "fill"), (StepType.Exchange, "release"), (StepType.Press, "pres"), (StepType.Hold, "pres"), (StepType.Vent, "to"), (StepType.Vent, "flushPres") })
            Assert.True(s.Field(t, k)!.IsPressure, t + "." + k);
        Assert.True(s.Field(StepType.Leak, "maxDrop")!.IsRate);
        // 新建步骤的默认值（MPa 下）
        string D(StepType t) { var x = PcStep.New(t, s); return string.Join(" ", x.Sel.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value).Concat(x.Num.OrderBy(k => k.Key).Select(k => k.Key + "=" + Math.Round(k.Value, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)))); }
        Assert.Equal("after=vent onFail=stop hold=30 maxDrop=0.0138 pres=3 settle=5", D(StepType.Leak));
        Assert.Equal("gas=inert cycles=3 fill=0.5 lineFlush=2 maxSec=60 release=0.02", D(StepType.Exchange));
        Assert.Equal("rpm=400", D(StepType.Stir));
        Assert.Equal("mode=fixed onTimeout=stop band=2 maxWait=120 rate=0 temp=80 wait=15", D(StepType.Heat));
        Assert.Equal("gas=A regulate=on maxSec=180 pres=2 refill=3 tol=2", D(StepType.Press));
        Assert.Equal("onTimeout=continue until=time maxWait=60 pres=0.1 temp=40 time=10", D(StepType.Hold));
        Assert.Equal("mmolOn=off stallOn=on minTime=30 mmol=5 stallMin=30 stallRate=0.005 time=480", D(StepType.React));
        Assert.Equal("flush=3 flushPres=0.3 maxSec=120 to=0.02", D(StepType.Vent));
        Assert.Equal("", D(StepType.End));
        // 范围（界面单位 MPa）
        string R(StepType t, string k) => s.Field(t, k)!.RangeText;
        Assert.Equal(("0.1 – 3.14", "0 – 60", "5 – 600", "0.0007 – 0.6894"), (R(StepType.Leak, "pres"), R(StepType.Leak, "settle"), R(StepType.Leak, "hold"), R(StepType.Leak, "maxDrop")));
        Assert.Equal(("0.06 – 3.14", "0 – 3.14", "0 – 20", "0 – 10", "10 – 300"), (R(StepType.Exchange, "fill"), R(StepType.Exchange, "release"), R(StepType.Exchange, "cycles"), R(StepType.Exchange, "lineFlush"), R(StepType.Exchange, "maxSec")));
        Assert.Equal("0 – 450", R(StepType.Stir, "rpm"));
        Assert.Equal(("0 – 20", "0.5 – 10", "0 – 120", "10 – 600"), (R(StepType.Heat, "rate"), R(StepType.Heat, "band"), R(StepType.Heat, "wait"), R(StepType.Heat, "maxWait")));
        Assert.Equal(("0.5 – 10", "1 – 10", "30 – 600"), (R(StepType.Press, "tol"), R(StepType.Press, "refill"), R(StepType.Press, "maxSec")));
        Assert.Equal(("0 – 1440", "0 – 200", "0 – 3.14", "1 – 1440"), (R(StepType.Hold, "time"), R(StepType.Hold, "temp"), R(StepType.Hold, "pres"), R(StepType.Hold, "maxWait")));
        Assert.Equal(("1 – 4320", "0.001 – 1", "5 – 240"), (R(StepType.React, "time"), R(StepType.React, "stallRate"), R(StepType.React, "stallMin")));
        Assert.Equal(("0 – 0.49", "10 – 600", "0 – 10", "0.06 – 3.14"), (R(StepType.Vent, "to"), R(StepType.Vent, "maxSec"), R(StepType.Vent, "flush"), R(StepType.Vent, "flushPres")));
        // 转速上限按设备（釜转速）；默认 400 不超过上限
        var low = ProgramSchema.Default(Pr8 with { RpmMax = 300 });
        Assert.Equal((300.0, 300.0), (low.Field(StepType.Stir, "rpm")!.Max, PcStep.New(StepType.Stir, low).N("rpm")));
        // 允许压降的默认 = 设置的泄漏率阈值
        Assert.Equal(5.0, Units.MpaToPsi(PcStep.New(StepType.Leak, ProgramSchema.Default(Pr8 with { LeakRatePsiH = 5 })).N("maxDrop")), 1);
    }

    [Fact]
    public void Schema_VisibleWhen()
    {
        var s = Schema;
        bool V(PcStep st, string key) => s.Field(st.Type, key)!.Visible(st);
        var hold = PcStep.New(StepType.Hold, s);
        Assert.Equal((true, false, false, false), (V(hold, "time"), V(hold, "temp"), V(hold, "pres"), V(hold, "maxWait")));
        hold.Sel["until"] = "tempBelow";
        Assert.Equal((false, true, false, true), (V(hold, "time"), V(hold, "temp"), V(hold, "pres"), V(hold, "maxWait")));
        hold.Sel["until"] = "pressAbove";
        Assert.Equal((false, false, true, true), (V(hold, "time"), V(hold, "temp"), V(hold, "pres"), V(hold, "onTimeout")));
        var heat = PcStep.New(StepType.Heat, s);
        Assert.True(V(heat, "temp") && V(heat, "rate") && V(heat, "wait") && V(heat, "band"));
        heat.Sel["mode"] = "curve";
        Assert.True(!V(heat, "temp") && !V(heat, "rate") && !V(heat, "wait") && V(heat, "band") && V(heat, "maxWait"));
        var press = PcStep.New(StepType.Press, s);
        Assert.True(V(press, "refill"));
        press.Sel["regulate"] = "off";
        Assert.False(V(press, "refill"));
        var react = PcStep.New(StepType.React, s);
        Assert.Equal((false, true, true), (V(react, "mmol"), V(react, "stallRate"), V(react, "stallMin")));
        // 同 HMI：置换的充到 / 放到、放空的置换充到一直显示（只吹总管时写说明）
        var ex = PcStep.New(StepType.Exchange, s);
        ex.Num["cycles"] = 0;
        Assert.True(V(ex, "fill") && V(ex, "release"));
        Assert.Equal("次数 0 = 只吹总管，釜里没有置换（充到 / 放到不起作用）", s.Of(StepType.Exchange)!.NoteFor(ex));
        var vent = PcStep.New(StepType.Vent, s);
        vent.Num["flush"] = 0;
        Assert.True(V(vent, "flushPres"));
    }

    [Fact]
    public void HmiSchema_FromLiveSample_ConvertsPressureAndCapsRpm()
    {
        // v0.7 的 HMI（旧 schema）：旧码 purge / wait 不进 schema，其余照旧换算
        var dto = JsonSerializer.Deserialize<ProgramSchemaDto>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "live-program-schema.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var s = ProgramSchema.FromDto(dto);
        Assert.Equal("hmi", s.Source);
        Assert.Equal(new[] { StepType.Stir, StepType.Heat, StepType.Press, StepType.React, StepType.End }, s.Steps.Select(x => x.Type));
        var p = s.Field(StepType.Press, "pres")!;
        Assert.Equal("MPa", p.Unit);
        Assert.Equal(1.37, p.Max);               // HMI 的 StepDef 声明 0–200 psi
        Assert.Equal(0.01, p.Min);
        Assert.Equal(1000, s.Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(450, s.Cap(450).Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(3, s.Of(StepType.Heat)!.SegmentFields!.Count);
    }

    /// <summary>v0.8 的 HMI：压力字段 unit = psi（全部换成 MPa，原始 psi 限值留着），压降 psi/h → MPa/h；visibleWhen 按本机的表。</summary>
    [Fact]
    public void HmiSchema_V08_PressureFieldsAndVisibility()
    {
        static FieldSchemaDto N(string k, string unit, double def, double lo, double hi) => new(k, k, "num", unit, def, lo, hi, lo, hi, null, true, null, null, null, null);
        static FieldSchemaDto Sel(string k, params string[] codes) => new(k, k, "sel", null, null, null, null, null, null, codes.Select(c => new SchemaOption(c, c)).ToArray(), true, null, null, null, null);
        var dto = new ProgramSchemaDto("0.8", 3, new[] { "leak", "hold", "vent", "end" }, new[]
        {
            new StepSchemaDto("leak", "气密检查", "", "", new[] { N("pres", "psi", 435.1, 14.5, 458.9), N("maxDrop", "psi/h", 2, 0.1, 100) }),
            new StepSchemaDto("hold", "保持", "", "", new[] { Sel("until", "time", "tempAbove", "tempBelow", "pressAbove", "pressBelow"), N("time", "min", 10, 0, 1440), N("pres", "psi", 14.5, 0, 458.9) }),
            new StepSchemaDto("vent", "放空", "", "", new[] { N("to", "psi", 2.9, 0, 72.5) }),
            new StepSchemaDto("end", "结束", "", "", Array.Empty<FieldSchemaDto>()),
            new StepSchemaDto("purge", "吹扫", "", "", Array.Empty<FieldSchemaDto>()),   // 旧码：不进 schema
        });
        var s = ProgramSchema.FromDto(dto);
        Assert.Equal(new[] { StepType.Leak, StepType.Hold, StepType.Vent, StepType.End }, s.Steps.Select(x => x.Type));
        var lp = s.Field(StepType.Leak, "pres")!;
        Assert.Equal(("MPa", 0.10, 3.16, 3.0, 435.1), (lp.Unit, lp.Min, lp.Max, lp.Default, lp.Psi!.Default));
        var md = s.Field(StepType.Leak, "maxDrop")!;
        Assert.Equal((FieldSpec.MpaPerHour, 0.0138), (md.Unit, md.Default));
        Assert.Equal(("MPa", 0.49), (s.Field(StepType.Vent, "to")!.Unit, s.Field(StepType.Vent, "to")!.Max));
        var hold = new PcStep(StepType.Hold, "保持");
        hold.Sel["until"] = "time";
        Assert.False(s.Field(StepType.Hold, "pres")!.Visible(hold));
        hold.Sel["until"] = "pressBelow";
        Assert.True(s.Field(StepType.Hold, "pres")!.Visible(hold));
    }

    // ———————————————————— 估计 / 待改 ————————————————————

    /// <summary>STEPS-SPEC §5 的总时长（演示程序库）。</summary>
    [Theory]
    [InlineData("A", "1 h 56 min")]     // 升温 125/3.5 + 15 · 充压 3 · 反应 45 · 降温（保持 ≤）min(60, 15) · 放空 1 + 3 × 20/60 · 结束 0
    [InlineData("H1", "2 h 51 min")]    // 气密 36 · 置换 1.3 × 2 · 搅拌 0 · 升温 95/3.5 + 10 · 充压 3 · 反应 90 · 放空 2
    [InlineData("LK", "36 min")]        // 气密 5 + 30 + 1
    [InlineData("OX", "4 h 40 min")]    // 旧格式转换：置换（只吹总管 10 次）1.5 · 升温 35/3.5 + 10 · 充压 3 · 反应 240 · 降温 15 · 结束
    [InlineData("IH", "4 h 48 min")]    // 置换 1.3 · 升温 95/3.5 + 15 · 充压 3 · 保持 240 · 放空 2
    public void Plan_TotalsPerSpec(string id, string want) => Assert.Equal(want, ProgramPlanner.Hm(ProgramPlanner.Plan(Lib(id).Steps).Total));

    [Fact]
    public void Plan_StepDurationsAndPoints()
    {
        var pl = ProgramPlanner.Plan(Lib("A").Steps);
        // 按条件结束的写「约」，反应写「≤」，0 = 立即
        Assert.Equal(new[] { "51 min", "3 min", "≤ 45 min", "约 15 min", "2 min", "立即" }, pl.Bounds.Select(ProgramPlanner.Dur));
        Assert.Equal(150, pl.Points.Max(p => p.T));
        Assert.Equal(2.5, pl.Points.Max(p => p.P));
        Assert.Equal(40, pl.Points[^1].T);        // 降温：保持到 ≤ 40
        Assert.Equal(0.02, pl.Points[^1].P);      // 放空到 0.02 MPa
        var lk = ProgramPlanner.Plan(Lib("LK").Steps);
        Assert.Equal(3.0, lk.Points.Max(p => p.P));
        Assert.Equal(0, lk.Points[^1].P);          // 气密之后放空
    }

    /// <summary>STEPS-SPEC §5：每种步骤的估计（分钟）与标注。</summary>
    [Fact]
    public void Plan_StepMinutes_PerSpec()
    {
        var s = Schema;
        (double, StepMark) M(PcStep st) { var r = ProgramPlanner.StepMinutes(st); return (Math.Round(r.Min, 4), r.Mark); }
        Assert.Equal((36.0, StepMark.None), M(PcStep.New(StepType.Leak, s)));                       // 5 + 30 + 1
        Assert.Equal((Math.Round(78 / 60.0, 4), StepMark.None), M(PcStep.New(StepType.Exchange, s)));   // (2 × 9 + 3 × 20) / 60
        Assert.Equal((0.0, StepMark.None), M(PcStep.New(StepType.Stir, s)));
        var heat = PcStep.New(StepType.Heat, s);
        Assert.Equal((Math.Round(55 / 3.5 + 15, 4), StepMark.None), M(heat));                      // |80 − 25| / 3.5 + 15
        heat.Num["rate"] = 5;
        Assert.Equal((55 / 5.0 + 15, StepMark.None), M(heat));
        heat.Sel["mode"] = "curve";
        heat.Segments.Clear();
        heat.Segments.Add(new CurveSeg(65, 2, 10));
        heat.Segments.Add(new CurveSeg(105, 4, 20));
        Assert.Equal((20.0 + 10 + 10 + 20, StepMark.None), M(heat));                              // 曲线总时长（从 25 ℃）
        Assert.Equal((3.0, StepMark.None), M(PcStep.New(StepType.Press, s)));
        var hold = PcStep.New(StepType.Hold, s);
        Assert.Equal((10.0, StepMark.None), M(hold));
        hold.Sel["until"] = "tempBelow";
        Assert.Equal((15.0, StepMark.About), M(hold));                                             // min(60, 15)
        hold.Num["maxWait"] = 8;
        Assert.Equal((8.0, StepMark.About), M(hold));
        Assert.Equal((480.0, StepMark.UpTo), M(PcStep.New(StepType.React, s)));
        Assert.Equal((2.0, StepMark.None), M(PcStep.New(StepType.Vent, s)));                       // 1 + 3 × 20 / 60
        Assert.Equal((0.0, StepMark.None), M(PcStep.New(StepType.End, s)));
        Assert.Equal("约 8 min", ProgramPlanner.Dur(new PlanBound(0, 8, 0, StepMark.About)));
        Assert.Equal("≤ 8 h 00 min", ProgramPlanner.Dur(new PlanBound(0, 480, 0, StepMark.UpTo)));
    }

    [Fact]
    public void Issues_OutOfRangeAndCurve()
    {
        Assert.Empty(ProgramIssues.Of(Lib("A").Steps, Schema, "HT-PR8"));
        Assert.Empty(ProgramIssues.Of(Lib("H1").Steps, Schema, "HT-PR8"));
        var ox = ProgramIssues.Of(Lib("OX").Steps, Schema, "HT-PR8");
        var one = Assert.Single(ox);
        Assert.Equal("第 03 步 升压：目标压力 3.40 MPa 超出 HT-PR8 范围 0.06 – 3.14 MPa", one.Msg);
        var p = Lib("A").Clone();
        p.Steps[0].Sel["mode"] = "curve";
        p.Steps[0].Segments.Clear();
        Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Msg.Contains("至少要 1 段"));
        p.Steps[0].Segments.Add(new CurveSeg(250, 3, 10));
        Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Msg.Contains("第 1 段段目标温度"));
        var e = Lib("A").Clone();
        e.Steps[1].Num["pres"] = double.NaN;
        Assert.Contains("未填", ProgramIssues.Of(e.Steps, Schema, "HT-PR8")[0].Msg);
        // 不显示的字段不检查（保持按时间：目标压力随便）
        var h = Lib("IH").Clone();
        h.Steps[4].Num["pres"] = 99;
        Assert.Empty(ProgramIssues.Of(h.Steps, Schema, "HT-PR8"));
        h.Steps[4].Sel["until"] = "pressBelow";
        Assert.Contains(ProgramIssues.Of(h.Steps, Schema, "HT-PR8"), x => x.Key == "pres");
        // 字段之间：放到要比充到低 1 psi；最短时间不超过最长时间
        var x2 = Lib("H1").Clone();
        x2.Steps[1].Num["release"] = 0.5;
        Assert.Contains(ProgramIssues.Of(x2.Steps, Schema, "HT-PR8"), x => x.Key == "release" && x.Msg.Contains("要比充到 0.50 MPa 低"));
        x2.Steps[6].Num["minTime"] = 100;
        Assert.Contains(ProgramIssues.Of(x2.Steps, Schema, "HT-PR8"), x => x.Msg == "第 07 步 反应：最短时间 100 min 超过最长时间 90 min");
    }

    [Fact]
    public void Summary_TwoItems()
    {
        var h1 = Lib("H1").Steps;
        Assert.Equal(new[] { ("试验压力", "3.00 MPa"), ("保压", "30 min") }, ProgramPlanner.Summary(h1[0], Schema));
        Assert.Equal(new[] { ("气源", "惰性气体 × 3"), ("充 / 放", "0.50 → 0.02 MPa") }, ProgramPlanner.Summary(h1[1], Schema));
        Assert.Equal(("转速", "400 rpm"), ProgramPlanner.Summary(h1[3], Schema)[0]);
        Assert.Equal(new[] { ("目标温度", "120.0 °C"), ("平衡", "10 min") }, ProgramPlanner.Summary(h1[4], Schema));
        Assert.Equal(new[] { ("目标压力", "3.00 MPa"), ("气源", "气体 A · 稳压") }, ProgramPlanner.Summary(h1[5], Schema));
        Assert.Equal(new[] { ("最长", "1 h 30 min"), ("结束于", "吸气停止") }, ProgramPlanner.Summary(h1[6], Schema));
        Assert.Equal(new[] { ("放到", "0.02 MPa"), ("之后", "惰性置换 × 3") }, ProgramPlanner.Summary(h1[7], Schema));
        Assert.Equal(("收尾", "关加热 · 阀全关"), ProgramPlanner.Summary(h1[8], Schema)[0]);
        Assert.Equal(new[] { ("条件", "温度 ≤ 40 °C"), ("最长", "60 min") }, ProgramPlanner.Summary(Lib("A").Steps[3], Schema));
    }

    // ———————————————————— HMI 程序 JSON ————————————————————

    [Fact]
    public void ToDoc_PressureInPsiAndCodes()
    {
        var doc = Lib("H1").ToDoc();
        Assert.Equal(new[] { "leak", "exchange", "exchange", "stir", "heat", "press", "react", "vent", "end" }, doc.Steps.Select(s => s.Type));
        Assert.Equal(435.1, doc.Steps[5].Params["pres"].GetDouble());     // 3.0 MPa
        Assert.Equal(435.1, doc.Steps[0].Params["pres"].GetDouble());     // 气密 3.0 MPa
        Assert.Equal(2.0, doc.Steps[0].Params["maxDrop"].GetDouble());    // 2 psi/h
        Assert.Equal((72.5, 2.9), (doc.Steps[1].Params["fill"].GetDouble(), doc.Steps[1].Params["release"].GetDouble()));
        Assert.Equal((2.9, 43.5), (doc.Steps[7].Params["to"].GetDouble(), doc.Steps[7].Params["flushPres"].GetDouble()));
        Assert.Equal("A", doc.Steps[5].Params["gas"].GetString());
        Assert.Equal(("inert", "A"), (doc.Steps[1].Params["gas"].GetString(), doc.Steps[2].Params["gas"].GetString()));
        Assert.Equal(("on", "off"), (doc.Steps[6].Params["stallOn"].GetString(), doc.Steps[6].Params["mmolOn"].GetString()));
        Assert.Empty(doc.Steps[8].Params);                                  // 结束没有参数
        Assert.Equal(new[] { "rpm" }, doc.Steps[3].Params.Keys);            // 搅拌只有转速（没有旧的 dur）
        var back = PcProgram.FromDoc(ParallelReactor.Client.ProgramDoc.FromJson(doc.ToJson(), "x"), "PR8", "x");
        Assert.False(back.Migrated);
        Assert.Equal(3.0, back.Steps[5].N("pres"));
        Assert.Equal((0.5, 0.02, 0.0138), (back.Steps[1].N("fill"), back.Steps[1].N("release"), back.Steps[0].N("maxDrop")));
        var c = Lib("A").Clone();
        c.Steps[0].Sel["mode"] = "curve";
        c.Steps[0].Segments.Add(new CurveSeg(80, 3, 20));
        var seg = c.ToDoc().Steps[0].Params["segments"];
        Assert.Equal(JsonValueKind.Array, seg.ValueKind);
        Assert.Equal(80, seg[0].GetProperty("temp").GetDouble());
        // 旧程序里留下的键不下发
        var old = Lib("A").Clone();
        old.Steps[2].Num["dur"] = 5;
        old.Steps[2].Sel["endBy"] = "time";
        Assert.False(old.ToDoc().Steps[2].Params.ContainsKey("dur") || old.ToDoc().Steps[2].Params.ContainsKey("endBy"));
    }

    /// <summary>旧格式的 HMI 程序 JSON（草稿 / 导入）→ 按 STEPS-SPEC §3 转换（经 StepMigration），标 Migrated。</summary>
    [Fact]
    public void FromDoc_LegacyDoc_IsMigrated()
    {
        var doc = new ParallelReactor.Client.ProgramDoc { Name = "旧程序" };
        doc.Add(StepType.Purge, new { gas = "inert", cycles = 5, fill = 3, vent = 5 });
        doc.Add(StepType.Heat, new { mode = "fixed", temp = 120, wait = 10 });
        doc.Add(StepType.Stir, new { rpm = 400, dur = 10 });
        doc.Add(StepType.Press, new { gas = "A", pres = 290.1, regulate = "on" });
        doc.Add(StepType.Wait, new { mode = "pressure", time = 20 });
        doc.Add(StepType.React, new { endBy = "either", time = 120, mmol = 8 });
        doc.Add(StepType.End, new { mode = "vent", cool = 40 });
        var p = PcProgram.FromDoc(doc, "PR8", "x");
        Assert.True(p.Migrated);
        Assert.Equal(new[] { StepType.Exchange, StepType.Heat, StepType.Stir, StepType.Hold, StepType.Stir, StepType.Press, StepType.Hold, StepType.React, StepType.Hold, StepType.Vent, StepType.End },
            p.Steps.Select(s => s.Type));
        Assert.Equal((0.0, 5.0, "inert"), (p.Steps[0].N("cycles"), p.Steps[0].N("lineFlush"), p.Steps[0].S("gas")));   // 只吹总管，行为不变
        Assert.Equal(60.0, p.Steps[0].N("maxSec"));                                                                  // 其余取默认
        Assert.Equal((400.0, "time", 10.0, 0.0), (p.Steps[2].N("rpm"), p.Steps[3].S("until"), p.Steps[3].N("time"), p.Steps[4].N("rpm")));
        Assert.Equal(("pressAbove", 1.94, 20.0, "continue"), (p.Steps[6].S("until"), p.Steps[6].N("pres"), p.Steps[6].N("maxWait"), p.Steps[6].S("onTimeout")));   // 290.1 × 0.97 psi
        Assert.Equal(("on", 8.0, 0.0, "off"), (p.Steps[7].S("mmolOn"), p.Steps[7].N("mmol"), p.Steps[7].N("minTime"), p.Steps[7].S("stallOn")));
        Assert.Equal(("tempBelow", 40.0, "保持"), (p.Steps[8].S("until"), p.Steps[8].N("temp"), p.Steps[8].Name));
        Assert.Equal(0.0, p.Steps[9].N("flush"));
        Assert.Equal(2.0, p.Steps[5].N("pres"));
        // 新格式的程序原样（不标转换），缺的结束补上
        var nd = new ParallelReactor.Client.ProgramDoc { Name = "新" };
        nd.Add(StepType.Hold, new { until = "time", time = 5 });
        var q = PcProgram.FromDoc(nd, "PR8", "y");
        Assert.False(q.Migrated);
        Assert.Equal(new[] { StepType.Hold, StepType.End }, q.Steps.Select(s => s.Type));
    }

    [Fact]
    public void ToDef_NamesAndEstimates()
    {
        var def = Lib("A").ToDef(null);
        Assert.Equal("A", def.Id);
        Assert.Equal("恒温反应", def.Steps[2].Name);
        Assert.Equal(StepKind.Heat, def.Steps[0].Type);
        Assert.True(def.Steps[0].EstMin >= 50);
        Assert.Equal((StepKind.Hold, StepMark.About, StepKind.Vent, StepKind.End), (def.Steps[3].Type, def.Steps[3].Mark, def.Steps[4].Type, def.Steps[5].Type));
        Assert.Equal(StepMark.UpTo, def.Steps[2].Mark);
    }

    [Fact]
    public void LibraryJson_RoundTrip()
    {
        // OX 是旧格式转换来的（内存里标着）；写盘按新格式，读回来就不再是转换的
        var p = Lib("OX");
        Assert.True(p.Migrated);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) p.Write(w);
        using var d = JsonDocument.Parse(ms.ToArray());
        var q = PcProgram.Read(d.RootElement);
        Assert.False(q.Migrated);
        Assert.Equal((p.Id, p.Name, p.Model, p.By, p.Draft, p.Modified), (q.Id, q.Name, q.Model, q.By, q.Draft, q.Modified));
        Assert.Equal(p.Steps.Select(s => s.Name), q.Steps.Select(s => s.Name));
        Assert.Equal(new[] { StepType.Exchange, StepType.Heat, StepType.Press, StepType.React, StepType.Hold, StepType.End }, q.Steps.Select(s => s.Type));
        Assert.Equal("on", q.Steps[3].S("mmolOn"));
        Assert.Equal(Units.PsiToMpa(493.1), q.Steps[2].N("pres"), 9);   // 旧的 3.4 MPa = 493.13 psi，超出声明上限 493.1 psi（34 bar）→ 转换时收紧（同 HMI）
        Assert.Equal(10.0, q.Steps[0].N("lineFlush"));                 // 旧吹扫 12 次 → 收紧到 10（转换时）
        Assert.Equal(new[] { "第 01 步 置换：先吹总管 12 次 超出新范围，已改为 10 次" }, p.MigrationNotes);   // 493.128 → 493.1 psi 照样收紧，界面单位（MPa）看不出差别，不写说明
        Assert.Empty(q.MigrationNotes);
    }

    /// <summary>旧版 library.json（吹扫 / 带 endBy 的反应 / 带方式的结束，压力 MPa）读进来就转换，压力原样（不经 psi 取整）。</summary>
    [Fact]
    public void LibraryJson_LegacyProgram_MigratedOnLoad()
    {
        const string json = """
            { "id": "old", "name": "旧程序", "model": "PR8", "steps": [
              { "type": "purge", "name": "吹扫置换", "params": { "gas": "inert", "cycles": 8, "fill": 3, "vent": 5 } },
              { "type": "heat", "name": "升温", "params": { "mode": "fixed", "temp": 120, "wait": 10 } },
              { "type": "press", "name": "升压", "params": { "gas": "A", "pres": 2.55, "regulate": "on" } },
              { "type": "react", "name": "反应", "params": { "endBy": "time", "time": 90, "mmol": 5 } },
              { "type": "end", "name": "降温", "params": { "mode": "seal", "cool": 35 } } ] }
            """;
        using var d = JsonDocument.Parse(json);
        var p = PcProgram.Read(d.RootElement);
        Assert.True(p.Migrated);
        Assert.Equal(new[] { StepType.Exchange, StepType.Heat, StepType.Press, StepType.React, StepType.Hold, StepType.End }, p.Steps.Select(s => s.Type));
        Assert.Equal(new[] { "置换", "升温", "升压", "反应", "保持", "结束" }, p.Steps.Select(s => s.Name));   // 没转换的步骤保留名字
        Assert.Equal(2.55, p.Steps[2].N("pres"), 9);
        Assert.Equal(("off", 0.0), (p.Steps[3].S("mmolOn"), p.Steps[3].N("minTime")));
        Assert.Empty(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"));
    }

    // ———————————————————— 模板 ————————————————————

    [Fact]
    public void Templates_PerDesign()
    {
        var h = ProgramTemplates.Hydrogenation("T1", "PR8");
        Assert.Equal(new[] { StepType.Leak, StepType.Exchange, StepType.Exchange, StepType.Stir, StepType.Heat, StepType.Press, StepType.React, StepType.Vent, StepType.End }, h.Steps.Select(s => s.Type));
        Assert.Equal((3.0, 30.0, "stop", "vent"), (h.Steps[0].N("pres"), h.Steps[0].N("hold"), h.Steps[0].S("onFail"), h.Steps[0].S("after")));
        Assert.Equal(("inert", 3.0, 0.5, 0.02, 2.0), (h.Steps[1].S("gas"), h.Steps[1].N("cycles"), h.Steps[1].N("fill"), h.Steps[1].N("release"), h.Steps[1].N("lineFlush")));
        Assert.Equal(("A", 3.0), (h.Steps[2].S("gas"), h.Steps[2].N("cycles")));
        Assert.Equal(400.0, h.Steps[3].N("rpm"));
        Assert.Equal((80.0, 2.0, 15.0), (h.Steps[4].N("temp"), h.Steps[4].N("band"), h.Steps[4].N("wait")));
        Assert.Equal(("A", 2.0, "on"), (h.Steps[5].S("gas"), h.Steps[5].N("pres"), h.Steps[5].S("regulate")));
        Assert.Equal((480.0, 30.0, "on", 0.005, 30.0), (h.Steps[6].N("time"), h.Steps[6].N("minTime"), h.Steps[6].S("stallOn"), h.Steps[6].N("stallRate"), h.Steps[6].N("stallMin")));
        Assert.Equal((0.02, 3.0, 0.3), (h.Steps[7].N("to"), h.Steps[7].N("flush"), h.Steps[7].N("flushPres")));
        Assert.DoesNotContain(h.Steps, s => s.Type == StepType.Hold && s.S("until") == "tempBelow");   // 没有降温
        Assert.Empty(ProgramIssues.Of(h.Steps, Schema, "HT-PR8"));
        Assert.Empty(ProgramChecks.Of(h.Steps, Schema));                                              // 没有提示：先升温后充压、有充压、有放空
        var lk = ProgramTemplates.LeakOnly("T2", "PR8");
        Assert.Equal(new[] { StepType.Leak, StepType.End }, lk.Steps.Select(s => s.Type));
        Assert.Equal(("vent", 30.0), (lk.Steps[0].S("after"), lk.Steps[0].N("hold")));
        var ih = ProgramTemplates.InertHeating("T3", "PR8");
        Assert.Equal(new[] { StepType.Exchange, StepType.Stir, StepType.Heat, StepType.Press, StepType.Hold, StepType.Vent, StepType.End }, ih.Steps.Select(s => s.Type));
        Assert.Equal((120.0, "inert", 0.5, 240.0), (ih.Steps[2].N("temp"), ih.Steps[3].S("gas"), ih.Steps[3].N("pres"), ih.Steps[4].N("time")));
        Assert.Empty(ProgramIssues.Of(ih.Steps, Schema, "HT-PR8"));
        Assert.Equal(new[] { "T1", "T2", "T3" }, ProgramTemplates.All("PR8").Select(p => p.Id));
    }

    /// <summary>模板库（2026-11-23，设计稿 tpl-design 方向 A）：四个内置模板、吸气量验证的步骤、用模板新建 / 存为模板都是复制、名字校验。</summary>
    [Fact]
    public void TemplateLibrary_BuiltIn_Instantiate_FromProgram()
    {
        var built = ProgramTemplates.BuiltIn("PR8");
        Assert.Equal(new[] { "T1", "T2", "T3", "T4" }, built.Select(t => t.Id));
        Assert.Equal(new[] { "加氢筛选", "只做气密", "惰性气氛加热", "吸气量验证" }, built.Select(t => t.Name));
        Assert.All(built, t => { Assert.True(t.BuiltIn); Assert.NotEmpty(t.Note); Assert.Equal("PR8", t.Model); Assert.Equal(StepType.End, t.Program.Steps[^1].Type); });
        Assert.All(built, t => Assert.Empty(ProgramIssues.Of(t.Program.Steps, Schema, "HT-PR8")));
        // 吸气量验证：只吹总管（釜不置换）→ 充压不稳压 → 保持 10 min → 搅拌 → 反应到吸气停止 → 结束；没有升温
        var u = built[3].Program;
        Assert.Equal(new[] { StepType.Exchange, StepType.Press, StepType.Hold, StepType.Stir, StepType.React, StepType.End }, u.Steps.Select(s => s.Type));
        Assert.Equal(("A", 0.0, 3.0), (u.Steps[0].S("gas"), u.Steps[0].N("cycles"), u.Steps[0].N("lineFlush")));
        Assert.Equal(("A", 0.5, "off"), (u.Steps[1].S("gas"), u.Steps[1].N("pres"), u.Steps[1].S("regulate")));
        Assert.Equal(("time", 10.0), (u.Steps[2].S("until"), u.Steps[2].N("time")));
        Assert.Equal(400.0, u.Steps[3].N("rpm"));
        Assert.Equal((120.0, 30.0, "on", "off", 15.0), (u.Steps[4].N("time"), u.Steps[4].N("minTime"), u.Steps[4].S("stallOn"), u.Steps[4].S("mmolOn"), u.Steps[4].N("stallMin")));
        // 用模板新建：一份独立的草稿，改它不影响模板
        var now = new DateTime(2026, 11, 23, 9, 0, 0);
        var p = built[0].Instantiate("N1", "我的加氢", "PR8", now);
        Assert.Equal(("N1", "我的加氢", "PR8", true, now), (p.Id, p.Name, p.Model, p.Draft, p.Modified));
        Assert.Equal(9, p.Steps.Count);
        p.Steps[4].Num["temp"] = 150;
        Assert.Equal(80.0, built[0].Program.Steps[4].N("temp"));
        // 存为模板：也是复制；型号、来源跟随程序
        var t = PcTemplate.FromProgram(p, "U9", "加氢 150", "说明", now, "张工");
        Assert.Equal((false, "加氢 150", "说明", "我的加氢", "PR8", "张工"), (t.BuiltIn, t.Name, t.Note, t.From, t.Model, t.Program.By));
        p.Steps[4].Num["temp"] = 100;
        Assert.Equal(150.0, t.Program.Steps[4].N("temp"));
        // 没有「结束」的模板（手改过文件）：新建时补上
        var broken = new PcTemplate(new PcProgram { Id = "U8", Name = "缺结束", Model = "PR8", Steps = { ProgramTemplates.Heat(80) } }, false);
        Assert.Equal(new[] { StepType.Heat, StepType.End }, broken.Instantiate("N2", "x", "PR8", now).Steps.Select(s => s.Type));
        // 名字：不能空、不能太长、不能重名（改名时不和自己比）
        var all = built.Append(t).ToList();
        Assert.Equal("请填模板名称", PcTemplate.NameError("  ", all));
        Assert.Equal("名称最多 40 个字符", PcTemplate.NameError(new string('长', 41), all));
        Assert.Equal("已有同名模板 · 换一个名字", PcTemplate.NameError(" 加氢筛选 ", all));
        Assert.Equal("已有同名模板 · 换一个名字", PcTemplate.NameError("加氢 150", all));
        Assert.Null(PcTemplate.NameError("加氢 150", all, selfId: "U9"));
        Assert.Null(PcTemplate.NameError("新名字", all));
        Assert.Equal("请填程序名称", PcTemplate.ProgramNameError(""));
        Assert.Null(PcTemplate.ProgramNameError("未命名程序"));
    }

    // ———————————————————— 下发前检查（STEPS-SPEC §4） ————————————————————

    private static PcProgram Prog(params PcStep[] steps) { var p = new PcProgram { Name = "t" }; p.Steps.AddRange(steps); return p; }

    [Fact]
    public void Checks_EndMustBeLastAndSingle()
    {
        var ok = Prog(ProgramTemplates.Heat(80), ProgramTemplates.Vent(), ProgramTemplates.End());
        Assert.DoesNotContain(ProgramChecks.Of(ok.Steps, Schema), c => c.Code == "end");
        var none = Prog(ProgramTemplates.Heat(80), ProgramTemplates.Vent());
        var two = Prog(ProgramTemplates.End(), ProgramTemplates.Heat(80), ProgramTemplates.Vent(), ProgramTemplates.End());
        var mid = Prog(ProgramTemplates.Heat(80), ProgramTemplates.End(), ProgramTemplates.Vent());
        foreach (var (p, msg) in new[] { (none, "程序没有「结束」步骤"), (two, "「结束」不止一个"), (mid, "「结束」不在最后") })
        {
            var c = Assert.Single(ProgramChecks.Of(p.Steps, Schema), x => x.Code == "end");
            Assert.Equal(CheckLevel.Reject, c.Level);
            Assert.StartsWith(msg, c.Msg);
            Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Key == "end");   // 拒绝 = 待改项
        }
    }

    [Fact]
    public void Checks_ReactWithoutPress_Warns()
    {
        var p = Prog(ProgramTemplates.Heat(80), ProgramTemplates.React(60), ProgramTemplates.Vent(), ProgramTemplates.End());
        var c = Assert.Single(ProgramChecks.Of(p.Steps, Schema));
        Assert.Equal((CheckLevel.Warn, "nogas", 1, "第 02 步 反应前面没有充压 · 吸气量不计量"), (c.Level, c.Code, c.I, c.Msg));
        Assert.Empty(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"));   // 只是提示
        // 充压之后又置换：计量停了 → 也提示
        var q = Prog(ProgramTemplates.Press("A", 1), ProgramTemplates.Exchange("inert"), ProgramTemplates.React(60), ProgramTemplates.Vent(), ProgramTemplates.End());
        Assert.Contains(ProgramChecks.Of(q.Steps, Schema), x => x.Code == "nogas");
        var r = Prog(ProgramTemplates.Press("A", 1), ProgramTemplates.Stir(), ProgramTemplates.React(60), ProgramTemplates.Vent(), ProgramTemplates.End());
        Assert.DoesNotContain(ProgramChecks.Of(r.Steps, Schema), x => x.Code == "nogas");
    }

    [Fact]
    public void Checks_PressBeforeHeat_WarnsWithEstimate_RejectsOverLimit()
    {
        // 25 ℃ 充到 2.0 MPa 再升到 80 ℃：(2.0 + 0.101325) × 353.15 / 298.15 − 0.101325 = 2.388 MPa
        Assert.Equal(2.388, ProgramChecks.PressAfterHeat(2.0, 25, 80), 3);
        var p = Prog(ProgramTemplates.Press("A", 2.0), ProgramTemplates.Heat(80), ProgramTemplates.React(60), ProgramTemplates.Vent(), ProgramTemplates.End());
        var c = Assert.Single(ProgramChecks.Of(p.Steps, Schema));
        Assert.Equal((CheckLevel.Warn, "heatafter", 0), (c.Level, c.Code, c.I));
        Assert.Equal("第 01 步 充压在升温之前 · 升温后压力会高于目标（估算 2.39 MPa），稳压不会放气", c.Msg);
        Assert.Empty(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"));
        // 估算超过上限（3.14 MPa）：拒绝
        var hot = Prog(ProgramTemplates.Press("A", 2.8), ProgramTemplates.Heat(150), ProgramTemplates.Vent(), ProgramTemplates.End());
        var r = Assert.Single(ProgramChecks.Of(hot.Steps, Schema));
        Assert.Equal((CheckLevel.Reject, "heatafter"), (r.Level, r.Code));
        Assert.Contains("超过上限 3.14 MPa", r.Msg);
        Assert.Contains(ProgramIssues.Of(hot.Steps, Schema, "HT-PR8"), x => x.Key == "heatafter");
        // 先升温后充压、或者中间放空了：不提示
        Assert.Empty(ProgramChecks.Of(Prog(ProgramTemplates.Heat(80), ProgramTemplates.Press("A", 2.0), ProgramTemplates.React(60), ProgramTemplates.Vent(), ProgramTemplates.End()).Steps, Schema));
        Assert.DoesNotContain(ProgramChecks.Of(Prog(ProgramTemplates.Press("A", 2.0), ProgramTemplates.Vent(), ProgramTemplates.Heat(80), ProgramTemplates.End()).Steps, Schema), x => x.Code == "heatafter");
        // 充压时已经热了：按充压时的温度算
        var warm = Prog(ProgramTemplates.Heat(60), ProgramTemplates.Press("A", 2.0), ProgramTemplates.Heat(80), ProgramTemplates.Vent(), ProgramTemplates.End());
        Assert.Contains("估算 " + PressureDisplay.InputU(ProgramChecks.PressAfterHeat(2.0, 60, 80)), Assert.Single(ProgramChecks.Of(warm.Steps, Schema)).Msg);
    }

    [Fact]
    public void Checks_NoVent_Warns()
    {
        var p = Prog(ProgramTemplates.Heat(80), ProgramTemplates.Press("A", 1.0), ProgramTemplates.React(60), ProgramTemplates.End());
        var c = Assert.Single(ProgramChecks.Of(p.Steps, Schema));
        Assert.Equal((CheckLevel.Warn, "novent", "程序里没有放空 · 结束时釜里可能带压"), (c.Level, c.Code, c.Msg));
        // 演示的 OX（旧格式转换来的）：没有放空 → 提示；3.4 MPa 超出范围是待改项
        Assert.Contains(ProgramChecks.Of(Lib("OX").Steps, Schema), x => x.Code == "novent");
    }

    [Fact]
    public void Checks_RpmMismatch_NamesTheChannels()
    {
        var a = Prog(ProgramTemplates.Stir(400), ProgramTemplates.End()).Steps;
        var b = Prog(ProgramTemplates.Stir(300), ProgramTemplates.Stir(400), ProgramTemplates.End()).Steps;
        var none = Prog(ProgramTemplates.Heat(80), ProgramTemplates.End()).Steps;
        Assert.Null(ProgramChecks.RpmMismatch(new[] { (1, (IReadOnlyList<PcStep>)a), (2, a), (3, none) }));      // 没有搅拌的路不算
        Assert.Equal("一台电机带全部釜，同时启动的几路转速必须一样：RV1–RV3、RV6 400 rpm；RV4、RV5 300 rpm",
            ProgramChecks.RpmMismatch(new[] { 1, 2, 3, 4, 5, 6 }.Select(n => (n, n is 4 or 5 ? b : (IReadOnlyList<PcStep>)a))));   // 第一个搅拌的转速
        Assert.Equal((400.0, 300.0, (double?)null), (ProgramChecks.FirstRpm(a)!.Value, ProgramChecks.FirstRpm(b)!.Value, ProgramChecks.FirstRpm(none)));
    }

    // ———————————————————— 条件矩阵 ————————————————————

    /// <summary>样稿 1号机：RV1–4、6、8 在跑（RV1 15:19、RV2 15:47 …），RV5 空闲、RV7 停用。</summary>
    private static MxChannel[] Dev1 => new[]
    {
        new MxChannel(1, false, true, new DateTime(2026, 11, 15, 15, 19, 0), 5), new MxChannel(2, false, true, new DateTime(2026, 11, 15, 15, 47, 0), 5),
        new MxChannel(3, false, true, new DateTime(2026, 11, 15, 15, 13, 0), 6), new MxChannel(4, false, true, null, 5),
        new MxChannel(5, false, false, null, 5), new MxChannel(6, false, true, new DateTime(2026, 11, 15, 15, 13, 0), 5),
        new MxChannel(7, true, false, null, null), new MxChannel(8, false, true, new DateTime(2026, 11, 15, 14, 39, 0), 4),
    };

    private static MatrixPlan DemoMatrix()
    {
        var m = MatrixPlan.Build(Lib("H1"), 1, Dev1, DemoPrograms.MatrixName);
        DemoPrograms.SeedMatrix(m, Dev1.Select(c => c.N).ToArray());
        return m;
    }

    private static MxContext Ctx(bool ctl = true) => new("1号机", "HT-PR8", "PR8", ctl, Dev1, Schema, 8);

    [Fact]
    public void Matrix_BuildLikeMock()
    {
        var m = DemoMatrix();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8 }, m.Include);
        Assert.Equal(6.0, m.Vol[3]);
        Assert.Equal(5.0, m.Vol[7]);              // 停用的没有体积：给 5
        Assert.Equal(new[] { 100.0, 110, 120, 130, 100, 110, 120, 130 }, Enumerable.Range(1, 8).Select(n => m.PerValue(4, "temp", n)));
        Assert.Equal("Pt/C 5%", m.Samples[5]);
        Assert.True(m.Changed(4, "temp", 1));
        Assert.False(m.Changed(4, "temp", 3));    // 120 = 模板值
        Assert.Equal(4.0, m.StepsFor(4)[5].N("pres"));
        Assert.Equal(3.0, m.StepsFor(5)[5].N("pres"));
        Assert.Equal("H1", m.ProgramFor(5).Id);
    }

    [Fact]
    public void Matrix_IssuesGroupedLikeMock()
    {
        var iss = DemoMatrix().Issues(Ctx());
        Assert.Equal(2, iss.Count);
        Assert.Equal(new MxIssue(MxLevel.No, "第 06 步目标压力：RV4 4.00 MPa · 超出 HT-PR8 范围 0.06 – 3.14 MPa"), iss[0]);
        Assert.Equal(new MxIssue(MxLevel.Wait, "RV1–RV4、RV6、RV8 正在运行，最晚约 15:47 结束 · 结束后才能下发，可以先保存"), iss[1]);
        var m = DemoMatrix();
        m.Vol[5] = double.NaN;
        m.Vol[6] = 9;
        var v = m.Issues(Ctx(ctl: false));
        Assert.Contains(new MxIssue(MxLevel.No, "溶液体积：RV5 未填 · 算不出吸气量"), v);
        Assert.Contains(new MxIssue(MxLevel.No, "溶液体积：RV6 超出 1–8 mL"), v);
        Assert.Contains(new MxIssue(MxLevel.Wait, "1号机为本地只读 · 在工具条「申请控制」后才能下发"), v);
        m.Include.Clear();
        Assert.Contains(new MxIssue(MxLevel.No, "没有参加的通道 · 在表头勾选"), m.Issues(Ctx()));
    }

    [Fact]
    public void Matrix_QueuePlansLikeMock()
    {
        var m = DemoMatrix();
        var inc = m.Included(Dev1);
        var totals = inc.Select((n, j) => (n, t: ProgramPlanner.Hm(m.PlanFor(n, j).Total))).ToArray();
        // STEPS-SPEC §5（升温按 |目标 − 25| / 3.5）+ 升压排队 j × 3 min
        Assert.Equal("2 h 45 min", totals.First(x => x.n == 1).t);
        Assert.Equal("2 h 51 min", totals.First(x => x.n == 2).t);
        Assert.Equal("3 h 12 min", totals.First(x => x.n == 8).t);
        Assert.Equal(18, m.PlanFor(8, 6).Queue);
        Assert.Equal("升压步骤按 RV1 → RV2 → … → RV8（共 7 路）依次占用共用总管，每路约 3 min；最后一路比第一路晚约 18 min 开始反应。对比吸气量时按各路升压完成的时刻对齐。",
            m.QueueNote(inc));
        Assert.Null(m.QueueNote(new[] { 5 }));
    }

    [Fact]
    public void Matrix_SameAndRamp()
    {
        var m = DemoMatrix();
        var f = Schema.Field(StepType.Heat, "temp")!;
        var inc = new[] { 1, 2, 3, 4 };
        Assert.Equal("目标温度：参加的 4 路都设为 100 °C", m.Same(4, "temp", inc, f));
        Assert.All(inc, n => Assert.Equal(100, m.PerValue(4, "temp", n)));
        Assert.Equal("等差填充：先把最后一路（RV4）改成终点值", m.Ramp(4, "temp", inc, f));
        m.SetPer(4, "temp", 4, 130);
        Assert.Equal("目标温度：从 100 到 130 °C 等差填充 4 路", m.Ramp(4, "temp", inc, f));
        Assert.Equal(new[] { 100.0, 110, 120, 130 }, inc.Select(n => m.PerValue(4, "temp", n)));
        Assert.Null(m.Same(4, "temp", new[] { 1 }, f));
        Assert.Equal("溶液体积：参加的 2 路都设为 5.0 mL", m.VolSame(new[] { 1, 3 }));
        Assert.Equal(5.0, m.Vol[3]);
    }

    /// <summary>转速不能按路变化（一台电机）：搅拌是共用参数，矩阵生成的每一路转速一样；为什么共用写在表里。</summary>
    [Fact]
    public void Matrix_RpmIsShared_NotPerChannel()
    {
        Assert.False(MatrixPlan.IsPer(StepType.Stir, "rpm"));
        Assert.Empty(MatrixPlan.PerKeys(StepType.Stir));
        Assert.Contains("rpm", MatrixPlan.RowKeys(StepType.Stir));
        Assert.True(MatrixPlan.IsHardwareShared(StepType.Stir, "rpm"));
        Assert.Equal("一台电机带全部釜 · 转速不能按路变化", MatrixPlan.Why(StepType.Stir, "rpm"));
        var m = DemoMatrix();
        m.Steps[3].Num["rpm"] = 320;
        var batch = m.Included(Dev1).Select(n => (n, m.StepsFor(n))).ToList();
        Assert.All(batch, b => Assert.Equal(320, ProgramChecks.FirstRpm(b.Item2)));
        Assert.Null(ProgramChecks.RpmMismatch(batch));
        // 每路的参数里有先充压后升温超上限：矩阵检查拒绝，写明哪几路
        var hot = MatrixPlan.Build(ProgramTemplates.InertHeating("IH", "PR8"), 1, Dev1, "x");
        hot.Steps.Insert(2, ProgramTemplates.Press("inert", 2.8));
        hot.Steps.Insert(3, ProgramTemplates.Heat(190));
        Assert.Contains(hot.Issues(Ctx()), x => x.Level == MxLevel.No && x.Msg.StartsWith("第 03 步充压在升温之前") && x.Msg.EndsWith("（RV1–RV6、RV8）"));
    }

    [Fact]
    public void ChList_RangesAndCommas()
    {
        Assert.Equal("RV1–RV4、RV6、RV8", MatrixPlan.ChList(new[] { 8, 1, 2, 3, 4, 6 }));
        Assert.Equal("RV1、RV2", MatrixPlan.ChList(new[] { 2, 1 }));
        Assert.Equal("RV9–RV11、RV13", MatrixPlan.ChList(new[] { 9, 10, 11, 13 }));
    }

    [Fact]
    public void Matrix_CsvRoundTrip()
    {
        var m = DemoMatrix();
        var chs = Dev1.Select(c => c.N).ToArray();
        var csv = MatrixCsv.Export(m, chs, Schema, "HT-PR8");
        Assert.Contains("s5.heat.temp,05 升温 · 目标温度 °C,100,110,120,130,100,110,120,130", csv);
        Assert.Contains("s4.stir.rpm,04 搅拌 · 转速 rpm,400,400,400,400,400,400,400,400", csv);   // 共用参数：每列同一个值
        Assert.Contains("sample,样品 / 催化剂,Pd/C 5%", csv);
        var n = MatrixPlan.Build(Lib("H1"), 1, Dev1, "x");
        var msg = MatrixCsv.Import(n, csv, chs, new[] { 7 });
        Assert.StartsWith("已导入", msg);
        Assert.Equal(130, n.PerValue(4, "temp", 4));
        Assert.Equal(4.0, n.PerValue(5, "pres", 4));
        Assert.Equal("Pd/C 5%", n.Samples[2]);
        Assert.DoesNotContain(7, n.Include);
        // 引号与逗号
        var q = MatrixCsv.Parse("a,\"b,c\",\"d\"\"e\"\r\nx");
        Assert.Equal(new[] { "a", "b,c", "d\"e" }, q[0]);
        // 步骤对不上：整份不导入
        var lk = MatrixPlan.Build(Lib("LK"), 1, Dev1, "x");
        Assert.Throws<FormatException>(() => MatrixCsv.Import(lk, csv, chs, Array.Empty<int>()));
        Assert.Throws<FormatException>(() => MatrixCsv.Import(n, "nothing", chs, Array.Empty<int>()));
    }

    [Fact]
    public void Matrix_JsonRoundTrip()
    {
        var m = DemoMatrix();
        m.Vol[5] = double.NaN;
        m.Steps[3].Num["rpm"] = 350;
        m.Record = false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) MatrixJson.Write(w, m, new DateTime(2026, 11, 15, 14, 31, 0));
        using var d = JsonDocument.Parse(ms.ToArray());
        var r = MatrixJson.Read(d.RootElement, Lib("H1"), 1, Dev1);
        Assert.Equal(m.Name, r.Name);
        Assert.False(r.Record);
        Assert.Equal(m.Include, r.Include);
        Assert.True(double.IsNaN(r.Vol[5]));
        Assert.Equal(350, r.Steps[3].N("rpm"));
        Assert.Equal(4.0, r.PerValue(5, "pres", 4));
        Assert.Equal("Pt/C 5%", r.Samples[8]);
    }

    // ———————————————————— 演示设备 ————————————————————

    [Fact]
    public void DemoFleet_LoadsCustomProgram_ThenRuns()
    {
        var fleet = new DemoFleet(DemoParams.Parse("fusion=1&freeze=1"));
        var d = fleet.Device(1)!;
        var p = DemoMatrix().ProgramFor(5);
        var (ok, msg) = fleet.LoadProgram(d, 5, p.ToDef(d.Model));
        Assert.True(ok, msg);
        Assert.Equal("程序「催化加氢筛选 · 120 ℃」已下发到 RV5", msg);
        Assert.Equal(ProgramStatus.Loaded, d.Channel(5)!.Program!.Status);
        Assert.True(fleet.Act(d, DeviceAction.ProgRun, ActionPayload.Ch(5), quiet: true).Ok);
        Assert.Equal(ProgramStatus.Running, d.Channel(5)!.Program!.Status);
        Assert.Equal("气密检查", d.Channel(5)!.Program!.Steps[0].Name);
        Assert.Equal(StepKind.Leak, d.Channel(5)!.Program!.Steps[0].Type);
        // 在跑的通道：守卫不让下发
        var (ok2, why) = fleet.LoadProgram(d, 1, p.ToDef(d.Model));
        Assert.False(ok2);
        Assert.Contains("程序运行中", why);
    }
}
