using System.Linq;
using ParallelReactor.Control.Controls.Pr;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（v2-stress-1 / v3-states-7 / v4-program-4）：中西文混排的单行文字逐字省略（Controls/Pr/TextFit.CharacterEllipsis，
/// 代替 Avalonia 11.1 的 CharacterEllipsis —— 它在一行有好几段字体时把量过的段宽重复扣掉，整段整段地丢：「反应 72 …」成了「反应 …」，
/// 「连接失败 · 6 s 后…」成了「连接失败 · 6 s …」）。这里查省略后排出来的文字（<see cref="TextFit.Trimmed"/>，Collapse 只做这一步再排版）；
/// 真字体下的结果由截图的树转储核对（程序库说明 rv-noprog313r-1366、资源管理器断线条 fake2-*）。
/// 假字体：中文 / ℃ / 省略号 14、其余 8、空格与「·」4（与真字体一样，西文段和中文段宽度不同）。
/// </summary>
public class M5ReviewTextFitTests
{
    private static double W(string t) => t.Sum(ch => ch is '…' or '℃' ? 14 : ch is ' ' or '·' ? 4 : ch > 0x2E80 ? 14 : 8);

    [Fact]
    public void LibraryNote_KeepsTheLatinRunBeforeTheEllipsis()
    {
        const string note = "120 ℃ · 0.35 MPa · 反应 72 min · 4 步";
        double w = W("120 ℃ · 0.35 MPa · 反应 72 …");
        Assert.Equal("120 ℃ · 0.35 MPa · 反应 72 …", TextFit.Trimmed(note, w, W));
        Assert.Equal("120 ℃ · 0.35 MPa · 反应 72…", TextFit.Trimmed(note, w - 1, W));     // 少 1 px：少一个字（空格），不是整段「72 」
        Assert.Equal("120 ℃ · 0.35 MPa · 反应 7…", TextFit.Trimmed(note, W("120 ℃ · 0.35 MPa · 反应 7…"), W));
    }

    [Fact]
    public void ExplorerRetryText_KeepsTheCjkCharacterThatFits()
    {
        const string retry = "连接失败 · 6 s 后重试";
        Assert.Equal("连接失败 · 6 s 后…", TextFit.Trimmed(retry, W("连接失败 · 6 s 后…") + 3, W));
        Assert.Equal("连接失败 · 6 s …", TextFit.Trimmed(retry, W("连接失败 · 6 s 后…") - 1, W));
    }

    [Fact]
    public void Fits_NothingFits_OnlyTheEllipsisFits()
    {
        const string name = "催化评价-200-长时反应";
        Assert.Null(TextFit.Trimmed(name, W(name), W));                      // 放得下：不省略（TextLayout 不会来问，防御）
        Assert.Null(TextFit.Trimmed(name, double.PositiveInfinity, W));
        Assert.Null(TextFit.Trimmed("", 10, W));
        Assert.Null(TextFit.Trimmed(null, 10, W));
        Assert.Equal("催化评价-200-长时…", TextFit.Trimmed(name, W("催化评价-200-长时…") + 0.5, W));
        Assert.Equal("…", TextFit.Trimmed(name, 14, W));                     // 一个字也放不下：只有「…」
        Assert.Equal("", TextFit.Trimmed(name, 13, W));                      // 连「…」都放不下：什么都不画（不伸到旁边）
    }

    /// <summary>省略的 TextTrimming 是同一个实例（XAML 的 x:Static 引用它），不是 11.1 自带的 CharacterEllipsis。</summary>
    [Fact]
    public void CharacterEllipsis_IsOurOwnTrimming()
    {
        Assert.Same(TextFit.CharacterEllipsis, TextFit.CharacterEllipsis);
        Assert.NotSame(Avalonia.Media.TextTrimming.CharacterEllipsis, TextFit.CharacterEllipsis);
        Assert.NotSame(Avalonia.Media.TextTrimming.None, TextFit.CharacterEllipsis);
    }
}
