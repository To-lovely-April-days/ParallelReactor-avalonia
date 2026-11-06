using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C3：下发快照与来源（编辑中的步骤）完全独立。</summary>
public class StepSnapshotTests
{
    [Fact]
    public void MutatingSource_DoesNotAffectSnapshot()
    {
        var p = new Dictionary<string, object> { ["temp"] = 150.0, ["mode"] = "curve" };
        var segs = new List<(double, double, double)> { (80, 5, 10), (150, 2, 30) };
        var s = StepSnapshot.Create(StepType.Heat, 2, "升温", "曲线", p, segs);

        p["temp"] = 300.0;
        p["extra"] = "x";
        p.Remove("mode");
        segs[0] = (250, 50, 0);
        segs.Clear();

        Assert.Equal(150, s.Num("temp"));
        Assert.Equal("curve", s.Str("mode"));
        Assert.False(s.Params.ContainsKey("extra"));
        Assert.Equal(2, s.Segments.Count);
        Assert.Equal((80.0, 5.0, 10.0), s.Segments[0]);
        Assert.True(s.IsCurve);
        Assert.Equal(2, s.Ordinal);
    }

    [Fact]
    public void Snapshot_IsReadOnly()
    {
        var s = StepSnapshot.Create(StepType.Stir, 1, "搅拌", "", new Dictionary<string, object> { ["rpm"] = 500.0 });
        Assert.False(s.Params is IDictionary<string, object> d && !d.IsReadOnly);
        Assert.False(s.Segments is IList<(double, double, double)> l && !l.IsReadOnly);
        Assert.Empty(s.Segments);
    }

    [Fact]
    public void NumStr_ParseLikeProgramStep()
    {
        var s = StepSnapshot.Create(StepType.Press, 1, "升压", "", new Dictionary<string, object>
        {
            ["pres"] = "42.5",      // 字符串数字按不变区域解析
            ["bad"] = "abc",
            ["gas"] = "气体 B",
            ["num"] = 3.0,
        });
        Assert.Equal(42.5, s.Num("pres"));
        Assert.Equal(7, s.Num("bad", 7));          // 无法解析 → 默认
        Assert.Equal(9, s.Num("missing", 9));      // 缺失 → 默认
        Assert.Equal("气体 B", s.Str("gas"));
        Assert.Equal("d", s.Str("num", "d"));      // 非字符串 → 默认
        Assert.Equal("", s.Str("missing"));
        Assert.False(s.IsCurve);
    }
}
