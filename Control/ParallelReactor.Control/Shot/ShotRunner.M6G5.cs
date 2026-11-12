#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 第 5 组的截图准备（ShotPlan.M6G5.cs；Setup「m6g5-动作:参数」）。样稿没有地址参数的状态，准备时对演示数据做同样稿 js 参数里的那个动作：
/// <list type="bullet">
/// <item>m6g5-reconnect:k / m6g5-disconnect:k / m6g5-ack:k（Post）：页面打开之后第 k 台「立即重连」（冻结时停在正在重连…）/ 用户断开 / 确认它的第一条报警
///   （样稿 PR.sim.act(dev, 'reconnect' | 'disconnect' | 'alarmAck', …, { quiet: true })：不提示，事件照记）。同样稿参照的 js 在页面打开之后才做：
///   设备页的页签由打开页面时的未确认报警定下、确认之后不换（dv-ack3 在 1366 下仍是「报警与事件」页签上的灰卡；以前打开页面之前就确认，开的是「设备信息」）。
///   m6g5-ackinsp:k 同 ack，再把检查器正文滚到「报警」分区（顶部在正文顶部下面 8 px，同样稿 js 参数）。</item>
/// <item>m6g5-sel:k/n（Post）：打开页面之后再选中 k 号机 · RVn（「不是当前页签」提示条；样稿 PR.shell.select(k, n)）。</item>
/// <item>m6g5-panel:max|close:i|last（Post）：当前页面上第 i 块（或最后一块）看得见的面板点 □ / ×（样稿点 [data-panel-max] / [data-panel-close]）。</item>
/// <item>m6g5-live:N[:lease2]（Post）：不冻结，带着渲染的界面推进 N 秒（每秒：FleetVm.OnTick → 刷新 → 排版 → 渲染一帧），再冻结、截图、审计；
///   lease2 = 先替 2号机申请控制权（5 s 后自动批准）。每秒的耗时写 &lt;名&gt;-&lt;宽&gt;.live.txt（R10）；推进之后演示时钟不对就抛异常（error.txt）。</item>
/// </list>
/// </summary>
public static partial class ShotRunner
{
    private static (string Verb, string[] Args) M6G5Parse(string setup)
    {
        var parts = setup["m6g5-".Length..].Split(':');
        return (parts[0], parts.Skip(1).ToArray());
    }

    private static int M6G5Int(string s, string setup) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new NotSupportedException($"截图准备「{setup}」的参数「{s}」不是数字");

    /// <summary>M6 第 5 组在外框重置之前不做事（全部在 <see cref="M6G5Post"/>：同样稿参照的 js，页面打开之后才动演示数据）。</summary>
    private static bool M6G5Pre(Window window, ShellVm vm, ShotPlan.Spec spec) => false;

    /// <summary>对第 k 台做样稿 js 参数里的动作（quiet：不提示），再刷新外框与页面。</summary>
    private static void M6G5Act(Window window, ShellVm vm, string setup, string verb, string[] args)
    {
        var demo = vm.Fleet.DemoData ?? throw new InvalidOperationException("截图准备「" + setup + "」要演示数据");
        int k = M6G5Int(args.FirstOrDefault() ?? "", setup);
        var d = demo.Device(k) ?? throw new InvalidOperationException($"截图准备「{setup}」：没有 {k} 号机");
        var (action, payload) = verb switch
        {
            "reconnect" => (DeviceAction.Reconnect, (ActionPayload?)null),
            "disconnect" => (DeviceAction.Disconnect, null),
            _ => (DeviceAction.AlarmAck, new ActionPayload(Id: (d.Alarms.FirstOrDefault() ?? throw new InvalidOperationException($"截图准备「{setup}」：{k} 号机没有报警")).Id)),
        };
        var r = demo.Act(d, action, payload, quiet: true);
        if (!r.Ok) throw new InvalidOperationException($"截图准备「{setup}」：{d.Alias} 不能 {action}（{r.Message}）");
        vm.Fleet.RefreshNow();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>M6 第 5 组的截图准备：路由打开、页面重置之后，冻结之前。</summary>
    private static void M6G5Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        if (spec.Setup is not { } setup || !setup.StartsWith("m6g5-", StringComparison.Ordinal)) return;
        var (verb, args) = M6G5Parse(setup);
        switch (verb)
        {
            case "sel":
            {
                var kn = (args.FirstOrDefault() ?? "").Split('/');
                vm.Select(M6G5Int(kn[0], setup), kn.Length > 1 ? M6G5Int(kn[1], setup) : null);
                break;
            }
            case "panel":
                M6G5Panel(window, setup, args);
                break;
            case "reconnect" or "disconnect" or "ack":
                M6G5Act(window, vm, setup, verb, args);
                break;
            case "ackinsp":
                M6G5Act(window, vm, setup, verb, args);
                M6G5ScrollInspector(window, "报警", setup);
                break;
            case "live":
                M6G5Live(window, vm, spec, args);
                break;
        }
    }

    /// <summary>这一组认得的准备名（Setup 以「m6g5-」开头、只在 Post 里做事的也要列上）。</summary>
    private static bool M6G5Known(string setup) =>
        setup.StartsWith("m6g5-", StringComparison.Ordinal) && M6G5Parse(setup).Verb is "reconnect" or "disconnect" or "ack" or "ackinsp" or "sel" or "panel" or "live";

    /// <summary>检查器正文滚到某个分区（小标题 = <paramref name="title"/>）：分区顶部在正文顶部下面 8 px（同 inspFocus 的滚法，样稿 js 参数一样算）。</summary>
    private static void M6G5ScrollInspector(Window window, string title, string setup)
    {
        window.UpdateLayout();
        var insp = window.GetVisualDescendants().OfType<InspectorView>().FirstOrDefault() ?? throw new InvalidOperationException("找不到检查器");
        var scroll = insp.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        var sec = insp.GetVisualDescendants().OfType<Subhead>().FirstOrDefault(s => s.Title == title && s.IsEffectivelyVisible)?
            .GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sec"))
            ?? throw new InvalidOperationException($"截图准备「{setup}」：检查器里没有「{title}」分区");
        var top = (sec.TranslatePoint(default, scroll)?.Y ?? 0) + scroll.Offset.Y;
        scroll.Offset = new Vector(0, Math.Max(0, top - 8));
        window.UpdateLayout();
    }

    /// <summary>当前页面上看得见的面板（可视树的先后，同样稿 DOM 的先后）。</summary>
    internal static List<PrPanel> M6G5Panels(Window window)
    {
        window.UpdateLayout();
        var host = window.GetVisualDescendants().OfType<PageHost>().FirstOrDefault() ?? throw new InvalidOperationException("找不到工作区（PageHost）");
        var view = host.CurrentView ?? throw new InvalidOperationException("工作区没有当前页面");
        return view.GetVisualDescendants().OfType<PrPanel>().Where(p => p.IsEffectivelyVisible && !p.IsClosed).ToList();
    }

    /// <summary>点面板标题栏的 □ / ×（模板里的按钮，同用户点：PrPanel 改 IsMaximized / IsClosed，宿主铺满或隐藏、关掉时提示）。</summary>
    private static void M6G5Panel(Window window, string setup, string[] args)
    {
        if (args.Length < 2 || args[0] is not ("max" or "close")) throw new NotSupportedException($"截图准备「{setup}」：要「m6g5-panel:max|close:序号|last」");
        var panels = M6G5Panels(window);
        if (panels.Count == 0) throw new InvalidOperationException($"截图准备「{setup}」：当前页面上没有面板");
        var p = args[1] == "last" ? panels[^1] : panels.ElementAtOrDefault(M6G5Int(args[1], setup))
            ?? throw new InvalidOperationException($"截图准备「{setup}」：只有 {panels.Count} 块面板");
        var name = args[0] == "max" ? "PART_Max" : "PART_Close";
        var btn = p.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible)
            ?? throw new InvalidOperationException($"截图准备「{setup}」：面板「{p.TitleTip ?? p.Title}」没有 {name}");
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>
    /// 不冻结地推进 N 秒（同交互走查里的 UiTicker：每秒 FleetVm.OnTick(true) → 合并后的刷新 → 外框与页面就地更新），每一秒都排版并渲染一帧；
    /// 窗口不带 frozen（动画、过渡照常）。之后演示数据重新冻结（截图时冻结在推进之后的时刻），窗口由 Show 冻结。
    /// </summary>
    private static void M6G5Live(Window window, ShellVm vm, ShotPlan.Spec spec, string[] args)
    {
        var setup = spec.Setup!;
        int n = M6G5Int(args.FirstOrDefault() ?? "", setup);
        var demo = vm.Fleet.DemoData ?? throw new InvalidOperationException("截图准备「" + setup + "」要演示数据");
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var t0 = demo.Now;
        vm.Fleet.ApplyDemoParams(p0 with { Freeze = false });
        Frozen.Apply(window, false);
        if (args.Contains("lease2"))
        {
            var d2 = demo.Device(2) ?? throw new InvalidOperationException($"截图准备「{setup}」：没有 2号机");
            var r = demo.Act(d2, DeviceAction.LeaseRequest, null, quiet: true);
            if (!r.Ok) throw new InvalidOperationException($"截图准备「{setup}」：2号机不能申请控制权（{r.Message}）");
        }
        var log = new StringBuilder();
        var ms = new List<double>();
        for (int i = 1; i <= n; i++)
        {
            var sw = Stopwatch.StartNew();
            vm.Fleet.OnTick(true);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            ms.Add(sw.Elapsed.TotalMilliseconds);
            log.AppendLine(FormattableString.Invariant($"tick {i} {demo.Now:HH:mm:ss} {ms[^1]:0.0} ms"));
        }
        vm.Fleet.ApplyDemoParams((vm.Fleet.Params ?? p0) with { Freeze = true });
        Dispatcher.UIThread.RunJobs();
        var want = t0.AddSeconds(n);
        log.AppendLine(FormattableString.Invariant($"ticks {n} · avg {ms.Average():0.0} ms · max {ms.Max():0.0} ms · clock {demo.Now:yyyy-MM-dd HH:mm:ss}"));
        log.AppendLine("status " + vm.Status.ClockText);
        var dir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT");
        if (!string.IsNullOrWhiteSpace(dir))
            File.WriteAllText(Path.Combine(dir, FormattableString.Invariant($"{spec.Name}-{(int)window.Width}.live.txt")), log.ToString());
        if (demo.Now != want) throw new InvalidOperationException($"截图准备「{setup}」：推进 {n} 秒之后演示时钟是 {demo.Now:HH:mm:ss}，应为 {want:HH:mm:ss}（冻结没有解开？）");
    }
}
#endif
