using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 打开着的菜单 / 弹出框（样稿只有一个 #pr-menu：shell.menu / shell.popover / closeMenu，shell.js）。同一时间只开一个，下面这些统一在这里：
/// <list type="bullet">
/// <item>键盘（样稿 keydown）：菜单打开时 ↑ ↓ 循环、Home / End 到两头，焦点不在菜单里时 ↓ 进第一项、↑ 进最后一项（带原因的项也在其中，按 Enter 只说明原因）；
///   空格同 Enter；用键盘（Enter / 空格）打开的菜单焦点进第一项；Esc 只关菜单 / 弹出框（不再往下走对话框 → 放大的面板 → 浮层），焦点回到打开它的按钮；
///   用键盘选的项先把焦点放回按钮再执行（样稿 e.detail === 0）。键盘移到的项是 :focus-visible（内侧 2 px a-d 环，Styles/Theme/Menus.axaml）。
///   菜单栏：每个菜单一个 Tab 停靠点，Enter / 空格 / ↓ 打开并进第一项，再按 Enter / 空格关掉；← → 换到相邻的菜单（Windows 的习惯，样稿没有）。</item>
/// <item>指针：点在外面 —— 关掉，同一下照常落到下面（Popup.OverlayDismissEventPassThrough：一下就换到别的菜单、点到急停）；
///   再点打开它的那个按钮 = 关掉（不会又打开，样稿 wasOpen）。</item>
/// <item>刷新：打开着的菜单每秒、每次动作后按 <see cref="MenuEntries.Rebuild"/> 重新生成，原地修补（MenuItems.Patch：键盘停在哪一项还在哪一项）。
///   重置视图（截图的每一张）、打开对话框时关掉（样稿 openDialog → closeMenu）。关掉时清掉菜单的选中（再打开不带着上次的悬停底色）。</item>
/// <item>位置（样稿 placeMenu）：横向夹在窗口里（左右各留 4），下面放不下（留 4）时翻到锚点 / 指针上面，最高窗口高 − 16（多的滚动）；
///   屏幕边上的再交给 Avalonia 平移 / 缩小（<see cref="ScreenEdge"/>）。</item>
/// <item>打开它的按钮画成按下的样子（类 menu-open，样稿 is-menu-open；「设备信息」锚在 «⋯» 上，它开着时 «⋯» 也是按下的）。</item>
/// </list>
/// 无头截图里弹出层是窗口里的覆盖层（Avalonia.Headless），桌面上是单独的原生窗口：桌面上的焦点、位置要实机核对（见 doc §8）。
/// </summary>
public static class PopupMenus
{
    /// <summary>一次打开（菜单栏的一个下拉、一个 MenuFlyout / 弹出框、一个右键菜单）。</summary>
    public sealed class Session
    {
        internal Session(object owner, Avalonia.Controls.Control? anchor, bool isMenu, bool isContext)
        {
            Owner = owner;
            Anchor = anchor;
            IsMenu = isMenu;
            IsContext = isContext;
        }

        /// <summary>弹出的那个东西：PrFlyout / DynamicMenuFlyout / ContextMenu / 菜单栏的顶层 MenuItem。</summary>
        public object Owner { get; }
        /// <summary>打开它的按钮（右键菜单 = 右键的那个元素，焦点只在菜单里时才回到它）。</summary>
        public Avalonia.Controls.Control? Anchor { get; }
        /// <summary>菜单（↑ ↓ 在菜单项之间移动）；false = 自定义内容的弹出框（「+」、图例、设备信息）。</summary>
        public bool IsMenu { get; }
        public bool IsContext { get; }
        internal Func<ItemsControl?> ItemsOf = () => null;
        internal Func<Visual?> ContentOf = () => null;
        /// <summary>菜单项的容器（MenuItem / ContextMenu / MenuFlyoutPresenter；弹出框没有菜单项时是 null）。</summary>
        public ItemsControl? Host => ItemsOf();
        /// <summary>弹出层的内容（焦点在不在里面；量位置）。</summary>
        public Visual? Root => ContentOf();
        internal Action CloseAction = () => { };
        internal IList<object>? Entries;
        internal Func<IReadOnlyList<MenuEntry>?>? Rebuild;
    }

    /// <summary>当前打开的那一个（没有为 null）。</summary>
    public static Session? Current { get; private set; }

    /// <summary>上一次输入是 Enter / 空格（按钮在 Enter 按下、空格抬起时打开弹出）：用键盘打开的菜单焦点进第一项、用键盘选的项焦点回到按钮。指针按下时清掉。</summary>
    private static bool _keyActivation;

    /// <summary>这一下按在打开它的按钮上、关掉的正是这个按钮自己的弹出（Button.Flyout）：这一下的 Click 不再打开它（再点一次 = 关闭）。</summary>
    private static object? _suppress;

    private static ShellVm? _shell;

    // ———————————————————— 挂到窗口 / 外壳 ————————————————————

    /// <summary>窗口的键盘 / 指针处理（隧道阶段：比菜单、按钮、资源管理器的树自己的处理先看到）。MainWindow 构造时调用一次。</summary>
    public static void Attach(TopLevel top)
    {
        top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        top.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>已经挂上键盘处理的弹出层内容（同一个 MenuFlyout / 弹出框反复打开，只挂一次）。</summary>
    private static readonly ConditionalWeakTable<Interactive, object> Hooked = new();

    /// <summary>
    /// 弹出层里的按键、按下也要先经过这里：Flyout / ContextMenu 的 Popup 不在可视树里（逻辑父元素是锚点），焦点在弹出层里时按键的路由到 Popup 就停了，
    /// 到不了窗口 —— 隧道处理挂在弹出层的内容上（菜单项之前、11.1 的菜单处理之前）。按下只清掉「键盘打开」：键盘打开、再用鼠标选的项
    /// 不把焦点（和焦点环）放回按钮（样稿 e.detail 1 不 focus）。菜单栏的下拉在顶层项的模板里，按键、按下照常经过窗口。
    /// </summary>
    private static void Hook(Interactive? content)
    {
        if (content == null || Hooked.TryGetValue(content, out _)) return;
        Hooked.Add(content, true);
        content.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        content.AddHandler(InputElement.PointerPressedEvent, (_, _) => _keyActivation = false, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>跟着这个外壳刷新打开着的菜单（数据、布局、选中、页签变了），重置视图 / 打开对话框时关掉。null = 不再跟。</summary>
    public static void Watch(ShellVm? shell)
    {
        if (ReferenceEquals(_shell, shell)) return;
        if (_shell is { } old)
        {
            old.Fleet.Changed -= OnChanged;
            old.Layout.Changed -= OnChanged;
            old.Selection.Changed -= OnChanged;
            old.Tabs.Tabs.CollectionChanged -= OnTabsChanged;
            old.LayoutReset -= OnReset;
            old.Dialogs.PropertyChanged -= OnDialogs;
        }
        _shell = shell;
        CloseAll();
        if (shell == null) return;
        shell.Fleet.Changed += OnChanged;
        shell.Layout.Changed += OnChanged;
        shell.Selection.Changed += OnChanged;
        shell.Tabs.Tabs.CollectionChanged += OnTabsChanged;
        shell.LayoutReset += OnReset;
        shell.Dialogs.PropertyChanged += OnDialogs;
    }

    /// <summary>窗口关了：还在跟这个外壳就不再跟。</summary>
    public static void Unwatch(ShellVm shell)
    {
        if (ReferenceEquals(_shell, shell)) Watch(null);
    }

    private static void OnChanged(object? sender, EventArgs e) => Refresh();
    private static void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();
    private static void OnReset(object? sender, EventArgs e) => CloseAll();

    private static void OnDialogs(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DialogHostVm.IsOpen) && _shell?.Dialogs.IsOpen == true) CloseAll();
    }

    /// <summary>关掉打开着的菜单 / 弹出框（不动焦点）。</summary>
    public static void CloseAll()
    {
        if (Current is { } s) Close(s, focus: false);
    }

    // ———————————————————— 打开 / 关闭 ————————————————————

    /// <summary>
    /// 屏幕边上的调整（桌面上的原生弹出窗口超出屏幕时）：只平移，还放不下再缩小 —— 不翻转（位置已经按样稿在窗口里摆好，见 <see cref="Place"/>）。
    /// </summary>
    internal const PopupPositionerConstraintAdjustment ScreenEdge =
        PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY
        | PopupPositionerConstraintAdjustment.ResizeX | PopupPositionerConstraintAdjustment.ResizeY;

    /// <summary>PrFlyout / DynamicMenuFlyout 的共同设置：点在外面关掉时这一下照常落到下面；不自己抢焦点（焦点由这里管）；屏幕边上见 <see cref="ScreenEdge"/>。</summary>
    internal static void Prepare(PopupFlyoutBase flyout, Popup popup)
    {
        flyout.ShowMode = FlyoutShowMode.Transient;
        flyout.PlacementConstraintAdjustment = ScreenEdge;
        popup.OverlayDismissEventPassThrough = true;
    }

    /// <summary>
    /// 菜单栏顶层项的下拉（PrMenuBarItem 模板里的 PART_Popup，MenuBarView 在模板套上时调用）：打开之后同样按样稿 placeMenu 摆 ——
    /// 平常就在「设备」等下面（偏移照模板），比窗口还高时（「窗口」里页签很多）最高窗口高 − 16、翻到上面（上沿 4，盖住菜单栏，同样稿）。
    /// </summary>
    internal static void PrepareBar(MenuItem top, Popup popup)
    {
        if (Hooked.TryGetValue(popup, out _)) return;
        Hooked.Add(popup, true);
        double h0 = popup.HorizontalOffset, v0 = popup.VerticalOffset;   // 模板里的偏移（Place 会改 Popup 的偏移，每次从这里算）
        popup.Opened += (_, _) => Place(popup, top, popup.Placement, null, h0, v0);
        // 关掉时偏移回到模板里的（下次打开先放在平常的位置，不先闪到上次翻上去的地方）
        popup.Closed += (_, _) => { popup.HorizontalOffset = h0; popup.VerticalOffset = v0; };
    }

    internal static Session FlyoutOpened(PopupFlyoutBase flyout, Popup popup, bool isMenu, IList<object>? entries, Func<IReadOnlyList<MenuEntry>?>? rebuild)
    {
        var anchor = flyout.Target;
        var s = new Session(flyout, anchor, isMenu, isContext: false)
        {
            ItemsOf = () => popup.Child as ItemsControl,
            ContentOf = () => popup.Child,
            CloseAction = flyout.Hide,
            Entries = entries,
            Rebuild = rebuild,
        };
        Begin(s);
        Hook(popup.Child);
        if (anchor != null) Place(popup, anchor, flyout.Placement, null, flyout.HorizontalOffset, flyout.VerticalOffset);
        if (isMenu && _keyActivation) FocusEdge(s, first: true);
        return s;
    }

    /// <summary>右键菜单（Views/Shell/ContextMenus）打开之后：登记、按样稿的规则摆好位置。</summary>
    internal static Session ContextOpened(ContextMenu menu, Avalonia.Controls.Control place, Point? pointer, IList<object> entries, Func<IReadOnlyList<MenuEntry>?>? rebuild)
    {
        var s = new Session(menu, place, isMenu: true, isContext: true)
        {
            ItemsOf = () => menu,
            ContentOf = () => menu,
            CloseAction = menu.Close,
            Entries = entries,
            Rebuild = rebuild,
        };
        Begin(s);
        Hook(menu);
        menu.AddHandler(MenuBase.ClosedEvent, (_, _) => End(s));
        if (menu.Parent is Popup popup) Place(popup, place, menu.Placement, pointer, menu.HorizontalOffset, menu.VerticalOffset);
        return s;
    }

    /// <summary>菜单栏的一个下拉打开了（MenuBarView 的 SubmenuOpened）。</summary>
    internal static Session BarOpened(Menu bar, MenuItem top, IList<object> entries, IReadOnlyList<MenuEntry> built)
    {
        var s = new Session(top, top, isMenu: true, isContext: false)
        {
            ItemsOf = () => top,
            ContentOf = () => top.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child,
            CloseAction = bar.Close,
            Entries = entries,
            Rebuild = (built as MenuEntries)?.Rebuild,
        };
        Begin(s);
        return s;
    }

    /// <summary>菜单栏的一个下拉关了（IsSubMenuOpen 变 false）。</summary>
    internal static void BarClosed(MenuItem top)
    {
        if (Current is { } s && ReferenceEquals(s.Owner, top)) End(s);
    }

    private static void Begin(Session s)
    {
        var old = Current;
        Current = s;
        // 同一时间只开一个（样稿 shell.menu / shell.popover 先 closeMenu）：键盘打开的、程序打开的不经过「点在外面」
        if (old != null && !ReferenceEquals(old.Owner, s.Owner)) Close(old, focus: false);
        if (!s.IsContext && s.Owner is PopupFlyoutBase) s.Anchor?.Classes.Add("menu-open");
    }

    /// <summary>
    /// 关掉之后（不论谁关的：选了项、点在外面、Esc、程序）。菜单的选中清掉：11.1 只在指针移出菜单项时清，Esc / 程序关掉时悬停或键盘停着的那一项
    /// 还是 :selected —— 同一个 MenuFlyout 的菜单项原地修补、下次打开还是这些对象，会画着悬停底色出来（样稿的底色只跟 :hover）。
    /// </summary>
    internal static void End(Session? s)
    {
        if (s == null) return;
        if (s.Owner is PopupFlyoutBase) s.Anchor?.Classes.Remove("menu-open");
        if (s.IsMenu && s.Host is SelectingItemsControl host) host.SelectedIndex = -1;
        if (ReferenceEquals(Current, s)) Current = null;
    }

    /// <summary>
    /// 关掉；<paramref name="focus"/> 时先把焦点放到按钮上再关（样稿 a.focus()，Tab 方式：画焦点环）—— 反过来的话，焦点所在的菜单项离开界面时
    /// 11.1 先把焦点（不带 :focus-visible）交给弹出层的逻辑父元素，也就是这个按钮，再聚焦它就不会变了。
    /// </summary>
    private static void Close(Session s, bool focus)
    {
        var anchor = s.Anchor;
        if (focus && anchor != null && anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled) anchor.Focus(NavigationMethod.Tab);
        s.CloseAction();
        End(s);
    }

    /// <summary>
    /// 菜单项执行之前（MenuItems 的命令）：先关菜单；用键盘选的把焦点放回打开它的按钮 / 右键的目标（样稿：e.detail === 0 时先 anchor.focus() 再 run）。
    /// 指针选的不动焦点（悬停时菜单项自己拿了焦点，看焦点分不出是不是键盘）。
    /// </summary>
    internal static void BeforeRun(MenuItem mi)
    {
        if (Current is not { } s || !Inside(s, mi)) return;
        Close(s, focus: _keyActivation);
    }

    /// <summary>这一下刚在打开它的按钮上关掉了这个弹出：取消这次打开（PrFlyout / DynamicMenuFlyout 的 OnOpening）。</summary>
    internal static bool TakeSuppressed(object flyout)
    {
        if (!ReferenceEquals(_suppress, flyout)) return false;
        _suppress = null;
        return true;
    }

    // ———————————————————— 刷新 ————————————————————

    /// <summary>
    /// 打开着的菜单按当前状态重新生成（原地修补）。结构变了（例如页签多了一个）整份换掉时，键盘焦点回到同名的那一项（没有就同一位置）。
    /// </summary>
    public static void Refresh()
    {
        if (Current is not { Rebuild: { } rebuild, Entries: { } target } s) return;
        var entries = rebuild();
        if (entries == null) { Close(s, focus: false); return; }
        var focused = Focusables(s).FirstOrDefault(m => m.IsFocused);
        int index = focused == null ? -1 : Focusables(s).IndexOf(focused);
        var header = focused?.Header;
        MenuItems.Patch(target, entries);
        if (focused == null || focused.GetVisualRoot() != null) return;
        Lay(s.Root, s.Anchor);   // 新的菜单项排出来才能聚焦
        var items = Focusables(s);
        var again = items.FirstOrDefault(m => Equals(m.Header, header)) ?? (index >= 0 && index < items.Count ? items[index] : null);
        again?.Focus(NavigationMethod.Directional);
    }

    // ———————————————————— 键盘 ————————————————————

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) _keyActivation = true;
        if (e.Handled || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        var src = e.Source as Avalonia.Controls.Control;
        if (src is MenuItem { Parent: Menu bar } head && BarKey(bar, head, e.Key)) { e.Handled = true; return; }
        if (Current is not { } s) return;
        bool inside = Inside(s, src);
        switch (e.Key)
        {
            // Esc 只关这一个（样稿 keydown：菜单开着时 closeMenu + 焦点回到按钮，return）；右键菜单没有按钮，焦点在菜单里时回到右键的目标
            case Key.Escape:
                Close(s, focus: !s.IsContext || inside);
                e.Handled = true;
                break;
            case Key.Down or Key.Up or Key.Home or Key.End when s.IsMenu:
                Move(s, src, e.Key);
                e.Handled = true;
                break;
            case Key.Left or Key.Right when s.Owner is MenuItem top && top.Parent is Menu bar2 && (inside || ReferenceEquals(src, top)):
                Switch(bar2, top, e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
            // 空格同 Enter（11.1 的菜单只认 Enter）；带原因的项交给 Pr.Reason（说明原因，菜单不关）
            case Key.Space when inside && src is MenuItem { HasSubMenu: false } mi && !PrB.IsLocked(mi):
                mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (ReferenceEquals(Current, s)) Close(s, focus: true);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 焦点在菜单栏的顶层项上：Enter / 空格 / ↓ 打开并进第一项（↑ 进最后一项）；已经打开时 Enter / 空格关掉、焦点留在它上面（样稿再点一次 = 关闭）。
    /// </summary>
    private static bool BarKey(Menu bar, MenuItem head, Key key)
    {
        switch (key)
        {
            case Key.Enter or Key.Space when head.IsSubMenuOpen:
                bar.Close();
                head.Focus(NavigationMethod.Tab);
                return true;
            case Key.Enter or Key.Space or Key.Down when !head.IsSubMenuOpen:
                OpenBar(bar, head, first: true);
                return true;
            case Key.Up when !head.IsSubMenuOpen:
                OpenBar(bar, head, first: false);
                return true;
            default:
                return false;
        }
    }

    private static void OpenBar(Menu bar, MenuItem head, bool first)
    {
        bar.Open();
        head.Open();   // SubmenuOpened：MenuBarView 生成菜单项、登记
        if (Current is { } s && ReferenceEquals(s.Owner, head)) FocusEdge(s, first);
    }

    private static void Switch(Menu bar, MenuItem top, int dir)
    {
        var heads = bar.Items.OfType<MenuItem>().ToList();
        int i = heads.IndexOf(top);
        if (i < 0 || heads.Count < 2) return;
        var next = heads[(i + dir + heads.Count) % heads.Count];
        top.Close();
        OpenBar(bar, next, first: true);
    }

    /// <summary>样稿 keydown 的 ↑ ↓ Home End：在所有可聚焦的菜单项（含带原因的）之间循环；焦点不在菜单里时 ↓ 进第一项、↑ 进最后一项。</summary>
    private static void Move(Session s, Avalonia.Controls.Control? src, Key key)
    {
        var items = Focusables(s);
        if (items.Count == 0) { FocusEdge(s, key is Key.Down or Key.Home); return; }
        int i = src is MenuItem m ? items.IndexOf(m) : -1;
        int j = key switch
        {
            Key.Home => 0,
            Key.End => items.Count - 1,
            Key.Down => (i + 1) % items.Count,
            _ => i <= 0 ? items.Count - 1 : i - 1,
        };
        items[j].Focus(NavigationMethod.Directional);
    }

    /// <summary>焦点进第一项 / 最后一项（Directional：:focus-visible，画焦点环）。刚打开、菜单项还没排出来时先排一次版，还没有就等排完版。</summary>
    private static void FocusEdge(Session s, bool first)
    {
        var items = Focusables(s);
        if (items.Count == 0)
        {
            Lay(s.Root, s.Anchor);
            items = Focusables(s);
        }
        if (items.Count > 0) { (first ? items[0] : items[^1]).Focus(NavigationMethod.Directional); return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Current, s)) return;
            var later = Focusables(s);
            if (later.Count > 0) (first ? later[0] : later[^1]).Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>菜单里可聚焦的菜单项（按顺序；标题项、分隔线不在其中，带原因的在）。上次打开时留下、还没挂回界面的容器不算。</summary>
    public static List<MenuItem> Focusables(Session s)
    {
        var list = new List<MenuItem>();
        if (s.Host is not { } host) return list;
        for (int i = 0; i < host.ItemCount; i++)
            if (host.ContainerFromIndex(i) is MenuItem m && m.Focusable && m.IsEffectivelyEnabled && m.IsEffectivelyVisible && m.GetVisualRoot() != null) list.Add(m);
        return list;
    }

    /// <summary>
    /// 刚打开的弹出层先排一次版：原生弹出层（PopupRoot）自己排，窗口里的覆盖层（无头）随窗口排 —— 打开之后到下一次排版之前，
    /// 弹出层的内容还没挂到界面上（量不到尺寸、菜单项聚焦不了）。
    /// </summary>
    private static void Lay(Visual? content, Visual? anchor)
    {
        (content as Layoutable)?.UpdateLayout();
        if (content == null || content.GetVisualRoot() == null || (content as Layoutable)?.IsArrangeValid != true) (anchor as Layoutable)?.UpdateLayout();
    }

    private static bool Inside(Session s, Avalonia.Controls.Control? c) =>
        c != null && s.Root is { } root && (ReferenceEquals(root, c) || root.IsVisualAncestorOf(c));

    // ———————————————————— 指针 ————————————————————

    /// <summary>
    /// 按下：清掉「键盘打开」。按在遮罩上（LightDismissOverlayLayer：弹出还开着，Popup 马上关掉它、再把这一下交给下面的元素）而下面正是打开它的按钮、
    /// 关掉的是这个按钮自己的 Button.Flyout 时，记下「这一下不再打开」—— 否则这一下的 Click 又把它打开（样稿：再点一次 = 关闭）。
    /// 「设备信息」锚在 «⋯» 上但不是 «⋯» 的 Flyout：点 «⋯» 关掉它并打开菜单（样稿 same = false）。
    /// </summary>
    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _keyActivation = false;
        if (BarHeadOf(e.Source) is { } head && head.Parent is Menu bar && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            BarClick(bar, head);
            e.Handled = true;
            return;
        }
        if (e.Source is not LightDismissOverlayLayer layer) return;
        _suppress = null;
        if (Current is not { Anchor: Button b } s || !ReferenceEquals(b.Flyout, s.Owner) || layer.GetVisualRoot() is not InputElement root) return;
        var hit = root.InputHitTest(e.GetPosition(root), x => !ReferenceEquals(x, layer)) as Visual;
        if (hit != null && (ReferenceEquals(hit, b) || b.IsVisualAncestorOf(hit))) _suppress = s.Owner;
    }

    /// <summary>
    /// 点菜单栏的顶层项（样稿 data-menu 按钮）：点开着的那个 = 关掉，开着别的 = 一下换过来，都没开 = 打开（焦点不动）。
    /// 11.1 的菜单开着时悬停就换菜单、再点一下又关掉 —— 样稿悬停不换（MenuBarView 拦掉顶层项的悬停），所以点击在这里自己处理。
    /// </summary>
    private static void BarClick(Menu bar, MenuItem head)
    {
        if (head.IsSubMenuOpen) { bar.Close(); return; }
        foreach (var other in bar.Items.OfType<MenuItem>().Where(m => m.IsSubMenuOpen && !ReferenceEquals(m, head)).ToList()) other.Close();
        bar.Open();
        head.Open();
    }

    /// <summary>按在菜单栏的哪个顶层项上（项本身或项里的字；它的下拉在弹出层里，不算）。</summary>
    private static MenuItem? BarHeadOf(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is MenuItem { Parent: Menu } m) return m;
        return null;
    }

    /// <summary>这一下结束（按钮在抬起时 Click）：不再拦。</summary>
    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _suppress = null;

    // ———————————————————— 位置 ————————————————————

    /// <summary>
    /// 样稿 placeMenu：锚定的菜单在锚点下沿 + 2（右对齐的「›」«⋯»「设备信息」以锚点右沿对齐），指针菜单在指针处；
    /// 横向夹在 [4, 窗口宽 − 宽 − 4]；下沿超出窗口高 − 4 时翻到上面（锚点上沿 − 2 − 高 / 指针 − 高，不小于 4）。
    /// 最高 = 窗口高 − 16（样稿 .pr-menu max-height: calc(100% − 16px)，多的在里面滚动）：先限高再量尺寸。
    /// 按锚点和弹出层的尺寸直接算出偏移（不看 Avalonia 先放在哪：它按屏幕调整，样稿按外框）；改了偏移的 Popup 会重新定位。
    /// </summary>
    internal static void Place(Popup popup, Avalonia.Controls.Control anchor, PlacementMode mode, Point? pointer, double h0, double v0)
    {
        if (popup.Child is not Layoutable child || TopLevel.GetTopLevel(anchor) is not { } top) return;
        double maxH = Math.Max(0, top.ClientSize.Height - 16);
        if (Math.Abs(child.MaxHeight - maxH) > 0.01) child.MaxHeight = maxH;
        Lay(child, anchor);
        var size = child.DesiredSize;
        var at = anchor.TranslatePoint(default, top);
        if (size.Width <= 0 || size.Height <= 0 || at == null) return;
        var r = Geometry(new Rect(at.Value, anchor.Bounds.Size), mode, pointer, size, h0, v0, top.ClientSize);
        if (Math.Abs(popup.HorizontalOffset - r.H) > 0.01) popup.HorizontalOffset = r.H;
        if (Math.Abs(popup.VerticalOffset - r.V) > 0.01) popup.VerticalOffset = r.V;
    }

    /// <summary>
    /// <see cref="Place"/> 的算法（纯函数，单元测试用）：锚点 / 指针（窗口坐标）、弹出层尺寸、原来的偏移、窗口尺寸 → 新的偏移（H, V）
    /// 与弹出层在窗口里的位置（X, Y）。
    /// </summary>
    public static (double H, double V, double X, double Y) Geometry(Rect anchor, PlacementMode mode, Point? pointer, Size size, double h0, double v0, Size window)
    {
        double x, y, up;
        if (mode == PlacementMode.Pointer && pointer is { } p)
        {
            x = p.X + h0; y = p.Y + v0; up = p.Y - size.Height;
        }
        else
        {
            x = (mode == PlacementMode.BottomEdgeAlignedRight ? anchor.Right - size.Width : anchor.Left) + h0;
            y = anchor.Bottom + v0;
            up = anchor.Top - v0 - size.Height;
        }
        double nx = Math.Max(4, Math.Min(x, window.Width - size.Width - 4));
        double ny = y + size.Height > window.Height - 4 ? Math.Max(4, up) : y;
        return (h0 + nx - x, v0 + ny - y, nx, ny);
    }
}

/// <summary>
/// 自定义内容的弹出框（样稿 shell.popover：「+」新页签、资源管理器 ⓘ 图例、«⋯» › 设备信息）：PrPopover 主题（默认 FlyoutPresenter 主题）。
/// 打开时不动焦点，Esc 关掉并把焦点放回打开它的按钮；点在外面关掉、这一下照常落到下面；再点同一个按钮 = 关掉；位置同样稿 placeMenu（见 PopupMenus）。
/// </summary>
public class PrFlyout : Flyout
{
    private PopupMenus.Session? _session;

    public PrFlyout() => PopupMenus.Prepare(this, Popup);

    protected override void OnOpening(CancelEventArgs args)
    {
        if (PopupMenus.TakeSuppressed(this)) { args.Cancel = true; return; }
        base.OnOpening(args);
    }

    protected override void OnOpened()
    {
        base.OnOpened();
        _session = PopupMenus.FlyoutOpened(this, Popup, isMenu: false, null, null);
    }

    protected override void OnClosed()
    {
        base.OnClosed();
        PopupMenus.End(_session);
        _session = null;
    }
}
