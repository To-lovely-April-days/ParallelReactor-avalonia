using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 自适应读数（PORT-PLAN §4.8 第 3 层，代替样稿几十条「按位数缩字号」的容器查询）：从 <see cref="Sizes"/>（如「26,24,22,20」）里
/// 挑最大的、能把「数值 + 空格 + 单位」放进父元素给的宽度的字号；最小字号也放不下时去掉单位，再放不下就继续缩小数值的字号
/// （每次 1 px，最小 <see cref="MinSize"/> = 13）；13 px 也放不下时只显示「…」—— <b>读数永远不被截成半个数</b>
/// （M1 复核：以前会显示「11…」，看起来像另一个数）。去掉单位、缩到候选字号以下或显示「…」时，悬停提示给出全文。
/// 判断只看父元素给的宽度（R4：不会来回振荡）；测宽按（文字, 字号）缓存，读数每秒变化时不重复排版；提示在排列阶段写（不在测量里改属性）。
/// 数字等宽请加类 num（Styles/Typography.axaml 对 TemplatedControl 生效，模板里的 TextBlock 继承 FontFeatures）。
/// </summary>
public sealed class FitText : TemplatedControl
{
    /// <summary>读数最小字号（RESTYLE-BRIEF §3：任何文字 ≥ 13 px）。</summary>
    public const double MinSize = 13;

    /// <summary>放不下任何字号时显示的占位（不是数字，不会被看成别的读数）。</summary>
    public const string Overflow = "…";

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<FitText, string?>(nameof(Text));
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<FitText, string?>(nameof(Unit));
    public static readonly StyledProperty<string> SizesProperty = AvaloniaProperty.Register<FitText, string>(nameof(Sizes), "26,24,22,20");
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty = AvaloniaProperty.Register<FitText, TextAlignment>(nameof(TextAlignment));

    static FitText() => AffectsMeasure<FitText>(TextProperty, UnitProperty, SizesProperty);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>单位（放不下时先去掉它）。</summary>
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>候选字号，逗号分隔，从大到小。</summary>
    public string Sizes { get => GetValue(SizesProperty); set => SetValue(SizesProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    /// <summary>最近一次选中的字号 / 是否带单位 / 是否只显示了「…」（测试、审计用）。</summary>
    public double ChosenSize { get; private set; }
    public bool ChosenWithUnit { get; private set; }
    public bool ChosenOverflow { get; private set; }

    private TextBlock? _part;
    private object? _tip;
    private bool _tipDirty;

    // 测宽缓存：键 = （文字, 字号）；字体相关属性变了整个清掉，条目太多时也清（读数每秒变化，旧数值不再需要）
    private readonly Dictionary<(string, double), double> _widths = new();
    private (Typeface Face, FontFeatureCollection? Features) _cacheFace;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _part = e.NameScope.Find<TextBlock>("PART_Text");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontWeightProperty || change.Property == FontStyleProperty
            || change.Property == FontStretchProperty || change.Property == FontFeaturesProperty)
        {
            _widths.Clear();
            InvalidateMeasure();
        }
    }

    private double[] ParsedSizes()
    {
        var list = new List<double>();
        foreach (var s in (Sizes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) list.Add(v);
        if (list.Count == 0) list.Add(FontSize);
        return list.OrderByDescending(v => v).ToArray();
    }

    private double MeasureWidth(string text, double size)
    {
        var face = (new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontFeatures);
        if (!Equals(face.Item1, _cacheFace.Face) || !ReferenceEquals(face.FontFeatures, _cacheFace.Features)) { _widths.Clear(); _cacheFace = face; }
        if (_widths.TryGetValue((text, size), out var w)) return w;
        if (_widths.Count > 64) _widths.Clear();
        using var layout = new TextLayout(text, face.Item1, face.FontFeatures, size, null);
        w = layout.WidthIncludingTrailingWhitespace;
        _widths[(text, size)] = w;
        return w;
    }

    /// <summary>
    /// 选字号：① 候选字号从大到小，带单位放得下就用；② 都放不下：去掉单位，用最小候选字号；③ 还放不下：数值从最小候选字号起每次缩 1 px，
    /// 直到 13 px；④ 13 px 也放不下：「…」（13 px）。返回（字号, 显示的文字, 是否完整显示了全文）。
    /// </summary>
    public (double Size, string Shown, bool Complete) Choose(double avail)
    {
        var value = Text ?? "";
        var full = string.IsNullOrEmpty(Unit) ? value : value + " " + Unit;
        var sizes = ParsedSizes();
        if (double.IsInfinity(avail) || double.IsNaN(avail)) return (sizes[0], full, true);
        bool Fits(string s, double size) => MeasureWidth(s, size) <= avail + 0.01;
        foreach (var size in sizes)
            if (Fits(full, size)) return (size, full, true);
        var smallest = sizes[^1];
        if (value.Length == 0) return (smallest, value, true);
        for (var size = smallest; size >= MinSize - 0.001; size = size > MinSize && size - 1 < MinSize ? MinSize : size - 1)
        {
            if (Fits(value, size)) return (size, value, false);
            if (size <= MinSize) break;
        }
        return (Math.Min(smallest, MinSize), Overflow, false);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_part != null)
        {
            var avail = availableSize.Width - Padding.Left - Padding.Right;
            var (size, shown, complete) = Choose(avail);
            if (!Equals(_part.FontSize, size)) _part.FontSize = size;
            if (_part.Text != shown) _part.Text = shown;
            var value = Text ?? "";
            var full = string.IsNullOrEmpty(Unit) ? value : value + " " + Unit;
            object? tip = complete ? null : full;
            if (!Equals(tip, _tip)) { _tip = tip; _tipDirty = true; }
            ChosenSize = size;
            ChosenWithUnit = complete && !string.IsNullOrEmpty(Unit);
            ChosenOverflow = shown == Overflow && value != Overflow;
        }
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 提示在排列阶段写（测量阶段只算），没变就不写
        if (_tipDirty) { _tipDirty = false; ToolTip.SetTip(this, _tip); }
        return base.ArrangeOverride(finalSize);
    }
}
