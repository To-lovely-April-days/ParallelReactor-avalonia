using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 倒计时环（样稿 ui.ring / .pr-ring）：直径 <see cref="Diameter"/>（默认 120）、环宽 8；轨道 line-faint，进度 a（圆头，从 12 点钟顺时针）。
/// 中心：数值 30/600 t-1 + 单位 15/500 t-3，下面标签 14 t-3。用于控制权申请等待（M3）。
/// </summary>
public sealed class Ring : TemplatedControl
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Ring, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Ring, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<double> DiameterProperty = AvaloniaProperty.Register<Ring, double>(nameof(Diameter), 120);
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Ring, double>(nameof(Thickness), 8);
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Ring, string?>(nameof(Text));
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<Ring, string?>(nameof(Unit));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Ring, string?>(nameof(Label));
    public static readonly DirectProperty<Ring, string> ShownTextProperty = AvaloniaProperty.RegisterDirect<Ring, string>(nameof(ShownText), o => o.ShownText);

    static Ring()
    {
        AffectsRender<Ring>(ValueProperty, MaximumProperty, ThicknessProperty);
        AffectsMeasure<Ring>(DiameterProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Diameter { get => GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    /// <summary>中心数值（默认 = 四舍五入的 Value）。</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private string _shown = "0";
    public string ShownText { get => _shown; private set => SetAndRaise(ShownTextProperty, ref _shown, value); }

    public Ring() => UpdateText();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == ValueProperty) UpdateText();
        if (change.Property == UnitProperty) PseudoClasses.Set(":unit", !string.IsNullOrEmpty(Unit));
        if (change.Property == LabelProperty) PseudoClasses.Set(":label", !string.IsNullOrEmpty(Label));
    }

    private void UpdateText() => ShownText = Text ?? Math.Round(Value, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(new Size(Diameter, Diameter));
        return new Size(Diameter, Diameter);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var sw = Thickness;
        var r = (Diameter - sw) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, Palette.Pen(Palette.LineFaintColor, sw), c, r, r);
        var f = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (f <= 0) return;
        var pen = Palette.Pen(Palette.AColor, sw, cap: PenLineCap.Round);
        if (f >= 0.9999) { context.DrawEllipse(null, pen, c, r, r); return; }
        var a0 = -Math.PI / 2;
        var a1 = a0 + f * 2 * Math.PI;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geo);
    }
}
