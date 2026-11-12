using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 右键菜单（样稿 shell.menu({x, y}, items)）：右键时按当前状态生成（禁用原因、控制权 / 连接项的写法都是那一刻的），打开着时跟着刷新
/// （MenuModel 给的 <see cref="MenuEntries"/> 带着重新生成的方法，见 PopupMenus）；PrContextMenu 主题（默认主题），关掉后丢弃。
/// 位置：指针右键在指针处；键盘（菜单键，Windows 另有 Shift+F10：平台的快捷键表；没有指针位置）在焦点所在的元素下面 2 px（样稿键盘的 contextmenu 落在焦点元素上）。
/// 横向夹在窗口里、下面放不下翻到上面、最高窗口高 − 16（样稿 placeMenu，PopupMenus.Place）。点在外面关掉、这一下照常落到下面（ContextMenu 本来如此）。
/// 菜单项同菜单栏（Views/Shell/MenuItems）。
/// </summary>
public static class ContextMenus
{
    /// <summary>最后一次弹出的右键菜单（截图核对 Shot/ShellChecks 用）。</summary>
    public static ContextMenu? Last { get; private set; }

    public static void Open(Avalonia.Controls.Control target, IReadOnlyList<MenuEntry> entries, ContextRequestedEventArgs? e = null)
    {
        Last?.Close();
        var items = new ObservableCollection<object>();
        MenuItems.Patch(items, entries);
        var top = TopLevel.GetTopLevel(target);
        Point? pointer = e != null && top != null && e.TryGetPosition(top, out var p) ? p : null;
        // 键盘打开：锚在焦点所在的元素上（总览分区头上按菜单键时是分区头，不是整个分区）
        var place = pointer == null && e?.Source is Avalonia.Controls.Control fc && (ReferenceEquals(fc, target) || target.IsVisualAncestorOf(fc)) ? fc : target;
        var menu = new ContextMenu
        {
            ItemsSource = items,
            Placement = pointer != null ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = pointer != null ? 0 : 2,
            PlacementTarget = place,
            PlacementConstraintAdjustment = PopupMenus.ScreenEdge,
        };
        Last = menu;
        menu.Open(target);
        PopupMenus.ContextOpened(menu, place, pointer, items, (entries as MenuEntries)?.Rebuild);
    }
}
