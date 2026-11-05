using System.Threading.Tasks;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 压力业务服务：艾莫迅 8AI 采集 8 路压力变送器（PT），按信号类型/量程换算为工程量（psi）。
/// 通道 i(0..7) 对应反应釜 RV(i+1)。轮询后由 MainViewModel 写回各釜的 P。
/// 总线由 <see cref="HardwareOptions.CreateAnalogBus"/> 决定（真串口或离线）。
/// </summary>
public sealed class PressureService
{
    private readonly AnalogModule _ai;
    private readonly bool _is4to20;

    /// <summary>变送器满量程（工程量 psi，原码满程对应该值）。可运行时修改（设置页改量程后即时生效）。</summary>
    public double FullScalePsi { get; set; }

    /// <summary>最近一次读到的 8 路压力（psi）。</summary>
    public double[] Pressures { get; } = new double[8];

    private int _fails = 3;   // 连续读失败次数（初值=离线，首次读成功才算在线）

    /// <summary>8AI 是否在线：连续 3 次读失败视为离线（未配置串口时恒为离线）。离线时界面显示「—」。</summary>
    public bool Online => _fails < 3;

    public PressureService(IModbusMaster bus, byte slave, double fullScale, bool is4to20)
    {
        _ai = new AnalogModule(bus, slave);
        FullScalePsi = fullScale;
        _is4to20 = is4to20;
    }

    /// <summary>读 8 路压力并换算为 psi。通讯异常时保留上次值。</summary>
    public async Task<bool> PollAsync()
    {
        try
        {
            var v = await _ai.ReadScaledAsync(0, FullScalePsi, _is4to20).ConfigureAwait(false);
            for (int i = 0; i < Pressures.Length && i < v.Length; i++) Pressures[i] = v[i];
            _fails = 0;
            return true;
        }
        catch
        {
            if (_fails < 3) _fails++;
            return false;
        }
    }
}
