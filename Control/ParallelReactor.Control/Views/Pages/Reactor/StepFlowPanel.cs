using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 步骤卡片画布（样稿 reactor.js flowLayout / flowHTML + base.css .pr-stepflow / .pr-step-* + reactor.css .reactor-flow / .reactor-vlink；PORT-PLAN §5 M5「StepFlowPanel」）：
/// 自定义 Measure / Arrange，位置全部来自 Core 的 <see cref="StepFlowLayout"/>（与样稿在 WebKit 里量到的版面逐个核对过）：
/// <list type="bullet">
/// <item>每行张数与卡宽（<see cref="StepFlowLayout.Fit"/>）按画布滚动区的外宽（<see cref="OuterWidth"/>，含 8 px 滚动条）与放大档（<see cref="Zoom"/>）算：
///   卡 214–280（每行一张时 ≤ 300），箭头 56，条件框右移 22；放大时按放大后的尺寸重排（每行的卡变少），整个画布按倍数缩放（RenderTransform），从不横向滚动。</item>
/// <item>子元素：每一步一张卡片（<see cref="StepCardView"/>，按卡宽量高度）+ 一个条件框（<see cref="StepCondView"/>，同宽、高 44，按卡宽决定标签与值的字号）；
///   最后是 <see cref="Tail"/>（安全联锁框，上边距 30）。</item>
/// <item>自绘：卡片到条件框的竖线（条件框左边 + 15，18 高）、行内的横向箭头（56 × 44 靠行底：线 22 → 52，箭头尖 7 × 8）、
///   每行多张时的行间分隔线（line-2，上 28 下 26）、每行一张时的向下连线（左 36，24 高 + 7 × 8 的箭头）。线一律 line-strong。</item>
/// <item>组件陈列（<see cref="GalleryPer"/> &gt; 0，样稿 ui.stepFlow）：每行张数由陈列区宽度给、卡宽 248、内边距 24 28 32、靠左、分隔线横跨内容宽度（左右各留 28）。</item>
/// </list>
/// 版面只看父元素给的宽度（R4）；排好之后发 <see cref="Arranged"/>（程序面板据此自动滚动 / 定位当前步）。
/// </summary>
public sealed class StepFlowPanel : Panel
{
    public static readonly StyledProperty<IReadOnlyList<StepCardVm>?> ItemsProperty = AvaloniaProperty.Register<StepFlowPanel, IReadOnlyList<StepCardVm>?>(nameof(Items));
    public static readonly StyledProperty<double> OuterWidthProperty = AvaloniaProperty.Register<StepFlowPanel, double>(nameof(OuterWidth));
    public static readonly StyledProperty<int> ZoomProperty = AvaloniaProperty.Register<StepFlowPanel, int>(nameof(Zoom));
    public static readonly StyledProperty<bool> PreviewProperty = AvaloniaProperty.Register<StepFlowPanel, bool>(nameof(Preview));
    public static readonly StyledProperty<int> GalleryPerProperty = AvaloniaProperty.Register<StepFlowPanel, int>(nameof(GalleryPer));
    public static readonly StyledProperty<Avalonia.Controls.Control?> TailProperty = AvaloniaProperty.Register<StepFlowPanel, Avalonia.Controls.Control?>(nameof(Tail));

    /// <summary>组件陈列的内边距（.pr-stepflow：24 28 32）比反应釜画布（20 16 28）多出来的部分。</summary>
    private const double GalleryDx = 28 - StepFlowLayout.PadX, GalleryDy = 24 - StepFlowLayout.PadTop, GalleryDb = 32 - StepFlowLayout.PadBottom;

    private readonly List<(StepCardView Card, StepCondView Cond)> _pairs = new();
    /// <summary>每一对卡片 / 条件框是哪个视图模型的（与 <see cref="_pairs"/> 同序；同步时按它认出留下来的卡片）。</summary>
    private readonly List<StepCardVm> _vms = new();
    private INotifyCollectionChanged? _watched;
    private double _scale = 1;

    static StepFlowPanel()
    {
        AffectsMeasure<StepFlowPanel>(OuterWidthProperty, ZoomProperty, PreviewProperty, GalleryPerProperty);
    }

    private readonly Decor _decor;

    public StepFlowPanel()
    {
        RenderTransformOrigin = RelativePoint.TopLeft;
        _decor = new Decor(this);
        Children.Add(_decor);
    }

    /// <summary>步骤（视图模型的集合；增删时卡片跟着增删）。</summary>
    public IReadOnlyList<StepCardVm>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    /// <summary>画布滚动区的外宽（含 8 px 滚动条，样稿 cv.offsetWidth；滚动条出现 / 消失时不重排）。0 = 按给的可用宽度。</summary>
    public double OuterWidth { get => GetValue(OuterWidthProperty); set => SetValue(OuterWidthProperty, value); }
    /// <summary>放大档 0 / 1 / 2（放不下时按 0 排，<see cref="StepFlowLayout.ClampZoom"/>）。</summary>
    public int Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <summary>程序库的预览（上内边距 12、不放大）。</summary>
    public bool Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    /// <summary>组件陈列：每行几张（样稿 placeholder.js flowPer）；0 = 反应釜画布。</summary>
    public int GalleryPer { get => GetValue(GalleryPerProperty); set => SetValue(GalleryPerProperty, value); }
    /// <summary>画布最后的安全联锁框（样稿 safetyHTML）；没有为 null（程序库预览、组件陈列）。</summary>
    public Avalonia.Controls.Control? Tail { get => GetValue(TailProperty); set => SetValue(TailProperty, value); }

    /// <summary>这一遍排版的每行张数、卡宽、放大档。</summary>
    public StepFlowFit Fit { get; private set; }
    /// <summary>这一遍排版的版面（设计 px，相对本控件左上角；组件陈列不含多出来的内边距）；没排过为 null。</summary>
    public StepFlowGeometry? Geometry { get; private set; }
    /// <summary>放大倍数（整个画布按它缩放）。</summary>
    public double Scale => _scale;
    /// <summary>卡片与条件框（核对用）。</summary>
    public IReadOnlyList<(StepCardView Card, StepCondView Cond)> Pairs => _pairs;

    /// <summary>排好了（程序面板据此自动滚动；在排版的最后触发，滚动由接收方排到之后做）。</summary>
    public event EventHandler? Arranged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (_watched != null) _watched.CollectionChanged -= OnItems;
            _watched = Items as INotifyCollectionChanged;
            if (_watched != null) _watched.CollectionChanged += OnItems;
            SyncChildren();
        }
        else if (change.Property == TailProperty)
        {
            if (change.OldValue is Avalonia.Controls.Control old) Children.Remove(old);
            if (Tail is { } t && !Children.Contains(t)) Children.Add(t);
            InvalidateMeasure();
        }
    }

    private void OnItems(object? sender, NotifyCollectionChangedEventArgs e) => SyncChildren();

    /// <summary>
    /// 卡片跟着步骤增删，按视图模型认卡片：留下来的视图模型保留原来的卡片和条件框（每秒的刷新、往集合里逐张加都不重建 ——
    /// 以前每次变化整批重建，逐张加 n 步要建 n(n+1)/2 对，复核 c2-ui-1），只给新的视图模型建、只拆掉不在了的。
    /// 拆掉的清掉 DataContext：绑定不再跟着旧的视图模型走（条件框摘下时已退订，见 <see cref="StepCondView"/>）。
    /// </summary>
    private void SyncChildren()
    {
        var items = Items;
        int n = items?.Count ?? 0;
        bool same = n == _vms.Count;
        for (int i = 0; same && i < n; i++) same = ReferenceEquals(_vms[i], items![i]);
        if (same) return;
        // 旧的按视图模型找（同一个视图模型在集合里出现两次时，第二次另建一对）
        var old = new Dictionary<StepCardVm, int>(_vms.Count, ReferenceEqualityComparer.Instance);
        for (int j = 0; j < _vms.Count; j++) old.TryAdd(_vms[j], j);
        var next = new List<(StepCardView Card, StepCondView Cond)>(n);
        var kept = new bool[_pairs.Count];
        for (int i = 0; i < n; i++)
        {
            var vm = items![i];
            if (old.Remove(vm, out int j))
            {
                next.Add(_pairs[j]);
                kept[j] = true;
            }
            else next.Add((new StepCardView { DataContext = vm }, new StepCondView { DataContext = vm }));
        }
        for (int j = 0; j < _pairs.Count; j++)
        {
            if (kept[j]) continue;
            var (card, cond) = _pairs[j];
            Children.Remove(card);
            Children.Remove(cond);
            card.DataContext = null;
            cond.DataContext = null;
        }
        // 按新的顺序摆：0 号是画线的 Decor，第 i 步的卡片 / 条件框在 1 + 2i / 2 + 2i（安全联锁框跟在最后）；留下来的只挪位置，不摘下
        for (int i = 0; i < next.Count; i++)
        {
            Place(next[i].Card, 1 + 2 * i);
            Place(next[i].Cond, 2 + 2 * i);
        }
        _pairs.Clear();
        _pairs.AddRange(next);
        _vms.Clear();
        for (int i = 0; i < n; i++) _vms.Add(items![i]);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Place(Avalonia.Controls.Control c, int at)
    {
        int cur = Children.IndexOf(c);
        if (cur == at) return;
        if (cur < 0) Children.Insert(at, c);
        else Children.Move(cur, at);
    }

    private bool IsGallery => GalleryPer > 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = _pairs.Count;
        StepFlowFit fit;
        if (IsGallery) fit = new StepFlowFit(Math.Max(1, GalleryPer), StepFlowLayout.StepDefault, 0, 1, 0);
        else
        {
            double outer = OuterWidth > 0 ? OuterWidth : double.IsInfinity(availableSize.Width) ? 564 : availableSize.Width;
            fit = StepFlowLayout.Fit(outer, Math.Max(1, n), Preview ? 0 : Zoom);
        }
        Fit = fit;
        double stepW = fit.StepW;
        var heights = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (card, cond) = _pairs[i];
            cond.ApplyWidth(stepW);
            card.Measure(new Size(stepW, double.PositiveInfinity));
            cond.Measure(new Size(stepW, StepFlowLayout.CondH));
            heights[i] = card.DesiredSize.Height;
        }
        double? tailH = null;
        if (Tail is { IsVisible: true } t)
        {
            int per = Math.Max(1, fit.Per);
            double rowW = per * stepW + (per - 1) * StepFlowLayout.ArrowW + StepFlowLayout.CondOff;
            if (t is IStepWidthAware sw) sw.ApplyWidth(stepW);
            t.Measure(new Size(rowW, double.PositiveInfinity));
            tailH = t.DesiredSize.Height;
        }
        var g = StepFlowLayout.Arrange(fit, heights, tailH, 0, Preview);
        Geometry = g;
        _decor.Measure(Size.Infinity);
        if (IsGallery)
        {
            double w = double.IsInfinity(availableSize.Width) ? g.Width + 2 * GalleryDx : availableSize.Width;
            _scale = 1;
            return new Size(w, g.Height + GalleryDy + GalleryDb);
        }
        _scale = fit.Scale;
        RenderTransform = _scale == 1 ? null : new ScaleTransform(_scale, _scale);
        return new Size(g.Width * _scale, g.Height * _scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var g = Geometry;
        if (g == null) return finalSize;
        double dx = IsGallery ? GalleryDx : 0, dy = IsGallery ? GalleryDy : 0;
        for (int i = 0; i < _pairs.Count && i < g.Items.Count; i++)
        {
            var it = g.Items[i];
            var (card, cond) = _pairs[i];
            card.Arrange(new Rect(it.Card.X + dx, it.Card.Y + dy, it.Card.W, it.Card.H));
            cond.Arrange(new Rect(it.Cond.X + dx, it.Cond.Y + dy, it.Cond.W, it.Cond.H));
        }
        if (Tail is { IsVisible: true } t && g.Tail is { } tr) t.Arrange(new Rect(tr.X + dx, tr.Y + dy, tr.W, tr.H));
        BuildDecorations(g, IsGallery ? finalSize.Width : g.Width);
        _decor.Arrange(new Rect(0, 0, IsGallery ? finalSize.Width : g.Width, IsGallery ? finalSize.Height : g.Height));
        _decor.InvalidateVisual();
        Arranged?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }

    // 自绘的线与箭头尖：排版时算好（R10：渲染时不分配）
    private readonly List<(Point A, Point B)> _lines = new();
    private readonly List<Rect> _dividers = new();
    private StreamGeometry? _heads;

    /// <summary>按这一遍的版面把竖线、箭头、分隔线、向下的连线算好（ArrangeOverride 里调）。</summary>
    private void BuildDecorations(StepFlowGeometry g, double width)
    {
        _lines.Clear();
        _dividers.Clear();
        double dx = IsGallery ? GalleryDx : 0, dy = IsGallery ? GalleryDy : 0;
        var heads = new StreamGeometry();
        using (var ctx = heads.Open())
        {
            void Tri(Point a, Point b, Point c)
            {
                ctx.BeginFigure(a, true);
                ctx.LineTo(b);
                ctx.LineTo(c);
                ctx.EndFigure(true);
            }
            // 卡片底到条件框顶的竖线（.pr-step-cond::before：以条件框的内边距盒为准 —— 左 14 = 边框盒 + 15，上 −19 = 边框盒 −18，高 18）
            foreach (var it in g.Items)
            {
                double x = it.Cond.X + dx + 15.5;
                _lines.Add((new Point(x, it.Cond.Y + dy - 18), new Point(x, it.Cond.Y + dy)));
            }
            // 行内的横向箭头（.pr-step-arrow：线从 22 到右 4，高度一半处；箭头尖 7 × 8，离右边 2）
            foreach (var a in g.Arrows)
            {
                double y = a.Y + dy + a.H / 2;
                _lines.Add((new Point(a.X + dx + 22, y + 0.5), new Point(a.Right + dx - 4, y + 0.5)));
                Tri(new Point(a.Right + dx - 9, y - 4), new Point(a.Right + dx - 2, y), new Point(a.Right + dx - 9, y + 4));
            }
            // 每行一张：向下的连线（.reactor-vlink：线在左 36、上 0 到下 6；箭头尖在左 32、底边，7 高 8 宽）
            foreach (var v in g.VLinks)
            {
                double x = v.X + dx + StepFlowLayout.VLinkLineX + 0.5, y = v.Y + dy;
                _lines.Add((new Point(x, y), new Point(x, y + StepFlowLayout.VLinkH - 6)));
                double hx = v.X + dx + StepFlowLayout.VLinkHeadX;
                Tri(new Point(hx, y + StepFlowLayout.VLinkH - 7), new Point(hx + 8, y + StepFlowLayout.VLinkH - 7), new Point(hx + 4, y + StepFlowLayout.VLinkH));
            }
        }
        _heads = heads;
        // 行间分隔线（line-2；组件陈列横跨陈列区的内容宽度：.pr-stepflow 左右内边距各 28，线从 28 到 宽 − 28）
        foreach (var d in g.Dividers)
            _dividers.Add(new Rect(d.X + dx, d.Y + dy, IsGallery ? Math.Max(0, width - 2 * (StepFlowLayout.PadX + GalleryDx)) : d.W, d.H));
    }

    /// <summary>画线的子元素（Panel.Render 在 11.1 里是密封的）：排在卡片下面、铺满画布，只画排版时算好的线和箭头尖。</summary>
    private sealed class Decor : Avalonia.Controls.Control
    {
        private readonly StepFlowPanel _owner;

        public Decor(StepFlowPanel owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            var pen = Palette.Pen(Palette.LineStrongColor, 1);
            foreach (var (a, b) in _owner._lines) context.DrawLine(pen, a, b);
            foreach (var d in _owner._dividers) context.DrawRectangle(Palette.Line2Brush, null, d);
            if (_owner._heads != null) context.DrawGeometry(Palette.LineStrongBrush, null, _owner._heads);
        }
    }
}
