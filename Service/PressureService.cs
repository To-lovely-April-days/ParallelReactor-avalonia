using System;
using System.Threading;
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

    /// <summary>最近一次成功读到的一帧：8 路原码 + 换算后的 psi（同一次读取，整体按引用替换，读者拿到的总是一致的快照）。</summary>
    public sealed record Sample(ushort[] Raw, double[] Psi, DateTimeOffset At);

    private volatile Sample _last = new(new ushort[8], new double[8], DateTimeOffset.MinValue);

    /// <summary>最近一帧（尚未读到时原码 / 压力全 0、At=MinValue）。</summary>
    public Sample Last => _last;

    /// <summary>最近一次读到的 8 路压力（psi）。</summary>
    public double[] Pressures => _last.Psi;

    /// <summary>最近一次读到的 8 路原码（0..16000）。</summary>
    public ushort[] Raw => _last.Raw;

    /// <summary>读取统计（Tick 轮询与泄漏测试 / 序列的即时读取合并计数）。</summary>
    public CommStats Stats { get; } = new();

    private int _fails = 3;   // 连续读失败次数（初值=离线，首次读成功才算在线）
    private readonly SemaphoreSlim _gate = new(1, 1);   // 同一时间只发一轮读取（Tick 轮询与序列即时读可能重叠）

    /// <summary>8AI 是否在线：连续 3 次读失败视为离线（未配置串口时恒为离线）。离线时界面显示「—」。</summary>
    public bool Online => _fails < 3;

    public PressureService(IModbusMaster bus, byte slave, double fullScale, bool is4to20)
    {
        _ai = new AnalogModule(bus, slave);
        FullScalePsi = fullScale;
        _is4to20 = is4to20;
    }

    /// <summary>
    /// 读 8 路压力并换算为 psi。通讯异常时保留上次值。
    /// skipIfBusy=true（Tick 轮询用）：上一轮还没读完就直接跳过，返回 false 且不计失败；
    /// 否则排队等上一轮结束再读（序列的即时读取必须拿到新数据）。
    /// </summary>
    public async Task<bool> PollAsync(bool skipIfBusy = false)
    {
        if (skipIfBusy) { if (!_gate.Wait(0)) return false; }
        else await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var raw = await _ai.ReadRawAsync().ConfigureAwait(false);
            var psi = new double[8];
            var r8 = new ushort[8];
            for (int i = 0; i < 8 && i < raw.Length; i++)
            {
                r8[i] = raw[i];
                psi[i] = AnalogModule.Scale(raw[i], 0, FullScalePsi, _is4to20);
            }
            var now = DateTimeOffset.Now;
            _last = new Sample(r8, psi, now);
            _fails = 0;
            Stats.Ok(now);
            return true;
        }
        catch
        {
            Stats.Fail();
            if (_fails < 3) _fails++;
            return false;
        }
        finally { _gate.Release(); }
    }
}
