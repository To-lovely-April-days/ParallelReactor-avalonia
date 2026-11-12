using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>一个色块：令牌名（--bg-app）、十六进制、画刷；悬停提示给出 XAML / C# 键名。</summary>
public sealed record PaletteSwatch(string Name, string Hex, IBrush Brush, string Tip);

/// <summary>一组色块（底色、线、文字、强调色 …，分组同 MOCK #/gallery）。</summary>
public sealed record PaletteGroup(string Title, IReadOnlyList<PaletteSwatch> Swatches);

public partial class PaletteSection : UserControl
{
    /// <summary>67 个颜色令牌按 Palette.Tokens 的顺序分组（GroupBy 保留首次出现的顺序 = 样稿的分组顺序）。</summary>
    public static IReadOnlyList<PaletteGroup> Groups { get; } = Palette.Tokens
        .GroupBy(t => t.Group)
        .Select(g => new PaletteGroup(g.Key, g.Select(t => new PaletteSwatch(t.Css, t.Hex, new ImmutableSolidColorBrush(t.Color), $"{t.Key}Color / {t.Key}Brush")).ToList()))
        .ToList();

    public PaletteSection() => InitializeComponent();
}
