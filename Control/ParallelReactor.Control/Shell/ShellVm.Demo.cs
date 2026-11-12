using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 演示数据的运行时改动（M6，样稿演示条 shell.js demoClick 的移植；界面是 Debug 的工具窗口「演示控制」，Views/Demo）：
/// 每个按钮都改成一组新的地址参数，交给 <see cref="ApplyDemoParams"/> 按差异套用（同样稿 PR.route.set → applyParams，换 devs 才重建）。
/// 对话框、选中、外框参数（dialog / sel / explorer / tl / scroll）不再带上 —— 不然每按一次都会重开对话框、改回启动时的外框。
/// 只对演示数据有效（实时设备什么都不做）。
/// </summary>
public sealed partial class ShellVm
{
    /// <summary>当前的演示参数，去掉只在打开页面时用一次的部分（对话框、选中、外框、滚动）。</summary>
    private DemoParams DemoBase => (Fleet.Params ?? DemoParams.Default) with { Dialog = null, Sel = null, Explorer = null, TimelineCollapsed = null, Scroll = null };

    private static string Key(int k) => k.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 样稿 setMap：把某台的值写进「k:v」表（v 为 null 时去掉这一台）。样稿用对象存，数字键按从小到大排 —— 这里同样按设备号排，
    /// 写出来的参数与样稿演示条点出来的地址一样。
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> SetMap(IReadOnlyList<KeyValuePair<string, string>> map, int k, string? v)
    {
        var key = Key(k);
        var list = map.Where(kv => kv.Key != key).ToList();
        if (v != null) list.Add(new KeyValuePair<string, string>(key, v));
        return list.OrderBy(kv => int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue).ToList();
    }

    /// <summary>按设备号排好的键（样稿 Object.keys：数字键从小到大）。</summary>
    private static IReadOnlyList<string> Keys(IEnumerable<int> ks) => ks.Distinct().OrderBy(k => k).Select(Key).ToList();

    /// <summary>
    /// 设备组合（样稿「设备 1 / 3 / 4 台」）：devs（3 = 默认），同时清掉控制权 / 断线 / 报警 / 急停的覆盖；页签回到默认（样稿 shell.tabs = []），
    /// 当前页面还在就留在原地（它的页签重新加上），不在了回到总览。
    /// </summary>
    public void DemoSetDevs(string combo)
    {
        if (!Fleet.IsDemo) return;
        var p = DemoBase with
        {
            Devs = combo == "3" ? null : combo,
            Lease = System.Array.Empty<KeyValuePair<string, string>>(),
            Offline = null,
            Alarm = System.Array.Empty<KeyValuePair<string, string>>(),
            Estop = System.Array.Empty<string>(),
        };
        Tabs.ResetToDefaults();
        ApplyDemoParams(p, Route);
    }

    /// <summary>
    /// 某台的控制权（样稿「控制权 本地 / 申请中 / 远程 / 丢失」：lease=k:v）。地址参数里已经是这个值、设备却不在这个状态（申请自动批准、超时、
    /// 宽限期满之后）时先撤掉这一台的覆盖再套上 —— 按差异套用时同样的参数什么都不做，按钮看起来就失灵了（样稿的演示条同样会失灵）。
    /// </summary>
    public void DemoSetLease(int k, string code)
    {
        if (Fleet.DemoData?.Device(k) is not { } d) return;
        var b = DemoBase;
        if (b.LeaseFor(k) == code && DeviceTexts.LeaseCode(d.Lease.State) != code) ApplyDemoParams(b with { Lease = SetMap(b.Lease, k, null) });
        ApplyDemoParams(DemoBase with { Lease = SetMap(b.Lease, k, code) });
    }

    /// <summary>
    /// 某台断线 / 恢复（样稿「断线」：offline 按设备此刻的状态重算 —— 断线、正在重连的算上，用户断开的不算 —— 再加上 / 去掉这一台，空 = none）。
    /// 恢复一台用户断开的设备时先悄悄重连（同样稿）。
    /// </summary>
    public void DemoSetOffline(int k, bool off)
    {
        if (Fleet.DemoData is not { } demo || demo.Device(k) is not { } d) return;
        var cur = demo.Devices.Where(x => x.Conn.State != ConnStatus.Online && x.Conn.State != ConnStatus.Disconnected).Select(x => x.K).ToList();
        if (off) cur.Add(k); else cur.Remove(k);
        if (!off && d.Conn.State == ConnStatus.Disconnected) demo.Act(d, DeviceAction.Reconnect, null, quiet: true);
        ApplyDemoParams(DemoBase with { Offline = Keys(cur) });
    }

    /// <summary>
    /// 某台的演示报警（样稿「报警」：开 = alarm=k:1 加一条超压报警，关 = k:0 清除全部报警）。参数已经是这个值时先回到场景默认再套上（同控制权）。
    /// </summary>
    public void DemoSetAlarm(int k, bool on)
    {
        if (Fleet.DemoData?.Device(k) is null) return;
        var v = on ? "1" : "0";
        var b = DemoBase;
        if (b.AlarmFor(k) == v) ApplyDemoParams(b with { Alarm = SetMap(b.Alarm, k, null) });
        ApplyDemoParams(DemoBase with { Alarm = SetMap(b.Alarm, k, v) });
    }

    /// <summary>这台有没有演示加的报警（样稿 d.alarms.some(a =&gt; a.demo)：「报警」按钮的开关状态）。</summary>
    public bool DemoHasAlarm(int k) => Fleet.DemoData?.Device(k)?.Alarms.Any(a => a.Demo) == true;

    /// <summary>
    /// 某台在设备上按下急停 / 在触屏复位（样稿「急停」：estop 加上 / 去掉这一台；去掉 = 触屏复位，事件「急停已在触屏复位」）。
    /// 本机下发的急停（不在地址参数里）要复位时先记进参数再去掉；参数里有、设备却没在急停时先去掉再加上 —— 按差异套用时才会真的动作。
    /// </summary>
    public void DemoSetEstop(int k, bool on)
    {
        if (Fleet.DemoData?.Device(k) is not { } d) return;
        var b = DemoBase;
        var set = b.Estop.Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : -1).Where(x => x > 0).ToList();
        bool listed = set.Contains(k), active = d.Estop.Active;
        if (on && listed && !active) ApplyDemoParams(b with { Estop = Keys(set.Where(x => x != k)) });
        if (!on && !listed && active) ApplyDemoParams(b with { Estop = Keys(set.Append(k)) });
        if (on) set.Add(k); else set.RemoveAll(x => x == k);
        ApplyDemoParams(DemoBase with { Estop = Keys(set) });
    }

    /// <summary>冻结 / 解冻演示数据（样稿「冻结」：freeze=1，演示时钟不走）。窗口的动画与 UiTicker 由调用方一起改（见 Views/Demo）。</summary>
    public void DemoSetFreeze(bool on)
    {
        if (!Fleet.IsDemo) return;
        ApplyDemoParams(DemoBase with { Freeze = on });
    }

    /// <summary>演示数据冻结着（freeze=1）。</summary>
    public bool DemoFrozen => Fleet.Params?.Freeze == true;

    /// <summary>
    /// 重置（样稿「重置」）：演示数据从头生成、参数全部回到默认（设备组合、冻结、覆盖都去掉），页签、资源管理器 / 时间轴的展开、专注、
    /// 时间轴收起与跨度回到默认，关掉对话框（样稿新地址里没有 dialog），回到总览，提示「演示数据已重置」（info）。
    /// 与截图的 <see cref="ResetView"/> 不同的两处照样稿：选中的对象还在就留着（样稿不动 shell.sel，pruneSel 只换掉不在了的），
    /// 已有的提示不清（同一个 ToastItem 放回去，计时器照旧按时把它拿掉）。
    /// </summary>
    public void DemoReset()
    {
        if (!Fleet.IsDemo) return;
        var (k, n) = (Selection.K, Selection.N);
        var toasts = Toasts.Items.ToList();
        Fleet.ResetDemo(DemoParams.Default);
        ResetView();
        foreach (var t in toasts) Toasts.Items.Add(t);
        if (Fleet.Device(k) is { } d && (n == null || d.Reactors.Any(r => r.N == n))) Select(k, n);
        ApplyDemoParams(DemoParams.Default, Route.Overview);
        Toast("演示数据已重置", ToastKind.Info);
    }
}
