using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ParallelReactor.Control.Controls;

namespace ParallelReactor.Control.Behaviors;

/// <summary>
/// 自己在 Toggle() 里检查 <see cref="Pr.ReasonProperty"/> 的开关（pr:Chip、pr:PrToggle、pr:PrCheckBox）：
/// 带原因时 UI 自动化的 Toggle、HotKey 也翻不动它。普通 ToggleButton / CheckBox 带原因时 ShotAudit 会报。
/// </summary>
public interface IReasonGuarded { }

/// <summary>
/// 禁用并说明原因（PORT-PLAN §2.1「提示框」、§5 M1；样稿 ui.dis / ui.reasonLine：aria-disabled + data-reason）：
/// <c>b:Pr.Reason="先申请控制权"</c>。控件仍然可以聚焦（键盘用户也能读到原因），看起来是禁用的（伪类 <c>:reason</c>，
/// 各 Pr 主题里与 <c>:disabled</c> 同样的外观），操作一律被拦下并说明原因：有订阅者时交给 <see cref="ReasonRequested"/>
/// （M2 的 toast 宿主），没有时直接打开原因提示。M2 起它是控制类操作（先申请控制权）的闸门，所以分四层拦（M1 复核：
/// 只拦指针按下和 Enter 按下时，Space 抬起、IsDefault 回车、IsCancel Esc、HotKey、UI 自动化 Invoke 都能执行命令）：
///  1. 输入（隧道阶段标记已处理）：指针按下 / 抬起；Enter、Space 的按下与抬起；选择 / 数值类控件（ComboBox、ListBox、Slider…）
///     另加方向键、Home / End、PageUp / PageDown、F4；文字输入。Tab、Esc 和带 Ctrl / ⌘ 的快捷键照常
///     （焦点能移走，对话框能关，全局快捷键如急停不受影响）。
///  2. 点击事件：Button（含 ToggleButton）的 Click 类处理器（handledEventsToo）标记已处理 → Click 订阅者不执行，
///     Button 也不再执行 Command（IsDefault 回车、IsCancel Esc、UI 自动化 Invoke 都走 OnClick）。
///  3. 命令：Button / MenuItem 的 Command 以 Animation 优先级换成「说明原因」的守卫命令（原来的绑定 / 本地值不动，
///     清空原因后自动恢复）→ HotKey 这类直接执行 Command 的路径也只会说明原因。
///  4. 开关：实现 <see cref="IReasonGuarded"/> 的开关在 Toggle() 里检查原因；普通 ToggleButton / CheckBox 只有 1–3 层（ShotAudit 报）。
/// TextBox 带原因时以 Animation 优先级只读。提示（PrTip，锁图标 + 原因，悬停 400 ms；ShowOnDisabled）、
/// 无障碍说明（AutomationProperties.HelpText = 原因）同样用 Animation 优先级覆盖，清空原因后原来的提示 / 说明原样回来。
/// 限制：原因要设在被拦的控件自己身上（按钮 / 菜单项 / 开关 / 输入），设在容器上只拦得住指针和键盘；带子菜单的菜单项不要带原因
/// （悬停仍会展开子菜单，原因写在子项上）；ComboBox 的 UI 自动化选择拦不住。第二道防线（M2）：受控制权约束的视图模型命令
/// 在 Execute 里再用 Core 的守卫检查一次。
/// </summary>
public static partial class Pr
{
    public static readonly AttachedProperty<string?> ReasonProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.Control, string?>("Reason", typeof(Pr));

    public static string? GetReason(Avalonia.Controls.Control c) => c.GetValue(ReasonProperty);
    public static void SetReason(Avalonia.Controls.Control c, string? value) => c.SetValue(ReasonProperty, value);

    /// <summary>被拦下的操作：（控件, 原因）。M2 的 toast 宿主订阅；没有订阅者时打开控件的原因提示。</summary>
    public static event Action<Avalonia.Controls.Control, string>? ReasonRequested;

    /// <summary>原因提示的显示延时（PrTip：ShowDelay 400、BetweenShowDelay 300）。</summary>
    public const int TipDelayMs = 400, TipBetweenMs = 300;

    /// <summary>控件自己带着原因（被拦着）。开关类在 Toggle() 里用它。</summary>
    public static bool IsLocked(AvaloniaObject o) => o is Avalonia.Controls.Control c && Has(c, out _);

    /// <summary>每个带过原因的控件一份：输入处理器是否已挂上、各个以 Animation 优先级覆盖的值（清空原因时撤销）。</summary>
    private sealed class ReasonState
    {
        public bool Hooked;
        public IDisposable? Tip, ShowOnDisabled, ShowDelay, BetweenDelay, Help, Command, ReadOnly;

        public void Undo()
        {
            foreach (var d in new[] { Tip, ShowOnDisabled, ShowDelay, BetweenDelay, Help, Command, ReadOnly }) d?.Dispose();
            Tip = ShowOnDisabled = ShowDelay = BetweenDelay = Help = Command = ReadOnly = null;
        }
    }

    private static readonly AttachedProperty<ReasonState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.Control, ReasonState?>("ReasonState", typeof(Pr));

    static Pr()
    {
        ReasonProperty.Changed.AddClassHandler<Avalonia.Controls.Control>(OnReasonChanged);
        // 第 2 层：所有 Button（含 ToggleButton）的 Click；只看控件自己或祖先有没有原因，没有原因的按钮不受影响
        Button.ClickEvent.AddClassHandler<Button>(OnButtonClick, RoutingStrategies.Bubble, handledEventsToo: true);
        // 纯图标按钮的无障碍名默认取提示文字（见 PrProps.cs）
        ToolTip.TipProperty.Changed.AddClassHandler<Button>((b, _) => UpdateAutoName(b));
        IconProperty.Changed.AddClassHandler<Button>((b, _) => UpdateAutoName(b));
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((b, _) => UpdateAutoName(b));
        // 混合字重的中文段（见 CjkRuns.cs）
        CjkRunsProperty.Changed.AddClassHandler<TextBlock>(OnCjkRunsChanged);
    }

    private static bool Has(Avalonia.Controls.Control c, out string reason)
    {
        reason = c.GetValue(ReasonProperty) ?? "";
        return !string.IsNullOrWhiteSpace(reason);
    }

    /// <summary>控件自己或祖先带原因（容器上的原因也拦得住点击事件）。</summary>
    private static bool HasSelfOrAncestor(Avalonia.Controls.Control c, out Avalonia.Controls.Control owner, out string reason)
    {
        for (Avalonia.Controls.Control? a = c; a != null; a = a.Parent as Avalonia.Controls.Control)
            if (Has(a, out reason)) { owner = a; return true; }
        owner = c;
        reason = "";
        return false;
    }

    private static void OnReasonChanged(Avalonia.Controls.Control c, AvaloniaPropertyChangedEventArgs e)
    {
        bool on = Has(c, out var reason);
        ((IPseudoClasses)c.Classes).Set(":reason", on);
        var st = c.GetValue(StateProperty);
        if (!on)
        {
            st?.Undo();
            return;
        }
        if (st == null) { st = new ReasonState(); c.SetValue(StateProperty, st); }
        if (!st.Hooked)
        {
            st.Hooked = true;
            c.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            c.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            c.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            c.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
            c.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        // 提示与无障碍说明：原因变了就换（先撤销旧的覆盖）
        st.Tip?.Dispose();
        st.Tip = c.SetValue(ToolTip.TipProperty, ReasonTip(reason), BindingPriority.Animation);
        st.Help?.Dispose();
        st.Help = c.SetValue(AutomationProperties.HelpTextProperty, reason, BindingPriority.Animation);
        st.ShowOnDisabled ??= c.SetValue(ToolTip.ShowOnDisabledProperty, true, BindingPriority.Animation);
        st.ShowDelay ??= c.SetValue(ToolTip.ShowDelayProperty, TipDelayMs, BindingPriority.Animation);
        st.BetweenDelay ??= c.SetValue(ToolTip.BetweenShowDelayProperty, TipBetweenMs, BindingPriority.Animation);
        // 第 3 层：命令换成守卫命令；TextBox 只读
        st.Command ??= c switch
        {
            Button b => b.SetValue(Button.CommandProperty, new GuardCommand(b), BindingPriority.Animation),
            MenuItem m => m.SetValue(MenuItem.CommandProperty, new GuardCommand(m), BindingPriority.Animation),
            _ => null,
        };
        if (c is TextBox t) st.ReadOnly ??= t.SetValue(TextBox.IsReadOnlyProperty, true, BindingPriority.Animation);
    }

    /// <summary>带原因时代替控件原来的命令：能执行（控件保持可聚焦、不变成真禁用），执行 = 说明原因。</summary>
    private sealed class GuardCommand : ICommand
    {
        private readonly WeakReference<Avalonia.Controls.Control> _owner;
        public GuardCommand(Avalonia.Controls.Control owner) => _owner = new(owner);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (_owner.TryGetTarget(out var c) && Has(c, out var reason)) Notify(c, reason);
        }
    }

    private static void OnButtonClick(Button b, RoutedEventArgs e)
    {
        if (!HasSelfOrAncestor(b, out var owner, out var reason)) return;
        e.Handled = true;
        Notify(owner, reason);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control c || !Has(c, out var reason)) return;
        e.Handled = true;
        Notify(c, reason);
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control c && Has(c, out _)) e.Handled = true;
    }

    /// <summary>
    /// 这个键会不会操作控件：Enter、Space 对所有控件；选择 / 数值类控件另加方向键、Home / End、PageUp / PageDown、F4（Alt+↓ 打开下拉也在内）。
    /// 带 Ctrl / ⌘ 的组合键放行（快捷键由窗口处理，即使穿过来按到按钮上，第 2、3 层也会拦住）。
    /// </summary>
    public static bool IsOperatingKey(Avalonia.Controls.Control c, KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return false;
        if (e.Key is Key.Enter or Key.Space) return true;
        if (c is SelectingItemsControl or RangeBase or NumericUpDown)
            return e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.F4;
        return false;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control c || !Has(c, out var reason) || !IsOperatingKey(c, e)) return;
        e.Handled = true;
        Notify(c, reason);
    }

    /// <summary>Button 在 Space 抬起时执行（ClickMode.Release），所以抬起也要拦。</summary>
    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is Avalonia.Controls.Control c && Has(c, out _) && IsOperatingKey(c, e)) e.Handled = true;
    }

    /// <summary>文字输入（TextBox 已只读；ComboBox / ListBox 的按字母选择）。</summary>
    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control c || !Has(c, out var reason)) return;
        e.Handled = true;
        Notify(c, reason);
    }

    private static WeakReference<Avalonia.Controls.Control>? _lastNotified;
    private static long _lastNotifyMs;

    /// <summary>
    /// 说明原因：交给 toast 宿主；没有宿主（M1）时打开原因提示。提示要等这次按下处理完再打开（ToolTip 服务在按下时会关掉提示）。
    /// 一次操作可能经过好几层拦截（按键 + 点击事件 + 守卫命令），同一个控件 300 ms 内只说明一次。
    /// 控件不在界面上时（例如没展开的子菜单里的菜单项被 HotKey 触发）没法开提示：不开（开了会抛异常），只交给宿主。
    /// </summary>
    public static void Notify(Avalonia.Controls.Control c, string reason)
    {
        var now = Environment.TickCount64;
        if (_lastNotified != null && _lastNotified.TryGetTarget(out var last) && ReferenceEquals(last, c) && now - _lastNotifyMs < 300) return;
        _lastNotified = new WeakReference<Avalonia.Controls.Control>(c);
        _lastNotifyMs = now;
        if (ReasonRequested is { } handler) { handler(c, reason); return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(c) == null || !c.IsEffectivelyVisible) return;
            try { ToolTip.SetIsOpen(c, true); }
            catch (InvalidOperationException) { /* 没有可用的弹出层（控件刚离开界面）：不开提示 */ }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 原因提示：锁图标 14 px + 原因（样稿 .pr-tip-ic）。直接给出 ToolTip 实例并套 PrTip 主题，
    /// 所以 M1 里不依赖全局的 ToolTip 默认主题（M2 起 PrTip 才成为默认）。
    /// </summary>
    public static ToolTip ReasonTip(string reason)
    {
        var tip = new ToolTip
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new IconView { Icon = "lock", Size = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        tip.Classes.Add("reason");
        if (Application.Current?.TryFindResource("PrTip", out var theme) == true && theme is ControlTheme ct) tip.Theme = ct;
        return tip;
    }
}
