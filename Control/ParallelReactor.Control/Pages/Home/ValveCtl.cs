using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>
/// 点阀门（HMI v0.7，用户 2026-11-19：「我不要维护模式 我要远程控制可以控制一切操作」）：气路图的阀门、检查器的 SVn 开关、共用资源的气路小块都走这里。
/// 开着就关（停止类，不要控制权），关着就开（控制类，要控制权）；守卫不让时提示原因、什么都不发。设备的联锁（超压、加压 / 反应中、气路序列占用、急停）照常由设备拒绝并提示。
/// </summary>
public static class ValveCtl
{
    /// <summary>第 n 路进气阀 SVn。</summary>
    public static void ToggleSv(ShellVm shell, DeviceVm d, int n, bool isOpen) =>
        Run(shell, d, isOpen ? DeviceAction.ValveClose : DeviceAction.ValveOpen, ActionPayload.Ch(n));

    /// <summary>一路气（型号表的气路键；排空也是一路）。</summary>
    public static void ToggleGas(ShellVm shell, DeviceVm d, string key, bool isOpen) =>
        Run(shell, d, isOpen ? DeviceAction.GasClose : DeviceAction.GasOpen, new ActionPayload(Id: key));

    /// <summary>不能点时的原因（悬停 / 禁用用）；能点时 null。</summary>
    public static string? SvReason(DeviceVm d, int n, bool isOpen) => Nz(d.Actions.Why(isOpen ? DeviceAction.ValveClose : DeviceAction.ValveOpen, ActionPayload.Ch(n)));
    public static string? GasReason(DeviceVm d, string key, bool isOpen) => Nz(d.Actions.Why(isOpen ? DeviceAction.GasClose : DeviceAction.GasOpen, new ActionPayload(Id: key)));

    /// <summary>悬停：「进气阀 SV1 · 开 · 点击关闭」/「… · 关 · 点击打开（要控制权）」；不能点时写原因。</summary>
    public static string Tip(string name, bool isOpen, string? reason) =>
        name + " · " + (isOpen ? "开" : "关") + " · " + (reason ?? (isOpen ? "点击关闭" : "点击打开（要控制权）"));

    private static void Run(ShellVm shell, DeviceVm d, DeviceAction action, ActionPayload p)
    {
        var why = d.Actions.Why(action, p);
        if (why.Length > 0) { shell.Toast(d.Alias + " · " + why, ToastKind.Warn); return; }
        _ = d.Actions.RunAsync(action, p);
    }

    private static string? Nz(string s) => s.Length > 0 ? s : null;
}
