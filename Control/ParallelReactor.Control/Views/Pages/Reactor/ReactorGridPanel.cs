using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 反应釜页的面板网格（样稿 css/reactor.css .reactor-grid：面板之间 1 px line-strong = 本面板的底色）。版面全部来自 Core 的 <see cref="ReactorPageLayout"/>
/// （样稿 WIDE_MIN / PROG2_MIN 与 CSS 网格规则，按网格自己的宽高）：
/// <list type="bullet">
/// <item>子元素按 Tag 认：「prog」程序、「vessel」釜体（页签版式 = 「釜体 / 属性查看器」页签面板，宽版式 = 只放釜体）、「props」属性查看器（只有宽版式有）、
///   「curves」曲线、「un」未安装（<see cref="Installed"/> = false 时只有它，铺满）。</item>
/// <item>宽版式（网格 ≥ 1800，支持的屏幕上不出现）：程序 ｜ 釜体 ｜ 属性查看器 / 曲线；页签版式：程序 ｜「釜体 / 属性查看器」+ 曲线（网格 ≥ 1100 时程序栏每行 2 张卡）。</item>
/// <item>关掉的面板（PrPanel :closed）由其余面板补位（关掉「程序」时右边一栏铺满整个宽度），规则同样稿的 :has(.is-closed)；视图 › 重置布局恢复。</item>
/// <item>放大（PrPanel 的 □）在网格里原地做（同样稿 .reactor-grid { position: relative }：放大的面板只盖住面板网格，上方的报警条照样显示）：
///   放大的那一块铺满网格，其余的藏起来（Animation 优先级，还原时撤掉）。宿主 PageHost 只记着它（Esc、重置布局照常还原，PageHost.InPlaceMax）。</item>
/// <item>边按设备像素对齐（<see cref="Snap"/>，100 % 时就是整 DIP：小数的面板（CSS 网格的 661.05）不会因为 Avalonia 把尺寸向上取整而多出 1 px；
///   分隔线在 100 % / 125 % / 150 % / 175 % 下都正好 1 个设备像素）；曲线 / 程序面板的容器查询按小数宽度判断。</item>
/// </list>
/// 只看父元素给的尺寸（R4）。
/// </summary>
public sealed class ReactorGridPanel : Panel
{
    public static readonly StyledProperty<bool> InstalledProperty = AvaloniaProperty.Register<ReactorGridPanel, bool>(nameof(Installed), true);

    private readonly Dictionary<Avalonia.Controls.Control, IDisposable> _hidden = new();
    private readonly HashSet<PrPanel> _watched = new();
    private Avalonia.Controls.Control? _max;
    private bool? _wide;

    static ReactorGridPanel() => AffectsMeasure<ReactorGridPanel>(InstalledProperty);

    /// <summary>这个位置装了反应釜（否则只有「un」一块）。</summary>
    public bool Installed { get => GetValue(InstalledProperty); set => SetValue(InstalledProperty, value); }

    /// <summary>上一遍排版的网格（核对用）。</summary>
    public ReactorPageGrid? Grid { get; private set; }

    private Avalonia.Controls.Control? Part(string tag)
    {
        foreach (var c in Children) if (c.Tag as string == tag) return c;
        return null;
    }

    private static PrPanel? FrameOf(Avalonia.Controls.Control? c) => (c as IReactorPanel)?.PanelControl;

    private static bool Closed(Avalonia.Controls.Control? c) => FrameOf(c) is { IsClosed: true };

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        foreach (var ch in Children)
        {
            ch.PropertyChanged -= OnChildProperty;
            ch.PropertyChanged += OnChildProperty;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchFrames();
    }

    /// <summary>每块面板的 PrPanel：放大在网格里原地做（PageHost 不再藏兄弟元素），放大 / 关闭变了就重排。</summary>
    private void WatchFrames()
    {
        foreach (var c in Children)
        {
            if (FrameOf(c) is not { } f || !_watched.Add(f)) continue;
            PageHost.SetInPlaceMax(f, true);
            f.PropertyChanged += OnFrameProperty;
        }
    }

    private void OnChildProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) InvalidateMeasure();
    }

    private void OnFrameProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == PrPanel.IsMaximizedProperty) SyncMax();
        else if (e.Property == PrPanel.IsClosedProperty) InvalidateMeasure();
    }

    /// <summary>放大的面板：铺满网格，其余的藏起来（Animation 优先级，盖过本地值 / 绑定 / 样式，还原时撤掉）。</summary>
    private void SyncMax()
    {
        Avalonia.Controls.Control? max = null;
        foreach (var c in Children) if (FrameOf(c) is { IsMaximized: true }) { max = c; break; }
        if (ReferenceEquals(max, _max)) return;
        foreach (var h in _hidden.Values) h.Dispose();
        _hidden.Clear();
        _max = max;
        if (max != null)
            foreach (var c in Children)
                if (!ReferenceEquals(c, max)) _hidden[c] = c.SetValue(IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation)!;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        WatchFrames();
        var size = new Size(double.IsInfinity(availableSize.Width) ? 1226 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 752 : availableSize.Height);
        var rects = Layout(size);
        double scale = LayoutHelper.GetLayoutScale(this);
        // 按对齐到设备像素之后的尺寸量（同排版；样稿的 offsetWidth 也是整数：程序画布按 564 排，不是 563.95）
        foreach (var c in Children)
            c.Measure(rects.TryGetValue(c, out var r) ? Snap(r, scale).Size : default);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rects = Layout(finalSize);
        double scale = LayoutHelper.GetLayoutScale(this);
        foreach (var c in Children)
        {
            var r = rects.TryGetValue(c, out var rr) ? rr : default;
            // 曲线 / 程序面板的图例、面包屑、状态行按小数宽度判断阈值（同样稿的容器查询）
            if (c is ReactorCurvesPanel cp) cp.LayoutWidth = r.Width;
            else if (c is ReactorProgramPanel pp) pp.LayoutWidth = r.Width;
            c.Arrange(Snap(r, scale));
        }
        return finalSize;
    }

    /// <summary>各块的矩形（小数，网格坐标）；不显示的块不在里面。</summary>
    private Dictionary<Avalonia.Controls.Control, Rect> Layout(Size size)
    {
        var map = new Dictionary<Avalonia.Controls.Control, Rect>();
        var prog = Part("prog");
        var vessel = Part("vessel");
        var props = Part("props");
        var curves = Part("curves");
        var un = Part("un");
        if (!Installed)
        {
            // 未安装：只有说明一块（其余不显示）
            if (un != null) map[un] = new Rect(size);
            SetShown(un, true);
            SetShown(prog, false);
            SetShown(vessel, false);
            SetShown(props, false);
            SetShown(curves, false);
            Grid = null;
            return map;
        }
        SetShown(un, false);
        SetShown(prog, true);
        SetShown(vessel, true);
        SetShown(curves, true);
        var closed = new List<ReactorPanel>(4);
        if (Closed(prog)) closed.Add(ReactorPanel.Program);
        if (Closed(vessel)) closed.Add(ReactorPanel.Vessel);
        if (Closed(props)) closed.Add(ReactorPanel.Props);
        if (Closed(curves)) closed.Add(ReactorPanel.Curves);
        var g = ReactorPageLayout.Compute(size.Width, size.Height, closed);
        Grid = g;
        // 宽版式：「釜体」与「属性查看器」各一块；页签版式：一块页签面板，属性查看器那一块不显示（只在版式变了的时候改，别处设的 Mode 不被每次排版顶掉）
        if (vessel is ReactorVesselPanel vp && g.Wide != _wide)
        {
            _wide = g.Wide;
            vp.Mode = g.Wide ? VesselPanelMode.Vessel : VesselPanelMode.Tabs;
        }
        SetShown(props, g.Wide);
        if (_max != null && _max.IsVisible)
        {
            map[_max] = new Rect(size);
            return map;
        }
        void Put(Avalonia.Controls.Control? c, ReactorPanel p)
        {
            if (c != null && g[p] is { } r) map[c] = new Rect(r.X, r.Y, r.W, r.H);
        }
        Put(prog, ReactorPanel.Program);
        Put(vessel, ReactorPanel.Vessel);
        Put(props, ReactorPanel.Props);
        Put(curves, ReactorPanel.Curves);
        return map;
    }

    // 版式决定显示哪几块（未安装 / 宽版式的属性查看器）：用一层自己的「显示」标记（本地值），放大 / 关闭用的 Animation 层照样盖在上面
    private void SetShown(Avalonia.Controls.Control? c, bool shown)
    {
        if (c == null) return;
        bool cur = c.GetValue(ShownProperty);
        if (cur == shown) return;
        c.SetValue(ShownProperty, shown);
        c.IsVisible = shown;
    }

    private static readonly AttachedProperty<bool> ShownProperty = AvaloniaProperty.RegisterAttached<ReactorGridPanel, Avalonia.Controls.Control, bool>("Shown", true);

    /// <summary>
    /// 面板的矩形对齐到设备像素（<paramref name="scale"/> = 渲染缩放 LayoutHelper.GetLayoutScale；100 % 时就是整 DIP，结果与以前逐位相同）。四条边各自取整，
    /// 不是位置和尺寸各自取整：右 / 下边 = <see cref="Edge"/>；左 / 上边 = <see cref="Lead"/>（前一块的右 / 下边取整后再隔开 <see cref="DividerPx"/> 个设备像素）。
    /// <list type="bullet">
    /// <item>Avalonia 排版时还要再取一次整（ArrangeCore：位置 RoundLayoutValue 银行家舍入、尺寸 RoundLayoutSizeUp 向上取整）：边在设备像素上时这两次取整都不改动它；
    ///   以前按整 DIP 取整，125 % 下约 10 % 的网格尺寸分隔线没了（0 px），150 % / 175 % 下有的变成 2 px（复核）。</item>
    /// <item>分隔线永远正好 <see cref="DividerPx"/> 个设备像素（100 % / 125 % / 150 % / 175 % 都是 1，200 % 是 2）。</item>
    /// <item>小数的面板（样稿 CSS 网格 1fr 的 661.05）不会因为尺寸向上取整多出 1 px、压到下一块上；100 % 时面板 = 样稿的 clientWidth / clientHeight（曲线容器 643 × 315，1920）。</item>
    /// <item>一律「.5 进位」，不用 Math.Round 的银行家舍入：银行家舍入在 x = 563.5 时把 563.5 与 564.5 都取成 564，分隔线就没了（复核：窗口宽 1889）。</item>
    /// </list>
    /// </summary>
    public static Rect Snap(Rect r, double scale = 1)
    {
        if (r.Width <= 0 || r.Height <= 0) return r;
        double x0 = Lead(r.X, scale), y0 = Lead(r.Y, scale), x1 = Edge(r.Right, scale), y1 = Edge(r.Bottom, scale);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>
    /// 一条边（右 / 下边，或网格起点上的左 / 上边）取整到设备像素：先对齐到 1/64 设备像素（同 WebKit 的 LayoutUnit；去掉浮点误差 ——
    /// 563.4999999999999 与 564.5000000000001 不会被分到 .5 的两边），再 .5 一律进位（floor(v + 0.5)）。
    /// </summary>
    public static double Edge(double v, double scale = 1)
    {
        double s = ValidScale(scale);
        return Px(v, s) / s;
    }

    /// <summary>
    /// 左 / 上边：网格起点（≤ 0）照常取整；其余的前面都是 1 DIP 的分隔线（网格 gap），取「前一块的右 / 下边（v − 1）取整 + 分隔线的设备像素数」——
    /// 分隔线的粗细不随位置变（单独取整时 1.25 px 的分隔线会是 1 或 2 px）。100 % 时 = <see cref="Edge"/>(v)。
    /// </summary>
    public static double Lead(double v, double scale = 1)
    {
        double s = ValidScale(scale);
        return v <= 0 ? Px(v, s) / s : (Px(v - ReactorPageLayout.Gap, s) + DividerPx(s)) / s;
    }

    /// <summary>1 DIP 的分隔线画成几个设备像素：floor(缩放)，至少 1（同浏览器给 1 px 边框取整：125 % / 150 % / 175 % 都是 1，200 % 是 2）。</summary>
    public static double DividerPx(double scale) => Math.Max(1, Math.Floor(ValidScale(scale) + 1e-6));

    /// <summary>边落在第几个设备像素上（整数）：对齐到 1/64 设备像素后 .5 进位。</summary>
    private static double Px(double v, double s) => Math.Floor(Math.Round(v * s * 64) / 64 + 0.5);

    private static double ValidScale(double scale) => scale > 0 && double.IsFinite(scale) ? scale : 1;
}
