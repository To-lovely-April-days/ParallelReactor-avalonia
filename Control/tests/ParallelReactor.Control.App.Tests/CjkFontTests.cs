using Avalonia.Media;
using ParallelReactor.Control.Theme;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 中文粗体不模拟加粗（2026-11-18 用户的 Windows 截图：粗体中文发虚）：微软雅黑 / Noto CJK 没有 600，fonts:Cjk 把 501–699 改成真 700；
/// Windows / Linux 的字体栈里中文都经 fonts:Cjk 取（西文 Segoe UI / Inter 不经过它，Segoe UI 有真 600）。
/// </summary>
public class CjkFontTests
{
    [Theory]
    [InlineData(400, 400)]
    [InlineData(500, 500)]
    [InlineData(600, 700)]
    [InlineData(650, 700)]
    [InlineData(700, 700)]
    [InlineData(300, 300)]
    public void SemiboldBecomesRealBold(int want, int got) => Assert.Equal(got, (int)CjkFontCollection.Effective((FontWeight)want));

    [Fact]
    public void Stacks_RouteChineseThroughTheCollection()
    {
        Assert.Equal("Segoe UI, fonts:Cjk#Microsoft YaHei UI, fonts:Cjk#Microsoft YaHei, fonts:Inter#Inter", UiFonts.WindowsStack);
        Assert.StartsWith("fonts:Cjk#", UiFonts.WindowsCjk);
        Assert.Contains("fonts:Cjk#Noto Sans CJK SC", UiFonts.OtherStack);
        Assert.DoesNotContain("fonts:Cjk", UiFonts.MacStack);     // 苹方有真 600
        var c = new CjkFontCollection();
        Assert.Equal(CjkFontCollection.KeyText, c.Key.ToString().TrimEnd('/'));
        Assert.Contains(c, f => f.Name == "Microsoft YaHei UI");
    }
}
