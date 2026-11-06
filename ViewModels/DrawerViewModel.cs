using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

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
    [ViewEntry("关闭抽屉")]
    private void Close() => IsOpen = false;

    // 以下写入口一律转给 Ops.Try*（显式通道号，H3）；守卫、校验与提示文字在命令层（OperatorCommands.Channels / Valves）。
    private static CommandSource Src => MainViewModel.Src;
    private OperatorCommands Ops => _main.Ops;

    // —— 步进器 ——
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetChannelTemp), nameof(OperatorCommands.TrySetChannelVolume))]
    private void Step(string arg)
    {
        if (Reactor is not { } c) return;
        // arg 形如 "Tsp:-1"；步进后的值先按范围限幅（与今天一致），再交给命令层
        var parts = arg.Split(':');
        int d = int.Parse(parts[1]);
        switch (parts[0])
        {
            case "Tsp": _main.Local(Ops.TrySetChannelTemp(Src, c.Id, Clamp(c.TSp + d * LimTsp.step, LimTsp))); break;
            case "Vol": _main.Local(Ops.TrySetChannelVolume(Src, c.Id, (int)Clamp(c.Vol + d * LimVol.step, LimVol))); break;
        }
    }

    private static double Clamp(double v, (double min, double max, double step) lim)
        => Math.Max(lim.min, Math.Min(lim.max, v));

    // —— 点击数字弹出数字键盘 ——
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetChannelTemp), nameof(OperatorCommands.TrySetChannelVolume))]
    private void EditField(string field)
    {
        if (Reactor is not { } c) return;
        int id = c.Id;
        switch (field)
        {
            case "Tsp":
                if (!_main.LocalOk(Ops.CheckChannelTemp(Src, id))) return;   // 打开键盘前先判程序占用（确认时命令层再判一次）
                _main.Keyboard.OpenNumeric("温度 SP", c.TSp, "°C", LimTsp.min, LimTsp.max,
                    v => Ops.TrySetChannelTemp(Src, id, v));
                break;
            case "Vol":
                _main.Keyboard.OpenNumeric("溶液体积", c.Vol, "mL", LimVol.min, LimVol.max,
                    v => Ops.TrySetChannelVolume(Src, id, (int)v));
                break;
        }
    }

    // ==================== 升温方式：定值 / 16 段曲线 ====================

    /// <summary>切换升温方式（fixed / curve）。切回定值时停止曲线。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetSpMode))]
    private void SetSpMode(string mode) { if (Reactor is { } c) _main.Local(Ops.TrySetSpMode(Src, c.Id, mode)); }

    /// <summary>编辑启用段数（1~16）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetProfileSegCount))]
    private void EditSegCount()
    {
        if (Reactor is not { } c) return;
        int id = c.Id;
        _main.Keyboard.OpenNumeric("曲线段数", c.Profile.SegCount, "段", 1, TempProfile.MaxSegments,
            v => Ops.TrySetProfileSegCount(Src, id, (int)v));
    }

    /// <summary>曲线段所属通道（段对象来自某个通道的 Profile.Segments）。</summary>
    private int? OwnerOf(ProfileSegment seg)
        => (_main.Reactors.FirstOrDefault(r => r.Profile.Segments.Contains(seg)) ?? Reactor)?.Id;

    /// <summary>编辑某段目标温度。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetProfileSegment))]
    private void EditSegTemp(ProfileSegment seg)
    {
        if (OwnerOf(seg) is not { } id) return;
        _main.Keyboard.OpenNumeric($"第 {seg.Index} 段目标温度", seg.Temp, "°C", 0, _main.Settings.TMaxSp,
            v => Ops.TrySetProfileSegment(Src, id, seg.Index, v, null));
    }

    /// <summary>编辑某段时长（分钟）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetProfileSegment))]
    private void EditSegTime(ProfileSegment seg)
    {
        if (OwnerOf(seg) is not { } id) return;
        _main.Keyboard.OpenNumeric($"第 {seg.Index} 段时长", seg.Minutes, "分钟", 0.1, 6000,
            v => Ops.TrySetProfileSegment(Src, id, seg.Index, null, v));
    }

    /// <summary>启动曲线升温（全链启动 + 逐周期下发 SP，见 TryStartProfile）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStartProfile))]
    private void StartProfile() { if (Reactor is { } c) _main.Local(Ops.TryStartProfile(Src, c.Id)); }

    /// <summary>启动本通道加热（定值恒温，全链：Srun=0 + At=0 + PID 套档 + SP；SP 走防超调逼近）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStartHeat))]
    private void StartHeat() { if (Reactor is { } c) _main.Local(Ops.TryStartHeat(Src, c.Id)); }

    /// <summary>停止本通道加热（At=4，输出关闭）。只停加热，不改阀门；「升温中」回到「已启用」。
    /// 停止类操作永不拒绝：通道正在执行程序时，中止该程序（程序中止会立即下发停加热并完成收尾）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStopHeat))]
    private void StopHeat() { if (Reactor is { } c) _main.Local(Ops.TryStopHeat(Src, c.Id)); }

    /// <summary>停止曲线升温（仪表保持最后下发的 SP）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStopProfile))]
    private void StopProfile() { if (Reactor is { } c) _main.Local(Ops.TryStopProfile(Src, c.Id)); }

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryClearRecipeTag))]
    private void ClearRecipe() { if (Reactor is { } c) _main.Local(Ops.TryClearRecipeTag(Src, c.Id)); }

    // —— 阀门 ——
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetReactorValve))]
    private void ToggleValve() { if (Reactor is { } c) _main.Local(Ops.TrySetReactorValve(Src, c.Id, !c.Valve)); }

    // —— 操作 ——
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryResetGasCounter))]
    private void CountReset() { if (Reactor is { } c) _main.Local(Ops.TryResetGasCounter(Src, c.Id)); }

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryEnableChannel))]
    private void Start() { if (Reactor is { } c) _main.Local(Ops.TryEnableChannel(Src, c.Id, defaultVolume: false)); }

    /// <summary>停止通道（→ 已结束，含加热输出）。按钮在停加热 / 关阀返回前保持禁用（AsyncRelayCommand）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStopChannel))]
    private Task Stop() => Reactor is { } c ? _main.LocalAsync(Ops.TryStopChannel(Src, c.Id, disable: false)) : Task.CompletedTask;

    /// <summary>停用通道（→ 停用，不参与运行）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStopChannel))]
    private Task Disable() => Reactor is { } c ? _main.LocalAsync(Ops.TryStopChannel(Src, c.Id, disable: true)) : Task.CompletedTask;

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryEnableChannel))]
    private void Enable() { if (Reactor is { } c) _main.Local(Ops.TryEnableChannel(Src, c.Id, defaultVolume: true)); }

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryCopyChannelConfig))]
    private void CopyToAll() { if (Reactor is { } c) _main.Local(Ops.TryCopyChannelConfig(Src, c.Id)); }

    [RelayCommand]
    [ViewEntry("关闭抽屉并切到曲线页")]
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
