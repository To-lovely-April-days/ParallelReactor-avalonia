using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 面板（样稿 ui.panel / .pr-panel，原稿语汇）：标题栏 42 px（bg-head + 下边 line-2）——抓手、图标 18、标题 16/600、
/// 副标题 14 t-3（先让位，标题最后才省略，两者都带提示）、右侧灰色图标操作 + 放大 / 关闭；内容区 bg-panel。
/// <see cref="Title"/> 可以是文字或控件（面包屑 <see cref="Crumb"/> 等）；文字标题自动省略 + 提示。
/// 放大 / 关闭只改 <see cref="IsMaximized"/> / <see cref="IsClosed"/>（伪类 :max / :closed），怎么铺满由宿主（M2 PageHost）决定。
/// </summary>
public sealed class PrPanel : ContentControl
{
    public static readonly StyledProperty<object?> TitleProperty = AvaloniaProperty.Register<PrPanel, object?>(nameof(Title));
    public static readonly StyledProperty<string?> TitleTipProperty = AvaloniaProperty.Register<PrPanel, string?>(nameof(TitleTip));
    public static readonly StyledProperty<string?> SubProperty = AvaloniaProperty.Register<PrPanel, string?>(nameof(Sub));
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<PrPanel, string?>(nameof(Icon));
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<PrPanel, object?>(nameof(Actions));
    public static readonly StyledProperty<bool> ShowGripProperty = AvaloniaProperty.Register<PrPanel, bool>(nameof(ShowGrip), true);
    public static readonly StyledProperty<bool> CanMaximizeProperty = AvaloniaProperty.Register<PrPanel, bool>(nameof(CanMaximize), true);
    public static readonly StyledProperty<bool> CanCloseProperty = AvaloniaProperty.Register<PrPanel, bool>(nameof(CanClose), true);
    public static readonly StyledProperty<bool> IsMaximizedProperty = AvaloniaProperty.Register<PrPanel, bool>(nameof(IsMaximized));
    public static readonly StyledProperty<bool> IsClosedProperty = AvaloniaProperty.Register<PrPanel, bool>(nameof(IsClosed));

    /// <summary>模板里显示的标题：文字标题包成省略的 TextBlock，控件原样。</summary>
    public static readonly DirectProperty<PrPanel, object?> TitleContentProperty =
        AvaloniaProperty.RegisterDirect<PrPanel, object?>(nameof(TitleContent), o => o.TitleContent);
    /// <summary>标题的悬停提示（TitleTip，否则标题文字 / 面包屑全文）。</summary>
    public static readonly DirectProperty<PrPanel, string?> ShownTitleTipProperty =
        AvaloniaProperty.RegisterDirect<PrPanel, string?>(nameof(ShownTitleTip), o => o.ShownTitleTip);
    /// <summary>放大按钮的图标与提示（max ↔ restore）。</summary>
    public static readonly DirectProperty<PrPanel, string> MaxIconProperty = AvaloniaProperty.RegisterDirect<PrPanel, string>(nameof(MaxIcon), o => o.MaxIcon);
    public static readonly DirectProperty<PrPanel, string> MaxTipProperty = AvaloniaProperty.RegisterDirect<PrPanel, string>(nameof(MaxTip), o => o.MaxTip);

    public static readonly RoutedEvent<RoutedEventArgs> ClosedEvent = RoutedEvent.Register<PrPanel, RoutedEventArgs>(nameof(Closed), RoutingStrategies.Bubble);

    public object? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? TitleTip { get => GetValue(TitleTipProperty); set => SetValue(TitleTipProperty, value); }
    public string? Sub { get => GetValue(SubProperty); set => SetValue(SubProperty, value); }
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>右侧操作（一般是 PrIconButton，多个时放 StackPanel）。</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool ShowGrip { get => GetValue(ShowGripProperty); set => SetValue(ShowGripProperty, value); }
    public bool CanMaximize { get => GetValue(CanMaximizeProperty); set => SetValue(CanMaximizeProperty, value); }
    public bool CanClose { get => GetValue(CanCloseProperty); set => SetValue(CanCloseProperty, value); }
    public bool IsMaximized { get => GetValue(IsMaximizedProperty); set => SetValue(IsMaximizedProperty, value); }
    public bool IsClosed { get => GetValue(IsClosedProperty); set => SetValue(IsClosedProperty, value); }

    /// <summary>点了关闭（IsClosed 已设为 true）。</summary>
    public event EventHandler<RoutedEventArgs>? Closed { add => AddHandler(ClosedEvent, value); remove => RemoveHandler(ClosedEvent, value); }

    private object? _titleContent;
    private string? _titleTip;
    private string _maxIcon = "max", _maxTip = "放大面板";
    public object? TitleContent { get => _titleContent; private set => SetAndRaise(TitleContentProperty, ref _titleContent, value); }
    public string? ShownTitleTip { get => _titleTip; private set => SetAndRaise(ShownTitleTipProperty, ref _titleTip, value); }
    public string MaxIcon { get => _maxIcon; private set => SetAndRaise(MaxIconProperty, ref _maxIcon, value); }
    public string MaxTip { get => _maxTip; private set => SetAndRaise(MaxTipProperty, ref _maxTip, value); }

    private Button? _max, _close;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_max != null) _max.Click -= OnMax;
        if (_close != null) _close.Click -= OnClose;
        _max = e.NameScope.Find<Button>("PART_Max");
        _close = e.NameScope.Find<Button>("PART_Close");
        if (_max != null) _max.Click += OnMax;
        if (_close != null) _close.Click += OnClose;
        UpdateTitle();
    }

    private void OnMax(object? sender, RoutedEventArgs e) => IsMaximized = !IsMaximized;

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        IsClosed = true;
        RaiseEvent(new RoutedEventArgs(ClosedEvent));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == TitleTipProperty) UpdateTitle();
        else if (change.Property == IsMaximizedProperty)
        {
            PseudoClasses.Set(":max", IsMaximized);
            MaxIcon = IsMaximized ? "restore" : "max";
            MaxTip = IsMaximized ? "还原面板" : "放大面板";
        }
        else if (change.Property == IsClosedProperty) PseudoClasses.Set(":closed", IsClosed);
    }

    private void UpdateTitle()
    {
        TitleContent = Title is string s
            ? new TextBlock { Text = s, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold }
            : Title;
        ShownTitleTip = TitleTip ?? (Title is string t ? t : Title is Crumb c ? c.Text : null);
    }
}
