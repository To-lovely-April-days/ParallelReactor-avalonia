using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 面包屑（样稿 ui.crumb / .pr-crumb）：「1号机 › RV1 › 属性查看器」—— 前几段 t-3 400，最后一段 t-1 600，分隔符 › t-4，间距 8。
/// 字号继承（面板标题里 16 px）。<see cref="Text"/> 用「 › 」分段（也接受「›」两边不带空格）。放不下时最后一段省略（全文在面板标题的提示里）。
/// </summary>
public sealed class Crumb : FlexRow
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Crumb, string?>(nameof(Text));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public Crumb() => Spacing = 8;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var parts = (Text ?? "").Split('›', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (i > 0) Children.Add(new TextBlock { Text = "›", Foreground = Palette.T4Brush, FontWeight = FontWeight.Normal });
            var tb = new TextBlock
            {
                Text = parts[i],
                Foreground = last ? Palette.T1Brush : Palette.T3Brush,
                FontWeight = last ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // 最后一段先缩（前面的设备名 / RV 保持完整）
            SetShrink(tb, last ? 1 : 0.01);
            Children.Add(tb);
        }
    }
}
