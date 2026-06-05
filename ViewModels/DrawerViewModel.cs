using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using System;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 反应釜详情抽屉。对应 HTML renderDrawer() + drawer 点击交互。
/// 持有当前选中的 Reactor，所有调节即时回写并触发气路图重绘。
/// </summary>
public partial class DrawerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public DrawerViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private Reactor? _reactor;
    [ObservableProperty] private bool _advOpen;

    // 步进器限制：[min,max,step]
    private static readonly (double min, double max, double step) LimTsp = (10, 200, 5);
    private static readonly (double min, double max, double step) LimPsp = (0, 500, 10);
    private static readonly (double min, double max, double step) LimRpm = (250, 1000, 50);
    private static readonly (double min, double max, double step) LimVol = (1, 5, 1);

    public void Open(int id)
    {
        Reactor = _main.FindReactor(id);
        AdvOpen = false;
        IsOpen = true;
        RaiseAll();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    // —— 步进器 ——
    [RelayCommand]
    private void Step(string arg)
    {
        if (Reactor is not { } c) return;
        // arg 形如 "Tsp:-1"
        var parts = arg.Split(':');
        string field = parts[0];
        int d = int.Parse(parts[1]);
        switch (field)
        {
            case "Tsp": c.TSp = Clamp(c.TSp + d * LimTsp.step, LimTsp); break;
            case "Psp": c.PSp = Clamp(c.PSp + d * LimPsp.step, LimPsp); break;
            case "RpmSp":
                c.RpmSp = (int)Clamp(c.RpmSp + d * LimRpm.step, LimRpm);
                if (c.Rpm > 0) c.Rpm = c.RpmSp;
                break;
            case "Vol": c.Vol = (int)Clamp(c.Vol + d * LimVol.step, LimVol); break;
        }
        _main.RefreshSchematic();
        RaiseAll();
    }

    private static double Clamp(double v, (double min, double max, double step) lim)
        => Math.Max(lim.min, Math.Min(lim.max, v));

    // —— 点击数字弹出数字键盘 ——
    [RelayCommand]
    private void EditField(string field)
    {
        if (Reactor is not { } c) return;
        switch (field)
        {
            case "Tsp":
                _main.Keyboard.OpenNumeric("温度 SP", c.TSp, "°C", LimTsp.min, LimTsp.max, v =>
                { c.TSp = v; _main.RefreshSchematic(); RaiseAll(); });
                break;
            case "Psp":
                _main.Keyboard.OpenNumeric("压力 SP", c.PSp, "psi", LimPsp.min, LimPsp.max, v =>
                { c.PSp = v; _main.RefreshSchematic(); RaiseAll(); });
                break;
            case "RpmSp":
                _main.Keyboard.OpenNumeric("搅拌 SP", c.RpmSp, "rpm", LimRpm.min, LimRpm.max, v =>
                { c.RpmSp = (int)v; if (c.Rpm > 0) c.Rpm = c.RpmSp; _main.RefreshSchematic(); RaiseAll(); });
                break;
            case "Vol":
                _main.Keyboard.OpenNumeric("溶液体积", c.Vol, "mL", LimVol.min, LimVol.max, v =>
                { c.Vol = (int)v; _main.RefreshSchematic(); RaiseAll(); });
                break;
        }
    }

    // —— 分段选择（桨叶 / 结束方式）——
    [RelayCommand]
    private void SetBlade(string val) { if (Reactor is { } c) { c.Blade = val; RaiseAll(); } }

    [RelayCommand]
    private void SetEnd(string val) { if (Reactor is { } c) { c.End = val; RaiseAll(); } }

    // —— 阀门 ——
    [RelayCommand]
    private void ToggleValve() { if (Reactor is { } c) _main.TryValve(c.Id); }

    // —— 操作 ——
    [RelayCommand]
    private void CountReset()
    {
        if (Reactor is not { } c) return;
        c.Gas = 0;
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 进样计数已清零，开始累计进气");
        RaiseAll();
    }

    [RelayCommand]
    private void Start()
    {
        if (Reactor is not { } c) return;
        c.State = ReactorState.Heating;
        c.Rpm = _main.StirOn ? _main.StirRpm : 0;
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 已启动");
        RaiseAll();
    }

    [RelayCommand]
    private void Stop()
    {
        if (Reactor is not { } c) return;
        c.State = ReactorState.Done;
        c.Valve = false;
        c.Rpm = 0;
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 已停止");
        RaiseAll();
    }

    [RelayCommand]
    private void Disable()
    {
        if (Reactor is not { } c) return;
        c.State = ReactorState.Idle;
        c.Valve = false;
        c.Rpm = 0;
        _main.RefreshSchematic();
        _main.Toast("warn", $"RV{c.Id} 已停用，不参与运行");
        RaiseAll();
    }

    [RelayCommand]
    private void Enable()
    {
        if (Reactor is not { } c) return;
        c.State = ReactorState.Heating;
        if (c.Vol == 0) c.Vol = 3;
        c.Rpm = _main.StirOn ? _main.StirRpm : 0;
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 已启用");
        RaiseAll();
    }

    [RelayCommand]
    private void CopyToAll()
    {
        if (Reactor is not { } c) return;
        _main.CopyConfigToAll(c);
        _main.Toast("ok", "已复制本配置到其余通道");
    }

    [RelayCommand]
    private void ToggleAdv() => AdvOpen = !AdvOpen;

    [RelayCommand]
    private void AdvReset()
    {
        if (Reactor is not { } c) return;
        c.Pid.MaxPulse = 3;
        c.Pid.PBoost = 7;
        _main.Toast("ok", "已恢复默认 PID 参数");
    }

    [RelayCommand]
    private void AdvAuto()
    {
        if (Reactor is not { } c) return;
        double sp = c.PSp;
        c.Pid.MaxPulse = sp < 100 ? 3 : sp < 300 ? 5 : 7;
        c.Pid.PBoost = sp < 100 ? 5 : sp < 300 ? 10 : 14;
        _main.Toast("ok", $"自动整定完成：maxpulse={c.Pid.MaxPulse}, pboost={c.Pid.PBoost}");
    }

    [RelayCommand]
    private void GotoGraph()
    {
        Close();
        _main.SwitchTab("graph");
    }

    // 通知所有派生只读属性刷新
    public void RaiseAll()
    {
        OnPropertyChanged(nameof(Reactor));
        OnPropertyChanged(nameof(ManualValveHint));
        OnPropertyChanged(nameof(StirInfo));
    }

    public string ManualValveHint => _main.ManualValve ? "手动可控" : "开启手动阀控后可手动操作";

    /// <summary>全局搅拌只读信息（搅拌为全部反应釜共用，在主界面调节）。</summary>
    public string StirInfo => _main.StirOn ? $"{_main.StirRpm} rpm" : "已停止";
}
