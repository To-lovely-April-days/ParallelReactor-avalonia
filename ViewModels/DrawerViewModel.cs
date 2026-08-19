using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using System;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 反应釜详情抽屉。只保留与真实硬件对应的项：
/// 温度设定（定值 / 16 段曲线，写 AI-8 SP）、溶液体积、进气阀（IO16R 线圈）、启停操作。
/// 压力为只读测量（8AI 无控制设备）；PID 自整定集中在设置页。
/// </summary>
public partial class DrawerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public DrawerViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private Reactor? _reactor;

    // 步进器限制：[min,max,step]；上限读全局设置（设置页可改）
    private (double min, double max, double step) LimTsp => (10, _main.Settings.TMaxSp, 5);
    private (double min, double max, double step) LimVol => (1, _main.Settings.VolMax, 1);

    // SP 范围提示文本（随设置变化）
    public string TspRange => $"{LimTsp.min:0} – {LimTsp.max:0} °C";
    public string VolRange => $"{LimVol.min:0} – {LimVol.max:0} mL";

    public void Open(int id)
    {
        Reactor = _main.FindReactor(id);
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
            case "Tsp":
                c.TSp = Clamp(c.TSp + d * LimTsp.step, LimTsp);
                _ = _main.WriteTempSpSafeAsync(c.Id, c.TSp);   // 定值模式：即时下发 AI-8
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
                { c.TSp = v; _ = _main.WriteTempSpSafeAsync(c.Id, v); _main.RefreshSchematic(); RaiseAll(); });
                break;
            case "Vol":
                _main.Keyboard.OpenNumeric("溶液体积", c.Vol, "mL", LimVol.min, LimVol.max, v =>
                { c.Vol = (int)v; _main.RefreshSchematic(); RaiseAll(); });
                break;
        }
    }

    // ==================== 升温方式：定值 / 16 段曲线 ====================

    /// <summary>切换升温方式（fixed / curve）。切回定值时停止曲线。</summary>
    [RelayCommand]
    private void SetSpMode(string mode)
    {
        if (Reactor is not { } c) return;
        c.SpMode = mode;
        if (mode != "curve" && c.Profile.Running) c.Profile.Stop();
        RaiseAll();
    }

    /// <summary>编辑启用段数（1~16）。</summary>
    [RelayCommand]
    private void EditSegCount()
    {
        if (Reactor is not { } c) return;
        _main.Keyboard.OpenNumeric("曲线段数", c.Profile.SegCount, "段", 1, TempProfile.MaxSegments,
            v => c.Profile.SegCount = (int)v);
    }

    /// <summary>编辑某段目标温度。</summary>
    [RelayCommand]
    private void EditSegTemp(ProfileSegment seg)
    {
        _main.Keyboard.OpenNumeric($"第 {seg.Index} 段目标温度", seg.Temp, "°C", 0, _main.Settings.TMaxSp,
            v => seg.Temp = v);
    }

    /// <summary>编辑某段时长（分钟）。</summary>
    [RelayCommand]
    private void EditSegTime(ProfileSegment seg)
    {
        _main.Keyboard.OpenNumeric($"第 {seg.Index} 段时长", seg.Minutes, "分钟", 0.1, 6000,
            v => seg.Minutes = v);
    }

    /// <summary>启动曲线升温：以当前实测 PV 为段 1 起点，逐周期下发 SP。</summary>
    [RelayCommand]
    private void StartProfile()
    {
        if (Reactor is not { } c) return;
        // PV 异常（温控通讯未就绪时读数为 0）则从室温起步，避免段 1 从 0℃ 开始爬
        double startPv = c.T > 1 ? c.T : 25;
        c.Profile.Start(startPv);
        _main.Toast("ok", $"RV{c.Id} 曲线升温已启动 · 共 {c.Profile.SegCount} 段");
        RaiseAll();
    }

    /// <summary>停止曲线升温（仪表保持最后下发的 SP）。</summary>
    [RelayCommand]
    private void StopProfile()
    {
        if (Reactor is not { } c) return;
        c.Profile.Stop();
        _main.Toast("warn", $"RV{c.Id} 曲线升温已停止 · 保持当前给定值");
        RaiseAll();
    }

    [RelayCommand]
    private void ClearRecipe() { if (Reactor is { } c) { c.AppliedRecipe = ""; RaiseAll(); _main.Toast("ok", $"RV{c.Id} 配方标签已清除"); } }

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
        if (c.Profile.Running) c.Profile.Stop();
        _ = _main.Temp.StopChannelAsync(c.Id);   // 停温控输出(At=4)——否则仪表带着旧 SP 继续加热
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 已停止（含加热输出）");
        RaiseAll();
    }

    [RelayCommand]
    private void Disable()
    {
        if (Reactor is not { } c) return;
        c.State = ReactorState.Idle;
        c.Valve = false;
        c.Rpm = 0;
        if (c.Profile.Running) c.Profile.Stop();
        _ = _main.Temp.StopChannelAsync(c.Id);   // 停用同样要停温控输出
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
        OnPropertyChanged(nameof(TspRange));
        OnPropertyChanged(nameof(VolRange));
    }

    public string ManualValveHint => _main.ManualValve ? "手动可控" : "开启手动阀控后可手动操作";

    /// <summary>全局搅拌只读信息（搅拌为全部反应釜共用，在主界面调节）。</summary>
    public string StirInfo => _main.StirOn ? $"{_main.StirRpm} rpm" : "已停止";
}
