using System;
using System.IO.Ports;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

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

    // ===== 温控（宇电 AI-8 ×2，A 站 RV1-4、B 站 RV5-8，同一条 RS485 总线）=====

    /// <summary>温控总线串口名（两台 AI-8 共用）。为空则用内存模拟。</summary>
    public static string? TempPort { get; set; } = Env("PR_TEMP_PORT");

    /// <summary>波特率（AI-8 出厂默认 19.2K）。</summary>
    public static int TempBaud { get; set; } = EnvInt("PR_TEMP_BAUD", 19200);

    /// <summary>A 站地址（管 RV1-4），出厂默认 1。</summary>
    public static byte TempSlaveA { get; set; } = (byte)EnvInt("PR_TEMP_SLAVE_A", 1);

    /// <summary>B 站地址（管 RV5-8）。</summary>
    public static byte TempSlaveB { get; set; } = (byte)EnvInt("PR_TEMP_SLAVE_B", 2);

    /// <summary>小数位 dPt（寄存器=实际值×10^dPt；默认 1 表示一位小数）。</summary>
    public static int TempDpt { get; set; } = EnvInt("PR_TEMP_DPT", 1);

    public static Parity TempParity { get; set; } = Parity.None;

    public static bool UseRealTemp => !string.IsNullOrWhiteSpace(TempPort);

    /// <summary>创建温控总线主站。mock 模式下预置两站地址，使 PV 能向 SP 漂移。</summary>
    public static IModbusMaster CreateTempBus()
        => UseRealTemp
            ? new ModbusRtuMaster(TempPort!, TempBaud, TempParity, 8, StopBits.One)
            : new MockModbusMaster { TempSlaves = new[] { TempSlaveA, TempSlaveB }, TempLoops = 4 };

    private static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static int EnvInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
