using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// <see cref="MenuEntry"/> → Avalonia 菜单项（PrMenuItem 主题；菜单栏下拉、「›」溢出菜单、«⋯»、右键菜单共用）：
/// 图标 b:Pr.Icon、提示 b:Pr.Hint、红点 b:Pr.Dot、勾选（ToggleType = CheckBox）、类 danger / stop / head、分隔 Separator；
/// 禁用原因 b:Pr.Reason（锁图标提示，点击 toast 原因，命令换成说明原因）。
/// 分隔线只画在两组菜单项之间：开头、结尾和连续的分隔线去掉（某一组按状态整组没有时，不会留下两条挨着的线）。
/// 打开着的菜单每秒、每次动作后重新生成（Views/Shell/PopupMenus）：<see cref="Patch"/> 结构不变时原地改每一项（键盘停着的那一项、
/// 悬停的那一项都还在），结构变了才整份换掉。
/// </summary>
public static class MenuItems
{
    /// <summary>菜单项的种类（原地修补时比较结构：种类和顺序都一样才逐项改）。</summary>
    private enum Kind { Separator, Head, Item }

    /// <summary>
    /// 每个菜单项自己的 <see cref="MenuRun"/>（不从 mi.Command 取：带原因时 Pr.Reason 以 Animation 优先级把 Command 换成守卫命令，
    /// 取到的是守卫命令 —— 那样原地修补换不到新的动作，原因清掉之后执行的是上一次不带原因时的动作）。
    /// </summary>
    private static readonly ConditionalWeakTable<MenuItem, MenuRun> Runs = new();

    public static List<object> Build(IReadOnlyList<MenuEntry> entries)
    {
        var list = new List<object>(entries.Count);
        foreach (var (kind, e) in Shape(entries)) list.Add(Create(kind, e));
        return list;
    }

    /// <summary>
    /// 把 <paramref name="target"/>（菜单的 ItemsSource，同一个集合一直用 —— 11.1 换 ItemsSource 会打开成空白，见 <see cref="DynamicMenuFlyout"/>）
    /// 改成 <paramref name="entries"/>：结构一样时逐项改原来的菜单项，否则清空重建。
    /// </summary>
    public static void Patch(IList<object> target, IReadOnlyList<MenuEntry> entries)
    {
        var shape = Shape(entries);
        bool same = shape.Count == target.Count;
        for (int i = 0; same && i < shape.Count; i++) same = KindOf(target[i]) == shape[i].Kind;
        if (same)
        {
            for (int i = 0; i < shape.Count; i++)
                if (target[i] is MenuItem mi) Apply(mi, shape[i].Kind, shape[i].Entry);
            return;
        }
        target.Clear();
        foreach (var (kind, e) in shape) target.Add(Create(kind, e));
    }

    private static List<(Kind Kind, MenuEntry Entry)> Shape(IReadOnlyList<MenuEntry> entries)
    {
        var list = new List<(Kind, MenuEntry)>(entries.Count);
        foreach (var e in entries)
        {
            if (e.IsSeparator)
            {
                if (list.Count > 0 && list[^1].Item1 != Kind.Separator) list.Add((Kind.Separator, e));
                continue;
            }
            list.Add((e.Head != null ? Kind.Head : Kind.Item, e));
        }
        if (list.Count > 0 && list[^1].Item1 == Kind.Separator) list.RemoveAt(list.Count - 1);
        return list;
    }

    private static Kind? KindOf(object o) => o switch
    {
        Separator => Kind.Separator,
        MenuItem m when m.Classes.Contains("head") => Kind.Head,
        MenuItem => Kind.Item,
        _ => null,
    };

    private static object Create(Kind kind, MenuEntry e)
    {
        if (kind == Kind.Separator) return new Separator();
        var mi = new MenuItem();
        if (kind == Kind.Head)
        {
            mi.Focusable = false;
            mi.Classes.Add("head");
        }
        else
        {
            var run = new MenuRun(mi);
            Runs.Add(mi, run);
            mi.Command = run;
        }
        Apply(mi, kind, e);
        return mi;
    }

    /// <summary>把一项的内容写到菜单项上（新建与原地修补共用：每一样都写，没有的清掉）。</summary>
    private static void Apply(MenuItem mi, Kind kind, MenuEntry e)
    {
        var header = kind == Kind.Head ? e.Head : e.Label;
        if (!Equals(mi.Header, header)) mi.Header = header;
        if (kind == Kind.Head) return;
        PrB.SetIcon(mi, e.Icon);
        PrB.SetHint(mi, e.Hint);
        PrB.SetDot(mi, e.Alarm);
        mi.ToggleType = e.Check ? MenuItemToggleType.CheckBox : MenuItemToggleType.None;
        mi.IsChecked = e.Check;
        mi.Classes.Set("danger", e.Danger);
        mi.Classes.Set("stop", e.Icon == "stop");
        if (Runs.TryGetValue(mi, out var run)) run.Run = e.Run;   // 带原因（守卫命令盖着）时也换：原因清掉时执行的是这一次的动作
        PrB.SetReason(mi, string.IsNullOrWhiteSpace(e.Reason) ? null : UiText.Keep(e.Reason));
    }

    /// <summary>
    /// 菜单项的命令：先关菜单（键盘选的把焦点放回打开它的按钮，样稿 e.detail === 0），再执行（打开对话框时对话框记下的「之前的焦点」是按钮，
    /// 不是关掉之后就没了的菜单项）。执行的内容随刷新换（<see cref="Run"/>），命令对象本身不换（不让菜单项每秒重挂 CanExecuteChanged）。
    /// </summary>
    private sealed class MenuRun : ICommand
    {
        private readonly WeakReference<MenuItem> _item;
        public MenuRun(MenuItem item) => _item = new WeakReference<MenuItem>(item);
        public Action? Run { get; set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            var run = Run;
            if (run == null) return;
            if (_item.TryGetTarget(out var mi)) PopupMenus.BeforeRun(mi);
            run();
        }
    }
}

/// <summary>
/// 打开时才按当前状态生成内容的 MenuFlyout（页签栏「›」全部页签、总览分区「⋯」设备菜单）：在 Opening 里调 <see cref="SetEntries"/>；
/// 打开着时由 PopupMenus 每秒、每次动作后按 <see cref="MenuEntries.Rebuild"/> 原地修补。键盘、点在外面、再点同一个按钮、位置见 PopupMenus。
/// <para>
/// 为什么不在 Opening 里直接给 MenuFlyout 换 ItemsSource（或 Items.Clear / Add）：Avalonia 11.1 里这样做，这一次（以及之后每一次）打开的
/// MenuFlyoutPresenter 都是空的（ItemCount 0，只剩一条 14 px 高的空白）。ItemsSource 必须是从一开始就设好的同一个集合，打开时只改它的内容 ——
/// ObservableCollection 的增删会传到 presenter（第一次和以后每次打开都有菜单项）。
/// </para>
/// </summary>
public sealed class DynamicMenuFlyout : MenuFlyout
{
    private readonly ObservableCollection<object> _entries = new();
    private Func<IReadOnlyList<MenuEntry>?>? _rebuild;
    private PopupMenus.Session? _session;

    public DynamicMenuFlyout()
    {
        ItemsSource = _entries;
        PopupMenus.Prepare(this, Popup);
    }

    /// <summary>换成这些菜单项（在 Opening 里调用；带 <see cref="MenuEntries.Rebuild"/> 的打开着时跟着刷新）。</summary>
    public void SetEntries(IReadOnlyList<MenuEntry> entries)
    {
        _rebuild = (entries as MenuEntries)?.Rebuild;
        MenuItems.Patch(_entries, entries);
    }

    protected override void OnOpening(CancelEventArgs args)
    {
        // 这一下刚在同一个按钮上关掉了它（样稿：再点一次 = 关闭），不再打开
        if (PopupMenus.TakeSuppressed(this)) { args.Cancel = true; return; }
        base.OnOpening(args);
    }

    protected override void OnOpened()
    {
        base.OnOpened();
        _session = PopupMenus.FlyoutOpened(this, Popup, isMenu: true, _entries, _rebuild);
    }

    protected override void OnClosed()
    {
        base.OnClosed();
        PopupMenus.End(_session);
        _session = null;
    }
}
