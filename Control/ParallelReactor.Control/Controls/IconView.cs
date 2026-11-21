using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// 统一矢量图标控件（Phosphor Icons，MIT 许可，256×256 网格，Styles/Icons.axaml）：
///  · <c>Icon="play"</c> → 资源 <c>Icon.play</c>（纯填充路径）+ 可选 <c>Icon.play.Tone</c>（Duotone 20% 浅底纹层）；
///    按 <see cref="Size"/>（默认 18，= 256 网格对应的渲染边长 px）等比缩放，颜色继承 <see cref="Foreground"/>（currentColor）。
///  · 保留 <see cref="StrokeWidth"/> / <see cref="Fill"/> 属性及旧 <see cref="Geometry"/> 路径兼容历史 XAML 样式。
/// 不设置 Cursor：指针由可点击的父控件决定。
/// </summary>
public sealed class IconView : Avalonia.Controls.Control
{
    /// <summary>Phosphor 统一网格边长（256×256）。</summary>
    public const double Grid = 256.0;

    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Icon));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<IconView, double>(nameof(StrokeWidth), 1.6);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();
    public static readonly StyledProperty<bool> FillProperty = AvaloniaProperty.Register<IconView, bool>(nameof(Fill));
    /// <summary>旧用法：直接给几何（24×24 viewBox）。</summary>
    public static readonly StyledProperty<Geometry?> GeometryProperty = AvaloniaProperty.Register<IconView, Geometry?>(nameof(Geometry));
    /// <summary>旧用法：描边宽度（viewBox 单位，随 Size 缩放）。</summary>
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Stroke), 1.45);

    static IconView()
    {
        AffectsRender<IconView>(IconProperty, GeometryProperty, ForegroundProperty, StrokeProperty, StrokeWidthProperty, FillProperty, SizeProperty);
        AffectsMeasure<IconView>(SizeProperty, IconProperty, GeometryProperty);
    }

    /// <summary>统一图标名（见 <see cref="IconSet.Names"/>，如 play、stop、bell-lg、estop）。</summary>
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>渲染边长（px），默认 18。</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>保留属性以兼容历史 XAML 样式（Phosphor 图标为预烘焙填充路径，不再额外描边）。</summary>
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>保留属性以兼容历史 XAML 样式。</summary>
    public bool Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Geometry? Geometry { get => GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private bool IsNamed => !string.IsNullOrEmpty(Icon);

    private Geometry? _main, _tone;
    private string? _resolvedFor;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) _resolvedFor = null;
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _resolvedFor = null;
    }

    private void Resolve()
    {
        var name = Icon;
        if (_resolvedFor == name) return;
        _resolvedFor = name;
        _main = _tone = null;
        if (string.IsNullOrEmpty(name)) return;
        if (this.TryFindResource("Icon." + name, out var g)) _main = g as Geometry;
        if (this.TryFindResource("Icon." + name + ".Tone", out var t)) _tone = t as Geometry;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var brush = Foreground;
        if (brush == null) return;
        if (IsNamed) RenderNamed(context, brush);
        else RenderLegacy(context, brush);
    }

    private void RenderNamed(DrawingContext context, IBrush brush)
    {
        Resolve();
        if (_main == null && _tone == null) return;
        var s = Size / Grid;
        var ox = (Bounds.Width - Size) / 2;
        var oy = (Bounds.Height - Size) / 2;
        using (context.PushTransform(Matrix.CreateTranslation(ox, oy)))
        {
            if (_tone != null)
            {
                using (context.PushOpacity(0.2))
                    context.DrawGeometry(brush, null, Scaled(_tone, s));
            }
            if (_main != null)
                context.DrawGeometry(brush, null, Scaled(_main, s));
        }
    }

    /// <summary>供自绘控件（TileMarkView / KeyDot / ReactorVessel 等）在 DrawingContext 中直接绘制统一图标。</summary>
    public static void Draw(DrawingContext context, string name, double size, IBrush brush, double ox, double oy)
    {
        var (main, tone) = LookupApp(name);
        if (main == null && tone == null) return;
        var s = size / Grid;
        using (context.PushTransform(Matrix.CreateTranslation(ox, oy)))
        {
            if (tone != null)
            {
                using (context.PushOpacity(0.2))
                    context.DrawGeometry(brush, null, Scaled(tone, s));
            }
            if (main != null)
                context.DrawGeometry(brush, null, Scaled(main, s));
        }
    }

    private static readonly Dictionary<string, (Geometry? Main, Geometry? Tone)> AppCache = new(System.StringComparer.Ordinal);

    private static (Geometry? Main, Geometry? Tone) LookupApp(string name)
    {
        if (AppCache.TryGetValue(name, out var cached)) return cached;
        if (Application.Current is not { } app) return default;
        var main = app.TryFindResource("Icon." + name, out var g) ? g as Geometry : null;
        var tone = app.TryFindResource("Icon." + name + ".Tone", out var t) ? t as Geometry : null;
        var res = (main, tone);
        AppCache[name] = res;
        return res;
    }

    private void RenderLegacy(DrawingContext context, IBrush brush)
    {
        var g = Geometry;
        if (g == null) return;
        var s = Size / 24.0;
        var ox = (Bounds.Width - Size) / 2;
        var oy = (Bounds.Height - Size) / 2;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
        {
            var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Fill ? brush : null, pen, g);
        }
    }

    // 缩放后的几何副本：资源里的几何是共享的，不能改它的 Transform
    private static readonly ConditionalWeakTable<Geometry, Dictionary<double, Geometry>> ScaledCache = new();

    private static Geometry Scaled(Geometry g, double s)
    {
        var byScale = ScaledCache.GetOrCreateValue(g);
        if (!byScale.TryGetValue(s, out var c))
        {
            c = g.Clone();
            c.Transform = new MatrixTransform(Matrix.CreateScale(s, s));
            byScale[s] = c;
        }
        return c;
    }
}
