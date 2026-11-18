using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>冻结步骤 → §6.9 程序 JSON（GET /channels/{id}/program/run 的 steps，v0.6）：sel 字段输出英文枚举码，heat 带 mode / segments。</summary>
public class ProgramStepFormatTests
{
    private static StepSnapshot Step(StepType t, string name, params (string K, object V)[] p)
        => StepSnapshot.Create(t, 1, name, name + " 摘要", p.Select(x => new KeyValuePair<string, object>(x.K, x.V)));

    [Fact]
    public void SelLabels_BecomeCodes_NumbersStayNumbers()
    {
        var steps = new[]
        {
            Step(StepType.Purge, "吹扫", ("gas", "惰性气体"), ("cycles", 10.0), ("fill", 3.0), ("vent", 5.0)),
            Step(StepType.Press, "升压", ("gas", "气体 B"), ("pres", 50.5), ("regulate", "关闭")),
            Step(StepType.Wait, "等待", ("mode", "等待压力到位"), ("time", 30.0)),
            Step(StepType.React, "反应", ("endBy", "两者先到"), ("time", 120.0), ("mmol", 1.5)),
            Step(StepType.End, "结束", ("mode", "密封 (推荐)"), ("cool", 40.0)),
        };
        var dto = ProgramStepFormat.Format(steps);
        Assert.Equal(new[] { "purge", "press", "wait", "react", "end" }, dto.Select(d => d.Type));
        Assert.Equal("inert", dto[0].Params.GetProperty("gas").GetString());
        Assert.Equal(JsonValueKind.Number, dto[0].Params.GetProperty("cycles").ValueKind);
        Assert.Equal(10, dto[0].Params.GetProperty("cycles").GetDouble());
        Assert.Equal("B", dto[1].Params.GetProperty("gas").GetString());
        Assert.Equal(50.5, dto[1].Params.GetProperty("pres").GetDouble());
        Assert.Equal("off", dto[1].Params.GetProperty("regulate").GetString());
        Assert.Equal("pressure", dto[2].Params.GetProperty("mode").GetString());
        Assert.Equal("either", dto[3].Params.GetProperty("endBy").GetString());
        Assert.Equal("seal", dto[4].Params.GetProperty("mode").GetString());
        Assert.Equal("吹扫", dto[0].Name);
        Assert.Equal("吹扫 摘要", dto[0].Detail);
    }

    [Fact]
    public void Heat_FixedAndCurve()
    {
        var fixedStep = Step(StepType.Heat, "升温", ("temp", 150.0), ("wait", 15.0));   // 没有 mode 参数 → fixed
        var curve = StepSnapshot.Create(StepType.Heat, 2, "升温", "曲线",
            new[] { new KeyValuePair<string, object>("mode", "curve"), new("temp", 150.0), new("wait", 10.0) },
            new[] { (80.0, 2.0, 20.0), (150.0, 1.0, 60.0) });
        var f = ProgramStepFormat.Format(fixedStep);
        Assert.Equal("fixed", f.Params.GetProperty("mode").GetString());
        Assert.False(f.Params.TryGetProperty("segments", out _));
        Assert.Equal("mode", f.Params.EnumerateObject().First().Name);
        var c = ProgramStepFormat.Format(curve);
        Assert.Equal("curve", c.Params.GetProperty("mode").GetString());
        Assert.Single(c.Params.EnumerateObject(), p => p.Name == "mode");   // 不重复输出参数里的 mode
        var segs = c.Params.GetProperty("segments");
        Assert.Equal(2, segs.GetArrayLength());
        Assert.Equal(new[] { "temp", "rate", "hold" }, segs[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(150, segs[1].GetProperty("temp").GetDouble());
        Assert.Equal(60, segs[1].GetProperty("hold").GetDouble());
    }

    [Fact]
    public void Output_SerializesThroughApiJson()
    {
        // 走源生成上下文：JsonElement 参数原样嵌入
        var dto = ProgramStepFormat.Format(Step(StepType.Stir, "搅拌", ("rpm", 300.0), ("dur", 5.0)));
        var json = ApiJson.Serialize(dto);
        Assert.Equal("{\"type\":\"stir\",\"params\":{\"rpm\":300,\"dur\":5},\"name\":\"搅拌\",\"detail\":\"搅拌 摘要\"}", json);
    }
}
