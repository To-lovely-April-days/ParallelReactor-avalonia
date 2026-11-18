using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 中文粗体发虚的修正（用户 2026-11-18 的 Windows 截图：「好多加粗的文字都变模糊了」）。
/// 设计里的粗体是 600（SemiBold）。Segoe UI 有真 600，西文 / 数字清楚；中文落到微软雅黑，它只有 300 / 400 / 700 三个字重，
/// 600 匹配到常规体，Avalonia 再用「模拟粗体」把字形描粗（FontSimulations.Bold），小字号下糊成一团。
/// 浏览器（样稿）在 Windows 上遇到同样情况直接用雅黑的 Bold（700）。这里照做：字体集合 <c>fonts:Cjk</c> 转给系统字体，
/// 对没有 600 的中文字体把 501–699 的请求改成 700，拿真粗体字形，不再模拟。字体栈里写 <c>fonts:Cjk#Microsoft YaHei UI</c>（见 UiFonts）。
/// Avalonia 11.1 的字体管理器成员不公开、不能包一层，所以用字体集合（Program.BuildAvaloniaApp 里 ConfigureFonts 注册）；
/// 直接实现 IFontCollection（FontCollectionBase 的 TryMatchCharacter 不能重写，它只查自己缓存过的字形）。
/// </summary>
public sealed class CjkFontCollection : IFontCollection
{
    public const string KeyText = "fonts:Cjk";
    private static readonly Uri KeyUri = new(KeyText);

    /// <summary>没有 600 字重、600 会被模拟加粗的中文字体（Windows 微软雅黑；Linux Noto Sans CJK 只有 500 / 700）。苹方有真 600，不用它。</summary>
    public static readonly IReadOnlyList<string> Families = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans SC" };

    private readonly List<FontFamily> _families = new();

    public CjkFontCollection()
    {
        foreach (var f in Families) _families.Add(new FontFamily(KeyText + "#" + f));
    }

    /// <summary>实际请求的字重：501–699（没有对应字形，会被模拟加粗）→ 700；其它不变。</summary>
    public static FontWeight Effective(FontWeight weight) => (int)weight > 500 && (int)weight < 700 ? FontWeight.Bold : weight;

    public Uri Key => KeyUri;
    public int Count => _families.Count;
    public FontFamily this[int index] => _families[index];
    public IEnumerator<FontFamily> GetEnumerator() => _families.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }

    public void Initialize(IFontManagerImpl fontManager) { }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        // 只认自己的几个字体（字体栈里别的写法照常走系统，不经过这里）；没装这个字体时返回 false，字体栈接着试下一个
        if (!_families.Exists(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))) return false;
        return FontManager.Current.SystemFonts.TryGetGlyphTypeface(familyName, style, Effective(weight), stretch, out glyphTypeface);
    }

    public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName, CultureInfo? culture, out Typeface match)
    {
        match = default;
        foreach (var f in _families)
        {
            if (familyName != null && !string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetGlyphTypeface(f.Name, style, weight, stretch, out var gt) || !gt.TryGetGlyph((uint)codepoint, out _)) continue;
            match = new Typeface(f, style, weight, stretch);
            return true;
        }
        return false;
    }
}
