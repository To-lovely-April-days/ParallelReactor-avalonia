using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Behaviors;

/// <summary>
/// 混合字重的中文段（Avalonia 11.1 回退缺陷的绕过，见 Theme/UiFonts.CjkStack）：同一个 TextBlock 里，后一段中文会沿用前一段回退到的字体
/// （连字重一起），「急停<b>不需要控制权</b>，连接正常就会发送。」的粗体段画成常规。<c>b:Pr.CjkRuns="True"</c> 让字重与前一段不同的
/// 纯中文 Run 显式用 UiFontCjk（不经回退）—— 粗体段和它后面的一段都满足这个条件。pr:Note 对 TextBlock 内容自动套用。
/// 含西文 / 数字的段不动（换成苹方会改掉西文字形）：这样的段请拆开，ShotAudit 的 font weight 检查会报出来。作者自己设了字体的段也不动。
/// </summary>
public static partial class Pr
{
    public static readonly AttachedProperty<bool> CjkRunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("CjkRuns", typeof(Pr));

    public static bool GetCjkRuns(TextBlock t) => t.GetValue(CjkRunsProperty);
    public static void SetCjkRuns(TextBlock t, bool value) => t.SetValue(CjkRunsProperty, value);

    // 已挂上的 TextBlock（上树、Inlines 增删时再套一次）
    private static readonly AttachedProperty<bool> CjkRunsHookedProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("CjkRunsHooked", typeof(Pr));

    private static void OnCjkRunsChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        if (!GetCjkRuns(tb)) return;
        if (!tb.GetValue(CjkRunsHookedProperty))
        {
            tb.SetValue(CjkRunsHookedProperty, true);
            tb.AttachedToVisualTree += (_, _) => FixCjkRuns(tb);
            if (tb.Inlines is { } inl) inl.CollectionChanged += (_, _) => FixCjkRuns(tb);
        }
        FixCjkRuns(tb);
    }

    /// <summary>纯中文（中文、中文标点、全角符号、空白），没有西文字母和数字。</summary>
    private static bool IsPureCjk(string s)
    {
        bool any = false;
        foreach (var ch in s)
        {
            // 空白与连接符（UiText.NoBreak / Keep 插的 U+2060 / U+FEFF）不算字
            if (char.IsWhiteSpace(ch) || ch == (char)0x2060 || ch == (char)0xFEFF) continue;
            bool cjk = ch is >= '　' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '！' and <= '～';
            if (!cjk) return false;
            any = true;
        }
        return any;
    }

    /// <summary>按上面的规则给 TextBlock 的 Run 设 UiFontCjk（可以重复调用）。</summary>
    public static void FixCjkRuns(TextBlock tb)
    {
        if (tb.Inlines is not { Count: > 1 } inlines) return;
        FontFamily? cjk = null;
        FontWeight? prev = null;
        foreach (var inline in inlines)
        {
            if (inline is not Run r || string.IsNullOrEmpty(r.Text)) continue;
            var w = r.FontWeight;
            if (prev is { } p && p != w && IsPureCjk(r.Text) && !r.IsSet(TextElement.FontFamilyProperty))
            {
                cjk ??= Application.Current?.TryFindResource("UiFontCjk", out var f) == true && f is FontFamily ff ? ff : UiFonts.CreateCjk();
                r.FontFamily = cjk;
            }
            prev = w;
        }
    }
}
