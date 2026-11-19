using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>v0.8 接口（STEPS-SPEC §6）：PUT 程序 JSON 的解析（新格式 / 旧格式转换）、/program-schema、导出往返。</summary>
public class ProgramJsonTests
{
    private static readonly ProgramLimitSettings Def = new(200, 250, 450, 500, 510);

    private static ProgramParseResult Parse(string json, double leakRate = 2)
    {
        using var doc = JsonDocument.Parse(json);
        return ProgramJson.Parse(doc.RootElement.Clone(), leakRate);
    }

    [Fact]
    public void NewFormat_ParsesWithDefaults_EndAppended()
    {
        var r = Parse("""
            [ { "type": "leak", "params": { "pres": 400 } },
              { "type": "exchange", "params": { "gas": "A", "cycles": 2 } },
              { "type": "stir", "params": { "rpm": 400 } },
              { "type": "hold", "params": { "until": "tempBelow", "temp": 45 } },
              { "type": "react", "params": { "stallOn": "on", "minTime": 10 } } ]
            """, leakRate: 3);
        Assert.Null(r.Error);
        Assert.False(r.Migrated);
        Assert.Equal(new[] { StepType.Leak, StepType.Exchange, StepType.Stir, StepType.Hold, StepType.React, StepType.End }, r.Steps!.Select(s => s.Type));
        Assert.Equal(400, r.Steps[0].Num("pres"));
        Assert.Equal(3, r.Steps[0].Num("maxDrop"));          // 缺省 = 设备设置 LeakRate
        Assert.Equal("stop", r.Steps[0].Str("onFail"));
        Assert.Equal("A", r.Steps[1].Str("gas"));
        Assert.Equal(72.5, r.Steps[1].Num("fill"));
        Assert.Equal("tempBelow", r.Steps[3].Str("until"));
        Assert.Equal(6, r.Steps[5].Ordinal);
        Assert.Empty(ProgramLimits.Validate(r.Steps, Def));
    }

    [Fact]
    public void FlatParams_AndChineseLabels_Accepted()
    {
        var r = Parse("""[ { "type": "press", "gas": "惰性气体", "pres": 100, "regulate": "关闭" } ]""");
        Assert.Null(r.Error);
        Assert.Equal("inert", r.Steps![0].Str("gas"));
        Assert.Equal("off", r.Steps[0].Str("regulate"));
    }

    [Fact]
    public void Legacy_ConvertedAndFlagged()
    {
        var r = Parse("""
            [ { "type": "purge", "params": { "gas": "惰性气体", "cycles": 5, "fill": 3, "vent": 5 } },
              { "type": "heat", "params": { "mode": "fixed", "temp": 120, "wait": 10 } },
              { "type": "press", "params": { "gas": "A", "pres": 100, "regulate": "on" } },
              { "type": "wait", "params": { "mode": "pressure", "time": 20 } },
              { "type": "stir", "params": { "rpm": 300, "dur": 5 } },
              { "type": "react", "params": { "endBy": "either", "time": 120, "mmol": 2 } },
              { "type": "end", "params": { "mode": "vent", "cool": 40 } } ]
            """);
        Assert.Null(r.Error);
        Assert.True(r.Migrated);
        Assert.Equal(new[] { StepType.Exchange, StepType.Heat, StepType.Press, StepType.Hold, StepType.Stir, StepType.Hold, StepType.Stir,
            StepType.React, StepType.Hold, StepType.Vent, StepType.End }, r.Steps!.Select(s => s.Type));
        Assert.Equal("inert", r.Steps[0].Str("gas"));       // label 先换成码再转换
        Assert.Equal(0, r.Steps[0].Num("cycles"));
        Assert.Equal(5, r.Steps[0].Num("lineFlush"));
        Assert.Equal("pressAbove", r.Steps[3].Str("until"));
        Assert.Equal(97, r.Steps[3].Num("pres"));
        Assert.Equal("on", r.Steps[7].Str("mmolOn"));
        Assert.Equal("off", r.Steps[7].Str("stallOn"));
        Assert.Equal(0, r.Steps[9].Num("flush"));
        Assert.Equal(Enumerable.Range(1, 11), r.Steps.Select(s => s.Ordinal));
        Assert.Empty(ProgramLimits.Validate(r.Steps, Def));
    }

    [Fact]
    public void Legacy_OutOfNewRange_ClampedWithNote()
    {
        // 旧吹扫 50 次（新的「先吹扫总管」0–10）、旧搅拌 2000 rpm（声明上限 3000 内，全局上限 RpmMax 由 Validate 再查）
        var r = Parse("""[ { "type": "purge", "params": { "cycles": 50 } }, { "type": "react", "params": { "endBy": "time", "time": 0 } } ]""");
        Assert.Null(r.Error);
        Assert.True(r.Migrated);
        Assert.Equal(10, r.Steps![0].Num("lineFlush"));
        Assert.Equal(1, r.Steps[1].Num("time"));
        Assert.Equal(2, r.Notes.Count);
        Assert.Contains("先吹扫总管 50", r.Notes[0]);
    }

    [Fact]
    public void NewFormat_OutOfRange_Rejected()
    {
        var r = Parse("""[ { "type": "exchange", "params": { "cycles": 50 } } ]""");
        Assert.Equal(OpCodes.ParamOutOfRange, r.Error!.Code);
        Assert.Contains("次数", r.Error.Message);
        Assert.Equal(OpCodes.BadRequest, Parse("""[ { "type": "exchange", "params": { "cycles": 2.5 } } ]""").Error!.Code);
        Assert.Equal(OpCodes.ProgramInvalid, Parse("""[ { "type": "press", "params": { "gas": "N2" } } ]""").Error!.Code);
        Assert.Equal(OpCodes.ProgramInvalid, Parse("""[ { "type": "cool" } ]""").Error!.Code);   // 没有降温步骤
        Assert.Equal(OpCodes.ProgramInvalid, Parse("[]").Error!.Code);
        Assert.Equal(OpCodes.BadRequest, Parse("""{ "type": "end" }""").Error!.Code);
        Assert.Equal(OpCodes.ProgramInvalid, Parse("""[ { "type": "heat", "params": { "mode": "curve" } } ]""").Error!.Code);
    }

    [Fact]
    public void Curve_SegmentsKept()
    {
        var r = Parse("""[ { "type": "heat", "params": { "mode": "curve", "band": 3, "segments": [ { "temp": 80, "rate": 5, "hold": 10 }, { "temp": 120, "rate": 2, "hold": 30 } ] } } ]""");
        Assert.Null(r.Error);
        Assert.True(r.Steps![0].IsCurve);
        Assert.Equal(2, r.Steps[0].Segments.Count);
        Assert.Equal(3, r.Steps[0].Num("band"));
        Assert.Equal(OpCodes.ParamOutOfRange,
            Parse("""[ { "type": "heat", "params": { "mode": "curve", "segments": [ { "temp": 80, "rate": 99, "hold": 10 } ] } } ]""").Error!.Code);
    }

    [Fact]
    public void Export_RoundTrips()
    {
        var r = Parse("""[ { "type": "purge", "params": { "cycles": 3 } }, { "type": "hold", "params": { "until": "pressBelow", "pres": 5 } } ]""");
        var dto = ProgramStepFormat.Format(r.Steps!);
        Assert.Equal(new[] { "exchange", "hold", "end" }, dto.Select(d => d.Type));
        Assert.Equal("inert", dto[0].Params.GetProperty("gas").GetString());
        Assert.Equal("pressBelow", dto[1].Params.GetProperty("until").GetString());
        // 导出的 steps 可以原样再下发，结果相同且不再算转换
        var again = Parse(JsonSerializer.Serialize(dto.Select(d => new { type = d.Type, @params = d.Params })));
        Assert.Null(again.Error);
        Assert.False(again.Migrated);
        Assert.Equal(r.Steps!.Select(s => s.Type), again.Steps!.Select(s => s.Type));
        Assert.Equal(r.Steps!.Select(s => string.Join(",", s.Params.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))),
            again.Steps!.Select(s => string.Join(",", s.Params.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))));
    }

    [Fact]
    public void Schema_ListsOnlyNineTypes_WithVisibleWhenAndPsi()
    {
        var sc = ProgramSchemaBuilder.Build(Def, 7, leakRate: 2.5);
        Assert.Equal(new[] { "leak", "exchange", "stir", "heat", "press", "hold", "react", "vent", "end" }, sc.StepTypes);
        Assert.Equal(7, sc.SettingsRevision);
        var hold = sc.Steps.Single(s => s.Type == "hold");
        var temp = hold.Fields.Single(f => f.Key == "temp");
        Assert.Equal("until", temp.VisibleWhen!.Key);
        Assert.Equal(new[] { "tempAbove", "tempBelow" }, temp.VisibleWhen.In);
        Assert.Equal(200, temp.Max);   // 温度上限 min(TMaxSp, OverTemp − 1)
        var pres = hold.Fields.Single(f => f.Key == "pres");
        Assert.Equal("psi", pres.Unit);
        Assert.Equal(458.9, pres.Max!.Value, 6);   // 压力上限 min(PMaxSp, OP × 0.9 − 0.1)
        Assert.Equal("time", hold.Fields.Single(f => f.Key == "until").DefaultCode);
        var react = sc.Steps.Single(s => s.Type == "react");
        Assert.Equal(new[] { "on" }, react.Fields.Single(f => f.Key == "mmol").VisibleWhen!.In);
        Assert.Null(react.Fields.Single(f => f.Key == "time").VisibleWhen);
        Assert.Equal(2.5, sc.Steps.Single(s => s.Type == "leak").Fields.Single(f => f.Key == "maxDrop").Default);
        Assert.Equal(450, sc.Steps.Single(s => s.Type == "stir").Fields.Single(f => f.Key == "rpm").Max);
        Assert.Empty(sc.Steps.Single(s => s.Type == "end").Fields);
        var heat = sc.Steps.Single(s => s.Type == "heat");
        Assert.Equal("mode", heat.Fields[0].Key);
        Assert.Equal("curve", heat.Fields.Single(f => f.Key == "segments").VisibleWhen!.In.Single());
        Assert.Equal(new[] { "inert", "A", "B" }, sc.Steps.Single(s => s.Type == "press").Fields.Single(f => f.Key == "gas").Options!.Select(o => o.Code));
        // 旧码不在 schema 里
        Assert.DoesNotContain(sc.Steps, s => s.Type is "purge" or "wait");
        // camelCase 序列化：visibleWhen / in
        var json = ApiJson.Serialize(sc);
        Assert.Contains("\"visibleWhen\":{\"key\":\"until\",\"in\":[\"tempAbove\",\"tempBelow\"]}", json);
    }
}
