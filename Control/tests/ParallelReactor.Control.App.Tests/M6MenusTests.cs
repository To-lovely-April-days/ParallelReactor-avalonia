using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 1 组：菜单 / 弹出框的位置（Views/Shell/PopupMenus.Geometry，同样稿 shell.js placeMenu）：锚定的在锚点下沿 + 2（右对齐的以右沿对齐），
/// 指针菜单在指针处；横向夹在 [4, 窗口宽 − 宽 − 4]；下沿超出窗口高 − 4 时翻到上面（锚点上沿 − 2 − 高 / 指针 − 高，不小于 4）。
/// </summary>
public class M6MenusGeometryTests
{
    private static readonly Size Win1920 = new(1920, 1080);

    [Fact]
    public void Plus_AtTabBarEnd_SlidesLeftInsideTheWindow()
    {
        // 1920：「+」在 1502（宽 44，页签栏 34..78），弹出框 550 × 512 → 样稿 x = 1920 − 550 − 4 = 1366（不是翻到按钮左边）
        var g = PopupMenus.Geometry(new Rect(1502, 35, 44, 42), Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, null, new Size(550, 512), 0, 2, Win1920);
        Assert.Equal(1366, g.X, 3);
        Assert.Equal(79, g.Y, 3);
        Assert.Equal(1366 - 1502, g.H, 3);
        Assert.Equal(2, g.V, 3);
        // 1366：同一个按钮在 978 → 812（样稿 snap）
        var g2 = PopupMenus.Geometry(new Rect(978, 35, 44, 42), Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, null, new Size(550, 512), 0, 2, new Size(1366, 768));
        Assert.Equal(812, g2.X, 3);
    }

    [Fact]
    public void RightAligned_KeepsTheAnchorRightEdge()
    {
        var g = PopupMenus.Geometry(new Rect(1540, 85, 32, 32), Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight, null, new Size(220, 422), 0, 2, Win1920);
        Assert.Equal(1572 - 220, g.X, 3);
        Assert.Equal(119, g.Y, 3);
        Assert.Equal(0, g.H, 3);
        Assert.Equal(2, g.V, 3);
    }

    [Fact]
    public void Pointer_NearTheRightEdge_SlidesInsteadOfFlipping()
    {
        var g = PopupMenus.Geometry(new Rect(1700, 400, 200, 36), Avalonia.Controls.PlacementMode.Pointer, new Point(1800, 410), new Size(220, 300), 0, 0, Win1920);
        Assert.Equal(1920 - 220 - 4, g.X, 3);
        Assert.Equal(410, g.Y, 3);
        Assert.Equal(1696 - 1800, g.H, 3);
        // 左边同样留 4
        var l = PopupMenus.Geometry(default, Avalonia.Controls.PlacementMode.Pointer, new Point(1, 10), new Size(220, 300), 0, 0, Win1920);
        Assert.Equal(4, l.X, 3);
    }

    [Fact]
    public void Pointer_BelowTheWindow_FlipsAboveThePointer()
    {
        var g = PopupMenus.Geometry(default, Avalonia.Controls.PlacementMode.Pointer, new Point(500, 997), new Size(220, 288), 0, 0, Win1920);
        Assert.Equal(997 - 288, g.Y, 3);
        Assert.Equal(-288, g.V, 3);
    }

    [Fact]
    public void Anchored_BelowTheWindow_FlipsAboveTheAnchor_AndNeverAboveFour()
    {
        var g = PopupMenus.Geometry(new Rect(100, 900, 32, 32), Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, null, new Size(220, 300), 0, 2, Win1920);
        Assert.Equal(900 - 2 - 300, g.Y, 3);
        // 上面也放不下：贴在 4（样稿 Math.max(4, …)）
        var tall = PopupMenus.Geometry(new Rect(100, 300, 32, 32), Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, null, new Size(220, 900), 0, 2, Win1920);
        Assert.Equal(4, tall.Y, 3);
    }
}

#if DEBUG
/// <summary>
/// M6 第 1 组：菜单项的原地修补（Views/Shell/MenuItems.Patch：打开着的菜单每秒刷新，结构不变时改原来的菜单项 —— 键盘停着的那一项还在；
/// 带原因、守卫命令盖着的时候动作也跟着换）、带着重新生成方法的菜单（Shell/MenuEntries：设备移除后返回 null，打开着的菜单关掉）。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6MenusPatchTests
{
    [Fact]
    public void Patch_SameShape_UpdatesTheSameMenuItems() => M5ReviewHeadless.Run(() =>
    {
        var items = new ObservableCollection<object>();
        MenuItems.Patch(items, new[]
        {
            Shell.MenuEntry.HeadOf("2号机 · HT-PR4"), new Shell.MenuEntry { Label = "打开设备页", Icon = "open", Run = () => { } }, Shell.MenuEntry.Separator,
            new Shell.MenuEntry { Label = "取消申请控制权", Icon = "x", Run = () => { } },
            new Shell.MenuEntry { Label = "全部停止", Icon = "stop", Reason = "2号机连接中断 · 重连中 12 s", Run = () => { } },
        });
        var before = items.ToList();
        var lease = Assert.IsType<MenuItem>(items[3]);
        var stop = Assert.IsType<MenuItem>(items[4]);
        Assert.Equal(Theme.UiText.Keep("2号机连接中断 · 重连中 12 s"), Behaviors.Pr.GetReason(stop));   // 原因经 UiText.Keep（不乱断行）
        Assert.Contains("stop", stop.Classes);

        MenuItems.Patch(items, new[]
        {
            Shell.MenuEntry.HeadOf("2号机 · HT-PR4"), new Shell.MenuEntry { Label = "打开设备页", Icon = "open", Run = () => { } }, Shell.MenuEntry.Separator,
            new Shell.MenuEntry { Label = "释放控制权", Icon = "unlock", Run = () => { } },
            new Shell.MenuEntry { Label = "全部停止", Icon = "stop", Hint = "不需要控制权", Run = () => { } },
        });
        Assert.True(before.SequenceEqual(items), "结构没变：还是原来的那几个对象");
        Assert.Equal("释放控制权", lease.Header);
        Assert.Equal("unlock", Behaviors.Pr.GetIcon(lease));
        Assert.Null(Behaviors.Pr.GetReason(stop));
        Assert.Equal("不需要控制权", Behaviors.Pr.GetHint(stop));
        Assert.True(lease.Command?.CanExecute(null) == true);
    });

    [Fact]
    public void Patch_StructureChange_RebuildsAndCollapsesSeparators() => M5ReviewHeadless.Run(() =>
    {
        var items = new ObservableCollection<object>();
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "总览", Run = () => { } }, new Shell.MenuEntry { Label = "1号机", Run = () => { } } });
        var first = items[0];
        MenuItems.Patch(items, new[]
        {
            Shell.MenuEntry.Separator, new Shell.MenuEntry { Label = "总览", Run = () => { } }, Shell.MenuEntry.Separator, Shell.MenuEntry.Separator,
            new Shell.MenuEntry { Label = "1号机", Run = () => { } }, new Shell.MenuEntry { Label = "2号机", Check = true, Run = () => { } }, Shell.MenuEntry.Separator,
        });
        Assert.Equal(4, items.Count);
        Assert.NotSame(first, items[0]);
        Assert.IsType<Separator>(items[1]);
        var check = Assert.IsType<MenuItem>(items[3]);
        Assert.True(check.IsChecked);
        Assert.Equal(MenuItemToggleType.CheckBox, check.ToggleType);
    });

    [Fact]
    public void Patch_ARunChangedWhileTheItemHadAReason_RunsTheNewOne() => M5ReviewHeadless.Run(() =>
    {
        // 带原因时 Pr.Reason 把 Command 换成守卫命令（Animation 优先级）：原地修补也要换掉下面的动作，原因清掉以后执行的是最新的那个
        var items = new ObservableCollection<object>();
        string ran = "";
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "立即重连", Icon = "refresh", Reason = "正在重连", Run = () => ran = "reconnect" } });
        var mi = Assert.IsType<MenuItem>(items[0]);
        mi.Command!.Execute(null);
        Assert.Equal("", ran);   // 带原因：只说明原因
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "立即重连", Icon = "refresh", Reason = "正在重连 · 稍候", Run = () => ran = "reconnect2" } });
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "断开连接…", Icon = "unplug", Run = () => ran = "disconnect" } });
        Assert.Same(mi, items[0]);
        Assert.Null(Behaviors.Pr.GetReason(mi));
        mi.Command!.Execute(null);
        Assert.Equal("disconnect", ran);
        // 反过来：没有原因 → 带原因 → 没有原因，动作换了两次
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "启动", Icon = "play", Reason = "RV2 已在运行", Run = () => ran = "start" } });
        MenuItems.Patch(items, new[] { new Shell.MenuEntry { Label = "运行程序", Icon = "play", Run = () => ran = "progRun" } });
        mi.Command!.Execute(null);
        Assert.Equal("progRun", ran);
    });

    [Fact]
    public void DeviceMenu_ConnectingThenOnline_TheRefreshedDisconnectItemDisconnects() => M5ReviewHeadless.Run(() =>
    {
        // 演示数据：2号机 断开 → 菜单「连接」→ 重连（「立即重连」带原因「正在重连」）→ 连上（「断开连接…」，同一个菜单项）→ 执行 = 断开（不是重连）
        var shell = new Shell.ShellVm(Fleet.FleetVm.CreateDemo(Core.Demo.DemoParams.Parse("")));
        shell.Toasts.Frozen = true;
        var demo = shell.Fleet.DemoData!;
        demo.Act(demo.Device(2)!, Core.Guard.DeviceAction.Disconnect, null, quiet: true);
        shell.Fleet.RefreshNow();
        var d2 = shell.Fleet.Device(2)!;
        var menu = Assert.IsType<Shell.MenuEntries>(Shell.MenuModel.DeviceItems(shell, d2));
        var items = new ObservableCollection<object>();
        MenuItems.Patch(items, menu);
        MenuItem Conn() => items.OfType<MenuItem>().Single(m => m.Header is "连接" or "立即重连" or "断开连接…");
        var conn = Conn();
        Assert.Equal("连接", conn.Header);
        demo.Act(demo.Device(2)!, Core.Guard.DeviceAction.Reconnect, null, quiet: true);
        shell.Fleet.RefreshNow();
        MenuItems.Patch(items, menu.Rebuild!()!);
        Assert.Same(conn, Conn());
        Assert.Equal("立即重连", conn.Header);
        Assert.True(Behaviors.Pr.IsLocked(conn));
        for (int i = 0; i < 10 && d2.Conn != Controls.Pr.ConnState.Online; i++)
        {
            shell.Fleet.OnTick(true);
            shell.Fleet.RefreshNow();
            MenuItems.Patch(items, menu.Rebuild!()!);
        }
        Assert.Equal(Controls.Pr.ConnState.Online, d2.Conn);
        Assert.Same(conn, Conn());
        Assert.Equal("断开连接…", conn.Header);
        Assert.False(Behaviors.Pr.IsLocked(conn));
        conn.Command!.Execute(null);
        shell.Fleet.RefreshNow();
        Assert.True(d2.Conn == Controls.Pr.ConnState.Disconnected || shell.Dialogs.Current?.Name == "disconnect",
            $"「断开连接…」= 断开（或先确认）：{d2.Conn}，对话框 {shell.Dialogs.Current?.Name ?? "无"}");
    });

    [Fact]
    public void MenuEntries_Rebuild_IsNullAfterTheDeviceIsRemoved()
    {
        var shell = new Shell.ShellVm(Fleet.FleetVm.CreateDemo(Core.Demo.DemoParams.Parse("freeze=1")));
        shell.Toasts.Frozen = true;
        var d2 = shell.Fleet.Device(2)!;
        var menu = Assert.IsType<Shell.MenuEntries>(Shell.MenuModel.DeviceItems(shell, d2));
        Assert.NotNull(menu.Rebuild);
        Assert.Contains(menu.Rebuild!()!, e => e.Label == "移除…");
        var rv = Assert.IsType<Shell.MenuEntries>(Shell.MenuModel.ReactorItems(shell, d2, d2.Reactor(1)!));
        var demo = shell.Fleet.DemoData!;
        demo.Act(demo.Device(2)!, Core.Guard.DeviceAction.Remove, null, quiet: true);
        shell.Fleet.RefreshNow();
        Assert.Null(menu.Rebuild!());
        Assert.Null(rv.Rebuild!());
        Assert.IsType<Shell.MenuEntries>(Shell.MenuModel.Items(shell, "device"));
        Assert.NotNull(((Shell.MenuEntries)Shell.MenuModel.TabItems(shell)).Rebuild!());
    }

}
#endif
