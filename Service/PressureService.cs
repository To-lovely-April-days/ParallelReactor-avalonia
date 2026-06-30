using System.Threading.Tasks;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 压力业务服务：艾莫迅 8AI 采集 8 路压力变送器（PT），按信号类型/量程换算为工程量（psi）。
/// 通道 i(0..7) 对应反应釜 RV(i+1)。轮询后由 MainViewModel 写回各釜的 P。
/// 总线由 <see cref="HardwareOptions.CreateAnalogBus"/> 决定（真串口或内存模拟）。
/// </summary>
public sealed class PressureService
{
    private readonly AnalogModule _ai;
    private readonly double _engHi;
    private readonly bool _is4to20;

    /// <summary>最近一次读到的 8 路压力（psi）。</summary>
    public double[] Pressures { get; } = new double[8];

    public PressureService(IModbusMaster bus, byte slave, double fullScale, bool is4to20)
    {
        _ai = new AnalogModule(bus, slave);
        _engHi = fullScale;
        _is4to20 = is4to20;
    }

    /// <summary>读 8 路压力并换算为 psi。通讯异常时保留上次值。</summary>
    public async Task<bool> PollAsync()
    {
        try
        {
            var v = await _ai.ReadScaledAsync(0, _engHi, _is4to20);
            for (int i = 0; i < Pressures.Length && i < v.Length; i++) Pressures[i] = v[i];
            return true;
        }
        catch { return false; }
    }
}
