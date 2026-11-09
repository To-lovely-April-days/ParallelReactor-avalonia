using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ParallelReactor.Control.Core.Text;

/// <summary>
/// 断行保护：样稿 PR.nbsp / PR.keep（js/core.js）的移植，外加给 Avalonia 用的 keep-all 替代（PORT-PLAN §4.3）。
/// <list type="bullet">
/// <item><see cref="Nbsp"/>：数字与单位之间换成 U+00A0（「已用 18 min」的 min 不会折到下一行）。与样稿逐字节相同。</item>
/// <item><see cref="Web"/>：与样稿 PR.keep 逐字节相同（只插 U+00A0）：「3.12 MPa ≥ 3.00 MPa」整段不断开，「 · 」只在点后面断，
///   中文词与后面的编号 / 数字不断开（「已关闭 SV11」）。样稿把它和 CSS word-break: keep-all 一起用（.pr-keep）。</item>
/// <item><see cref="Apply"/>：Avalonia 用 = Web + <see cref="JoinCjk"/>。Avalonia 按 UAX #14 断行，汉字之间处处可断，又没有 keep-all，
///   所以在中文词内部插 U+2060 WORD JOINER（零宽、禁止断行）。这样断点与样稿渲染一致：只在普通空格、标点处断。</item>
/// </list>
/// 正则照抄样稿。JS 的 \s 与 .NET 不同（含 U+FEFF、不含 U+0085），\d 只算 0–9，所以字符类都显式写出。
/// 显示用的文字才过 Keep；提示 / 复制用原文或 <see cref="Plain"/>。
/// </summary>
public static class Keep
{
    public const char NoBreakSpace = (char)0xA0;
    public const char WordJoiner = (char)0x2060;

    /// <summary>JS 正则 \s 的成员（WhiteSpace + LineTerminator）：TAB VT FF SP NBSP ZWNBSP + 全部 Zs + LF CR LS PS。</summary>
    private const string JsSpace = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    private const RegexOptions Opt = RegexOptions.CultureInvariant;

    /// <summary>PR.nbsp：/(\d)\s+(?=(min|s|h|℃|°C|MPa|psi|rpm|%|mL)(?![A-Za-z]))/g → '$1' + U+00A0。</summary>
    private static readonly Regex UnitGap = new("([0-9])[" + JsSpace + "]+(?=(min|s|h|℃|°C|MPa|psi|rpm|%|mL)(?![A-Za-z]))", Opt);
    /// <summary>「4.14 MPa ≥ 3.00 MPa」：运算符两边的空格都不断开。</summary>
    private static readonly Regex OpGap = new("([^·" + JsSpace + "]) (≥|≤|>|<|=|→) ", Opt);
    /// <summary>「… · ≤ 200 ℃」：点后面照样能断行，运算符与后面的数不断开。</summary>
    private static readonly Regex DotOp = new("· (≥|≤|>|<|=|→) ", Opt);
    /// <summary>中文字与后面的字母 / 数字之间的空格不断开（「已关闭 SV11」「超压 3.12」）。</summary>
    private static readonly Regex CjkGap = new("([\\u4e00-\\u9fff]) (?=[A-Za-z0-9])", Opt);

    /// <summary>样稿 PR.nbsp。null → ""。</summary>
    public static string Nbsp(string? s) => s is null ? "" : UnitGap.Replace(s, m => m.Groups[1].Value + NoBreakSpace);

    /// <summary>样稿 PR.keep（逐字节相同；浏览器配合 keep-all 用）。null → ""。</summary>
    public static string Web(string? s)
    {
        string t = Nbsp(s);
        t = OpGap.Replace(t, m => m.Groups[1].Value + NoBreakSpace + m.Groups[2].Value + NoBreakSpace);
        t = DotOp.Replace(t, m => "· " + m.Groups[1].Value + NoBreakSpace);
        t = t.Replace(" · ", NoBreakSpace + "· ");
        t = CjkGap.Replace(t, m => m.Groups[1].Value + NoBreakSpace);
        return t;
    }

    /// <summary>
    /// Avalonia 的 TextBlock 用：<see cref="Web"/> + <see cref="JoinCjk"/>（报警 / 事件 / 说明句子，配合 TextWrapping.Wrap）。
    /// 界面上请用 App 的 Theme.UiText.Keep：它在这之后把 U+2060 换成当前字体能正确显示的连接符（Avalonia 11.1 下首选字体是 Inter 时，
    /// U+2060 后面的中文会画成方块，M1 复核实测）。
    /// </summary>
    public static string Apply(string? s) => JoinCjk(Web(s));

    /// <summary>
    /// keep-all 的替代（CSS Text 3：取消 NU / AL / AI / ID 类「字母单位」之间的断行机会）：相邻两个字母单位
    /// （汉字 / 假名 / 谚文；拉丁字母、数字；· × ÷ ≥ ≤ ≠ ≈ 箭头这些 AI 类符号）中至少有一个是汉字时，中间插 U+2060。
    /// 空格、标点（「（」「，」「-」「、」等）、℃、% 处的断行机会不变；拉丁字母、数字之间 UAX #14 本来就不断，不插。已有 WJ 不重复插。
    /// </summary>
    public static string JoinCjk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        StringBuilder? sb = null;
        Unit prev = Unit.Other;
        for (int i = 0; i < s.Length;)
        {
            int cp, len;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); len = 2; }
            else { cp = s[i]; len = 1; }
            Unit u = Classify(cp);
            if ((prev == Unit.Cjk && u != Unit.Other) || (prev == Unit.Letter && u == Unit.Cjk))
            {
                sb ??= new StringBuilder(s.Length + 16).Append(s, 0, i);
                sb.Append(WordJoiner);
            }
            sb?.Append(s, i, len);
            prev = u;
            i += len;
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>
    /// 整段不断开（样稿 white-space: nowrap 的一小段，如进气阀列表的「SV1 开」「开 SV1–SV6」「全部关闭」、急停红框里的「1号机触屏」）：
    /// 每两个字之间插 U+2060（代理对中间不插）、空格换成 U+00A0。界面上请经 App 的 Theme.UiText.Joiners / NoBreak 换成当前字体能显示的连接符。null → ""。
    /// </summary>
    public static string NoBreak(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string t = s.Replace(' ', NoBreakSpace);
        var sb = new StringBuilder(t.Length * 2);
        for (int i = 0; i < t.Length; i++)
        {
            if (i > 0 && !char.IsLowSurrogate(t[i])) sb.Append(WordJoiner);
            sb.Append(t[i]);
        }
        return sb.ToString();
    }

    /// <summary>去掉 U+2060、把 U+00A0 换回普通空格（提示、复制到剪贴板用）。null → ""。</summary>
    public static string Plain(string? s) => s is null ? "" : s.Replace(WordJoiner.ToString(), "").Replace(NoBreakSpace, ' ');

    private enum Unit { Other, Letter, Cjk }

    private static Unit Classify(int cp)
    {
        if (IsCjk(cp)) return Unit.Cjk;
        if (cp < 0x80) return cp is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? Unit.Letter : Unit.Other;
        switch (cp)
        {
            case 0xB7: case 0xD7: case 0xF7:                     // · × ÷
            case 0x2264: case 0x2265: case 0x2260: case 0x2248:  // ≤ ≥ ≠ ≈
            case 0x2190: case 0x2191: case 0x2192: case 0x2193:  // ← ↑ → ↓
                return Unit.Letter;
        }
        return CharUnicodeInfo.GetUnicodeCategory(cp) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => Unit.Letter,
            _ => Unit.Other,
        };
    }

    /// <summary>UAX #14 的 ID 类里会成词的部分：汉字（含扩展区、兼容区、部首）、〆〇、假名、注音、谚文。</summary>
    private static bool IsCjk(int cp) =>
        cp is >= 0x2E80 and <= 0x2FDF
           or 0x3006 or 0x3007
           or >= 0x3040 and <= 0x30FF
           or >= 0x3100 and <= 0x318F
           or >= 0x31A0 and <= 0x31FF
           or >= 0x3400 and <= 0x4DBF
           or >= 0x4E00 and <= 0x9FFF
           or >= 0xAC00 and <= 0xD7A3
           or >= 0xF900 and <= 0xFAFF
           or >= 0x20000 and <= 0x3FFFF;
}
