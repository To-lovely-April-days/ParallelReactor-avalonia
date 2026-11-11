using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Chart;

/// <summary>
/// 折线图（样稿 js/ui.js ui.lineChart 的移植；PORT-PLAN §5 M5「TimeSeriesChart」）。输入 = Core 的 <see cref="ChartOptions"/>（系列、纵轴、参考线、事件标记、横轴范围），
/// 版面 = Core 的 <see cref="ChartLayout"/>（刻度 niceTicks / fitTicks、分带 ChartBands、上限文字避开曲线 —— 都与样稿逐值对照过），画法 = <see cref="ChartScene"/>（样稿 SVG 的元素与顺序）。
/// <list type="bullet">
/// <item>双轴：温度在左、压力在右（分带：温度在上半区、压力在下半区）；网格、刻度字 13 px（等宽数字）、轴标题 14/500。</item>
/// <item>设定值 / 目标是浅色虚线，画在实测曲线<b>下面</b>；事件标记（--ch-mark 紫色竖虚线）与文字；上限线（--ch-limit 红虚线）只在接近 / 超过上限时由 Core 给出，
///   文字（--r-ink）先挑不被曲线穿过的位置，再带 4 px --ch-bg 描边画在曲线之上（样稿第 3 轮：曲线不会从「上限 3.000 MPa」字上划过）。</item>
/// <item>空值 / 断线处断开（ChannelHistory 的断开标记；屏幕坐标不是有限数的点也断开、末端圆点不画）；没有数据时绘图区正中「暂无数据」/「离线 · 暂无数据」。</item>
/// <item>悬停：十字线（--t-4 虚线）+ 每条线的圆点 + 深色提示（时间、各系列读数），最近的点按时刻二分查找（Core 的 NearestIndex）；提示与十字线画在单独的一层
///   （<see cref="ChartHoverLayer"/>），指针移动只重画那一层，同一个采样点只挪提示的位置，不重新排字。数据更新时在原来的指针位置重新取读数（同样稿 draw() 末尾的 onMove）。</item>
/// </list>
/// R10：版面与绘制列表只在 <see cref="Options"/> 或尺寸变化时重建一次（每秒至多一次）；画笔、画刷来自缓存，文字排版（TextLayout）按（文字, 样式, 颜色）缓存、跨刷新复用，
/// 绘制本身不分配。自绘的文字经 <see cref="IAuditText"/> 报给截图审计（字号 ≥ 13、色板）。冻结（截图）时没有任何动画可停：画面只随 Options 变。
/// 容器给多大就画多大（同样稿：容器要有明确的高度）；小于 40 × 40 不画。
/// </summary>
public sealed class TimeSeriesChart : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<ChartOptions?> OptionsProperty =
        AvaloniaProperty.Register<TimeSeriesChart, ChartOptions?>(nameof(Options));

    /// <summary>悬停读数（默认开；组件陈列里也开）。</summary>
    public static readonly StyledProperty<bool> IsHoverEnabledProperty =
        AvaloniaProperty.Register<TimeSeriesChart, bool>(nameof(IsHoverEnabled), true);

    private static readonly FontFeatureCollection Tnum = new() { FontFeature.Parse("+tnum") };

    private readonly ChartScene _scene = new();
    private readonly ChartHoverLayer _hover;
    private readonly Func<string, double> _measureLabel;
    private ChartGeometry? _g;
    private ChartOptions? _laidOpts;
    private Size _laidFor;
    private bool _dirty = true;
    private int _version;
    private Point? _pointer;

    // 绘制列表（与 _scene 的下标一一对应），重建时复用
    private readonly List<IPen> _linePens = new();
    private readonly List<DrawnText> _texts = new();
    private readonly List<StreamGeometry?> _paths = new();
    private readonly List<IPen> _pathPens = new();

    // 文字排版缓存：（文字, 样式, 颜色）→ TextLayout；换字体时清空，每次重建后去掉这次没用到的
    private readonly Dictionary<(string Text, ChartTextKind Kind, string Color), (TextLayout Layout, int Gen)> _layouts = new();
    private readonly Dictionary<string, double> _labelWidths = new(StringComparer.Ordinal);
    private int _gen;
    private FontFamily? _family;

    /// <summary>一段画好的文字：排版、左上角、描边（参考线 / 标记文字）、审计用的报告。</summary>
    private readonly record struct DrawnText(TextLayout Layout, Point Origin, Geometry? Halo, AuditTextRun Audit);

    static TimeSeriesChart()
    {
        AffectsRender<TimeSeriesChart>(OptionsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TimeSeriesChart>(true);
        FocusableProperty.OverrideDefaultValue<TimeSeriesChart>(false);
    }

    public TimeSeriesChart()
    {
        _measureLabel = MeasureLabel;
        _hover = new ChartHoverLayer(this);
        VisualChildren.Add(_hover);
    }

    /// <summary>曲线输入（每次数据变化换一个新实例；同一个实例不会重排）。null = 什么都不画。</summary>
    public ChartOptions? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

    public bool IsHoverEnabled { get => GetValue(IsHoverEnabledProperty); set => SetValue(IsHoverEnabledProperty, value); }

    /// <summary>当前的版面（核对 / 测试用）：还没排过或没有输入为 null。</summary>
    public ChartGeometry? Geometry { get { EnsureScene(); return _g; } }

    /// <summary>当前的绘制列表（核对 / 测试用）。</summary>
    public ChartScene Scene { get { EnsureScene(); return _scene; } }

    /// <summary>当前的悬停读数（没有悬停为 null）。</summary>
    public ChartHover? Hover => _hover.Current;

    /// <summary>重建次数（每次版面或数据变化加一；核对「没有每帧重建」用）。</summary>
    public int Version => _version;

    /// <summary>按控件坐标悬停（指针事件与核对共用）；点在绘图区外或没有数据时收起。</summary>
    public void HoverAt(Point p)
    {
        _pointer = p;
        EnsureScene();
        _hover.Update(_g, p, _version);
    }

    /// <summary>收起悬停（指针离开）。</summary>
    public void ClearHover()
    {
        _pointer = null;
        _hover.Hide();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OptionsProperty)
        {
            _dirty = true;
            // 尺寸已知时立刻重排：悬停（指针还在图上）马上按新数据取读数，与画出来的一致
            if (Bounds.Width > 0 && Bounds.Height > 0) EnsureScene();
        }
        else if (change.Property == TextElement.FontFamilyProperty)
        {
            ClearLayouts();
            _dirty = true;
            InvalidateVisual();
        }
        else if (change.Property == IsHoverEnabledProperty && !IsHoverEnabled) ClearHover();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _hover.Measure(availableSize);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _hover.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsHoverEnabled) return;
        HoverAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearHover();
    }

    // ———————————————————— 版面 → 绘制列表 ————————————————————

    /// <summary>输入或尺寸变了才重建（样稿 draw() 的一次）：Core 算版面 → 绘制列表 → 画笔、排版、曲线几何。</summary>
    private void EnsureScene()
    {
        var size = Bounds.Size;
        var opts = Options;
        var family = TextElement.GetFontFamily(this);
        if (!ReferenceEquals(family, _family)) { ClearLayouts(); _family = family; _dirty = true; }
        if (!_dirty && size == _laidFor && ReferenceEquals(opts, _laidOpts)) return;
        _dirty = false;
        _laidFor = size;
        _laidOpts = opts;
        _version++;
        if (opts is null)
        {
            _g = null;
            _scene.Clear();
        }
        else
        {
            _g = ChartLayout.Compute(opts, size.Width, size.Height, _measureLabel);
            _scene.Build(_g, opts);
        }
        Prepare();
        if (_pointer is { } p && IsHoverEnabled) _hover.Update(_g, p, _version);
        else _hover.Hide();
    }

    private void Prepare()
    {
        var s = _scene;
        _linePens.Clear();
        foreach (var l in s.Lines) _linePens.Add(LinePen(l));
        _pathPens.Clear();
        _paths.Clear();
        foreach (var p in s.Paths)
        {
            _pathPens.Add(ChartPaint.Pen(p.Color, p.Width, p.Dash, PenLineCap.Round));
            _paths.Add(PathGeometry(s, p));
        }
        _gen++;
        _texts.Clear();
        foreach (var t in s.Texts) _texts.Add(Place(t));
        TrimLayouts();
    }

    private static IPen LinePen(ChartSceneLine l) => l.Kind switch
    {
        ChartLineKind.Axis => ChartPaint.Pen(ChartStyle.AxisLine, ChartStyle.AxisWidth),
        ChartLineKind.Ref => ChartPaint.Pen(l.Color ?? ChartTokens.TempSp, ChartStyle.RefWidth, l.Dashed ? ChartStyle.RefDash : null),
        ChartLineKind.Marker => ChartPaint.Pen(l.Color ?? ChartTokens.Mark, ChartStyle.MarkerWidth, ChartStyle.MarkerDash),
        _ => ChartPaint.Pen(ChartStyle.Grid, ChartStyle.GridWidth),
    };

    /// <summary>
    /// 一条曲线的几何：NaN 处断开（样稿 path 的下一个「M」）；一个有效点都没有的段不画。纵坐标不是有限数（坐标轴算不出来，例如样稿分带在零下读数时的颠倒范围）
    /// 也当断开：不把 NaN 交给渲染。
    /// </summary>
    private static StreamGeometry? PathGeometry(ChartScene s, ChartScenePath p)
    {
        var geo = new StreamGeometry();
        bool any = false;
        using (var ctx = geo.Open())
        {
            bool open = false;
            for (int i = p.Start; i < p.Start + p.Count; i++)
            {
                double x = s.Xs[i], y = s.Ys[i];
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    if (open) ctx.EndFigure(false);
                    open = false;
                    continue;
                }
                if (!open)
                {
                    ctx.BeginFigure(new Point(x, y), false);
                    open = true;
                    any = true;
                }
                else ctx.LineTo(new Point(x, y));
            }
            if (open) ctx.EndFigure(false);
        }
        return any ? geo : null;
    }

    /// <summary>文字的排版与位置：x / y 是 SVG 的锚点与基线（起点 / 中点 / 终点）；参考线 / 标记文字另带描边的几何（4 px --ch-bg，样稿 paint-order: stroke）。</summary>
    private DrawnText Place(ChartSceneText t)
    {
        var layout = Layout(t.Text, t.Kind, t.Color);
        double w = layout.WidthIncludingTrailingWhitespace;
        double x = t.Anchor switch { ChartAnchor.End => t.X - w, ChartAnchor.Middle => t.X - w / 2, _ => t.X };
        double baseline = layout.TextLines.Count > 0 ? layout.TextLines[0].Baseline : layout.Height * 0.8;
        var origin = new Point(x, t.Y - baseline);
        Geometry? halo = null;
        if (t.Kind is ChartTextKind.RefLabel or ChartTextKind.MarkerLabel)
        {
            var ft = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face(t.Kind), SizeOf(t.Kind), null);
            halo = ft.BuildGeometry(new Point(x, t.Y - ft.Baseline));
        }
        var audit = new AuditTextRun(t.Text, SizeOf(t.Kind), new Rect(origin, new Size(w, layout.Height)), ChartPaint.Of(t.Color));
        return new DrawnText(layout, origin, halo, audit);
    }

    /// <summary>字号（样稿 base.css）：刻度 13、轴标题 14、参考线 / 标记文字 14、空数据 15。</summary>
    public static double SizeOf(ChartTextKind kind) => kind switch
    {
        ChartTextKind.Tick => ChartStyle.TickFont,
        ChartTextKind.Empty => ChartStyle.EmptyFont,
        ChartTextKind.AxisTitle => ChartStyle.AxisTitleFont,
        _ => ChartStyle.LabelFont,
    };

    private static FontWeight WeightOf(ChartTextKind kind) => kind switch
    {
        ChartTextKind.AxisTitle => FontWeight.Medium,
        ChartTextKind.RefLabel or ChartTextKind.MarkerLabel => FontWeight.SemiBold,
        _ => FontWeight.Normal,
    };

    private Typeface Face(ChartTextKind kind) => new(_family ?? FontFamily.Default, FontStyle.Normal, WeightOf(kind));

    private TextLayout Layout(string text, ChartTextKind kind, string color)
    {
        var key = (text, kind, color);
        if (_layouts.TryGetValue(key, out var hit))
        {
            if (hit.Gen != _gen) _layouts[key] = (hit.Layout, _gen);
            return hit.Layout;
        }
        // 刻度：等宽数字（样稿 .pr-chart-tick font-variant-numeric: tabular-nums）
        var tl = new TextLayout(text, Face(kind), kind == ChartTextKind.Tick ? Tnum : null, SizeOf(kind), ChartPaint.Brush(color));
        _layouts[key] = (tl, _gen);
        return tl;
    }

    /// <summary>这次重建没用到的排版去掉（缓存最多留一屏的量：数字刻度、时间、标签每秒复用）。</summary>
    private void TrimLayouts()
    {
        if (_layouts.Count <= 96) return;
        List<(string, ChartTextKind, string)>? drop = null;
        foreach (var kv in _layouts)
            if (kv.Value.Gen != _gen) (drop ??= new()).Add(kv.Key);
        if (drop == null) return;
        foreach (var k in drop)
        {
            _layouts[k].Layout.Dispose();
            _layouts.Remove(k);
        }
    }

    private void ClearLayouts()
    {
        foreach (var v in _layouts.Values) v.Layout.Dispose();
        _layouts.Clear();
        _labelWidths.Clear();
        _texts.Clear();
        _dirty = true;
    }

    /// <summary>参考线 / 标记文字的宽（样稿 ui.textWidth(text, 14, 600)：数字一律按「0」量 —— 页面的数字是等宽的）。</summary>
    private double MeasureLabel(string text)
    {
        if (_labelWidths.TryGetValue(text, out var w)) return w;
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (chars[i] is >= '0' and <= '9') chars[i] = '0';
        using var tl = new TextLayout(new string(chars), Face(ChartTextKind.RefLabel), ChartStyle.LabelFont, null);
        w = tl.WidthIncludingTrailingWhitespace;
        if (_labelWidths.Count > 256) _labelWidths.Clear();
        _labelWidths[text] = w;
        return w;
    }

    // ———————————————————— 绘制（不分配） ————————————————————

    public override void Render(DrawingContext ctx)
    {
        EnsureScene();
        // 整个控件都接指针（悬停）：透明底
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var s = _scene;
        if (!s.Drawn) return;
        var halo = HaloPen;
        for (int k = 0; k < s.Ops.Count; k++)
        {
            var op = s.Ops[k];
            switch (op.Kind)
            {
                case ChartOpKind.Plot:
                    ctx.DrawRectangle(Palette.ChBgBrush, null, new Rect(s.PlotX, s.PlotY, s.PlotW, s.PlotH));
                    break;
                case ChartOpKind.Line:
                {
                    var l = s.Lines[op.Index];
                    ctx.DrawLine(_linePens[op.Index], new Point(l.X1, l.Y1), new Point(l.X2, l.Y2));
                    break;
                }
                case ChartOpKind.Text:
                {
                    var t = _texts[op.Index];
                    if (t.Halo != null) ctx.DrawGeometry(null, halo, t.Halo);
                    t.Layout.Draw(ctx, t.Origin);
                    break;
                }
                case ChartOpKind.Series:
                    if (_paths[op.Index] is { } geo) ctx.DrawGeometry(null, _pathPens[op.Index], geo);
                    break;
                case ChartOpKind.EndDot:
                {
                    var d = s.Dots[op.Index];
                    // 坐标不是有限数（同曲线的断开）：不画
                    if (double.IsFinite(d.X) && double.IsFinite(d.Y))
                        ctx.DrawEllipse(ChartPaint.Brush(d.Color), null, new Point(d.X, d.Y), ChartStyle.EndDotR, ChartStyle.EndDotR);
                    break;
                }
            }
        }
    }

    /// <summary>参考线 / 标记文字的描边：--ch-bg 4 px、圆角连接（样稿 stroke-width: 4px; stroke-linejoin: round）。</summary>
    private static IPen HaloPen => ChartPaint.Pen(ChartStyle.Plot, ChartStyle.LabelHalo, null, PenLineCap.Round);

    /// <summary>自绘文字实际用到的字体（截图日志里核对字体回退与字重）：（文字, 「家族 字重」…）。</summary>
    internal IEnumerable<(string Text, string Faces)> DrawnFaces()
    {
        EnsureScene();
        foreach (var t in _texts)
        {
            var faces = new List<string>();
            foreach (var line in t.Layout.TextLines)
                foreach (var run in line.TextRuns)
                    if (run is ShapedTextRun sr) faces.Add(sr.ShapedBuffer.GlyphTypeface.FamilyName + " " + sr.ShapedBuffer.GlyphTypeface.Weight);
            yield return (t.Audit.Text, string.Join(" + ", faces));
        }
    }

    /// <summary>截图审计：此刻画着的文字（刻度、轴标题、参考线 / 标记文字、空数据）。悬停提示的文字由提示层自己报。</summary>
    public IEnumerable<AuditTextRun> AuditTexts
    {
        get
        {
            EnsureScene();
            foreach (var t in _texts) yield return t.Audit;
        }
    }
}
