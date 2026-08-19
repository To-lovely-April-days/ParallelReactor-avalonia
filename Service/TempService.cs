using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 温控业务服务：把两台宇电 AI-8（A 站 RV1-4、B 站 RV5-8）抽象成 8 个通道。
/// 负责轮询 PV/输出/自整定状态，并提供写 SP、写 PID、启动自整定等操作。
/// <para>
/// 支持「温度档位」：每个档位每通道独立存一组 PID。切换档位时界面 P/I/D 随之切换；
/// 同一温区整定一次即可复用，跑反应时按目标温度选对应档位的 PID 下发。
/// </para>
/// 当前总线由 <see cref="HardwareOptions.CreateTempBus"/> 决定（真串口或内存 mock）。
/// </summary>
public sealed partial class TempService : ObservableObject
{
    private readonly TempController _ctrl;   // AI-8 一台 8 回路（RV1-8）
    private readonly MockModbusMaster? _mock;   // mock 模式下推进 PV 漂移
    private int _curBand;

    public ObservableCollection<TempChannel> Channels { get; } = new();

    /// <summary>温度档位（边界可改；默认 3 档）。</summary>
    public List<TempBand> Bands { get; } = new()
    {
        new TempBand("低温档", 0, 100),
        new TempBand("中温档", 100, 200),
        new TempBand("高温档", 200, 400),
    };

    public int CurrentBand => _curBand;

    public TempService(IModbusMaster bus, byte slave, int dpt)
    {
        _ctrl = new TempController(bus, slave, 8, dpt);   // 1 拖 8
        _mock = bus as MockModbusMaster;
        for (int i = 1; i <= 8; i++) Channels.Add(new TempChannel(i, Bands.Count));
    }

    /// <summary>通道号(1..8) → (温控器, 回路号 1..8)。一台 AI-8 直接对应。</summary>
    private (TempController c, int loop) Map(int ch) => (_ctrl, ch);

    /// <summary>切换当前档位：把界面值存回旧档，再载入新档到界面。</summary>
    public void SelectBand(int idx)
    {
        if (idx < 0 || idx >= Bands.Count || idx == _curBand) return;
        foreach (var ch in Channels) ch.StoreBand(_curBand);
        _curBand = idx;
        foreach (var ch in Channels) ch.LoadBand(_curBand);
    }

    /// <summary>给定温度落在哪个档位（超出上限归最高档）。</summary>
    public int BandIndexForTemp(double t)
    {
        for (int i = 0; i < Bands.Count; i++)
            if (t >= Bands[i].Lo && t < Bands[i].Hi) return i;
        return Bands.Count - 1;
    }

    /// <summary>按目标温度为某通道选用对应档位的 PID，并连同 SP 一起下发到仪表（跑反应前调用）。</summary>
    public async Task ApplyBandForAsync(int ch, double targetTemp)
    {
        int band = BandIndexForTemp(targetTemp);
        var (p, i, d) = Channels[ch - 1].GetBand(band);
        var (c, loop) = Map(ch);
        try
        {
            await c.RunAllAsync();     // 解除可能的 Srun 全局停止（9655/断电后 15→9655），否则输出被闸死
            await c.StartAsync(loop);  // At=0 回到 APID 自动控制——通道可能因「取消整定」被置于停止态(At=4)
            await c.SetSetpointAsync(loop, targetTemp);
            await c.SetPidAsync(loop, p, i, d);
            Channels[ch - 1].Sp = targetTemp;
        }
        catch { /* 通讯异常忽略 */ }
    }

    /// <summary>开机读一次各通道 PID 作为「当前档位」初值填充界面。串口读在后台，结果批量回 UI。
    /// 同时自愈启用回路数：Ctn&lt;8 时自动写 8（出厂可能按 4 路配置，会导致 RV5-8 完全不受控）。</summary>
    public async Task InitAsync()
    {
        _mock?.SimulateStep();   // mock：先触发种子 PID，再读取

        try
        {
            int ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            if (ctn >= 0 && ctn < 8)
            {
                await _ctrl.SetCtnAsync(8);
                ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            }
            int v = ctn;
            await Dispatcher.UIThread.InvokeAsync(() => Ctn = v);
        }
        catch { /* 通讯没通：徽章保持"未读到"，轮询恢复后会刷新 */ }
        var pids = new PidParams?[8];
        for (int ch = 1; ch <= 8; ch++)
        {
            var (c, loop) = Map(ch);
            try { pids[ch - 1] = await c.ReadPidAsync(loop).ConfigureAwait(false); }
            catch { pids[ch - 1] = null; }
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            for (int i = 0; i < 8; i++)
                if (pids[i] is { } p) Channels[i].SetBand(_curBand, p.P, p.I, p.D);
            foreach (var m in Channels) m.LoadBand(_curBand);
        });
    }

    private bool _polling;   // 防止上一轮没读完又发起新一轮，造成堆积刷爆 UI 线程

    /// <summary>周期轮询：用 3 次块读(PV/输出/模式各读 8 路)代替 24 次单读；后台读完一次性回 UI 批量刷新。</summary>
    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            _mock?.SimulateStep();
            double[] pv, op;
            int[] modes;
            int srun, ctn;
            try
            {
                pv = await _ctrl.ReadAllPvAsync().ConfigureAwait(false);
                op = await _ctrl.ReadAllOutputsAsync().ConfigureAwait(false);
                modes = await _ctrl.ReadAllModesAsync().ConfigureAwait(false);
                srun = await _ctrl.ReadSrunAsync().ConfigureAwait(false);
                ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            }
            catch { return; }   // 通讯异常：保留上次值，下个周期重试

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Srun = srun;
                Ctn = ctn;
                for (int i = 0; i < 8 && i < pv.Length; i++)
                {
                    var m = Channels[i];
                    bool tuning = modes[i] == 1;
                    bool finished = m.Autotuning && !tuning;
                    m.Pv = pv[i]; m.OutputPct = op[i]; m.Autotuning = tuning;
                    if (finished) _ = ReloadPidAsync(i + 1);   // 整定刚结束：回读新 PID
                }
            });
        }
        finally { _polling = false; }
    }

    public async Task SetSetpointAsync(int ch, double sp)
    {
        var (c, loop) = Map(ch);
        await c.SetSetpointAsync(loop, sp);
        Channels[ch - 1].Sp = sp;
    }

    public async Task SetPidAsync(int ch, double p, double i, double d)
    {
        var (c, loop) = Map(ch);
        await c.SetPidAsync(loop, p, i, d);
        var m = Channels[ch - 1];
        m.P = p; m.I = i; m.D = d;
        m.StoreBand(_curBand);
    }

    /// <summary>AutotuneAsync 的返回值：需要先设定目标温度（SP 未设或过低）。</summary>
    public const string NoSetpoint = "SP_NOT_SET";

    /// <summary>
    /// 对该通道启动自整定（At=1）。整定结束后轮询会自动回读并存入当前档位。
    /// <para>
    /// AI-8 整定原理 = 在 SP 附近做 ON/OFF 振荡后写出 PID。若 SP 未设（≈0℃，低于室温），
    /// 加热输出永远不开、振荡不起来，仪表会停在 At=1 出不来——界面表现为「整定中」但温度不动。
    /// 因此启动前先回读 SP 把关；同时写 Srun=0 确保仪表处于全局运行态（Srun=15 的仪表断电重启后
    /// 会自动进入 9655 全局停止，输出被闸死，同样表现为不加热，见手册 §6.1）。
    /// </para>
    /// 返回 null=已启动；<see cref="NoSetpoint"/>=需先设 SP；其他=错误文本。
    /// </summary>
    public async Task<string?> AutotuneAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try
        {
            double sp = await c.ReadSetpointAsync(loop).ConfigureAwait(false);
            if (sp < 35) return NoSetpoint;   // 必须明显高于室温，加热振荡才可能发生
            await c.RunAllAsync();
            await c.AutotuneAsync(loop);
            await Dispatcher.UIThread.InvokeAsync(() => Channels[ch - 1].Autotuning = true);
            return null;
        }
        catch { return "通讯失败，未能启动整定"; }
    }

    /// <summary>取消自整定：At 写 4（停止控制、关闭输出），解除「整定中」。
    /// 注意不能写回 0（APID）——那是恢复自动控温，SP 还在，通道会继续朝目标温度输出
    /// （表现为取消后输出仍有百分之几十）。取消的语义应当是"停下来"。</summary>
    public async Task CancelAutotuneAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try { await c.StopAsync(loop); } catch { /* 通讯失败也要把界面状态解开 */ }
        await Dispatcher.UIThread.InvokeAsync(() => Channels[ch - 1].Autotuning = false);
    }

    /// <summary>停止该通道的温控输出（At=4，停止控制、关闭输出）。
    /// 停止/停用反应釜时必须调用——否则仪表带着旧 SP 继续加热，界面上看着停了、加热器还在全力工作。</summary>
    public async Task StopChannelAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try { await c.StopAsync(loop); } catch { /* 通讯异常：轮询恢复后仍会按界面状态兜底 */ }
    }

    /// <summary>停止全部 8 路温控输出（逐路 At=4）。「全部停止」时调用。
    /// 不用 Srun=9655 全局闸——那会把状态徽章打红，且语义上属于急停级别。</summary>
    public async Task StopAllChannelsAsync()
    {
        for (int ch = 1; ch <= 8; ch++) await StopChannelAsync(ch);
    }

    // ===== 仪表全局运行状态（Srun 0x0845），随轮询刷新：现场排查「输出100%却不加热」用 =====
    /// <summary>-1=尚未读到；0=运行；15=运行(断电重启后自动全停)；9655=全局停止。</summary>
    [ObservableProperty] private int _srun = -1;

    public string SrunText => Srun switch
    {
        -1 => "Srun 状态：未读到（通讯未通）",
        0 => "Srun：运行（全局输出已放行）",
        15 => "Srun：运行 · 断电重启后会自动全停（建议改为 0）",
        9655 => "Srun：全局停止（所有输出被闸死）",
        _ => $"Srun：{Srun}（非常规值）"
    };
    public bool SrunOk => Srun == 0;
    public bool SrunBad => Srun == 9655;
    public bool SrunOther => !SrunOk && !SrunBad;

    partial void OnSrunChanged(int value)
    {
        OnPropertyChanged(nameof(SrunText));
        OnPropertyChanged(nameof(SrunOk));
        OnPropertyChanged(nameof(SrunBad));
        OnPropertyChanged(nameof(SrunOther));
    }

    // ===== 启用回路数 Ctn（0x0844）：<8 时后面的通道完全不受控（出厂可能按 4 路配） =====
    /// <summary>-1=尚未读到。</summary>
    [ObservableProperty] private int _ctn = -1;

    public string CtnText => Ctn < 0 ? "" : Ctn >= 8
        ? $"启用回路 Ctn={Ctn}"
        : $"启用回路 Ctn={Ctn}（不足 8：RV{Ctn + 1}~RV8 不受控！点此修复）";
    public bool CtnOk => Ctn >= 8;
    public bool CtnBad => Ctn >= 0 && Ctn < 8;

    partial void OnCtnChanged(int value)
    {
        OnPropertyChanged(nameof(CtnText));
        OnPropertyChanged(nameof(CtnOk));
        OnPropertyChanged(nameof(CtnBad));
    }

    /// <summary>把 Ctn 设为 8（修复"部分通道不受控"）。返回 null=成功，否则错误文本。</summary>
    public async Task<string?> FixCtnAsync()
    {
        try
        {
            await _ctrl.SetCtnAsync(8);
            int v = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Ctn = v);
            return v >= 8 ? null : $"写入后回读 Ctn={v}，未生效（可能需在仪表面板解锁参数）";
        }
        catch { return "通讯失败，未能写入 Ctn"; }
    }

    private async Task ReloadPidAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try
        {
            var pid = await c.ReadPidAsync(loop).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var m = Channels[ch - 1];
                m.P = pid.P; m.I = pid.I; m.D = pid.D;
                m.StoreBand(_curBand);
            });
        }
        catch { }
    }
}

/// <summary>温度档位（一段温区，边界可改；变更后标签自动刷新）。</summary>
public sealed partial class TempBand : ObservableObject
{
    public string Name { get; }

    [ObservableProperty] private double _lo;
    [ObservableProperty] private double _hi;

    public TempBand(string name, double lo, double hi) { Name = name; _lo = lo; _hi = hi; }

    public string Range => Hi >= 400 ? $"≥{Lo:0}℃" : Lo <= 0 ? $"≤{Hi:0}℃" : $"{Lo:0}–{Hi:0}℃";
    public string Label => $"{Name} · {Range}";

    partial void OnLoChanged(double value) { OnPropertyChanged(nameof(Range)); OnPropertyChanged(nameof(Label)); }
    partial void OnHiChanged(double value) { OnPropertyChanged(nameof(Range)); OnPropertyChanged(nameof(Label)); }
}

/// <summary>单个温控通道的可观察状态（绑定到界面）。P/I/D 为「当前档位」的值。</summary>
public partial class TempChannel : ObservableObject
{
    public int Id { get; }
    public string Name => $"RV{Id}";

    private readonly double[] _bp, _bi, _bd;   // 各档位的 P / I / D

    public TempChannel(int id, int bandCount)
    {
        Id = id;
        _bp = new double[bandCount];
        _bi = new double[bandCount];
        _bd = new double[bandCount];
        for (int b = 0; b < bandCount; b++)   // 每档预置一组合理默认（高温档 P/I/D 更大）
        {
            _bp[b] = 40 + 20 * b;
            _bi[b] = 100 + 25 * b;
            _bd[b] = 15 + 5 * b;
        }
        LoadBand(0);
    }

    [ObservableProperty] private double _pv;
    [ObservableProperty] private double _sp;
    [ObservableProperty] private double _outputPct;
    [ObservableProperty] private double _p;
    [ObservableProperty] private double _i;
    [ObservableProperty] private double _d;
    [ObservableProperty] private bool _autotuning;

    /// <summary>把某档位的 PID 载入到界面 P/I/D。</summary>
    public void LoadBand(int idx) { P = _bp[idx]; I = _bi[idx]; D = _bd[idx]; }

    /// <summary>把界面当前 P/I/D 存回某档位。</summary>
    public void StoreBand(int idx) { _bp[idx] = P; _bi[idx] = I; _bd[idx] = D; }

    /// <summary>直接写某档位 PID（不影响当前界面值，除非该档位正显示）。</summary>
    public void SetBand(int idx, double p, double i, double d) { _bp[idx] = p; _bi[idx] = i; _bd[idx] = d; }

    /// <summary>读某档位存储的 PID。</summary>
    public (double p, double i, double d) GetBand(int idx) => (_bp[idx], _bi[idx], _bd[idx]);
}
