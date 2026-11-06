using System.IO;
using System.Linq;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>架构测试的源码扫描器自检：掩码与成员切分在常见写法上可靠（否则规则可能「全部通过」）。</summary>
public class SourceScanTests
{
    private const string Sample = """
        namespace X;
        public partial class Foo
        {
            private static readonly string[] Tabs = { "a{", "}b" };
            // 注释里的 { 不算 [RelayCommand]
            [RelayCommand]
            [WriteEntry(nameof(OperatorCommands.TryA), nameof(OperatorCommands.TryB))]
            private void Alpha() { var s = $"{(x ? "}" : "{")} {{ "; Ops.TryA(Src); Ops.TryB(Src, '}'); }

            [RelayCommand]
            [ViewEntry("只切页")]
            private void Beta(string id) => Keyboard.OpenNumeric("x", 1, "", 0, 2, v => { StirOn = true; return Ops.TryC(Src, v); });

            public int Gamma { get; set; } = 3;
            partial void OnGammaChanged(int value) { _stir.Stop(); }
            private sealed class Inner { [RelayCommand] private void Delta() { } }
        }
        """;

    private static System.Collections.Generic.List<SourceMember> Scan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan-{System.Guid.NewGuid():N}.cs");
        File.WriteAllText(path, Sample);
        try { return SourceScan.Members(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mask_BlanksStringsCommentsChars_KeepsLength()
    {
        var m = SourceScan.Mask(Sample);
        Assert.Equal(Sample.Length, m.Length);
        Assert.DoesNotContain("只切页", m);
        Assert.DoesNotContain("注释", m);
        Assert.Equal(m.Count(c => c == '{'), m.Count(c => c == '}'));
        Assert.Contains("(x ?", m);   // 插值洞保留为代码
    }

    [Fact]
    public void Members_SplitAndAttributes()
    {
        var ms = Scan();
        var alpha = ms.Single(x => x.Name == "Alpha");
        Assert.True(SourceScan.HasAttribute(alpha, "RelayCommand"));
        Assert.True(SourceScan.HasAttribute(alpha, "WriteEntry"));
        Assert.False(SourceScan.HasAttribute(alpha, "ViewEntry"));
        Assert.Equal(new[] { "TryA", "TryB" }, SourceScan.WriteEntryCommands(alpha));
        Assert.Contains(".TryB(", alpha.Body);

        var beta = ms.Single(x => x.Name == "Beta");
        Assert.True(SourceScan.HasAttribute(beta, "ViewEntry"));
        Assert.Contains("StirOn = true", beta.Body);
        Assert.Contains("TryC(", beta.Body);

        Assert.Contains(ms, x => x.Name == "Gamma");
        Assert.Contains("_stir.Stop", ms.Single(x => x.Name == "OnGammaChanged").Body);
        var delta = ms.Single(x => x.Name == "Delta");
        Assert.Equal("Inner", delta.Type);
        Assert.EndsWith(":Inner.Delta", delta.Key);
        Assert.Contains(ms, x => x.Name == "Tabs");
    }
}
