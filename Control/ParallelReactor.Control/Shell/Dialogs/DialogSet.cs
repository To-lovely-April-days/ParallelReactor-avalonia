namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 样稿的对话框（PORT-PLAN §2.1「对话框宿主 + 9 个对话框」、§5 M3；样稿 js/dialogs.js，外观 css/dialogs.css）。演示与真机都注册，
/// 同一套视图模型（Shell/Dialogs/*）经 FleetVm 改设备（演示 = Core 的 DemoFleet，真机 = Services/DeviceRegistry + LiveDeviceSource）。
/// 名字同样稿 ?dialog=name:arg：
/// <list type="bullet">
/// <item>add —— 添加设备（菜单「添加设备…」、资源管理器「添加」、设备管理「添加设备…」）。</item>
/// <item>devices —— 设备管理。</item>
/// <item>lease:k —— 申请控制权（检查器「申请控制权」、菜单「申请控制权…」），倒计时环。</item>
/// <item>estop / estop:k —— 急停（全局停止格、菜单「急停…」/「急停 1号机…」）：多选，已连接的默认全选 / 只预选 k。</item>
/// <item>stopall —— 全部停止（全局停止格、菜单「全部停止…」）：多选。</item>
/// <item>edit:k、rename:k、remove:k、disconnect:k —— 编辑设置、重命名、移除、断开确认（断开确认只在本机持有 / 申请控制权时打开）。</item>
/// <item>runall:k —— 「全部运行」的清单确认（真机，D6；演示照样稿直接运行，检查器不打开它）。</item>
/// <item>client —— 本机客户端名（真机；M3 复核新加：样稿把它放在第二阶段的设置页）。</item>
/// <item>exit —— 退出确认（真机；M3 复核新加：本机的控制权申请还在触屏上时，R7）。</item>
/// </list>
/// M2 的临时对话框「连接设备」（connect）与无勾选的急停 / 全部停止确认由这些取代。
/// </summary>
public static class DialogSet
{
    public static void Register(ShellVm shell)
    {
        shell.Dialogs.Register("add", _ => new AddDeviceDialogVm(shell));
        shell.Dialogs.Register("devices", _ => new DevicesDialogVm(shell));
        shell.Dialogs.Register("lease", arg => LeaseDialogVm.Create(shell, arg));
        shell.Dialogs.Register("estop", arg => new EstopDialogVm(shell, arg));
        shell.Dialogs.Register("stopall", arg => new StopAllDialogVm(shell, arg));
        shell.Dialogs.Register("edit", arg => EditDeviceDialogVm.Create(shell, arg));
        shell.Dialogs.Register("rename", arg => RenameDialogVm.Create(shell, arg));
        shell.Dialogs.Register("remove", arg => RemoveDialogVm.Create(shell, arg));
        shell.Dialogs.Register("disconnect", arg => DisconnectDialogVm.Create(shell, arg));
        shell.Dialogs.Register("runall", arg => RunAllDialogVm.Create(shell, arg));
        // M3 复核新加（样稿没有）：本机客户端名（真机，「设备 › 本机客户端名…」）、退出确认（真机，控制权申请还在触屏上时）
        shell.Dialogs.Register("client", _ => ClientNameDialogVm.Create(shell));
        shell.Dialogs.Register("exit", _ => ExitDialogVm.Create(shell));
    }
}
