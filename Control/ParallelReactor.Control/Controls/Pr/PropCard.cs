using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 属性卡片（样稿 ui.propCard / .pr-prop，原稿属性查看器）：高 83 = 路径行 24（树图标 15 + 路径 14 t-3）+ 主体 58。
/// 主体两行网格：图标 30 | 名称 15 t-2 … 读数 28/600 t-1（右对齐）/ 说明 15/600 t-1 … 单位 14 t-3（单位行只和说明行争宽度）。
/// 实时值（<see cref="Edge"/>）右侧 4 px g-edge；没有读数时显示「—」（t-dis 400）；<see cref="IsStale"/> 读数 t-3。
/// 底色：白 60 % → 0 的横向渐变叠在面板底色上，下边 1 px line-2。
/// </summary>
public sealed class PropCard : TemplatedControl
{
    public static readonly StyledProperty<string?> PathTextProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(PathText));
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(Icon), "gauge");
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(Sub));
    public static readonly StyledProperty<string?> ValueProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(Value));
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<PropCard, string?>(nameof(Unit));
    public static readonly StyledProperty<string?> Unit2Property = AvaloniaProperty.Register<PropCard, string?>(nameof(Unit2));
    public static readonly StyledProperty<bool> EdgeProperty = AvaloniaProperty.Register<PropCard, bool>(nameof(Edge));
    public static readonly StyledProperty<bool> IsStaleProperty = AvaloniaProperty.Register<PropCard, bool>(nameof(IsStale));
    public static readonly StyledProperty<bool> ShowCloseProperty = AvaloniaProperty.Register<PropCard, bool>(nameof(ShowClose), true);

    public static readonly DirectProperty<PropCard, string> ShownValueProperty = AvaloniaProperty.RegisterDirect<PropCard, string>(nameof(ShownValue), o => o.ShownValue);
    public static readonly DirectProperty<PropCard, string> ShownPathProperty = AvaloniaProperty.RegisterDirect<PropCard, string>(nameof(ShownPath), o => o.ShownPath);
    public static readonly DirectProperty<PropCard, string> UnitTextProperty = AvaloniaProperty.RegisterDirect<PropCard, string>(nameof(UnitText), o => o.UnitText);

    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent = RoutedEvent.Register<PropCard, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    /// <summary>路径行（/1号机/RV1/模拟输入）。</summary>
    public string? PathText { get => GetValue(PathTextProperty); set => SetValue(PathTextProperty, value); }
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>名称（反应器温度）。</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>粗体说明（温度 PV）。</summary>
    public string? Sub { get => GetValue(SubProperty); set => SetValue(SubProperty, value); }
    /// <summary>已格式化的读数（null / 「—」= 没有读数）。</summary>
    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>第二单位 / 说明（「50.6 psi」「触屏操作」）。</summary>
    public string? Unit2 { get => GetValue(Unit2Property); set => SetValue(Unit2Property, value); }
    /// <summary>实时值：右侧 4 px 绿边。</summary>
    public bool Edge { get => GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public bool IsStale { get => GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }
    public bool ShowClose { get => GetValue(ShowCloseProperty); set => SetValue(ShowCloseProperty, value); }

    public event EventHandler<RoutedEventArgs>? CloseRequested { add => AddHandler(CloseRequestedEvent, value); remove => RemoveHandler(CloseRequestedEvent, value); }

    private string _shownValue = Fmt.Dash, _unitText = "", _shownPath = "";
    public string ShownValue { get => _shownValue; private set => SetAndRaise(ShownValueProperty, ref _shownValue, value); }

    /// <summary>
    /// 路径行实际显示的文字：放得下是 <see cref="PathText"/>，放不下是逐字省略的「/1号机/RV1/设备/加…」（<see cref="TextFit"/>：
    /// Avalonia 11.1 的 CharacterEllipsis 遇到中文回退段与西文段混排时省略得过早，复核）。悬停照样是全文。
    /// </summary>
    public string ShownPath { get => _shownPath; private set => SetAndRaise(ShownPathProperty, ref _shownPath, value); }
    /// <summary>单位行（「MPa · 50.6 psi」；没有读数时不写第一单位）。</summary>
    public string UnitText { get => _unitText; private set => SetAndRaise(UnitTextProperty, ref _unitText, value); }

    public PropCard() => Update();

    private Button? _close;
    private FlexRow? _pathRow;
    private TextBlock? _path;
    private Border? _edgeBox;
    private (string? Text, double Width, double Size, FontFamily? Family) _fitKey;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_close != null) _close.Click -= OnClose;
        _close = e.NameScope.Find<Button>("PART_Close");
        if (_close != null) _close.Click += OnClose;
        _pathRow = e.NameScope.Find<FlexRow>("PART_PathRow");
        _path = e.NameScope.Find<TextBlock>("PART_Path");
        _edgeBox = e.NameScope.Find<Border>("PART_EdgeBox");
        _fitKey = default;
    }

    /// <summary>量之前先定路径行的文字（按这张卡这一遍拿到的宽度，只看父元素给的宽度，R4；无穷宽 = 全文）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        FitPath(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// 路径行留给文字的宽度 = 卡宽 − 左右边框 − 实时值的右边线 − 行的左右外边距 − 其它可见元素（树图标、×）− 间距；放不下时逐字省略。
    /// 结果按（全文, 宽度, 字号, 字族）缓存，同样的输入不再量。
    /// </summary>
    private void FitPath(double cardWidth)
    {
        var full = PathText ?? "";
        if (_pathRow is null || _path is null || double.IsInfinity(cardWidth) || double.IsNaN(cardWidth)) { ShownPath = full; return; }
        double w = cardWidth - BorderThickness.Left - BorderThickness.Right - Padding.Left - Padding.Right;
        if (_edgeBox is { } eb) w -= eb.BorderThickness.Left + eb.BorderThickness.Right + eb.Padding.Left + eb.Padding.Right;
        w -= _pathRow.Margin.Left + _pathRow.Margin.Right;
        int others = 0;
        foreach (var c in _pathRow.Children)
        {
            if (ReferenceEquals(c, _path) || !c.IsVisible) continue;
            c.Measure(Size.Infinity);
            w -= c.DesiredSize.Width;   // DesiredSize 含外边距
            others++;
        }
        w -= others * _pathRow.Spacing;
        w = Math.Max(0, w);
        var key = (full, Math.Round(w, 2), _path.FontSize, _path.FontFamily);
        if (key == _fitKey) return;
        _fitKey = key;
        var tb = _path;
        ShownPath = TextFit.Prefix(full, w, t => TextFit.Width(tb, t));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CloseRequestedEvent));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == UnitProperty || change.Property == Unit2Property
            || change.Property == EdgeProperty || change.Property == IsStaleProperty) Update();
        if (change.Property == PathTextProperty) { _fitKey = default; ShownPath = PathText ?? ""; InvalidateMeasure(); }
    }

    private void Update()
    {
        bool na = string.IsNullOrEmpty(Value) || Value == Fmt.Dash;
        ShownValue = na ? Fmt.Dash : Value!;
        var unit = !na && !string.IsNullOrEmpty(Unit) ? Unit : "";
        UnitText = unit.Length > 0 && !string.IsNullOrEmpty(Unit2) ? unit + " · " + Unit2 : unit.Length > 0 ? unit : Unit2 ?? "";
        PseudoClasses.Set(":na", na);
        PseudoClasses.Set(":edge", Edge);
        PseudoClasses.Set(":stale", IsStale);
    }
}
