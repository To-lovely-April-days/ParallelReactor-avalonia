#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 1 组的行为核对（PR_CONTROL_SHELL_CHECKS=1）：菜单、弹出框、Esc、点在外面一下就换（PORT-PLAN §5 M6「交互」；Views/Shell/PopupMenus）——
/// 标题项 32 高、「+」/ 图例的尺寸、别名里的「_」照原样显示；键盘（菜单栏每个菜单一个 Tab 停靠点、Enter / 空格 / ↓ 打开进第一项、↑ ↓ Home End 循环、
/// Esc 回到按钮、键盘选的项把焦点放回按钮、«⋯»「›」用键盘打开、指针打开后 ↓ 进菜单、菜单键（Windows 另有 Shift+F10）在焦点元素下面、带原因的项不拦方向键）、
/// 键盘焦点的样子（菜单项内侧的环不画悬停底色、菜单栏项外侧的环、总览卡片没有 Fluent 的黑框）；Esc 只关菜单 / 弹出框；
/// 打开着菜单时一下点到急停 / 全部停止 / 别的菜单 / 树的行，再点同一个按钮 = 关掉；再打开不带着上次的悬停底色；键盘打开、鼠标选的不把焦点环画回按钮；
/// 锚点按下的样子；打开着的菜单跟着刷新（文字、原因、动作）；位置（样稿 placeMenu：翻到上面、比窗口还高时限高 + 上沿 4）；
/// 右键总览分区里的按钮 = 设备菜单。跑完把演示数据、外框和焦点恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task M6G1Checks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var L = vm.Layout;
        bool narrow = L.FrameWidth < 1600;
        double W = window.ClientSize.Width, H = window.ClientSize.Height;
        // 跑完把焦点还原（下面 Tab、点按钮都会动焦点；接在后面的别的组的核对看到的还是跑之前的焦点）
        var focus0 = window.FocusManager?.GetFocusedElement();
        bool fv0 = focus0 is StyledElement sf0 && sf0.Classes.Contains(":focus-visible");
        Info($"M6 第 1 组核对之前的焦点：{G1.Desc(focus0)}{(fv0 ? "（:focus-visible）" : "")}");
        vm.ResetView();
        await Settle();

        var bar = Find<Menu>(window, _ => true);
        MenuItem Head(string title) => bar.Items.OfType<MenuItem>().First(m => (m.Header as string) == title);
        var stopCell = Find<GlobalStopCell>(window, _ => true);
        var estopBtn = stopCell.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("estop"));
        var stopAllBtn = stopCell.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "全部停止");
        var add = Find<Button>(window, b => b.Name == "Add");
        var more = Find<Button>(window, b => b.Name == "More");
        Button OvMore(int k) => Find<PageHost>(window, _ => true).CurrentView!.GetVisualDescendants().OfType<Button>()
            .First(b => Behaviors.Pr.GetIcon(b) == "more" && b.DataContext is OverviewSectionVm s && s.K == k);
        Button Legend() => Find<Button>(window, b => b.Name == "Legend");
        PopupMenus.Session? Cur() => PopupMenus.Current;
        string Foc() => G1.Desc(window.FocusManager?.GetFocusedElement());
        Rect PopRect() => Cur()?.Root is Visual c ? G1.RectIn(c, window) : default;

        // ———————————————————— 尺寸：标题项 32 高、「+」550、图例 362、«⋯» 菜单 ————————————————————
        Click(window, Head("设备"));
        await Settle();
        var dev = Cur();
        var devItems = dev?.Host?.GetRealizedContainers().OfType<MenuItem>().ToList() ?? new List<MenuItem>();
        var head = devItems.FirstOrDefault(m => m.Classes.Contains("head"));
        Check(dev?.Owner == Head("设备") && head != null && Math.Abs(head.Bounds.Height - 32) < 0.5 && Math.Abs(PopRect().Height - 324) < 0.6 && Math.Abs(PopRect().Width - 220) < 0.6,
            $"设备菜单：标题项「当前：1号机」高 {head?.Bounds.Height:0.##}（样稿 8 + 20 + 4 = 32），菜单 {PopRect().Width:0.#}×{PopRect().Height:0.#}（样稿 220×324）");
        var h0 = G1.RectIn(Head("设备"), window);
        Check(Math.Abs(PopRect().Left - h0.Left) < 0.6 && Math.Abs(PopRect().Top - (h0.Bottom + 3)) < 0.6,
            $"设备菜单在「设备」下面：左 {PopRect().Left:0.#}（按钮 {h0.Left:0.#}），上 {PopRect().Top:0.#}（按钮下沿 {h0.Bottom:0.#}）");
        Click(window, Head("设备"));   // 再点一次 = 关闭
        await Settle();
        Check(!Head("设备").IsSubMenuOpen && Cur() == null && !bar.IsOpen, "再点一次「设备」→ 关掉（菜单栏也退出菜单模式）");

        Click(window, add);
        await Settle();
        var addRect = G1.RectIn(add, window);
        var nt = PopRect();
        Check(Cur()?.Anchor == add && !Cur()!.IsMenu && Math.Abs(nt.Width - 550) < 0.6 && Math.Abs(nt.Height - 512) < 3,
            $"「+」弹出框 {nt.Width:0.#}×{nt.Height:0.#}（样稿 550×512：宽 548 含内边距 + 边框）");
        Check(Math.Abs(nt.Right - (W - 4)) < 0.6 && Math.Abs(nt.Top - (addRect.Bottom + 2)) < 0.6,
            $"「+」夹在窗口里：左 {nt.Left:0.#}、右 {nt.Right:0.#}（窗口 {W:0} − 4；按钮在 {addRect.Left:0.#}，样稿 placeMenu），上 {nt.Top:0.#} = 按钮下沿 + 2");
        Check(add.Classes.Contains(":flyout-open") && G1.Bg(add) == Theme.Palette.BgHoverColor, $"「+」打开时按钮是展开的样子（bg-hover，{(G1.Bg(add) is { } addBg ? Hex(new SolidColorBrush(addBg)) : "透明")}）");
        Click(window, add);
        await Settle();
        Check(Cur() == null && (add.Flyout as Flyout)?.IsOpen != true, "再点一次「+」→ 关掉（不会又打开）");

        if (narrow) { vm.ToggleExplorer(); await Settle(); }
        Click(window, Legend());
        await Settle();
        var lg = PopRect();
        var lgBtn = G1.RectIn(Legend(), window);
        Check(Cur()?.Anchor == Legend() && Math.Abs(lg.Width - 362) < 0.6 && Math.Abs(lg.Left - lgBtn.Left) < 0.6 && Math.Abs(lg.Top - (lgBtn.Bottom + 2)) < 0.6,
            $"ⓘ 图例 {lg.Width:0.#}×{lg.Height:0.#}（样稿 362×314），在 ⓘ 下面 2 px、左对齐（{lg.Left:0.#},{lg.Top:0.#}）");
        Check(G1.Bg(Legend()) == Theme.Palette.BgPressColor && Legend().Classes.Contains(":flyout-open"), "图例打开时 ⓘ 一直是按下的样子（bg-press，样稿 is-menu-open）");
        if (Cur()?.Root is Avalonia.Controls.Control lgc) Check(Save(lgc, Path.Combine(dir, $"shell-popup-legend-{width}.png"), lines), $"图例弹出层截图 shell-popup-legend-{width}.png");
        Click(window, Legend());
        await Settle();
        Check(Cur() == null, "再点一次 ⓘ → 关掉");
        if (L.ExplorerFloat) { L.CloseFloat(); await Settle(); }

        // ———————————————————— 别名里的「_」照原样显示（菜单不认访问键） ————————————————————
        var demo = vm.Fleet.DemoData!;
        demo.EditDevice(demo.Device(1)!, new DemoDeviceEdit("Reactor_A"));
        vm.Fleet.RefreshNow();
        await Settle();
        Click(window, OvMore(1));
        await Settle();
        var estopItem = Cur() is { } ms ? PopupMenus.Focusables(ms).LastOrDefault() : null;
        var estopText = estopItem?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name != "PART_Hint" && t.IsEffectivelyVisible);
        Check(estopText != null && estopText is not AccessText && estopText.Text == "急停 Reactor_A…" && estopText.TextLayout.TextLines.Sum(l => l.Length) >= estopText.Text.Length,
            $"别名「Reactor_A」：«⋯» 最后一项画出来是「{estopText?.Text}」（{estopText?.GetType().Name}，不吃掉「_」）");
        Click(window, OvMore(1));
        await Settle();
        Click(window, Head("窗口"));
        await Settle();
        var winTexts = (Cur()?.Host?.GetRealizedContainers().OfType<MenuItem>() ?? Enumerable.Empty<MenuItem>())
            .SelectMany(m => m.GetVisualDescendants().OfType<TextBlock>()).Where(t => t.Text?.Contains("Reactor_A") == true).ToList();
        Check(winTexts.Count > 0 && winTexts.All(t => t is not AccessText && t.TextLayout.TextLines.Sum(l => l.Length) >= t.Text!.Length),
            "窗口菜单的页签名照原样（" + string.Join("、", winTexts.Select(t => t.Text)) + "）");
        Esc(window);
        await Settle();
        demo.EditDevice(demo.Device(1)!, new DemoDeviceEdit("1号机"));
        vm.Fleet.RefreshNow();
        await Settle();

        // ———————————————————— 键盘：菜单栏 ————————————————————
        vm.ResetView();
        window.MouseMove(new Point(W - 2, H - 2));   // 指针移开（弹出层出现在静止的指针下面时 11.1 会选中悬停的那一项）
        await Settle();
        Head("文件").Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Tab);
        await Settle();
        var fe = window.FocusManager?.GetFocusedElement();
        Check(ReferenceEquals(fe, Head("设备")) && G1.Fv(fe), $"菜单栏每个菜单一个 Tab 停靠点：从「文件」Tab → {Foc()}（:focus-visible）");
        var ring = Head("设备").GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Focus");
        var dr = G1.RectIn(Head("设备"), window);
        var rr = ring != null ? G1.RectIn(ring, window) : default;
        Check(ring?.IsVisible == true && Math.Abs(rr.Left - (dr.Left - 4)) < 0.6 && Math.Abs(rr.Width - (dr.Width + 8)) < 0.6 && G1.Bg(Head("设备")) == null,
            $"菜单栏项的焦点环在外面 2..4 px（环 {rr.Left:0.#}…{rr.Right:0.#}，项 {dr.Left:0.#}…{dr.Right:0.#}），不画悬停底色（同样稿）");
        G1.Press(window, Key.Space);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        var first = fe as MenuItem;
        Check(Head("设备").IsSubMenuOpen && first?.Header as string == "添加设备…" && G1.Fv(first) && G1.FocusRing(first) && G1.Bg(first) == null,
            $"空格打开「设备」，焦点进第一项：{Foc()}（内侧 2 px 环，不画悬停底色）");
        if (Cur()?.Root is Avalonia.Controls.Control kc) Check(Save(kc, Path.Combine(dir, $"shell-popup-kbd-device-{width}.png"), lines), $"键盘打开的设备菜单 shell-popup-kbd-device-{width}.png");
        G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「设备管理…」" && G1.Fv(window.FocusManager?.GetFocusedElement()), $"↓ → {Foc()}");
        G1.Press(window, Key.End);
        await Settle();
        Check(Foc() == "MenuItem「急停…」", $"End → {Foc()}");
        G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「添加设备…」", $"↓ 在最后一项循环回第一项 → {Foc()}");
        G1.Press(window, Key.Up);
        await Settle();
        Check(Foc() == "MenuItem「急停…」", $"↑ 在第一项循环到最后一项 → {Foc()}");
        G1.Press(window, Key.Home);
        await Settle();
        Check(Foc() == "MenuItem「添加设备…」", $"Home → {Foc()}");
        G1.Press(window, Key.Right);
        await Settle();
        Check(Head("视图").IsSubMenuOpen && !Head("设备").IsSubMenuOpen && Foc() == "MenuItem「总览」", $"→ 换到「视图」并进第一项（{Foc()}）");
        G1.Press(window, Key.Escape);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        Check(!Head("视图").IsSubMenuOpen && !bar.IsOpen && ReferenceEquals(fe, Head("视图")) && G1.Fv(fe) && Cur() == null,
            $"Esc 一次就关掉（菜单栏退出菜单模式），焦点回到 {Foc()}（:focus-visible）");
        G1.Press(window, Key.Enter);
        await Settle();
        for (int i = 0; i < 2; i++) G1.Press(window, Key.Down);
        await Settle();
        bool tlBefore = L.TimelineCollapsed;
        Check(Foc() == "MenuItem「时间轴」", $"Enter 打开「视图」，↓↓ → {Foc()}");
        G1.Press(window, Key.Enter);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        Check(L.TimelineCollapsed != tlBefore && ReferenceEquals(fe, Head("视图")) && G1.Fv(fe) && !Head("视图").IsSubMenuOpen,
            $"Enter 选「时间轴」→ 收起时间轴，焦点回到 {Foc()}（样稿：键盘选的先回到按钮再执行）");
        vm.ToggleTimeline();
        await Settle();
        Head("帮助").Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Space);
        await Settle();
        Check(Head("帮助").IsSubMenuOpen && Foc() == "MenuItem「使用说明」", $"空格打开「帮助」（{Foc()}）");
        G1.Press(window, Key.Space);
        await Settle();
        Check(!Head("帮助").IsSubMenuOpen && vm.Toasts.Items.LastOrDefault()?.Text == "使用说明（第二阶段设计）" && ReferenceEquals(window.FocusManager?.GetFocusedElement(), Head("帮助")),
            $"空格选「使用说明」→ 执行、关掉，焦点回到「帮助」（{Foc()}）");
        vm.Toasts.Clear();
        Click(window, Head("设备"));
        await Settle();
        Head("设备").Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Enter);
        await Settle();
        Check(!Head("设备").IsSubMenuOpen && Cur() == null && !vm.Dialogs.IsOpen && ReferenceEquals(window.FocusManager?.GetFocusedElement(), Head("设备")),
            "焦点在打开着的「设备」上按 Enter → 关掉（样稿再点一次），焦点留在「设备」");

        // ———————————————————— 键盘：«⋯» / 「›」 ————————————————————
        var m1 = OvMore(1);
        window.MouseMove(new Point(W - 2, H - 2));
        m1.Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Enter);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        Check(Cur()?.Anchor == m1 && Foc() == "MenuItem「打开设备页」" && G1.Fv(fe), $"«⋯» 上按 Enter → 打开设备菜单，焦点进第一项（{Foc()}）");
        Check(m1.Classes.Contains(":flyout-open") && G1.Bg(m1) == Theme.Palette.BgPressColor, "«⋯» 打开时一直是按下的样子（bg-press，样稿 is-menu-open）");
        G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「设备信息」" && G1.Fv(window.FocusManager?.GetFocusedElement()), $"↓ → {Foc()}（:focus-visible）");
        G1.Press(window, Key.Escape);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        Check(Cur() == null && ReferenceEquals(fe, m1) && G1.Fv(fe) && vm.Route.Id == "overview",
            $"Esc → 关掉，焦点回到 «⋯»（{Foc()}，{string.Join(" ", m1.Classes.Where(c => c.StartsWith(':')))}；当前弹出 {Cur()?.Owner.GetType().Name ?? "无"}）");
        G1.Press(window, Key.Space);
        await Settle();
        Check(Cur()?.Anchor == m1 && Foc() == "MenuItem「打开设备页」", $"空格（抬起时打开）也进第一项（{Foc()}）");
        G1.Press(window, Key.Down);
        G1.Press(window, Key.Enter);
        await Settle();
        await Settle();
        var info = OverviewPage.LastInfo;
        fe = window.FocusManager?.GetFocusedElement();
        Check(info?.IsOpen == true && ReferenceEquals(fe, m1) && m1.Classes.Contains("menu-open") && G1.Bg(m1) == Theme.Palette.BgPressColor,
            $"Enter 选「设备信息」→ 焦点先回到 «⋯»，设备信息锚在 «⋯» 下、«⋯» 是按下的样子（{Foc()}）");
        var ir = PopRect();
        var mr = G1.RectIn(m1, window);
        Check(Math.Abs(ir.Right - mr.Right) < 0.6 && Math.Abs(ir.Top - (mr.Bottom + 2)) < 0.6 && Math.Abs(ir.Width - 342) < 0.6,
            $"设备信息：右对齐 «⋯»、下沿 + 2（{ir.Left:0.#},{ir.Top:0.#} {ir.Width:0.#}×{ir.Height:0.#}）");
        G1.Press(window, Key.Escape);
        await Settle();
        Check(info?.IsOpen != true && ReferenceEquals(window.FocusManager?.GetFocusedElement(), m1) && !m1.Classes.Contains("menu-open"), "Esc 关掉设备信息，焦点在 «⋯»，«⋯» 不再按下");
        // 指针打开：焦点留在按钮上，↓ 进第一项、↑ 进最后一项
        Click(window, m1);
        await Settle();
        Check(Cur()?.Anchor == m1 && !(window.FocusManager?.GetFocusedElement() is MenuItem), $"点 «⋯» 打开：焦点不进菜单（{Foc()}）");
        window.MouseMove(new Point(W / 2, H - 10));
        await Settle();
        Check(G1.Bg(m1) == Theme.Palette.BgPressColor, "指针移开以后 «⋯» 仍是按下的样子");
        G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「打开设备页」", $"指针打开的菜单：↓ 进第一项（{Foc()}）");
        G1.Press(window, Key.Escape);
        await Settle();
        Click(window, m1);
        await Settle();
        G1.Press(window, Key.Up);
        await Settle();
        Check(Foc() == "MenuItem「急停 1号机…」", $"↑ 进最后一项（{Foc()}）");
        G1.Press(window, Key.Escape);
        await Settle();
        more.Focus(NavigationMethod.Tab);
        window.MouseMove(new Point(W - 2, H - 2));   // 指针移开：弹出层出现在静止的指针下面时 11.1 会把悬停的那一项选中（焦点跟过去）
        G1.Press(window, Key.Enter);
        await Settle();

        var moreRect = G1.RectIn(more, window);
        var tabHeads = Cur() is { } ts ? PopupMenus.Focusables(ts).Select(m => m.Header as string).ToList() : new List<string?>();
        Check(Cur()?.Anchor == more && Cur()!.IsMenu && Foc() == "MenuItem「" + tabHeads.FirstOrDefault() + "」" && tabHeads.FirstOrDefault() == vm.Tabs.Tabs[0].Label
              && Math.Abs(PopRect().Right - moreRect.Right) < 0.6,
            $"「›」上按 Enter → 全部页签，焦点进第一项（{Foc()}；菜单 {string.Join("、", tabHeads)}），右对齐「›」（{PopRect().Right:0.#} / {moreRect.Right:0.#}）");
        G1.Press(window, Key.Escape);
        await Settle();
        Check(Cur() == null && ReferenceEquals(window.FocusManager?.GetFocusedElement(), more) && G1.Fv(more), $"Esc → 焦点回到「›」（{Foc()}）");
        // 页签放不下（同 ShellChecks.cs 的溢出）：键盘移到写「未显示」的那一项，Enter 切过去，焦点回到「›」
        foreach (var r in new[] { "reactor/3/11", "reactor/3/12", "device/2", "reactor/2/3", "placeholder/records", "placeholder/settings", "reactor/3/5" })
            vm.Navigate(Route.Parse(r)!);
        await Settle();
        more.Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Enter);
        await Settle();
        MenuItem? hiddenItem = null;
        for (int i = 0; i < vm.Tabs.Tabs.Count && hiddenItem == null; i++)
        {
            G1.Press(window, Key.Down);
            if (window.FocusManager?.GetFocusedElement() is MenuItem fm && Behaviors.Pr.GetHint(fm) == "未显示") hiddenItem = fm;
        }
        var hiddenTab = vm.Tabs.Tabs.FirstOrDefault(t => t.Label == hiddenItem?.Header as string);
        G1.Press(window, Key.Enter);
        await Settle();
        Check(hiddenItem != null && hiddenTab != null && vm.Route.Id == hiddenTab.Id && Cur() == null && ReferenceEquals(window.FocusManager?.GetFocusedElement(), more),
            $"「›」：键盘移到写「未显示」的「{hiddenItem?.Header}」、Enter → 切到 {vm.Route.Id}，菜单关掉、焦点回到「›」（{Foc()}）");
        vm.ResetView();
        await Settle();

        // ———————————————————— 右键菜单：键盘、菜单键、带原因的项 ————————————————————
        if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }
        var tree = Find<ListBox>(window, l => l.Name == "Tree");
        ListBoxItem Row(string key)
        {
            int i = vm.Explorer.Rows.ToList().FindIndex(r => r.Key == key);
            tree.ScrollIntoView(i);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);
        }
        RightClick(window, Row("r1/2"));
        await Settle();
        int toasts = vm.Toasts.Items.Count;
        var ctx = Cur();
        Check(ctx?.IsContext == true && MenuLabels(ctx.Host?.ItemsSource?.Cast<object>()).FirstOrDefault()?.StartsWith("#1号机 · RV2", StringComparison.Ordinal) == true,
            "右键 1号机 · RV2 → 反应釜菜单");
        for (int i = 0; i < 3; i++) G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「停止」" && vm.Toasts.Items.Count == toasts, $"↓ 三次经过带原因的「启动」到「停止」（{Foc()}），没有 toast（带原因的项不拦方向键）");
        G1.Press(window, Key.Up);
        await Settle();
        var startItem = window.FocusManager?.GetFocusedElement() as MenuItem;
        Check(startItem?.Header as string == "启动" && Behaviors.Pr.IsLocked(startItem!), $"↑ → 带原因的「启动」（{Behaviors.Pr.GetReason(startItem!)}）");
        G1.Press(window, Key.Enter);
        await Settle();
        Check(Cur() == ctx && vm.Toasts.Items.Count == toasts + 1 && G1.Plain(vm.Toasts.Items.Last().Text) == G1.Plain(Behaviors.Pr.GetReason(startItem!)),
            $"Enter 在带原因的项上 → toast 原因（{vm.Toasts.Items.LastOrDefault()?.Text}），菜单不关");
        G1.Press(window, Key.Escape);
        await Settle();
        fe = window.FocusManager?.GetFocusedElement();
        Check(Cur() == null && ContextMenus.Last?.IsOpen != true && ReferenceEquals(fe, Row("r1/2")),
            $"右键菜单 Esc → 关掉，焦点回到右键的那一行（{G1.Desc(fe)}）");
        vm.Toasts.Clear();
        var r13 = Row("r1/3");
        r13.Focus(NavigationMethod.Tab);
        await Settle();
        // 键盘的右键菜单键：平台的快捷键表（Windows 是菜单键和 Shift+F10，无头 / macOS 只有菜单键）
        var ctxKey = TopLevel.GetTopLevel(window)?.PlatformSettings?.HotkeyConfiguration.OpenContextMenu.FirstOrDefault();
        G1.Press(window, ctxKey?.Key ?? Key.Apps, (RawInputModifiers)(int)(ctxKey?.KeyModifiers ?? KeyModifiers.None));
        await Settle();
        var rowRect = G1.RectIn(r13, window);
        var cm = PopRect();
        Check(Cur()?.IsContext == true && MenuLabels(Cur()!.Host?.ItemsSource?.Cast<object>()).FirstOrDefault()?.StartsWith("#1号机 · RV3", StringComparison.Ordinal) == true
              && Math.Abs(cm.Left - Math.Max(4, rowRect.Left)) < 0.6 && (Math.Abs(cm.Top - (rowRect.Bottom + 2)) < 0.6 || Math.Abs(cm.Bottom - (rowRect.Top - 2)) < 0.6),
            $"菜单键（{ctxKey}）在聚焦的行上 → 反应釜菜单在这一行下面（菜单 {cm.Left:0.#},{cm.Top:0.#}；行 {rowRect.Left:0.#},{rowRect.Bottom:0.#}）");
        G1.Press(window, Key.Down);
        await Settle();
        Check(Foc() == "MenuItem「打开」", $"↓ 进第一项（{Foc()}）");
        G1.Press(window, Key.Escape);
        await Settle();
        if (L.ExplorerFloat) { L.CloseFloat(); await Settle(); }

        // ———————————————————— Esc 只关菜单 / 弹出框（不再还原放大的面板、收起浮层） ————————————————————
        vm.Navigate(Route.Device(1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        var res = pages.CurrentView!.GetVisualDescendants().OfType<PrPanel>().First(p => p.Title as string == "共用资源");
        Click(window, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        Click(window, more);
        await Settle();
        G1.Press(window, Key.Escape);
        await Settle();
        Check(Cur() == null && res.IsMaximized, "放大「共用资源」+ 打开「›」：Esc 只关菜单，面板仍放大");
        Click(window, add);
        await Settle();
        G1.Press(window, Key.Escape);
        await Settle();
        Check(Cur() == null && res.IsMaximized && ReferenceEquals(window.FocusManager?.GetFocusedElement(), add), "打开「+」：Esc 只关弹出框（焦点回到「+」），面板仍放大");
        G1.Press(window, Key.Escape);
        await Settle();
        Check(!res.IsMaximized, "再按 Esc → 还原面板");
        vm.Navigate(Route.Overview);
        await Settle();
        if (narrow)
        {
            vm.ToggleExplorer();
            await Settle();
            RightClick(window, Row("r1/2"));
            await Settle();
            G1.Press(window, Key.Escape);
            await Settle();
            Check(Cur() == null && L.ExplorerFloat, "浮层资源管理器里右键菜单：Esc 只关菜单，浮层还开着");
            Click(window, Legend());
            await Settle();
            G1.Press(window, Key.Escape);
            await Settle();
            Check(Cur() == null && L.ExplorerFloat && ReferenceEquals(window.FocusManager?.GetFocusedElement(), Legend()), "浮层里的 ⓘ 图例：Esc 只关图例（焦点回到 ⓘ），浮层还开着");
            G1.Press(window, Key.Escape);
            await Settle();
            Check(!L.ExplorerFloat, "再按 Esc → 收起浮层");
        }

        // ———————————————————— 点在外面：一下就换 / 点到急停、全部停止、树的行 ————————————————————
        vm.ResetView();
        await Settle();
        // 宽屏：先开下面那台的 «⋯»（菜单在它下面，不会盖住上面那台的 «⋯»）再点上面那台的；窄屏的 «⋯» 都在同一列、菜单会盖住别的 «⋯»，改点「›」
        Click(window, OvMore(2));
        await Settle();
        bool open2 = Cur()?.Anchor == OvMore(2);
        Click(window, narrow ? more : OvMore(1));
        await Settle();
        Check(open2 && (OvMore(2).Flyout as MenuFlyout)?.IsOpen != true && (narrow ? Cur()?.Anchor == more : Cur()?.Anchor == OvMore(1) && vm.SelectedDevice?.K == 1),
            narrow ? "打开 2号机 «⋯» 时点「›」→ 一下就换成全部页签菜单" : "打开 2号机 «⋯» 时点 1号机 «⋯» → 一下就换成 1号机的菜单");
        G1.Press(window, Key.Escape);
        await Settle();
        Click(window, OvMore(1));
        await Settle();
        bool open1 = Cur()?.Anchor == OvMore(1);
        Click(window, OvMore(1));
        await Settle();
        Check(open1 && Cur() == null && (OvMore(1).Flyout as MenuFlyout)?.IsOpen != true, "再点 1号机 «⋯» → 关掉（不会又打开）");
        var opens = new (string Name, Func<Task> Open)[]
        {
            ("设备菜单", async () => { Click(window, Head("设备")); await Settle(); }),
            ("「+」", async () => { Click(window, add); await Settle(); }),
            ("「›」", async () => { Click(window, more); await Settle(); }),
            ("1号机 «⋯»", async () => { Click(window, OvMore(1)); await Settle(); }),
            ("设备信息", async () =>
            {
                Click(window, OvMore(1));
                await Settle();
                var it = Cur() is { } s ? PopupMenus.Focusables(s).FirstOrDefault(m => m.Header as string == "设备信息") : null;
                if (it != null) Click(window, it);
                await Settle();
                await Settle();
            }),
            ("右键菜单", async () => { RightClick(window, Find<PageHost>(window, _ => true).CurrentView!.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("ovtile"))); await Settle(); }),
            // 窄屏的资源管理器是浮层：先打开它（ⓘ 在浮层里）
            ("ⓘ 图例", async () => { if (narrow && !L.ExplorerFloat) { vm.ToggleExplorer(); await Settle(); } Click(window, Legend()); await Settle(); }),
        };
        foreach (var (target, dlg, tname) in new[] { (estopBtn, "estop", "急停"), (stopAllBtn, "stopall", "全部停止") })
            foreach (var (name, open) in opens)
            {
                await open();
                bool wasOpen = Cur() != null;
                Click(window, target);
                await Settle();
                Check(wasOpen && Cur() == null && vm.Dialogs.Current?.Name == dlg,
                    $"{name}打开着时点一下「{tname}」→ 关掉{name}并打开{tname}对话框（{vm.Dialogs.Current?.Name ?? "没有对话框"}）"
                    + (wasOpen && Cur() == null ? "" : $"【之前开着 {wasOpen}，之后还开着 {Cur()?.Owner.GetType().Name ?? "无"}】"));
                // 指针移开再关对话框：停在「全部停止」上的话，关掉之后 400 ms 它的提示出来、盖住下面 1号机 的 «⋯»（无头的指针一步跳过去，点到提示上）
                window.MouseMove(new Point(W - 2, H - 2));
                vm.Dialogs.Close();
                await Settle();
                if (L.ExplorerFloat) { L.CloseFloat(); await Settle(); }
            }
        // 宽屏：资源管理器停靠着，点它的行；窄屏的浮层有遮罩盖住页签栏（点「+」先收起浮层，同样稿），改点总览的卡片
        Panel Tile(int k, int n) => Find<PageHost>(window, _ => true).CurrentView!.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Classes.Contains("ovtile") && p.DataContext is OverviewCardVm c && c.Section.K == k && c.N == n);
        Visual Target(int n) => narrow ? Tile(1, n) : Row("r1/" + n);
        string where = narrow ? "总览卡片" : "树的行";
        Click(window, add);
        await Settle();
        RightClick(window, Target(1));
        await Settle();
        Check(Cur()?.IsContext == true && (add.Flyout as Flyout)?.IsOpen != true && vm.SelectedRv == 1,
            $"「+」打开着时右键{where} 1号机 · RV1 → 一下就换成反应釜菜单（选中 RV1）");
        G1.Press(window, Key.Escape);
        await Settle();
        Click(window, more);
        await Settle();
        Click(window, Target(3));
        await Settle();
        Check(Cur() == null && vm.SelectedDevice?.K == 1 && vm.SelectedRv == 3, $"「›」打开着时点{where} 1号机 · RV3 → 一下就选中（{vm.Inspector.PathTip}）");
        Click(window, OvMore(1));
        await Settle();
        var infoIt = Cur() is { } s1 ? PopupMenus.Focusables(s1).FirstOrDefault(m => m.Header as string == "设备信息") : null;
        if (infoIt != null) Click(window, infoIt);
        await Settle();
        await Settle();
        Check(OverviewPage.LastInfo?.IsOpen == true && OvMore(1).Classes.Contains("menu-open"), "设备信息打开（«⋯» 按下的样子）");
        Click(window, OvMore(1));
        await Settle();
        Check(OverviewPage.LastInfo?.IsOpen != true && Cur()?.Anchor == OvMore(1) && Cur()!.IsMenu, "设备信息开着时点 «⋯» → 一下关掉它并打开菜单（样稿 same = false）");
        G1.Press(window, Key.Escape);
        await Settle();
        Click(window, Head("设备"));
        await Settle();
        Click(window, Head("视图"));
        await Settle();
        Check(Head("视图").IsSubMenuOpen && !Head("设备").IsSubMenuOpen && Cur()?.Owner == Head("视图"), "菜单栏：「设备」开着时点「视图」→ 一下就换");
        G1.Press(window, Key.Escape);
        await Settle();

        // ———————————————————— 再打开：不带着上次的悬停底色；键盘打开、鼠标选：焦点环不画回按钮 ————————————————————
        // «⋯»「›」的菜单项原地修补、下次打开还是同一批对象：Esc 关掉时悬停着的那一项不能还是 :selected（11.1 只在指针移出时清）
        foreach (var (name, anchorOf) in new (string, Func<Button>)[] { ("1号机 «⋯»", () => OvMore(1)), ("「›」", () => more) })
        {
            Click(window, anchorOf());
            await Settle();
            var hov = Cur() is { } hs ? PopupMenus.Focusables(hs).Skip(1).FirstOrDefault() : null;
            if (hov != null) window.MouseMove(Center(hov, window));
            await Settle();
            bool hovSel = hov != null && hov.Classes.Contains(":selected") && G1.Bg(hov) != null;
            G1.Press(window, Key.Escape);
            window.MouseMove(new Point(W - 2, H - 2));
            await Settle();
            Click(window, anchorOf());
            window.MouseMove(new Point(W - 2, H - 2));
            await Settle();
            var again = Cur() is { } hs2 ? PopupMenus.Focusables(hs2) : new List<MenuItem>();
            var stale = again.Where(m => m.Classes.Contains(":selected") || G1.Bg(m) != null).Select(m => m.Header).ToList();
            Check(hovSel && again.Count > 1 && again.Contains(hov!) && stale.Count == 0,
                $"{name}：悬停「{hov?.Header}」（bg-hover）→ Esc → 再点开：没有哪一项带着悬停底色（{(stale.Count == 0 ? "没有" : string.Join("、", stale))}）");
            G1.Press(window, Key.Escape);
            await Settle();
        }
        var mk = OvMore(1);
        window.MouseMove(new Point(W - 2, H - 2));
        mk.Focus(NavigationMethod.Tab);
        G1.Press(window, Key.Enter);
        await Settle();
        bool kbdFirst = Foc() == "MenuItem「打开设备页」";
        var infoByMouse = Cur() is { } ks ? PopupMenus.Focusables(ks).FirstOrDefault(m => m.Header as string == "设备信息") : null;
        if (infoByMouse != null) Click(window, infoByMouse);
        await Settle();
        await Settle();
        Check(kbdFirst && OverviewPage.LastInfo?.IsOpen == true && !G1.Fv(mk) && !G1.Fv(window.FocusManager?.GetFocusedElement()),
            $"Enter 打开 «⋯»、鼠标点「设备信息」→ 焦点环不画回 «⋯»（样稿 e.detail 1 不 focus；焦点 {Foc()}，«⋯» {string.Join(" ", mk.Classes.Where(c => c.StartsWith(':')))}）");
        G1.Press(window, Key.Escape);
        await Settle();
        window.MouseMove(new Point(W - 2, H - 2));
        await Settle();

        // ———————————————————— 右键总览分区里的按钮 = 设备菜单 ————————————————————
        var sec2 = Find<PageHost>(window, _ => true).CurrentView!.GetVisualDescendants().OfType<PrPanel>().First(p => p.Classes.Contains("ovdev") && p.DataContext is OverviewSectionVm s && s.K == 2);
        RightClick(window, sec2.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "打开"));
        await Settle();
        var secMenu = MenuLabels(Cur()?.Host?.ItemsSource?.Cast<object>());
        Check(Cur()?.IsContext == true && secMenu.FirstOrDefault() == "#2号机 · HT-PR4" && vm.SelectedDevice?.K == 2 && vm.Route.Id == "overview",
            "右键 2号机 分区的「打开」按钮 → 设备菜单（样稿 closest('[data-ctx]')）：" + string.Join(" | ", secMenu.Take(3)));
        G1.Press(window, Key.Escape);
        await Settle();
        RightClick(window, OvMore(2));
        await Settle();
        Check(MenuLabels(Cur()?.Host?.ItemsSource?.Cast<object>()).FirstOrDefault() == "#2号机 · HT-PR4" && (OvMore(2).Flyout as MenuFlyout)?.IsOpen != true,
            "右键 2号机 «⋯» → 也是设备菜单（不打开 «⋯» 自己的菜单）");
        G1.Press(window, Key.Escape);
        await Settle();

        // ———————————————————— 总览卡片：键盘焦点没有 Fluent 的黑框 ————————————————————
        var tile = Find<PageHost>(window, _ => true).CurrentView!.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("ovtile"));
        tile.Focus(NavigationMethod.Tab);
        await Settle();
        var adorners = AdornerLayer.GetAdornerLayer(tile)?.Children.Where(c => ReferenceEquals(AdornerLayer.GetAdornedElement(c), tile)).ToList() ?? new List<Avalonia.Controls.Control>();
        var ovfocus = tile.GetVisualChildren().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("ovfocus"));
        Check(adorners.Count == 0 && ovfocus?.IsVisible == true && tile.FocusAdorner == null, $"Tab 到总览卡片：只有 a-d 环（Fluent 的焦点装饰 {adorners.Count} 个）");
        await Capture(window, Path.Combine(dir, $"shell-kbd-card-{width}.png"), lines);

        // ———————————————————— 打开着的菜单跟着刷新 ————————————————————
        vm.ApplyDemoParams(DemoParams.Parse(""));   // 不冻结：演示数据每次 OnTick 推进 1 s
        demo.Act(demo.Device(2)!, Core.Guard.DeviceAction.LeaseRequest, null, quiet: true);   // 样稿 sim：申请 5 s 后触屏自动批准
        vm.Fleet.RefreshNow();
        await Settle();
        if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }
        RightClick(window, Row("d2"));
        await Settle();
        var leaseItem = Cur() is { } s2 ? PopupMenus.Focusables(s2).FirstOrDefault(m => (m.Header as string)?.Contains("控制权") == true) : null;
        string before = leaseItem?.Header as string ?? "";
        leaseItem?.Focus(NavigationMethod.Directional);
        for (int i = 0; i < 6; i++) vm.Fleet.OnTick(true);
        vm.Fleet.RefreshNow();
        await Settle();
        var leaseNow = Cur() is { } s3 ? PopupMenus.Focusables(s3).FirstOrDefault(m => (m.Header as string)?.Contains("控制权") == true) : null;
        Check(before == "取消申请控制权" && Cur()?.IsContext == true && ReferenceEquals(leaseNow, leaseItem) && leaseNow?.Header as string == "释放控制权"
              && ReferenceEquals(window.FocusManager?.GetFocusedElement(), leaseItem) && G1.Fv(leaseItem),
            $"2号机 申请中 → 演示 5 s 后批准：打开着的右键菜单原地变成「{leaseNow?.Header}」（原来「{before}」），键盘停着的那一项还是它");
        G1.Press(window, Key.Escape);
        await Settle();
        if (L.ExplorerFloat) { L.CloseFloat(); await Settle(); }
        var pOff = DemoParams.Parse("offline=1");   // 不冻结：断线的重连倒计时每次 OnTick 走 1 s
        vm.Fleet.ResetDemo(pOff);
        vm.ApplyDemoParams(pOff);
        await Settle();
        Click(window, OvMore(1));
        await Settle();
        var stopAll = Cur() is { } s4 ? PopupMenus.Focusables(s4).FirstOrDefault(m => m.Header as string == "全部停止") : null;
        string r0 = stopAll != null ? Behaviors.Pr.GetReason(stopAll) ?? "" : "";
        for (int i = 0; i < 3; i++) vm.Fleet.OnTick(true);
        vm.Fleet.RefreshNow();
        await Settle();
        string r1 = stopAll != null ? Behaviors.Pr.GetReason(stopAll) ?? "" : "";
        Check(r0.Length > 0 && r1.Length > 0 && r0 != r1 && Cur()?.Anchor == OvMore(1), $"1号机 断线：«⋯» 开着时「全部停止」的原因跟着倒计时（「{r0}」→「{r1}」）");
        G1.Press(window, Key.Escape);
        await Settle();
        // 动作也跟着换：带原因的项（守卫命令盖着 MenuRun）原因清掉以后执行的是这一次的动作 —— 2号机 断开 → 开着 «⋯» 重连（「立即重连」正在重连）
        // → 连上（「断开连接…」）→ 点它 = 断开（复核：以前执行的还是重连）
        var pLive = DemoParams.Parse("");
        vm.Fleet.ResetDemo(pLive);
        vm.ApplyDemoParams(pLive);
        await Settle();
        var dLive = vm.Fleet.DemoData!;
        dLive.Act(dLive.Device(2)!, Core.Guard.DeviceAction.Disconnect, null, quiet: true);
        vm.Fleet.RefreshNow();
        await Settle();
        Click(window, OvMore(2));
        await Settle();
        MenuItem? ConnItem() => Cur() is { } cs ? PopupMenus.Focusables(cs).FirstOrDefault(m => m.Header is "连接" or "立即重连" or "断开连接…") : null;
        var connItem = ConnItem();
        string c0 = connItem?.Header as string ?? "";
        dLive.Act(dLive.Device(2)!, Core.Guard.DeviceAction.Reconnect, null, quiet: true);
        vm.Fleet.RefreshNow();
        await Settle();
        string c1 = (connItem?.Header as string ?? "") + (connItem != null && Behaviors.Pr.IsLocked(connItem) ? "（" + Behaviors.Pr.GetReason(connItem) + "）" : "");
        var d2Live = vm.Fleet.Device(2)!;
        for (int i = 0; i < 10 && d2Live.Conn != ConnState.Online; i++) vm.Fleet.OnTick(true);
        vm.Fleet.RefreshNow();
        await Settle();
        bool sameItem = ReferenceEquals(ConnItem(), connItem) && connItem?.Header as string == "断开连接…" && !Behaviors.Pr.IsLocked(connItem);
        if (connItem != null) Click(window, connItem);
        await Settle();
        vm.Fleet.RefreshNow();
        await Settle();
        Check(c0 == "连接" && c1.StartsWith("立即重连（", StringComparison.Ordinal) && sameItem && Cur() == null
              && (d2Live.Conn == ConnState.Disconnected || vm.Dialogs.Current?.Name == "disconnect"),
            $"2号机 «⋯» 开着：「{c0}」→「{c1}」→ 连上「断开连接…」（同一个菜单项），点它 → {(vm.Dialogs.Current?.Name == "disconnect" ? "断开确认" : d2Live.Conn.ToString())}（不是重连）");
        vm.Dialogs.Close();
        await Settle();

        // ———————————————————— 位置：下面放不下时翻到上面（时间轴最下面一行的右键菜单） ————————————————————
        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
        var tlv = Find<TimelineView>(window, _ => true);
        var lowest = tlv.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tllabel") && b.IsEffectivelyVisible && b.DataContext is TlRowVm)
            .OrderByDescending(b => G1.RectIn(b, window).Bottom).Where(b => G1.RectIn(b, window).Bottom < H - 30).FirstOrDefault();
        if (lowest != null)
        {
            var pt = Center(lowest, window);
            RightClick(window, lowest);
            await Settle();
            var lr = PopRect();
            Info($"时间轴右键菜单：Popup 偏移 {(ContextMenus.Last?.Parent as Popup)?.HorizontalOffset:0.#},{(ContextMenus.Last?.Parent as Popup)?.VerticalOffset:0.#}，父 {ContextMenus.Last?.Parent?.GetType().Name}");
            Check(Cur()?.IsContext == true && lr.Bottom <= H - 4 + 0.6 && Math.Abs(lr.Bottom - pt.Y) < 1.1 && lr.Left >= 4,
                $"时间轴靠下的行（指针 y {pt.Y:0}）右键：菜单放不下就翻到指针上面（{lr.Left:0.#},{lr.Top:0.#}–{lr.Bottom:0.#}，窗口高 {H:0}）");
            G1.Press(window, Key.Escape);
            await Settle();
        }
        else Info("时间轴没有看得见的行（跳过「翻到上面」）");

        // ———————————————————— 比窗口还高的菜单（页签很多）：最高窗口高 − 16、翻到上沿 4，多的滚动 ————————————————————
        // 样稿 .pr-menu max-height: calc(100% − 16px) + placeMenu 的 Math.max(4, …)：「›」和菜单栏的「窗口」都列出全部页签
        foreach (var dv in vm.Fleet.Devices.ToList())
        {
            vm.Navigate(Route.Device(dv.K));
            for (int n = 1; n <= 16; n++)
                if (Route.Parse($"reactor/{dv.K}/{n}") is { } rvRoute && TabsVm.Routable(vm.Fleet, rvRoute)) vm.Navigate(rvRoute);
        }
        foreach (var ph in Placeholders.All) vm.Navigate(Route.Placeholder(ph.Key));
        vm.Navigate(Route.Overview);
        await Settle();
        foreach (var (name, open) in new (string, Action)[] { ("「›」", () => Click(window, more)), ("菜单栏「窗口」", () => Click(window, Head("窗口"))) })
        {
            open();
            await Settle();
            window.MouseMove(new Point(W - 2, H - 2));
            await Settle();
            var tall = PopRect();
            var sv = (Cur()?.Root as Visual)?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            Check(Cur() != null && Math.Abs(tall.Top - 4) < 0.6 && Math.Abs(tall.Height - (H - 16)) < 0.6 && sv != null && sv.Extent.Height > sv.Viewport.Height + 1,
                $"{name}（{vm.Tabs.Tabs.Count} 个页签）比窗口还高：{tall.Left:0.#},{tall.Top:0.#} {tall.Width:0.#}×{tall.Height:0.#}（样稿上沿 4、高 {H - 16:0}），"
                + $"里面滚动（{sv?.Viewport.Height:0.#} / {sv?.Extent.Height:0.#}）");
            G1.Press(window, Key.Escape);
            await Settle();
        }

        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
        ShotRunner.G1Shot.Restore(window, focus0, fv0);
        window.MouseMove(new Point(2, 2));
        await Settle();
        var focus1 = window.FocusManager?.GetFocusedElement();
        Check(ReferenceEquals(focus1, focus0) && (focus1 is StyledElement sf1 && sf1.Classes.Contains(":focus-visible")) == fv0,
            $"M6 第 1 组核对跑完：焦点还原成跑之前的 {G1.Desc(focus1)}，指针回到 (2, 2)");
    }

    // ———————————————————— 工具（放在 G1 里：别的组的 ShellChecks.M6G*.cs 可能有同名的工具） ————————————————————

    private static class G1
    {
        internal static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
        {
            var pk = key switch
            {
                Key.Down => PhysicalKey.ArrowDown, Key.Up => PhysicalKey.ArrowUp, Key.Left => PhysicalKey.ArrowLeft, Key.Right => PhysicalKey.ArrowRight,
                Key.Home => PhysicalKey.Home, Key.End => PhysicalKey.End, Key.Enter => PhysicalKey.Enter, Key.Space => PhysicalKey.Space,
                Key.Escape => PhysicalKey.Escape, Key.Tab => PhysicalKey.Tab, Key.F10 => PhysicalKey.F10, Key.Apps => PhysicalKey.ContextMenu, _ => PhysicalKey.None,
            };
            w.KeyPress(key, mods, pk, null);
            w.KeyRelease(key, mods, pk, null);
            Dispatcher.UIThread.RunJobs();
        }

        internal static bool Fv(object? o) => o is StyledElement s && s.Classes.Contains(":focus-visible");

        /// <summary>去掉不断行用的连接符（UiText.Keep 插的 U+2060 / U+FEFF）再比较。</summary>
        internal static string Plain(string? s) => (s ?? "").Replace("\u2060", "").Replace("\uFEFF", "");

        /// <summary>菜单项的焦点环（PART_Focus）显示着。</summary>
        internal static bool FocusRing(MenuItem m) => m.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Focus")?.IsVisible == true;

        /// <summary>控件自己画的底色（菜单项 / 菜单栏项的 PART_LayoutRoot，按钮的 PART_Body；没有时取 Background）；透明 = null。</summary>
        internal static Color? Bg(Avalonia.Controls.Control c)
        {
            var b = c.GetVisualDescendants().OfType<Border>().FirstOrDefault(x => x.Name is "PART_LayoutRoot" or "PART_Body")?.Background ?? (c as TemplatedControl)?.Background;
            return b is ISolidColorBrush s && s.Opacity >= 0.01 && s.Color.A > 1 ? s.Color : null;
        }

        internal static string Desc(object? o) => o switch
        {
            null => "（没有焦点）",
            MenuItem m => "MenuItem「" + m.Header + "」",
            Button b => "Button「" + (b.Content as string ?? Behaviors.Pr.GetIcon(b) ?? b.Name) + "」",
            ListBoxItem li => "ListBoxItem「" + (li.DataContext as XRow)?.Key + "」",
            StyledElement s => s.GetType().Name + (string.IsNullOrEmpty(s.Name) ? "" : "#" + s.Name),
            _ => o.GetType().Name,
        };

        internal static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);
    }
}
#endif
