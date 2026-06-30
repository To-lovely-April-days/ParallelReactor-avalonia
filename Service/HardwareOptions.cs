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
    public static int StirBaud { get; set; } = EnvInt("PR_STIR_BAUD", 38400);

    /// <summary>DM2C 从站地址（RS485 节点号，以拨码为准）。</summary>
    public static byte StirSlave { get; set; } = (byte)EnvInt("PR_STIR_SLAVE", 1);

    public static Parity StirParity { get; set; } = Parity.None;
    public static int StirDataBits { get; set; } = 8;
    public static StopBits StirStopBits { get; set; } = StopBits.One;

    /// <summary>是否已配置真机端口。</summary>
    public static bool UseRealStir => !string.IsNullOrWhiteSpace(StirPort);

    /// <summary>是否配置了任一真机串口（决定开机是否进入"真机初始态"而非演示数据）。</summary>
    public static bool AnyReal => UseRealStir || UseRealTemp || UseRealAnalog || UseRealIo;

    /// <summary>按当前配置创建搅拌所在总线的 Modbus 主站（真串口或内存模拟）。</summary>
    public static IModbusMaster CreateStirBus()
        => UseRealStir
            ? new ModbusRtuMaster(StirPort!, StirBaud, StirParity, StirDataBits, StirStopBits)
            : new MockModbusMaster();

    // ===== 温控（宇电 AI-8 ×2，A 站 RV1-4、B 站 RV5-8，同一条 RS485 总线）=====

    /// <summary>温控总线串口名（两台 AI-8 共用）。为空则用内存模拟。</summary>
    public static string? TempPort { get; set; } = Env("PR_TEMP_PORT");

    /// <summary>波特率（现场 AI-8 为 9600，8-N-1）。</summary>
    public static int TempBaud { get; set; } = EnvInt("PR_TEMP_BAUD", 19200);

    /// <summary>AI-8 站地址（1 拖 8，一台管 RV1-8），出厂默认 1。</summary>
    public static byte TempSlave { get; set; } = (byte)EnvInt("PR_TEMP_SLAVE", 1);

    /// <summary>小数位 dPt（寄存器=实际值×10^dPt；默认 1 表示一位小数）。</summary>
    public static int TempDpt { get; set; } = EnvInt("PR_TEMP_DPT", 1);

    public static Parity TempParity { get; set; } = Parity.None;

    public static bool UseRealTemp => !string.IsNullOrWhiteSpace(TempPort);

    /// <summary>创建温控总线主站。mock 模式下预置该站 8 路，使 PV 能向 SP 漂移。</summary>
    public static IModbusMaster CreateTempBus()
        => UseRealTemp
            ? new ModbusRtuMaster(TempPort!, TempBaud, TempParity, 8, StopBits.One)
            : new MockModbusMaster { TempSlaves = new[] { TempSlave }, TempLoops = 8 };

    // ===== 压力（艾莫迅 JY-MODBUS-8AI，8 路压力变送器）=====

    /// <summary>8AI 串口名（/dev/ttyS6）。为空则用内存模拟。</summary>
    public static string? AnalogPort { get; set; } = Env("PR_PRESS_PORT");

    /// <summary>波特率（现场 9600，8-N-1）。</summary>
    public static int AnalogBaud { get; set; } = EnvInt("PR_PRESS_BAUD", 9600);

    /// <summary>8AI 站地址（默认 1）。</summary>
    public static byte AnalogSlave { get; set; } = (byte)EnvInt("PR_PRESS_SLAVE", 1);

    /// <summary>压力变送器是否 4-20mA（true=4-20mA，原码起点 3200；false=0-10V/0-20mA，起点 0）。</summary>
    public static bool Press4to20 { get; set; } = (Env("PR_PRESS_SIGNAL") ?? "4-20") != "0-10";

    /// <summary>压力满量程（工程量，单位 psi；原码 16000 对应该值）。</summary>
    public static double PressFullScale { get; set; } = EnvDouble("PR_PRESS_FULLSCALE", 600);

    public static bool UseRealAnalog => !string.IsNullOrWhiteSpace(AnalogPort);

    /// <summary>创建压力总线主站。mock 模式下让 8 路输入抖动。</summary>
    public static IModbusMaster CreateAnalogBus()
        => UseRealAnalog
            ? new ModbusRtuMaster(AnalogPort!, AnalogBaud, Parity.None, 8, StopBits.One)
            : new MockModbusMaster { AnalogSlave = AnalogSlave };

    // ===== 阀门（艾莫迅 JY-MODBUS-IO16R，16 路线圈）=====
    // 线圈映射：0-7=RV1-8 进气阀，8=惰性气体，9=气体A，10=气体B，11=排空/Vent。

    /// <summary>IO16R 串口名（/dev/ttyS8）。为空则用内存模拟。</summary>
    public static string? IoPort { get; set; } = Env("PR_IO_PORT");

    /// <summary>波特率（现场 9600，8-N-1）。</summary>
    public static int IoBaud { get; set; } = EnvInt("PR_IO_BAUD", 9600);

    /// <summary>IO16R 站地址（默认 1）。</summary>
    public static byte IoSlave { get; set; } = (byte)EnvInt("PR_IO_SLAVE", 1);

    /// <summary>线圈通电时阀门是「开」还是「关」。默认通电=开；若为常开阀（通电关）设 PR_IO_ENERGIZE=close。</summary>
    public static bool IoEnergizeOpens { get; set; } = (Env("PR_IO_ENERGIZE") ?? "open") != "close";

    public static bool UseRealIo => !string.IsNullOrWhiteSpace(IoPort);

    /// <summary>创建阀门总线主站。</summary>
    public static IModbusMaster CreateIoBus()
        => UseRealIo
            ? new ModbusRtuMaster(IoPort!, IoBaud, Parity.None, 8, StopBits.One)
            : new MockModbusMaster();

    private static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static int EnvInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;

    private static double EnvDouble(string key, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
}
