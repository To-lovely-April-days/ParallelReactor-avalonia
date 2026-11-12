using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// 线描图标（v2，PORT-PLAN M1）。两种用法：
///  · 新：<c>Icon="play"</c> → 资源 Icon.play（描边）+ Icon.play.Fill（填充子图形，如录制点，总是实心）；Styles/Icons.axaml 由 tools/gen-icons.py 生成。
///    按 <see cref="Size"/>（默认 18，= 24 网格的边长；logo 的 viewBox 24×28 按比例更高）缩放几何本身，不缩放画布，
///    所以描边恒为 <see cref="StrokeWidth"/> px（默认 1.6，round cap / join，样稿的 non-scaling-stroke）。
///    <see cref="Fill"/>=true 时把描边图形也填实（停止类按钮的深灰方块：样稿 fill: currentColor + stroke-width 1）。
///  · 旧：<c>Geometry="{StaticResource IcoX}"</c> → 与 M1 之前完全相同（按 Size/24 缩放画布，<see cref="Stroke"/> 是 viewBox 单位的描边宽，默认 1.45），
///    旧视图外观不变。M7 删除。
/// 颜色继承 Foreground（currentColor）。不设置 Cursor：指针由可点击的父控件决定。
/// </summary>
public sealed class IconView : Avalonia.Controls.Control
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Icon));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<IconView, double>(nameof(StrokeWidth), 1.6);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();
    public static readonly StyledProperty<bool> FillProperty = AvaloniaProperty.Register<IconView, bool>(nameof(Fill));
    /// <summary>旧用法：直接给几何（24×24 viewBox）。</summary>
    public static readonly StyledProperty<Geometry?> GeometryProperty = AvaloniaProperty.Register<IconView, Geometry?>(nameof(Geometry));
    /// <summary>旧用法：描边宽度（viewBox 单位，随 Size 缩放；1.45，gauge 1.7，logo 1.8）。</summary>
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Stroke), 1.45);

    static IconView()
    {
        AffectsRender<IconView>(IconProperty, GeometryProperty, ForegroundProperty, StrokeProperty, StrokeWidthProperty, FillProperty, SizeProperty);
        AffectsMeasure<IconView>(SizeProperty, IconProperty, GeometryProperty);
    }

    /// <summary>图标名（ui.js 的 ICONS 键，如 play、stop、rec、logo）。</summary>
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>24 网格对应的边长（px），默认 18（样稿 .pr-icon）。</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>描边宽（px，不随 Size 缩放），默认 1.6。</summary>
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>描边图形同时填实（停止方块）。旧用法里同义（mock 的图标全部 fill:none，仅状态点等自绘用）。</summary>
    public bool Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Geometry? Geometry { get => GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private bool IsNamed => !string.IsNullOrEmpty(Icon);

    // ---- 新用法：几何按名字取资源，缩放后的副本按（几何, 比例）缓存 ----
    private Geometry? _stroke, _fill;
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
        _stroke = _fill = null;
        if (string.IsNullOrEmpty(name)) return;
        if (this.TryFindResource("Icon." + name, out var g)) _stroke = g as Geometry;
        if (this.TryFindResource("Icon." + name + ".Fill", out var f)) _fill = f as Geometry;
    }

    private (double X, double Y, double W, double H) ViewBox
        => Icon is { } n && IconSet.ViewBoxes.TryGetValue(n, out var vb) ? vb : (0, 0, 24, 24);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IsNamed) return new(Size, Size);
        var vb = ViewBox;
        var s = Size / 24.0;
        return new(vb.W * s, vb.H * s);
    }

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
        if (_stroke == null && _fill == null) return;
        var vb = ViewBox;
        var s = Size / 24.0;
        var ox = (Bounds.Width - vb.W * s) / 2 - vb.X * s;
        var oy = (Bounds.Height - vb.H * s) / 2 - vb.Y * s;
        var pen = PenFor(brush, StrokeWidth);
        using (context.PushTransform(Matrix.CreateTranslation(ox, oy)))
        {
            if (_stroke != null) context.DrawGeometry(Fill ? brush : null, pen, Scaled(_stroke, s));
            if (_fill != null) context.DrawGeometry(brush, pen, Scaled(_fill, s));
        }
    }

    /// <summary>M1 之前的绘制，逐字保留（旧视图外观不变）。</summary>
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

    private IPen? _pen;
    private IBrush? _penBrush;
    private double _penWidth;

    private IPen PenFor(IBrush brush, double width)
    {
        if (_pen == null || !ReferenceEquals(_penBrush, brush) || _penWidth != width)
        {
            _pen = brush is IImmutableBrush ib
                ? new ImmutablePen(ib, width, null, PenLineCap.Round, PenLineJoin.Round)
                : new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _penBrush = brush; _penWidth = width;
        }
        return _pen;
    }
}
