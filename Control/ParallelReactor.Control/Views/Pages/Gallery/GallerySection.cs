using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页的一个分区（MOCK placeholder.js 的 sec(title, sub, body)：白卡 + 浅灰标题栏「标题 16/600 · 说明 14」+ 16 px 内边距的内容）。
/// 外观在 GalleryTheme.axaml 的 ControlTheme 里。每个分区的内容是一个独立的 UserControl（PaletteSection、TypeScaleSection …），
/// 新分区只需在 GalleryPage.axaml 里追加一个 GallerySection。
/// </summary>
public class GallerySection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<GallerySection, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubProperty = AvaloniaProperty.Register<GallerySection, string?>(nameof(Sub));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Sub { get => GetValue(SubProperty); set => SetValue(SubProperty, value); }
}
