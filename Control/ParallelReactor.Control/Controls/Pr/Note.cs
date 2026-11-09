using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>提示条色调（样稿 ui.note 的 tone）：中性 · warn（中性 + ⚠，不用琥珀色）· err（红）· ok（✓）· info（强调色浅底）。</summary>
public enum NoteTone { Neutral, Warn, Err, Ok, Info }

/// <summary>
/// 提示条（样稿 ui.note / .pr-note）：内边距 10 14、圆角 6、1 px line-soft、bg-head 底；图标 17（t-3）+ 14 px t-2 正文（行高 1.55，自动换行）。
/// 内容是文字或带粗体（t-1 600）的 TextBlock。报警 / 说明句子请先用 Theme.UiText.Keep 处理（Core.Text.Keep.Apply：中文词不从中间断开；
/// 再换成当前字体能正确显示的连接符，见 UiText）。
/// 粗体段用 Run 时，内容 TextBlock 自动套用 b:Pr.CjkRuns（Avalonia 11.1 回退沿用上一段字重的缺陷：粗体段和它后面的中文段改用 UiFontCjk，
/// 见 Behaviors/CjkRuns.cs）；粗体段里有西文 / 数字时请拆开，ShotAudit 会报。
/// </summary>
public sealed class Note : ContentControl
{
    public static readonly StyledProperty<NoteTone> ToneProperty = AvaloniaProperty.Register<Note, NoteTone>(nameof(Tone));
    public static readonly DirectProperty<Note, string> ToneIconProperty = AvaloniaProperty.RegisterDirect<Note, string>(nameof(ToneIcon), o => o.ToneIcon);

    public NoteTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    private string _icon = "info";
    /// <summary>色调对应的图标（err ⚠ 圆、warn 三角、ok ✓、其它 ⓘ）。</summary>
    public string ToneIcon { get => _icon; private set => SetAndRaise(ToneIconProperty, ref _icon, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && change.NewValue is TextBlock tb) Behaviors.Pr.SetCjkRuns(tb, true);
        if (change.Property != ToneProperty) return;
        var t = Tone;
        ToneIcon = t switch { NoteTone.Err => "alert", NoteTone.Warn => "warn", NoteTone.Ok => "check", _ => "info" };
        PseudoClasses.Set(":warn", t == NoteTone.Warn);
        PseudoClasses.Set(":err", t == NoteTone.Err);
        PseudoClasses.Set(":ok", t == NoteTone.Ok);
        PseudoClasses.Set(":info", t == NoteTone.Info);
    }
}

/// <summary>
/// 禁用原因行（样稿 ui.reasonLine / .pr-reason）：锁图标 15 + 原因 14 px t-3（不用琥珀色），写在被禁用按钮的下方，上边距 8。
/// </summary>
public sealed class ReasonLine : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ReasonLine, string?>(nameof(Text));
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>
/// 空状态（样稿 ui.empty / .pr-empty）：居中一列，图标 32 t-dis、标题 15/600 t-2、说明 14 t-3，间距 8，内边距 32 16。
/// </summary>
public sealed class Empty : TemplatedControl
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<Empty, string?>(nameof(Icon), "info");
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Empty, string?>(nameof(Title), "暂无数据");
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Empty, string?>(nameof(Text));

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
