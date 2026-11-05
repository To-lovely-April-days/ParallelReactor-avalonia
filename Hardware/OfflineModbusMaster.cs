using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 未配置串口时使用的主站：每笔事务都以 <see cref="IOException"/> 失败，不产生任何数据。
/// 上层统一按「通讯失败」处理（读数显示「—」、模块显示离线、自动序列拒绝启动），界面不会出现虚假读数。
/// </summary>
public sealed class OfflineModbusMaster : IModbusMaster
{
    private readonly string _what;

    /// <param name="what">模块说明，如「8AI 压力采集（PR_PRESS_PORT）」，用于错误信息。</param>
    public OfflineModbusMaster(string what) => _what = what;

    private IOException Err() => new($"{_what}未配置串口");
    private Task Fail() => Task.FromException(Err());
    private Task<T> Fail<T>() => Task.FromException<T>(Err());

    public Task<bool[]> ReadCoilsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default) => Fail<bool[]>();
    public Task<bool[]> ReadDiscreteInputsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default) => Fail<bool[]>();
    public Task<ushort[]> ReadHoldingRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default) => Fail<ushort[]>();
    public Task<ushort[]> ReadInputRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default) => Fail<ushort[]>();
    public Task WriteSingleCoilAsync(byte slave, ushort addr, bool on, CancellationToken ct = default) => Fail();
    public Task WriteSingleRegisterAsync(byte slave, ushort addr, ushort value, CancellationToken ct = default) => Fail();
    public Task WriteMultipleCoilsAsync(byte slave, ushort start, bool[] values, CancellationToken ct = default) => Fail();
    public Task WriteMultipleRegistersAsync(byte slave, ushort start, ushort[] values, CancellationToken ct = default) => Fail();
}
