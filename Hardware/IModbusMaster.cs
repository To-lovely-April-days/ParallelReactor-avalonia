using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// Modbus RTU 主站抽象。每条 RS485 总线对应一个实例（半双工，内部串行化事务）。
/// 真实实现走串口(ModbusRtuMaster)，无硬件时用 MockModbusMaster。
/// 地址均为 Modbus 协议地址（0 基），与各模块手册一致。
/// </summary>
public interface IModbusMaster
{
    /// <summary>0x01 读输出线圈。</summary>
    Task<bool[]> ReadCoilsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>0x02 读离散量输入。</summary>
    Task<bool[]> ReadDiscreteInputsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>0x03 读保持寄存器。</summary>
    Task<ushort[]> ReadHoldingRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>0x04 读输入寄存器。</summary>
    Task<ushort[]> ReadInputRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>0x05 写单个线圈。</summary>
    Task WriteSingleCoilAsync(byte slave, ushort addr, bool on, CancellationToken ct = default);

    /// <summary>0x06 写单个寄存器。</summary>
    Task WriteSingleRegisterAsync(byte slave, ushort addr, ushort value, CancellationToken ct = default);

    /// <summary>0x0F 写多个线圈。</summary>
    Task WriteMultipleCoilsAsync(byte slave, ushort start, bool[] values, CancellationToken ct = default);

    /// <summary>0x10 写多个寄存器。</summary>
    Task WriteMultipleRegistersAsync(byte slave, ushort start, ushort[] values, CancellationToken ct = default);
}
