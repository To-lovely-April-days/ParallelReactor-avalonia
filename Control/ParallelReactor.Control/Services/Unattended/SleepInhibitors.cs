using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ParallelReactor.Control.Core.Unattended;

namespace ParallelReactor.Control.Services.Unattended;

/// <summary>
/// 按操作系统选「别睡眠」的实现（融合版 F8）：Windows = <see cref="WindowsSleepInhibitor"/>，macOS = <see cref="MacSleepInhibitor"/>，
/// 其它系统 = 不支持（<see cref="UnsupportedSleepInhibitor"/>）。演示数据、无头运行、截图一律 <see cref="SimulatedSleepInhibitor"/>（不调系统，
/// 状态栏照样稿写「运行中 · 已阻止睡眠」—— 那时并没有真的阻止，交接里写明）。
/// </summary>
public static class PlatformUnattended
{
    /// <summary>无头 / 截图（PR_CONTROL_HEADLESS / PR_CONTROL_SHOT）：不调系统、不出声。</summary>
    public static bool Headless =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_HEADLESS")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT"));

    public static ISleepInhibitor SleepInhibitor(bool demo)
    {
        if (demo || Headless) return new SimulatedSleepInhibitor();
        if (OperatingSystem.IsWindows()) return new WindowsSleepInhibitor();
        if (OperatingSystem.IsMacOS()) return new MacSleepInhibitor();
        return new UnsupportedSleepInhibitor();
    }

    public static IAlarmSound AlarmSound(bool demo)
    {
        if (demo || Headless) return new SilentAlarmSound();
        if (OperatingSystem.IsWindows()) return new WindowsAlarmSound();
        if (OperatingSystem.IsMacOS()) return new MacAlarmSound();
        return new SilentAlarmSound();
    }
}

/// <summary>
/// Windows：SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)（只阻止系统睡眠，屏幕照常可以关）；释放 = 只留 ES_CONTINUOUS。
/// 这个请求跟着调用的线程走：外壳在 UI 线程上调（UI 线程活到程序退出）。
/// </summary>
public sealed class WindowsSleepInhibitor : ISleepInhibitor
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public string Name => "Windows 电源请求";
    public string? LastError { get; private set; }

    public bool Acquire()
    {
        try
        {
            if (SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0) { LastError = null; return true; }
            LastError = "SetThreadExecutionState 失败（错误 " + Marshal.GetLastWin32Error() + "）";
        }
        catch (Exception ex) { LastError = ex.Message; }
        return false;
    }

    public void Release()
    {
        try { SetThreadExecutionState(EsContinuous); } catch { }
    }
}

/// <summary>
/// macOS：起一个 <c>caffeinate -i -w &lt;本进程&gt;</c>（-i = 阻止空闲睡眠，屏幕照常可以关；-w = 本程序退出 / 崩溃时它自己结束）；释放 = 结束它。
/// </summary>
public sealed class MacSleepInhibitor : ISleepInhibitor
{
    public const string Tool = "/usr/bin/caffeinate";
    private Process? _p;

    public string Name => "macOS caffeinate";
    public string? LastError { get; private set; }

    public bool Acquire()
    {
        if (_p is { HasExited: false }) return true;
        try
        {
            if (!File.Exists(Tool)) { LastError = "找不到 " + Tool; return false; }
            var psi = new ProcessStartInfo(Tool, "-i -w " + Environment.ProcessId) { UseShellExecute = false, CreateNoWindow = true };
            _p = Process.Start(psi);
            if (_p == null || _p.HasExited) { LastError = "caffeinate 没有启动"; _p = null; return false; }
            LastError = null;
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; _p = null; return false; }
    }

    public void Release()
    {
        var p = _p;
        _p = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(); } catch { }
        try { p.Dispose(); } catch { }
    }
}

/// <summary>演示 / 无头 / 截图：不调系统，申请总是「成功」（状态栏照样稿显示；没有真的阻止睡眠）。</summary>
public sealed class SimulatedSleepInhibitor : ISleepInhibitor
{
    public string Name => "模拟（演示 / 无头）";
    public string? LastError => null;
    public bool Acquire() => true;
    public void Release() { }
}

/// <summary>不支持的系统（Linux 等）：申请失败，状态栏不写「已阻止睡眠」。</summary>
public sealed class UnsupportedSleepInhibitor : ISleepInhibitor
{
    public string Name => "不支持";
    public string? LastError => "这个操作系统上还不能阻止睡眠";
    public bool Acquire() => false;
    public void Release() { }
}
