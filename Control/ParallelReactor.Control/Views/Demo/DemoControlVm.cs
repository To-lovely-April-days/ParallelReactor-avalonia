#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Demo;

/// <summary>
/// 「演示控制」工具窗口的状态（M6，仅 Debug；样稿演示条 shell.js renderDemo / demoClick）：目标设备、它的控制权 / 断线 / 报警 / 急停、
/// 设备组合、冻结。按钮的开关状态按设备此刻的状态算（同样稿：自动批准、超时之后按钮跟着变），操作交给 ShellVm 的 DemoSet*（Shell/ShellVm.Demo.cs）。
/// 冻结同时要停 UiTicker 的推进与窗口的动画：由 <see cref="_setFrozen"/>（窗口给的）一起改；toast 不冻结（样稿冻结时 toast 照常消失）。
/// </summary>
public sealed class DemoControlVm : IDisposable
{
    private readonly ShellVm _shell;
    private readonly Action<bool> _setFrozen;

    /// <param name="shell">主窗口的外壳（演示数据）。</param>
    /// <param name="setFrozen">冻结 / 解冻时改 UiTicker 与窗口的 frozen（Theme/Frozen）；测试里记下来。</param>
    public DemoControlVm(ShellVm shell, Action<bool> setFrozen)
    {
        _shell = shell;
        _setFrozen = setFrozen;
        _shell.Fleet.Changed += OnFleet;
        Refresh();
    }

    /// <summary>样稿的标题（演示条最左边）。</summary>
    public const string Caption = "原型演示 · 模拟数据 · 示例型号参数为假设";

    /// <summary>控制权的四个按钮：（代码, 字）。</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> LeaseChoices = new[] { ("local", "本地"), ("requested", "申请中"), ("remote", "远程"), ("lost", "丢失") };

    /// <summary>设备组合的三个按钮：（devs, 字）。</summary>
    public static readonly IReadOnlyList<(string Combo, string Label)> DevsChoices = new[] { ("1", "1 台"), ("3", "3 台"), ("4", "4 台") };

    /// <summary>可选的目标设备：（K, 别名）。</summary>
    public IReadOnlyList<(int K, string Alias)> Targets { get; private set; } = Array.Empty<(int, string)>();

    /// <summary>目标设备（样稿 shell.demoDev；没了时回到第一台）。</summary>
    public int TargetK { get; private set; }

    /// <summary>目标设备此刻的控制权（local / requested / remote / lost）。</summary>
    public string LeaseCode { get; private set; } = "local";

    /// <summary>目标设备不在线（断线、正在重连、用户断开都算：样稿 off = conn.state !== 'online'）。</summary>
    public bool Offline { get; private set; }

    /// <summary>目标设备有演示加的报警。</summary>
    public bool Alarm { get; private set; }

    /// <summary>目标设备在急停。</summary>
    public bool Estop { get; private set; }

    /// <summary>当前的设备组合：「1」「3」「4」，别的组合为空串（样稿 combo）。</summary>
    public string Combo { get; private set; } = "3";

    /// <summary>冻结着。</summary>
    public bool Frozen { get; private set; }

    /// <summary>按钮的状态变了（窗口跟着刷新）。</summary>
    public event Action? Changed;

    private void OnFleet(object? sender, EventArgs e) => Refresh();

    /// <summary>按演示数据此刻的状态重算按钮。</summary>
    public void Refresh()
    {
        var demo = _shell.Fleet.DemoData;
        var devs = demo?.Devices ?? (IReadOnlyList<DemoDevice>)Array.Empty<DemoDevice>();
        Targets = devs.Select(d => (d.K, d.Alias)).ToList();
        if (devs.All(d => d.K != TargetK)) TargetK = devs.Count > 0 ? devs[0].K : 0;
        var t = demo?.Device(TargetK);
        LeaseCode = t == null ? "local" : DeviceTexts.LeaseCode(t.Lease.State);
        Offline = t != null && t.Conn.State != ConnStatus.Online;
        Alarm = t != null && _shell.DemoHasAlarm(t.K);
        Estop = t?.Estop.Active == true;
        var p = _shell.Fleet.Params ?? DemoParams.Default;
        var s = p.Devs ?? "";
        string Joined(string c) => string.Join(",", DemoParams.Combos[c]);
        Combo = s.Length == 0 || s == "3" || s == Joined("3") ? "3" : s == "1" || s == Joined("1") ? "1" : s == "4" || s == Joined("4") ? "4" : "";
        Frozen = p.Freeze;
        Changed?.Invoke();
    }

    /// <summary>换目标设备（样稿 data-demo="dev"）。</summary>
    public void SelectTarget(int k)
    {
        if (Targets.All(x => x.K != k)) return;
        TargetK = k;
        Refresh();
    }

    public void SetLease(string code) { _shell.DemoSetLease(TargetK, code); Refresh(); }

    public void ToggleOffline() { _shell.DemoSetOffline(TargetK, !Offline); Refresh(); }

    public void ToggleAlarm() { _shell.DemoSetAlarm(TargetK, !Alarm); Refresh(); }

    /// <summary>急停：开 = 在设备上按下急停；关 = 在触屏复位（事件「急停已在触屏复位」）。</summary>
    public void ToggleEstop() { _shell.DemoSetEstop(TargetK, !Estop); Refresh(); }

    public void SetDevs(string combo) { _shell.DemoSetDevs(combo); Refresh(); }

    /// <summary>冻结 / 解冻：演示数据（freeze=1）、UiTicker 的推进、窗口的动画与过渡一起改。</summary>
    public void ToggleFreeze()
    {
        bool on = !Frozen;
        _shell.DemoSetFreeze(on);
        _setFrozen(on);
        Refresh();
    }

    /// <summary>重置：演示数据与外框回到默认（冻结也去掉），目标回到第一台（样稿 demoDev = null），提示「演示数据已重置」。</summary>
    public void Reset()
    {
        _shell.DemoReset();
        _setFrozen(false);
        TargetK = 0;
        Refresh();
    }

    public void Dispose() => _shell.Fleet.Changed -= OnFleet;
}
#endif
