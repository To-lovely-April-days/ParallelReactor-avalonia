#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;
using ParallelReactor.Control.Views.Pages.Overview;
using ParallelReactor.Control.Views.Pages.Reactor;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 4 组（页签、外框布局、面板、总览读数）的行为核对：
/// <list type="bullet">
/// <item>页签溢出与截图顺序无关：rv-done24 接在不同的截图后面（含「›」写着个数的）收起的页签、「›」的宽度都一样，等于按「›」不写个数的宽度算的结果；1600 上全显示（同样稿）。</item>
/// <item>收进「›」的页签不是 Tab 键的停靠点：Tab 走过页签栏只经过显示着的页签。</item>
/// <item>页签栏的 Tab 顺序同样稿：最后一个页签之后「+」再「›」；拿着焦点的页签收进「›」时焦点移到「›」上。</item>
/// <item>断线设备的反应釜页签：报警红点、提示写反应釜自己的状态，「›」菜单跟着。</item>
/// <item>窗口变宽 / 变窄：1366 打开的浮层到 1920 改成停靠；宽屏停靠的到 1366 收起，导航后照样收着。</item>
/// <item>资源管理器断线细节条整条带提示「连接说明 · 数据停在 …」（悬停真的弹出来）；设备名的行高 20（基线不在半像素上）。</item>
/// <item>放大的反应釜面板：框外 1 px line-strong 的圈和 ShadowDlg 盖到上方的状态条上（同样稿），还原后恢复裁剪；设备页的放大照旧裁在面板里。</item>
/// <item>总览读数「-0.001」：单位放不放得下按小数的前进宽度判断。</item>
/// </list>
/// 最后把演示数据、窗口尺寸和外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task M6G4Checks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var size = new PixelSize((int)Math.Round(window.ClientSize.Width), (int)Math.Round(window.ClientSize.Height));
        async Task Show(string name, string? route = null, string? query = null)
        {
            var spec = route == null ? ShotPlan.All.First(s => s.Name == name) : new ShotPlan.Spec(name, route, query ?? "", new[] { size });
            ShotRunner.Show(window, spec, size);
            await Settle();
            await Settle();
        }
        try
        {
            await G4TabOrderChecks(window, vm, Show, width, Check, Info);
            await G4HiddenTabStopChecks(window, vm, Check, Info);
            await G4TabBarOrderChecks(window, vm, Check, Info);
            await G4FocusCollapseChecks(window, vm, size, Check, Info);
            await G4OfflineTabChecks(window, vm, Show, dir, width, Check, Info, lines);
            await G4ResizeChecks(window, vm, size, Check, Info);
            await G4XoffChecks(window, vm, Show, Check, Info);
            await G4MaxLookChecks(window, vm, Show, dir, width, Check, Info, lines);
            await G4NegativeReadingChecks(window, vm, Show, width, Check, Info);
        }
        finally
        {
            window.Width = size.Width;
            window.Height = size.Height;
            vm.Layout.Update(new Size(size.Width, size.Height));
            vm.Fleet.ResetDemo(p0);
            vm.ApplyDemoParams(p0);
            vm.ResetView();
            await Settle();
        }
    }

    // ---------------- 页签溢出与截图顺序无关 ----------------

    /// <summary>页签栏现在的样子：收起的页签、「›」的宽、显示着的页签的位置。</summary>
    private static string G4TabState(Window window, ShellVm vm)
    {
        var more = Find<Button>(window, b => b.Name == "More");
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var shown = strip.GetVisualDescendants().OfType<NavToggle>().Where(t => t.Bounds.Width > 0)
            .Select(t => (t.DataContext as DocTabVm)?.Id + "@" + t.TranslatePoint(default, window)!.Value.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        return "收起 [" + string.Join(",", vm.Tabs.Tabs.Where(t => t.IsHidden).Select(t => t.Label)) + "]「›」" + more.Bounds.Width + " · " + string.Join(" ", shown);
    }

    private static async Task G4TabOrderChecks(Window window, ShellVm vm, Func<string, string?, string?, Task> show, int width, Action<bool, string> Check, Action<string> Info)
    {
        await show("overview", null, null);
        await show("rv-done24", null, null);
        var fresh = G4TabState(window, vm);
        await show("rv-estop311", null, null);
        await show("rv-done24", null, null);
        var afterEstop = G4TabState(window, vm);
        // 前一张「›」写着个数：多开几个页签挤出去，再拍 rv-done24
        foreach (var r in new[] { "reactor/3/12", "device/2", "reactor/2/3", "placeholder/records" }) vm.Navigate(Route.Parse(r)!);
        await Settle();
        bool hadCount = vm.Tabs.HiddenCount > 0;
        await show("rv-done24", null, null);
        var afterCount = G4TabState(window, vm);
        Info($"rv-done24：{fresh}");
        Check(fresh == afterEstop && fresh == afterCount && hadCount,
            "rv-done24 接在 overview / rv-estop311 / 「›」写着个数的页面后面，页签栏都一样" + (fresh == afterEstop && fresh == afterCount ? "" : $"（{afterEstop} ／ {afterCount}）"));
        if (width == 1600) Check(vm.Tabs.HiddenCount == 0, $"1600：rv-done24 的 8 个页签全显示（同样稿新打开的页面；「›」不写个数时页签区 848，页签共 834）");

        Check(G4LayoutMatches(window, vm, out var why), "rv-done24：" + why);
    }

    /// <summary>
    /// 收起的页签 = 按「›」不写个数时的页签区宽度算的（TabOverflowPanel.Layout，同样稿新打开的页面的 layoutTabs）；「›」写着个数时宽 44 + 20（一位数）。
    /// 页签区宽度 = 页签栏 − 「+」− 44，页签宽度 = 各页签量出来的宽（收起的也量过）。
    /// </summary>
    private static bool G4LayoutMatches(Window window, ShellVm vm, out string why)
    {
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var more = Find<Button>(window, b => b.Name == "More");
        var add = Find<Button>(window, b => b.Name == "Add");
        var bar = more.GetVisualParent() as Avalonia.Controls.Control;
        var kids = strip.Children.ToList();
        var widths = kids.Select(k => k.DesiredSize.Width).ToList();
        int active = kids.FindIndex(k => (k.DataContext as DocTabVm)?.IsActive == true);
        double baseAvail = (bar?.Bounds.Width ?? 0) - add.Bounds.Width - TabOverflowPanel.DefaultMoreWidth;
        var expect = TabOverflowPanel.Layout(widths, baseAvail, active, k => k <= 0 ? 0 : k < 10 ? TabOverflowPanel.DefaultExtra : TabOverflowPanel.DefaultExtra + 9);
        var have = kids.Select(k => !((k.DataContext as DocTabVm)?.IsHidden ?? false)).ToArray();
        int hidden = have.Count(h => !h);
        why = $"收起的 {hidden} 个页签 = 按「›」不写个数的页签区 {baseAvail:0.#} 算（页签 {string.Join(" ", widths)}），「›」{more.Bounds.Width}";
        return expect.SequenceEqual(have) && hidden == vm.Tabs.HiddenCount
               && more.Bounds.Width == TabOverflowPanel.DefaultMoreWidth + (hidden == 0 ? 0 : hidden < 10 ? TabOverflowPanel.DefaultExtra : TabOverflowPanel.DefaultExtra + 9);
    }

    // ---------------- 收进「›」的页签不是 Tab 停靠点 ----------------

    private static async Task G4HiddenTabStopChecks(Window window, ShellVm vm, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        await Settle();
        foreach (var r in new[] { "reactor/3/11", "reactor/3/12", "device/2", "reactor/2/3", "placeholder/records", "placeholder/settings", "reactor/3/5" })
            vm.Navigate(Route.Parse(r)!);
        await Settle();
        Check(G4LayoutMatches(window, vm, out var why), "多开了 7 个页签：" + why);
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var toggles = strip.GetVisualDescendants().OfType<NavToggle>().ToList();
        var hidden = toggles.Where(t => (t.DataContext as DocTabVm)?.IsHidden == true).ToList();
        var shown = toggles.Except(hidden).ToList();
        bool stops = hidden.All(t => !t.IsTabStop && t.GetVisualDescendants().OfType<InputElement>().Where(e => e.Focusable).All(e => !e.IsTabStop)) && shown.All(t => t.IsTabStop);
        Check(hidden.Count > 0 && stops, $"收进「›」的 {hidden.Count} 个页签（连同里面的 ×）不是 Tab 停靠点，显示着的 {shown.Count} 个是");
        var first = shown.First(t => (t.DataContext as DocTabVm)?.Id == "overview");
        first.Focus(NavigationMethod.Tab);
        await Settle();
        var seen = new List<NavToggle>();
        bool onHidden = false;
        for (int i = 0; i < 3 * toggles.Count + 6; i++)
        {
            Tab(window);
            var f = window.FocusManager?.GetFocusedElement() as Visual;
            if (f == null) continue;
            if (hidden.Any(h => ReferenceEquals(h, f) || h.IsVisualAncestorOf(f))) onHidden = true;
            if (f is NavToggle nt && toggles.Contains(nt) && !seen.Contains(nt)) seen.Add(nt);
        }
        Check(!onHidden && shown.Skip(1).All(seen.Contains), $"从「总览」按 Tab：经过显示着的页签（{seen.Count} 个），一次也没落到收起的页签上");
        // 关掉几个页签腾出地方：收起过的页签显示出来，又是 Tab 停靠点（Animation 层撤掉）
        var wasHidden = hidden.Select(t => t.DataContext).ToList();
        foreach (var t in vm.Tabs.Tabs.Where(t => !t.Pinned && !t.IsActive && !t.IsHidden).Take(4).ToList()) vm.Tabs.Close(t);
        await Settle();
        var reshown = Find<TabOverflowPanel>(window, _ => true).GetVisualDescendants().OfType<NavToggle>()
            .Where(t => wasHidden.Contains(t.DataContext) && (t.DataContext as DocTabVm)?.IsHidden == false).ToList();
        Check(reshown.Count > 0 && reshown.All(t => t.IsTabStop), $"收起过又显示出来的 {reshown.Count} 个页签又是 Tab 停靠点");
        vm.ResetView();
        await Settle();
    }

    // ---------------- 页签栏的 Tab 顺序、拿着焦点的页签收进「›」 ----------------

    private static void G4ShiftTab(Window w)
    {
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        w.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>焦点所在的元素：类型#名字「无障碍名称」@x × 宽。</summary>
    private static string G4Desc(Window window, IInputElement? f) => f is Avalonia.Controls.Control c
        ? c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name) + "「" + AutomationProperties.GetName(c) + "」@"
          + (c.TranslatePoint(default, window)?.X ?? -1).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "×" + c.Bounds.Width
        : "（没有焦点）";

    /// <summary>
    /// 页签栏的 Tab 顺序同样稿（.pr-tabs、.pr-tab-add、.pr-tab-more）：从最后一个显示着的页签（跳过它里面的 ×）按 Tab 到「+」再到「›」，
    /// Shift+Tab 倒回来（以前 DockPanel 先放「›」「+」：从最后一个页签直接跳到工具栏，「+」「›」只有绕一圈才到）。
    /// </summary>
    private static async Task G4TabBarOrderChecks(Window window, ShellVm vm, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        await Settle();
        var strip = Find<DocTabStrip>(window, _ => true);
        var add = Find<Button>(window, b => b.Name == "Add");
        var more = Find<Button>(window, b => b.Name == "More");
        Check(ReferenceEquals(strip.MoreButton, more), "页签条的 MoreButton 是「›」（「›」在 Grid 里写在页签条后面，#More 照样绑上）");
        var last = strip.GetVisualDescendants().OfType<NavToggle>().Where(t => t.Bounds.Width > 0)
            .OrderBy(t => t.TranslatePoint(default, window)?.X ?? 0).Last();
        bool InLast(IInputElement? f) => f is Visual v && (ReferenceEquals(v, last) || last.IsVisualAncestorOf(v));
        last.Focus(NavigationMethod.Tab);
        await Settle();
        var seq = new List<IInputElement?>();
        for (int i = 0; i < 8 && seq.Count < 3; i++)
        {
            Tab(window);
            var f = window.FocusManager?.GetFocusedElement();
            if (!InLast(f)) seq.Add(f);
        }
        Check(seq.Count >= 2 && ReferenceEquals(seq[0], add) && ReferenceEquals(seq[1], more),
            $"从最后一个页签「{(last.DataContext as DocTabVm)?.Label}」按 Tab：{string.Join(" → ", seq.Select(f => G4Desc(window, f)))}（同样稿：页签之后「+」再「›」）");
        more.Focus(NavigationMethod.Tab);
        await Settle();
        G4ShiftTab(window);
        var b1 = window.FocusManager?.GetFocusedElement();
        G4ShiftTab(window);
        var b2 = window.FocusManager?.GetFocusedElement();
        Check(ReferenceEquals(b1, add) && InLast(b2), $"从「›」按 Shift+Tab：{G4Desc(window, b1)} → {G4Desc(window, b2)}（回到最后一个页签）");
        vm.ResetView();
        await Settle();
    }

    /// <summary>
    /// 拿着键盘焦点的页签被挤进「›」（窗口 1920 → 1366）：焦点移到「›」上、画焦点框，当前页签不变
    /// （以前焦点留在排成 0 × 0 的页签上：看不见，回车还会打开它；样稿 display: none 让焦点丢掉）。
    /// </summary>
    private static async Task G4FocusCollapseChecks(Window window, ShellVm vm, PixelSize size, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        await G4ResizeTo(window, vm, 1920, 1080);
        foreach (var r in new[] { "device/2", "reactor/2/3", "reactor/2/4" }) vm.Navigate(Route.Parse(r)!);
        await Settle();
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var more = Find<Button>(window, b => b.Name == "More");
        var target = strip.GetVisualDescendants().OfType<NavToggle>().First(t => (t.DataContext as DocTabVm)?.Id == "reactor/2/3");
        bool wideShown = !((target.DataContext as DocTabVm)?.IsHidden ?? true) && target.Bounds.Width > 0;
        target.Focus(NavigationMethod.Tab);
        await Settle();
        bool focusedWide = target.IsFocused;
        await G4ResizeTo(window, vm, 1366, 768);
        var f = window.FocusManager?.GetFocusedElement();
        bool hiddenNarrow = (target.DataContext as DocTabVm)?.IsHidden == true;
        Check(wideShown && focusedWide && hiddenNarrow && ReferenceEquals(f, more) && more.Classes.Contains(":focus-visible") && !target.IsFocused
              && vm.Tabs.Active?.Id == "reactor/2/4",
            $"1920 上 Tab 停在「2号机 · RV3」，窗口变窄到 1366 它收进「›」：焦点移到 {G4Desc(window, f)}（画焦点框），当前页签还是「2号机 · RV4」");
        await G4ResizeTo(window, vm, size.Width, size.Height);
        vm.ResetView();
        await Settle();
    }

    // ---------------- 断线设备的反应釜页签 ----------------

    private static async Task G4OfflineTabChecks(Window window, ShellVm vm, Func<string, string?, string?, Task> show, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        await show("m6g4-offtab", "reactor/3/11", "offline=3&sel=3/11");
        var tab = vm.Tabs.Tabs.Single(t => t.Id == "reactor/3/11");
        var d = vm.Fleet.Device(3)!;
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var toggle = strip.GetVisualDescendants().OfType<NavToggle>().First(t => ReferenceEquals(t.DataContext, tab));
        var off = toggle.GetVisualDescendants().OfType<Controls.IconView>().FirstOrDefault(i => i.Classes.Contains("tab-off"));
        Check(tab.Offline && tab.ErrAlarm && Behaviors.Pr.GetDot(toggle) && off?.IsEffectivelyVisible == true,
            "3号机断线：「3号机 · RV11」页签 = wifi-off + 报警红点（看反应釜自己的超压报警，同样稿 tabInfo）");
        string want = "3号机 · RV11 · 超压报警 · " + d.DataText;
        Check(ToolTip.GetTip(toggle) as string == want, $"页签提示「{ToolTip.GetTip(toggle)}」（应为「{want}」）");
        var entry = MenuModel.TabItems(vm).FirstOrDefault(e => e.Label == "3号机 · RV11");
        Check(entry is { Alarm: true }, "「›」菜单里「3号机 · RV11」同样有红点");
        var r1 = vm.Fleet.Device(3)!.Reactor(1)!;
        vm.Navigate(Route.Reactor(3, 1));
        await Settle();
        var tab1 = vm.Tabs.Tabs.Single(t => t.Id == "reactor/3/1");
        Check(!tab1.ErrAlarm && tab1.Tip == "3号机 · RV1 · " + r1.OwnWord + " · " + d.DataText,
            $"没有报警的「3号机 · RV1」：没有红点，提示写断线前的状态（{tab1.Tip}）");
    }

    // ---------------- 窗口变宽 / 变窄 ----------------

    private static async Task G4ResizeTo(Window window, ShellVm vm, double w, double h)
    {
        window.Width = w;
        window.Height = h;
        vm.Layout.Update(new Size(w, h));
        await Settle();
        await Settle();
    }

    private static async Task G4ResizeChecks(Window window, ShellVm vm, PixelSize size, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        vm.Navigate(Route.Overview);
        await G4ResizeTo(window, vm, 1366, 768);
        var L = vm.Layout;
        var root = Find<Grid>(window, g => g.Name == "Root");
        var scrim = Find<Border>(window, b => b.Name == "XScrim");
        var explorer = Find<ExplorerView>(window, _ => true);
        vm.ToggleExplorer();
        await Settle();
        Check(L.ExplorerFloat && explorer.IsFloating && scrim.IsVisible, "1366：打开浮层资源管理器（遮罩）");
        await G4ResizeTo(window, vm, 1920, 1080);
        Check(L.ExplorerDocked && !L.ExplorerFloat && !explorer.IsFloating && !scrim.IsVisible && root.ColumnDefinitions[1].ActualWidth == 300,
            $"窗口变宽到 1920：浮层改成停靠 300，没有遮罩（列宽 {root.ColumnDefinitions[1].ActualWidth}）");
        await G4ResizeTo(window, vm, 1366, 768);
        Check(!L.ExplorerOpen && !scrim.IsVisible && root.ColumnDefinitions[1].ActualWidth == 0, "再变窄到 1366：收起（不留浮层）");
        vm.Navigate(Route.Device(1));
        await Settle();
        Check(!L.ExplorerOpen, "导航之后照样收着（样稿下一次导航会弹成浮层）");
        await G4ResizeTo(window, vm, 1920, 1080);
        Check(L.ExplorerDocked && root.ColumnDefinitions[1].ActualWidth == 300, "宽回 1920：又停靠");
        await G4ResizeTo(window, vm, size.Width, size.Height);
        vm.ResetView();
        await Settle();
    }

    // ---------------- 资源管理器：断线细节条的提示、设备名的行高 ----------------

    private static async Task G4XoffChecks(Window window, ShellVm vm, Func<string, string?, string?, Task> show, Action<bool, string> Check, Action<string> Info)
    {
        await show("m6g4-xoff", "overview", "offline=1&lease=1:lost&explorer=1");
        var explorer = Find<ExplorerView>(window, _ => true);
        var d = vm.Fleet.Device(1)!;
        var strips = explorer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("xoff") && b.IsEffectivelyVisible).ToList();
        var conn = strips.FirstOrDefault(b => !b.Classes.Contains("grace"));
        var grace = strips.FirstOrDefault(b => b.Classes.Contains("grace"));
        string want = d.ConnText + " · " + d.DataText;
        Check(conn != null && ToolTip.GetTip(conn) as string == want && ToolTip.GetShowOnDisabled(conn),
            $"断线细节条整条的提示「{(conn == null ? "" : ToolTip.GetTip(conn))}」（应为「{want}」，样稿 .pr-xoff 的 title）");
        Check(grace == null || ToolTip.GetTip(grace) == null, $"控制权宽限那一条整条没有提示（样稿只给文字加 title）{(grace == null ? "（没有宽限条）" : "")}");
        if (conn != null)
        {
            // 这一行不可选、整行禁用：提示要 ShowOnDisabled 才弹得出来
            var p = Center(conn, window);
            window.MouseMove(p);
            Dispatcher.UIThread.RunJobs();
            for (int i = 0; i < 12 && !ToolTip.GetIsOpen(conn); i++) { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
            Check(ToolTip.GetIsOpen(conn), "指针停在断线细节条上：提示弹出来（行禁用也照样弹）");
            window.MouseMove(new Point(2, window.Bounds.Height - 2));
            ToolTip.SetIsOpen(conn, false);
            await Settle();
        }
        var names = explorer.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("xname") && t.IsEffectivelyVisible).ToList();
        var bl = names.Select(t => t.TextLayout.TextLines.Count > 0 ? t.TextLayout.TextLines[0].Baseline : double.NaN).ToList();
        Check(names.Count >= 3 && names.All(t => t.LineHeight == 20 && t.Bounds.Height == 20) && bl.All(b => Math.Abs(b - Math.Round(b)) > 0.1 && Math.Abs(b - Math.Round(b) + 0.5) > 0.1 && Math.Abs(b - Math.Round(b) - 0.5) > 0.1),
            $"设备名行高 20、框高 20，基线 {string.Join(" / ", bl.Select(b => b.ToString("0.###")))}（不落在整 / 半像素上）");
    }

    // ---------------- 放大的面板：外圈与阴影 ----------------

    private static (byte R, byte G, byte B) G4Px(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height) return (0, 0, 0);
        var o = y * fb.RowBytes + x * 4;
        byte c0 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o), c1 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 1),
             c2 = System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 2);
        return fb.Format == PixelFormat.Bgra8888 ? (c2, c1, c0) : (c0, c1, c2);
    }

    private static string G4HexPx((byte R, byte G, byte B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static async Task G4MaxLookChecks(Window window, ShellVm vm, Func<string, string?, string?, Task> show, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        await show("rv-alarm21", null, null);
        var pages = Find<PageHost>(window, _ => true);
        var view = pages.CurrentView;
        var curves = view?.GetVisualDescendants().OfType<ReactorCurvesPanel>().FirstOrDefault(c => c.IsEffectivelyVisible);
        var strip = view?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("rvalert") && b.IsEffectivelyVisible);
        var grid = view?.GetVisualDescendants().OfType<ReactorGridPanel>().FirstOrDefault(g => g.IsEffectivelyVisible);
        if (curves == null || strip == null || grid == null) { Check(false, "rv-alarm21：有曲线面板、状态条和面板网格"); return; }
        var frame = curves.PanelControl;
        bool clipFrame = frame.ClipToBounds, clipHost = curves.ClipToBounds;
        Info($"放大前：PrPanel 裁剪 {clipFrame}，放它的 UserControl 裁剪 {clipHost}");
        await Settle();
        var before = window.CaptureRenderedFrame();
        var gp = grid.TranslatePoint(default, window)!.Value;
        int x = (int)Math.Round(gp.X + grid.Bounds.Width / 2), yLine = (int)Math.Round(gp.Y) - 1, yShade = yLine - 4;
        frame.IsMaximized = true;
        await Settle();
        await Settle();
        var after = window.CaptureRenderedFrame();
        var ring = frame.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_MaxRing");
        var chain = new List<Visual>();
        for (Visual? v = frame; v != null && !ReferenceEquals(v, grid); v = v.GetVisualParent()) chain.Add(v);
        Check(ring?.IsEffectivelyVisible == true && chain.All(v => !v.ClipToBounds), $"放大「曲线」：框外的圈显示，面板到网格之间 {chain.Count} 层都不裁剪");
        if (before != null && after != null)
        {
            var lb = G4Px(before, x, yLine); var la = G4Px(after, x, yLine);
            var sb = G4Px(before, x, yShade); var sa = G4Px(after, x, yShade);
            var ls = Theme.Palette.LineStrongColor;
            bool isLine = Math.Abs(la.R - ls.R) <= 3 && Math.Abs(la.G - ls.G) <= 3 && Math.Abs(la.B - ls.B) <= 3;
            bool darker = sa.R + sa.G + sa.B + 6 < sb.R + sb.G + sb.B;
            Check(isLine && darker,
                $"状态条最下一行（y {yLine}）{G4HexPx(lb)} → {G4HexPx(la)}（line-strong），往上 4 px {G4HexPx(sb)} → {G4HexPx(sa)}（阴影压暗；样稿同样：r-line → line-strong，条的底色往下越来越暗）");
            await Capture(window, Path.Combine(dir, $"shell-m6g4-maxring-{width}.png"), lines);
        }
        else Check(false, "放大前后截不到图");
        Esc(window);
        await Settle();
        await Settle();
        var restored = window.CaptureRenderedFrame();
        Check(!frame.IsMaximized && ring?.IsEffectivelyVisible != true && frame.ClipToBounds == clipFrame && curves.ClipToBounds == clipHost
              && (restored == null || before == null || G4Px(restored, x, yLine) == G4Px(before, x, yLine)),
            "Esc 还原：圈不画、面板和放它的 UserControl 照常裁剪，状态条回到原样");

        // 设备页：放大的面板正好铺满页面，圈和阴影照旧裁在面板里（同样稿：.device 裁剪）
        await show("device1", null, null);
        var dv = pages.CurrentView;
        var res = dv?.GetVisualDescendants().OfType<PrPanel>().FirstOrDefault(p => p.Title as string == "共用资源");
        if (res == null) { Check(false, "设备页有「共用资源」面板"); return; }
        res.IsMaximized = true;
        await Settle();
        Check(res.ClipToBounds && res.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "PART_MaxRing" && b.IsEffectivelyVisible),
            "设备页放大「共用资源」：圈在模板里，面板照常裁剪（看不见，同样稿）");
        res.IsMaximized = false;
        await Settle();
    }

    // ---------------- 总览读数「-0.001」 ----------------

    private static async Task G4NegativeReadingChecks(Window window, ShellVm vm, Func<string, string?, string?, Task> show, int width, Action<bool, string> Check, Action<string> Info)
    {
        await show("overview", null, null);
        // 演示数据里没有负的压力读数：同样稿的核对（m6map neg.js）把 1号机、3号机所有装了的反应釜的压力改成 -0.001
        // （DemoChannel 的 setter 是 internal，用反射；只在核对里）。3号机 16 路是紧凑卡：1366 读数行宽 93（样稿 92.5）放得下单位，1920 / 1600（87 / 85，样稿 87 / 84）放不下
        var demo = vm.Fleet.DemoData;
        var prop = typeof(DemoChannel).GetProperty(nameof(DemoChannel.PressureMPa));
        if (demo == null || prop == null) { Check(false, "演示数据里有 1号机、3号机"); return; }
        foreach (var k in new[] { 1, 3 })
            for (int n = 1; n <= 16; n++)
                if (demo.Device(k)?.Channel(n) is { Installed: true } ch) prop.SetValue(ch, -0.001);
        vm.Fleet.RefreshNow();
        await Settle();
        await Settle();
        var rows = window.GetVisualDescendants().OfType<OvReadRow>()
            .Where(r => r.IsEffectivelyVisible && r.Classes.Contains("rp") && r.Children.Count > 1 && (r.Children[0] as TextBlock)?.Text == "-0.001").ToList();
        var facts = new List<string>();
        bool ok = rows.Count > 0;
        foreach (var r in rows)
        {
            var v = (PrText)r.Children[0];
            var u = r.Children[1] as PrText;
            double nat = v.NaturalWidth + OvReadRow.Gap + (u?.NaturalWidth ?? 0);
            bool fits = nat <= r.Bounds.Width + 0.01;
            bool wanted = r.ShowUnit && r.Chosen.Size >= r.ValueSize && u is { IsVisible: true };
            ok &= r.Chosen.Unit == (wanted && fits);
            facts.Add($"{r.Bounds.Width:0.##}:{nat:0.##}{(r.Chosen.Unit ? "+MPa" : "")}");
        }
        Check(ok, $"「-0.001」{rows.Count} 行：单位按小数宽度取舍（宽:数值+3+单位）{string.Join(" ", facts.Distinct())}");
        var compact = rows.Where(r => r.Bounds.Width < 100).ToList();
        if (width == 1366) Check(compact.Count > 0 && compact.All(r => r.Chosen.Unit), $"1366：{compact.Count} 张紧凑卡的「-0.001」都写单位「MPa」（同样稿：92.03 ≤ 92.5）");
        else Check(compact.Count > 0 && compact.All(r => !r.Chosen.Unit), $"{width}：{compact.Count} 张紧凑卡的「-0.001」不写单位（同样稿：列宽放不下）");
        vm.Fleet.ResetDemo(vm.Fleet.Params ?? DemoParams.Default);
        await Settle();
    }
}
#endif
