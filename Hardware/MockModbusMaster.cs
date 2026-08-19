using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 无硬件时的内存模拟主站：保存写入值、返回存储值，并可通过 <see cref="SimulateStep"/> 让数据"活"起来
/// （温度 PV 向 SP 漂移、压力轻微抖动）。用于在没有真实设备时运行与演示。线程安全（单锁）。
/// </summary>
public sealed class MockModbusMaster : IModbusMaster
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ushort> _hold = new();
    private readonly Dictionary<int, ushort> _input = new();
    private readonly Dictionary<int, bool> _coil = new();
    private readonly Dictionary<int, bool> _di = new();
    private readonly Dictionary<int, int> _tuneCount = new();   // 自整定模拟计时
    private readonly Random _rng = new();
    private bool _seeded;

    /// <summary>AI-8 温控站地址（用于 PV 模拟），每台回路数见 <see cref="TempLoops"/>。</summary>
    public byte[] TempSlaves { get; init; } = Array.Empty<byte>();
    public int TempLoops { get; init; } = 4;

    /// <summary>8AI 模拟量站地址（用于压力模拟），0 表示不模拟。</summary>
    public byte AnalogSlave { get; init; }

    private static int Key(byte s, ushort a) => (s << 16) | a;

    /// <summary>每个轮询周期调用一次，推进模拟值。</summary>
    public void SimulateStep()
    {
        lock (_lock)
        {
            if (!_seeded) { SeedTemp(); _seeded = true; }

            foreach (var s in TempSlaves)
                for (int i = 0; i < TempLoops; i++)
                {
                    // PV 向 SP 漂移
                    short sp = (short)_hold.GetValueOrDefault(Key(s, (ushort)(0x0000 + i)), (ushort)250); // 默认 25.0℃
                    int pvKey = Key(s, (ushort)(0x0600 + i));
                    short pv = (short)_hold.GetValueOrDefault(pvKey, (ushort)sp);
                    int step = Math.Sign(sp - pv) * Math.Min(Math.Abs(sp - pv), 5);
                    pv = (short)(pv + step + _rng.Next(-1, 2));
                    _hold[pvKey] = (ushort)pv;

                    // 自整定模拟：At=1 持续若干周期后回 0，并写入一组 PID 结果
                    int atKey = Key(s, (ushort)(0x0300 + i));
                    if (_hold.GetValueOrDefault(atKey) == 1)
                    {
                        int n = _tuneCount.GetValueOrDefault(atKey) + 1;
                        _tuneCount[atKey] = n;
                        if (n >= 6)   // ≈ 7 秒（按 1.2s tick）
                        {
                            _tuneCount[atKey] = 0;
                            _hold[atKey] = 0;   // 整定结束，回 APID
                            _hold[Key(s, (ushort)(0x0060 + i))] = (ushort)(700 + _rng.Next(0, 200));    // P
                            _hold[Key(s, (ushort)(0x00C0 + i))] = (ushort)(1000 + _rng.Next(0, 400));   // I（0.1s）
                            _hold[Key(s, (ushort)(0x0120 + i))] = (ushort)(1500 + _rng.Next(0, 800));   // D（0.01s）
                        }
                    }
                }

            if (AnalogSlave != 0)
                for (int i = 0; i < 8; i++)
                {
                    int k = Key(AnalogSlave, (ushort)i);
                    int v = _input.GetValueOrDefault(k, (ushort)8000) + _rng.Next(-30, 31);
                    _input[k] = (ushort)Math.Clamp(v, 3200, 16000);
                }
        }
    }

    /// <summary>给温控站预置一组默认 PID，使界面初始就有合理数值（mock 演示用）。</summary>
    private void SeedTemp()
    {
        foreach (var s in TempSlaves)
        {
            for (int i = 0; i < TempLoops; i++)
            {
                _hold.TryAdd(Key(s, (ushort)(0x0060 + i)), 600);    // P = 60.0
                _hold.TryAdd(Key(s, (ushort)(0x00C0 + i)), 1200);   // I = 120.0s
                _hold.TryAdd(Key(s, (ushort)(0x0120 + i)), 2000);   // D = 20.0s
            }
            _hold.TryAdd(Key(s, 0x0844), (ushort)TempLoops);        // Ctn 启用回路数
            _hold.TryAdd(Key(s, 0x0845), 0);                        // Srun 运行
        }
    }

    public Task<bool[]> ReadCoilsAsync(byte s, ushort start, ushort count, CancellationToken ct = default)
        => ReadBits(_coil, s, start, count);

    public Task<bool[]> ReadDiscreteInputsAsync(byte s, ushort start, ushort count, CancellationToken ct = default)
        => ReadBits(_di, s, start, count);

    public Task<ushort[]> ReadHoldingRegistersAsync(byte s, ushort start, ushort count, CancellationToken ct = default)
        => ReadRegs(_hold, s, start, count);

    public Task<ushort[]> ReadInputRegistersAsync(byte s, ushort start, ushort count, CancellationToken ct = default)
        => ReadRegs(_input, s, start, count);

    public Task WriteSingleCoilAsync(byte s, ushort a, bool on, CancellationToken ct = default)
    { lock (_lock) { _coil[Key(s, a)] = on; } return Task.CompletedTask; }

    public Task WriteSingleRegisterAsync(byte s, ushort a, ushort v, CancellationToken ct = default)
    { lock (_lock) { _hold[Key(s, a)] = v; } return Task.CompletedTask; }

    public Task WriteMultipleCoilsAsync(byte s, ushort start, bool[] values, CancellationToken ct = default)
    { lock (_lock) { for (int i = 0; i < values.Length; i++) _coil[Key(s, (ushort)(start + i))] = values[i]; } return Task.CompletedTask; }

    public Task WriteMultipleRegistersAsync(byte s, ushort start, ushort[] values, CancellationToken ct = default)
    { lock (_lock) { for (int i = 0; i < values.Length; i++) _hold[Key(s, (ushort)(start + i))] = values[i]; } return Task.CompletedTask; }

    private Task<bool[]> ReadBits(Dictionary<int, bool> map, byte s, ushort start, ushort count)
    {
        lock (_lock)
        {
            var r = new bool[count];
            for (int i = 0; i < count; i++) r[i] = map.GetValueOrDefault(Key(s, (ushort)(start + i)));
            return Task.FromResult(r);
        }
    }

    private Task<ushort[]> ReadRegs(Dictionary<int, ushort> map, byte s, ushort start, ushort count)
    {
        lock (_lock)
        {
            var r = new ushort[count];
            for (int i = 0; i < count; i++) r[i] = map.GetValueOrDefault(Key(s, (ushort)(start + i)));
            return Task.FromResult(r);
        }
    }
}
