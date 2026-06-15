using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 艾莫迅 JY-MODBUS-8AI：8 路模拟量输入（0x04 读输入寄存器，地址 0x00..0x07）。
/// 原码范围 0..16000：0-10V/0-20mA 对应 0..16000；4-20mA 对应 3200..16000。
/// 在本系统中用于采集 8 路压力变送器(PT)。
/// </summary>
public sealed class AnalogModule
{
    private readonly IModbusMaster _bus;
    private readonly byte _slave;

    public AnalogModule(IModbusMaster bus, byte slave)
    {
        _bus = bus;
        _slave = slave;
    }

    /// <summary>读 8 路原码（0..16000）。</summary>
    public Task<ushort[]> ReadRawAsync(int count = 8) => _bus.ReadInputRegistersAsync(_slave, 0x0000, (ushort)count);

    /// <summary>原码线性换算为工程量。is4to20=true 时起点为 3200(4mA)，否则为 0。</summary>
    public static double Scale(ushort raw, double engLo, double engHi, bool is4to20)
    {
        double rawLo = is4to20 ? 3200.0 : 0.0;
        const double rawHi = 16000.0;
        double t = (raw - rawLo) / (rawHi - rawLo);
        if (t < 0) t = 0;
        return engLo + t * (engHi - engLo);
    }

    /// <summary>读全部 8 路并换算为工程量（统一量程/信号类型）。</summary>
    public async Task<double[]> ReadScaledAsync(double engLo, double engHi, bool is4to20)
    {
        var raw = await ReadRawAsync();
        var v = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++) v[i] = Scale(raw[i], engLo, engHi, is4to20);
        return v;
    }
}
