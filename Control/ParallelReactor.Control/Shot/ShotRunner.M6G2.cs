#if DEBUG
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

public static partial class ShotRunner
{
    /// <summary>M6 第 2 组的截图准备（Setup 以「m6g2-」开头）：演示数据重置之后、外框重置之前。做了事返回 true。键盘焦点的截图只在 Post 里做事。</summary>
    private static bool M6G2Pre(Window window, ShellVm vm, ShotPlan.Spec spec) => false;

    /// <summary>
    /// M6 第 2 组的截图准备：路由打开、页面重置之后，冻结之前。m6g2-kbd-*：先排一遍（刚建的页面视图这时还不在可视树里），再用 Tab 的方式聚焦一个控件
    /// （:focus-visible，画焦点环）；下一张截图重置外框（ShellVm.ResetView → LayoutReset）时清掉焦点 —— 焦点不能带到别的截图里。
    /// </summary>
    private static void M6G2Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        if (spec.Setup is not { } setup || !setup.StartsWith("m6g2-kbd-", StringComparison.Ordinal)) return;
        window.UpdateLayout();
        T First<T>(Func<T, bool> pred) where T : Visual =>
            window.GetVisualDescendants().OfType<T>().FirstOrDefault(x => x.IsEffectivelyVisible && pred(x))
            ?? throw new InvalidOperationException($"截图准备「{setup}」：找不到 {typeof(T).Name}");
        InputElement target = setup switch
        {
            // 树里选中的那一行（sel=1/2）
            "m6g2-kbd-xrv" => First<ListBoxItem>(li => li.DataContext is XRow { Key: "r1/2" }),
            // 没打开的页签「1号机 · RV1」（设备页 1号机 打开着）
            "m6g2-kbd-tab" => First<NavToggle>(t => t.DataContext is DocTabVm { Id: "reactor/1/1" }),
            "m6g2-kbd-add" => First<Button>(b => b.Name == "Add"),
            "m6g2-kbd-side" => First<NavToggle>(t => t.Content as string == "总览"),
            "m6g2-kbd-stalarm" => First<Button>(b => ReferenceEquals(b.Theme, Application.Current!.FindResource("PrStatusButton"))),
            "m6g2-kbd-dvcard" => First<Panel>(p => p.Classes.Contains("dvtile")),
            "m6g2-kbd-slot" => First<Button>(b => b.Theme != null && ReferenceEquals(b.Theme, b.FindAncestorOfType<ReactorUnPanel>()?.FindResource("UnSlot"))),
            "m6g2-kbd-chip" => First<Button>(b => b.Theme != null && ReferenceEquals(b.Theme, b.FindAncestorOfType<ReactorUnPanel>()?.FindResource("UnChip"))),
            "m6g2-kbd-libpick" => First<Button>(b => b.Theme != null && ReferenceEquals(b.Theme, b.FindAncestorOfType<ReactorProgramPanel>()?.FindResource("LibPick"))),
            _ => throw new NotSupportedException($"截图准备「{setup}」不认识（m6g2-kbd-xrv / tab / add / side / stalarm / dvcard / slot / chip / libpick）"),
        };
        if (!target.Focus(NavigationMethod.Tab)) throw new InvalidOperationException($"截图准备「{setup}」：聚焦不了 {target.GetType().Name}");
        EventHandler? clear = null;
        clear = (_, _) =>
        {
            vm.LayoutReset -= clear;
            window.FocusManager?.ClearFocus();
        };
        vm.LayoutReset += clear;
    }

    /// <summary>这一组认得的准备名（Setup 以「m6g2-」开头、只在 Post 里做事的也要列上）。</summary>
    private static bool M6G2Known(string setup) =>
        setup is "m6g2-kbd-xrv" or "m6g2-kbd-tab" or "m6g2-kbd-add" or "m6g2-kbd-side" or "m6g2-kbd-stalarm" or "m6g2-kbd-dvcard"
            or "m6g2-kbd-slot" or "m6g2-kbd-chip" or "m6g2-kbd-libpick";
}
#endif
