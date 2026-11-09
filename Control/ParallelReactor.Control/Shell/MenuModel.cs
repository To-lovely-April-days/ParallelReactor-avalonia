using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 一个菜单项（样稿 shell.menu 的 items：{ label, icon, run, reason, check, danger, hint, head, alarm } 或 '-'）。
/// <see cref="Reason"/> 非空 = 禁用并说明原因（b:Pr.Reason：锁图标提示，点击 toast 原因）；图标是 stop 的项画成深灰实心方块（同停止按钮）。
/// </summary>
public sealed record MenuEntry
{
    public string? Label { get; init; }
    public string? Icon { get; init; }
    public Action? Run { get; init; }
    public string? Reason { get; init; }
    public bool Check { get; init; }
    public bool Danger { get; init; }
    public string? Hint { get; init; }
    /// <summary>标题项（不可点，14 px 600 t-3）。</summary>
    public string? Head { get; init; }
    /// <summary>错误报警红点（溢出菜单里的页签）。</summary>
    public bool Alarm { get; init; }
    public bool IsSeparator { get; init; }

    public static MenuEntry Separator { get; } = new() { IsSeparator = true };
    public static MenuEntry HeadOf(string text) => new() { Head = text };
}

/// <summary>
/// 菜单（PORT-PLAN §2.1「菜单栏 34 px」；样稿 shell.js MENUS / menuItems / devMenuItems，文字与顺序逐字照抄）：
/// 文件 设备 视图 程序 工具 窗口 帮助。每次打开时按当前状态重新生成（勾选、标题项「当前：1号机」、禁用原因）。
/// 设备菜单只有一份（菜单栏「设备」、右键、总览分区 «⋯» 共用 <see cref="DeviceItems"/>）。
/// 对话框（添加设备、设备管理、控制权、急停、全部停止、编辑、重命名、移除、断开连接）见 Shell/Dialogs（M3）。
/// </summary>
public static class MenuModel
{
    public static readonly IReadOnlyList<(string Key, string Title)> Menus = new[]
    {
        ("file", "文件"), ("device", "设备"), ("view", "视图"), ("program", "程序"), ("tools", "工具"), ("window", "窗口"), ("help", "帮助"),
    };

    public static IReadOnlyList<MenuEntry> Items(ShellVm shell, string key)
    {
        MenuEntry Go(string label, string icon, Route r) => new() { Label = label, Icon = icon, Run = () => shell.Navigate(r) };
        MenuEntry Ph(string k)
        {
            var p = Placeholders.Get(k)!;
            return Go(p.Title, p.Icon, Route.Placeholder(k));
        }
        switch (key)
        {
            case "file":
                return new[]
                {
                    Ph("program"),
                    new MenuEntry { Label = "导出数据…", Icon = "download", Run = () => shell.Toast("导出数据（第二阶段设计）", ToastKind.Info) },
                    new MenuEntry { Label = "打印报告…", Icon = "records", Run = () => shell.Toast("打印报告（第二阶段设计）", ToastKind.Info) },
                    MenuEntry.Separator,
                    new MenuEntry { Label = "退出", Icon = "x", Run = shell.Exit },
                };
            case "device":
            {
                var d = shell.SelectedDevice;
                var list = new List<MenuEntry>
                {
                    new() { Label = "添加设备…", Icon = "plus", Run = () => shell.OpenDialog("add") },
                    new() { Label = "设备管理…", Icon = "list", Run = () => shell.OpenDialog("devices") },
                };
                // 真机：本机客户端名（样稿在第二阶段的设置页里改；之前从这里改，M3 复核新加。演示数据没有这一项，菜单同样稿）
                if (!shell.Fleet.IsDemo) list.Add(new() { Label = Dialogs.ClientNameDialogVm.MenuLabel, Icon = "monitor", Run = () => shell.OpenDialog("client") });
                list.Add(MenuEntry.Separator);
                if (d != null)
                {
                    list.Add(MenuEntry.HeadOf("当前：" + d.Alias));
                    list.Add(LeaseItem(shell, d));
                    list.Add(ConnItem(shell, d));
                    list.Add(new MenuEntry { Label = "编辑设置…", Icon = "settings", Run = () => shell.OpenDialog("edit", d.K.ToString()) });
                }
                list.Add(MenuEntry.Separator);
                list.Add(new MenuEntry { Label = "全部停止…", Icon = "stop", Run = () => shell.OpenDialog("stopall") });
                list.Add(new MenuEntry { Label = "急停…", Icon = "estop", Danger = true, Run = () => shell.OpenDialog("estop") });
                return list;
            }
            case "view":
                return new[]
                {
                    Go("总览", "overview", Route.Overview),
                    new MenuEntry { Label = "设备资源管理器", Icon = "devices", Check = shell.Layout.Mode != ExplorerMode.Collapsed, Run = shell.ToggleExplorer },
                    new MenuEntry { Label = "时间轴", Icon = "clock", Check = !shell.Layout.TimelineCollapsed, Run = shell.ToggleTimeline },
                    MenuEntry.Separator,
                    new MenuEntry { Label = "重置布局", Icon = "layout", Run = shell.ResetLayout },
                };
            case "program":
                return new[]
                {
                    Ph("program"),
                    Go("程序库", "list", Route.Placeholder("program")),
                    MenuEntry.Separator,
                    new MenuEntry { Label = "下发程序…", Icon = "send", Run = () => shell.Toast("在右侧检查器选择反应釜后下发", ToastKind.Info) },
                };
            case "tools":
                return new[] { Ph("curves"), Ph("perf"), Ph("records"), Ph("alarms"), Ph("settings") };
            case "window":
            {
                var list = shell.Tabs.Tabs.Select(t => new MenuEntry
                {
                    Label = t.Label, Icon = t.Icon ?? t.MenuIcon, Check = t.IsActive, Run = () => shell.Navigate(t.Route),
                }).ToList();
                list.Add(MenuEntry.Separator);
                list.Add(new MenuEntry { Label = "关闭其他页签", Icon = "x", Run = shell.CloseOtherTabs });
                return list;
            }
            case "help":
                return new[]
                {
                    new MenuEntry { Label = "使用说明", Icon = "info", Run = () => shell.Toast("使用说明（第二阶段设计）", ToastKind.Info) },
                    new MenuEntry { Label = "接口说明", Icon = "link", Run = () => shell.Toast("每台设备一个接口地址、一个令牌、一个控制权", ToastKind.Info) },
                    MenuEntry.Separator,
                    new MenuEntry { Label = "关于", Icon = "logo", Run = () => shell.Toast(ShellVm.AboutText, ToastKind.Info) },
                };
            default:
                return Array.Empty<MenuEntry>();
        }
    }

    /// <summary>「›」溢出菜单：全部页签（断线 = wifi-off 图标，错误报警红点，当前页签打勾，放不下的写「未显示」）。</summary>
    public static IReadOnlyList<MenuEntry> TabItems(ShellVm shell) =>
        shell.Tabs.Tabs.Select(t => new MenuEntry
        {
            Label = t.Label, Icon = t.Offline ? "wifioff" : t.Icon ?? t.MenuIcon, Alarm = t.ErrAlarm, Check = t.IsActive,
            Hint = t.IsHidden ? "未显示" : null, Run = () => shell.Navigate(t.Route),
        }).ToList();

    /// <summary>控制权：远程 / 宽限 = 释放；申请中 = 取消申请控制权；本地 = 申请控制权…（样稿 leaseItem）。</summary>
    public static MenuEntry LeaseItem(ShellVm shell, IFleetDevice d) => d.Lease switch
    {
        LeaseState.Remote or LeaseState.Lost => new MenuEntry { Label = "释放控制权", Icon = "unlock", Run = () => shell.DeviceAct(d.K, "leaseRelease") },
        LeaseState.Requested => new MenuEntry { Label = "取消申请控制权", Icon = "x", Run = () => shell.DeviceAct(d.K, "leaseCancel") },
        // 本地只读（真机还有「其他客户端持有」）：打开申请控制权对话框（倒计时环），守卫的原因写在菜单项上
        _ => new MenuEntry
        {
            Label = "申请控制权…", Icon = "key", Reason = Nz(d.Why("leaseRequest")),
            Run = () => shell.OpenDialog("lease", d.K.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        },
    };

    /// <summary>
    /// 连接：在线 = 断开连接…（本机持有或正在申请控制权时先确认，同样稿 requestDisconnect）；断线 = 立即重连；用户断开的 = 连接（样稿 connItem）。
    /// </summary>
    public static MenuEntry ConnItem(ShellVm shell, IFleetDevice d) => d.Conn == ConnState.Online
        ? new MenuEntry
        {
            Label = "断开连接…", Icon = "unplug",
            // 本机持有或正在申请控制权时先确认（样稿 requestDisconnect）
            Run = () => shell.RequestDisconnect(d),
        }
        : new MenuEntry
        {
            Label = d.Conn == ConnState.Disconnected ? "连接" : "立即重连", Icon = "refresh", Reason = Nz(d.Why("reconnect")),
            Run = () => shell.DeviceAct(d.K, "reconnect"),
        };

    /// <summary>
    /// 一台设备的完整菜单（右键、总览分区 «⋯» 共用；样稿 devMenuItems）：extra = 调用方自己的项（放在「打开设备页」后面）。
    /// 急停在最后、单独一组，先打开只预选这一台的急停对话框。
    /// </summary>
    public static IReadOnlyList<MenuEntry> DeviceItems(ShellVm shell, IFleetDevice d, IEnumerable<MenuEntry>? extra = null)
    {
        var k = d.K.ToString();
        var list = new List<MenuEntry>
        {
            MenuEntry.HeadOf(d.Alias + " · " + d.ModelLabel),
            new() { Label = "打开设备页", Icon = "open", Run = () => shell.Navigate(Route.Device(d.K)) },
        };
        if (extra != null) list.AddRange(extra);
        list.Add(MenuEntry.Separator);
        list.Add(new MenuEntry { Label = "全部停止", Icon = "stop", Hint = "不需要控制权", Reason = Nz(d.Why("stopAll")), Run = () => shell.DeviceAct(d.K, "stopAll") });
        list.Add(MenuEntry.Separator);
        list.Add(LeaseItem(shell, d));
        list.Add(ConnItem(shell, d));
        list.Add(MenuEntry.Separator);
        list.Add(new MenuEntry { Label = "重命名…", Icon = "edit", Run = () => shell.OpenDialog("rename", k) });
        list.Add(new MenuEntry { Label = "编辑设置…", Icon = "settings", Run = () => shell.OpenDialog("edit", k) });
        list.Add(new MenuEntry { Label = "移除…", Icon = "trash", Run = () => shell.OpenDialog("remove", k) });
        list.Add(MenuEntry.Separator);
        list.Add(new MenuEntry { Label = "急停 " + d.Alias + "…", Icon = "estop", Danger = true, Reason = Nz(d.Why("estop")), Run = () => shell.OpenDialog("estop", k) });
        return list;
    }

    /// <summary>
    /// 一个反应釜的右键菜单（资源管理器的反应釜行、时间轴的行标签、反应釜页签共用；样稿 ctxMenu 'rv'）：
    /// 标题「1号机 · RV1 · 反应中」· 打开 · 运行程序（已下发 / 已中止 / 已结束的程序）或启动 · 停止 · 停止加热 · 中止程序 · 打标记。
    /// 原因来自 Core 的守卫（停止类只要连接）；执行时 DeviceActions 再查一次。
    /// </summary>
    public static IReadOnlyList<MenuEntry> ReactorItems(ShellVm shell, Fleet.DeviceVm d, Fleet.ReactorVm r)
    {
        int n = r.N;
        var p = Core.Guard.ActionPayload.Ch(n);
        var prog = d.Source.Program(n);
        var runAct = prog is { Status: Core.Programs.ProgramStatus.Loaded or Core.Programs.ProgramStatus.Aborted or Core.Programs.ProgramStatus.Done }
            ? Core.Guard.DeviceAction.ProgRun : Core.Guard.DeviceAction.Start;
        MenuEntry Act(string label, string icon, Core.Guard.DeviceAction a, Core.Guard.ActionPayload? payload = null) => new()
        {
            Label = label, Icon = icon, Reason = Nz(d.Actions.Why(a, payload ?? p)), Run = () => _ = d.Actions.RunAsync(a, payload ?? p),
        };
        return new[]
        {
            MenuEntry.HeadOf(d.Alias + " · " + r.Rv + " · " + r.OwnWord),
            new MenuEntry { Label = "打开", Icon = "open", Run = () => shell.Navigate(Route.Reactor(d.K, n)) },
            MenuEntry.Separator,
            Act(runAct == Core.Guard.DeviceAction.ProgRun ? "运行程序" : "启动", "play", runAct),
            Act("停止", "stop", Core.Guard.DeviceAction.Stop),
            Act("停止加热", "flame", Core.Guard.DeviceAction.HeatStop),
            Act("中止程序", "x", Core.Guard.DeviceAction.ProgAbort),
            MenuEntry.Separator,
            Act("打标记", "flag", Core.Guard.DeviceAction.SessionMark, new Core.Guard.ActionPayload(N: n, Text: r.Rv + " 标记")),
        };
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
