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
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Demo;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 5 组的行为核对（PR_CONTROL_SHELL_CHECKS=1）：
/// <list type="bullet">
/// <item>「演示控制」工具窗口（Views/Demo，样稿演示条）：同交互走查用 OpenBeside 打开（归主窗口所有、不抢键盘），真的点它的按钮 ——
///   急停（开 = 设备上按下，关 = 触屏复位）、报警（加 / 清）、控制权四种、断线、设备 1 / 3 / 4 台、冻结（演示数据 + UiTicker + 主窗口 frozen 一起）、
///   重置（「演示数据已重置」；目标回到第一台，选中的对象与已有的提示照样稿留着）。</item>
/// <item>「设置」→ 检查器（样稿 inspFocus）：分区不在视野里时滚到顶部下面 8 px，数值框聚焦并全选，分区提亮（类 flash）1.2 s 后熄灭；数值框带原因时不聚焦。</item>
/// <item>「不是当前页签」提示条：一整句（一个 SegText），1366 / 1600 下图标单独一行、句子换到下一行。</item>
/// <item>面板的放大 / 关闭 / 还原（样稿 README 自检：每个面板关闭后再放大剩下的第一块、放大、还原、只留第一块 / 最后一块）：总览、设备页 1 / 3、
///   反应釜页 1/1 与 3/11，演示 / stress=1 / stress=2 三种数据，另加一台设备的总览（devs=1，三种数据：分区 + 温度趋势），
///   每个状态跑一遍 ShotAudit，问题写 panel-states-&lt;w&gt;.audit.txt（干净时为空）。</item>
/// </list>
/// 跑完把演示数据（冻结）、外框、布局、提示恢复原样。
/// </summary>
public static partial class ShellChecks
{
    private static async Task M6G5Checks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        await M6G5DemoControl(window, vm, dir, width, Check, Info, lines);
        M6G5Restore(window, vm);
        await Settle();
        await M6G5InspFocus(window, vm, dir, width, Check, Info, lines);
        M6G5Restore(window, vm);
        await Settle();
        await M6G5InspOther(window, vm, Check, Info);
        M6G5Restore(window, vm);
        await Settle();
        await M6G5PanelAudits(window, vm, dir, width, Check, Info, lines);
        M6G5Restore(window, vm);
        await Settle();
    }

    /// <summary>演示数据回到默认（冻结）、窗口冻结、外框与布局复位、提示清空、指针挪到窗口外（悬停提示收起）：后面的核对与截图不受影响。</summary>
    private static void M6G5Restore(Window window, ShellVm vm)
    {
        window.MouseMove(new Point(-40, -40));
        var p = DemoParams.Default with { Freeze = true };
        vm.Fleet.ResetDemo(p);
        vm.ApplyDemoParams(p);
        vm.ResetView();
        vm.ResetLayout();
        vm.Toasts.Clear();
        Frozen.Apply(window);
    }

    // ---------------- 「演示控制」工具窗口 ----------------

    private static async Task M6G5DemoControl(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        await Settle();
        var demo = vm.Fleet.DemoData!;
        // 同交互走查（ShotRunner.StartDemo）：OpenBeside 开在主窗口旁边，冻结的回调改 UiTicker 与主窗口（这里的 UiTicker 不启动，只看开关）
        using var ticker = new UiTicker { Frozen = true };
        var tool = DemoControlWindow.OpenBeside(window, vm, ticker);
        await Settle();
        ToggleButton T(string label) => tool.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == label && b.IsEffectivelyVisible);
        async Task Press(string label) { Click(tool, T(label)); await Settle(); }
        List<int> DemoAlarms() => vm.Fleet.DemoData!.Devices.Where(d => d.Alarms.Any(a => a.Demo)).Select(d => d.K).ToList();
        try
        {
            Check(ReferenceEquals(tool.Owner, window), "演示控制窗口归主窗口所有（主窗口关了它跟着关）");
            Check(!tool.ShowActivated && !tool.IsActive && window.IsActive, "演示控制窗口打开时不抢键盘：它不是活动窗口，主窗口还是（ShowActivated = false）");
            // 无头窗口不记位置（Position 总是 0, 0）：位置的算法由单元测试核对（M6DemoControlTests），这里只记下算出来的值
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            double mainW = window.FrameSize?.Width ?? window.Bounds.Width;
            Info($"演示控制窗口的位置：{DemoControlWindow.BesidePosition(window.Position, mainW, tool.Width, window.DesktopScaling, screen?.WorkingArea)}"
                 + $"（主窗口 {window.Position}、宽 {mainW:0.#}，桌面缩放 {window.DesktopScaling:0.##}，工作区 {screen?.WorkingArea}）");
            Check(T("1号机").IsChecked == true && T("3 台").IsChecked == true && T("已冻结").IsChecked == true && T("远程").IsChecked == true,
                "演示控制：目标 1号机、3 台、已冻结（截图 / 核对都冻结）、1号机远程控制中 —— 按钮按此刻的状态勾上");
            await Capture(tool, Path.Combine(dir, $"shell-democtl-{width}.png"), lines);
            var toolIssues = ShotAudit.Run(tool);
            Check(toolIssues.Count == 0, "「演示控制」窗口审计为空" + (toolIssues.Count > 0 ? "：" + string.Join("；", toolIssues) : ""));

            // 急停：开 = 在设备上按下（检查器红框、ESTOP 报警），关 = 在触屏复位（事件「急停已在触屏复位」，报警去掉）
            await Press("急停");
            var d1 = demo.Device(1)!;
            Check(d1.Estop.Active && d1.Alarms.Any(a => a.Code == "ESTOP") && (vm.Inspector.Body as DeviceInspectorVm)?.EstopOn == true && T("急停").IsChecked == true,
                "演示控制「急停」开：1号机急停中（ESTOP 报警、检查器红框），按钮勾上");
            await Press("急停");
            Check(!d1.Estop.Active && d1.Alarms.All(a => a.Code != "ESTOP") && d1.Events.FirstOrDefault()?.Text == "急停已在触屏复位"
                  && (vm.Inspector.Body as DeviceInspectorVm)?.EstopOn == false && T("急停").IsChecked == false,
                $"「急停」关 = 触屏复位：急停与 ESTOP 报警去掉，最新事件「{d1.Events.FirstOrDefault()?.Text}」，红框消失");

            // 报警：开 = 加一条演示超压报警，关 = 清除全部报警
            await Press("报警");
            Check(d1.Alarms.Any(a => a.Demo && a.Code == "P-HIGH") && T("报警").IsChecked == true, "「报警」开：1号机加一条超压报警（演示）");
            await Press("报警");
            Check(d1.Alarms.Count == 0 && T("报警").IsChecked == false, "「报警」关：1号机的报警全部清除");

            // 目标换 2号机：控制权四种、断线
            await Press("2号机");
            var d2 = demo.Device(2)!;
            Check(T("2号机").IsChecked == true && T("1号机").IsChecked == false && T("本地").IsChecked == true, "目标换成 2号机（本地只读）");
            foreach (var (label, want) in new[] { ("申请中", LeaseStatus.Requested), ("远程", LeaseStatus.Remote), ("丢失", LeaseStatus.Lost), ("本地", LeaseStatus.Local) })
            {
                await Press(label);
                Check(d2.Lease.State == want && T(label).IsChecked == true && vm.Fleet.Device(2)!.LeaseStatus == want, $"控制权「{label}」→ 2号机 {want}");
            }
            await Press("断线");
            Check(d2.Conn.State == ConnStatus.Offline && T("断线").IsChecked == true, "「断线」开：2号机连接中断");
            await Press("断线");
            Check(d2.Conn.State == ConnStatus.Online && T("断线").IsChecked == false, "「断线」关：2号机重新连上");

            // 设备组合：4 台（4号机断线）/ 1 台 / 3 台；目标那一排跟着重建
            await Press("4 台");
            Check(vm.Fleet.Devices.Count == 4 && demo.Device(4)?.Conn.State == ConnStatus.Offline && T("4号机").IsVisible, "「4 台」：4 台、4号机断线重连中，目标里有 4号机");
            await Press("1 台");
            Check(vm.Fleet.Devices.Count == 1 && tool.GetVisualDescendants().OfType<ToggleButton>().All(b => b.Content as string != "2号机"), "「1 台」：只剩 1号机");
            await Press("3 台");
            Check(vm.Fleet.Devices.Count == 3 && vm.Fleet.Params?.Devs == null, "「3 台」：回到默认 3 台");

            // 冻结：演示数据、UiTicker、主窗口的 frozen 一起改，提示不冻结
            bool toastsFrozen = vm.Toasts.Frozen;
            await Press("已冻结");
            Check(vm.Fleet.Params?.Freeze == false && !ticker.Frozen && !Frozen.GetIsFrozen(window) && T("冻结").IsChecked == false,
                "「已冻结」→ 解冻：演示数据与 UiTicker 不冻结、主窗口去掉 frozen（动画、过渡回来），按钮写「冻结」");
            await Press("冻结");
            Check(vm.Fleet.Params?.Freeze == true && ticker.Frozen && Frozen.GetIsFrozen(window) && T("已冻结").IsChecked == true && vm.Toasts.Frozen == toastsFrozen,
                "「冻结」→ 演示数据、UiTicker 冻结，主窗口 frozen；toast 的冻结不变（样稿冻结时 toast 照常消失）");

            // 重置（样稿 reset）：演示数据从头来、回到总览，提示「演示数据已重置」（info）；目标回到第一台，选中的对象还在就留着，已有的提示不清
            vm.Navigate(Route.Device(2));
            await Press("2号机");
            await Press("报警");
            Check(DemoAlarms().SequenceEqual(new[] { 2 }) && T("报警").IsChecked == true, $"重置之前：目标 2号机，演示报警加在 2号机上（有演示报警的：{string.Join("、", DemoAlarms())}）");
            vm.Select(3, 11);
            vm.Toasts.Clear();
            vm.Toast("重置之前的提示", ToastKind.Info);
            await Settle();
            Click(tool, tool.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "重置"));
            await Settle();
            Check(vm.Route.Id == "overview" && vm.Fleet.Params?.Equals(DemoParams.Default) == true && DemoAlarms().Count == 0
                  && !Frozen.GetIsFrozen(window) && !ticker.Frozen,
                $"「重置」：回到总览、参数全部默认（{vm.Fleet.Params}）、哪一台都没有演示报警了，不再冻结（UiTicker、主窗口）");
            Check(T("1号机").IsChecked == true && T("2号机").IsChecked == false && T("本地").IsChecked == false && T("远程").IsChecked == true,
                "「重置」：目标回到第一台 1号机（样稿 demoDev = null），按钮按它的状态勾上");
            Check(vm.Selection.K == 3 && vm.Selection.N == 11, $"「重置」：选中的 3号机 · RV11 还在，留着（样稿不动 shell.sel）—— 选中 {vm.Selection}");
            var texts = vm.Toasts.Items.Select(t => t.Text).ToList();
            Check(texts.SequenceEqual(new[] { "重置之前的提示", "演示数据已重置" }) && vm.Toasts.Items[^1].Kind == ToastKind.Info,
                $"「重置」：已有的提示不清（样稿），后面跟「演示数据已重置」（info）—— {string.Join(" / ", texts)}");
        }
        finally
        {
            tool.Close();
        }
    }

    // ---------------- 「设置」→ 检查器（样稿 inspFocus） ----------------

    private static async Task M6G5InspFocus(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);
        var scroll = insp.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        vm.Select(1, 1);
        await Settle();
        var link = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "设置" && b.IsEffectivelyVisible);
        if (link == null) { Check(false, "1号机 · RV1 检查器的搅拌说明条有「设置」"); return; }
        link.BringIntoView();   // 1366 下检查器矮：同用户先滚到它再点
        await Settle();
        Click(window, link);
        await Settle();
        var box = insp.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Name == "SharedRpm" && n.IsEffectivelyVisible);
        var sec = box?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sec"));
        var tb = box?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        var focused = window.FocusManager?.GetFocusedElement();
        Check(vm.SelectedDevice?.K == 1 && vm.SelectedRv == null && box != null && sec != null && ReferenceEquals(focused, tb),
            $"「设置」→ 选中 1号机，焦点在共用转速框（{focused?.GetType().Name}）");
        if (box == null || sec == null || tb == null) return;
        Check(tb.SelectionStart == 0 && tb.SelectionEnd == (tb.Text ?? "").Length && (tb.Text ?? "").Length > 0,
            $"数值全选（样稿 inp.select()）：「{tb.Text}」选中 {tb.SelectionStart}–{tb.SelectionEnd}");
        Check(sec.Classes.Contains("flash") && sec.Background is Avalonia.Media.ISolidColorBrush bg && bg.Color == Palette.ATintColor && sec.BoxShadow.Count == 1 && sec.BoxShadow[0].Spread == 8,
            "「共用搅拌」分区提亮：类 flash，a-tint 底 + 8 px a-tint 外圈");
        var top = sec.TranslatePoint(default, scroll)?.Y ?? double.NaN;
        bool scrolled = scroll.Offset.Y > 0.5;
        Check(scrolled ? Math.Abs(top - 8) <= 1 : top >= 0 && top + sec.Bounds.Height <= scroll.Viewport.Height + 0.5,
            scrolled ? $"分区原来不全在视野里：滚到分区顶部在正文顶部下面 8 px（{top:0.#}，滚动 {scroll.Offset.Y:0.#}）"
                     : $"分区本来就全在视野里：不滚动（顶部 {top:0.#}）");
        await Capture(window, Path.Combine(dir, $"shell-inspflash-{width}.png"), lines);
        await Task.Delay(InspectorView.FlashFor + TimeSpan.FromMilliseconds(250));
        Dispatcher.UIThread.RunJobs();
        Check(!sec.Classes.Contains("flash"), "1.2 s 后熄灭（去掉类 flash）");

        // 数值框带原因（3号机本地只读）：不聚焦（样稿 if (!inp.readOnly)），分区照样提亮
        window.FocusManager?.ClearFocus();
        vm.Select(3, 11);
        await Settle();
        var link3 = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "设置" && b.IsEffectivelyVisible);
        if (link3 == null) { Check(false, "3号机 · RV11 检查器的搅拌说明条有「设置」"); return; }
        link3.BringIntoView();
        await Settle();
        Click(window, link3);
        await Settle();
        var box3 = insp.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Name == "SharedRpm" && n.IsEffectivelyVisible);
        var tb3 = box3?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        var sec3 = box3?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sec"));
        var f3 = window.FocusManager?.GetFocusedElement();
        Check(vm.SelectedDevice?.K == 3 && tb3 is { IsReadOnly: true } && !ReferenceEquals(f3, tb3) && sec3?.Classes.Contains("flash") == true,
            $"3号机（本地只读，共用转速带原因）：不聚焦数值框（焦点 {f3?.GetType().Name}），分区照样提亮");
    }

    // ---------------- 「不是当前页签」提示条 ----------------

    private static async Task M6G5InspOther(Window window, ShellVm vm, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        vm.Navigate(Route.Reactor(1, 1));
        vm.Select(3, 11);
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);
        var wrap = insp.GetVisualDescendants().OfType<WrapEnd>().FirstOrDefault(w => w.IsEffectivelyVisible && w.Children.OfType<SegText>().Any());
        var seg = wrap?.Children.OfType<SegText>().FirstOrDefault();
        var icon = wrap?.Children.OfType<Avalonia.Controls.Control>().FirstOrDefault();
        var link = wrap?.Children.OfType<Button>().FirstOrDefault();
        var text = seg?.Inlines?.Text ?? "";
        Check(seg != null && link != null && text == "检查器：3号机 · RV11（不是当前页签）" && link.Content as string == "回到 1号机 · RV1",
            $"提示条：图标 · 一整句「{text}」·「{link?.Content}」");
        if (seg == null || icon == null || link == null) return;
        bool wide = vm.Layout.FrameWidth >= 1920;
        double iconBottom = icon.Bounds.Bottom, segTop = seg.Bounds.Top, segH = seg.Bounds.Height, linkTop = link.Bounds.Top;
        Info($"提示条 {wrap!.Bounds.Width:0.#}×{wrap.Bounds.Height:0.#}：图标 {icon.Bounds} · 句子 {seg.Bounds} · 链接 {link.Bounds}");
        Check(wide ? Math.Abs(segTop - icon.Bounds.Top) < 6 && linkTop >= seg.Bounds.Bottom
                   : segTop >= iconBottom && segH > 30 && linkTop >= seg.Bounds.Bottom,
            wide ? "1920（检查器 330）：句子跟在图标后面一行放下，「回到 …」在下一行（同样稿 297×72）"
                 : "300 宽：图标单独一行，整句换到下一行并折成两行，「回到 …」再一行（同样稿 267×110）");
        Check(Math.Abs(link.Bounds.Right - wrap.Bounds.Width) < 1, "「回到 …」靠这一行的右端（样稿 margin-left: auto）");
    }

    // ---------------- 面板的放大 / 关闭 / 还原（每个状态审计） ----------------

    private static async Task M6G5PanelAudits(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var report = new List<string>();
        int states = 0, bad = 0;
        var routes = new[] { Route.Overview, Route.Device(1), Route.Device(3), Route.Reactor(1, 1), Route.Reactor(3, 11) };
        // 总览的「温度趋势」只在一台设备时有（分区下面剩 200 px 以上，OverviewTrendVm）：一台设备的总览另跑三种数据，放大 / 还原它也审计
        var ovOnly = new[] { Route.Overview };
        foreach (var (data, query, set) in new[]
                 {
                     ("演示", "", routes), ("stress=1", "stress=1", routes), ("stress=2", "stress=2", routes),
                     ("devs=1", "devs=1", ovOnly), ("devs=1&stress=1", "devs=1&stress=1", ovOnly), ("devs=1&stress=2", "devs=1&stress=2", ovOnly),
                 })
        {
            var p = DemoParams.Parse(query) with { Freeze = true };
            vm.Fleet.ResetDemo(p);
            vm.ApplyDemoParams(p);
            foreach (var route in set)
            {
                vm.ResetView();
                vm.ApplyDemoParams(p, route);
                await Settle();
                var all = ShotRunner.M6G5Panels(window);
                // 名字：无障碍名 · 标题文字 / 面包屑 · 总览分区的设备别名（分区的标题是一排控件）
                string Name(PrPanel x) => (Avalonia.Automation.AutomationProperties.GetName(x) is { Length: > 0 } an ? an
                    : x.ShownTitleTip ?? (x.DataContext as Pages.OverviewSectionVm)?.Alias ?? "面板").Trim();
                if (set == ovOnly)
                    Check(all.Any(x => x.Name == "TrendPanel"), $"{data} · overview：一台设备，分区下面有温度趋势（{string.Join("、", all.Select(Name))}）");
                async Task Audit(string what)
                {
                    // 指针挪到窗口外：前面的核对点过的地方留着指针，面板换了位置之后下面的控件会弹出悬停提示，截进审计里
                    window.MouseMove(new Point(-40, -40));
                    Dispatcher.UIThread.RunJobs();
                    await Settle();
                    states++;
                    var issues = ShotAudit.Run(window);
                    if (issues.Count == 0) return;
                    bad++;
                    report.Add($"## {data} · {route.Id} · {what}");
                    report.AddRange(issues);
                    await Capture(window, Path.Combine(dir, $"panel-state-{bad}-{width}.png"), lines);
                }
                async Task Reset()
                {
                    vm.ResetLayout();
                    vm.Toasts.Clear();
                    await Settle();
                }
                Button? Btn(PrPanel x, string name) => x.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
                async Task Press(PrPanel x, string name)
                {
                    if (Btn(x, name) is { } b) b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Settle();
                }
                foreach (var pnl in all)
                {
                    var title = Name(pnl);
                    if (Btn(pnl, "PART_Max") != null)
                    {
                        await Press(pnl, "PART_Max");
                        Check(pnl.IsMaximized, $"{data} · {route.Id}：放大「{title}」");
                        await Audit("放大「" + title + "」");
                        await Press(pnl, "PART_Max");
                        Check(!pnl.IsMaximized, $"{data} · {route.Id}：还原「{title}」");
                        await Audit("还原「" + title + "」");
                    }
                    if (Btn(pnl, "PART_Close") != null)
                    {
                        await Press(pnl, "PART_Close");
                        await Audit("关闭「" + title + "」");
                        // 样稿：关掉一块之后再放大剩下的第一块
                        var rest = ShotRunner.M6G5Panels(window);
                        if (rest.FirstOrDefault(r => Btn(r, "PART_Max") != null) is { } first)
                        {
                            await Press(first, "PART_Max");
                            await Audit("关闭「" + title + "」后放大「" + Name(first) + "」");
                            await Press(first, "PART_Max");
                        }
                    }
                    await Reset();
                }
                // 只留第一块 / 只留最后一块
                var closable = all.Where(x => Btn(x, "PART_Close") != null).ToList();
                if (closable.Count > 1)
                {
                    foreach (var keep in new[] { closable[0], closable[^1] })
                    {
                        foreach (var x in closable.Where(x => !ReferenceEquals(x, keep))) await Press(x, "PART_Close");
                        await Audit("只留「" + Name(keep) + "」");
                        await Reset();
                    }
                }
                Info($"{data} · {route.Id}：{all.Count} 块面板（{string.Join("、", all.Select(Name))}）");
            }
        }
        var file = Path.Combine(dir, $"panel-states-{width}.audit.txt");
        File.WriteAllText(file, report.Count == 0 ? "" : string.Join("\n", report) + "\n");
        Check(bad == 0, $"面板的放大 / 还原 / 关闭（再放大剩下的第一块）/ 只留第一块或最后一块：{states} 个状态审计 {(bad == 0 ? "全部为空" : bad + " 个有问题（见 " + Path.GetFileName(file) + "）")}");
        Check(states >= 100, $"面板状态审计确实跑了（{states} 个）");
    }
}
#endif
