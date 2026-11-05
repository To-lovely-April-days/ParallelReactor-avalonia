using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>程序「升温·曲线」步骤的（温度/斜率/保温）→ 曲线引擎分段（温度, 分钟）换算（纯逻辑）。</summary>
public class HeatCurvePlanTests
{
    [Fact]
    public void RampAndHold_ProducesRampThenHold()
    {
        var conv = HeatCurvePlan.ToProfileSegments(new List<(double, double, double)> { (80, 5, 10) }, startTemp: 25);
        Assert.Equal(2, conv.Count);
        Assert.Equal((80, 11), conv[0]);   // 斜坡：(80−25)/5 = 11 分钟
        Assert.Equal((80, 10), conv[1]);   // 保温：10 分钟
    }

    [Fact]
    public void ChainedSegments_RampStartsFromPreviousTarget()
    {
        var conv = HeatCurvePlan.ToProfileSegments(
            new List<(double, double, double)> { (80, 5, 0), (130, 2.5, 20) }, startTemp: 25);
        Assert.Equal(3, conv.Count);
        Assert.Equal((80, 11), conv[0]);    // 25 → 80
        Assert.Equal((130, 20), conv[1]);   // 80 → 130，(130−80)/2.5 = 20 分钟
        Assert.Equal((130, 20.0), conv[2]); // 保温 20 分钟
    }

    [Fact]
    public void ZeroRate_WithTempChange_IsStepChange()
    {
        var conv = HeatCurvePlan.ToProfileSegments(new List<(double, double, double)> { (100, 0, 0) }, startTemp: 25);
        Assert.Single(conv);
        Assert.Equal((100, 0.01), conv[0]);   // 斜率 0 = 阶跃（0.01 分钟）
    }

    [Fact]
    public void SameTemp_SkipsRamp()
    {
        var conv = HeatCurvePlan.ToProfileSegments(new List<(double, double, double)> { (25, 5, 30) }, startTemp: 25);
        Assert.Single(conv);
        Assert.Equal((25, 30), conv[0]);   // 温度没变：只有保温段
    }

    [Fact]
    public void NoRampNoHold_ReturnsEmpty()
    {
        var conv = HeatCurvePlan.ToProfileSegments(new List<(double, double, double)> { (25, 5, 0) }, startTemp: 25);
        Assert.Empty(conv);
    }

    [Fact]
    public void OverMaxSegments_IsTruncated()
    {
        var segs = new List<(double, double, double)>();
        for (int i = 0; i < 10; i++) segs.Add((50 + i * 10, 100, 5));   // 每段 = 斜坡 + 保温 = 20 段
        var conv = HeatCurvePlan.ToProfileSegments(segs, startTemp: 25);
        Assert.Equal(16, conv.Count);
    }
}
