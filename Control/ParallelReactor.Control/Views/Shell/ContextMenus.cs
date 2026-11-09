using System.Collections.Generic;
using Avalonia.Controls;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 右键菜单（样稿 shell.menu({x, y}, items)）：每次右键时按当前状态生成（禁用原因、控制权 / 连接项的写法都是那一刻的），
/// 用 PrContextMenu 主题（默认主题），在指针处弹出；关掉后丢弃。菜单项同菜单栏（Views/Shell/MenuItems）。
/// </summary>
public static class ContextMenus
{
    /// <summary>最后一次弹出的右键菜单（截图核对 Shot/ShellChecks 用）。</summary>
    public static ContextMenu? Last { get; private set; }

    public static void Open(Avalonia.Controls.Control target, IReadOnlyList<MenuEntry> entries, ContextRequestedEventArgs? e = null)
    {
        Last?.Close();
        var menu = new ContextMenu { ItemsSource = MenuItems.Build(entries), Placement = PlacementMode.Pointer };
        Last = menu;
        menu.Open(target);
    }
}
