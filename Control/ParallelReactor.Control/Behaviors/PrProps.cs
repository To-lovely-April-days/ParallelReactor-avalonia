using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Behaviors;

/// <summary>
/// Pr 主题（Styles/Theme/*.axaml）读取的附加属性：按钮 / 芯片 / 页签 / 菜单项都是 Avalonia 自带的控件，
/// 图标、单位、红点、计数这些样稿参数用附加属性传给模板（<c>b:Pr.Icon="edit"</c>），不必为每种按钮派生新类型。
/// </summary>
public static partial class Pr
{
    /// <summary>图标名（ui.js ICONS 的键；资源 Icon.名字）。PrButton / PrIconButton / PrChip / PrSideItem / PrDocTab / PrMenuItem 的模板显示它。</summary>
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Icon", typeof(Pr));

    /// <summary>输入框的单位后缀（PrInput：右侧 14 px t-3，样稿 ui.input 的 unit）。</summary>
    public static readonly AttachedProperty<string?> UnitProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Unit", typeof(Pr));

    /// <summary>红点：只表示「有未确认的错误报警」（PrSegItem / PrPTab / PrDocTab）。</summary>
    public static readonly AttachedProperty<bool> DotProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("Dot", typeof(Pr));

    /// <summary>中性计数（PrPTab 的未确认提醒数、PrChip 的数字）；null / 空 = 不显示。</summary>
    public static readonly AttachedProperty<string?> CountProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Count", typeof(Pr));

    /// <summary>文档页签的关闭命令（PrDocTab 的 ×）；null = 固定页签，不显示 ×。</summary>
    public static readonly AttachedProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, ICommand?>("CloseCommand", typeof(Pr));

    public static string? GetIcon(AvaloniaObject o) => o.GetValue(IconProperty);
    public static void SetIcon(AvaloniaObject o, string? v) => o.SetValue(IconProperty, v);
    public static string? GetUnit(AvaloniaObject o) => o.GetValue(UnitProperty);
    public static void SetUnit(AvaloniaObject o, string? v) => o.SetValue(UnitProperty, v);
    public static bool GetDot(AvaloniaObject o) => o.GetValue(DotProperty);
    public static void SetDot(AvaloniaObject o, bool v) => o.SetValue(DotProperty, v);
    public static string? GetCount(AvaloniaObject o) => o.GetValue(CountProperty);
    public static void SetCount(AvaloniaObject o, string? v) => o.SetValue(CountProperty, v);
    public static ICommand? GetCloseCommand(AvaloniaObject o) => o.GetValue(CloseCommandProperty);
    public static void SetCloseCommand(AvaloniaObject o, ICommand? v) => o.SetValue(CloseCommandProperty, v);

    // 自动给的无障碍名（Style 优先级的值，换名字时先撤销旧的）
    private static readonly AttachedProperty<IDisposable?> AutoNameProperty =
        AvaloniaProperty.RegisterAttached<Button, IDisposable?>("AutoName", typeof(Pr));

    /// <summary>
    /// 纯图标按钮（有 b:Pr.Icon、没有 Content）没写 AutomationProperties.Name 时，用提示文字当无障碍名（读屏不再念「按钮」）。
    /// 取的是原因覆盖之前的提示（GetBaseValue 不含 Animation 优先级的原因提示）。以 Style 优先级设置：调用处（本地值）或模板里写的名字总是优先。
    /// </summary>
    private static void UpdateAutoName(Button b)
    {
        if (string.IsNullOrEmpty(GetIcon(b)) || b.Content is not null) return;
        b.GetValue(AutoNameProperty)?.Dispose();
        var tip = b.GetBaseValue(ToolTip.TipProperty);
        var name = tip.HasValue && tip.Value is string s && !string.IsNullOrWhiteSpace(s) ? UiText.Plain(s) : null;
        b.SetValue(AutoNameProperty, name == null ? null : b.SetValue(AutomationProperties.NameProperty, name, BindingPriority.Style));
    }
}
