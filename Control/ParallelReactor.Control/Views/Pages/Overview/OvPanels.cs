using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Layout;

namespace ParallelReactor.Control.Views.Pages.Overview;

// ============================================================================
// 总览的几个布局面板（PORT-PLAN §4.8：没有容器查询 → 布局计算器 + 放得下才显示）。全部只看父元素给的尺寸（R4，不会来回振荡）；
// 整个让出去的子元素排成 0 × 0（审计跳过、不画），再出现时让里面的文字重新排（Controls/TextRelayout，11.1 的缺陷）。
// ============================================================================

/// <summary>
/// 一个设备分区的卡片网格（样稿 .overview-grid，CSS grid）：子元素按顺序放进 Core 算好的块（<see cref="Section"/>.Tiles：位置、跨列、尺寸）——
/// 紧凑卡固定 8 列、未安装段跨它自己的列数；其余每张卡一列、未安装合并成 96 px 窄列。高度 = 网格高（上下内边距 8 + 行 + 行距 10）。
/// </summary>
public sealed class OvGridPanel : Panel
{
    public static readonly StyledProperty<OverviewSection?> SectionProperty =
        AvaloniaProperty.Register<OvGridPanel, OverviewSection?>(nameof(Section));

    static OvGridPanel() => AffectsMeasure<OvGridPanel>(SectionProperty);

    public OverviewSection? Section { get => GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Section;
        var tiles = s?.Tiles;
        double right = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (tiles != null && i < tiles.Count)
            {
                var t = tiles[i];
                c.Measure(new Size(t.Width, t.Height));
                right = Math.Max(right, t.X + t.Width);
            }
            else c.Measure(default);
        }
        if (s == null) return default;
        double w = double.IsInfinity(availableSize.Width) ? right + OverviewLayout.GridPadX : availableSize.Width;
        return new Size(w, s.GridHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tiles = Section?.Tiles;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (tiles != null && i < tiles.Count)
            {
                var t = tiles[i];
                c.Arrange(new Rect(t.X, t.Y, t.Width, t.Height));
            }
            else c.Arrange(default);
        }
        return finalSize;
    }
}

/// <summary>
/// 读数行（样稿 .overview-r.pr-fitrow：大数字 + 小单位 + 右侧目标，按基线对齐，固定一行高）：
/// 子元素 [0] 数值（字号 / 行高由 <see cref="ValueSize"/> / <see cref="ValueLineHeight"/> 决定 —— Core 的 data-len 阶梯）、[1] 单位、[2] 目标（可选）。
/// <list type="bullet">
/// <item>单位：<see cref="ShowUnit"/> = false（阶梯的最后一档）时不画；数值 + 3 + 单位放不下时也不画（同 flex-wrap 把它换到看不见的第二行）。</item>
/// <item>目标「放得下才显示」（.pr-fitopt：margin-left auto + padding-left 8，整个放不下时换到看不见的第二行）：不省略、不挤读数；单位被挤走时目标也不画。</item>
/// <item>兜底（样稿没有，Skia 与 WebKit 的字宽不同时）：数值自己都放不下就从阶梯字号起每次缩 1 px（≥ 13），读数永远不被截断。</item>
/// </list>
/// </summary>
public sealed class OvReadRow : Panel
{
    public const double Gap = 3, TargetPad = 8;

    public static readonly StyledProperty<double> ValueSizeProperty = AvaloniaProperty.Register<OvReadRow, double>(nameof(ValueSize), 26);
    public static readonly StyledProperty<double> ValueLineHeightProperty = AvaloniaProperty.Register<OvReadRow, double>(nameof(ValueLineHeight), 32);
    public static readonly StyledProperty<bool> ShowUnitProperty = AvaloniaProperty.Register<OvReadRow, bool>(nameof(ShowUnit), true);

    static OvReadRow() => AffectsMeasure<OvReadRow>(ValueSizeProperty, ValueLineHeightProperty, ShowUnitProperty);

    public double ValueSize { get => GetValue(ValueSizeProperty); set => SetValue(ValueSizeProperty, value); }
    public double ValueLineHeight { get => GetValue(ValueLineHeightProperty); set => SetValue(ValueLineHeightProperty, value); }
    public bool ShowUnit { get => GetValue(ShowUnitProperty); set => SetValue(ShowUnitProperty, value); }

    private bool _unit, _target;
    private readonly HashSet<Layoutable> _zeroed = new();

    /// <summary>最近一次量出来的取舍（测试 / 排查用）：画不画单位、目标，数值最后的字号。</summary>
    public (bool Unit, bool Target, double Size) Chosen { get; private set; }

    private TextBlock? Value => Children.Count > 0 ? Children[0] as TextBlock : null;
    private Avalonia.Controls.Control? Unit => Children.Count > 1 ? Children[1] : null;
    private Avalonia.Controls.Control? Target => Children.Count > 2 ? Children[2] : null;

    private static bool HasText(Avalonia.Controls.Control? c) => c is { IsVisible: true } && (c is not TextBlock tb || !string.IsNullOrEmpty(tb.Text));

    protected override Size MeasureOverride(Size availableSize)
    {
        var v = Value;
        if (v == null) return default;
        double size = ValueSize;
        if (!Equals(v.LineHeight, ValueLineHeight)) v.LineHeight = ValueLineHeight;
        if (!Equals(v.FontSize, size)) v.FontSize = size;
        v.Measure(Size.Infinity);
        double w = availableSize.Width;
        bool finite = !double.IsInfinity(w);
        // 兜底：数值自己放不下 → 缩字号（≥ 13），行高不变
        while (finite && v.DesiredSize.Width > w + 0.01 && size > FitText.MinSize)
        {
            size = Math.Max(FitText.MinSize, size - 1);
            v.FontSize = size;
            v.Measure(Size.Infinity);
        }
        double vw = v.DesiredSize.Width;
        var unit = Unit;
        var target = Target;
        bool unitWanted = ShowUnit && size >= ValueSize && HasText(unit);
        unit?.Measure(Size.Infinity);
        target?.Measure(Size.Infinity);
        double uw = unit?.DesiredSize.Width ?? 0, tw = target?.DesiredSize.Width ?? 0;
        (_unit, _target) = OvFit.Read(w, vw, uw, tw, unitWanted, HasText(target));
        double used = vw + (_unit ? Gap + uw : 0);
        Chosen = (_unit, _target, size);
        double h = double.IsInfinity(availableSize.Height) ? ValueLineHeight : availableSize.Height;
        return new Size(finite ? Math.Min(w, used + (_target ? Gap + TargetPad + tw : 0)) : used + (_target ? Gap + TargetPad + tw : 0), Math.Max(h, ValueLineHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var v = Value;
        if (v == null) return finalSize;
        double vb = Baseline(v);
        double vh = v.DesiredSize.Height;
        v.Arrange(new Rect(0, 0, v.DesiredSize.Width, vh));
        double x = v.DesiredSize.Width;
        Place(Unit, _unit, c => new Rect(x + Gap, vb - Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        Place(Target, _target, c => new Rect(finalSize.Width - c.DesiredSize.Width, vb - Baseline(c), c.DesiredSize.Width, c.DesiredSize.Height));
        for (int i = 3; i < Children.Count; i++) Children[i].Arrange(default);
        return finalSize;
    }

    private void Place(Avalonia.Controls.Control? c, bool shown, Func<Avalonia.Controls.Control, Rect> rect)
    {
        if (c == null) return;
        if (!shown) { c.Arrange(default); _zeroed.Add(c); return; }
        if (_zeroed.Remove(c)) TextRelayout.Revive(c);
        c.Arrange(rect(c));
    }

    /// <summary>子元素顶边到第一行基线的距离（TextBlock：第一行的基线；其它：底边）。</summary>
    internal static double Baseline(Layoutable c)
    {
        if (c is TextBlock tb && tb.TextLayout.TextLines is { Count: > 0 } lines) return tb.Padding.Top + lines[0].Baseline;
        return c.DesiredSize.Height;
    }
}

/// <summary>
/// 卡片底部的程序行（样稿 .overview-prog：程序名 · 分段进度条 · 剩 x min）：
/// 子元素 [0] 程序名、[1] 步骤条、[2] 短文字、[3] 「无程序」/「—」。
/// <list type="bullet">
/// <item>有程序（<see cref="HasProgram"/>）：程序名最多占 42 %（<see cref="ShowName"/> = false 时不画：窄卡、紧凑卡）、步骤条拿剩下的（至少 24 px）、
///   短文字按自身宽度靠右；步骤条每段会窄于 6 px 时短文字整个不画、步骤条占满（ui.fitStepBars 的 is-barfit，这一行的提示里有完整文字）。</item>
/// <item>换行（<see cref="Wrap"/>，紧凑卡 ≤ 104 px）：步骤条一整行，下面 4 px 是短文字（靠左）。</item>
/// <item>没有程序：只写「无程序」（t-4，不限宽）。</item>
/// </list>
/// 一行时高度取父元素给的（20 / 紧凑 18），内容纵向居中。
/// </summary>
public sealed class OvProgRow : Panel
{
    public const double BarMin = 24, NameMax = 0.42, RowGap = 4;

    public static readonly StyledProperty<bool> HasProgramProperty = AvaloniaProperty.Register<OvProgRow, bool>(nameof(HasProgram));
    public static readonly StyledProperty<bool> ShowNameProperty = AvaloniaProperty.Register<OvProgRow, bool>(nameof(ShowName), true);
    public static readonly StyledProperty<bool> WrapProperty = AvaloniaProperty.Register<OvProgRow, bool>(nameof(Wrap));
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<OvProgRow, double>(nameof(Spacing), 10);
    public static readonly StyledProperty<double> RowHeightProperty = AvaloniaProperty.Register<OvProgRow, double>(nameof(RowHeight), 20);

    static OvProgRow() => AffectsMeasure<OvProgRow>(HasProgramProperty, ShowNameProperty, WrapProperty, SpacingProperty, RowHeightProperty);

    public bool HasProgram { get => GetValue(HasProgramProperty); set => SetValue(HasProgramProperty, value); }
    public bool ShowName { get => GetValue(ShowNameProperty); set => SetValue(ShowNameProperty, value); }
    public bool Wrap { get => GetValue(WrapProperty); set => SetValue(WrapProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    /// <summary>短文字放不下、已让位（步骤条占满）。</summary>
    public bool IsBarFit { get; private set; }

    private double _nameW, _barW, _textW;
    private bool _name, _text;
    private readonly HashSet<Layoutable> _zeroed = new();

    private Avalonia.Controls.Control? Kid(int i) => i < Children.Count ? Children[i] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var name = Kid(0);
        var bar = Kid(1);
        var text = Kid(2);
        var none = Kid(3);
        foreach (var c in Children) c.Measure(Size.Infinity);
        double W = availableSize.Width, gap = Spacing;
        IsBarFit = false;
        if (!HasProgram || bar == null)
        {
            _name = _text = false;
            double nh = none?.DesiredSize.Height ?? 0;
            return new Size(double.IsInfinity(W) ? none?.DesiredSize.Width ?? 0 : W, Math.Max(RowHeight, nh));
        }
        int n = bar is StepBar sb ? sb.Segments.Count : 0;
        _textW = text?.DesiredSize.Width ?? 0;
        bool hasText = text is TextBlock tt && !string.IsNullOrEmpty(tt.Text);
        if (double.IsInfinity(W))
        {
            _name = ShowName && !Wrap;
            _text = hasText;
            return new Size((_name ? name!.DesiredSize.Width + gap : 0) + BarMin + (_text ? gap + _textW : 0), RowHeight);
        }
        if (Wrap)
        {
            // 两行：步骤条一整行；短文字（放得下才写）在下面一行
            _name = false;
            _barW = W;
            IsBarFit = n > 1 && W < StepBar.MinBarWidth(n);
            _text = hasText && !IsBarFit;
            if (_text && text != null) text.Measure(new Size(W, double.PositiveInfinity));
            double th = _text ? text!.DesiredSize.Height : 0;
            bar.Measure(new Size(W, double.PositiveInfinity));
            return new Size(W, StepBar.TrackHeight + (_text ? RowGap + Math.Max(RowHeight, th) : 0));
        }
        _name = ShowName && name != null;
        var fit = OvFit.ProgRow(W, _name ? name!.DesiredSize.Width : 0, _textW, n, _name, hasText, gap);
        IsBarFit = fit.BarFit;
        _text = fit.Text;
        _nameW = fit.NameW;
        _barW = fit.BarW;
        if (_name) name!.Measure(new Size(_nameW, double.PositiveInfinity));
        bar.Measure(new Size(_barW, double.PositiveInfinity));
        return new Size(W, RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var name = Kid(0);
        var bar = Kid(1);
        var text = Kid(2);
        var none = Kid(3);
        double H = finalSize.Height, gap = Spacing;
        if (!HasProgram || bar == null)
        {
            Hide(name); Hide(bar); Hide(text);
            if (none != null)
            {
                Show(none);
                // 换行的紧凑卡（程序行 34 高）里「无程序」贴着底边（样稿 margin-top: auto，行本身 20 高）
                double nh = Math.Min(none.DesiredSize.Height, H);
                double y = Wrap ? H - Math.Max(RowHeight, nh) + (Math.Max(RowHeight, nh) - nh) / 2 : (H - nh) / 2;
                none.Arrange(new Rect(0, y, Math.Min(none.DesiredSize.Width, finalSize.Width), nh));
            }
            return finalSize;
        }
        Hide(none);
        if (Wrap)
        {
            Hide(name);
            Show(bar);
            bar.Arrange(new Rect(0, 0, finalSize.Width, StepBar.TrackHeight));
            if (_text && text != null)
            {
                Show(text);
                double rowTop = StepBar.TrackHeight + RowGap;
                double rh = Math.Max(RowHeight, text.DesiredSize.Height);
                text.Arrange(new Rect(0, rowTop + (rh - text.DesiredSize.Height) / 2, Math.Min(text.DesiredSize.Width, finalSize.Width), text.DesiredSize.Height));
            }
            else Hide(text);
            return finalSize;
        }
        double x = 0;
        if (_name && name != null)
        {
            Show(name);
            name.Arrange(new Rect(0, (H - name.DesiredSize.Height) / 2, _nameW, name.DesiredSize.Height));
            x = _nameW + gap;
        }
        else Hide(name);
        Show(bar);
        double bh = bar.DesiredSize.Height;
        bar.Arrange(new Rect(x, (H - bh) / 2, _barW, bh));
        if (_text && text != null)
        {
            Show(text);
            text.Arrange(new Rect(finalSize.Width - _textW, (H - text.DesiredSize.Height) / 2, _textW, text.DesiredSize.Height));
        }
        else Hide(text);
        return finalSize;
    }

    private void Hide(Avalonia.Controls.Control? c)
    {
        if (c == null) return;
        c.Arrange(default);
        _zeroed.Add(c);
    }

    private void Show(Avalonia.Controls.Control c)
    {
        if (_zeroed.Remove(c)) TextRelayout.Revive(c);
    }
}

/// <summary>
/// 卡片标题行（样稿 .overview-chead）：RV 靠左、状态（点 + 字）靠右，纵向居中；<see cref="Wrap"/>（紧凑卡 ≤ 117 px）时状态换到第二行（靠左）。
/// 子元素 [0] RV、[1] 状态。一行时高度取父元素给的。
/// </summary>
public sealed class OvCardHead : Panel
{
    public static readonly StyledProperty<bool> WrapProperty = AvaloniaProperty.Register<OvCardHead, bool>(nameof(Wrap));
    public static readonly StyledProperty<double> LineProperty = AvaloniaProperty.Register<OvCardHead, double>(nameof(Line), 22);

    static OvCardHead() => AffectsMeasure<OvCardHead>(WrapProperty, LineProperty);

    public bool Wrap { get => GetValue(WrapProperty); set => SetValue(WrapProperty, value); }
    /// <summary>一行的高度（22；紧凑卡 20）。</summary>
    public double Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(w, Wrap ? 2 * Line : Line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double L = Line;
        if (Children.Count > 0)
        {
            var rv = Children[0];
            rv.Arrange(new Rect(0, (L - rv.DesiredSize.Height) / 2, rv.DesiredSize.Width, rv.DesiredSize.Height));
        }
        if (Children.Count > 1)
        {
            var st = Children[1];
            double sw = st.DesiredSize.Width, sh = st.DesiredSize.Height;
            if (Wrap) st.Arrange(new Rect(0, L + (L - sh) / 2, sw, sh));
            else st.Arrange(new Rect(Math.Max(0, finalSize.Width - sw), (L - sh) / 2, sw, sh));
        }
        return finalSize;
    }
}

/// <summary>
/// 分区头的「别名 + 型号」（样稿 .overview-name：baseline 对齐、间距 8；overview.js fitHeads）：放得下时两个都写；放不下时型号<b>整个</b>不写（不再挤成「H」），
/// 别名自己还放不下才省略（子元素 [0] 带 CharacterEllipsis，提示在面板上：别名 · 型号 · SN · 地址）。子元素 [0] 别名、[1] 型号。
/// </summary>
public sealed class OvNamePanel : Panel
{
    public const double Gap = 8;
    private bool _model;
    private bool _modelZeroed;
    private bool _modelAllowed = true;

    /// <summary>型号写出来了（测试 / 排查用）。</summary>
    public bool ModelShown => _model;

    /// <summary>
    /// 分区头这一行放得下（<see cref="OvHeadRow"/> 量过）：false 时型号整个不写，自然宽度只算别名 —— 后面的徽标紧跟别名，不留出型号的空（样稿 fitHeads 加 is-nomodel 后重新排版）。
    /// </summary>
    public bool ModelAllowed
    {
        get => _modelAllowed;
        set { if (_modelAllowed == value) return; _modelAllowed = value; InvalidateMeasure(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return default;
        var alias = Children[0];
        var model = Children.Count > 1 ? Children[1] : null;
        alias.Measure(Size.Infinity);
        model?.Measure(Size.Infinity);
        double aw = alias.DesiredSize.Width, mw = model is { IsVisible: true } ? model.DesiredSize.Width : 0;
        double W = availableSize.Width;
        _model = ModelAllowed && model is { IsVisible: true } && OvFit.ModelFits(W, aw, mw);
        if (!_model && !double.IsInfinity(W) && aw > W) alias.Measure(new Size(W, double.PositiveInfinity));
        double used = alias.DesiredSize.Width + (_model ? Gap + mw : 0);
        double above = OvReadRow.Baseline(alias), below = alias.DesiredSize.Height - above;
        if (_model && model != null)
        {
            double mb = OvReadRow.Baseline(model);
            above = Math.Max(above, mb);
            below = Math.Max(below, model.DesiredSize.Height - mb);
        }
        return new Size(double.IsInfinity(W) ? used : Math.Min(W, used), above + below);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        var alias = Children[0];
        var model = Children.Count > 1 ? Children[1] : null;
        double ab = OvReadRow.Baseline(alias);
        double above = ab;
        if (_model && model != null) above = Math.Max(above, OvReadRow.Baseline(model));
        double aw = Math.Min(alias.DesiredSize.Width, finalSize.Width);
        alias.Arrange(new Rect(0, above - ab, aw, alias.DesiredSize.Height));
        if (model != null)
        {
            if (_model)
            {
                if (_modelZeroed) { _modelZeroed = false; TextRelayout.Revive(model); }
                model.Arrange(new Rect(aw + Gap, above - OvReadRow.Baseline(model), model.DesiredSize.Width, model.DesiredSize.Height));
            }
            else { model.Arrange(default); _modelZeroed = true; }
        }
        return finalSize;
    }
}

/// <summary>
/// 分区头的标题行（样稿 .overview-dev .pr-panel-title：flex、间距 12，只有「别名 + 型号」可以收缩；overview.js fitHeads）：
/// 先按自然宽度量一遍，整行放不下时让 <see cref="OvNamePanel"/> 不写型号（它的自然宽度变成只有别名），再按 FlexRow 的规则排 ——
/// 徽标紧跟别名；还放不下时别名省略。只看父元素给的宽度（R4）。
/// </summary>
public sealed class OvHeadRow : FlexRow
{
    protected override Type StyleKeyOverride => typeof(FlexRow);

    protected override Size MeasureOverride(Size availableSize)
    {
        OvNamePanel? name = null;
        foreach (var c in Children) if (c is OvNamePanel np) { name = np; break; }
        if (name != null)
        {
            name.ModelAllowed = true;
            if (!double.IsInfinity(availableSize.Width))
            {
                double total = 0;
                int n = 0;
                foreach (var c in Children)
                {
                    if (!c.IsVisible) continue;
                    c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    total += c.DesiredSize.Width;
                    n++;
                }
                total += Math.Max(0, n - 1) * Spacing;
                name.ModelAllowed = OvFit.HeadFits(availableSize.Width, total);
            }
        }
        return base.MeasureOverride(availableSize);
    }
}

/// <summary>
/// 总览几处「放得下才显示」的取舍（纯函数，只看父元素给的宽度和各段的自然宽度；面板与测试共用）。
/// </summary>
public static class OvFit
{
    /// <summary>
    /// 读数行（.overview-r.pr-fitrow）：单位跟在数值后面（间距 3），放不下就换到看不见的第二行；目标在右边（间距 3 + 内边距 8），整个放不下就不画；
    /// 单位被挤走时目标也被挤走（同一行 flex-wrap，它在单位后面）。<paramref name="unitWanted"/> = 阶梯这一档带单位；<paramref name="hasTarget"/> = 有目标文字。
    /// </summary>
    public static (bool Unit, bool Target) Read(double width, double valueW, double unitW, double targetW, bool unitWanted, bool hasTarget)
    {
        bool finite = !double.IsInfinity(width);
        bool unitFits = !finite || valueW + OvReadRow.Gap + unitW <= width + 0.01;
        bool unit = unitWanted && unitFits;
        double used = valueW + (unit ? OvReadRow.Gap + unitW : 0);
        bool target = hasTarget && !(unitWanted && !unitFits) && (!finite || used + OvReadRow.Gap + OvReadRow.TargetPad + targetW <= width + 0.01);
        return (unit, target);
    }

    /// <summary>程序行的分配结果：程序名宽、步骤条宽、画不画程序名 / 短文字、短文字是否为步骤条让位（is-barfit）。</summary>
    public readonly record struct ProgFit(double NameW, double BarW, bool Name, bool Text, bool BarFit);

    /// <summary>
    /// 程序行（.overview-prog，一行时）：程序名最多占 42 %（flex 0 1 auto），步骤条拿剩下的（flex 1 1 0，至少 24 px，不够时先压程序名），短文字按自然宽度靠右；
    /// 步骤条会窄于每段 6 px 的最小宽度（<see cref="StepBar.MinBarWidth"/>）时短文字整个不画，重新分配（ui.fitStepBars）。
    /// </summary>
    public static ProgFit ProgRow(double width, double nameNatural, double textW, int segments, bool showName, bool hasText, double gap)
    {
        (double, double) Split(bool withText)
        {
            int items = 1 + (showName ? 1 : 0) + (withText ? 1 : 0);
            double gaps = (items - 1) * gap;
            double nw = showName ? Math.Min(nameNatural, OvProgRow.NameMax * width) : 0;
            double bw = width - nw - (withText ? textW : 0) - gaps;
            if (bw < OvProgRow.BarMin && nw > 0) { nw = Math.Max(0, nw - (OvProgRow.BarMin - bw)); bw = width - nw - (withText ? textW : 0) - gaps; }
            return (nw, Math.Max(0, bw));
        }
        var (n0, b0) = Split(hasText);
        if (hasText && segments > 1 && b0 < StepBar.MinBarWidth(segments))
        {
            var (n1, b1) = Split(false);
            return new ProgFit(n1, b1, showName, false, true);
        }
        return new ProgFit(n0, b0, showName, hasText, false);
    }

    /// <summary>分区头这一行（各项自然宽度 + 间距的总和）放得下；放不下时型号整个不写（fitHeads 的 is-nomodel：别名被挤了就去掉型号）。</summary>
    public static bool HeadFits(double width, double naturalTotal) => double.IsInfinity(width) || naturalTotal <= width + 0.01;

    /// <summary>分区头的型号放得下才写（overview.js fitHeads：别名 + 8 + 型号放不下时型号整个不写，别名最后才省略）。</summary>
    public static bool ModelFits(double width, double aliasW, double modelW) =>
        double.IsInfinity(width) || aliasW + OvNamePanel.Gap + modelW <= width + 0.01;
}
