using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>进度条色调（样稿 ui.progress 的 tone）。Ok = 已完成浅绿；Err 报警；Info 强调色；Mute / Warn 灰。</summary>
public enum ProgressTone { Ok, Err, Info, Mute, Warn }

/// <summary>
/// 进度条（样稿 ui.progress / .pr-progress，原稿时间轴样式）：高 10、白底 1 px line-2 边框，填充是浅绿渐变（g-2 → g-3）；
/// 运行中（<see cref="IsRunning"/>）更深（g-2 → g-4）+ 填充末端 1 px g-dot-line，并缓慢呼吸（冻结时不动）。
/// 报警 r-bg → r-line；强调 a-tint → a-soft；灰 bg-press。渐变按填充部分的宽度展开（同 CSS 的 i 元素）。
/// </summary>
public sealed class ProgressPr : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressPr, double>(nameof(Value));
    public static readonly StyledProperty<ProgressTone> ToneProperty = AvaloniaProperty.Register<ProgressPr, ProgressTone>(nameof(Tone));
    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<ProgressPr, bool>(nameof(IsRunning));

    static ProgressPr()
    {
        AffectsRender<ProgressPr>(ValueProperty, ToneProperty, IsRunningProperty);
        HeightProperty.OverrideDefaultValue<ProgressPr>(10);
    }

    /// <summary>0..1（超出范围按边界画）。</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public ProgressTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    /// <summary>运行中：更深的绿 + 末端线 + 呼吸。</summary>
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    internal static readonly IBrush DoneFill = Gradient(Palette.G2Color, Palette.G3Color);
    internal static readonly IBrush RunFill = Gradient(Palette.G2Color, Palette.G4Color);
    private static readonly IBrush ErrFill = Gradient(Palette.RBgColor, Palette.RLineColor);
    private static readonly IBrush InfoFill = Gradient(Palette.ATintColor, Palette.ASoftColor);

    internal static IBrush Gradient(Color a, Color b) => new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, a), new ImmutableGradientStop(1, b) },
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    private IBrush Fill => IsRunning && Tone == ProgressTone.Ok ? RunFill : Tone switch
    {
        ProgressTone.Err => ErrFill,
        ProgressTone.Info => InfoFill,
        ProgressTone.Mute or ProgressTone.Warn => Palette.BgPressBrush,
        _ => DoneFill,
    };

    public override void Render(DrawingContext context)
    {
        var b = new Rect(Bounds.Size);
        if (b.Width < 2 || b.Height < 2) return;
        // 白底 + 1 px line-2 边框（边框画在内侧）
        context.DrawRectangle(Palette.BgCardBrush, Palette.Pen(Palette.Line2Color, 1), b.Deflate(0.5));
        var inner = b.Deflate(1);
        var w = Math.Round(inner.Width * Math.Clamp(Value, 0, 1));   // 整像素（填充末端的 1 px 线不发虚）
        if (w <= 0) return;
        var fill = new Rect(inner.X, inner.Y, w, inner.Height);
        context.DrawRectangle(Fill, null, fill);
        if (IsRunning && Tone == ProgressTone.Ok)
        {
            // 末端 1 px g-dot-line（inset -1px 0 0）+ 呼吸
            context.DrawRectangle(Palette.GDotLineBrush, null, new Rect(fill.Right - 1, fill.Y, 1, fill.Height));
            if (_pulsing) context.DrawRectangle(PulseClock.Overlay, null, fill);
        }
    }

    // ---------------- 呼吸：只有运行中、已上树、未冻结时订阅共用计时器 ----------------
    private bool _pulsing, _attached;

    private void UpdatePulse()
    {
        bool on = IsRunning && Tone == ProgressTone.Ok && IsVisible && _attached && !Frozen.GetIsFrozen(this);
        if (on == _pulsing) return;
        _pulsing = on;
        PulseClock.Set(this, on);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; UpdatePulse(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _attached = false; UpdatePulse(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRunningProperty || change.Property == ToneProperty || change.Property == IsVisibleProperty || change.Property == Frozen.IsFrozenProperty)
            UpdatePulse();
    }
}
