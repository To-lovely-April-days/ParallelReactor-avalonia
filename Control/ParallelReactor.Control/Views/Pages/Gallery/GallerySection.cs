using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页的一个分区（MOCK placeholder.js 的 sec(title, sub, body)：白卡 + 浅灰标题栏「标题 16/600 · 说明 14」+ 16 px 内边距的内容）。
/// 外观在 GalleryTheme.axaml 的 ControlTheme 里。每个分区的内容是一个独立的 UserControl（PaletteSection、TypeScaleSection …），
/// 新分区只需在 GalleryPage.axaml 里追加一个 GallerySection。
/// 说明里的「⚠」画成线描图标（Icon.warn，同样稿 WebKit 的描边三角；Avalonia 的字体回退会落到实心的符号字形上），其余是文字。
/// </summary>
public class GallerySection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<GallerySection, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubProperty = AvaloniaProperty.Register<GallerySection, string?>(nameof(Sub));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Sub { get => GetValue(SubProperty); set => SetValue(SubProperty, value); }

    private TextBlock? _sub;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _sub = e.NameScope.Find<TextBlock>("PART_Sub");
        FillSub();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SubProperty) FillSub();
    }

    private void FillSub()
    {
        if (_sub == null) return;
        var text = Sub ?? "";
        ToolTip.SetTip(_sub, string.IsNullOrEmpty(text) ? null : text);
        int i = text.IndexOf('⚠');
        if (i < 0) { _sub.Inlines = null; _sub.Text = text; return; }
        var icon = new IconView { Icon = "warn", Size = 14 };
        icon.Bind(IconView.ForegroundProperty, _sub.GetObservable(TextBlock.ForegroundProperty));
        _sub.Inlines = new InlineCollection
        {
            new Run(text[..i]),
            new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center },
            new Run(text[(i + 1)..]),
        };
    }
}
