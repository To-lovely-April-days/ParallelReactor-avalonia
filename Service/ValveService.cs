using System;
using System.Threading.Tasks;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 阀门业务服务：艾莫迅 IO16R 线圈驱动电磁阀。线圈映射：
///   0-7  = RV1-8 进气阀
///   8    = 惰性气体源阀
///   9    = 气体 A 源阀
///   10   = 气体 B 源阀
///   11   = 排空 / Vent 阀
/// 「开/关」与「线圈通电/断电」的对应由 <see cref="HardwareOptions.IoEnergizeOpens"/> 决定（默认通电=开）。
/// </summary>
public sealed class ValveService
{
    private readonly IoModule _io;
    private readonly bool _energizeOpens;

    private const int RvBase = 0;     // RV1 进气阀 = 线圈 0
    private const int GasBase = 8;    // 惰性=8, A=9, B=10
    private const int VentCoil = 11;

    private int _fails = 3;   // 连续通讯失败次数（初值=离线，首次通讯成功才算在线）

    /// <summary>IO16R 是否在线：连续 3 次通讯失败视为离线（未配置串口时恒为离线）。</summary>
    public bool Online => _fails < 3;

    public ValveService(IModbusMaster bus, byte slave, bool energizeOpens)
    {
        _io = new IoModule(bus, slave);
        _energizeOpens = energizeOpens;
    }

    /// <summary>把"希望阀门开"转成线圈电平。</summary>
    private bool Level(bool open) => _energizeOpens ? open : !open;
    private bool OpenFrom(bool coil) => _energizeOpens ? coil : !coil;

    /// <summary>执行一笔事务并记录在线状态（异常照常抛给调用方）。</summary>
    private async Task<T> Track<T>(Func<Task<T>> op)
    {
        try { var v = await op(); _fails = 0; return v; }
        catch { if (_fails < 3) _fails++; throw; }
    }

    private Task Track(Func<Task> op) => Track(async () => { await op(); return true; });

    /// <summary>设 RV 进气阀（reactorId 1..8）。</summary>
    public Task SetReactorValveAsync(int reactorId, bool open)
        => Track(() => _io.SetCoilAsync(RvBase + reactorId - 1, Level(open)));

    /// <summary>设气源阀（index 0=惰性,1=A,2=B）。</summary>
    public Task SetGasAsync(int index, bool open)
        => Track(() => _io.SetCoilAsync(GasBase + index, Level(open)));

    /// <summary>设排空/Vent 阀。</summary>
    public Task SetVentAsync(bool open) => Track(() => _io.SetCoilAsync(VentCoil, Level(open)));

    /// <summary>
    /// 一帧（0x0F）同时写 12 路阀门：open12[0..7]=RV1-8 SV，[8..10]=惰性/A/B，[11]=Vent。
    /// 吹扫 / 泄漏测试用它整体切换阀态，避免逐路写时出现「气源与排空同时开」的中间态。
    /// </summary>
    public Task WriteAllAsync(bool[] open12)
    {
        var levels = new bool[12];
        for (int i = 0; i < levels.Length && i < open12.Length; i++) levels[i] = Level(open12[i]);
        return Track(() => _io.SetAllCoilsAsync(levels));
    }

    /// <summary>读回全部 12 路阀门的「开」状态（0-7 RV，8-10 气源，11 Vent）。</summary>
    public async Task<bool[]> ReadOpenStatesAsync()
    {
        var coils = await Track(() => _io.ReadCoilsAsync());   // 16 路
        var open = new bool[12];
        for (int i = 0; i < open.Length && i < coils.Length; i++) open[i] = OpenFrom(coils[i]);
        return open;
    }
}
