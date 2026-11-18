using System;
using System.IO;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 本机设置（融合版 F8 设置页「本机」：常规 + 无人值守；存在 devices.json 的 "pc" 一节，<see cref="ControlConfig.Prefs"/>）：
/// <list type="bullet">
/// <item><see cref="DataDir"/>：数据目录（空 = 默认 文稿/ParallelReactor；环境变量 PR_CONTROL_DATA 优先，见 <see cref="DataDirs"/>）。</item>
/// <item><see cref="PressureUnit"/>：压力显示单位（'MPa' / 'bar' / 'psi'，默认 MPa；改了全界面马上按它换算显示，见 Core.Format.PressureDisplay）。</item>
/// <item><see cref="AutoConnect"/>：启动时自动连接（关 = 启动时哪台都不连，各台的「自动连接」勾照旧记着）。</item>
/// <item><see cref="AlarmSound"/>：报警声音（有未确认的报警级报警时每 30 s 响一次；报警弹窗上的开关是同一个）。</item>
/// <item><see cref="PreventSleep"/>：运行中阻止电脑睡眠；<see cref="Backfill"/>：断线重连后补拉这段数据。</item>
/// </list>
/// </summary>
public sealed record PcPrefs(string DataDir = "", string PressureUnit = "MPa", bool AutoConnect = true, bool AlarmSound = true,
    bool PreventSleep = true, bool Backfill = true)
{
    public static readonly PcPrefs Default = new();
}

/// <summary>
/// 本机设置的当前值与已保存的值（设置页「保存」之前改动就生效，样稿 SETP：改了状态栏 / 防睡眠马上跟着变；「保存」写进 devices.json）。
/// 报警弹窗上的「报警声音」开关直接保存（<see cref="SetAndSave"/>）。演示 / 截图 / 测试没有配置文件：只在内存里。
/// </summary>
public sealed class PrefsStore
{
    private readonly ControlConfig? _cfg;

    public PrefsStore(ControlConfig? cfg)
    {
        _cfg = cfg;
        Saved = cfg?.Prefs ?? PcPrefs.Default;
        Current = Saved;
        DataDirs.Configured = Saved.DataDir;
        Core.Format.PressureDisplay.Set(Saved.PressureUnit);
    }

    /// <summary>已保存的（devices.json 里的）。</summary>
    public PcPrefs Saved { get; private set; }

    /// <summary>现在生效的（设置页改了、还没保存的也算）。</summary>
    public PcPrefs Current { get; private set; }

    /// <summary>有没保存的改动。</summary>
    public bool Dirty => Current != Saved;

    /// <summary>能写文件（实时、配置可写）。</summary>
    public bool CanWrite => _cfg is { CanWrite: true };

    /// <summary>当前值变了 / 保存了。</summary>
    public event EventHandler? Changed;

    /// <summary>改当前值（不保存）。</summary>
    public void Set(PcPrefs p)
    {
        if (p == Current) return;
        Current = p;
        DataDirs.Configured = p.DataDir;
        Core.Format.PressureDisplay.Set(p.PressureUnit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存当前值；返回是否写进了文件（演示 / 截图 / 只读目录为 false，当前值照样算已保存）。</summary>
    public bool Save()
    {
        Saved = Current;
        bool wrote = false;
        if (_cfg != null) { _cfg.Prefs = Saved; wrote = _cfg.Save(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return wrote;
    }

    /// <summary>改一项并立即保存（不带上设置页里其它没保存的改动）：报警弹窗的声音开关。</summary>
    public void SetAndSave(Func<PcPrefs, PcPrefs> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
        Saved = change(Saved);
        DataDirs.Configured = Current.DataDir;
        Core.Format.PressureDisplay.Set(Current.PressureUnit);
        if (_cfg != null) { _cfg.Prefs = Saved; _cfg.Save(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>放弃没保存的改动。</summary>
    public void Revert() => Set(Saved);
}

/// <summary>
/// 数据目录（融合版 F8 起全软件一处）：环境变量 PR_CONTROL_DATA（截图 / 测试指到 scratch）&gt; 设置页的「数据目录」&gt; 文稿/ParallelReactor。
/// 记录页的下载缓存、报告、导出、实验方案、性能测试的输出都在它下面。
/// </summary>
public static class DataDirs
{
    /// <summary>设置页里填的（空 = 默认）。</summary>
    public static string? Configured { get; set; }

    /// <summary>默认：文稿/ParallelReactor。</summary>
    public static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ParallelReactor");

    /// <summary>环境变量指定的（有时设置页的目录不生效，设置页写明）。</summary>
    public static string? FromEnvironment => Environment.GetEnvironmentVariable("PR_CONTROL_DATA") is { Length: > 0 } env ? env : null;

    /// <summary>现在用的数据目录。</summary>
    public static string Root => FromEnvironment ?? (Configured is { Length: > 0 } c ? c.Trim() : Default);
}
