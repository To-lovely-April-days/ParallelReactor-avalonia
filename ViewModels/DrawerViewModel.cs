using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using System;
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
    private void Close() => IsOpen = false;

    /// <summary>
    /// 该通道正在执行程序（C20）：温度 / 加热 / 启用类操作由程序控制，抽屉拒绝并提示（停止 / 停用仍允许，见 C9）。
    /// </summary>
    private bool OwnedByProgram(Reactor c)
    {
        if (_main.Program.RunningRv != c.Id) return false;
        _main.Toast("warn", $"RV{c.Id} 正在执行程序 · 请先中止程序");
        return true;
    }

    // —— 步进器 ——
    [RelayCommand]
    private void Step(string arg)
    {
        if (Reactor is not { } c) return;
        if (arg.StartsWith("Tsp") && OwnedByProgram(c)) return;
        // arg 形如 "Tsp:-1"
        var parts = arg.Split(':');
        string field = parts[0];
        int d = int.Parse(parts[1]);
        switch (field)
        {
            case "Tsp":
                c.TSp = Clamp(c.TSp + d * LimTsp.step, LimTsp);
                _main.SetFixedTarget(c);   // 加热中：交给防超调逼近重新规划；未加热：即时下发 AI-8
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
        if (field == "Tsp" && OwnedByProgram(c)) return;
        switch (field)
        {
            case "Tsp":
                _main.Keyboard.OpenNumeric("温度 SP", c.TSp, "°C", LimTsp.min, LimTsp.max, v =>
                { c.TSp = v; _main.SetFixedTarget(c); _main.RefreshSchematic(); RaiseAll(); });
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
        if (Reactor is not { } c || OwnedByProgram(c)) return;
        c.SpMode = mode;
        if (mode != "curve" && c.Profile.Running) c.Profile.Stop();
        if (mode != "fixed") c.Approach.Stop();
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

    /// <summary>启动曲线升温：以当前实测 PV 为段 1 起点，逐周期下发 SP。
    /// 同时做全链启动（Srun=0 + At=0 + PID 套档）——开机/停止后通道处于 At=4 停止态，
    /// 只写 SP 曲线会"空转"（引擎在跑、仪表不出力）。PID 档按曲线最高段温度选。</summary>
    [RelayCommand]
    private void StartProfile()
    {
        if (Reactor is not { } c || OwnedByProgram(c)) return;
        if (_main.HeatBlockReason(c.Id) is { } why) { _main.Toast("err", why); return; }
        // PV 无效（温控离线 / 读数为 0）则从室温起步，避免段 1 从 0℃ 开始爬
        double startPv = c.TOnline && c.T > 1 ? c.T : 25;
        double bandTemp = startPv;
        foreach (var s in c.Profile.Segments)
            if (s.Enabled) bandTemp = Math.Max(bandTemp, s.Temp);
        _ = StartProfileAsync(c, bandTemp, startPv);
        c.Profile.Start(startPv);
        if (c.State is ReactorState.Idle or ReactorState.Done or ReactorState.Ready)
            c.State = ReactorState.Heating;   // 曲线升温也是加热，状态同步为「升温中」
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 曲线升温已启动 · 共 {c.Profile.SegCount} 段");
        RaiseAll();
    }

    private async Task StartProfileAsync(Reactor c, double bandTemp, double startPv)
    {
        if (await _main.Temp.StartChannelAsync(c.Id, bandTemp, startPv)) return;
        // 全链启动没成功：仪表不出力，曲线引擎空转没有意义，停掉并提示
        if (c.Profile.Running) c.Profile.Stop();
        _main.ReportHeatStartFailed(c.Id);
        RaiseAll();
    }

    /// <summary>启动本通道加热（定值恒温，全链：Srun=0 + At=0 + PID 套档 + SP）。
    /// SP 走防超调逼近：先写 目标−Δ 判稳，再慢推到目标（Δ 等参数见设置页「参数与报警」）。</summary>
    [RelayCommand]
    private void StartHeat()
    {
        if (Reactor is not { } c || OwnedByProgram(c)) return;
        if (_main.HeatBlockReason(c.Id) is { } why) { _main.Toast("err", why); return; }
        if (c.TSp < 35) { _main.Toast("err", "请先设置目标温度（≥35℃）再启动加热"); return; }
        _main.StartFixedHeat(c);
        if (c.State is ReactorState.Idle or ReactorState.Done or ReactorState.Ready)
            c.State = ReactorState.Heating;   // 加热真的开始了才显示「升温中」
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 加热已启动 · 目标 {c.TSp:0}℃ · 防超调逼近");
        RaiseAll();
    }

    /// <summary>停止本通道加热（At=4，输出关闭）。只停加热，不改阀门；「升温中」回到「已启用」。
    /// 停止类操作永不拒绝：通道正在执行程序时，中止该程序（程序中止会立即下发停加热并完成收尾）。</summary>
    [RelayCommand]
    private void StopHeat()
    {
        if (Reactor is not { } c) return;
        if (_main.Program.RunningRv == c.Id)
        {
            if (_main.Program.AbortProgram())
                _main.Toast("warn", $"RV{c.Id} 停止加热 · 正在执行的程序已中止");
            RaiseAll();
            return;
        }
        if (c.Profile.Running) c.Profile.Stop();   // 曲线在跑也一并停，避免引擎继续抬 SP
        c.Approach.Stop();
        if (c.State == ReactorState.Heating) c.State = ReactorState.Ready;
        _ = _main.StopHeatAsync(c.Id, $"RV{c.Id} 加热已停止（输出关闭）");
        _main.RefreshSchematic();
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
        if (Reactor is not { } c || OwnedByProgram(c)) return;
        if (_main.Estopped) { _main.Toast("err", "急停状态下不可启动 · 请先复位"); return; }
        if (_main.GasBusy) { _main.Toast("warn", $"{_main.GasTask}进行中 · 请等待完成或先停止"); return; }
        c.State = ReactorState.Ready;   // 启用 ≠ 升温中：只表示通道参与运行，开始加热后才转 Heating
        c.Rpm = _main.StirOn ? _main.StirRpm : 0;
        _main.RefreshSchematic();
        _main.Toast("ok", $"RV{c.Id} 已启用");
        RaiseAll();
    }

    [RelayCommand]
    private Task Stop() => StopChannelAsync(ReactorState.Done, "已停止（含加热输出）");

    [RelayCommand]
    private Task Disable() => StopChannelAsync(ReactorState.Idle, "已停用，不参与运行");

    /// <summary>
    /// 停止 / 停用通道（C9）。顺序：
    /// <list type="number">
    /// <item>置状态、停曲线 / 逼近（界面立即生效）；</item>
    /// <item>该通道正被程序执行或参与气路序列（泄漏测试 / 程序吹扫升压稳压）：先按占用者干净地中止那个序列，
    ///       等它自己的收尾全关阀、释放气路（否则序列下一个脉冲会把 SV 重新打开）；</item>
    /// <item>停温控输出（At=4）——程序已被中止时由程序收尾停加热，这里不重复下发；</item>
    /// <item>SV 仍开着就关闭（写成功才改显示）。</item>
    /// </list>
    /// </summary>
    private async Task StopChannelAsync(ReactorState to, string what)
    {
        if (Reactor is not { } c) return;
        c.State = to;
        c.Rpm = 0;
        if (c.Profile.Running) c.Profile.Stop();
        c.Approach.Stop();
        _main.RefreshSchematic();
        RaiseAll();

        bool programAborted = await _main.ReleaseChannelFromGasAsync(c.Id);
        // 停温控输出(At=4)——否则仪表带着旧 SP 继续加热
        if (!programAborted) await _main.StopHeatAsync(c.Id, $"RV{c.Id} {what}");
        if (c.Valve) await _main.CloseReactorValveAsync(c.Id);
        _main.RefreshSchematic();
        RaiseAll();
    }

    [RelayCommand]
    private void Enable()
    {
        if (Reactor is not { } c || OwnedByProgram(c)) return;
        if (_main.Estopped) { _main.Toast("err", "急停状态下不可启用通道 · 请先复位"); return; }
        if (_main.GasBusy) { _main.Toast("warn", $"{_main.GasTask}进行中 · 请等待完成或先停止"); return; }
        c.State = ReactorState.Ready;
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
