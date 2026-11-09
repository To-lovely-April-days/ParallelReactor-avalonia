using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 一段文字（样稿 HTML 里的一小段）：<see cref="Bold"/> = &lt;b&gt;（600 t-1）；<see cref="NoWrap"/> = 样稿 .dlg-nw 的编号（0 = 没有）：
/// 编号相同的相邻几段合起来中间不断行（样稿一个 &lt;span class="dlg-nw"&gt; 里可以有粗体段），放不下时整组换到下一行，
/// 例如急停说明里的「急停<b>复位只能在设备触屏上操作</b>，」与「本机不能复位。」是两组（组与组之间可以断开，不留单字孤行）。
/// </summary>
public sealed record TextSeg(string Text, bool Bold = false, int NoWrap = 0);

/// <summary>
/// 由几段文字拼成的一行 / 一段（对话框说明里的「急停<b>不需要控制权</b>，连接正常就会发送。」、「请求会显示在<b>2号机触屏</b>上…」）：
/// <see cref="Segments"/> 一变就重建 Inlines。粗体段用 600 + t-1（同样稿 .pr-note b / .dlg-explain b）。
/// 每段再按文字种类拆开（中文 / 西文数字各自一个 Run）并套用 b:Pr.CjkRuns —— Avalonia 11.1 的字体回退会让粗体段和它后面的中文段沿用前一段的字重
/// （Behaviors/CjkRuns.cs）；别名这类混合文字（「2号机」）拆开后中文部分也能正确加粗。相邻的、粗细与文字种类都相同的片段合成一个 Run：
/// 11.1 在两个 Run 的接缝处有时找不到断行点（会从前一句中间断开），合并之后句号与下一句之间照常可以断行。样式照旧按 TextBlock 匹配。
/// </summary>
public class SegText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<TextSeg>?> SegmentsProperty =
        AvaloniaProperty.Register<SegText, IReadOnlyList<TextSeg>?>(nameof(Segments));

    private const char WordJoiner = (char)0x2060, Bom = (char)0xFEFF;

    protected override System.Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<TextSeg>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty) Rebuild();
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        if (Segments is not { } segs) return;
        // （文字, 粗体, 是不是中文）的片段：不断行组里每两个字之间、组内相邻两段的接缝处都插连接符（UiText.NoBreak / Joiner）
        var parts = new List<(string Text, bool Bold, bool Cjk)>();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var text = s.NoWrap > 0 ? UiText.NoBreak(s.Text) : s.Text;
            if (s.NoWrap > 0 && i > 0 && segs[i - 1].NoWrap == s.NoWrap) text = UiText.Joiner + text;
            foreach (var (part, cjk) in SplitScript(text))
            {
                if (parts.Count > 0 && parts[^1].Bold == s.Bold && parts[^1].Cjk == cjk) parts[^1] = (parts[^1].Text + part, s.Bold, cjk);
                else parts.Add((part, s.Bold, cjk));
            }
        }
        foreach (var (text, bold, _) in parts)
        {
            var r = new Run(text);
            if (bold)
            {
                r.FontWeight = FontWeight.SemiBold;
                r.Foreground = Palette.T1Brush;
            }
            inlines.Add(r);
        }
        Behaviors.Pr.FixCjkRuns(this);
    }

    /// <summary>按「中文 / 其它」拆开（连接符、空白跟着前一段走）：（片段, 是不是中文）。</summary>
    private static IEnumerable<(string Text, bool Cjk)> SplitScript(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        int start = 0;
        int? kind = null;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (char.IsWhiteSpace(ch) || ch == WordJoiner || ch == Bom) continue;
            int k = IsCjk(ch) ? 1 : 0;
            if (kind is int prev && prev != k)
            {
                yield return (s[start..i], prev == 1);
                start = i;
            }
            kind = k;
        }
        yield return (s[start..], kind == 1);
    }

    private static bool IsCjk(char ch) => ch is >= '　' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '！' and <= '～';
}
