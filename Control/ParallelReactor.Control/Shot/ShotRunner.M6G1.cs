#if DEBUG
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

public static partial class ShotRunner
{
    /// <summary>M6 第 1 组的截图准备（Setup 以「m6g1-」开头）：演示数据重置之后、外框重置之前。做了事返回 true。</summary>
    private static bool M6G1Pre(Window window, ShellVm vm, ShotPlan.Spec spec) => false;

    /// <summary>
    /// M6 第 1 组的截图准备：路由打开、页面重置之后，冻结之前 —— 打开菜单 / 弹出框（同样稿参照的 js：点菜单栏的按钮、「+」「›」«⋯»、ⓘ，
    /// 在元素中心发 contextmenu；键盘的几张同 fv.js：Tab / Enter / ↓）。先排一遍版（刚导航，要量位置）；打开之后指针移到右下角（样稿的 js 不动鼠标：没有悬停）。
    /// 下一张截图的 ResetView 会关掉它（PopupMenus 跟着 LayoutReset），焦点和指针也在那时还原（<see cref="G1Shot.RestoreAtNextReset"/>）。
    /// </summary>
    private static void M6G1Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        if (spec.Setup is not { } setup || !setup.StartsWith("m6g1-", StringComparison.Ordinal)) return;
        G1Shot.Lay(window);
        G1Shot.RestoreAtNextReset(window, vm);
        switch (setup)
        {
            case "m6g1-menu-device": G1Shot.Click(window, G1Shot.Head(window, "设备")); break;
            case "m6g1-menu-view": G1Shot.Click(window, G1Shot.Head(window, "视图")); break;
            case "m6g1-menu-window": G1Shot.Click(window, G1Shot.Head(window, "窗口")); break;
            case "m6g1-pop-newtab": G1Shot.Click(window, G1Shot.Named(window, "Add")); break;
            case "m6g1-menu-tabs": G1Shot.Click(window, G1Shot.Named(window, "More")); break;
            case "m6g1-menu-ovmore": G1Shot.Click(window, G1Shot.OvMore(window, 1)); break;
            case "m6g1-pop-ovinfo":
                G1Shot.Click(window, G1Shot.OvMore(window, 1));
                if (PopupMenus.Current is { } s && PopupMenus.Focusables(s).FirstOrDefault(m => m.Header as string == "设备信息") is { } info) G1Shot.Click(window, info);
                G1Shot.Lay(window);   // 「设备信息」是关掉菜单之后再打开的（Dispatcher.Post）
                break;
            case "m6g1-pop-legend": G1Shot.Click(window, G1Shot.Named(window, "Legend")); break;
            case "m6g1-ctx-xdev": G1Shot.RightClick(window, G1Shot.Row(window, vm, "d2")); break;
            case "m6g1-ctx-xrv": G1Shot.RightClick(window, G1Shot.Row(window, vm, "r1/2")); break;
            case "m6g1-ctx-tl":
                G1Shot.RightClick(window, window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("tllabel") && b.DataContext is TlRowVm r && r.D.K == 1 && r.R.N == 2));
                break;
            case "m6g1-ctx-card": G1Shot.RightClick(window, G1Shot.Tile(window, 1, 1)); break;
            case "m6g1-menu-kbd":
                G1Shot.Head(window, "设备").Focus(NavigationMethod.Tab);
                G1Shot.Key(window, Key.Enter, PhysicalKey.Enter);
                G1Shot.Key(window, Key.Down, PhysicalKey.ArrowDown);
                break;
            case "m6g1-kbd-menubar": G1Shot.Head(window, "设备").Focus(NavigationMethod.Tab); break;
            case "m6g1-kbd-ovcard": G1Shot.Tile(window, 1, 1).Focus(NavigationMethod.Tab); break;
            default: throw new NotSupportedException($"截图准备「{setup}」不认识（ShotRunner.M6G1.cs）");
        }
        window.MouseMove(new Point(window.ClientSize.Width - 2, window.ClientSize.Height - 2));
        G1Shot.Lay(window);
    }

    /// <summary>这一组认得的准备名（Setup 以「m6g1-」开头、只在 Post 里做事的也要列上）。</summary>
    private static bool M6G1Known(string setup) => setup is "m6g1-menu-device" or "m6g1-menu-view" or "m6g1-menu-window" or "m6g1-pop-newtab" or "m6g1-menu-tabs"
        or "m6g1-menu-ovmore" or "m6g1-pop-ovinfo" or "m6g1-pop-legend" or "m6g1-ctx-xdev" or "m6g1-ctx-xrv" or "m6g1-ctx-tl" or "m6g1-ctx-card"
        or "m6g1-menu-kbd" or "m6g1-kbd-menubar" or "m6g1-kbd-ovcard";

    /// <summary>截图准备用的小工具（放在 G1Shot 里：别的组的 ShotRunner.M6G*.cs 可能有同名的）。</summary>
    internal static class G1Shot
    {
        internal static void Lay(Window w)
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        /// <summary>
        /// 准备会动焦点（Tab 到卡片 / 菜单栏项、点按钮、右键行）和指针：下一张截图重置视图时（LayoutReset，PopupMenus 先关掉弹出层）
        /// 还原成准备之前的样子 —— 焦点回到原来的元素（原来画着焦点环的照样画，没有就清掉），指针移到窗口外面（别的截图从不动指针：没有悬停）。
        /// 不还原的话焦点环、悬停会带到接在后面的截图里（截图计划里别的组的截图排在这一组后面）。
        /// </summary>
        internal static void RestoreAtNextReset(Window w, ShellVm vm)
        {
            var before = w.FocusManager?.GetFocusedElement();
            bool fv = before is StyledElement s && s.Classes.Contains(":focus-visible");
            EventHandler? once = null;
            once = (_, _) =>
            {
                vm.LayoutReset -= once;
                Restore(w, before, fv);
                w.MouseMove(new Point(-1, -1));
            };
            vm.LayoutReset += once;
        }

        /// <summary>焦点回到 <paramref name="before"/>（<paramref name="fv"/>：原来画着焦点环）；它不在界面上了、或原来没有焦点时清掉焦点。</summary>
        internal static void Restore(TopLevel w, IInputElement? before, bool fv)
        {
            var fm = w.FocusManager;
            var now = fm?.GetFocusedElement();
            if (ReferenceEquals(now, before) && (now is StyledElement n && n.Classes.Contains(":focus-visible")) == fv) return;
            // 先清掉（同一个元素再 Focus 不会改 :focus-visible）。11.1 的 ClearFocus 先退回焦点范围记着的上一个元素（例如焦点在菜单栏里时
            // 退回窗口里上一次聚焦的按钮），再清一次才没有焦点
            for (int i = 0; i < 4 && fm?.GetFocusedElement() != null; i++) fm.ClearFocus();
            if (before is InputElement ie && ie.GetVisualRoot() != null && ie.Focusable && ie.IsEffectivelyEnabled && ie.IsEffectivelyVisible)
                ie.Focus(fv ? NavigationMethod.Tab : NavigationMethod.Unspecified);
        }

        internal static MenuItem Head(Window w, string title) =>
            w.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().First(m => (m.Header as string) == title);

        internal static Button Named(Window w, string name) => w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name && b.IsEffectivelyVisible);

        internal static Button OvMore(Window w, int k) =>
            w.GetVisualDescendants().OfType<Button>().First(b => Behaviors.Pr.GetIcon(b) == "more" && b.DataContext is OverviewSectionVm s && s.K == k);

        internal static Panel Tile(Window w, int k, int n) => w.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Classes.Contains("ovtile") && p.DataContext is OverviewCardVm c && c.Section.K == k && c.N == n);

        internal static ListBoxItem Row(Window w, ShellVm vm, string key)
        {
            var tree = w.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Tree");
            tree.ScrollIntoView(vm.Explorer.Rows.ToList().FindIndex(r => r.Key == key));
            Lay(w);
            return tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);
        }

        private static Point Center(Visual v, Visual root) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root) ?? default;

        internal static void Click(Window w, Visual v)
        {
            var p = Center(v, w);
            w.MouseMove(p);
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            Lay(w);
        }

        internal static void RightClick(Window w, Visual v)
        {
            var p = Center(v, w);
            w.MouseMove(p);
            w.MouseDown(p, MouseButton.Right);
            w.MouseUp(p, MouseButton.Right);
            Lay(w);
        }

        internal static void Key(Window w, Key key, PhysicalKey pk)
        {
            w.KeyPress(key, RawInputModifiers.None, pk, null);
            w.KeyRelease(key, RawInputModifiers.None, pk, null);
            Lay(w);
        }
    }
}
#endif
