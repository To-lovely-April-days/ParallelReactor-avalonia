using System;
using System.IO.Ports;
using ParallelReactor.Hardware;

namespace ParallelReactor.Service;

/// <summary>
/// 硬件连接配置（搅拌驱动器所在串口）。决定业务跑在「真串口」还是「内存模拟」上。
/// <para>
/// 连真机的两种方式（任选其一）：<br/>
/// 1) 设环境变量 <c>PR_STIR_PORT</c>（如 Windows 的 <c>COM3</c> 或 Linux 的 <c>/dev/ttyUSB2</c>），
///    可选再设 <c>PR_STIR_BAUD</c> / <c>PR_STIR_SLAVE</c>；<br/>
/// 2) 直接改下面的默认值并重新编译。<br/>
/// 端口为空 → 用 <see cref="MockModbusMaster"/> 演示，不连真机。
/// </para>
/// </summary>
public static class HardwareOptions
{
    /// <summary>搅拌驱动器（雷赛 DM2C）串口名。为空则用内存模拟。</summary>
    public static string? StirPort { get; set; } = Env("PR_STIR_PORT");

    /// <summary>波特率（DM2C 出厂常见 9600/38400，以现场拨码/参数为准）。</summary>
    public static int StirBaud { get; set; } = EnvInt("PR_STIR_BAUD", 9600);

    /// <summary>DM2C 从站地址（RS485 节点号，以拨码为准）。</summary>
    public static byte StirSlave { get; set; } = (byte)EnvInt("PR_STIR_SLAVE", 1);

    public static Parity StirParity { get; set; } = Parity.None;
    public static int StirDataBits { get; set; } = 8;
    public static StopBits StirStopBits { get; set; } = StopBits.One;

    /// <summary>是否已配置真机端口。</summary>
    public static bool UseRealStir => !string.IsNullOrWhiteSpace(StirPort);

    /// <summary>按当前配置创建搅拌所在总线的 Modbus 主站（真串口或内存模拟）。</summary>
    public static IModbusMaster CreateStirBus()
        => UseRealStir
            ? new ModbusRtuMaster(StirPort!, StirBaud, StirParity, StirDataBits, StirStopBits)
            : new MockModbusMaster();

    private static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static int EnvInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
