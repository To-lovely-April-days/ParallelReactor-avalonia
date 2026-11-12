using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Metadata;
using Avalonia.Styling;
using ParallelReactor.Control.Behaviors;
using PrAttach = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 分段 / 页签的一项（样稿 ui.seg / ui.ptabs 的 option）。Value 按字符串比较（同样稿 String(op.value) === String(value)）。
/// 属性可以随时改（红点、计数、原因每秒都可能变）：所在的分段 / 页签就地更新那一个按钮，不重建、不丢键盘焦点。
/// </summary>
public sealed class SegOption : INotifyPropertyChanged
{
    private object? _value;
    private string _label = "";
    private bool _dot;
    private string? _count, _reason, _tip;

    public object? Value { get => _value; set => Set(ref _value, value); }
    public string Label { get => _label; set => Set(ref _label, value ?? ""); }
    /// <summary>红点：只表示未确认的错误报警。</summary>
    public bool Dot { get => _dot; set => Set(ref _dot, value); }
    /// <summary>中性计数（PTabs：未确认的提醒数）。</summary>
    public string? Count { get => _count; set => Set(ref _count, value); }
    /// <summary>禁用原因（Pr.Reason）。</summary>
    public string? Reason { get => _reason; set => Set(ref _reason, value); }
    /// <summary>悬停提示（有原因时显示原因）。</summary>
    public string? Tip { get => _tip; set => Set(ref _tip, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 一组互斥的开关（分段控件与面板标题页签的共同部分）：Options → 按钮（<see cref="PrToggle"/>，主题 <see cref="ItemTheme"/>），
/// 点哪个 Value 就是哪个的 Value。键盘见 <see cref="Roving"/>：面板标题页签只有当前项在 Tab 顺序里、← → Home End 切换；分段控件每段都是 Tab 停靠点。
/// 选项增删改都是增量的：改某项的属性只更新那个按钮，替换 / 插入 / 删除只动对应的按钮（R10：实时更新不重建，焦点不丢）。
/// </summary>
public abstract class ToggleGroup : TemplatedControl
{
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<ToggleGroup, object?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// 选项的来源（视图模型给的一组 <see cref="SegOption"/> 实例，可以绑定）：设了就把 <see cref="Options"/> 换成这一组（同一组对象时不动）。
    /// 模板里的分段要用它 —— 模板什么时候实例化（自动换页签、窗口跨过宽度类、Esc 还原）都直接带着选项，不靠页面事后去填
    /// （M4 复核：设备页「报警与事件」的事件筛选在自动切到页签、改窗口尺寸之后是空的）。一组固定的选项：增删选项换一组新的；
    /// 选项自己的属性（文字、计数、红点）照旧就地更新。null = 不管 <see cref="Options"/>（XAML 里直接写的选项照旧）。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<SegOption>?> OptionsSourceProperty =
        AvaloniaProperty.Register<ToggleGroup, IReadOnlyList<SegOption>?>(nameof(OptionsSource));

    protected ToggleGroup()
    {
        Options.CollectionChanged += OnOptionsChanged;
    }

    /// <summary>选项（XAML 里直接写 &lt;pr:SegOption …/&gt;；或者绑定 <see cref="OptionsSource"/>）。</summary>
    [Content]
    public AvaloniaList<SegOption> Options { get; } = new();

    public object? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="OptionsSourceProperty"/>
    public IReadOnlyList<SegOption>? OptionsSource { get => GetValue(OptionsSourceProperty); set => SetValue(OptionsSourceProperty, value); }

    /// <summary>选项换成来源里的这一组（逐项同一个对象时不动：按钮、焦点都保留）。</summary>
    private void SyncFromSource(IReadOnlyList<SegOption>? src, bool hadSource)
    {
        if (src == null)
        {
            if (hadSource) Options.Clear();
            return;
        }
        bool same = Options.Count == src.Count;
        for (int i = 0; same && i < src.Count; i++) same = ReferenceEquals(Options[i], src[i]);
        if (same) return;
        Options.Clear();
        Options.AddRange(src);
    }

    /// <summary>按钮的主题键（PrSegItem / PrPTab）。</summary>
    protected abstract string ItemTheme { get; }

    /// <summary>
    /// 键盘：true = 只有当前项在 Tab 顺序里（roving tabindex），← → Home End 切到相邻项并打开它（面板标题页签，样稿 ui.ptabs + 外框的方向键）；
    /// false = 每项都是普通按钮、都是 Tab 停靠点，方向键不管（分段控件，样稿 ui.seg：外框的方向键只管 .pr-ptab）。Enter / 空格照常点击。
    /// </summary>
    protected virtual bool Roving => true;

    private Panel? _host;
    private ControlTheme? _theme;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_host != null) foreach (var b in _host.Children.OfType<PrToggle>()) Detach(b);
        _host = e.NameScope.Find<Panel>("PART_Host");
        _host?.Children.Clear();
        _theme = this.TryFindResource(ItemTheme, out var t) ? t as ControlTheme : null;
        if (_host != null) for (int i = 0; i < Options.Count; i++) _host.Children.Add(Create(Options[i]));
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) Sync();
        else if (change.Property == OptionsSourceProperty)
            SyncFromSource(change.GetNewValue<IReadOnlyList<SegOption>?>(), change.GetOldValue<IReadOnlyList<SegOption>?>() != null);
    }

    // 不在树上时不订阅选项的变化（选项对象可能比控件活得久）；回到树上时重新订阅并按选项的现状刷新按钮
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_host == null) return;
        foreach (var b in _host.Children.OfType<PrToggle>())
            if (b.Tag is SegOption op)
            {
                op.PropertyChanged -= OnOptionPropertyChanged;
                op.PropertyChanged += OnOptionPropertyChanged;
                Apply(b, op);
            }
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_host == null) return;
        foreach (var b in _host.Children.OfType<PrToggle>())
            if (b.Tag is SegOption op) op.PropertyChanged -= OnOptionPropertyChanged;
    }

    private static bool Same(object? a, object? b) => string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);

    private PrToggle Create(SegOption op)
    {
        var b = new PrToggle { Theme = _theme, VerticalAlignment = VerticalAlignment.Stretch };
        b.Click += OnItemClick;
        b.KeyDown += OnItemKeyDown;
        Attach(b, op);
        return b;
    }

    private void Attach(PrToggle b, SegOption op)
    {
        if (b.Tag is SegOption old) old.PropertyChanged -= OnOptionPropertyChanged;
        b.Tag = op;
        op.PropertyChanged += OnOptionPropertyChanged;
        Apply(b, op);
    }

    private void Detach(PrToggle b)
    {
        if (b.Tag is SegOption op) op.PropertyChanged -= OnOptionPropertyChanged;
        b.Tag = null;
        b.Click -= OnItemClick;
        b.KeyDown -= OnItemKeyDown;
    }

    /// <summary>把选项的属性写到它的按钮上（就地，按钮实例不变）。提示写本地值，原因（Pr.Reason）以更高优先级盖住它。</summary>
    private static void Apply(PrToggle b, SegOption op)
    {
        if (!Equals(b.Content, op.Label)) b.Content = op.Label;
        PrAttach.SetDot(b, op.Dot);
        PrAttach.SetCount(b, string.IsNullOrEmpty(op.Count) ? null : op.Count);
        PrAttach.SetReason(b, string.IsNullOrEmpty(op.Reason) ? null : op.Reason);
        ToolTip.SetTip(b, string.IsNullOrEmpty(op.Tip) ? null : op.Tip);
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_host == null || sender is not SegOption op) return;
        foreach (var b in _host.Children.OfType<PrToggle>())
            if (ReferenceEquals(b.Tag, op)) Apply(b, op);
        if (e.PropertyName is nameof(SegOption.Value) or nameof(SegOption.Reason)) Sync();
    }

    private void OnOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_host == null) return;
        var kids = _host.Children;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                for (int i = 0; i < e.NewItems.Count; i++) kids.Insert(e.NewStartingIndex + i, Create((SegOption)e.NewItems[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                for (int i = 0; i < e.OldItems.Count; i++)
                {
                    var b = (PrToggle)kids[e.OldStartingIndex];
                    Detach(b);
                    kids.RemoveAt(e.OldStartingIndex);
                }
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems != null:
                // 同一位置换了新对象：沿用原来的按钮（焦点、悬停状态都还在）
                for (int i = 0; i < e.NewItems.Count; i++) Attach((PrToggle)kids[e.NewStartingIndex + i], (SegOption)e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Move when e.OldItems != null:
                for (int i = 0; i < e.OldItems.Count; i++)
                {
                    var b = kids[e.OldStartingIndex];
                    kids.RemoveAt(e.OldStartingIndex);
                    kids.Insert(e.NewStartingIndex, b);
                }
                break;
            default:
                foreach (var b in kids.OfType<PrToggle>()) Detach(b);
                kids.Clear();
                foreach (var op in Options) kids.Add(Create(op));
                break;
        }
        Sync();
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is PrToggle b && b.Tag is SegOption op && !PrAttach.IsLocked(b)) Value = op.Value;
        Sync();
    }

    private void Sync()
    {
        if (_host == null) return;
        var items = _host.Children.OfType<PrToggle>().ToList();
        bool any = items.Any(x => x.Tag is SegOption o && Same(o.Value, Value));
        foreach (var b in items)
        {
            bool on = b.Tag is SegOption op && Same(op.Value, Value);
            b.IsChecked = on;
            b.IsTabStop = !Roving || on || (!any && ReferenceEquals(b, items.FirstOrDefault(x => !PrAttach.IsLocked(x))));
        }
    }

    private void OnItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Roving || _host == null || sender is not PrToggle cur) return;
        var items = _host.Children.OfType<PrToggle>().Where(x => !PrAttach.IsLocked(x)).ToList();
        int i = items.IndexOf(cur);
        if (i < 0 || items.Count == 0) return;
        int j = e.Key switch { Key.Left => i - 1, Key.Right => i + 1, Key.Home => 0, Key.End => items.Count - 1, _ => -99 };
        if (j == -99) return;
        j = (j + items.Count) % items.Count;
        if (items[j].Tag is SegOption op) Value = op.Value;
        items[j].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}

/// <summary>
/// 中性分段控件（样稿 ui.seg / .pr-seg）：高 34、bg-sunken 底、1 px line 边、内边距 2、段间 2；
/// 段 14 px 500 t-3，悬停 bg-press，当前段 a-soft + a-ink（唯一的强调色）。段里的红点只给未确认的错误报警。
/// 键盘同样稿（每段是普通按钮）：每段都是 Tab 停靠点（带原因的段也是，同样稿 aria-disabled），方向键不切换。
/// </summary>
public sealed class SegControl : ToggleGroup
{
    protected override string ItemTheme => "PrSegItem";

    protected override bool Roving => false;
}

/// <summary>
/// 面板标题位置的文字页签（样稿 ui.ptabs / .pr-ptabs）：16 px，当前页签 t-1 600 + 2 px a-tab 下划线；
/// 红点 = 有未确认的错误报警；灰色数字 = 未确认的提醒（不用红色）。切换只换内容（面板的放大 / 关闭、焦点保留由宿主负责）。
/// </summary>
public sealed class PTabs : ToggleGroup
{
    protected override string ItemTheme => "PrPTab";
}

/// <summary>
/// 芯片（样稿 ui.chip / .pr-chip）：可开关的 32 px 小按钮，开 = a-soft + a-line 边 + a-ink 字。
/// 图标、数字用附加属性 b:Pr.Icon / b:Pr.Count；禁用原因 b:Pr.Reason（Toggle() 里检查，UI 自动化 / HotKey 也翻不动）。主题 PrChip。
/// </summary>
public sealed class Chip : ToggleButton, IReasonGuarded
{
    protected override Type StyleKeyOverride => typeof(Chip);

    protected override void Toggle()
    {
        if (PrAttach.IsLocked(this)) return;
        base.Toggle();
    }
}
