using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Vessel;

// ============================================================================
// 反应釜页「釜体」页签的舞台（样稿 js/pages/reactor.js stageHTML / coHTML + css/reactor.css 的 .reactor-stage / .reactor-co* / .reactor-lead*）：
// 大号线描釜体（ReactorVessel large，类 audit-fig）+ 细灰引线 + 六个中性标注框（类 audit-box）。版面全部来自 StageLayout（只看父元素给的尺寸，R4）；
// 标注框里的文字是真的 TextBlock（PrText：WebKit 式量宽），截图审计照常查字号、省略、溢出、重叠；ShotAudit 另查「釜体被标注框 / 控件 / 文字盖住」。
// ============================================================================

/// <summary>
/// 舞台：釜体 + 引线 + 六个标注框，按自己的尺寸（= 面板内容区，父元素给的）用 <see cref="StageLayout.Compute"/> 排版；尺寸或任何一个标注的内容变了就重算。
/// 釜体的样子（状态、加热、冷却、搅拌、液位）转给里面的 <see cref="ReactorVessel"/>（large：线宽随缩放、角标 14 px、超压时压力表红圈；TT / PT 由标注框写，釜内不写）。
/// 面板太小（宽或高 &lt; 120）时什么都不画（样稿 stageHTML）。读数字宽按 UiFont 量（<see cref="StageTextMeasure"/>：数字按「0」、+tnum，按（文字, 字号, 字重）缓存）。
/// </summary>
public sealed class VesselStage : Panel
{
    public static readonly StyledProperty<CalloutContent?> SvProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(Sv));
    public static readonly StyledProperty<CalloutContent?> HtProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(Ht));
    public static readonly StyledProperty<CalloutContent?> TtProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(Tt));
    public static readonly StyledProperty<CalloutContent?> MProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(M));
    public static readonly StyledProperty<CalloutContent?> PtProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(Pt));
    public static readonly StyledProperty<CalloutContent?> LvProperty = AvaloniaProperty.Register<VesselStage, CalloutContent?>(nameof(Lv));
    public static readonly StyledProperty<StateKey> KeyProperty = AvaloniaProperty.Register<VesselStage, StateKey>(nameof(Key), StateKey.Idle);
    public static readonly StyledProperty<bool> HeatingProperty = AvaloniaProperty.Register<VesselStage, bool>(nameof(Heating));
    public static readonly StyledProperty<bool> CoolingProperty = AvaloniaProperty.Register<VesselStage, bool>(nameof(Cooling));
    public static readonly StyledProperty<bool> StirringProperty = AvaloniaProperty.Register<VesselStage, bool>(nameof(Stirring));
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<VesselStage, double>(nameof(Level), double.NaN);
    /// <summary>设备断线：读数停在断线前（标注的读数 t-3）。</summary>
    public static readonly StyledProperty<bool> IsStaleProperty = AvaloniaProperty.Register<VesselStage, bool>(nameof(IsStale));

    static VesselStage()
    {
        AffectsMeasure<VesselStage>(SvProperty, HtProperty, TtProperty, MProperty, PtProperty, LvProperty, LevelProperty, IsStaleProperty);
    }

    public CalloutContent? Sv { get => GetValue(SvProperty); set => SetValue(SvProperty, value); }
    public CalloutContent? Ht { get => GetValue(HtProperty); set => SetValue(HtProperty, value); }
    public CalloutContent? Tt { get => GetValue(TtProperty); set => SetValue(TtProperty, value); }
    public CalloutContent? M { get => GetValue(MProperty); set => SetValue(MProperty, value); }
    public CalloutContent? Pt { get => GetValue(PtProperty); set => SetValue(PtProperty, value); }
    public CalloutContent? Lv { get => GetValue(LvProperty); set => SetValue(LvProperty, value); }
    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public bool Heating { get => GetValue(HeatingProperty); set => SetValue(HeatingProperty, value); }
    public bool Cooling { get => GetValue(CoolingProperty); set => SetValue(CoolingProperty, value); }
    public bool Stirring { get => GetValue(StirringProperty); set => SetValue(StirringProperty, value); }
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsStale { get => GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }

    private readonly ReactorVessel _vessel;
    private readonly VesselLeads _leads;
    private readonly VesselCalloutBox[] _boxes = new VesselCalloutBox[6];
    private readonly StageTextMeasure _measure = new();
    private readonly HashSet<Layoutable> _zeroed = new();
    private StageResult _result = StageResult.None;
    private (double W, double H, FontFamily? Family)? _for;
    private bool _dirty = true;

    public VesselStage()
    {
        // 釜体（data-ov-fig）：不能被标注框、控件或文字盖住（ShotAudit 的 figure × box）；TT / PT 由标注框写（样稿 .reactor-vbox .rv-tt / .rv-pt）
        _vessel = new ReactorVessel { Size = VesselSize.Large, ShowTagLabels = false };
        _vessel.Classes.Add("audit-fig");
        _leads = new VesselLeads { IsHitTestVisible = false };
        Children.Add(_vessel);
        Children.Add(_leads);
        foreach (VesselCallout k in Enum.GetValues(typeof(VesselCallout)))
        {
            var box = new VesselCalloutBox(k);
            _boxes[(int)k] = box;
            Children.Add(box);
        }
    }

    /// <summary>此刻的版面（测试 / 核对用）。</summary>
    public StageResult Result => _result;

    /// <summary>里面的釜体（核对用）。</summary>
    public ReactorVessel Vessel => _vessel;

    /// <summary>某个标注框（核对用）。</summary>
    public VesselCalloutBox Box(VesselCallout key) => _boxes[(int)key];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == KeyProperty) _vessel.Key = Key;
        else if (p == HeatingProperty) _vessel.Heating = Heating;
        else if (p == CoolingProperty) _vessel.Cooling = Cooling;
        else if (p == StirringProperty) _vessel.Stirring = Stirring;
        else if (p == LevelProperty) { _vessel.Level = Level; _dirty = true; }
        else if (p == SvProperty || p == HtProperty || p == TtProperty || p == MProperty || p == PtProperty || p == LvProperty || p == IsStaleProperty
                 || p == TextElement.FontFamilyProperty) _dirty = true;
    }

    private CalloutContent Content(VesselCallout k) => k switch
    {
        VesselCallout.Sv => Sv,
        VesselCallout.Ht => Ht,
        VesselCallout.Tt => Tt,
        VesselCallout.M => M,
        VesselCallout.Pt => Pt,
        _ => Lv,
    } ?? Placeholder(k);

    private static CalloutContent Placeholder(VesselCallout k) => new(k.ToString().ToUpperInvariant(), "", Core.Format.Fmt.Dash, "", Core.Format.Fmt.Dash);

    /// <summary>按这个尺寸排版（尺寸、字体、内容没变就沿用上一次的结果）。</summary>
    private void Layout(double w, double h)
    {
        var family = TextElement.GetFontFamily(this);
        if (!_dirty && _for is { } f && f.W == w && f.H == h && ReferenceEquals(f.Family, family)) return;
        _dirty = false;
        _for = (w, h, family);
        var v = new Dictionary<VesselCallout, CalloutContent>(6);
        foreach (VesselCallout k in Enum.GetValues(typeof(VesselCallout))) v[k] = Content(k);
        double levelY = VesselAnchors.LevelY(Level);
        _result = StageLayout.Compute(w, h, v, levelY, (text, size, bold) => _measure.Width(family, text, size, bold));
        _leads.Result = _result;
        foreach (var box in _boxes)
        {
            var c = v[box.Key];
            var sb = _result.Box(box.Key);
            box.Apply(c, _result.Mode, StageLayout.SideOf(box.Key), c.Main ? _result.FsMain : _result.FsSub, IsStale);
            box.Spec = sb;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        Layout(w, h);
        MeasureChildren(w, h);
        return new Size(w, h);
    }

    private void MeasureChildren(double w, double h)
    {
        var r = _result;
        _vessel.Measure(r.Empty ? default : new Size(r.Vw, r.Vh));
        _leads.Measure(new Size(w, h));
        foreach (var box in _boxes)
            box.Measure(box.Spec is { } s && !r.Empty ? new Size(s.Width, s.Height) : default);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_for is not { } f || f.W != finalSize.Width || f.H != finalSize.Height || _dirty)
        {
            Layout(finalSize.Width, finalSize.Height);
            MeasureChildren(finalSize.Width, finalSize.Height);
        }
        var r = _result;
        Put(_vessel, r.Empty ? default : new Rect(r.Vx, r.Vy, r.Vw, r.Vh));
        Put(_leads, r.Empty ? default : new Rect(finalSize));
        foreach (var box in _boxes)
            Put(box, !r.Empty && box.Spec is { } s ? new Rect(s.Left, s.Top, s.Width, s.Height) : default);
        return finalSize;
    }

    /// <summary>排一个子元素：空矩形排成 0 × 0（不画、审计跳过）；从 0 × 0 回来时先让里面的文字重新排（Controls/TextRelayout）。</summary>
    private void Put(Layoutable c, Rect r)
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
}

/// <summary>
/// 量字宽（<see cref="StageLayout"/> 的 <see cref="TextWidth"/>，样稿 ui.textWidth 的 App 版）：UiFont、+tnum（读数等宽），字重 400 / 600；
/// 带下标数字的气体名（H₂）按显示的样子量：下标段 max(13, 0.78 × 字号)（<see cref="ChemText"/>）。按（文字, 字号, 字重）缓存，换字体时清空。
/// </summary>
internal sealed class StageTextMeasure
{
    private static readonly FontFeatureCollection Tnum = new() { FontFeature.Parse("+tnum") };
    private readonly Dictionary<(string Text, double Size, bool Bold), double> _cache = new();
    private FontFamily? _family;

    public double Width(FontFamily family, string text, double size, bool semiBold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (!ReferenceEquals(family, _family)) { _cache.Clear(); _family = family; }
        var key = (text, size, semiBold);
        if (_cache.TryGetValue(key, out var w)) return w;
        if (_cache.Count > 512) _cache.Clear();
        w = 0;
        foreach (var (seg, sub) in ChemText.Segments(text))
        {
            double fs = sub ? ChemText.SubSize(size) : size;
            using var tl = new TextLayout(seg, new Typeface(family, FontStyle.Normal, semiBold ? FontWeight.SemiBold : FontWeight.Normal), Tnum, fs, null);
            w += tl.WidthIncludingTrailingWhitespace;
        }
        _cache[key] = w;
        return w;
    }
}

/// <summary>
/// 带下标数字的短文字（样稿 PR.chem：H₂、N₂ 的下标数字换成 &lt;sub class="pr-chem"&gt;，字号 max(13 px, 0.78 em)、下移 0.15 em ——
/// 下标字符在部分字体里只有 6 px）：普通段与下标段各一个 PrText，按基线排；<see cref="Baseline"/> = 顶边到普通段基线的距离。没有下标时就是一个 PrText。
/// </summary>
public sealed class ChemText : Panel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ChemText, string?>(nameof(Text));
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<ChemText>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ChemText>();
    public static readonly StyledProperty<bool> TrimProperty = AvaloniaProperty.Register<ChemText, bool>(nameof(Trim));

    static ChemText() => AffectsMeasure<ChemText>(TextProperty, FontSizeProperty, TrimProperty);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>
    /// 放不下时省略（样稿整行 text-overflow: ellipsis，例如反应釜页程序库的「80 °C · 1.2 MPa N₂ · 反…」）：从左往右放，
    /// 放不下的那一段在剩下的宽度里带省略号，后面的段不画（透明）。默认 false：标注框按自然宽度排（舞台先量好，放不下就换样式）。
    /// </summary>
    public bool Trim { get => GetValue(TrimProperty); set => SetValue(TrimProperty, value); }

    /// <summary>带省略号的那一段（−1 = 全部放得下）。</summary>
    private int _cut = -1;

    /// <summary>下标字号：max(13, 0.78 × 字号)（样稿 sub.pr-chem）。</summary>
    public static double SubSize(double size) => Math.Max(13, Math.Round(size * 0.78, 2));

    /// <summary>（段, 是否下标）：下标数字 ₀–₉ 换成 0–9。</summary>
    public static IEnumerable<(string Text, bool Sub)> Segments(string? s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        var sb = new StringBuilder();
        bool sub = false;
        foreach (var ch in s)
        {
            bool isSub = ch is >= '₀' and <= '₉';
            if (sb.Length > 0 && isSub != sub) { yield return (sb.ToString(), sub); sb.Clear(); }
            sub = isSub;
            sb.Append(isSub ? (char)('0' + (ch - '₀')) : ch);
        }
        if (sb.Length > 0) yield return (sb.ToString(), sub);
    }

    /// <summary>顶边到普通段基线的距离（测量后）。</summary>
    public double Baseline { get; private set; }

    /// <summary>各段前进宽度之和（小数，同 WebKit）。</summary>
    public double NaturalWidth { get; private set; }

    private readonly List<(PrText Text, bool Sub)> _parts = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontSizeProperty) Rebuild();
        else if (change.Property == ForegroundProperty) foreach (var (t, _) in _parts) t.Foreground = Foreground;
    }

    private void Rebuild()
    {
        var segs = new List<(string Text, bool Sub)>(Segments(Text));
        // 段数不变时就地改文字（读数不重建控件）
        if (segs.Count != _parts.Count)
        {
            Children.Clear();
            _parts.Clear();
            foreach (var s in segs)
            {
                var t = new PrText { Foreground = Foreground };
                _parts.Add((t, s.Sub));
                Children.Add(t);
            }
        }
        for (int i = 0; i < segs.Count; i++)
        {
            var (t, _) = _parts[i];
            _parts[i] = (t, segs[i].Sub);
            if (t.Text != segs[i].Text) t.Text = segs[i].Text;
            double fs = segs[i].Sub ? SubSize(FontSize) : FontSize;
            if (!Equals(t.FontSize, fs)) t.FontSize = fs;
        }
    }

    private static double BaselineOf(TextBlock t) => t.TextLayout.TextLines.Count > 0 ? t.TextLayout.TextLines[0].Baseline : t.DesiredSize.Height * 0.8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, above = 0, below = 0;
        foreach (var (t, sub) in _parts)
        {
            t.Measure(Size.Infinity);
            double b = BaselineOf(t), shift = sub ? 0.15 * t.FontSize : 0;
            above = Math.Max(above, b - shift);
            below = Math.Max(below, t.DesiredSize.Height - b + shift);
            w += t.NaturalWidth;
        }
        Baseline = above;
        NaturalWidth = w;
        // 省略（Trim）：第一段放不下的在剩下的宽度里带省略号，后面的透明（不改 IsVisible：排版中途改可见性会让版面来回重排）
        int cut = -1;
        double cutW = 0;
        if (Trim && availableSize.Width < w - 0.01)
        {
            double x = 0;
            for (int i = 0; i < _parts.Count; i++)
            {
                double nw = _parts[i].Text.NaturalWidth;
                if (x + nw > availableSize.Width + 0.01) { cut = i; cutW = Math.Max(0, availableSize.Width - x); break; }
                x += nw;
            }
        }
        _cut = cut;
        for (int i = 0; i < _parts.Count; i++)
        {
            var t = _parts[i].Text;
            var trim = i == cut ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            if (t.TextTrimming != trim) t.TextTrimming = trim;
            double op = cut >= 0 && i > cut ? 0 : 1;
            if (t.Opacity != op) t.Opacity = op;
            if (i == cut) t.Measure(new Size(cutW, double.PositiveInfinity));
        }
        return cut >= 0 ? new Size(availableSize.Width, above + below) : new Size(Math.Ceiling(w - 1e-6), above + below);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            var (t, sub) = _parts[i];
            double b = BaselineOf(t), shift = sub ? 0.15 * t.FontSize : 0;
            // 带省略号的那一段排在剩下的宽度里（DesiredSize 已按它量）
            double tw = i == _cut ? Math.Max(0, finalSize.Width - x) : t.DesiredSize.Width;
            t.Arrange(new Rect(x, Baseline + shift - b, tw, t.DesiredSize.Height));
            x += t.NaturalWidth;
        }
        return finalSize;
    }
}

/// <summary>
/// 一个标注框（样稿 .reactor-co）：白底、1 px line-2 边、4 px 圆角、卡片阴影；左列内容靠右（右内边距 12）、右列靠左（左内边距 12）；
/// 实时测量值靠釜体一侧 4 px g-edge（样稿 inset box-shadow）。行：标签行 20（左列「名称 [TAG]」、右列「[TAG] 名称」，间距 6）·
/// 读数行 30（读数 600 + 单位 13，基线对齐、间距 3；中文单位 14、多 2 px；手形图标 15 居中）· 说明行 20（图标 14 + 文字 14，间距 4）·
/// 附加行 20（14/600 t-2，超限 r-ink）；一行式 28（「读数 单位 ✋ [TAG]」/「[TAG] 读数 单位 ✋」，间距 5）。
/// 位置、尺寸、样式、读数字号由 <see cref="VesselStage"/> 给（<see cref="Apply"/> + <see cref="Spec"/>）。紧凑 / 一行式整框带悬停说明，完整 / 基本由说明行带。
/// </summary>
public sealed class VesselCalloutBox : Border
{
    private readonly CalloutRows _rows;

    internal VesselCalloutBox(VesselCallout key)
    {
        Key = key;
        Classes.Add("audit-box");
        Background = Palette.BgCardBrush;
        BorderBrush = Palette.Line2Brush;
        BorderThickness = new Thickness(1);
        CornerRadius = Res("RadiusS", new CornerRadius(4));
        if (Application.Current?.TryFindResource("ShadowCard", out var s) == true && s is BoxShadows bs) BoxShadow = bs;
        Child = _rows = new CalloutRows(this);
    }

    private static T Res<T>(string key, T fallback) => Application.Current?.TryFindResource(key, out var v) == true && v is T t ? t : fallback;

    public VesselCallout Key { get; }

    /// <summary>当前内容 / 样式 / 一侧 / 读数字号 / 断线。</summary>
    public CalloutContent? Data { get; private set; }
    public CalloutMode Mode { get; private set; } = CalloutMode.Full;
    public CalloutSide Side { get; private set; }
    public double ValueSize { get; private set; } = 24;
    public bool IsStale { get; private set; }

    /// <summary>舞台给的位置（空版面时 null）。</summary>
    public StageBox? Spec { get; internal set; }

    /// <summary>读数 / 手形图标 / 标签（核对用）。</summary>
    public TextBlock ValueText => _rows.Value;
    public IconView Hand => _rows.Hand;

    internal void Apply(CalloutContent data, CalloutMode mode, CalloutSide side, double valueSize, bool stale)
    {
        if (Equals(data, Data) && mode == Mode && side == Side && valueSize == ValueSize && stale == IsStale) return;
        Data = data;
        Mode = mode;
        Side = side;
        ValueSize = valueSize;
        IsStale = stale;
        _rows.Update();
        ToolTip.SetTip(this, data.BoxTip(mode));
    }
}

/// <summary>标注框里的各行（<see cref="VesselCalloutBox"/> 的子元素，坐标在边框里面）。</summary>
internal sealed class CalloutRows : Panel
{
    private const double TagFs = 14, LabelFs = 14, UnitFs = 13, UnitCjkFs = 14, SubFs = 14, HandSize = 15, SubIconSize = 14;

    private readonly VesselCalloutBox _box;
    internal readonly Border Edge;
    internal readonly Border TagBox;
    internal readonly PrText TagText;
    internal readonly PrText Label;
    internal readonly IconView Hand;
    internal readonly PrText Value;
    internal readonly ChemText Unit;
    internal readonly IconView SubIcon;
    internal readonly PrText Sub;
    internal readonly PrText Extra;

    public CalloutRows(VesselCalloutBox box)
    {
        _box = box;
        Edge = new Border { Background = Palette.GEdgeBrush, IsHitTestVisible = false };
        TagText = new PrText { FontSize = TagFs, FontWeight = FontWeight.SemiBold, Foreground = Palette.T2Brush, VerticalAlignment = VerticalAlignment.Center };
        TagBox = new Border
        {
            Height = 20, Padding = new Thickness(5, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Background = Palette.BgHeadBrush, BorderBrush = Palette.Line2Brush, Child = TagText,
        };
        Label = new PrText { FontSize = LabelFs, Foreground = Palette.T3Brush, TextTrimming = TextTrimming.CharacterEllipsis };
        Hand = new IconView { Icon = "hand", Size = HandSize, Foreground = Palette.T3Brush };
        Value = new PrText { FontWeight = FontWeight.SemiBold, Foreground = Palette.T1Brush };
        Value.Classes.Add("num");
        Unit = new ChemText { Foreground = Palette.T3Brush, FontSize = UnitFs };
        SubIcon = new IconView { Size = SubIconSize, Foreground = Palette.T3Brush };
        Sub = new PrText { FontSize = SubFs, Foreground = Palette.T3Brush, TextTrimming = TextTrimming.CharacterEllipsis };
        Extra = new PrText { FontSize = SubFs, FontWeight = FontWeight.SemiBold, Foreground = Palette.T2Brush };
        Children.Add(Edge);
        Children.Add(TagBox);
        Children.Add(Label);
        Children.Add(Hand);
        Children.Add(Value);
        Children.Add(Unit);
        Children.Add(SubIcon);
        Children.Add(Sub);
        Children.Add(Extra);
    }

    /// <summary>内容 / 样式变了：文字、颜色、谁显示（只在这里改可见性，排版时不改）。</summary>
    internal void Update()
    {
        if (_box.Data is not { } d) return;
        var mode = _box.Mode;
        bool tight = mode == CalloutMode.Tight, compact = mode == CalloutMode.Compact, rows3 = !tight && !compact;
        bool left = _box.Side == CalloutSide.Left;
        Edge.IsVisible = d.Live;
        Edge.CornerRadius = left ? new CornerRadius(0, 3, 3, 0) : new CornerRadius(3, 0, 0, 3);
        Set(TagText, d.Tag);
        Label.IsVisible = !tight;
        Set(Label, d.Label);
        Hand.IsVisible = d.HandInValueRow(mode);
        ToolTip.SetTip(Hand, tight ? null : d.HandTip);
        Set(Value, d.Value);
        if (!Equals(Value.FontSize, _box.ValueSize)) Value.FontSize = _box.ValueSize;
        // 断线（舞台 stale）时读数一律 t-3，缺值的「—」也是：样稿 .reactor-stage.is-stale .reactor-co-val b（0,3,1）盖过 b.is-na（0,2,1）的 t-dis；缺值的字重照样 400
        Value.FontWeight = d.Na ? FontWeight.Normal : FontWeight.SemiBold;
        Value.Foreground = _box.IsStale ? Palette.T3Brush : d.Na ? Palette.TDisBrush : Palette.T1Brush;
        Unit.IsVisible = d.ShowsUnit;
        if (Unit.Text != d.Unit) Unit.Text = d.Unit;
        Unit.FontSize = d.CjkUnit ? UnitCjkFs : UnitFs;
        SubIcon.IsVisible = rows3 && d.Icon != null;
        if (d.Icon != null) SubIcon.Icon = d.Icon;
        Sub.IsVisible = rows3;
        Set(Sub, d.Sub);
        ToolTip.SetTip(Sub, rows3 ? d.SubTip : null);
        Extra.IsVisible = mode == CalloutMode.Full && d.Extra.Length > 0;
        Set(Extra, d.Extra);
        Extra.Foreground = d.Err ? Palette.RInkBrush : Palette.T2Brush;
        InvalidateMeasure();
        InvalidateArrange();
    }

    private static void Set(TextBlock t, string s)
    {
        if (t.Text != s) t.Text = s;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children)
            if (c.IsVisible && !ReferenceEquals(c, Edge)) c.Measure(Size.Infinity);
        Edge.Measure(Size.Infinity);
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    private static double Nat(Layoutable c) => c is PrText p ? Math.Min(p.NaturalWidth, p.DesiredSize.Width) : c is ChemText ct ? ct.NaturalWidth : c.DesiredSize.Width;

    private static double BaseOf(TextBlock t) => t.TextLayout.TextLines.Count > 0 ? t.TextLayout.TextLines[0].Baseline : t.DesiredSize.Height * 0.8;

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_box.Data is not { } d) return finalSize;
        var mode = _box.Mode;
        var B = StageLayout.Box(mode);
        bool left = _box.Side == CalloutSide.Left, tight = mode == CalloutMode.Tight;
        double W = finalSize.Width, H = finalSize.Height;
        double padL = left ? 8 : 12, padR = left ? 12 : 8;
        double xs = padL, xe = W - padR;
        double gap = StageLayout.ValueGap(mode);
        double unitGap = gap + (d.CjkUnit ? 2 : 0);
        double tagW = TagBox.DesiredSize.Width;

        // 实时边：靠釜体一侧，边框里面整条高
        Edge.Arrange(d.Live ? new Rect(left ? W - 4 : 0, 0, 4, H) : default);

        if (tight)
        {
            // 一行式：读数居中、单位按基线（margin-top 3）、标签 20 居中、手形 15 居中
            double y = B.Py, rh = StageLayout.RowTight;
            var items = new List<(Layoutable El, double W, double Gap)>();
            if (!left) items.Add((TagBox, tagW, 0));
            items.Add((Value, Nat(Value), items.Count > 0 ? gap : 0));
            if (Unit.IsVisible) items.Add((Unit, Unit.NaturalWidth, unitGap));
            if (Hand.IsVisible) items.Add((Hand, HandSize + 4, gap));
            if (left) items.Add((TagBox, tagW, gap));
            double x = RowStart(items, left, xs, xe);
            foreach (var (el, w, g) in items)
            {
                x += g;
                if (ReferenceEquals(el, TagBox)) TagBox.Arrange(new Rect(x, y + (rh - 20) / 2, w, 20));
                else if (ReferenceEquals(el, Value)) Value.Arrange(new Rect(x, y + (rh - Value.DesiredSize.Height) / 2, Value.DesiredSize.Width, Value.DesiredSize.Height));
                else if (ReferenceEquals(el, Unit)) Unit.Arrange(new Rect(x, y + 3 + (rh - Unit.DesiredSize.Height) / 2, Unit.DesiredSize.Width, Unit.DesiredSize.Height));
                else Hand.Arrange(new Rect(x + 2, y + (rh - HandSize) / 2, HandSize, HandSize));
                x += w;
            }
            return finalSize;
        }

        // 标签行（20）：左列「名称 [TAG]」靠右，右列「[TAG] 名称」靠左；名称放不下时省略（带提示）
        double y0 = B.Py;
        double labW = Nat(Label);
        double room = Math.Max(0, xe - xs - tagW - 6);
        double lw = Math.Min(labW, room);
        if (left)
        {
            double tx = xe - tagW;
            TagBox.Arrange(new Rect(tx, y0, tagW, 20));
            Label.Arrange(new Rect(tx - 6 - lw, y0 + (20 - Label.DesiredSize.Height) / 2, Math.Max(lw, 0), Label.DesiredSize.Height));
        }
        else
        {
            TagBox.Arrange(new Rect(xs, y0, tagW, 20));
            Label.Arrange(new Rect(xs + tagW + 6, y0 + (20 - Label.DesiredSize.Height) / 2, Math.Max(lw, 0), Label.DesiredSize.Height));
        }
        ToolTip.SetTip(Label, lw + 0.5 < labW ? d.Label : null);

        // 读数行（30）：读数与单位按基线对齐（CSS flex baseline：两者的行高都是 30），手形图标居中
        double y1 = y0 + StageLayout.RowHead, rv = StageLayout.RowVal;
        double bv = BaseOf(Value), hv = Value.DesiredSize.Height;
        double bu = Unit.Baseline, hu = Unit.DesiredSize.Height;
        double rowBase = (rv - hv) / 2 + bv;
        if (Unit.IsVisible) rowBase = Math.Max(rowBase, (rv - hu) / 2 + bu);
        var vals = new List<(Layoutable El, double W, double Gap)>();
        if (left && Hand.IsVisible) vals.Add((Hand, HandSize + 4, 0));
        vals.Add((Value, Nat(Value), vals.Count > 0 ? gap : 0));
        if (Unit.IsVisible) vals.Add((Unit, Unit.NaturalWidth, unitGap));
        if (!left && Hand.IsVisible) vals.Add((Hand, HandSize + 4, gap));
        double vx = RowStart(vals, left, xs, xe);
        foreach (var (el, w, g) in vals)
        {
            vx += g;
            if (ReferenceEquals(el, Value)) Value.Arrange(new Rect(vx, y1 + rowBase - bv, Value.DesiredSize.Width, hv));
            else if (ReferenceEquals(el, Unit)) Unit.Arrange(new Rect(vx, y1 + rowBase - bu, Unit.DesiredSize.Width, hu));
            else Hand.Arrange(new Rect(vx + 2, y1 + (rv - HandSize) / 2, HandSize, HandSize));
            vx += w;
        }

        // 说明行（20）：图标 14 + 文字，间距 4；放不下时文字省略（自带提示）
        if (Sub.IsVisible)
        {
            double y2 = y1 + rv;
            double iw = SubIcon.IsVisible ? SubIconSize + 4 : 0;
            double sw = Math.Min(Nat(Sub), Math.Max(0, xe - xs - iw));
            double sx = left ? xe - sw - iw : xs;
            if (SubIcon.IsVisible) SubIcon.Arrange(new Rect(sx, y2 + (20 - SubIconSize) / 2, SubIconSize, SubIconSize));
            Sub.Arrange(new Rect(sx + iw, y2 + (20 - Sub.DesiredSize.Height) / 2, sw, Sub.DesiredSize.Height));
            if (Extra.IsVisible)
            {
                double y3 = y2 + StageLayout.RowSub;
                double ew = Math.Min(Nat(Extra), Math.Max(0, xe - xs));
                Extra.Arrange(new Rect(left ? xe - ew : xs, y3 + (20 - Extra.DesiredSize.Height) / 2, Extra.DesiredSize.Width, Extra.DesiredSize.Height));
            }
        }
        return finalSize;
    }

    /// <summary>一行的起点：左列靠右（从右边减去整行宽度），右列靠左。</summary>
    private static double RowStart(List<(Layoutable El, double W, double Gap)> items, bool alignRight, double xs, double xe)
    {
        double total = 0;
        foreach (var (_, w, g) in items) total += w + g;
        return alignRight ? xe - total : xs;
    }
}

/// <summary>
/// 引线（样稿 .reactor-lead：细灰 v-detail 1 px，fill none）与锚点上的小圆点（.reactor-lead-dot：r 2.5，bg-card 填充、v-line 1.25 描边）。
/// 几何按版面缓存（版面变了才重建）。不接收鼠标。
/// </summary>
internal sealed class VesselLeads : Avalonia.Controls.Control
{
    private StageResult _result = StageResult.None;
    private Geometry? _paths;

    public StageResult Result
    {
        get => _result;
        set
        {
            if (ReferenceEquals(value, _result)) return;
            _result = value;
            _paths = null;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    public override void Render(DrawingContext context)
    {
        var r = _result;
        if (r.Empty || r.Leads.Count == 0) return;
        _paths ??= Build(r);
        context.DrawGeometry(null, VesselPaint.Pen(Palette.VDetailColor, 1), _paths);
        var fill = VesselPaint.Brush(Palette.BgCardColor);
        var pen = VesselPaint.Pen(Palette.VLineColor, 1.25);
        foreach (var l in r.Leads) context.DrawEllipse(fill, pen, new Point(l.Dot.X, l.Dot.Y), 2.5, 2.5);
    }

    private static Geometry Build(StageResult r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var l in r.Leads)
            {
                if (l.Points.Count < 2) continue;
                ctx.BeginFigure(new Point(l.Points[0].X, l.Points[0].Y), false);
                for (int i = 1; i < l.Points.Count; i++) ctx.LineTo(new Point(l.Points[i].X, l.Points[i].Y));
                ctx.EndFigure(false);
            }
        }
        return g;
    }
}
