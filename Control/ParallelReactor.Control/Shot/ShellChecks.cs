#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 新外壳的行为核对（PR_CONTROL_SHELL_CHECKS=1，仅 Debug，演示数据）：在无头窗口里真的点一遍外框 ——
/// 区域尺寸（按窗口尺寸）、菜单的内容与勾选、视图菜单收起时间轴、图标栏开关资源管理器（停靠 / 浮层 + Esc）、页签切换与关闭、
/// 「+」新页签弹出框导航、页签溢出与「›」菜单、带原因的按钮 toast、对话框宿主（未注册的名字提示、遮罩之上的全局停止格仍可点、Esc 关闭）、
/// 面板原地放大 / 关闭 / 重置布局、窗口标题；字符串内容的字跟着自己的控件（没被外面的「X TextBlock」样式盖掉，说明条里的链接是 a-d / t-dis）、
/// 对话框与浮层资源管理器的阴影画在框外、旧样式页面的字符串内容照旧。结果写 shell-checks-&lt;w&gt;.txt（PASS / FAIL / INFO），弹出层另存 shell-popup-*.png；有 FAIL 时退出码 5。
/// </summary>
public static class ShellChecks
{
    public static async Task<(int Fails, List<string> Lines)> RunAsync(Window window, ShellVm vm, string dir, int width)
    {
        var lines = new List<string>();
        int fails = 0;
        void Check(bool ok, string what) { lines.Add((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        void Info(string s) => lines.Add("INFO " + s);
        var L = vm.Layout;
        var root = Find<Grid>(window, g => g.Name == "Root");

        // ---------------- 区域（只看窗口尺寸） ----------------
        Check(window.Title?.Contains("演示数据") == true, $"窗口标题带「演示数据」（{window.Title}）");
        var cols = root.ColumnDefinitions.Select(c => c.ActualWidth).ToArray();
        var rows = root.RowDefinitions.Select(r => r.ActualHeight).ToArray();
        Info($"frame {L.FrameWidth:0.#}×{L.FrameHeight:0.#} scale {L.Scale:0.###} {L.WidthClass} {L.HeightClass} · 列 {string.Join(" / ", cols.Select(c => c.ToString("0.#")))} · 行 {string.Join(" / ", rows.Select(r => r.ToString("0.#")))}");
        bool narrow = L.FrameWidth < 1600, wide = L.FrameWidth >= 1920;
        Check(cols[0] == 64, $"图标栏 64（{cols[0]}）");
        Check(cols[1] == (narrow ? 0 : 300), $"资源管理器 {(narrow ? "收起 0" : "停靠 300")}（{cols[1]}）");
        Check(cols[3] == (wide ? 330 : 300), $"检查器 {(wide ? 330 : 300)}（{cols[3]}）");
        Check(rows[0] == 34 && rows[1] == 44 && rows[4] == 30, $"菜单 34 / 页签 44 / 状态栏 30（{rows[0]} / {rows[1]} / {rows[4]}）");
        Check(rows[3] == (L.FrameHeight >= 1000 ? 220 : 200), $"时间轴 {(L.FrameHeight >= 1000 ? 220 : 200)}（{rows[3]}）");

        // ---------------- 没有可见的 Fluent 模板 ----------------
        // 控件的隐式主题（没设 Theme 时按类型找的资源）还是 FluentTheme 自己的那一个：只允许没有外观的容器（内容 / 列表的承载、窗口）
        var fluent = Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().First();
        var chromeless = new HashSet<Type> { typeof(UserControl), typeof(ContentControl), typeof(ItemsControl), typeof(Window), typeof(Avalonia.Controls.Primitives.TemplatedControl) };
        var fluentTypes = window.GetVisualDescendants().OfType<TemplatedControl>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0 && c.Theme == null && c.TryFindResource(StyleKeyOf(c), out var t) && fluent.TryGetResource(StyleKeyOf(c), Avalonia.Styling.ThemeVariant.Light, out var ft) && ReferenceEquals(t, ft))
            .Select(StyleKeyOf).Distinct().ToList();
        Info("用 Fluent 隐式主题的控件类型：" + (fluentTypes.Count == 0 ? "（无）" : string.Join("、", fluentTypes.Select(t => t.Name))));
        Check(fluentTypes.All(chromeless.Contains), "外壳里没有可见的 Fluent 模板（只剩没有外观的容器）");
        var ovOver = ContentTextOverrides(window);
        Check(ovOver.Count == 0, "总览：按钮 / 页签 / 图标栏里字符串内容的字跟着自己的控件（颜色、字号、字重没被外面的「X TextBlock」样式盖掉）"
            + (ovOver.Count > 0 ? "：" + string.Join("；", ovOver) : ""));

        // ---------------- 菜单 ----------------
        var bar = Find<Menu>(window, _ => true);
        var devMenu = bar.Items.OfType<MenuItem>().First(m => (m.Header as string) == "设备");
        devMenu.Open();
        await Settle();
        var labels = devMenu.Items.Select(o => o switch { Separator => "-", MenuItem m => (m.Classes.Contains("head") ? "#" : "") + m.Header, _ => "?" }).ToList();
        var expect = new[] { "添加设备…", "设备管理…", "-", "#当前：1号机", "释放控制权", "断开连接…", "编辑设置…", "-", "全部停止…", "急停…" };
        Check(labels.SequenceEqual(expect), "设备菜单：" + string.Join(" | ", labels));
        var stopItem = devMenu.Items.OfType<MenuItem>().First(m => (m.Header as string) == "全部停止…");
        var estopItem = devMenu.Items.OfType<MenuItem>().First(m => (m.Header as string) == "急停…");
        Check(stopItem.Classes.Contains("stop") && estopItem.Classes.Contains("danger"), "全部停止 = 深灰方块（stop），急停 = 红字（danger）");
        Check(SavePopup(devMenu, Path.Combine(dir, $"shell-popup-device-{width}.png"), lines), $"设备菜单弹出层截图 shell-popup-device-{width}.png");
        devMenu.Close();
        await Settle();

        var viewMenu = bar.Items.OfType<MenuItem>().First(m => (m.Header as string) == "视图");
        viewMenu.Open();
        await Settle();
        var checkedItems = viewMenu.Items.OfType<MenuItem>().Where(m => m.IsChecked).Select(m => m.Header as string).ToList();
        var expChecked = narrow ? new[] { "时间轴" } : new[] { "设备资源管理器", "时间轴" };
        Check(checkedItems.SequenceEqual(expChecked), "视图菜单勾选：" + string.Join("、", checkedItems));
        Check(SavePopup(viewMenu, Path.Combine(dir, $"shell-popup-view-{width}.png"), lines), $"视图菜单弹出层截图 shell-popup-view-{width}.png");
        var tl = viewMenu.Items.OfType<MenuItem>().First(m => (m.Header as string) == "时间轴");
        tl.Command?.Execute(null);
        viewMenu.Close();
        await Settle();
        Check(L.TimelineCollapsed && root.RowDefinitions[3].ActualHeight == 40, $"视图 › 时间轴：收起为 40（{root.RowDefinitions[3].ActualHeight}）");
        vm.ToggleTimeline();
        await Settle();
        Check(!L.TimelineCollapsed && root.RowDefinitions[3].ActualHeight == rows[3], "再点一次展开");

        // ---------------- 图标栏「设备」开关资源管理器 ----------------
        var explorer = Find<ExplorerView>(window, _ => true);
        var devToggle = Find<NavToggle>(window, t => t.Content as string == "设备");
        await Settle();
        var noFloat = narrow ? window.CaptureRenderedFrame() : null;   // 浮层打开前的整窗截图（量阴影用）
        Click(window, devToggle);
        await Settle();
        if (narrow)
        {
            Check(L.ExplorerFloat && explorer.IsVisible && explorer.Bounds.Width == 320 && explorer.IsFloating, $"窄屏：打开 320 的浮层（{explorer.Bounds.Width}）");
            Check(devToggle.Classes.Contains("open"), "「设备」图标亮起（open）");
            await Capture(window, Path.Combine(dir, $"shell-float-{width}.png"), lines);
            // 浮层的阴影（ShadowPop 0 8 28，样稿 .pr-app.x-float .pr-explorer）：右边框外由深到浅（UserControl 默认裁剪会把它整个裁掉）
            if (noFloat != null && window.CaptureRenderedFrame() is { } withFloat)
            {
                var xr = explorer.TranslatePoint(new Point(explorer.Bounds.Width, 0), window)!.Value;
                int edge = (int)Math.Round(xr.X), top = (int)xr.Y + 60, bottom = (int)(xr.Y + explorer.Bounds.Height) - 60;
                var pts = Enumerable.Range(0, 12).Select(i => top + i * (bottom - top) / 11).Select(y => (new PixelPoint(edge + 3, y), new PixelPoint(edge + 70, y)));
                var (n, f) = ShadeRatios(noFloat, withFloat, pts);
                Check(f - n >= 0.02, $"浮层右边框外有阴影：框外 3 px 的亮度比 {n:0.###}、70 px 处 {f:0.###}（只有遮罩时两者一样）");
            }
            else Check(false, "浮层阴影：截不到图");
            Esc(window);
            await Settle();
            Check(!L.ExplorerFloat && !explorer.IsVisible, "Esc 收起浮层");
            // 点遮罩收起浮层（样稿：click 落在 #pr-xscrim 上），这一下不传给下面；收起之后图标栏、工作区照常可点
            Click(window, devToggle);
            await Settle();
            var scrim = Find<Border>(window, b => b.Name == "XScrim");
            var wsPt = Center(Find<PageHost>(window, _ => true), window);
            var under = window.InputHitTest(wsPt) as Visual;
            Check(L.ExplorerFloat && under != null && under.GetSelfAndVisualAncestors().Contains(scrim), "浮层打开时遮罩盖住工作区（点不到下面）");
            var routeBefore = vm.Route.Id;
            ClickAt(window, wsPt);
            await Settle();
            Check(!L.ExplorerFloat && !explorer.IsVisible && vm.Route.Id == routeBefore, $"点遮罩 → 收起浮层，页面不变（{vm.Route.Id}）");
            Click(window, Find<NavToggle>(window, t => t.Content as string == "曲线"));
            await Settle();
            Check(vm.Route.Id == "placeholder/curves", $"收起后点图标栏「曲线」照常导航（{vm.Route.Id}）");
            vm.Navigate(Route.Overview);
            await Settle();
        }
        else
        {
            Check(!L.ExplorerDocked && !explorer.IsVisible && root.ColumnDefinitions[1].ActualWidth == 0, "宽屏：收起资源管理器（列宽 0）");
            Click(window, devToggle);
            await Settle();
            Check(L.ExplorerDocked && explorer.IsVisible && root.ColumnDefinitions[1].ActualWidth == 300, "再点一次停靠 300");
        }

        // ---------------- 页签 ----------------
        var strip = Find<TabOverflowPanel>(window, _ => true);
        NavToggle TabOf(string id) => strip.GetVisualDescendants().OfType<NavToggle>().First(t => (t.DataContext as DocTabVm)?.Id == id);
        Click(window, TabOf("device/1"));
        await Settle();
        Check(vm.Route.Id == "device/1" && vm.Inspector.PathAlias == "/1号机" && !vm.Sidebar.OverviewOn, $"点「1号机」页签 → {vm.Route.Id}，检查器 {vm.Inspector.PathAlias}");
        Click(window, TabOf("device/1"));
        await Settle();
        Check(vm.Route.Id == "device/1" && TabOf("device/1").IsChecked == true, "再点当前页签仍是当前（不会被取消选中）");
        var close = TabOf("device/1").GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close");
        Click(window, close);
        await Settle();
        Check(vm.Tabs.Tabs.All(t => t.Id != "device/1") && vm.Route.Id == "overview", $"× 关闭当前页签 → 回到左边的 {vm.Route.Id}");
        Check(TabOf("overview").GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close").IsVisible == false, "总览（固定页签）没有 ×");

        // ---------------- 「+」新页签 ----------------
        var add = Find<Button>(window, b => b.Name == "Add");
        Click(window, add);
        await Settle();
        var flyout = add.Flyout as Flyout;
        var pop = flyout?.Content as NewTabPopover;
        Check(flyout?.IsOpen == true && pop != null, "「+」打开新页签弹出框");
        if (pop != null)
        {
            Check(Save(pop, Path.Combine(dir, $"shell-popup-newtab-{width}.png"), lines), $"「+」弹出框截图 shell-popup-newtab-{width}.png");
            var chips = pop.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nt-rv")).ToList();
            Check(chips.Count == 8 + 4 + 16, $"反应釜芯片 8 + 4 + 16（{chips.Count}）");
            Check(chips.Skip(4).Take(4).All(b => b.Classes.Contains("un") && !string.IsNullOrEmpty(Behaviors.Pr.GetReason(b))), "1号机 RV5–RV8 未安装：虚线 + 原因");
            chips[2].Command?.Execute(null);
            await Settle();
            Check(vm.Route.Id == "reactor/1/3" && flyout!.IsOpen == false, $"点 1号机 RV3 → {vm.Route.Id}，弹出框关闭");
        }

        // ---------------- 页签溢出 ----------------
        foreach (var r in new[] { "reactor/3/11", "reactor/3/12", "device/2", "reactor/2/3", "placeholder/records", "placeholder/settings", "reactor/3/5" })
            vm.Navigate(Route.Parse(r)!);
        await Settle();
        var more = Find<Button>(window, b => b.Name == "More");
        Info($"页签 {vm.Tabs.Tabs.Count} 个，未显示 {vm.Tabs.HiddenCount}：{string.Join("、", vm.Tabs.Tabs.Where(t => t.IsHidden).Select(t => t.Label))}");
        Check(vm.Tabs.HiddenCount > 0 && more.Classes.Contains("has-more") && Behaviors.Pr.GetCount(more) == vm.Tabs.HiddenCount.ToString(), $"放不下时「›」写未显示个数（{Behaviors.Pr.GetCount(more)}）");
        Check(!vm.Tabs.Tabs.First().IsHidden && !vm.Tabs.Active!.IsHidden, "总览和当前页签始终显示");
        var visibleRight = strip.GetVisualDescendants().OfType<NavToggle>().Where(t => t.Bounds.Width > 0).Max(t => t.TranslatePoint(new Point(t.Bounds.Width, 0), window)!.Value.X);
        var moreLeft = more.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var addLeft = add.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Check(visibleRight <= addLeft + 0.5, $"显示的页签不压到「+」（{visibleRight:0.#} ≤ {addLeft:0.#}），「›」在 {moreLeft:0.#}");
        var hiddenTab = vm.Tabs.Tabs.FirstOrDefault(t => t.IsHidden);
        for (int round = 1; round <= 2; round++)   // 第一次和再次打开都要有菜单项（Avalonia 11.1：Opening 里换 ItemsSource 会打开成空白条）
        {
            Click(window, more);
            await Settle();
            var mf = more.Flyout as MenuFlyout;
            // 看弹出层里真正排出来的 presenter（不是 MenuFlyout.Items 这份逻辑清单：清单有、弹出层空着的情况它看不出来）
            var mfp = PresenterOf(mf);
            var tabItems = mfp?.GetRealizedContainers().OfType<MenuItem>().ToList() ?? new List<MenuItem>();
            Check(mf?.IsOpen == true && mfp != null && mfp.ItemCount == vm.Tabs.Tabs.Count && tabItems.Count == vm.Tabs.Tabs.Count && mfp.Bounds.Height > 100,
                $"第 {round} 次打开「›」：弹出层实际显示 {mfp?.ItemCount} 项、排出 {tabItems.Count} 个菜单项（{mfp?.Bounds.Width:0}×{mfp?.Bounds.Height:0}）");
            Check(tabItems.Count(m => Behaviors.Pr.GetHint(m) == "未显示") == vm.Tabs.HiddenCount,
                $"「›」菜单列出全部 {tabItems.Count} 个页签，{tabItems.Count(m => Behaviors.Pr.GetHint(m) == "未显示")} 个写「未显示」");
            Check(tabItems.Count(m => m.IsChecked) == 1 && tabItems.Any(m => Behaviors.Pr.GetDot(m)), "当前页签打勾；有错误报警的页签有红点（3号机 · RV11）");
            if (round == 1)
            {
                Check(mfp != null && Save(mfp, Path.Combine(dir, $"shell-popup-tabs-{width}.png"), lines), $"「›」弹出层截图 shell-popup-tabs-{width}.png");
                mf?.Hide();
                await Settle();
                continue;
            }
            // 点一个未显示的页签 → 切过去（它变成当前页签，所以显示出来），菜单关闭
            var item = tabItems.FirstOrDefault(m => Behaviors.Pr.GetHint(m) == "未显示" && (m.Header as string) == hiddenTab?.Label);
            if (item != null) { Click(window, item); await Settle(); }
            Check(item != null && hiddenTab != null && vm.Route.Id == hiddenTab.Id && mf?.IsOpen != true && !hiddenTab.IsHidden,
                $"点「›」菜单里未显示的「{hiddenTab?.Label}」→ 切到 {vm.Route.Id}，菜单关闭，页签显示出来");
            // 收进「›」时页签排成 0 × 0，名字按 0 宽排过；显示出来以后名字要重新排（11.1 只在变窄时重排，见 Controls/TextRelayout）
            var shownTab = strip.GetVisualDescendants().OfType<NavToggle>().FirstOrDefault(t => (t.DataContext as DocTabVm)?.Id == hiddenTab?.Id);
            var names = shownTab?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList() ?? new List<TextBlock>();
            Check(names.Count > 0 && names.All(t => t.TextLayout.TextLines.Sum(l => l.Length) >= t.Text!.TrimEnd().Length && t.TextLayout.WidthIncludingTrailingWhitespace > 1),
                "显示出来的页签名字是完整的：" + string.Join(" + ", names.Select(t => $"「{t.Text}」{t.TextLayout.WidthIncludingTrailingWhitespace:0} px")));
            if (mf?.IsOpen == true) { mf.Hide(); await Settle(); }
        }
        // 收进「›」的页签再显示出来（关掉前面几个页签腾出地方；不是当前页签，样式不变、不会顺带重新测量），名字要完整 ——
        // 收起时页签排成 0 × 0，名字按 0 宽排过，11.1 只在变窄时重排（Controls/TextRelayout）
        List<TextBlock> TabNames(string id) => strip.GetVisualDescendants().OfType<NavToggle>().Where(t => (t.DataContext as DocTabVm)?.Id == id)
            .SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>()).Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
        var wasHidden = vm.Tabs.Tabs.Where(t => t.IsHidden).Select(t => t.Id).ToList();
        foreach (var t in vm.Tabs.Tabs.Where(t => !t.Pinned && !t.IsActive && !t.IsHidden).Take(3).ToList()) vm.Tabs.Close(t);
        await Settle();
        var reshown = vm.Tabs.Tabs.Where(t => wasHidden.Contains(t.Id) && !t.IsHidden && !t.IsActive).ToList();
        var blank = reshown.SelectMany(t => TabNames(t.Id)).Where(tb => tb.TextLayout.TextLines.Sum(l => l.Length) < tb.Text!.TrimEnd().Length || tb.TextLayout.WidthIncludingTrailingWhitespace <= 1).ToList();
        Check(reshown.Count > 0 && blank.Count == 0,
            $"收进「›」又显示出来的 {reshown.Count} 个页签（{string.Join("、", reshown.Select(t => t.Label))}）名字完整" + (blank.Count > 0 ? "；空白：" + string.Join("、", blank.Select(b => b.Text)) : ""));

        // ---------------- 原因 → toast ----------------
        vm.Timeline.ZoomCommand.Execute("-1");
        await Settle();
        var zoomIn = Find<Button>(window, b => (ToolTip.GetTip(b) as string) == "放大（缩短时间范围）" || Behaviors.Pr.GetIcon(b) == "zoomin");
        Check(Behaviors.Pr.GetReason(zoomIn) == "已是最短 1 h", $"跨度 1 h 时「放大」带原因（{Behaviors.Pr.GetReason(zoomIn)}）");
        int before = vm.Toasts.Items.Count;
        Click(window, zoomIn);
        await Settle();
        Check(vm.Toasts.Items.Count == before + 1 && vm.Toasts.Items.Last() is { Kind: Services.ToastKind.Warn, Text: "已是最短 1 h" }, "点带原因的按钮 → toast 说明原因（warn）");
        await Capture(window, Path.Combine(dir, $"shell-toast-{width}.png"), lines);
        vm.Timeline.ZoomCommand.Execute("1");
        vm.Toasts.Clear();

        // ---------------- 对话框宿主 ----------------
        vm.OpenDialog("nope");
        await Settle();
        Check(!vm.Dialogs.IsOpen && vm.Toasts.Items.LastOrDefault()?.Text == "对话框 nope 未定义", "未注册的对话框：toast「对话框 nope 未定义」（同样稿）");
        // M3：样稿的对话框都注册了（演示与真机同一套，Shell/Dialogs）
        foreach (var name in new[] { "add", "devices", "lease", "estop", "stopall", "edit", "rename", "remove", "disconnect" })
            Check(vm.Dialogs.IsRegistered(name), $"对话框「{name}」已注册");
        vm.Toasts.Clear();
        vm.Dialogs.Register("probe", _ => new DialogVm { Name = "probe", Title = "设备管理", Sub = "宿主核对（对话框在 M3）", Icon = "list", Width = 560 });
        await Settle();
        var noDialog = window.CaptureRenderedFrame();   // 对话框打开前的整窗截图（量阴影用）
        vm.OpenDialog("probe");
        await Settle();
        var host = Find<DialogHost>(window, _ => true);
        var frame = host.GetVisualDescendants().OfType<DialogFrame>().FirstOrDefault();
        Check(vm.Dialogs.IsOpen && host.IsVisible && frame != null && Math.Abs(frame.Bounds.Width - 560) < 1, $"对话框打开：遮罩 + 居中的 DialogFrame（宽 {frame?.Bounds.Width:0}）");
        // 对话框的阴影（ShadowDlg 0 10 45，样稿 .pr-modal）：框下方由深到浅（放框的 ContentControl 默认裁剪会把它整个裁掉）
        if (frame != null && noDialog != null && window.CaptureRenderedFrame() is { } withDialog)
        {
            var fb = frame.TranslatePoint(new Point(0, frame.Bounds.Height), window)!.Value;
            int y = (int)Math.Ceiling(fb.Y);
            var pts = Enumerable.Range(0, 12).Select(i => (int)fb.X + 30 + i * (int)(frame.Bounds.Width - 60) / 11).Select(x => (new PixelPoint(x, y + 4), new PixelPoint(x, y + 90)));
            var (n, f) = ShadeRatios(noDialog, withDialog, pts);
            Check(f - n >= 0.02, $"对话框框外有阴影：框下 4 px 的亮度比 {n:0.###}、90 px 处 {f:0.###}（只有遮罩时两者一样）");
        }
        else Check(false, "对话框阴影：截不到图");
        var dlgOver = ContentTextOverrides(host);
        Check(dlgOver.Count == 0, "对话框里字符串内容的字跟着自己的控件" + (dlgOver.Count > 0 ? "：" + string.Join("；", dlgOver) : ""));
        var stopCell = Find<GlobalStopCell>(window, _ => true);
        var estopBtn = stopCell.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("estop"));
        var hit = window.InputHitTest(Center(estopBtn, window)) as Visual;
        Check(hit != null && hit.GetSelfAndVisualAncestors().Contains(estopBtn), $"遮罩之上的全局「急停」仍能点到（命中 {hit?.GetType().Name}）");
        var hitWs = window.InputHitTest(new Point(window.Bounds.Width / 2, 120)) as Visual;
        Check(hitWs != null && hitWs.GetSelfAndVisualAncestors().OfType<DialogHost>().Any(), "工作区被遮罩挡住");
        await Capture(window, Path.Combine(dir, $"shell-dialog-{width}.png"), lines);
        Esc(window);
        await Settle();
        Check(!vm.Dialogs.IsOpen && !host.IsVisible, "Esc 关闭对话框");

        // M3 复核：遮罩只挡指针 —— 键盘焦点要进对话框、Tab 只在对话框里循环（碰不到下面的页面），关闭后焦点回到打开之前的地方
        var pageBtn = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Focusable && !host.IsVisualAncestorOf(b));
        pageBtn.Focus();
        await Settle();
        vm.OpenDialog("rename", "2");
        await Settle();
        Check(host.FocusInside, "对话框打开：键盘焦点移进对话框（看不见的锚点，没有焦点外观）");
        var tabbed = new HashSet<object>();
        bool allInside = true;
        for (int i = 0; i < 12; i++)
        {
            Tab(window);
            await Settle();
            allInside &= host.FocusInside;
            if (window.FocusManager?.GetFocusedElement() is { } fe) tabbed.Add(fe);
        }
        Check(allInside && tabbed.Count >= 3 && tabbed.OfType<TextBox>().Any(),
            $"Tab 只在对话框里循环（{tabbed.Count} 个控件：{string.Join("、", tabbed.Select(x => x.GetType().Name).Distinct())}），到不了下面的页面");
        Esc(window);
        await Settle();
        Check(!vm.Dialogs.IsOpen && ReferenceEquals(window.FocusManager?.GetFocusedElement(), pageBtn), "关闭对话框后焦点回到打开之前的按钮");

        // ---------------- 面板放大 / 关闭 / 重置布局 ----------------
        vm.Navigate(Route.Device(1));
        await Settle();
        var pages = Find<PageHost>(window, _ => true);
        var view = pages.CurrentView!;
        PrPanel PanelOf(string title) => view.GetVisualDescendants().OfType<PrPanel>().First(p => p.Title as string == title);
        var res = PanelOf("共用资源");
        Click(window, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max"));
        await Settle();
        var others = view.GetVisualDescendants().OfType<PrPanel>().Where(p => !ReferenceEquals(p, res)).ToList();
        Check(res.IsMaximized && Math.Abs(res.Bounds.Width - pages.Bounds.Width) < 1.5 && Math.Abs(res.Bounds.Height - pages.Bounds.Height) < 1.5 && others.All(p => !p.IsEffectivelyVisible),
            $"「共用资源」原地放大铺满工作区（{res.Bounds.Width:0}×{res.Bounds.Height:0} / {pages.Bounds.Width:0}×{pages.Bounds.Height:0}），其它面板隐藏");
        await Capture(window, Path.Combine(dir, $"shell-max-{width}.png"), lines);
        Esc(window);
        await Settle();
        Check(!res.IsMaximized && others.All(p => p.IsEffectivelyVisible) && res.Bounds.Width < pages.Bounds.Width - 10, "Esc 还原，其它面板回来");
        Click(window, res.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close"));
        await Settle();
        Check(res.IsClosed && !res.IsVisible && vm.Toasts.Items.LastOrDefault()?.Text == "已关闭「共用资源」· 视图 → 重置布局 可恢复", "关闭面板 → 隐藏 + toast");
        vm.ResetLayout();
        await Settle();
        Check(!res.IsClosed && res.IsVisible && vm.Toasts.Items.LastOrDefault()?.Text == "布局已重置", "视图 › 重置布局 → 面板回来 + toast「布局已重置」");
        vm.Toasts.Clear();

        // ---------------- 总览分区：「⋯」设备菜单、「打开」 ----------------
        vm.Navigate(Route.Overview);
        await Settle();
        var ov = pages.CurrentView!;
        var moreBtn = ov.GetVisualDescendants().OfType<Button>().First(b => Behaviors.Pr.GetIcon(b) == "more");
        var expDev = new[] { "#1号机 · HT-PR8 · 现场", "打开设备页", "-", "全部停止（不需要控制权）", "-", "释放控制权", "断开连接…", "-", "重命名…", "编辑设置…", "移除…", "-", "急停 1号机…" };
        for (int round = 1; round <= 2; round++)
        {
            Click(window, moreBtn);
            await Settle();
            var dmf = moreBtn.Flyout as MenuFlyout;
            var dp = PresenterOf(dmf);
            var shown = dp?.GetRealizedContainers().ToList() ?? new List<Avalonia.Controls.Control>();
            var devItems = MenuLabels(shown.Cast<object>()).Select(l => l.TrimEnd('⊘')).ToList();
            Check(dmf?.IsOpen == true && dp != null && dp.ItemCount == expDev.Length && devItems.SequenceEqual(expDev) && dp.Bounds.Height > 100,
                $"第 {round} 次打开总览「⋯」：弹出层实际显示 {dp?.ItemCount} 项（{dp?.Bounds.Width:0}×{dp?.Bounds.Height:0}）= 设备菜单：" + string.Join(" | ", devItems));
            if (round == 1)
            {
                Check(dp != null && Save(dp, Path.Combine(dir, $"shell-popup-ovmore-{width}.png"), lines), $"总览「⋯」弹出层截图 shell-popup-ovmore-{width}.png");
                dmf?.Hide();
                await Settle();
                continue;
            }
            var open = shown.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "打开设备页");
            if (open != null) { Click(window, open); await Settle(); }
            Check(open != null && vm.Route.Id == "device/1" && dmf?.IsOpen != true, $"点「⋯」菜单的「打开设备页」→ {vm.Route.Id}，菜单关闭");
            if (dmf?.IsOpen == true) { dmf.Hide(); await Settle(); }
        }
        vm.Navigate(Route.Overview);
        await Settle();
        ov = pages.CurrentView!;
        Click(window, ov.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "打开"));
        await Settle();
        Check(vm.Route.Id == "device/1", $"总览「打开」→ {vm.Route.Id}");

        // ---------------- 状态栏报警项 ----------------
        Click(window, Find<Button>(window, b => b.Theme == Application.Current!.FindResource("PrStatusButton")));
        await Settle();
        Check(vm.SelectedDevice?.K == 3 && vm.SelectedRv == 11 && vm.Inspector.PathTip == "/3号机/RV11", $"点状态栏报警项 → 选中有错误报警的 3号机 RV11（{vm.Inspector.PathTip}）");

        // ---------------- 资源管理器树：单击选中、双击打开页签、展开箭头 ----------------
        if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }
        var tree = Find<ListBox>(window, l => l.Name == "Tree");
        Avalonia.Controls.Control Row(string key)
        {
            int i = vm.Explorer.Rows.ToList().FindIndex(r => r.Key == key);
            tree.ScrollIntoView(i);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return tree.GetVisualDescendants().OfType<ListBoxItem>().First(c => (c.DataContext as XRow)?.Key == key);
        }
        Check(vm.Explorer.Rows.OfType<XReactorRow>().Any(r => r.Key == "r3/11" && r.IsSelected), "选中 3号机 RV11 → 树里 16 路的 3号机展开、RV11 行高亮");
        Click(window, Row("r1/2"));
        await Settle();
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == 2 && vm.Route.Id == "device/1", $"单击 1号机 · RV2 行 → 只选中（检查器 {vm.Inspector.PathTip}），页面不变（{vm.Route.Id}）");
        var head2 = Row("d2");
        var p2 = Center(head2, window);
        window.MouseMove(p2);
        window.MouseDown(p2, MouseButton.Left);
        window.MouseUp(p2, MouseButton.Left);
        window.MouseDown(p2, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(p2, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        if (vm.Route.Id != "device/2")
        {
            // 无头平台的双击计数：直接发 DoubleTapped（与真实双击走同一个处理器）
            head2.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, new PointerEventArgs(InputElement.PointerMovedEvent, head2, new Pointer(1, PointerType.Mouse, true), window, p2, 0, default, KeyModifiers.None)));
            await Settle();
            Info("双击：无头平台没有合成双击，改发 DoubleTapped");
        }
        Check(vm.Route.Id == "device/2" && vm.SelectedDevice?.K == 2, $"双击 2号机 设备头 → 打开设备页（{vm.Route.Id}）");
        if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }   // 浮层（窄屏）在导航后收起了：再打开
        int rowsBefore = vm.Explorer.Rows.Count;
        var twist = Row("d1").GetVisualDescendants().OfType<Button>().First();
        Click(window, twist);
        await Settle();
        Check(vm.Explorer.Rows.Count < rowsBefore && !vm.Explorer.Rows.Any(r => r.Key == "r1/1") && vm.Route.Id == "device/2",
            $"点 1号机 的展开箭头 → 收起（行 {rowsBefore} → {vm.Explorer.Rows.Count}），不选中、不导航");
        Click(window, Row("d1").GetVisualDescendants().OfType<Button>().First());
        await Settle();
        Check(vm.Explorer.Rows.Any(r => r.Key == "r1/1"), "再点 → 展开");
        await Capture(window, Path.Combine(dir, $"shell-explorer-{width}.png"), lines);
        await InspectorAndTimeline(window, vm, dir, width, Check, Info, lines);
        return (fails, lines);
    }

    private static List<string> MenuLabels(IEnumerable<object>? items) =>
        (items ?? Array.Empty<object>()).Select(o => o switch { Separator => "-", MenuItem m => (m.Classes.Contains("head") ? "#" : "") + m.Header + (Behaviors.Pr.GetHint(m) is { } h ? "（" + h + "）" : "") + (string.IsNullOrEmpty(Behaviors.Pr.GetReason(m)) ? "" : "⊘"), _ => "?" }).ToList();

    private static void RightClick(Window w, Visual v)
    {
        var p = Center(v, w);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>检查器（设备 / 反应釜 / 未安装、控制权卡的操作、「不是当前页签」提示条、名称逐级缩字号）、时间轴（分组、展开、单击选中、跟随、吸顶）、右键菜单（资源管理器、时间轴、页签）。</summary>
    private static async Task InspectorAndTimeline(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var L = vm.Layout;
        vm.ResetView();
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);

        // ---------------- 检查器 ----------------
        Check(vm.Inspector.Body is DeviceInspectorVm { HasHead: true } dv0 && dv0.SharedStir && dv0.ShowLog, "总览：检查器是 1号机 的设备检查器（共用搅拌、报警 / 最近事件分区）");
        var lease = (vm.Inspector.Body as DeviceInspectorVm)!.Lease;
        Check(lease.ShowLease && lease.LeaseState == LeaseState.Remote && lease.BtnLabel == "释放" && !lease.HasCta, $"控制权卡：远程控制中 + 「释放」（{lease.LeaseText} / {lease.BtnLabel}）");
        var release = insp.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "释放");
        Click(window, release);
        await Settle();
        Check(lease.LeaseState == LeaseState.Local && lease.HasCtaPrimary && lease.CtaLabel == "申请控制权", $"点「释放」→ 本地只读，整行主要按钮「申请控制权」（{lease.LeaseText}）");
        var dv = (DeviceInspectorVm)vm.Inspector.Body!;
        Check(!string.IsNullOrEmpty(dv.RunAll.Reason) && string.IsNullOrEmpty(dv.StopAll.Reason) && string.IsNullOrEmpty(dv.Estop.Reason) && !string.IsNullOrEmpty(dv.RpmReason),
            $"本地只读：「全部运行」与转速框带原因（{dv.RunAll.Reason}），「全部停止」「急停」照常可用");
        await Capture(window, Path.Combine(dir, $"shell-insp-local-{width}.png"), lines);
        Click(window, insp.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "申请控制权"));
        await Settle();
        // M3：检查器「申请控制权」先打开申请控制权对话框（样稿 data-dialog="lease"），「发送申请」才发出申请；对话框留着，换成倒计时
        Check(vm.Dialogs.Current is ParallelReactor.Control.Shell.Dialogs.LeaseDialogVm { IsLocal: true }, "点「申请控制权」→ 打开「申请控制权 · 1号机」对话框（发送前的说明）");
        if (vm.Dialogs.Current is ParallelReactor.Control.Shell.Dialogs.LeaseDialogVm ld)
        {
            ld.SendButton.Command.Execute(null);
            await Settle();
            Check(ld.IsRequested && ld.Remain > 0 && vm.Dialogs.IsOpen, $"「发送申请」→ 对话框换成倒计时（剩 {ld.RemainText} s）");
            await Capture(window, Path.Combine(dir, $"shell-lease-dialog-{width}.png"), lines);
            ld.WaitButton.Command.Execute(null);   // 后台等待
            await Settle();
        }
        Check(!vm.Dialogs.IsOpen && lease.IsRequested && lease.CtaLabel == "取消申请" && !lease.CtaPrimary, $"「后台等待」→ 关闭对话框；控制权卡申请中（{lease.LeaseText}），整行「取消申请」");
        Click(window, insp.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "取消申请"));
        await Settle();
        Check(lease.LeaseState == LeaseState.Local, "「取消申请」→ 回到本地只读");
        vm.ApplyDemoParams(Core.Demo.DemoParams.Parse("lease=1:remote&freeze=1"));
        await Settle();
        Check(lease.LeaseState == LeaseState.Remote, "（还原：1号机 远程控制中）");

        vm.Select(1, 1);
        await Settle();
        var rv = vm.Inspector.Body as ReactorInspectorVm;
        Check(rv != null && rv.EnabledCell && rv.RunCell && rv.HeatingCell && rv.ProgCell && rv.HasProgram && rv.Steps?.Count == 4,
            "1号机 · RV1：已启用 / 反应中 / 加热中 / 程序运行中 都是状态格（不画灰按钮），程序卡 4 段步骤条");
        Check(rv != null && rv.HeatReason == null && rv.SpValue == 120 && !string.IsNullOrEmpty(rv.ProgLoad.Reason), $"设定温度框 120.0、可设；程序运行中「下发」带原因（{rv?.ProgLoad.Reason}）");
        // 说明条里的链接按钮（PrLink「设置」）：紫色 a-d —— 说明条文字的样式（14 px t-2）不能盖到按钮里的字；带原因（:reason）时 t-dis
        var rvOver = ContentTextOverrides(insp);
        Check(rvOver.Count == 0, "1号机 · RV1 检查器：按钮 / 链接里字符串内容的字跟着自己的控件" + (rvOver.Count > 0 ? "：" + string.Join("；", rvOver) : ""));
        var setLink = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "设置" && b.IsEffectivelyVisible);
        var setText = setLink?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "设置");
        Check(setLink?.Theme == Application.Current!.FindResource("PrLink") && setText != null && SameBrush(setText.Foreground, Theme.Palette.ADBrush),
            $"搅拌说明条的「设置」是 PrLink、字是紫色 a-d（{Hex(setText?.Foreground)}）");
        if (setLink != null && setText != null)
        {
            Behaviors.Pr.SetReason(setLink, "（核对：带原因的链接）");
            await Settle();
            var locked = setText.Foreground;
            Behaviors.Pr.SetReason(setLink, null);
            await Settle();
            Check(SameBrush(locked, Theme.Palette.TDisBrush) && SameBrush(setText.Foreground, Theme.Palette.ADBrush),
                $"「设置」带原因时字变 t-dis（{Hex(locked)}），去掉原因回到 a-d（{Hex(setText.Foreground)}）");
        }
        Check(vm.Inspector.PathTip == "/1号机/RV1" && !vm.Inspector.HasOther, "检查器路径 /1号机/RV1，没有「不是当前页签」提示条（总览页）");
        vm.Navigate(Route.Reactor(1, 1));
        vm.Select(3, 11);
        await Settle();
        Check(vm.Inspector.HasOther && vm.Inspector.OtherName == "3号机 · RV11" && vm.Inspector.OtherBack == "回到 1号机 · RV1",
            $"在 1号机 · RV1 页签上选中 3号机 RV11 → 提示条「{vm.Inspector.OtherName}（不是当前页签）」+「{vm.Inspector.OtherBack}」");
        var r311 = vm.Inspector.Body as ReactorInspectorVm;
        Check(r311 != null && r311.TopAlarms.Count == 1 && r311.TopAlarms[0].ShowAck && r311.TopAlarms[0].IsErrCard && r311.RunCellText == "报警暂停",
            "3号机 RV11：标题下面一张超压报警卡（带「确认」，页面骨架没有报警条），运行格「报警暂停」");
        await Capture(window, Path.Combine(dir, $"shell-insp-other-{width}.png"), lines);
        Click(window, insp.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "回到 1号机 · RV1"));
        await Settle();
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == 1 && !vm.Inspector.HasOther, "「回到 1号机 · RV1」→ 选中页签的对象，提示条消失");
        vm.Select(1, 6);
        await Settle();
        Check(vm.Inspector.Body is UninstalledInspectorVm { Note: "RV6 未安装 · 无可用操作" } u && u.Device.HasHead == false, "选中 1号机 RV6（未安装）→ 「RV6 未安装 · 无可用操作」+ 整机操作");
        // 没有会话的设备：记录分区的说明条带「去2号机」链接（PrLink，紫色 a-d）
        vm.Select(2, 1);
        await Settle();
        var goLink = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "去2号机" && b.IsEffectivelyVisible);
        var goText = goLink?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "去2号机");
        var r21Over = ContentTextOverrides(insp);
        Check(goLink?.Theme == Application.Current!.FindResource("PrLink") && goText != null && SameBrush(goText.Foreground, Theme.Palette.ADBrush) && r21Over.Count == 0,
            $"2号机 · RV1（没有会话）：记录说明条的「去2号机」是紫色 a-d 链接（{Hex(goText?.Foreground)}）" + (r21Over.Count > 0 ? "；被盖掉的字：" + string.Join("；", r21Over) : ""));
        await Capture(window, Path.Combine(dir, $"shell-insp-r21-{width}.png"), lines);

        // 名称放不下：18 → 16 → 15 → 两行，「RV16」从不省略
        var demo = vm.Fleet.DemoData!;
        demo.EditDevice(demo.Device(3)!, new Core.Demo.DemoDeviceEdit("高压加氢测试三号"));
        vm.Fleet.RefreshNow();
        vm.Select(3, 16);
        await Settle();
        var name = insp.GetVisualDescendants().OfType<InspObjName>().FirstOrDefault(n => n.IsEffectivelyVisible);
        var rvLine = name?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).ToList();
        Check(name != null && name.Mode != "18" && rvLine!.All(t => !t.TextLayout.TextLines.Any(l => l.HasCollapsed)) ,
            $"长别名「高压加氢测试三号 · RV16」：名称逐级缩小 / 分行（{name?.Mode}），不省略");
        await Capture(window, Path.Combine(dir, $"shell-insp-longname-{width}.png"), lines);
        demo.EditDevice(demo.Device(3)!, new Core.Demo.DemoDeviceEdit("3号机"));
        vm.Fleet.RefreshNow();

        // ---------------- 时间轴 ----------------
        vm.ResetView();
        await Settle();
        var tl = vm.Timeline;
        Check(tl.Groups.Count == vm.Fleet.Devices.Count && tl.Groups[0].IsOpen && tl.Groups[1].IsOpen && !tl.Groups[2].IsOpen,
            "时间轴按设备分组：1号机 / 2号机 展开，16 路的 3号机 收起");
        Check(tl.Groups[0].Rows.Count == 5 && tl.Groups[0].Rows[4] is TlUnRowVm { Text: "RV5–RV8 未安装" }, "1号机：4 行 + 合并的「RV5–RV8 未安装」");
        Check(tl.Groups[2].LeaseText.EndsWith(" · 16 路已折叠"), $"收起的组头写「16 路已折叠」（{tl.Groups[2].LeaseText}）");
        var rv1 = (TlRowVm)tl.Groups[0].Rows[0];
        Check(rv1.Segments.Count == 4 && rv1.Segments[2].State == Core.Programs.SegmentState.Cur && rv1.Segments[2].Full == "反应 · 当前", "RV1 程序条：4 段，第 3 段「反应 · 当前」");
        var rv3 = (TlRowVm)tl.Groups[0].Rows[2];
        Check(rv3.MoreText == "11:54" && rv3.MoreTip == "预计 11:54 结束", $"RV3 程序在窗口外结束：行尾「{rv3.MoreText} ›」");
        var tlView = Find<TimelineView>(window, _ => true);
        var label = tlView.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tllabel") && b.DataContext is TlRowVm r && r.R.N == 2 && r.D.K == 1);
        Click(window, label);
        await Settle();
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == 2 && ((TlRowVm)tl.Groups[0].Rows[1]).IsSelected && vm.Route.Id == "overview", "单击时间轴 RV2 的标签 → 选中（检查器、资源管理器同步），不导航");
        var acts = label.GetVisualParent()!.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("acts"));
        Check(acts.Children.All(c => c.Opacity == 1), "选中行的行操作（运行 / 停止 / 打开）显示");
        RightClick(window, label);
        await Settle();
        var ctx = MenuLabels(ContextMenus.Last?.ItemsSource?.Cast<object>());
        Check(ctx.FirstOrDefault() == "#1号机 · RV2 · 反应中" && ctx.Contains("停止") && ctx.Contains("中止程序") && ctx.Contains("打标记"), "时间轴右键 = 反应釜菜单：" + string.Join(" | ", ctx));
        ContextMenus.Last?.Close();
        var g3 = tlView.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("glabel") && b.DataContext is TlGroupVm g && g.K == 3);
        g3.BringIntoView();
        await Settle();
        Click(window, g3);
        await Settle();
        Check(tl.Groups[2].IsOpen && tl.Groups[2].Rows.Count == 16, "点 3号机 组头 → 展开 16 行");
        await Task.Delay(700);   // 两次单击隔开（不算双击）
        Click(window, g3);
        await Settle();
        Check(!tl.Groups[2].IsOpen, "再点 → 收起");
        vm.Navigate(Route.Reactor(3, 11));
        await Settle();
        var body = tlView.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Body");
        var row11 = tlView.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tlrow") && b.DataContext is TlRowVm r && r.D.K == 3 && r.R.N == 11);
        var rowTop = row11?.TranslatePoint(default, body)?.Y ?? -1;
        Check(tl.Groups[2].IsOpen && body.Offset.Y > 0 && rowTop >= 36 - 0.5 && rowTop + 36 <= body.Viewport.Height + 0.5,
            $"打开 3号机 · RV11 → 时间轴跟随：展开 3号机、RV11 行在可视区内且不在吸顶组头下面（行顶 {rowTop:0.#}，滚动 {body.Offset.Y:0.#}）");
        await Capture(window, Path.Combine(dir, $"shell-tl-follow-{width}.png"), lines);
        tl.ZoomCommand.Execute("1");
        await Settle();
        Check(tl.SpanMin == 240 && tl.Ticks.All(t => t.Text.EndsWith(":00") || t.Text.EndsWith(":30")), $"缩小 → 4 h，刻度 30 min（{string.Join(" ", tl.Ticks.Select(t => t.Text))}）");
        tl.ZoomCommand.Execute("-1");

        // ---------------- 右键菜单：资源管理器、页签 ----------------
        if (!L.ExplorerOpen) { vm.ToggleExplorer(); await Settle(); }
        var tree = Find<ListBox>(window, l => l.Name == "Tree");
        var dev2 = tree.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(c => (c.DataContext as XRow)?.Key == "d2");
        if (dev2 != null)
        {
            RightClick(window, dev2);
            await Settle();
            var dm = MenuLabels(ContextMenus.Last?.ItemsSource?.Cast<object>());
            var exp = new[] { "#2号机 · HT-PR4", "打开设备页", "-", "全部停止（不需要控制权）", "-", "申请控制权…", "断开连接…", "-", "重命名…", "编辑设置…", "移除…", "-", "急停 2号机…" };
            Check(vm.SelectedDevice?.K == 2 && dm.SequenceEqual(exp), "资源管理器右键 2号机 → 选中并弹出设备菜单：" + string.Join(" | ", dm));
            ContextMenus.Last?.Close();
        }
        if (L.ExplorerFloat) { vm.Layout.CloseFloat(); await Settle(); }
        var strip = Find<TabOverflowPanel>(window, _ => true);
        var tab = strip.GetVisualDescendants().OfType<NavToggle>().FirstOrDefault(t => (t.DataContext as DocTabVm)?.Id == "reactor/1/3" && t.Bounds.Width > 0);
        if (tab != null)
        {
            RightClick(window, tab);
            await Settle();
            var tm = MenuLabels(ContextMenus.Last?.ItemsSource?.Cast<object>());
            Check(tm.FirstOrDefault() == "#1号机 · RV3 · 升温中" && tm.Contains("打开"), "页签「1号机 · RV3」右键 = 反应釜菜单：" + string.Join(" | ", tm));
            ContextMenus.Last?.Close();
        }

        // ---------------- 旧样式页面：字符串内容照旧 ----------------
        // 旧视图（LegacyHost）里字符串内容的字是普通 TextBlock（不是外框的 PrText），而且它们的 presenter 都不认访问键（旧样式文件的平面按钮模板）——
        // 所以普通 TextBlock 正是 11.1 的默认做法，旧样式的「X TextBlock」后代选择器照旧只匹配原来匹配的字（M1 的样子不变）。
        // 将来旧视图里出现认访问键的 presenter（默认会是 AccessText），这里会失败，提醒 LegacyHost 的 DataTemplate 要跟着改。
        foreach (var key in new[] { "perf", "program" })
        {
            vm.Navigate(Route.Placeholder(key));
            await Settle();
            var legacy = window.GetVisualDescendants().OfType<Views.Legacy.LegacyHost>().FirstOrDefault(h => h.IsEffectivelyVisible);
            var strings = legacy?.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.Content is string && p.Child is TextBlock).ToList() ?? new List<ContentPresenter>();
            var off = strings.Where(p => p.RecognizesAccessKey || p.Child is PrText || StyleKeyOf(p.Child!) != typeof(TextBlock)).Select(p => p.Content as string).ToList();
            Check(legacy != null && strings.Count >= 5 && off.Count == 0,
                $"{(key == "perf" ? "性能测试页" : "程序页")}（旧样式）：{strings.Count} 处字符串内容是普通 TextBlock、presenter 不认访问键（同 M1 的默认做法）"
                + (off.Count > 0 ? "；不一样的：" + string.Join("、", off) : ""));
        }
        vm.ResetView();
        await Settle();
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 字符串内容的字（ContentPresenter 里由 DataTemplate 生成的 PrText / TextBlock：按钮、链接、页签、图标栏…）跟着自己的控件走：
    /// 颜色、字号、字重等于 presenter 的（presenter 的这几项从控件继承或模板绑定，字从 presenter 继承）。不相等 = 外面某条「X TextBlock」后代样式
    /// 盖到了控件里的字（M2 复核：检查器说明条的「Border.note TextBlock」把「设置」「去N号机」链接染成 t-2）。返回不相等的那些（空 = 没有）。
    /// </summary>
    private static List<string> ContentTextOverrides(Visual root)
    {
        var bad = new List<string>();
        foreach (var cp in root.GetVisualDescendants().OfType<ContentPresenter>())
        {
            if (cp.Content is not string s || cp.Child is not TextBlock tb || !tb.IsEffectivelyVisible) continue;
            if (!SameBrush(tb.Foreground, cp.Foreground) || tb.FontSize != cp.FontSize || tb.FontWeight != cp.FontWeight)
                bad.Add($"「{s}」（{cp.TemplatedParent?.GetType().Name}）字 {Hex(tb.Foreground)} {tb.FontSize:0.#} {tb.FontWeight} ≠ 控件 {Hex(cp.Foreground)} {cp.FontSize:0.#} {cp.FontWeight}");
        }
        return bad;
    }

    private static bool SameBrush(IBrush? a, IBrush? b) =>
        ReferenceEquals(a, b) || (a is ISolidColorBrush x && b is ISolidColorBrush y && x.Color == y.Color && Math.Abs(x.Opacity - y.Opacity) < 1e-6);

    private static string Hex(IBrush? b) =>
        b is ISolidColorBrush s ? $"#{s.Color.R:x2}{s.Color.G:x2}{s.Color.B:x2}" + (s.Opacity < 1 ? $"@{s.Opacity:0.##}" : "") : b?.GetType().Name ?? "null";

    /// <summary>
    /// 框外有没有阴影：同一批像素在「打开前」与「打开后」两张整窗截图里的亮度比（背景是什么都行：遮罩是均匀的半透明层，没有阴影时近处和远处的比值一样）。
    /// 返回（近处平均比值，远处平均比值）；阴影 = 近处明显更暗。points 是（近，远）成对的窗口坐标。
    /// </summary>
    private static (double Near, double Far) ShadeRatios(WriteableBitmap before, WriteableBitmap after, IEnumerable<(PixelPoint Near, PixelPoint Far)> points)
    {
        using var a = before.Lock();
        using var b = after.Lock();
        double Grey(ILockedFramebuffer fb, PixelPoint p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= fb.Size.Width || p.Y >= fb.Size.Height) return double.NaN;
            var o = p.Y * fb.RowBytes + p.X * 4;   // 8 位 4 通道（Bgra / Rgba 都一样：只取前三个通道的平均）
            return (System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o) + System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 1)
                + System.Runtime.InteropServices.Marshal.ReadByte(fb.Address, o + 2)) / 3.0;
        }
        var near = new List<double>();
        var far = new List<double>();
        foreach (var (n, f) in points)
        {
            double n0 = Grey(a, n), n1 = Grey(b, n), f0 = Grey(a, f), f1 = Grey(b, f);
            if (double.IsNaN(n0 + n1 + f0 + f1) || n0 < 40 || f0 < 40) continue;   // 太暗的背景（字）比值不稳，跳过
            near.Add(n1 / n0);
            far.Add(f1 / f0);
        }
        return near.Count == 0 ? (double.NaN, double.NaN) : (near.Average(), far.Average());
    }

    /// <summary>控件找隐式主题用的类型（StyleKeyOverride；受保护属性，用反射取）。</summary>
    private static Type StyleKeyOf(StyledElement e) =>
        typeof(StyledElement).GetProperty("StyleKeyOverride", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(e) as Type ?? e.GetType();

    private static T Find<T>(Visual root, Func<T, bool> pred) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(pred) ?? throw new InvalidOperationException("找不到 " + typeof(T).Name);

    private static Point Center(Visual v, Visual root) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), root) ?? default;

    private static void Click(Window w, Visual v) => ClickAt(w, Center(v, w));

    private static void ClickAt(Window w, Point p)
    {
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Tab(Window w)
    {
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        w.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Esc(Window w)
    {
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        w.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(60);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static async Task Capture(Window w, string path, List<string> lines)
    {
        await Settle();
        var bmp = w.CaptureRenderedFrame();
        if (bmp == null) { lines.Add("INFO capture null " + Path.GetFileName(path)); return; }
        bmp.Save(path);
        lines.Add($"INFO saved {Path.GetFileName(path)}");
    }

    private static bool SavePopup(MenuItem owner, string path, List<string> lines)
    {
        if (owner.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child is Avalonia.Controls.Control c) return Save(c, path, lines);
        lines.Add("INFO 没有找到弹出层 " + Path.GetFileName(path));
        return false;
    }

    /// <summary>把控件（弹出层）存成 PNG；控件还没排版（0 宽 / 0 高）或存不下来时返回 false（调用方记 FAIL，不能悄悄跳过）。</summary>
    private static bool Save(Avalonia.Controls.Control c, string path, List<string> lines)
    {
        if (!(c.Bounds.Width >= 1 && c.Bounds.Height >= 1)) { lines.Add($"INFO 弹出层没有尺寸，没存 {Path.GetFileName(path)}"); return false; }
        try
        {
            var size = new PixelSize((int)Math.Ceiling(c.Bounds.Width), (int)Math.Ceiling(c.Bounds.Height));
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(c);
            rtb.Save(path);
            lines.Add($"INFO saved {Path.GetFileName(path)} {size.Width}x{size.Height}");
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            lines.Add($"INFO 存不下 {Path.GetFileName(path)}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 打开着的 MenuFlyout 在弹出层里真正排出来的 presenter（PopupFlyoutBase 的 Popup 是非公开属性，用反射取；取不到时在窗口的覆盖层里找）。
    /// </summary>
    private static MenuFlyoutPresenter? PresenterOf(MenuFlyout? mf)
    {
        if (mf == null) return null;
        var prop = typeof(PopupFlyoutBase).GetProperty("Popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (prop?.GetValue(mf) is Popup { Child: MenuFlyoutPresenter p }) return p;
        return (mf.Target?.GetVisualRoot() as Visual)?.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault(x => x.IsEffectivelyVisible);
    }
}
#endif
