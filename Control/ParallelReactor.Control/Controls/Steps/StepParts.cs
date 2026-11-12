using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Steps;

/// <summary>
/// 1 px 虚线框（样稿 border-style: dashed：WebKit 的 1 px 虚线是 3 px 线 + 3 px 空）：画在自己的框的内侧。中止的步骤卡 / 条件框（line）、
/// 程序库预览的步骤卡（line-2）用它代替实线边框。只画线，不挡点击（IsHitTestVisible = false）。
/// </summary>
public sealed class DashFrame : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<DashFrame, Color>(nameof(Color), Palette.LineColor);
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<DashFrame, double>(nameof(StrokeWidth), 1);
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<DashFrame, double>(nameof(Radius));

    static DashFrame()
    {
        AffectsRender<DashFrame>(ColorProperty, StrokeWidthProperty, RadiusProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DashFrame>(false);
    }

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    /// <summary>线宽（1；未安装的当前位置 2）。虚线按线宽的倍数：3 倍线 + 3 倍空（同 WebKit）。</summary>
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    /// <summary>圆角（位置按钮 6）。</summary>
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public override void Render(DrawingContext context)
    {
        var b = new Rect(Bounds.Size);
        double w = StrokeWidth;
        if (b.Width < 2 * w || b.Height < 2 * w) return;
        var r = b.Deflate(w / 2);
        double rad = Math.Max(0, Radius - w / 2);
        context.DrawRectangle(null, Palette.Pen(Color, w, "3 3"), r, rad, rad);
    }
}

/// <summary>
/// 条件框底部的进度线（样稿 .reactor-cond-fill：高 3、靠左下，宽 = 进度（至少 4 %），g-3 → g-dot 渐变；报警暂停时灰色停住（line）；
/// 运行中缓慢呼吸（共用的 PulseClock，冻结时不动）；断线时去色（bg-side → line-2，样稿 grayscale）。
/// </summary>
public sealed class CondFillBar : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<CondFillBar, double>(nameof(Value));
    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<CondFillBar, bool>(nameof(IsRunning));
    public static readonly StyledProperty<bool> IsHeldProperty = AvaloniaProperty.Register<CondFillBar, bool>(nameof(IsHeld));
    public static readonly StyledProperty<bool> IsGrayProperty = AvaloniaProperty.Register<CondFillBar, bool>(nameof(IsGray));

    static CondFillBar()
    {
        AffectsRender<CondFillBar>(ValueProperty, IsRunningProperty, IsHeldProperty, IsGrayProperty);
        HeightProperty.OverrideDefaultValue<CondFillBar>(3);
        IsHitTestVisibleProperty.OverrideDefaultValue<CondFillBar>(false);
    }

    /// <summary>0..1；0 = 不画。</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public bool IsHeld { get => GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }
    public bool IsGray { get => GetValue(IsGrayProperty); set => SetValue(IsGrayProperty, value); }

    private static readonly IBrush RunFill = Gradient(Palette.G3Color, Palette.GDotColor);
    private static readonly IBrush GrayFill = Gradient(Palette.BgSideColor, Palette.Line2Color);

    private static IBrush Gradient(Color a, Color b) => new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, a), new ImmutableGradientStop(1, b) },
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    public override void Render(DrawingContext context)
    {
        var v = Math.Clamp(Value, 0, 1);
        if (v <= 0 || Bounds.Width <= 0) return;
        var r = new Rect(0, 0, Bounds.Width * v, Bounds.Height);
        context.DrawRectangle(IsHeld ? Palette.LineBrush : IsGray ? GrayFill : RunFill, null, r);
        if (_pulsing) context.DrawRectangle(PulseClock.Overlay, null, r);
    }

    private bool _pulsing, _attached;

    private void UpdatePulse()
    {
        bool on = IsRunning && !IsHeld && !IsGray && Value > 0 && IsVisible && _attached && !Frozen.GetIsFrozen(this);
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
        if (change.Property == IsRunningProperty || change.Property == IsHeldProperty || change.Property == IsGrayProperty || change.Property == ValueProperty
            || change.Property == IsVisibleProperty || change.Property == Frozen.IsFrozenProperty)
            UpdatePulse();
    }
}

/// <summary>
/// 一行可以带下标数字的文字（步骤卡的方式「N₂ · 到压后保持」、程序库的说明「1.2 MPa N₂」，样稿 PR.chem）：没有下标时是一个逐字省略 + 悬停的 PrText
/// （<see cref="TextFit.CharacterEllipsis"/>：中西文混排的说明「120 ℃ · 0.35 MPa · 反应 72 …」同样稿，11.1 自带的省略会丢掉整段「72 」，复核）；
/// 有下标时是 Controls/Vessel 的 ChemText（普通段与下标段按基线排，下标 max(13, 0.78 em)），框不够宽时同样省略（ChemText.Trim：放不下的那一段带省略号，悬停有全文）。
/// 字号、字重、颜色、行高按属性给（同 TextBlock）。
/// </summary>
public sealed class ChemLine : Panel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ChemLine, string?>(nameof(Text));
    public static readonly StyledProperty<double> FontSizeProperty = Avalonia.Controls.Documents.TextElement.FontSizeProperty.AddOwner<ChemLine>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<ChemLine>();
    public static readonly StyledProperty<FontWeight> FontWeightProperty = Avalonia.Controls.Documents.TextElement.FontWeightProperty.AddOwner<ChemLine>();
    public static readonly StyledProperty<double> LineHeightProperty = AvaloniaProperty.Register<ChemLine, double>(nameof(LineHeight), double.NaN);

    private readonly PrText _plain = new() { TextTrimming = TextFit.CharacterEllipsis };
    private readonly Vessel.ChemText _chem = new() { IsVisible = false, Trim = true };

    static ChemLine() => AffectsMeasure<ChemLine>(TextProperty, FontSizeProperty, LineHeightProperty, FontWeightProperty);

    public ChemLine()
    {
        ClipToBounds = true;
        Children.Add(_plain);
        Children.Add(_chem);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double LineHeight { get => GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }

    /// <summary>有下标数字（₀–₉）。</summary>
    public static bool HasSub(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s) if (ch is >= '₀' and <= '₉') return true;
        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var t = Text ?? "";
            bool sub = HasSub(t);
            _plain.IsVisible = !sub;
            _chem.IsVisible = sub;
            if (sub) _chem.Text = t; else _plain.Text = t;
            ToolTip.SetTip(this, string.IsNullOrEmpty(t) ? null : t);
        }
        else if (change.Property == FontSizeProperty) { _plain.FontSize = FontSize; _chem.FontSize = FontSize; }
        else if (change.Property == ForegroundProperty) { _plain.Foreground = Foreground; _chem.Foreground = Foreground; }
        else if (change.Property == FontWeightProperty) { _plain.FontWeight = FontWeight; foreach (var c in _chem.Children) if (c is TextBlock tb) tb.FontWeight = FontWeight; }
        else if (change.Property == LineHeightProperty) _plain.LineHeight = LineHeight;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size s;
        if (_chem.IsVisible)
        {
            _chem.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            s = _chem.DesiredSize;
        }
        else
        {
            _plain.Measure(availableSize);
            s = _plain.DesiredSize;
        }
        double h = double.IsNaN(LineHeight) ? s.Height : Math.Max(LineHeight, s.Height > LineHeight ? LineHeight : s.Height);
        if (!double.IsNaN(LineHeight)) h = LineHeight;
        return new Size(Math.Min(s.Width, availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_chem.IsVisible)
        {
            var d = _chem.DesiredSize;
            _chem.Arrange(new Rect(0, Math.Round((finalSize.Height - d.Height) / 2), Math.Min(d.Width, finalSize.Width), d.Height));
            _plain.Arrange(default);
        }
        else
        {
            _plain.Arrange(new Rect(finalSize));
            _chem.Arrange(default);
        }
        return finalSize;
    }
}
