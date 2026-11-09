using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 对话框外壳（样稿 .pr-modal，shell.css「对话框外壳」）：白底、8 px 圆角、1 px line-2、ShadowDlg；
/// 标题区（最小高 66）：38 px 图标块（bg-sunken / 急停 r-bg）+ 标题 18/600 + 说明 14 t-3 + 关闭 ×；内容区内边距 20、15 px；
/// 底栏 bg-head + 上边 line-faint，按钮靠右间距 10，左侧可放 14 px t-3 的说明。
/// 中性：只有急停对话框的确认按钮是红色（PrDanger）。对话框宿主与 9 个对话框在 M3 做；遮罩用 OverlayBrush。
/// </summary>
public sealed class DialogFrame : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DialogFrame, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubProperty = AvaloniaProperty.Register<DialogFrame, string?>(nameof(Sub));
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<DialogFrame, string?>(nameof(Icon), "info");
    public static readonly StyledProperty<bool> IsDangerProperty = AvaloniaProperty.Register<DialogFrame, bool>(nameof(IsDanger));
    public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<DialogFrame, object?>(nameof(Footer));
    public static readonly StyledProperty<object?> FooterLeftProperty = AvaloniaProperty.Register<DialogFrame, object?>(nameof(FooterLeft));

    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent = RoutedEvent.Register<DialogFrame, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Sub { get => GetValue(SubProperty); set => SetValue(SubProperty, value); }
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>急停类对话框：图标块 r-bg + r。</summary>
    public bool IsDanger { get => GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }
    /// <summary>底栏右侧的按钮（一般是 StackPanel）。</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    /// <summary>底栏左侧说明。</summary>
    public object? FooterLeft { get => GetValue(FooterLeftProperty); set => SetValue(FooterLeftProperty, value); }

    public event EventHandler<RoutedEventArgs>? CloseRequested { add => AddHandler(CloseRequestedEvent, value); remove => RemoveHandler(CloseRequestedEvent, value); }

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
        if (change.Property == IsDangerProperty) PseudoClasses.Set(":danger", IsDanger);
    }
}
