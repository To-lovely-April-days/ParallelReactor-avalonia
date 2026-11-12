using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 资源管理器树的键盘（M6，样稿 shell.js treeKey / xTabTarget；做什么见 <see cref="ExplorerVm.TreeKey"/>）：
/// <list type="bullet">
/// <item>Tab 只停在一行上（roving tabindex）：ListBox 的 TabNavigation = Once，停靠点（TabOnceActiveElement）每次刷新后设为 <see cref="ExplorerVm.TabTarget"/>
///   的容器 —— ListBox 自己只跟着选中行走，选中的反应釜藏在收起的设备里（没有选中行）时会停在旧的行上。</item>
/// <item>↑ ↓ Home End ← → Enter 空格在隧道阶段先处理：ListBox 自己的方向键用 NavigationMethod.Unspecified 聚焦（:focus-visible 没了，焦点环消失），
///   ← → Enter 它不处理。焦点用 Directional 放到该在的行上，焦点环留着。PageUp / PageDown 照旧由 ScrollViewer 滚动（同样稿：浏览器滚动树）。</item>
/// <item>资源管理器收起（停靠时收起、浮层 Esc / 点遮罩 / 指针打开）时焦点在它里面：清掉焦点 —— Avalonia 让焦点留在看不见的行上，树的键会照样选中、打开；
///   样稿 display: none 之后焦点回到 body（不画环、按键什么都不做）。之后焦点到别处、按下指针之前：Tab / Shift+Tab 从资源管理器的位置接着走
///   （浏览器的顺序焦点导航起点；Avalonia 没有焦点时从窗口开头走），方向键吃掉。</item>
/// </list>
/// </summary>
public partial class ExplorerView
{
    private ExplorerVm? _keysVm;
    private bool _tabTargetPending;
    /// <summary>清掉焦点之后等着接下一个 Tab 的窗口（没有时 null）。</summary>
    private TopLevel? _resumeTop;

    /// <summary>构造时调用一次：接上键盘与 Tab 停靠点。</summary>
    private void InitKeyboard()
    {
        Tree.AddHandler(KeyDownEvent, OnTreeKey, RoutingStrategies.Tunnel);
        // 新生成的容器正好是停靠点（展开之后的行、刚换了数据）：生成时就设上
        Tree.ContainerPrepared += (_, e) =>
        {
            if (_keysVm?.TabTarget is { } t && ReferenceEquals(e.Container.DataContext, t)) KeyboardNavigation.SetTabOnceActiveElement(Tree, e.Container);
        };
        DataContextChanged += (_, _) =>
        {
            if (_keysVm != null) _keysVm.Refreshed -= OnExplorerRefreshed;
            _keysVm = (DataContext as ShellVm)?.Explorer;
            if (_keysVm != null) _keysVm.Refreshed += OnExplorerRefreshed;
            UpdateTabTarget();
        };
        // 收起 / 浮层关掉（ShellView 设 IsVisible）：焦点在里面就清掉；重新显示时不再接 Tab
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) { ResumeTab(null); return; }
            if (TopLevel.GetTopLevel(this) is { FocusManager: { } fm } top && fm.GetFocusedElement() is Visual f && this.IsVisualAncestorOf(f))
            {
                fm.ClearFocus();
                ResumeTab(top);
            }
        };
        DetachedFromVisualTree += (_, _) => ResumeTab(null);
    }

    /// <summary>
    /// 等着接下一个 Tab（<paramref name="top"/> 非 null）或不再等（null）。只在等的时候挂窗口上的处理：焦点到了别处、指针按下（浏览器把起点挪到点的地方）
    /// 就不再等。
    /// </summary>
    private void ResumeTab(TopLevel? top)
    {
        if (ReferenceEquals(top, _resumeTop)) return;
        if (_resumeTop is { } old)
        {
            old.RemoveHandler(KeyDownEvent, OnResumeKey);
            old.RemoveHandler(GotFocusEvent, OnResumeEnd);
            old.RemoveHandler(PointerPressedEvent, OnResumeEnd);
        }
        _resumeTop = top;
        if (top == null) return;
        top.AddHandler(KeyDownEvent, OnResumeKey, RoutingStrategies.Tunnel);
        top.AddHandler(GotFocusEvent, OnResumeEnd, RoutingStrategies.Bubble, handledEventsToo: true);
        top.AddHandler(PointerPressedEvent, OnResumeEnd, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnResumeEnd(object? sender, RoutedEventArgs e) => ResumeTab(null);

    /// <summary>
    /// 没有焦点时的 Tab / Shift+Tab：从资源管理器（看不见，里面的都跳过）往后 / 往前找停靠点。方向键吃掉：没有焦点时 Avalonia 的方向键把焦点挪到窗口里
    /// 第一个控件（菜单栏「文件」，带焦点环，再按 Enter 就打开菜单）；样稿焦点在 body 上，方向键什么都不做。
    /// </summary>
    private void OnResumeKey(object? sender, KeyEventArgs e)
    {
        var top = _resumeTop;
        if (e.Handled || top?.FocusManager?.GetFocusedElement() != null) return;
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right) { e.Handled = true; return; }
        if (e.Key != Key.Tab) return;
        ResumeTab(null);
        var dir = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? NavigationDirection.Previous : NavigationDirection.Next;
        if (KeyboardNavigationHandler.GetNext(this, dir) is { } next && next.Focus(NavigationMethod.Tab, e.KeyModifiers)) e.Handled = true;
    }

    private void OnExplorerRefreshed(object? sender, EventArgs e) => UpdateTabTarget();

    /// <summary>
    /// 把树的 Tab 停靠点设到 <see cref="ExplorerVm.TabTarget"/> 的容器上（选中行变了、行增删之后）。ListBox 在选中项变化时自己也会设（设成选中行，
    /// 或者清空选中时留着旧的），这里在它之后再设一次；容器还没生成时排完版再试。
    /// </summary>
    private void UpdateTabTarget()
    {
        if (_keysVm?.TabTarget is not { } t) return;
        if (ContainerOf(t) is { } c)
        {
            if (!ReferenceEquals(KeyboardNavigation.GetTabOnceActiveElement(Tree), c)) KeyboardNavigation.SetTabOnceActiveElement(Tree, c);
            return;
        }
        if (_tabTargetPending) return;
        _tabTargetPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tabTargetPending = false;
            if (_keysVm?.TabTarget is { } t2 && ContainerOf(t2) is { } c2) KeyboardNavigation.SetTabOnceActiveElement(Tree, c2);
        }, DispatcherPriority.Background);
    }

    private Avalonia.Controls.Control? ContainerOf(XRow row)
    {
        var vm = _keysVm;
        int i = vm?.Rows.IndexOf(row) ?? -1;
        return i >= 0 && Tree.ContainerFromIndex(i) is Avalonia.Controls.Control c && ReferenceEquals(c.DataContext, row) ? c : null;
    }

    private void OnTreeKey(object? sender, KeyEventArgs e)
    {
        // 收起时焦点已经清掉（见上）；万一焦点还在看不见的行上，键也不动树
        if (_keysVm is not { } vm || !Tree.IsEffectivelyVisible) return;
        XTreeKey? key = e.Key switch
        {
            Key.Down => XTreeKey.Down,
            Key.Up => XTreeKey.Up,
            Key.Home => XTreeKey.Home,
            Key.End => XTreeKey.End,
            Key.Right => XTreeKey.Right,
            Key.Left => XTreeKey.Left,
            Key.Enter => XTreeKey.Enter,
            Key.Space => XTreeKey.Space,
            _ => null,
        };
        if (key == null) return;
        // 只管焦点在树的某一行上的键（样稿 it.matches('.pr-explorer-tree [role="treeitem"]')）
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item == null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), Tree) || item.DataContext is not XRow { Selectable: true } row) return;
        e.Handled = true;
        var to = vm.TreeKey(row, key.Value);
        FocusRow(to, item);
    }

    /// <summary>
    /// 焦点放到 <paramref name="row"/> 上（Directional：:focus-visible 留着，焦点环照样画）。还是原来那一行时不动；
    /// 展开 / 收起之后行有增删，排完版再核对一次（容器换了的话放到新容器上）。
    /// </summary>
    private void FocusRow(XRow row, ListBoxItem from)
    {
        var c = ContainerOf(row);
        if (c != null && !ReferenceEquals(c, from)) c.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.Post(() =>
        {
            if (ContainerOf(row) is { } now && !now.IsFocused && (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual f || !f.IsAttachedToVisualTree() || ReferenceEquals(f, from)))
                now.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Background);
    }
}
