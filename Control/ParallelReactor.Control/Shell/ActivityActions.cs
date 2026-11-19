using System;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 泄漏测试的「中止」（v0.8.1：浮动活动面板和性能测试页共用）：停止类，先弹现有的「中止泄漏测试」确认框（StopConfirmDialogVm.LeakAbort），
/// 确认后真机 DELETE /leak-tests/current（HMI 全关阀、不放空），演示 = DemoFleet.AbortLeakDemo。不需要控制权。
/// </summary>
public static class ActivityActions
{
    /// <summary>这台设备此刻有进行中的泄漏测试（按最新的数据）。</summary>
    public static bool LeakRunning(DeviceVm d) => d.Source switch
    {
        DemoDeviceSource demo => demo.Device.LeakTest is { Running: true },
        _ => d.Source.Latest?.LeakTest is { Running: true },
    };

    /// <summary>「中止」：没有进行中的测试时只提示；否则打开确认框。</summary>
    public static void ConfirmAbort(ShellVm shell, DeviceVm d)
    {
        if (d.Source is not DemoDeviceSource && d.Source is not LiveDeviceSource { Connection: not null }) { shell.Toast(d.Alias + "未连接", ToastKind.Info); return; }
        if (!LeakRunning(d)) { shell.Toast(d.Alias + " 当前没有进行中的泄漏测试", ToastKind.Info); return; }
        shell.ShowDialog(Dialogs.StopConfirmDialogVm.LeakAbort(shell, d, () => LeakRunning(d) ? null : "泄漏测试已经结束", () => AbortAsync(shell, d)));
    }

    /// <summary>中止（确认之后）。</summary>
    public static async Task AbortAsync(ShellVm shell, DeviceVm d)
    {
        if (d.Source is DemoDeviceSource demo)
        {
            // 成功时不另提示：面板看到「结束」时统一提示一次（ActivityPanelVm.FinishToast）
            var why = demo.Fleet.AbortLeakDemo(demo.Device);
            if (why.Length > 0) shell.Toast(d.Alias + " · " + why, ToastKind.Info);
            shell.Fleet.RequestRefresh();
            return;
        }
        if (d.Source is not LiveDeviceSource { Connection: { } c }) { shell.Toast(d.Alias + "未连接", ToastKind.Info); return; }
        if (!LeakRunning(d)) { shell.Toast(d.Alias + " 当前没有进行中的泄漏测试", ToastKind.Info); return; }
        try
        {
            await c.Rest.AbortLeakTestAsync(CancellationToken.None).ConfigureAwait(true);
            shell.Toast(d.Alias + " · 已发送中止 · 阀门全关（不放空）", ToastKind.Info);
        }
        catch (Exception ex) { shell.Toast(d.Alias + " · 中止失败：" + DeviceActions.Describe(ex), ToastKind.Err); }
        shell.Fleet.RequestRefresh();
    }
}
