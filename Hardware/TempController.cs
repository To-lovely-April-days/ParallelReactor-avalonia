using System;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 宇电 AI-8 多回路温控器（一台一实例）。功能码 0x03 读 / 0x06 写单 / 0x10 写多。
/// 关键寄存器（通道 loop 从 1 起）：
///   SP  0x0000+(loop-1)  给定值
///   PV  0x0600+(loop-1)  测量值（只读，有符号，按小数位缩放）
///   SV  0x0480+(loop-1)  实际给定值（升降温斜率时为过程给定）
///   At  0x0300+(loop-1)  工作模式：0=APID 运行, 1=自整定, 2=ON/OFF, 3=手动, 4=停止
///   OP  0x0360+(loop-1)  输出值（25600=100%）
///   Srun 0x0845          一键全停=9655 / 全运行=0
/// 寄存器为整数，小数由 dPt 软件还原（如 150.0℃ 在 dPt=1 时寄存器=1500）。
/// </summary>
public sealed class TempController
{
    private readonly IModbusMaster _bus;
    private readonly byte _slave;
    private readonly int _loops;
    private readonly int _dpt;

    private const ushort SpBase = 0x0000;
    private const ushort PvBase = 0x0600;
    private const ushort SvBase = 0x0480;
    private const ushort AtBase = 0x0300;
    private const ushort OpBase = 0x0360;
    private const ushort PBase = 0x0060;    // 比例带 P（单位同 SP）
    private const ushort IBase = 0x00C0;    // 积分时间 I（寄存器单位 0.1s）
    private const ushort DBase = 0x0120;    // 微分时间 d（寄存器单位 0.01s，有符号）
    private const ushort SrunReg = 0x0845;

    private double Scale => Math.Pow(10, _dpt);

    /// <param name="slave">站地址（A=RV1-4, B=RV5-8）。</param>
    /// <param name="loops">本机回路数（4 路机为 4）。</param>
    /// <param name="dpt">小数位（与仪表 dPt 一致；1 表示寄存器=实际值×10）。</param>
    public TempController(IModbusMaster bus, byte slave, int loops = 4, int dpt = 1)
    {
        _bus = bus;
        _slave = slave;
        _loops = loops;
        _dpt = dpt;
    }

    public int Loops => _loops;

    /// <summary>读单通道测量值 PV（℃）。</summary>
    public async Task<double> ReadPvAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(PvBase + loop - 1), 1);
        return (short)r[0] / Scale;
    }

    /// <summary>一次性读全部回路 PV。</summary>
    public async Task<double[]> ReadAllPvAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, PvBase, (ushort)_loops);
        var pv = new double[_loops];
        for (int i = 0; i < _loops; i++) pv[i] = (short)r[i] / Scale;
        return pv;
    }

    /// <summary>写通道给定值 SP（℃）。</summary>
    public Task SetSetpointAsync(int loop, double sp)
        => _bus.WriteSingleRegisterAsync(_slave, (ushort)(SpBase + loop - 1), (ushort)(short)Math.Round(sp * Scale));

    /// <summary>读输出百分比（0..100）。</summary>
    public async Task<double> ReadOutputAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(OpBase + loop - 1), 1);
        return r[0] / 256.0;
    }

    /// <summary>通道进入 APID 自动控制。</summary>
    public Task StartAsync(int loop) => WriteAt(loop, 0);

    /// <summary>通道停止控制（关闭输出）。</summary>
    public Task StopAsync(int loop) => WriteAt(loop, 4);

    /// <summary>启动通道自整定（At=1）。整定结束后仪表会自动把 At 置回 0（APID）。</summary>
    public Task AutotuneAsync(int loop) => WriteAt(loop, 1);

    // ===================== PID 参数（每通道独立）=====================

    /// <summary>读比例带 P（工程量，单位同 SP）。</summary>
    public async Task<double> ReadPAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(PBase + loop - 1), 1);
        return (short)r[0] / Scale;
    }

    /// <summary>读积分时间 I（秒）。寄存器单位 0.1s。</summary>
    public async Task<double> ReadIAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(IBase + loop - 1), 1);
        return r[0] / 10.0;
    }

    /// <summary>读微分时间 D（秒）。寄存器单位 0.01s，有符号。</summary>
    public async Task<double> ReadDAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(DBase + loop - 1), 1);
        return (short)r[0] / 100.0;
    }

    /// <summary>一次性读通道 PID（P 工程量、I 秒、D 秒）。</summary>
    public async Task<PidParams> ReadPidAsync(int loop)
        => new(await ReadPAsync(loop), await ReadIAsync(loop), await ReadDAsync(loop));

    /// <summary>写比例带 P（工程量，单位同 SP）。</summary>
    public Task SetPAsync(int loop, double p)
        => _bus.WriteSingleRegisterAsync(_slave, (ushort)(PBase + loop - 1), (ushort)Math.Round(p * Scale));

    /// <summary>写积分时间 I（秒）。</summary>
    public Task SetIAsync(int loop, double seconds)
        => _bus.WriteSingleRegisterAsync(_slave, (ushort)(IBase + loop - 1), (ushort)Math.Round(seconds * 10));

    /// <summary>写微分时间 D（秒，可负）。</summary>
    public Task SetDAsync(int loop, double seconds)
        => _bus.WriteSingleRegisterAsync(_slave, (ushort)(DBase + loop - 1), (ushort)(short)Math.Round(seconds * 100));

    /// <summary>整组写 PID（P 工程量、I 秒、D 秒）。</summary>
    public async Task SetPidAsync(int loop, double p, double i, double d)
    {
        await SetPAsync(loop, p);
        await SetIAsync(loop, i);
        await SetDAsync(loop, d);
    }

    /// <summary>读通道工作模式 At（0=APID,1=自整定中,2=ONOFF,3=手动,4=停止,5=PV变送）。</summary>
    public async Task<int> ReadModeAsync(int loop)
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, (ushort)(AtBase + loop - 1), 1);
        return r[0];
    }

    /// <summary>该通道是否正在自整定（At=1）。整定完成后仪表自动把 At 置回 0。</summary>
    public async Task<bool> IsAutotuningAsync(int loop) => await ReadModeAsync(loop) == 1;

    /// <summary>一条指令停止本机全部通道（Srun=9655）。</summary>
    public Task StopAllAsync() => _bus.WriteSingleRegisterAsync(_slave, SrunReg, 9655);

    /// <summary>一条指令使本机全部通道进入自动控制（Srun=0）。</summary>
    public Task RunAllAsync() => _bus.WriteSingleRegisterAsync(_slave, SrunReg, 0);

    private Task WriteAt(int loop, ushort mode)
        => _bus.WriteSingleRegisterAsync(_slave, (ushort)(AtBase + loop - 1), mode);
}

/// <summary>温控通道 PID 参数：P 为工程量（同 SP 单位），I/D 为秒。</summary>
public readonly record struct PidParams(double P, double I, double D);
