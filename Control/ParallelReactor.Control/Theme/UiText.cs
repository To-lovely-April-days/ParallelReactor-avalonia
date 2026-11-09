using System.Linq;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using CoreKeep = ParallelReactor.Control.Core.Text.Keep;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 界面上显示的「不乱断行」文字：Core.Text.Keep.Apply（数字与单位、「3.12 MPa ≥ 3.00 MPa」「已关闭 SV11」不拆开，中文词不从中间断开）
/// 再换成当前 UiFont 能正确显示的连接符。报警 / 说明句子在界面上一律用 <see cref="Keep"/>，不要直接用 Core 的 Keep.Apply。
/// 原因（M1 复核实测，Avalonia 11.1 的字体回退缺陷）：首选字体自己有零宽连接符的字形时，连接符后面的中文会留在首选字体里排版，
/// 画成方块（.notdef）—— Inter 对 U+2060 WORD JOINER 出错、对 U+FEFF 正常，系统字体（SF）正好相反；Windows 的 Segoe UI 还没在实机上核对。
/// 所以启动后用当前字体实际排一次「超⁠压⁠关」，选第一个不出方块的连接符（U+2060 → U+FEFF，两者在 UAX #14 里都是 WJ：零宽、禁止断行）；
/// 都不行时不插连接符（中文词可能从中间断开，但不会画方块）。ShotAudit 的 missing glyph 检查兜底。
/// </summary>
public static class UiText
{
    private static string? _joiner;

    /// <summary>当前 UiFont 下使用的连接符（"⁠"、"﻿" 或 ""）。第一次使用时探测（要在 Avalonia 初始化之后）。</summary>
    public static string Joiner => _joiner ??= ProbeJoiner(UiFonts.Create());

    /// <summary>用指定字体栈探测：返回第一个排版后没有 .notdef 字形的连接符；都不行返回空串。</summary>
    public static string ProbeJoiner(FontFamily family)
    {
        var face = new Typeface(family);
        foreach (var j in new[] { "⁠", "﻿" })
        {
            using var layout = new TextLayout("RV" + j + "超" + j + "压" + j + "关 1" + j + "2", face, 15, null);
            var ok = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().All(r => r.ShapedBuffer.All(g => g.GlyphIndex != 0));
            if (ok) return j;
        }
        return "";
    }

    /// <summary>Core.Text.Keep.Apply + 当前字体能显示的连接符。null → ""。</summary>
    public static string Keep(string? s)
    {
        var t = CoreKeep.Apply(s);
        var j = Joiner;
        return j == "⁠" ? t : t.Replace("⁠", j);
    }

    /// <summary>去掉连接符（U+2060 / U+FEFF）、NBSP 换回普通空格（提示、复制到剪贴板、无障碍名用）。null → ""。</summary>
    public static string Plain(string? s) => CoreKeep.Plain(s).Replace("﻿", "");
}
