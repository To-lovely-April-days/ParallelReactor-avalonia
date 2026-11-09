using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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

    private string _shownValue = Fmt.Dash, _unitText = "";
    public string ShownValue { get => _shownValue; private set => SetAndRaise(ShownValueProperty, ref _shownValue, value); }
    /// <summary>单位行（「MPa · 50.6 psi」；没有读数时不写第一单位）。</summary>
    public string UnitText { get => _unitText; private set => SetAndRaise(UnitTextProperty, ref _unitText, value); }

    public PropCard() => Update();

    private Button? _close;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_close != null) _close.Click -= OnClose;
        _close = e.NameScope.Find<Button>("PART_Close");
        if (_close != null) _close.Click += OnClose;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CloseRequestedEvent));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == UnitProperty || change.Property == Unit2Property
            || change.Property == EdgeProperty || change.Property == IsStaleProperty) Update();
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
