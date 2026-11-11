using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 逐字省略（同 CSS text-overflow: ellipsis）：最长的前缀 +「…」放进给定宽度。
/// 为什么不用 TextBlock 的 CharacterEllipsis：Avalonia 11.1 的 TextEllipsisHelper 在一行有好几段（中文回退字体段 + 西文段）时把已经量过的段宽重复扣掉，
/// 省略得过早、整段整段地丢（「/高压加氢测试一号/RV1/…」后面空着 25 px，样稿是「/高压加氢测试一号/RV1/设备/加…」，复核）。
/// 按字素切（不切开代理对、组合字符）；宽度按 <see cref="TextLayout"/> 量（与 TextBlock 同一套字体回退）。
/// 文字直接放在 TextBlock / PrText 里时用 <see cref="CharacterEllipsis"/>（TextTrimming 的替身）；自己拼路径行的控件（PropCard）用 <see cref="Prefix"/>。
/// </summary>
public static class TextFit
{
    public const string Ellipsis = "…";

    /// <summary>
    /// 逐字省略的 TextTrimming，代替 TextTrimming.CharacterEllipsis（XAML：TextTrimming="{x:Static pr:TextFit.CharacterEllipsis}"）。
    /// 单行放不下时排出 <see cref="Prefix"/> 的结果（「反应 72 …」，不是 11.1 的「反应 …」），行照样是已省略（TextLine.HasCollapsed：
    /// 审计的「省略要有悬停」照查）；Text 不变，绑定照旧。只给单行、整段一种字体属性的文字用（不分段的 Text，不是 Inlines）。
    /// </summary>
    public static TextTrimming CharacterEllipsis { get; } = new PrefixTrimming();

    /// <summary>
    /// <paramref name="text"/> 整段放得进 <paramref name="maxWidth"/> 时原样返回；否则返回最长的「前缀 + …」（一个字也放不下时只有「…」）。
    /// <paramref name="measure"/> = 一段文字的宽度（含行尾空白）。
    /// </summary>
    public static string Prefix(string? text, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text)) return "";
        if (double.IsNaN(maxWidth) || double.IsInfinity(maxWidth) || measure(text) <= maxWidth + 0.01) return text;
        var starts = StringInfo.ParseCombiningCharacters(text);
        // k = 保留的字素个数（0 … n − 1）：最大的 k 使「前 k 个字素 + …」放得下（宽度随 k 单调不减）
        int lo = 0, hi = starts.Length - 1, best = -1;
        while (lo <= hi)
        {
            int k = (lo + hi) / 2;
            if (measure(Cut(text, starts, k) + Ellipsis) <= maxWidth + 0.01) { best = k; lo = k + 1; }
            else hi = k - 1;
        }
        return best <= 0 ? Ellipsis : Cut(text, starts, best) + Ellipsis;
    }

    private static string Cut(string text, int[] starts, int k) => k <= 0 ? "" : k >= starts.Length ? text : text[..starts[k]];

    /// <summary>
    /// <see cref="CharacterEllipsis"/> 省略后排出来的文字：放得下 = null（不省略）；连「…」都放不下 = ""（什么都不画，同 11.1 自带的省略，
    /// 不让「…」伸到旁边的字上）；否则 = <see cref="Prefix"/> 的「前缀 + …」。
    /// </summary>
    public static string? Trimmed(string? text, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text)) return null;
        var shown = Prefix(text, maxWidth, measure);
        if (shown == text) return null;
        return shown == Ellipsis && measure(Ellipsis) > maxWidth + 0.01 ? "" : shown;
    }

    /// <summary>按 <paramref name="tb"/> 的字体（字族、字号、字重、字形、字宽、字体特性）量一段文字的宽度（含行尾空白）。</summary>
    public static double Width(TextBlock tb, string text)
    {
        ArgumentNullException.ThrowIfNull(tb);
        if (string.IsNullOrEmpty(text)) return 0;
        var face = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        using var tl = new TextLayout(text, face, tb.FontFeatures, tb.FontSize, null);
        return tl.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// <see cref="CharacterEllipsis"/> 的实现：TextLayout 只在行比最大宽度宽时才调 Collapse —— 按段落的字体属性（同一套回退）求 <see cref="Trimmed"/>，
    /// 把「前缀 + …」单独排一行，交出它的已成形的段（字体回退已经做好）当作省略后的行。
    /// </summary>
    private sealed class PrefixTrimming : TextTrimming
    {
        public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo) =>
            new PrefixCollapsing(createInfo.Width, createInfo.TextRunProperties, createInfo.FlowDirection);

        public override string ToString() => "PrefixCharacterEllipsis";
    }

    private sealed class PrefixCollapsing : TextCollapsingProperties
    {
        private readonly TextRunProperties _props;

        public PrefixCollapsing(double width, TextRunProperties props, FlowDirection flowDirection)
        {
            Width = width;
            _props = props;
            FlowDirection = flowDirection;
            Symbol = new TextCharacters(Ellipsis, props);
        }

        public override double Width { get; }
        public override TextRun Symbol { get; }
        public override FlowDirection FlowDirection { get; }

        public override TextRun[]? Collapse(TextLine textLine)
        {
            var shown = Trimmed(string.Concat(textLine.TextRuns.Select(r => r.Text.ToString())), Width, Measure);
            if (shown is null) return null;
            if (shown.Length == 0) return Array.Empty<TextRun>();
            // 不 Dispose：交出去的段归省略后的行所有（行随 TextBlock 的 TextLayout 一起释放）
            var line = new TextLayout(shown, _props.Typeface, _props.FontFeatures, _props.FontRenderingEmSize, _props.ForegroundBrush,
                textDecorations: _props.TextDecorations, flowDirection: FlowDirection);
            return line.TextLines.Count == 0 ? Array.Empty<TextRun>() : line.TextLines[0].TextRuns.ToArray();
        }

        private double Measure(string s)
        {
            using var tl = new TextLayout(s, _props.Typeface, _props.FontFeatures, _props.FontRenderingEmSize, null);
            return tl.WidthIncludingTrailingWhitespace;
        }
    }
}
