#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 3 组（禁用原因、提示、锁着的输入）：
/// <list type="bullet">
/// <item>悬停提示的位置（样稿 showTip）：2号机「全部运行」的原因提示在按钮正下方居中、间距 6（间距在弹出层外面）；状态栏（窗口最下面）的提示翻到上面、
///   间距 6；图标栏「设备」的提示左边贴窗口时留 4 px。原因同时是无障碍说明（HelpText）。</item>
/// <item>锁着的输入离开 Tab 顺序（样稿 readonly + tabindex=-1）：2号机 · RV1（本地只读）的检查器里 Tab 走一圈，不停在设定温度 / 转速框、程序下拉上，
///   照样停在带原因的按钮上；申请到控制权（原因清掉）后输入框回到 Tab 顺序。</item>
/// <item>数值框超出量程（样稿 clampNum）：1号机 · RV1 设定温度打 999 → 离开 → 200.0 + warn「超出量程 · 已限制为 200.0」；量程里的数、坏值不提示。</item>
/// <item>提示：四种语气、polite 的实时区域 + 每条的无障碍名；长句显示 4.2 s；一行放不下的换行（最多 3 行）、整句在框里。对话框的无障碍名 = 标题。</item>
/// <item>全局停止格：「急停」的提示同样稿（「… · 复位只能在触屏上操作」）；没有已连接的设备时两个都带原因、点了 warn 提示。</item>
/// </list>
/// 最后把演示数据和外框恢复原样。
/// </summary>
public static partial class ShellChecks
{
    /// <summary>M6 第 3 组的行为核对（PR_CONTROL_SHELL_CHECKS=1）。跑完把演示数据和外框恢复原样（vm.ResetView 等），不影响后面的组。</summary>
    private static async Task M6G3Checks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        try
        {
            await TipChecks(window, vm, dir, width, Check, Info, lines);
            await TabStopChecks(window, vm, Check, Info);
            await ClampChecks(window, vm, dir, width, Check, lines);
            await ToastChecks(window, vm, dir, width, Check, lines);
            await StopCellChecks(window, vm, Check);
        }
        finally
        {
            window.MouseMove(new Point(2, 2));
            vm.Fleet.ResetDemo(p0);
            vm.ApplyDemoParams(p0);
            vm.ResetView();
            await Settle();
        }
    }

    /// <summary>窗口坐标里的矩形。</summary>
    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    /// <summary>字符串提示自动生成的 ToolTip（ToolTipDiagnostics.ToolTipProperty）；原因提示直接是 ToolTip.Tip。</summary>
    private static ToolTip? TipOf(Avalonia.Controls.Control c) =>
        ToolTip.GetTip(c) as ToolTip ?? c.GetValue(Avalonia.Controls.Diagnostics.ToolTipDiagnostics.ToolTipProperty) as ToolTip;

    /// <summary>装着提示的弹出层（无头时是窗口里的 OverlayPopupHost；桌面上同样大小的是单独的弹出窗口）在窗口坐标里的矩形。</summary>
    private static Rect? HostRect(ToolTip tip, Visual root) =>
        tip.GetVisualAncestors().OfType<Avalonia.Controls.Primitives.OverlayPopupHost>().FirstOrDefault() is { } h ? RectIn(h, root) : null;

    private static async Task TipChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        vm.ResetView();
        vm.Select(2);
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);
        var runAll = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "全部运行" && b.IsEffectivelyVisible);
        if (runAll == null) { Check(false, "2号机检查器里有「全部运行」"); return; }
        string reason = Theme.UiText.Plain(PrB.GetReason(runAll));
        Check(reason == "2号机为本地只读 · 先申请控制权" && AutomationProperties.GetHelpText(runAll) == reason,
            $"2号机「全部运行」带原因，无障碍说明 = 原因的纯文字（没有连接符）（「{reason}」/「{AutomationProperties.GetHelpText(runAll)}」）");
        ToolTip.SetIsOpen(runAll, true);
        await Settle();
        var tip = TipOf(runAll);
        if (tip == null || !tip.IsEffectivelyVisible) Check(false, "「全部运行」的原因提示打开了");
        else
        {
            var b = RectIn(runAll, window);
            var t = RectIn(tip, window);
            Info($"原因提示 {t.X:0.#},{t.Y:0.#} {t.Width:0.#}×{t.Height:0.#}；按钮 {b.X:0.#},{b.Y:0.#} {b.Width:0.#}×{b.Height:0.#}");
            Check(Math.Abs(t.Center.X - b.Center.X) <= 1 && Math.Abs(t.Y - (b.Bottom + PrB.TipGap)) <= 1,
                $"原因提示在按钮正下方居中、间距 6（提示中心 {t.Center.X:0.#} / 按钮中心 {b.Center.X:0.#}，提示顶 {t.Y:0.#} / 按钮底 + 6 = {b.Bottom + PrB.TipGap:0.#}）");
            // 间距在弹出层外面（桌面上提示是单独的窗口：窗口里透明的地方照样接住指针，间距不能算在里面）
            var h = HostRect(tip, window);
            Check(h is { } hr && Math.Abs(hr.Y - t.Y) <= 0.5 && Math.Abs(hr.Height - t.Height) <= 0.5 && hr.Y >= b.Bottom + PrB.TipGap - 0.5,
                $"提示的弹出层从提示顶开始、不含按钮下面的 6 px（弹出层 {h?.Y:0.#} … {h?.Bottom:0.#}，提示 {t.Y:0.#} … {t.Bottom:0.#}）");
            Check(tip.Classes.Contains("reason") && tip.GetVisualDescendants().OfType<Controls.IconView>().Any(i => i.Icon == "lock"), "原因提示前面是锁图标");
            await Capture(window, Path.Combine(dir, $"shell-tip-reason-{width}.png"), lines);
        }
        ToolTip.SetIsOpen(runAll, false);
        await Settle();

        // 窗口最下面（状态栏的报警按钮）：下面放不下 → 翻到上面，间距照样 6（不压住按钮）
        var status = Find<StatusBarView>(window, _ => true);
        var bell = status.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => ToolTip.GetTip(x) is string s && s.Length > 0 && x.IsEffectivelyVisible);
        if (bell == null) Check(false, "状态栏有带提示的报警按钮");
        else
        {
            ToolTip.SetIsOpen(bell, true);
            await Settle();
            var tip2 = TipOf(bell);
            if (tip2 == null || !tip2.IsEffectivelyVisible) Check(false, "状态栏报警按钮的提示打开了");
            else
            {
                var b = RectIn(bell, window);
                var t = RectIn(tip2, window);
                Check(Math.Abs(t.Bottom - (b.Y - PrB.TipGap)) <= 1 && t.Bottom <= window.Bounds.Height,
                    $"窗口最下面的提示翻到上面、间距 6（提示底 {t.Bottom:0.#} / 按钮顶 − 6 = {b.Y - PrB.TipGap:0.#}）");
                await Capture(window, Path.Combine(dir, $"shell-tip-flip-{width}.png"), lines);
            }
            ToolTip.SetIsOpen(bell, false);
            await Settle();
        }

        // 窗口左边（图标栏「设备」）：居中会伸出窗口 → 滑回来、离窗口边 4 px
        var side = Find<SidebarView>(window, _ => true);
        var devs = side.GetVisualDescendants().OfType<Controls.Shell.NavToggle>().FirstOrDefault(n => n.Content as string == "设备");
        if (devs == null) Check(false, "图标栏有「设备」");
        else
        {
            ToolTip.SetIsOpen(devs, true);
            await Settle();
            var tip3 = TipOf(devs);
            if (tip3 == null || !tip3.IsEffectivelyVisible) Check(false, "图标栏「设备」的提示打开了");
            else
            {
                var b = RectIn(devs, window);
                var t = RectIn(tip3, window);
                bool slid = b.Center.X - t.Width / 2 < PrB.TipInset;
                Check(slid ? Math.Abs(t.X - PrB.TipInset) <= 1 : Math.Abs(t.Center.X - b.Center.X) <= 1,
                    $"图标栏「设备」的提示{(slid ? "贴左边时离窗口边 4 px" : "居中")}（提示左 {t.X:0.#}，宽 {t.Width:0.#}；按钮中心 {b.Center.X:0.#}）");
            }
            ToolTip.SetIsOpen(devs, false);
            await Settle();
        }
    }

    /// <summary>2号机 · RV1（本地只读）：Tab 走一圈。</summary>
    private static async Task TabStopChecks(Window window, ShellVm vm, Action<bool, string> Check, Action<string> Info)
    {
        vm.ResetView();
        vm.Select(2, 1);
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);
        var boxes = insp.GetVisualDescendants().OfType<NumBox>().Where(n => n.IsEffectivelyVisible).ToList();
        var lockedInputs = boxes.Where(n => !string.IsNullOrEmpty(PrB.GetReason(n))).SelectMany(n => n.GetVisualDescendants().OfType<TextBox>()).ToList();
        var lockedCombos = insp.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && !string.IsNullOrEmpty(PrB.GetReason(c))).ToList();
        Check(lockedInputs.Count >= 2 && lockedCombos.Count == 1, $"2号机 · RV1：锁着的数值框 {lockedInputs.Count} 个（设定温度、转速）、程序下拉 {lockedCombos.Count} 个");
        Check(lockedInputs.All(t => !t.IsTabStop) && lockedCombos.All(c => !c.IsTabStop), "锁着的输入框、下拉不在 Tab 顺序里（IsTabStop = false）");
        var first = insp.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Focusable && b.IsEffectivelyEnabled);
        if (first == null) { Check(false, "检查器里有可以聚焦的按钮"); return; }
        first.Focus(NavigationMethod.Tab);
        await Settle();
        var seen = new List<IInputElement>();
        for (int i = 0; i < 60; i++)
        {
            var f = window.FocusManager?.GetFocusedElement();
            if (f is not Visual fv || !insp.IsVisualAncestorOf(fv)) break;
            if (seen.Contains(f)) break;
            seen.Add(f);
            Tab(window);
        }
        var bad = seen.OfType<Visual>().Where(v => lockedInputs.Contains(v) || lockedCombos.Any(c => c == v || c.IsVisualAncestorOf(v))).ToList();
        var reasonButtons = seen.OfType<Button>().Where(b => !string.IsNullOrEmpty(PrB.GetReason(b))).Select(b => b.Content as string ?? PrB.GetIcon(b) ?? "?").ToList();
        Info("Tab 停过：" + string.Join(" → ", seen.Select(e => e is Button b ? (b.Content as string ?? PrB.GetIcon(b) ?? "按钮") : e.GetType().Name)));
        Check(bad.Count == 0, "Tab 不停在锁着的设定温度 / 转速框、程序下拉上" + (bad.Count > 0 ? "：" + string.Join("、", bad.Select(b => b.GetType().Name)) : ""));
        Check(reasonButtons.Contains("设定") && reasonButtons.Contains("下发"), "带原因的按钮（设定、下发）照样能 Tab 到：" + string.Join("、", reasonButtons));
        // Tab 到带原因的「设定」按 Enter：说明原因（warn 提示），不执行
        var setBtn = seen.OfType<Button>().FirstOrDefault(b => b.Content as string == "设定" && !string.IsNullOrEmpty(PrB.GetReason(b)));
        if (setBtn != null)
        {
            vm.Toasts.Clear();
            setBtn.Focus(NavigationMethod.Tab);
            Key1(window, Key.Enter, PhysicalKey.Enter);
            await Settle();
            string why = Theme.UiText.Plain(PrB.GetReason(setBtn));
            Check(vm.Toasts.Items.LastOrDefault() is { Kind: ToastKind.Warn } t && t.Text == why, $"Tab 到带原因的「设定」按 Enter → warn 提示原因（「{vm.Toasts.Items.LastOrDefault()?.Text}」）");
            vm.Toasts.Clear();
        }
        // 申请到控制权（原因清掉）：输入框回到 Tab 顺序
        vm.ApplyDemoParams(DemoParams.Parse("lease=2:remote&freeze=1"));
        await Settle();
        var spBox = insp.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Label == "设定温度" && n.IsEffectivelyVisible);
        var spInput = spBox?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        var combo = insp.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsEffectivelyVisible);
        Check(spBox != null && string.IsNullOrEmpty(PrB.GetReason(spBox)) && spInput?.IsTabStop == true && combo != null && string.IsNullOrEmpty(PrB.GetReason(combo)) && combo.IsTabStop,
            "2号机远程控制中：设定温度框、程序下拉回到 Tab 顺序");
        window.FocusManager?.ClearFocus();
        await Settle();
    }

    /// <summary>1号机 · RV1（远程控制中）：设定温度框超出量程。</summary>
    private static async Task ClampChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, List<string> lines)
    {
        vm.ResetView();
        vm.Select(1, 1);
        await Settle();
        var insp = Find<InspectorView>(window, _ => true);
        var sp = insp.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Label == "设定温度" && n.IsEffectivelyVisible);
        var input = sp?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (sp == null || input == null) { Check(false, "1号机 · RV1 检查器里有设定温度框"); return; }
        async Task<(double? Value, string? Text, ToastItem? Toast)> Type(string s)
        {
            vm.Toasts.Clear();
            input.Focus(NavigationMethod.Tab);
            await Settle();
            input.Text = s;
            Tab(window);                       // 离开（失焦时提交，同样稿 focusout）
            await Settle();
            return (sp.Value, input.Text, vm.Toasts.Items.LastOrDefault());
        }
        var (v1, t1, toast1) = await Type("999");
        Check(v1 == 200 && t1 == "200.0" && toast1 is { Kind: ToastKind.Warn, Text: "超出量程 · 已限制为 200.0" },
            $"设定温度打 999 → 离开：200.0 + warn「超出量程 · 已限制为 200.0」（{v1} /「{t1}」/「{toast1?.Text}」）");
        await Capture(window, Path.Combine(dir, $"shell-clamp-{width}.png"), lines);
        var (v2, _, toast2) = await Type("-5");
        Check(v2 == 0 && toast2 is { Kind: ToastKind.Warn, Text: "超出量程 · 已限制为 0.0" }, $"打 -5 → 0.0 + 提示（{v2} /「{toast2?.Text}」）");
        var (v3, _, toast3) = await Type("150");
        Check(v3 == 150 && toast3 == null, $"量程里的数（150）不提示（{v3} / {toast3?.Text ?? "无提示"}）");
        var (v4, t4, toast4) = await Type("abc");
        Check(v4 == 150 && t4 == "150.0" && toast4 == null, $"坏值（abc）不改值、不提示（{v4} /「{t4}」）");
        vm.Toasts.Clear();
        window.FocusManager?.ClearFocus();
        await Settle();
    }

    private static async Task ToastChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, List<string> lines)
    {
        vm.ResetView();
        await Settle();
        vm.Toast("1号机 · RV1 设定温度 120 ℃", ToastKind.Ok);
        vm.Toast("2号机 · RV4 未在运行 · 已发送停止", ToastKind.Info);
        vm.Toast("急停已发送到 1 台：1号机 · 复位需在触屏操作", ToastKind.Err);
        await Settle();
        var list = Find<ItemsControl>(window, c => c.Name == "ToastList");
        var boxes = list.GetVisualDescendants().OfType<ContentPresenter>().Where(c => c.DataContext is ToastItem)
            .Select(c => c.GetVisualDescendants().OfType<Border>().FirstOrDefault()).Where(b => b != null).Cast<Border>().ToList();
        Check(AutomationProperties.GetLiveSetting(list) == AutomationLiveSetting.Polite && boxes.Count == 3
              && boxes.All(b => AutomationProperties.GetLiveSetting(b) == AutomationLiveSetting.Polite && AutomationProperties.GetName(b) == ((ToastItem)b.DataContext!).Text),
            $"提示列表和每一条都是 polite 的实时区域，无障碍名 = 提示文字（{boxes.Count} 条）");
        await Capture(window, Path.Combine(dir, $"shell-toast-tones-{width}.png"), lines);
        vm.Toast("第四条", ToastKind.Warn);
        await Settle();
        Check(vm.Toasts.Items.Count == ToastService.MaxShown && vm.Toasts.Items[0].Kind == ToastKind.Info, "最多 3 条，最早的先走");
        var hw = "1号机 · RV1 停止 · " + Fleet.DeviceActions.HwUnconfirmedText;
        Check(ToastService.DurationOf(hw, ToastKind.Warn) == ToastService.LongFor && ToastService.DurationOf("布局已重置", ToastKind.Info) == ToastService.ShortFor
              && ToastService.DurationOf("连接失败", ToastKind.Err) == ToastService.LongFor,
            "提示的时长：错误和长句（硬件未确认）4.2 s，其余 2.6 s");
        vm.Toasts.Clear();
        await LongToastChecks(window, vm, dir, width, Check, lines);

        // 对话框的无障碍名 = 标题
        vm.OpenDialog("estop");
        await Settle();
        var frame = Find<DialogHost>(window, _ => true).GetVisualDescendants().OfType<DialogFrame>().FirstOrDefault();
        Check(frame != null && AutomationProperties.GetName(frame) == "急停", $"急停对话框的无障碍名 = 标题（「{(frame == null ? "" : AutomationProperties.GetName(frame))}」）");
        vm.Dialogs.Close();
        await Settle();
    }

    /// <summary>
    /// 本机才有的长句（合成的硬件未确认、硬件执行失败）：框最宽 820，一行放不下的换行（最多 3 行），字不画到框外、句末不被省略号截掉
    /// （审查 layout2.fsx：以前横向 StackPanel 给文字无限宽，省略号不起作用，超过 766 px 的字画到框外）。
    /// </summary>
    private static async Task LongToastChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, List<string> lines)
    {
        string merged = "1号机、2号机、3号机 · 全部停止 · " + Fleet.DeviceActions.HwUnconfirmedText;
        string failed = "1号机 · 全部停止 · " + Services.LiveDeviceSource.HwFailedText + "：RV1 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认；"
                        + "RV3 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认（HARDWARE_WRITE_FAILED）";
        vm.Toast(merged, ToastKind.Warn);
        vm.Toast(failed, ToastKind.Err);
        await Settle();
        var list = Find<ItemsControl>(window, c => c.Name == "ToastList");
        foreach (var cp in list.GetVisualDescendants().OfType<ContentPresenter>().Where(c => c.DataContext is ToastItem).ToList())
        {
            var item = (ToastItem)cp.DataContext!;
            var box = cp.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            var tb = box?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == item.Text);
            if (box == null || tb == null) { Check(false, "长句提示有框和文字"); continue; }
            var b = RectIn(box, window);
            var t = RectIn(tb, window);
            var tl = tb.TextLayout.TextLines;
            string head = item.Text.Length > 12 ? item.Text[..12] + "…" : item.Text;
            Check(b.Width <= 820.5 && t.X >= b.X && t.Right <= b.Right - 20 + 0.5 && t.Bottom <= b.Bottom - 11 + 0.5 && tl.Count >= 2 && tl.Count <= 3 && !tl.Any(l => l.HasCollapsed),
                $"长句提示「{head}」换成 {tl.Count} 行、整句在框里（框 {b.X:0.#} … {b.Right:0.#} 宽 {b.Width:0.#}，字 {t.X:0.#} … {t.Right:0.#}，高 {t.Height:0.#}）");
        }
        await Capture(window, Path.Combine(dir, $"shell-toast-long-{width}.png"), lines);
        vm.Toasts.Clear();
        await Settle();
    }

    private static async Task StopCellChecks(Window window, ShellVm vm, Action<bool, string> Check)
    {
        vm.ResetView();
        await Settle();
        var cell = Find<GlobalStopCell>(window, _ => true);
        var estop = cell.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("estop"));
        var stopAll = cell.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "全部停止");
        Check(ToolTip.GetTip(estop) as string == "选择设备后急停 · 复位只能在触屏上操作" && ToolTip.GetTip(stopAll) as string == "选择设备后停止全部反应釜（不需要控制权）",
            $"全局停止格的提示同样稿（「{ToolTip.GetTip(estop)}」）");
        vm.ApplyDemoParams(DemoParams.Parse("offline=1,2,3&freeze=1"));
        await Settle();
        vm.Toasts.Clear();
        Click(window, estop);
        await Settle();
        Check(PrB.GetReason(estop) == "没有已连接的设备" && PrB.GetReason(stopAll) == "没有已连接的设备" && !vm.Dialogs.IsOpen
              && vm.Toasts.Items.LastOrDefault() is { Kind: ToastKind.Warn, Text: "没有已连接的设备" },
            "全部断线：「全部停止」「急停」都带原因「没有已连接的设备」，点「急停」→ warn 提示、不打开对话框");
    }
}
#endif
