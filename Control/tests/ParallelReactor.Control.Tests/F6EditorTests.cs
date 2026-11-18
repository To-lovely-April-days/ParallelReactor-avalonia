using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F6 程序页的纯逻辑：程序 schema（HMI 的 / 本机生成）、程序估计（样稿 progPlan）、待改项、HMI 程序 JSON 换算、
/// 条件矩阵（建、检查、统一 / 等差、升压排队、CSV、方案 JSON）、演示设备下发自定义程序。
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
        var temp = s.Field(StepType.Heat, "temp")!;
        Assert.Equal((35.0, 200.0), (temp.Min, temp.Max));
        var p = s.Field(StepType.Press, "pres")!;
        Assert.Equal("MPa", p.Unit);
        Assert.Equal(2, p.Dp);
        Assert.Equal(0.01, p.Min);
        Assert.Equal(3.14, p.Max);               // 3.50 × 0.9 − 0.1 psi，向下取到 0.01（保证 HMI 收）
        Assert.Equal("0.01 – 3.14", p.RangeText);
        Assert.Equal(450, s.Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(new[] { "fixed", "curve" }, s.Field(StepType.Heat, "mode")!.Options.Select(o => o.Code));
        Assert.Equal("0 – 100", s.Field(StepType.React, "mmol")!.RangeText);
    }

    [Fact]
    public void HmiSchema_FromLiveSample_ConvertsPressureAndCapsRpm()
    {
        var dto = JsonSerializer.Deserialize<ProgramSchemaDto>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "live-program-schema.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var s = ProgramSchema.FromDto(dto);
        Assert.Equal("hmi", s.Source);
        Assert.Equal(7, s.Steps.Count);
        var p = s.Field(StepType.Press, "pres")!;
        Assert.Equal("MPa", p.Unit);
        Assert.Equal(1.37, p.Max);               // HMI 的 StepDef 声明 0–200 psi
        Assert.Equal(0.01, p.Min);
        Assert.Equal(1000, s.Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(450, s.Cap(450).Field(StepType.Stir, "rpm")!.Max);
        Assert.Equal(3, s.Of(StepType.Heat)!.SegmentFields!.Count);
        // 默认 schema 与 HMI 的字段一一对应（键、名字、选项码）
        foreach (var st in s.Steps)
        {
            var mine = Schema.Of(st.Type)!;
            Assert.Equal(st.Fields.Select(f => f.Key), mine.Fields.Select(f => f.Key));
            Assert.Equal(st.Fields.Select(f => f.Label), mine.Fields.Select(f => f.Label));
            Assert.Equal(st.Fields.Select(f => string.Join(",", f.Options.Select(o => o.Code))), mine.Fields.Select(f => string.Join(",", f.Options.Select(o => o.Code))));
        }
    }

    // ———————————————————— 估计 / 待改 ————————————————————

    [Theory]
    [InlineData("A", "2 h 01 min")]
    [InlineData("H1", "2 h 38 min")]
    [InlineData("LK", "34 min")]
    [InlineData("OX", "4 h 29 min")]
    public void Plan_TotalsMatchMock(string id, string want) => Assert.Equal(want, ProgramPlanner.Hm(ProgramPlanner.Plan(Lib(id).Steps).Total));

    [Fact]
    public void Plan_StepDurationsAndPoints()
    {
        var pl = ProgramPlanner.Plan(Lib("A").Steps);
        Assert.Equal(new[] { "51 min", "3 min", "45 min", "22 min" }, pl.Bounds.Select(b => ProgramPlanner.Hm(b.M1 - b.M0)));
        Assert.Equal(150, pl.Points.Max(p => p.T));
        Assert.Equal(2.5, pl.Points.Max(p => p.P));
        Assert.Equal(2.5, pl.Points[^1].P);       // 密封：压力保持
        var lk = ProgramPlanner.Plan(Lib("LK").Steps);
        Assert.Equal(0, lk.Points[^1].P);          // 排空：泄到 0
    }

    [Fact]
    public void Issues_OutOfRangeAndCurve()
    {
        Assert.Empty(ProgramIssues.Of(Lib("A").Steps, Schema, "HT-PR8"));
        var ox = ProgramIssues.Of(Lib("OX").Steps, Schema, "HT-PR8");
        var one = Assert.Single(ox);
        Assert.Equal("第 02 步 升压：目标压力 3.40 MPa 超出 HT-PR8 范围 0.01 – 3.14 MPa", one.Msg);
        var p = Lib("A").Clone();
        p.Steps[0].Sel["mode"] = "curve";
        p.Steps[0].Segments.Clear();
        Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Msg.Contains("至少要 1 段"));
        p.Steps[0].Segments.Add(new CurveSeg(250, 3, 10));
        Assert.Contains(ProgramIssues.Of(p.Steps, Schema, "HT-PR8"), x => x.Msg.Contains("第 1 段段目标温度"));
        var e = Lib("A").Clone();
        e.Steps[1].Num["pres"] = double.NaN;
        Assert.Contains("未填", ProgramIssues.Of(e.Steps, Schema, "HT-PR8")[0].Msg);
    }

    [Fact]
    public void Summary_TwoItems()
    {
        Assert.Equal(new[] { ("目标温度", "150.0 °C"), ("等待平衡", "15 min") }, ProgramPlanner.Summary(Lib("A").Steps[0], Schema));
        Assert.Equal(new[] { ("目标压力", "2.50 MPa"), ("压力稳压", "开启") }, ProgramPlanner.Summary(Lib("A").Steps[1], Schema));
        Assert.Equal(("结束方式", "密封 (推荐)"), ProgramPlanner.Summary(Lib("A").Steps[3], Schema)[1]);
    }

    // ———————————————————— HMI 程序 JSON ————————————————————

    [Fact]
    public void ToDoc_PressureInPsiAndCodes()
    {
        var doc = Lib("H1").ToDoc();
        Assert.Equal(6, doc.Steps.Count);
        Assert.Equal(new[] { "purge", "heat", "stir", "press", "react", "end" }, doc.Steps.Select(s => s.Type));
        Assert.Equal(435.1, doc.Steps[3].Params["pres"].GetDouble());     // 3.0 MPa
        Assert.Equal("A", doc.Steps[3].Params["gas"].GetString());
        Assert.Equal("inert", doc.Steps[0].Params["gas"].GetString());
        var back = PcProgram.FromDoc(ParallelReactor.Client.ProgramDoc.FromJson(doc.ToJson(), "x"), "PR8", "x");
        Assert.Equal(3.0, back.Steps[3].N("pres"));
        var c = Lib("A").Clone();
        c.Steps[0].Sel["mode"] = "curve";
        c.Steps[0].Segments.Add(new CurveSeg(80, 3, 20));
        var seg = c.ToDoc().Steps[0].Params["segments"];
        Assert.Equal(JsonValueKind.Array, seg.ValueKind);
        Assert.Equal(80, seg[0].GetProperty("temp").GetDouble());
    }

    [Fact]
    public void ToDef_NamesAndEstimates()
    {
        var def = Lib("A").ToDef(null);
        Assert.Equal("A", def.Id);
        Assert.Equal("恒温反应", def.Steps[2].Name);
        Assert.Equal(StepKind.Heat, def.Steps[0].Type);
        Assert.True(def.Steps[0].EstMin >= 50);
    }

    [Fact]
    public void LibraryJson_RoundTrip()
    {
        var p = Lib("OX");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) p.Write(w);
        using var d = JsonDocument.Parse(ms.ToArray());
        var q = PcProgram.Read(d.RootElement);
        Assert.Equal((p.Id, p.Name, p.Model, p.By, p.Draft, p.Modified), (q.Id, q.Name, q.Model, q.By, q.Draft, q.Modified));
        Assert.Equal(p.Steps.Select(s => s.Name), q.Steps.Select(s => s.Name));
        Assert.Equal("either", q.Steps[2].S("endBy"));
        Assert.Equal(3.4, q.Steps[1].N("pres"));
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
        Assert.Equal(new[] { 100.0, 110, 120, 130, 100, 110, 120, 130 }, Enumerable.Range(1, 8).Select(n => m.PerValue(1, "temp", n)));
        Assert.Equal("Pt/C 5%", m.Samples[5]);
        Assert.True(m.Changed(1, "temp", 1));
        Assert.False(m.Changed(1, "temp", 3));    // 120 = 模板值
        Assert.Equal(4.0, m.StepsFor(4)[3].N("pres"));
        Assert.Equal(3.0, m.StepsFor(5)[3].N("pres"));
        Assert.Equal("H1", m.ProgramFor(5).Id);
    }

    [Fact]
    public void Matrix_IssuesGroupedLikeMock()
    {
        var iss = DemoMatrix().Issues(Ctx());
        Assert.Equal(2, iss.Count);
        Assert.Equal(new MxIssue(MxLevel.No, "第 04 步目标压力：RV4 4.00 MPa · 超出 HT-PR8 范围 0.01 – 3.14 MPa"), iss[0]);
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
        Assert.Equal("2 h 28 min", totals.First(x => x.n == 1).t);
        Assert.Equal("2 h 36 min", totals.First(x => x.n == 2).t);
        Assert.Equal("3 h 01 min", totals.First(x => x.n == 8).t);
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
        Assert.Equal("目标温度：参加的 4 路都设为 100 °C", m.Same(1, "temp", inc, f));
        Assert.All(inc, n => Assert.Equal(100, m.PerValue(1, "temp", n)));
        Assert.Equal("等差填充：先把最后一路（RV4）改成终点值", m.Ramp(1, "temp", inc, f));
        m.SetPer(1, "temp", 4, 130);
        Assert.Equal("目标温度：从 100 到 130 °C 等差填充 4 路", m.Ramp(1, "temp", inc, f));
        Assert.Equal(new[] { 100.0, 110, 120, 130 }, inc.Select(n => m.PerValue(1, "temp", n)));
        Assert.Null(m.Same(1, "temp", new[] { 1 }, f));
        Assert.Equal("溶液体积：参加的 2 路都设为 5.0 mL", m.VolSame(new[] { 1, 3 }));
        Assert.Equal(5.0, m.Vol[3]);
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
        Assert.Contains("s2.heat.temp,02 升温 · 目标温度 °C,100,110,120,130,100,110,120,130", csv);
        Assert.Contains("sample,样品 / 催化剂,Pd/C 5%", csv);
        var n = MatrixPlan.Build(Lib("H1"), 1, Dev1, "x");
        var msg = MatrixCsv.Import(n, csv, chs, new[] { 7 });
        Assert.StartsWith("已导入", msg);
        Assert.Equal(130, n.PerValue(1, "temp", 4));
        Assert.Equal(4.0, n.PerValue(3, "pres", 4));
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
        m.Steps[2].Num["rpm"] = 350;
        m.Record = false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) MatrixJson.Write(w, m, new DateTime(2026, 11, 15, 14, 31, 0));
        using var d = JsonDocument.Parse(ms.ToArray());
        var r = MatrixJson.Read(d.RootElement, Lib("H1"), 1, Dev1);
        Assert.Equal(m.Name, r.Name);
        Assert.False(r.Record);
        Assert.Equal(m.Include, r.Include);
        Assert.True(double.IsNaN(r.Vol[5]));
        Assert.Equal(350, r.Steps[2].N("rpm"));
        Assert.Equal(4.0, r.PerValue(3, "pres", 4));
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
        Assert.Equal("吹扫置换", d.Channel(5)!.Program!.Steps[0].Name);
        // 在跑的通道：守卫不让下发
        var (ok2, why) = fleet.LoadProgram(d, 1, p.ToDef(d.Model));
        Assert.False(ok2);
        Assert.Contains("程序运行中", why);
    }
}
