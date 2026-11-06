using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 调试用 Modbus 追踪装饰器：把总线上每一笔事务（读/写、从站、地址、数据、耗时、异常）
/// 追加写入日志文件。用于排查"软件是否在运行中对设备下发过指令"——
/// 若电机转速波动的时间窗内日志里没有任何【写】，即可 100% 排除软件原因。
/// 由 PR_STIR_TRACE=1 启用（见 <see cref="Services.HardwareOptions.CreateStirBus"/>），默认关闭零开销。
/// </summary>
public sealed class TracingModbusMaster : IUrgentModbusMaster
{
    private readonly IModbusMaster _inner;
    private readonly string _path;
    private readonly object _lock;
    private readonly string _tag;
    private TracingModbusMaster? _urgent;

    public TracingModbusMaster(IModbusMaster inner, string path)
    {
        _inner = inner;
        _path = path;
        _lock = new object();
        _tag = "";
        Log($"=== 追踪开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    // 紧急视图的追踪：同一日志文件、同一把锁，行首加【急】
    private TracingModbusMaster(IModbusMaster inner, string path, object sharedLock)
    {
        _inner = inner;
        _path = path;
        _lock = sharedLock;
        _tag = "【急】";
    }

    /// <summary>紧急视图（转发到被包装总线的紧急视图，事务同样记日志）。</summary>
    public IModbusMaster Urgent => _tag.Length > 0 ? this
        : _urgent ??= new TracingModbusMaster(_inner.AsUrgent(), _path, _lock);

    private void Log(string line)
    {
        try { lock (_lock) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {_tag}{line}{Environment.NewLine}"); }
        catch { /* 日志失败不影响业务 */ }
    }

    private async Task<T> Trace<T>(string op, Func<Task<T>> run)
    {
        long t0 = Environment.TickCount64;
        try
        {
            var r = await run().ConfigureAwait(false);
            Log($"{op} · {Environment.TickCount64 - t0}ms");
            return r;
        }
        catch (Exception ex)
        {
            Log($"{op} · 异常 {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static string Hex(ushort v) => $"0x{v:X4}";

    public Task<bool[]> ReadCoilsAsync(byte s, ushort a, ushort n, CancellationToken ct = default)
        => Trace($"读线圈 从站{s} {Hex(a)}×{n}", () => _inner.ReadCoilsAsync(s, a, n, ct));

    public Task<bool[]> ReadDiscreteInputsAsync(byte s, ushort a, ushort n, CancellationToken ct = default)
        => Trace($"读离散输入 从站{s} {Hex(a)}×{n}", () => _inner.ReadDiscreteInputsAsync(s, a, n, ct));

    public Task<ushort[]> ReadHoldingRegistersAsync(byte s, ushort a, ushort n, CancellationToken ct = default)
        => Trace($"读寄存器 从站{s} {Hex(a)}×{n}", () => _inner.ReadHoldingRegistersAsync(s, a, n, ct));

    public Task<ushort[]> ReadInputRegistersAsync(byte s, ushort a, ushort n, CancellationToken ct = default)
        => Trace($"读输入寄存器 从站{s} {Hex(a)}×{n}", () => _inner.ReadInputRegistersAsync(s, a, n, ct));

    public Task WriteSingleCoilAsync(byte s, ushort a, bool on, CancellationToken ct = default)
        => Trace($"【写】线圈 从站{s} {Hex(a)} = {(on ? "ON" : "OFF")}",
            async () => { await _inner.WriteSingleCoilAsync(s, a, on, ct).ConfigureAwait(false); return 0; });

    public Task WriteSingleRegisterAsync(byte s, ushort a, ushort v, CancellationToken ct = default)
        => Trace($"【写】寄存器 从站{s} {Hex(a)} = {Hex(v)}",
            async () => { await _inner.WriteSingleRegisterAsync(s, a, v, ct).ConfigureAwait(false); return 0; });

    public Task WriteMultipleCoilsAsync(byte s, ushort a, bool[] vs, CancellationToken ct = default)
        => Trace($"【写】多线圈 从站{s} {Hex(a)}×{vs.Length}",
            async () => { await _inner.WriteMultipleCoilsAsync(s, a, vs, ct).ConfigureAwait(false); return 0; });

    public Task WriteMultipleRegistersAsync(byte s, ushort a, ushort[] vs, CancellationToken ct = default)
        => Trace($"【写】多寄存器 从站{s} {Hex(a)}×{vs.Length} = [{string.Join(",", vs.Select(Hex))}]",
            async () => { await _inner.WriteMultipleRegistersAsync(s, a, vs, ct).ConfigureAwait(false); return 0; });
}
