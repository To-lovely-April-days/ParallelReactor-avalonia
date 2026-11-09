using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Theme;
using LayoutPage = ParallelReactor.Control.Core.Layout.DevicePage;

namespace ParallelReactor.Control.Views.Pages.Device;

// ============================================================================
// 设备页的布局面板（PORT-PLAN §4.8：没有容器查询 → Core 的布局计算器 + 放得下才显示）。全部只看父元素给的尺寸（R4，不会来回振荡）；
// 数字规则来自 Core 的 DeviceLayout（样稿 device.js gridOf / sizeRoot / fitSubs、css/device.css 的容器查询），这里只量字宽、摆位置。
// 让出去的子元素排成 0 × 0（审计跳过、不画），再出现时让里面的文字重新排（Controls/TextRelayout，11.1 的缺陷）。
// ============================================================================

/// <summary>
/// 设备页布局面板的基类：子元素排成 0 × 0（让出去、不画、审计跳过）时记下来，之后再按正常的大小排出来时让它子树里的文字重新排
/// （Controls/TextRelayout：Avalonia 11.1 的 TextBlock 按 0 宽重建过文字之后，宽度变大不会再重建 —— 文字是空的）。
/// 第一次排版时卡片还没有版面（视图还没给尺寸）、卡片里按卡片尺寸让出去的行，都走这里。
/// </summary>
public abstract class DvPanel : Panel
{
    private readonly HashSet<Layoutable> _zeroed = new();

    /// <summary>排一个子元素：矩形是空的就排成 0 × 0 并记下；从 0 × 0 回来时先让它的文字重新排。</summary>
    protected void Put(Layoutable c, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            c.Arrange(default);
            _zeroed.Add(c);
            return;
        }
        if (_zeroed.Remove(c) && c is Visual v) TextRelayout.Revive(v);
        c.Arrange(r);
    }

    /// <summary>
    /// 放得下吗用的宽度：文字 = 前进宽度的小数值（同 WebKit 的小数排版 —— PrText 的框宽向上取整到整像素，几段加起来会多出 1–2 px，
    /// 刚好放得下的「冷却 → 40 ℃」就被挤掉了）；其它 = 测量宽度。
    /// </summary>
    internal static double Nat(Layoutable? c)
    {
        if (c is null) return 0;
        if (c is PrText pt && pt.IsVisible) return Math.Min(pt.NaturalWidth, pt.DesiredSize.Width);
        return c.DesiredSize.Width;
    }

    /// <summary>这个面板的布局缩放（渲染缩放：1 / 1.25 / 1.5 …；还没上树时 1）。</summary>
    protected double LayoutScale
    {
        get
        {
            double s = LayoutHelper.GetLayoutScale(this);
            return s > 0 && !double.IsInfinity(s) ? s : 1;
        }
    }

    /// <summary>设计 px → 最近的设备像素（四舍五入，.5 进位），再换回设计 px。</summary>
    internal static double SnapToDevice(double v, double scale) => Math.Round(v * scale, MidpointRounding.AwayFromZero) / scale;

    /// <summary>被父元素排成 0 × 0（让出去）时：子元素也都排成 0 × 0。返回是否这样处理了。</summary>
    protected bool Collapsed(Size finalSize)
    {
        if (finalSize.Width > 0 && finalSize.Height > 0) return false;
        foreach (var c in Children) Put(c, default);
        return true;
    }
}

/// <summary>
/// 设备页的两行（样稿 .device-grid：上「反应釜」、下排三块 / 两块，行间 1 px line-strong）：子元素 [0] 反应釜面板、[1] 下排（<see cref="DvRowPanel"/>）。
/// 上排高度 = <see cref="TopRow"/>（Core 的 DevicePage.TopRow：默认 52 %，卡片放不下时加高）；只剩一行（反应釜面板或整个下排关掉、或放大时被藏起来）时它铺满。
/// </summary>
public sealed class DvPagePanel : DvPanel
{
    public static readonly StyledProperty<double?> TopRowProperty = AvaloniaProperty.Register<DvPagePanel, double?>(nameof(TopRow));

    static DvPagePanel() => AffectsMeasure<DvPagePanel>(TopRowProperty);

    public double? TopRow { get => GetValue(TopRowProperty); set => SetValue(TopRowProperty, value); }

    /// <summary>上排高度与下排的起点（两行都在时中间 1 px line-strong 的缝：两边都对齐到设备像素，缩放 125 % / 150 % 时缝也不会被取整盖住）。</summary>
    private (bool Rv, bool Row, double Top, double Below) Split(Size size)
    {
        var rv = Children.Count > 0 ? Children[0] : null;
        var row = Children.Count > 1 ? Children[1] as DvRowPanel : null;
        bool a = rv is { IsVisible: true }, b = row is { IsVisible: true } && row.HasActive;
        double top = a && b ? Math.Clamp(TopRow ?? Math.Floor(size.Height * DeviceLayout.TopShare + 0.5), 0, Math.Max(0, size.Height - 1)) : a ? size.Height : 0;
        if (!(a && b)) return (a, b, top, a ? size.Height : 0);
        double scale = LayoutScale;
        return (a, b, SnapToDevice(top, scale), SnapToDevice(top + 1, scale));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 800 : availableSize.Height);
        var (a, b, top, below) = Split(size);
        if (Children.Count > 0) Children[0].Measure(a ? new Size(size.Width, top) : default);
        if (Children.Count > 1) Children[1].Measure(b ? new Size(size.Width, a ? Math.Max(0, size.Height - below) : size.Height) : default);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (a, b, top, below) = Split(finalSize);
        if (Children.Count > 0) Put(Children[0], a ? new Rect(0, 0, finalSize.Width, top) : default);
        if (Children.Count > 1)
            Put(Children[1], b ? (a ? new Rect(0, below, finalSize.Width, Math.Max(0, finalSize.Height - below)) : new Rect(finalSize)) : default);
        return finalSize;
    }
}

/// <summary>下排里一块面板的占位（宽工作区的「设备信息」「报警与事件」、窄工作区的页签面板）：按宽度类显示 / 不显示，面板自己的关闭照常。</summary>
public sealed class DvSlot : Decorator
{
}

/// <summary>
/// 下排（样稿 .device-row：列间 1 px line-strong）：宽工作区三列 1 : 1 : 1.2，窄工作区两列 1 : 1.3（附加属性 <see cref="FrProperty"/>）；
/// 有面板关掉时其余平分（device.css :has(> .pr-panel.is-closed)）。只排显示着的面板（关掉的、放大时被藏起来的、宽度类不用的都不排）。
/// </summary>
public sealed class DvRowPanel : DvPanel
{
    public static readonly AttachedProperty<double> FrProperty = AvaloniaProperty.RegisterAttached<DvRowPanel, Layoutable, double>("Fr", 1);

    static DvRowPanel() => AffectsParentMeasure<DvRowPanel>(FrProperty);

    public static double GetFr(Layoutable c) => c.GetValue(FrProperty);
    public static void SetFr(Layoutable c, double v) => c.SetValue(FrProperty, v);

    /// <summary>这一格里的面板（直接放的，或 <see cref="DvSlot"/> 里的）。</summary>
    private static PrPanel? PanelOf(Avalonia.Controls.Control c) => c as PrPanel ?? (c as DvSlot)?.Child as PrPanel;

    /// <summary>参与排版：格子显示着、面板没关。</summary>
    private static bool Active(Avalonia.Controls.Control c) => c.IsVisible && PanelOf(c) is { IsVisible: true };

    /// <summary>至少有一块面板在排（整个下排都关掉时上排铺满）。</summary>
    public bool HasActive
    {
        get
        {
            foreach (var c in Children) if (Active(c)) return true;
            return false;
        }
    }

    private List<(Avalonia.Controls.Control C, double X, double W)> Columns(double width)
    {
        var act = new List<Avalonia.Controls.Control>();
        bool anyClosed = false;
        foreach (var c in Children)
        {
            if (Active(c)) act.Add(c);
            else if (c.IsVisible && PanelOf(c) is { IsClosed: true }) anyClosed = true;
        }
        var cols = new List<(Avalonia.Controls.Control, double, double)>(act.Count);
        if (act.Count == 0) return cols;
        var fr = new double[act.Count];
        for (int i = 0; i < act.Count; i++) fr[i] = anyClosed ? 1 : GetFr(act[i]);
        var split = Split(width, fr, LayoutScale);
        for (int i = 0; i < act.Count; i++) cols.Add((act[i], split[i].X, split[i].W));
        return cols;
    }

    /// <summary>
    /// 各列的位置与宽度（设计 px）：<paramref name="width"/> 减去列间各 1 px 后按 <paramref name="fr"/> 分；每列的左右边缘都对齐到设备像素
    /// （<paramref name="scale"/> = 渲染缩放），最后一列到右边缘。列间的 1 px line-strong 缝因此至少是 1 个设备像素 —— 以前按小数排，
    /// 布局取整把前一列的宽度向上取整、正好盖住缝（M4 复核：1920 宽时「设备信息 | 报警与事件」、1366 宽时「共用资源 | 页签面板」之间没有线）。
    /// </summary>
    public static (double X, double W)[] Split(double width, IReadOnlyList<double> fr, double scale)
    {
        int n = fr.Count;
        var r = new (double X, double W)[n];
        if (n == 0) return r;
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        double sum = 0;
        foreach (var f in fr) sum += f;
        double avail = Math.Max(0, width - (n - 1)), x = 0;
        long Dev(double v) => (long)Math.Round(v * scale, MidpointRounding.AwayFromZero);
        long right = Dev(width);
        for (int i = 0; i < n; i++)
        {
            double w = sum > 0 ? avail * fr[i] / sum : 0;
            long a = Math.Min(Dev(x), right), b = i == n - 1 ? right : Math.Min(Dev(x + w), right);
            r[i] = (a / scale, Math.Max(0, b - a) / scale);
            x += w + 1;
        }
        return r;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height);
        var cols = Columns(size.Width);
        foreach (var c in Children) if (!cols.Exists(x => ReferenceEquals(x.C, c))) c.Measure(default);
        foreach (var (c, _, w) in cols) c.Measure(new Size(w, size.Height));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Columns(finalSize.Width);
        foreach (var c in Children) if (!cols.Exists(x => ReferenceEquals(x.C, c))) Put(c, default);
        foreach (var (c, x, w) in cols) Put(c, new Rect(x, 0, w, finalSize.Height));
        return finalSize;
    }
}

/// <summary>
/// 反应釜卡片区（样稿 .device-cards，CSS grid）：子元素按顺序放进 Core 算好的块（<see cref="Layout"/>.Tiles：位置、跨列、尺寸）——
/// 宽卡 / 一排卡每张一列、未安装合并成 140 px 窄列；紧凑卡固定 8 列、未安装段跨它自己的列数。高度 = <see cref="ContentHeight"/>
/// （卡片区滚动时 = 内边距 + 行 × 卡片最小高度 + 行距；否则 = 卡片区高度）。
/// </summary>
public sealed class DvCardsPanel : DvPanel
{
    public static readonly StyledProperty<LayoutPage?> LayoutProperty = AvaloniaProperty.Register<DvCardsPanel, LayoutPage?>(nameof(Layout));
    public static readonly StyledProperty<double> ContentHeightProperty = AvaloniaProperty.Register<DvCardsPanel, double>(nameof(ContentHeight));

    static DvCardsPanel() => AffectsMeasure<DvCardsPanel>(LayoutProperty, ContentHeightProperty);

    public LayoutPage? Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public double ContentHeight { get => GetValue(ContentHeightProperty); set => SetValue(ContentHeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var tiles = Layout?.Tiles;
        double right = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (tiles != null && i < tiles.Count)
            {
                var t = tiles[i];
                c.Measure(new Size(Math.Max(0, t.Width), Math.Max(0, t.Height)));
                right = Math.Max(right, t.X + t.Width);
            }
            else c.Measure(default);
        }
        if (Layout == null) return default;
        double w = double.IsInfinity(availableSize.Width) ? right + Layout.Grid.Pad : availableSize.Width;
        return new Size(w, Math.Max(0, ContentHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tiles = Layout?.Tiles;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (tiles != null && i < tiles.Count)
            {
                var t = tiles[i];
                Put(c, new Rect(t.X, t.Y, Math.Max(0, t.Width), Math.Max(0, t.Height)));
            }
            else Put(c, default);
        }
        return finalSize;
    }
}

/// <summary>
/// 一张反应釜卡片的内部（样稿 .device-card：flex 竖排 标题 · 主体 · 程序块，间距 10 / 紧凑 6 / 窄紧凑 3；内部取舍按卡片自身尺寸的容器查询）。
/// 放在卡片的边框（1 px，Border）里：可用尺寸 = 网格给的格子 − 2。子元素 [0] 标题（<see cref="DvCardHead"/>）、[1] 釜体、[2] 读数列（<see cref="DvReads"/>）、
/// [3] 程序块（<see cref="DvProg"/>）。
/// <para>
/// 测量时先按卡片宽量标题（「RV11 ● 超压报警」放不下时状态换到第二行：48），再用 Core 的 <see cref="DeviceLayout.Card"/> 算这张卡：内边距、主体高、
/// 釜体在左 / 在上 / 不画及其尺寸、读数列宽、读数字号阶梯（data-len：<see cref="TempLen"/> …）、设定值 / 目标同一行 / 单独一行（放不下整行不显示：
/// <see cref="DeviceCard.FitSubs"/>，先目标、再设定值；升压中 / 等待中先藏设定值）、宽卡按高度去掉耗气 → 加热 → 搅拌、程序块显示什么；再把这些交给子元素。
/// </para>
/// </summary>
public sealed class DvCard : DvPanel
{
    public static readonly StyledProperty<DvCardSpec> SpecProperty = AvaloniaProperty.Register<DvCard, DvCardSpec>(nameof(Spec), DvCardSpec.Default);
    public static readonly StyledProperty<int> TempLenProperty = AvaloniaProperty.Register<DvCard, int>(nameof(TempLen));
    public static readonly StyledProperty<int> PressLenProperty = AvaloniaProperty.Register<DvCard, int>(nameof(PressLen));
    public static readonly StyledProperty<int> StirLenProperty = AvaloniaProperty.Register<DvCard, int>(nameof(StirLen));
    public static readonly StyledProperty<int> HeatLenProperty = AvaloniaProperty.Register<DvCard, int>(nameof(HeatLen));
    public static readonly StyledProperty<int> GasLenProperty = AvaloniaProperty.Register<DvCard, int>(nameof(GasLen));
    public static readonly StyledProperty<bool> HasSetpointProperty = AvaloniaProperty.Register<DvCard, bool>(nameof(HasSetpoint));
    public static readonly StyledProperty<bool> HasTargetProperty = AvaloniaProperty.Register<DvCard, bool>(nameof(HasTarget));
    public static readonly StyledProperty<bool> PressFirstProperty = AvaloniaProperty.Register<DvCard, bool>(nameof(PressFirst));

    static DvCard() => AffectsMeasure<DvCard>(SpecProperty, TempLenProperty, PressLenProperty, StirLenProperty, HeatLenProperty, GasLenProperty,
        HasSetpointProperty, HasTargetProperty, PressFirstProperty);

    public DvCardSpec Spec { get => GetValue(SpecProperty); set => SetValue(SpecProperty, value); }
    public int TempLen { get => GetValue(TempLenProperty); set => SetValue(TempLenProperty, value); }
    public int PressLen { get => GetValue(PressLenProperty); set => SetValue(PressLenProperty, value); }
    public int StirLen { get => GetValue(StirLenProperty); set => SetValue(StirLenProperty, value); }
    public int HeatLen { get => GetValue(HeatLenProperty); set => SetValue(HeatLenProperty, value); }
    public int GasLen { get => GetValue(GasLenProperty); set => SetValue(GasLenProperty, value); }
    /// <summary>温度旁有设定值小字（「SP 120 ℃」/「→ 120 ℃」）。</summary>
    public bool HasSetpoint { get => GetValue(HasSetpointProperty); set => SetValue(HasSetpointProperty, value); }
    /// <summary>压力旁有目标小字（「→ 0.350 MPa」/「等待进气」）。</summary>
    public bool HasTarget { get => GetValue(HasTargetProperty); set => SetValue(HasTargetProperty, value); }
    /// <summary>升压中 / 等待中：放不下时先藏设定值。</summary>
    public bool PressFirst { get => GetValue(PressFirstProperty); set => SetValue(PressFirstProperty, value); }

    /// <summary>最近一次算出的卡片版面（测试 / 排查用）。</summary>
    public DeviceCard? Card { get; private set; }

    /// <summary>最近一次 fitSubs 的结果（测试 / 排查用）。</summary>
    public (bool HideSetpoint, bool HideTarget) Subs { get; private set; }

    private DvCardHead? Head => Children.Count > 0 ? Children[0] as DvCardHead : null;
    private Avalonia.Controls.Control? Vessel => Children.Count > 1 ? Children[1] : null;
    private DvReads? Reads => Children.Count > 2 ? Children[2] as DvReads : null;
    private DvProg? Prog => Children.Count > 3 ? Children[3] as DvProg : null;

    private double _vesselH;

    protected override Size MeasureOverride(Size availableSize)
    {
        var spec = Spec;
        double ow = (double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width) + 2;
        double oh = (double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height) + 2;
        var c = DeviceLayout.Card(spec.Kind, ow, oh, spec.Narrow, spec.Tight, spec.Maximized);
        if (Head is { } head)
        {
            head.Column = c.HeadColumn;
            head.Gap = spec.Kind == CardKind.Compact ? 4 : 8;
            head.Measure(new Size(c.Width, double.PositiveInfinity));
            double hh = head.DesiredSize.Height;
            if (Math.Abs(hh - c.HeadHeight) > 0.01) c = DeviceLayout.Card(spec.Kind, ow, oh, spec.Narrow, spec.Tight, spec.Maximized, hh);
        }
        Card = c;
        var subs = c.FitSubs(HasSetpoint, HasTarget, PressFirst);
        Subs = subs;
        bool spLine = c.Subs == SubLine.OwnLine && HasSetpoint && !subs.HideSetpoint;
        bool tgLine = c.Subs == SubLine.OwnLine && HasTarget && !subs.HideTarget;
        if (Reads is { } reads)
        {
            reads.Configure(c, c.Temp(TempLen), c.Pressure(PressLen), c.StirValue(StirLen), c.HeatValue(HeatLen), c.GasValue(GasLen),
                subs.HideSetpoint, subs.HideTarget);
            reads.Measure(new Size(c.ReadsWidth, double.PositiveInfinity));
        }
        _vesselH = c.Vessel == VesselPlacement.Above ? c.VesselHeightWith(spLine, tgLine) : c.VesselHeight;
        if (Vessel is { } v)
        {
            if (c.Vessel == VesselPlacement.Hidden) v.Measure(default);
            else v.Measure(new Size(Math.Max(0, c.VesselWidth), Math.Max(0, _vesselH)));
        }
        if (Prog is { } prog)
        {
            prog.Configure(c);
            prog.Measure(new Size(c.Width, c.ProgHeight));
        }
        return new Size(ow - 2, oh - 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var c = Card;
        if (c == null) return finalSize;
        double x0 = c.PadLeft, y0 = c.PadTop;
        if (Head is { } head) Put(head, new Rect(x0, y0, c.Width, c.HeadHeight));
        if (Prog is { } prog) Put(prog, new Rect(x0, y0 + c.Height - c.ProgHeight, c.Width, c.ProgHeight));
        var reads = Reads;
        double bodyTop = y0 + c.HeadHeight + c.Gap, bodyH = c.BodyHeight;
        double rh = Reads?.DesiredSize.Height ?? 0;
        var v = Vessel;
        switch (c.Vessel)
        {
            case VesselPlacement.Left:
                if (v != null) Put(v, new Rect(x0, bodyTop, c.VesselWidth, Math.Max(0, _vesselH)));
                if (reads != null) Put(reads, new Rect(x0 + c.VesselWidth + c.BodyGap, c.Narrow ? bodyTop : bodyTop + (bodyH - rh) / 2, c.ReadsWidth, rh));
                break;
            case VesselPlacement.Above:
                if (v != null) Put(v, new Rect(x0, bodyTop, c.Width, Math.Max(0, _vesselH)));
                if (reads != null) Put(reads, new Rect(x0, bodyTop + _vesselH + c.AboveGap, c.Width, rh));
                break;
            default:
                if (v != null) Put(v, default);
                if (reads != null) Put(reads, new Rect(x0, c.Narrow ? bodyTop : bodyTop + (bodyH - rh) / 2, c.ReadsWidth, rh));
                break;
        }
        return finalSize;
    }

}

/// <summary>
/// 卡片标题（样稿 .device-chead：flex-wrap，RV 16/600 行高 24 + 状态（点 + 字，margin-left: auto，高 24）；间距 8（紧凑 4））：
/// 一行放不下时状态换到第二行（仍靠右，高 48）—— 状态字永不省略。<see cref="Column"/>（窄紧凑卡）：RV（行高 20）与状态（高 18）上下两行、都靠左（38）。
/// 子元素 [0] RV、[1] 状态。
/// </summary>
public sealed class DvCardHead : DvPanel
{
    public const double Line = 24, ColRv = 20, ColState = 18;

    public static readonly StyledProperty<bool> ColumnProperty = AvaloniaProperty.Register<DvCardHead, bool>(nameof(Column));
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<DvCardHead, double>(nameof(Gap), 8);

    static DvCardHead() => AffectsMeasure<DvCardHead>(ColumnProperty, GapProperty);

    public bool Column { get => GetValue(ColumnProperty); set => SetValue(ColumnProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>状态换到了第二行（测试 / 排查用）。</summary>
    public bool Wrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        if (Column) { Wrapped = true; return new Size(w, ColRv + ColState); }
        double rw = Children.Count > 0 ? Nat(Children[0]) : 0, sw = Children.Count > 1 ? Children[1].DesiredSize.Width : 0;
        Wrapped = !double.IsInfinity(availableSize.Width) && rw + Gap + sw > availableSize.Width + 0.01;
        return new Size(w, Wrapped ? 2 * Line : Line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Collapsed(finalSize)) return finalSize;
        var rv = Children.Count > 0 ? Children[0] : null;
        var st = Children.Count > 1 ? Children[1] : null;
        if (Column)
        {
            if (rv != null) Put(rv, new Rect(0, (ColRv - rv.DesiredSize.Height) / 2, rv.DesiredSize.Width, rv.DesiredSize.Height));
            if (st != null) Put(st, new Rect(0, ColRv + (ColState - st.DesiredSize.Height) / 2, st.DesiredSize.Width, st.DesiredSize.Height));
            return finalSize;
        }
        if (rv != null) Put(rv, new Rect(0, (Line - rv.DesiredSize.Height) / 2, rv.DesiredSize.Width, rv.DesiredSize.Height));
        if (st != null)
        {
            double y = Wrapped ? Line : 0;
            Put(st, new Rect(Math.Max(0, finalSize.Width - st.DesiredSize.Width), y + (Line - st.DesiredSize.Height) / 2, st.DesiredSize.Width, st.DesiredSize.Height));
        }
        return finalSize;
    }
}

/// <summary>
/// 读数列（样稿 .device-reads：温度行 · 设定值单独一行 · 压力行 · 目标单独一行 · 宽卡的搅拌 / 加热 / 耗气）：
/// 子元素 [0] 温度行（<see cref="DvReadRow"/>）、[1] 设定值行（TextBlock）、[2] 压力行、[3] 目标行、[4] 搅拌、[5] 加热、[6] 耗气（<see cref="DvKvRow"/>）。
/// 由 <see cref="DvCard"/> 在测量时用 <see cref="Configure"/> 交给它这张卡的取舍；它只按顺序竖排（读数列 ≥ 190 时温度行 36、压力行 26 固定高）。
/// </summary>
public sealed class DvReads : DvPanel
{
    public const double SubLineHeight = 18, KvRowHeight = 26, KvTop = 2;

    private DeviceCard? _card;
    private bool _hideSp, _hideTg;

    /// <summary>这张卡的取舍（DvCard 测量时给）：读数字号阶梯、设定值 / 目标的排法与 fitSubs、宽卡的次要行。</summary>
    public void Configure(DeviceCard card, ReadingFont temp, ReadingFont press, ReadingFont stir, ReadingFont heat, ReadingFont gas, bool hideSetpoint, bool hideTarget)
    {
        _card = card;
        _hideSp = hideSetpoint;
        _hideTg = hideTarget;
        bool inline = card.Subs == SubLine.Inline;
        if (Kid(0) is DvReadRow t) t.Set(temp, inline ? DvReadRow.InlineT : temp.LineHeight, inline);
        if (Kid(2) is DvReadRow p) p.Set(press, inline ? DvReadRow.InlineP : press.LineHeight, inline);
        if (Kid(4) is DvKvRow s) s.Set(stir, card.KvNotes);
        if (Kid(5) is DvKvRow h) h.Set(heat, card.KvNotes);
        if (Kid(6) is DvKvRow g) g.Set(gas, card.KvNotes);
    }

    private Avalonia.Controls.Control? Kid(int i) => i < Children.Count ? Children[i] : null;

    private static bool HasText(Avalonia.Controls.Control? c) => c is TextBlock tb && !string.IsNullOrEmpty(tb.Text);

    /// <summary>每个子元素这一遍是否排出来、占多高。</summary>
    private (bool Shown, double H)[] Plan()
    {
        var plan = new (bool, double)[Children.Count];
        var c = _card;
        if (c == null) return plan;
        bool own = c.Subs == SubLine.OwnLine;
        for (int i = 0; i < Children.Count; i++)
        {
            switch (i)
            {
                case 0: plan[i] = (true, Kid(0) is DvReadRow t ? t.RowHeight : 36); break;
                case 1: plan[i] = (own && HasText(Kid(1)) && !_hideSp, SubLineHeight); break;
                case 2: plan[i] = (true, Kid(2) is DvReadRow p ? p.RowHeight : 26); break;
                case 3: plan[i] = (own && HasText(Kid(3)) && !_hideTg, SubLineHeight); break;
                case 4: plan[i] = (c.Kind == CardKind.Wide && c.StirRow, KvRowHeight); break;
                case 5: plan[i] = (c.Kind == CardKind.Wide && c.HeatRow, KvRowHeight); break;
                case 6: plan[i] = (c.Kind == CardKind.Wide && c.GasRow, KvRowHeight); break;
                default: plan[i] = (false, 0); break;
            }
        }
        return plan;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var plan = Plan();
        double w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, h = 0;
        bool kv = false;
        for (int i = 0; i < Children.Count; i++)
        {
            var (shown, ph) = plan[i];
            if (!shown) { Children[i].Measure(default); continue; }
            if (i >= 4 && !kv) { h += KvTop; kv = true; }
            Children[i].Measure(new Size(w, ph));
            h += ph;
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var plan = Plan();
        double y = 0;
        bool kv = false;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            var (shown, ph) = plan[i];
            if (!shown) { Put(c, default); continue; }
            if (i >= 4 && !kv) { y += KvTop; kv = true; }
            Put(c, new Rect(0, y, finalSize.Width, ph));
            y += ph;
        }
        return finalSize;
    }
}

/// <summary>
/// 读数行（样稿 .device-t / .device-p：数字 600 + 单位（间距 4）+ 右侧设定值 / 目标（间距 10，margin-left: auto），按基线对齐）：
/// 子元素 [0] 数值、[1] 单位、[2] 同一行的小字（读数列 ≥ 190 时：放得下才显示，整个让出去，不省略成「SP 25…」）。
/// 字号 / 行高 / 单位来自 Core 的读数阶梯（data-len：只有长读数才缩，最后一档去单位）；兜底（Skia 与 WebKit 的字宽不同时）：数值自己都放不下就每次缩 1 px（≥ 13），读数永不截断。
/// </summary>
public sealed class DvReadRow : DvPanel
{
    public const double UnitGap = 4, SubGap = 10, InlineT = 36, InlineP = 26;

    private ReadingFont _font = new(30, 36);
    private double _rowH = 36;
    private bool _inline;
    private bool _unit, _sub;

    /// <summary>这一行的高度（读数列 ≥ 190：温度 36 / 压力 26 固定；否则 = 阶梯的行高）。</summary>
    public double RowHeight => _rowH;

    /// <summary>最近一次的取舍（测试 / 排查用）：单位、同一行的小字、数值最后的字号。</summary>
    public (bool Unit, bool Sub, double Size) Chosen { get; private set; }

    public void Set(ReadingFont font, double rowHeight, bool inlineSub)
    {
        if (font == _font && Math.Abs(rowHeight - _rowH) < 0.01 && inlineSub == _inline) return;
        _font = font;
        _rowH = rowHeight;
        _inline = inlineSub;
        InvalidateMeasure();
    }

    private TextBlock? Value => Children.Count > 0 ? Children[0] as TextBlock : null;
    private Avalonia.Controls.Control? Unit => Children.Count > 1 ? Children[1] : null;
    private Avalonia.Controls.Control? Sub => Children.Count > 2 ? Children[2] : null;

    private static bool HasText(Avalonia.Controls.Control? c) => c is { IsVisible: true } && (c is not TextBlock tb || !string.IsNullOrEmpty(tb.Text));

    protected override Size MeasureOverride(Size availableSize)
    {
        var v = Value;
        if (v == null) return default;
        double size = _font.Size;
        if (!Equals(v.LineHeight, _font.LineHeight)) v.LineHeight = _font.LineHeight;
        SetValueSize(v, size);
        v.Measure(Size.Infinity);
        double w = availableSize.Width;
        bool finite = !double.IsInfinity(w);
        while (finite && Nat(v) > w + 0.01 && size > FitText.MinSize)
        {
            size = Math.Max(FitText.MinSize, size - 1);
            SetValueSize(v, size);
            v.Measure(Size.Infinity);
        }
        double vw = Nat(v);
        var unit = Unit;
        var sub = Sub;
        unit?.Measure(Size.Infinity);
        sub?.Measure(Size.Infinity);
        double uw = Nat(unit), sw = Nat(sub);
        bool unitWanted = _font.Unit && size >= _font.Size && HasText(unit);
        bool unitFits = !finite || vw + UnitGap + uw <= w + 0.01;
        _unit = unitWanted && unitFits;
        double used = vw + (_unit ? UnitGap + uw : 0);
        _sub = _inline && HasText(sub) && !(unitWanted && !unitFits) && (!finite || used + SubGap + sw <= w + 0.01);
        Chosen = (_unit, _sub, size);
        return new Size(finite ? w : used + (_sub ? SubGap + sw : 0), _rowH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var v = Value;
        if (v == null) return finalSize;
        if (Collapsed(finalSize)) return finalSize;
        double vb = Baseline(v);
        // 数值在行里的位置：行高 = 数值的行高时贴顶；行更高（≥ 190 的固定 36 / 26）时同 WebKit 的基线对齐（数值的框从行顶开始）
        Put(v, new Rect(0, 0, v.DesiredSize.Width, v.DesiredSize.Height));
        double x = v.DesiredSize.Width;
        Place(Unit, _unit, c => new Rect(x + UnitGap, vb - Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        Place(Sub, _sub, c => new Rect(finalSize.Width - c.DesiredSize.Width, vb - Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        for (int i = 3; i < Children.Count; i++) Put(Children[i], default);
        return finalSize;
    }

    private void Place(Avalonia.Controls.Control? c, bool shown, Func<Avalonia.Controls.Control, Rect> rect)
    {
        if (c == null) return;
        Put(c, shown ? rect(c) : default);
    }

    /// <summary>读数数值的字号与字距（样稿 .device-v b：letter-spacing -.01em，温度 / 压力 / 宽卡次要行的数值都是它）。</summary>
    internal static void SetValueSize(TextBlock v, double size)
    {
        if (!Equals(v.FontSize, size)) v.FontSize = size;
        double ls = -0.01 * size;
        if (!Equals(v.LetterSpacing, ls)) v.LetterSpacing = ls;
    }

    /// <summary>子元素顶边到第一行基线的距离（TextBlock：第一行的基线；其它：底边）。</summary>
    internal static double Baseline(Layoutable c)
    {
        if (c is TextBlock tb && tb.TextLayout.TextLines is { Count: > 0 } lines) return tb.Padding.Top + lines[0].Baseline;
        return c.DesiredSize.Height;
    }
}

/// <summary>
/// 宽卡的次要读数行（样稿 .device-kvr.pr-fitrow：标签 14 t-3 宽 30 · 间距 8 · 数值 16/600（不压缩，阶梯：只有长读数才缩）+ 单位 13 t-3（间距 4）·
/// 右侧说明 14 t-3（margin-left: auto，放得下才显示，读数列 ≤ 150 时不显示），高 26，按基线对齐）。子元素 [0] 标签、[1] 数值、[2] 单位、[3] 说明。
/// </summary>
public sealed class DvKvRow : DvPanel
{
    public const double LabelWidth = 30, Gap = 8, UnitGap = 4;

    private ReadingFont _font = new(16, 26);
    private bool _notes = true;
    private bool _unit, _note;

    /// <summary>最近一次的取舍（测试 / 排查用）。</summary>
    public (bool Unit, bool Note, double Size) Chosen { get; private set; }

    public void Set(ReadingFont font, bool notes)
    {
        if (font == _font && notes == _notes) return;
        _font = font;
        _notes = notes;
        InvalidateMeasure();
    }

    private Avalonia.Controls.Control? Kid(int i) => i < Children.Count ? Children[i] : null;

    private static bool HasText(Avalonia.Controls.Control? c) => c is { IsVisible: true } && (c is not TextBlock tb || !string.IsNullOrEmpty(tb.Text));

    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Kid(0);
        var v = Kid(1) as TextBlock;
        var unit = Kid(2);
        var note = Kid(3);
        label?.Measure(Size.Infinity);
        double w = availableSize.Width;
        bool finite = !double.IsInfinity(w);
        double size = _font.Size;
        if (v != null)
        {
            DvReadRow.SetValueSize(v, size);
            v.Measure(Size.Infinity);
            while (finite && LabelWidth + Gap + Nat(v) > w + 0.01 && size > FitText.MinSize)
            {
                size = Math.Max(FitText.MinSize, size - 1);
                DvReadRow.SetValueSize(v, size);
                v.Measure(Size.Infinity);
            }
        }
        unit?.Measure(Size.Infinity);
        note?.Measure(Size.Infinity);
        double vw = Nat(v), uw = Nat(unit), nw = Nat(note);
        double x = LabelWidth + Gap + vw;
        bool unitWanted = _font.Unit && size >= _font.Size && HasText(unit);
        _unit = unitWanted && (!finite || x + UnitGap + uw <= w + 0.01);
        if (_unit) x += UnitGap + uw;
        _note = _notes && HasText(note) && (!finite || x + Gap + nw <= w + 0.01);
        Chosen = (_unit, _note, size);
        return new Size(finite ? w : x + (_note ? Gap + nw : 0), DvReads.KvRowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var label = Kid(0);
        var v = Kid(1);
        if (v == null) return finalSize;
        if (Collapsed(finalSize)) return finalSize;
        // 基线：数值（16 px，行高 26 的行里居中）的基线；标签、单位、说明对齐到它
        double vh = v.DesiredSize.Height;
        double vy = (finalSize.Height - vh) / 2;
        Put(v, new Rect(LabelWidth + Gap, vy, v.DesiredSize.Width, vh));
        double vb = vy + DvReadRow.Baseline(v);
        if (label != null) Put(label, new Rect(0, vb - DvReadRow.Baseline(label), Math.Min(label.DesiredSize.Width, LabelWidth + Gap), label.DesiredSize.Height));
        double x = LabelWidth + Gap + v.DesiredSize.Width;
        Place(Kid(2), _unit, c => new Rect(x + UnitGap, vb - DvReadRow.Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        Place(Kid(3), _note, c => new Rect(finalSize.Width - c.DesiredSize.Width, vb - DvReadRow.Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        return finalSize;
    }

    private void Place(Avalonia.Controls.Control? c, bool shown, Func<Avalonia.Controls.Control, Rect> rect)
    {
        if (c == null) return;
        Put(c, shown ? rect(c) : default);
    }
}

/// <summary>
/// 程序块（样稿 .device-prog：上边 1 px line-faint + 上内边距 9；两行各 20、行距 6 ——「程序名 … 第 k/n 步」｜「分段进度条 … 剩余」）：
/// 子元素 [0] 程序名（或「无程序」）、[1] 第 k/n 步、[2] 进度条、[3] 剩余（或「无程序」）、[4] 上边线（1 px line-faint，窄紧凑卡不画）。由 <see cref="DvCard"/> 用 <see cref="Configure"/> 交给它这张卡的取舍：
/// 卡片矮于 240 只留进度条一行（上内边距 8，紧凑 6）、窄于 168 不写第 k/n 步、没有程序时进度行写不写「无程序」/ 画不画空轨、窄紧凑卡只有 8 px 的进度条。
/// 进度条每段会窄于 6 px 时剩余时间整个不显示，进度条占满整行（ui.fitStepBars 的 is-barfit）。
/// </summary>
public sealed class DvProg : DvPanel
{
    public const double LineH = 20, LineGap = 6, Gap = 10;

    public static readonly StyledProperty<bool> HasProgramProperty = AvaloniaProperty.Register<DvProg, bool>(nameof(HasProgram));

    static DvProg()
    {
        AffectsMeasure<DvProg>(HasProgramProperty);
    }

    public bool HasProgram { get => GetValue(HasProgramProperty); set => SetValue(HasProgramProperty, value); }

    private DeviceCard? _card;
    private bool _name, _kn, _bar, _rem;
    private double _barW, _knW, _remW;

    /// <summary>剩余时间为进度条让位了（is-barfit）。</summary>
    public bool IsBarFit { get; private set; }

    public void Configure(DeviceCard card)
    {
        if (ReferenceEquals(card, _card) || card == _card) return;
        _card = card;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private Avalonia.Controls.Control? Kid(int i) => i < Children.Count ? Children[i] : null;

    private static bool HasText(Avalonia.Controls.Control? c) => c is TextBlock tb && !string.IsNullOrEmpty(tb.Text);

    private double PadTop => _card is not { } c || c.Narrow ? 0 : c.ProgNameLine ? 9 : c.Kind == CardKind.Compact ? 6 : 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var c = _card;
        foreach (var k in Children) k.Measure(Size.Infinity);
        double w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        IsBarFit = false;
        if (c == null) return new Size(w, 0);
        bool has = HasProgram;
        var bar = Kid(2);
        int n = bar is StepBar sb ? sb.Segments.Count : 0;
        if (bar is StepBar sbar) sbar.Track = c.Narrow ? 8 : StepBar.TrackHeight;
        if (c.Narrow)
        {
            _name = _kn = _rem = false;
            _bar = has || c.NoneBar;
            _barW = w;
        }
        else
        {
            _name = c.ProgNameLine;
            _kn = _name && has && c.StepCount && HasText(Kid(1));
            _knW = _kn ? Kid(1)!.DesiredSize.Width : 0;
            double remNat = HasText(Kid(3)) ? Kid(3)!.DesiredSize.Width : 0;
            if (has)
            {
                _bar = true;
                _rem = remNat > 0;
                _remW = remNat;
                _barW = Math.Max(0, w - (_rem ? remNat + Gap : 0));
                if (_rem && n > 1 && _barW < StepBar.MinBarWidth(n))
                {
                    IsBarFit = true;
                    _rem = false;
                    _barW = w;
                }
            }
            else
            {
                _bar = c.NoneBar;
                _rem = c.NoneRemaining && remNat > 0;
                _remW = remNat;
                _barW = _bar ? Math.Max(0, w - (_rem ? remNat + Gap : 0)) : 0;
            }
            if (_name && Kid(0) is { } name) name.Measure(new Size(Math.Max(0, w - (_kn ? _knW + Gap : 0)), double.PositiveInfinity));
        }
        if (bar != null) bar.Measure(_bar ? new Size(_barW, double.PositiveInfinity) : default);
        return new Size(w, c.ProgHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var c = _card;
        if (c == null) { foreach (var k in Children) Put(k, default); return finalSize; }
        double w = finalSize.Width;
        if (Kid(4) is { } line) Put(line, c.Narrow ? default : new Rect(0, 0, w, 1));
        if (c.Narrow)
        {
            Hide(Kid(0)); Hide(Kid(1)); Hide(Kid(3));
            if (_bar && Kid(2) is { } b) { Revive(b); Put(b, new Rect(0, 0, w, finalSize.Height)); } else Hide(Kid(2));
            return finalSize;
        }
        double y = 1 + PadTop;
        if (_name && Kid(0) is { } name)
        {
            Revive(name);
            double nw = Math.Min(name.DesiredSize.Width, Math.Max(0, w - (_kn ? _knW + Gap : 0)));
            Put(name, new Rect(0, y + (LineH - name.DesiredSize.Height) / 2, nw, name.DesiredSize.Height));
            if (_kn && Kid(1) is { } kn) { Revive(kn); Put(kn, new Rect(w - _knW, y + (LineH - kn.DesiredSize.Height) / 2, _knW, kn.DesiredSize.Height)); }
            else Hide(Kid(1));
            y += LineH + LineGap;
        }
        else { Hide(Kid(0)); Hide(Kid(1)); }
        if (_bar && Kid(2) is { } bar)
        {
            Revive(bar);
            double bh = bar.DesiredSize.Height;
            Put(bar, new Rect(0, y + (LineH - bh) / 2, _barW, bh));
        }
        else Hide(Kid(2));
        if (_rem && Kid(3) is { } rem)
        {
            Revive(rem);
            double x = _bar ? w - _remW : 0;
            Put(rem, new Rect(x, y + (LineH - rem.DesiredSize.Height) / 2, _remW, rem.DesiredSize.Height));
        }
        else Hide(Kid(3));
        return finalSize;
    }

    private void Hide(Avalonia.Controls.Control? c)
    {
        if (c != null) Put(c, default);
    }

    private static void Revive(Avalonia.Controls.Control c) { }

}

/// <summary>
/// 「报警与事件」的内容（样稿 .device-log-in：当前报警（最多 58 %，自己滚动）· 最近事件标题 · 事件列表（铺满剩下的，自己滚动））。
/// 子元素 [0] 当前报警、[1] 事件标题、[2] 事件列表（后两块里的滚动区由视图给）。<see cref="Short"/>（内容高 ≤ 259）：不再分两个滚动区 ——
/// 三块按自然高度竖排，由外面的滚动区整块一起滚（当前报警和它的「确认」永远在最上面完整可见）。<see cref="BodyHeight"/> = 面板内容区的高（外面的滚动区给的是无限高）。
/// </summary>
public sealed class DvLogPanel : DvPanel
{
    public const double AlarmsMaxShare = 0.58;

    public static readonly StyledProperty<bool> ShortProperty = AvaloniaProperty.Register<DvLogPanel, bool>(nameof(Short));
    public static readonly StyledProperty<double> BodyHeightProperty = AvaloniaProperty.Register<DvLogPanel, double>(nameof(BodyHeight));

    static DvLogPanel() => AffectsMeasure<DvLogPanel>(ShortProperty, BodyHeightProperty);

    public bool Short { get => GetValue(ShortProperty); set => SetValue(ShortProperty, value); }
    public double BodyHeight { get => GetValue(BodyHeightProperty); set => SetValue(BodyHeightProperty, value); }

    private double _a, _h, _e;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        var alarms = Children.Count > 0 ? Children[0] : null;
        var head = Children.Count > 1 ? Children[1] : null;
        var list = Children.Count > 2 ? Children[2] : null;
        alarms?.Measure(new Size(w, double.PositiveInfinity));
        head?.Measure(new Size(w, double.PositiveInfinity));
        list?.Measure(new Size(w, double.PositiveInfinity));
        double an = alarms?.DesiredSize.Height ?? 0, hn = head?.DesiredSize.Height ?? 0, en = list?.DesiredSize.Height ?? 0;
        if (Short || BodyHeight <= 0)
        {
            (_a, _h, _e) = (an, hn, en);
            return new Size(w, an + hn + en);
        }
        double H = BodyHeight;
        _a = Math.Min(an, AlarmsMaxShare * H);
        _h = hn;
        _e = Math.Max(0, H - _a - _h);
        alarms?.Measure(new Size(w, _a));
        list?.Measure(new Size(w, _e));
        return new Size(w, H);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        if (Children.Count > 0) { Put(Children[0], new Rect(0, y, finalSize.Width, _a)); y += _a; }
        if (Children.Count > 1) { Put(Children[1], new Rect(0, y, finalSize.Width, _h)); y += _h; }
        if (Children.Count > 2) Put(Children[2], new Rect(0, y, finalSize.Width, _e));
        return finalSize;
    }
}

/// <summary>
/// 两列网格（样稿 .device-gas：grid-template-columns: repeat(2, minmax(0, 1fr))，列间 <see cref="ColumnGap"/>）：子元素按顺序先左后右、一行两个，行高取这一行里高的那个。
/// </summary>
public sealed class DvTwoColumns : DvPanel
{
    public static readonly StyledProperty<double> ColumnGapProperty = AvaloniaProperty.Register<DvTwoColumns, double>(nameof(ColumnGap), 20);

    static DvTwoColumns() => AffectsMeasure<DvTwoColumns>(ColumnGapProperty);

    public double ColumnGap { get => GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }

    private double ColW(double w) => Math.Max(0, (w - ColumnGap) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, cw = ColW(w), h = 0;
        for (int i = 0; i < Children.Count; i += 2)
        {
            double rh = 0;
            for (int j = i; j < Math.Min(i + 2, Children.Count); j++)
            {
                Children[j].Measure(new Size(cw, double.PositiveInfinity));
                rh = Math.Max(rh, Children[j].DesiredSize.Height);
            }
            h += rh;
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cw = ColW(finalSize.Width), y = 0;
        for (int i = 0; i < Children.Count; i += 2)
        {
            double rh = 0;
            for (int j = i; j < Math.Min(i + 2, Children.Count); j++) rh = Math.Max(rh, Children[j].DesiredSize.Height);
            for (int j = i; j < Math.Min(i + 2, Children.Count); j++)
                Put(Children[j], new Rect(j == i ? 0 : cw + ColumnGap, y, cw, rh));
            y += rh;
        }
        return finalSize;
    }
}

/// <summary>
/// 表格（样稿 .pr-table，HTML 表格的自动列宽：table-layout: auto; width: 100%）：子元素是行（每个子元素的子树里有一个 <see cref="DvTableRow"/>），
/// 每列的宽 = 这一列各行（含表头）最宽的内容；表格比内容宽时多出来的宽按各列的内容宽成比例分给各列（同 WebKit 的自动表格布局），窄时同样按比例缩。
/// </summary>
public sealed class DvTable : DvPanel
{
    private static DvTableRow? RowOf(Avalonia.Controls.Control c) =>
        c as DvTableRow ?? System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<DvTableRow>(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(c)));

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 352 : availableSize.Width;
        var mc = new double[DvTableRow.Columns];
        var rows = new List<DvTableRow>();
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (RowOf(c) is not { } r) continue;
            rows.Add(r);
            for (int i = 0; i < mc.Length; i++) mc[i] = Math.Max(mc[i], r.Natural(i));
        }
        double sum = 0;
        foreach (var x in mc) sum += x;
        var widths = new double[mc.Length];
        for (int i = 0; i < mc.Length; i++) widths[i] = sum > 0 ? mc[i] * w / sum : w / mc.Length;
        foreach (var r in rows) r.SetWidths(widths);
        double h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(w, double.PositiveInfinity));
            h += c.DesiredSize.Height;
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var c in Children)
        {
            Put(c, new Rect(0, y, finalSize.Width, c.DesiredSize.Height));
            y += c.DesiredSize.Height;
        }
        return finalSize;
    }
}

/// <summary>
/// 表格的一行：四个单元格（子元素，单元格的内边距写在它们的 Margin 上），列宽由 <see cref="DvTable"/> 给（<see cref="SetWidths"/>）；
/// 单元格按自己的 HorizontalAlignment 在列里靠左 / 靠右，纵向居中。
/// </summary>
public sealed class DvTableRow : DvPanel
{
    public const int Columns = 4;

    private double[]? _widths;

    /// <summary>第 i 列单元格的自然宽度（含外边距）。</summary>
    public double Natural(int i) => i < Children.Count ? Children[i].DesiredSize.Width : 0;

    public void SetWidths(double[] widths)
    {
        if (_widths != null && System.Linq.Enumerable.SequenceEqual(_widths, widths)) return;
        _widths = (double[])widths.Clone();
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = 0, w = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            double cw = _widths != null && i < _widths.Length && !double.IsInfinity(availableSize.Width) ? _widths[i] : double.PositiveInfinity;
            c.Measure(new Size(double.IsInfinity(cw) ? double.PositiveInfinity : cw, double.PositiveInfinity));
            h = Math.Max(h, c.DesiredSize.Height);
            w += c.DesiredSize.Width;
        }
        // 在表格量自然宽度时（无限宽）要的是各单元格的自然宽度之和；之后按表格的宽
        return new Size(double.IsInfinity(availableSize.Width) ? w : availableSize.Width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            double cw = _widths != null && i < _widths.Length ? _widths[i] : c.DesiredSize.Width;
            double dw = Math.Min(c.DesiredSize.Width, cw);
            double cx = c.HorizontalAlignment == HorizontalAlignment.Right ? x + cw - dw : x;
            Put(c, new Rect(cx, (finalSize.Height - c.DesiredSize.Height) / 2, dw, c.DesiredSize.Height));
            x += cw;
        }
        return finalSize;
    }
}
