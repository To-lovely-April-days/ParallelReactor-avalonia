using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 走真实串口（RS485）的 Modbus RTU 主站。实现 <see cref="IModbusMaster"/> 的 8 个功能码。
/// <para>
/// 半双工：所有事务用一把信号量串行化（一问一答，收完响应再发下一帧）。串口惰性打开，
/// 断线后下次事务会自动重连。所有阻塞式串口 I/O 都放到线程池（Task.Run），不卡 UI。
/// </para>
/// 每条物理总线（一个串口）对应一个实例；同一总线上的多个从站共用这一个实例。
/// </summary>
public sealed class ModbusRtuMaster : IModbusMaster, IDisposable
{
    private readonly string _portName;
    private readonly int _baud;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialPort? _sp;

    public ModbusRtuMaster(string portName, int baud = 9600, Parity parity = Parity.None,
        int dataBits = 8, StopBits stopBits = StopBits.One, int timeoutMs = 300)
    {
        _portName = portName;
        _baud = baud;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
        _timeoutMs = timeoutMs;
    }

    public string PortName => _portName;

    private SerialPort EnsureOpen()
    {
        var sp = _sp;
        if (sp is { IsOpen: true }) return sp;

        sp?.Dispose();
        sp = new SerialPort(_portName, _baud, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = _timeoutMs,
            WriteTimeout = _timeoutMs,
        };
        sp.Open();
        sp.DiscardInBuffer();
        sp.DiscardOutBuffer();
        _sp = sp;
        return sp;
    }

    // ===================== 读 =====================

    public Task<bool[]> ReadCoilsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default)
        => Task.Run(() => ReadBits(slave, 0x01, start, count), ct);

    public Task<bool[]> ReadDiscreteInputsAsync(byte slave, ushort start, ushort count, CancellationToken ct = default)
        => Task.Run(() => ReadBits(slave, 0x02, start, count), ct);

    public Task<ushort[]> ReadHoldingRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default)
        => Task.Run(() => ReadRegs(slave, 0x03, start, count), ct);

    public Task<ushort[]> ReadInputRegistersAsync(byte slave, ushort start, ushort count, CancellationToken ct = default)
        => Task.Run(() => ReadRegs(slave, 0x04, start, count), ct);

    private bool[] ReadBits(byte slave, byte fn, ushort start, ushort count)
    {
        int nbytes = (count + 7) / 8;
        var resp = Txn(new[] { slave, fn, Hi(start), Lo(start), Hi(count), Lo(count) }, 3 + nbytes + 2);
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (resp[3 + i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    private ushort[] ReadRegs(byte slave, byte fn, ushort start, ushort count)
    {
        var resp = Txn(new[] { slave, fn, Hi(start), Lo(start), Hi(count), Lo(count) }, 3 + count * 2 + 2);
        var regs = new ushort[count];
        for (int i = 0; i < count; i++)
            regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[3 + i * 2 + 1]);
        return regs;
    }

    // ===================== 写 =====================

    public Task WriteSingleCoilAsync(byte slave, ushort addr, bool on, CancellationToken ct = default)
        => Task.Run(() => Txn(new byte[] { slave, 0x05, Hi(addr), Lo(addr), (byte)(on ? 0xFF : 0x00), 0x00 }, 8), ct);

    public Task WriteSingleRegisterAsync(byte slave, ushort addr, ushort value, CancellationToken ct = default)
        => Task.Run(() => Txn(new[] { slave, (byte)0x06, Hi(addr), Lo(addr), Hi(value), Lo(value) }, 8), ct);

    public Task WriteMultipleCoilsAsync(byte slave, ushort start, bool[] values, CancellationToken ct = default)
        => Task.Run(() =>
        {
            int n = values.Length;
            int nbytes = (n + 7) / 8;
            var pdu = new byte[7 + nbytes];
            pdu[0] = slave; pdu[1] = 0x0F;
            pdu[2] = Hi(start); pdu[3] = Lo(start);
            pdu[4] = Hi((ushort)n); pdu[5] = Lo((ushort)n);
            pdu[6] = (byte)nbytes;
            for (int i = 0; i < n; i++)
                if (values[i]) pdu[7 + i / 8] |= (byte)(1 << (i % 8));
            Txn(pdu, 8);
        }, ct);

    public Task WriteMultipleRegistersAsync(byte slave, ushort start, ushort[] values, CancellationToken ct = default)
        => Task.Run(() =>
        {
            int n = values.Length;
            var pdu = new byte[7 + n * 2];
            pdu[0] = slave; pdu[1] = 0x10;
            pdu[2] = Hi(start); pdu[3] = Lo(start);
            pdu[4] = Hi((ushort)n); pdu[5] = Lo((ushort)n);
            pdu[6] = (byte)(n * 2);
            for (int i = 0; i < n; i++) { pdu[7 + i * 2] = Hi(values[i]); pdu[8 + i * 2] = Lo(values[i]); }
            Txn(pdu, 8);
        }, ct);

    // ===================== 事务 =====================

    /// <summary>发送一帧并读取完整响应（含 CRC 校验、异常码判定、地址比对）。<paramref name="expectedLen"/> 为正常响应总字节数。</summary>
    private byte[] Txn(byte[] pdu, int expectedLen)
    {
        _gate.Wait();
        try
        {
            var sp = EnsureOpen();
            var frame = ModbusCrc.Append(pdu);
            sp.DiscardInBuffer();
            sp.Write(frame, 0, frame.Length);

            // 先读 地址 + 功能码，借此判断是否为异常响应
            var head = ReadExact(sp, 2);
            byte addr = head[0], fn = head[1];

            if ((fn & 0x80) != 0)
            {
                var tail = ReadExact(sp, 3); // 异常码(1) + CRC(2)
                throw new ModbusException(addr, (byte)(fn & 0x7F), tail[0]);
            }

            var rest = ReadExact(sp, expectedLen - 2);
            var resp = new byte[expectedLen];
            resp[0] = addr; resp[1] = fn;
            Array.Copy(rest, 0, resp, 2, rest.Length);

            if (addr != pdu[0])
                throw new IOException($"Modbus 从站地址不匹配：期望 {pdu[0]}，收到 {addr}");
            if (!ModbusCrc.Validate(resp, resp.Length))
                throw new IOException("Modbus CRC 校验失败");
            return resp;
        }
        catch
        {
            // 出错时关闭串口，下次事务重连（清掉可能错位的半截帧）
            try { _sp?.Close(); } catch { }
            _sp = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] ReadExact(SerialPort sp, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = sp.Read(buf, got, n - got); // 超时抛 TimeoutException
            if (r <= 0) break;
            got += r;
        }
        if (got < n) throw new TimeoutException($"Modbus 响应不足：期望 {n} 字节，实收 {got}");
        return buf;
    }

    private static byte Hi(ushort v) => (byte)(v >> 8);
    private static byte Lo(ushort v) => (byte)(v & 0xFF);

    public void Dispose()
    {
        try { _sp?.Dispose(); } catch { }
        _gate.Dispose();
    }
}

/// <summary>Modbus 从站返回的异常响应（功能码最高位置 1）。</summary>
public sealed class ModbusException : Exception
{
    public byte Slave { get; }
    public byte Function { get; }
    public byte Code { get; }

    public ModbusException(byte slave, byte function, byte code)
        : base($"Modbus 异常 0x{code:X2}（{Describe(code)}）· 从站 {slave} 功能 0x{function:X2}")
    {
        Slave = slave;
        Function = function;
        Code = code;
    }

    private static string Describe(byte code) => code switch
    {
        0x01 => "非法功能码",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认（处理中）",
        0x06 => "从站忙",
        _ => "未知异常",
    };
}
