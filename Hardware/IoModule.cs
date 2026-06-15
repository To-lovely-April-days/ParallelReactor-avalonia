using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 艾莫迅 JY-MODBUS-IO16R：16 路 DO 线圈 + 16 路 DI。
/// 线圈：0x01 读 / 0x05 写单(FF00 闭合, 0000 断开) / 0x0F 写多；离散输入：0x02。
/// 在本系统中线圈用于驱动电磁阀（进气阀 / 气源阀 / 排空阀），DI 读注射检测 / 限位等。
/// </summary>
public sealed class IoModule
{
    private readonly IModbusMaster _bus;
    private readonly byte _slave;

    public IoModule(IModbusMaster bus, byte slave)
    {
        _bus = bus;
        _slave = slave;
    }

    /// <summary>读全部 16 路数字输入（DI1..DI16）。</summary>
    public Task<bool[]> ReadInputsAsync() => _bus.ReadDiscreteInputsAsync(_slave, 0x0000, 16);

    /// <summary>读全部 16 路输出线圈当前状态。</summary>
    public Task<bool[]> ReadCoilsAsync() => _bus.ReadCoilsAsync(_slave, 0x0000, 16);

    /// <summary>设置单个线圈（index 0..15）。</summary>
    public Task SetCoilAsync(int index, bool on) => _bus.WriteSingleCoilAsync(_slave, (ushort)index, on);

    /// <summary>一次性写全部 16 路线圈。</summary>
    public Task SetAllCoilsAsync(bool[] coils16) => _bus.WriteMultipleCoilsAsync(_slave, 0x0000, coils16);
}
