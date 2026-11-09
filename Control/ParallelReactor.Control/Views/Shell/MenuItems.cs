using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// <see cref="MenuEntry"/> → Avalonia 菜单项（PrMenuItem 主题；菜单栏下拉、「›」溢出菜单、右键菜单共用）：
/// 图标 b:Pr.Icon、提示 b:Pr.Hint、红点 b:Pr.Dot、勾选（ToggleType = CheckBox）、类 danger / stop / head、分隔 Separator；
/// 禁用原因 b:Pr.Reason（锁图标提示，点击 toast 原因，命令换成说明原因）。
/// 分隔线只画在两组菜单项之间：开头、结尾和连续的分隔线去掉（某一组按状态整组没有时，不会留下两条挨着的线）。
/// </summary>
public static class MenuItems
{
    public static List<object> Build(IReadOnlyList<MenuEntry> entries)
    {
        var list = new List<object>(entries.Count);
        foreach (var e in entries)
        {
            if (e.IsSeparator)
            {
                if (list.Count > 0 && list[^1] is not Separator) list.Add(new Separator());
                continue;
            }
            if (e.Head != null)
            {
                var head = new MenuItem { Header = e.Head, Focusable = false };
                head.Classes.Add("head");
                list.Add(head);
                continue;
            }
            var mi = new MenuItem { Header = e.Label };
            if (e.Icon != null) PrB.SetIcon(mi, e.Icon);
            if (e.Hint != null) PrB.SetHint(mi, e.Hint);
            if (e.Alarm) PrB.SetDot(mi, true);
            if (e.Check) { mi.ToggleType = MenuItemToggleType.CheckBox; mi.IsChecked = true; }
            if (e.Danger) mi.Classes.Add("danger");
            if (e.Icon == "stop") mi.Classes.Add("stop");
            if (e.Run is { } run) mi.Command = new RelayCommand(run);
            if (!string.IsNullOrWhiteSpace(e.Reason)) PrB.SetReason(mi, UiText.Keep(e.Reason));
            list.Add(mi);
        }
        if (list.Count > 0 && list[^1] is Separator) list.RemoveAt(list.Count - 1);
        return list;
    }
}

/// <summary>
/// 打开时才按当前状态生成内容的 MenuFlyout（页签栏「›」全部页签、总览分区「⋯」设备菜单）：在 Opening 里调 <see cref="SetEntries"/>。
/// <para>
/// 为什么不在 Opening 里直接给 MenuFlyout 换 ItemsSource（或 Items.Clear / Add）：Avalonia 11.1 里这样做，这一次（以及之后每一次）打开的
/// MenuFlyoutPresenter 都是空的（ItemCount 0，只剩一条 14 px 高的空白）。ItemsSource 必须是从一开始就设好的同一个集合，打开时只改它的内容 ——
/// ObservableCollection 的增删会传到 presenter（第一次和以后每次打开都有菜单项）。
/// </para>
/// </summary>
public sealed class DynamicMenuFlyout : MenuFlyout
{
    private readonly ObservableCollection<object> _entries = new();

    public DynamicMenuFlyout() => ItemsSource = _entries;

    /// <summary>换成这些菜单项（在 Opening 里调用）。</summary>
    public void SetEntries(IReadOnlyList<MenuEntry> entries)
    {
        _entries.Clear();
        foreach (var o in MenuItems.Build(entries)) _entries.Add(o);
    }
}
