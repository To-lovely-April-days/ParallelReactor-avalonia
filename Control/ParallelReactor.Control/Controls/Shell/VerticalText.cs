using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 竖排文字（融合版 F2；样稿停靠面板竖条上的名字 .rl：writing-mode: vertical-rl、14 px、字距 .14em）。Avalonia 没有竖排：
/// 一个字一行、自上而下，每个字水平居中；每个字占 字号 + <see cref="Spacing"/>（同 CSS 竖排时字距加在每个字后面），最后一个字后面的字距不算高度。
/// 宽 = 最宽的字（向上取整）。只给中文短名字用（「设备」「检查器」「共用资源」）。
/// </summary>
public sealed class VerticalText : Avalonia.Controls.Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<VerticalText, string?>(nameof(Text));
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<VerticalText, double>(nameof(Spacing));
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<VerticalText>();
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<VerticalText>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<VerticalText>();

    static VerticalText()
    {
        AffectsMeasure<VerticalText>(TextProperty, SpacingProperty, FontSizeProperty, FontFamilyProperty);
        AffectsRender<VerticalText>(ForegroundProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>字距（px，加在每个字后面）。</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private readonly List<FormattedText> _chars = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _chars.Clear();
        var s = Text ?? "";
        var face = new Typeface(FontFamily);
        double w = 0;
        foreach (var ch in s)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, Foreground);
            _chars.Add(ft);
            if (ft.Width > w) w = ft.Width;
        }
        double h = _chars.Count == 0 ? 0 : _chars.Count * (FontSize + Spacing) - Spacing;
        return new Size(System.Math.Ceiling(w), System.Math.Ceiling(h));
    }

    public override void Render(DrawingContext context)
    {
        if (_chars.Count == 0) return;
        double step = FontSize + Spacing;
        for (int i = 0; i < _chars.Count; i++)
        {
            var ft = _chars[i];
            if (Foreground is { } fg) ft.SetForegroundBrush(fg);
            // 字框高（行高）比字号高：按字号的一格居中
            double y = i * step + (FontSize - ft.Height) / 2;
            context.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, y));
        }
    }
}
