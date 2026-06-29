using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
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
public sealed class TempService
{
    private readonly TempController _a;   // RV1-4
    private readonly TempController _b;   // RV5-8
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

    public TempService(IModbusMaster bus, byte slaveA, byte slaveB, int dpt)
    {
        _a = new TempController(bus, slaveA, 4, dpt);
        _b = new TempController(bus, slaveB, 4, dpt);
        _mock = bus as MockModbusMaster;
        for (int i = 1; i <= 8; i++) Channels.Add(new TempChannel(i, Bands.Count));
    }

    /// <summary>通道号(1..8) → (对应温控器, 本机回路号 1..4)。</summary>
    private (TempController c, int loop) Map(int ch) => ch <= 4 ? (_a, ch) : (_b, ch - 4);

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
            await c.SetSetpointAsync(loop, targetTemp);
            await c.SetPidAsync(loop, p, i, d);
            Channels[ch - 1].Sp = targetTemp;
        }
        catch { /* 通讯异常忽略 */ }
    }

    /// <summary>开机读一次各通道 PID 作为「当前档位」初值填充界面。</summary>
    public async Task InitAsync()
    {
        _mock?.SimulateStep();   // mock：先触发种子 PID，再读取
        for (int ch = 1; ch <= 8; ch++)
        {
            var (c, loop) = Map(ch);
            try
            {
                var pid = await c.ReadPidAsync(loop);
                Channels[ch - 1].SetBand(_curBand, pid.P, pid.I, pid.D);
            }
            catch { /* 通讯异常忽略 */ }
        }
        foreach (var m in Channels) m.LoadBand(_curBand);
    }

    /// <summary>周期轮询：PV、输出百分比、自整定状态。</summary>
    public async Task PollAsync()
    {
        _mock?.SimulateStep();
        for (int ch = 1; ch <= 8; ch++)
        {
            var (c, loop) = Map(ch);
            var m = Channels[ch - 1];
            try
            {
                m.Pv = await c.ReadPvAsync(loop);
                m.OutputPct = await c.ReadOutputAsync(loop);
                var tuning = await c.IsAutotuningAsync(loop);
                if (m.Autotuning && !tuning)   // 整定刚结束：回读新 PID 并存入当前档位
                    await ReloadPidAsync(ch);
                m.Autotuning = tuning;
            }
            catch { }
        }
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

    /// <summary>对该通道启动自整定（At=1）。整定结束后轮询会自动回读并存入当前档位。</summary>
    public async Task AutotuneAsync(int ch)
    {
        var (c, loop) = Map(ch);
        await c.AutotuneAsync(loop);
        Channels[ch - 1].Autotuning = true;
    }

    private async Task ReloadPidAsync(int ch)
    {
        var (c, loop) = Map(ch);
        var pid = await c.ReadPidAsync(loop);
        var m = Channels[ch - 1];
        m.P = pid.P; m.I = pid.I; m.D = pid.D;
        m.StoreBand(_curBand);
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
