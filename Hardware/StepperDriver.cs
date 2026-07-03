using System;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 雷赛 DM2C-RS432 驱控一体步进驱动器（全局搅拌电机）。功能码 0x03/0x06/0x10。
/// <para>
/// 连续搅拌用 <b>PR 路径0 速度模式</b>（推荐）：一次触发后持续运转、速度稳定，不需要保活。
/// 写 Pr9.00-9.07（0x6200 起 8 个寄存器）：Pr9.00=2(速度运行)、Pr9.03=转速 rpm、Pr9.04/9.05=加/减速、
/// Pr9.07=0x10 立即触发。停止：0x6002 写 0x0040（急停/减速停）。
/// </para>
/// 另有 JOG 保活方式（写 0x1801=0x4001，必须每 ≤50ms 重发一次），但在 9600 波特率下单帧往返≈25ms，
/// 抖动易越过 50ms 而降级为点动 → 「一会快一会慢」，故默认改用速度模式。
/// 状态 0x1003、报警 0x2203、保存 0x1801=0x2211、复位报警 0x1801=0x1111。参数为 32 位，实际只用低 16 位。
/// </summary>
public sealed class StepperDriver
{
    private readonly IModbusMaster _bus;
    private readonly byte _slave;

    private const ushort SpeedReg = 0x01E1;   // Pr6.00 JOG 速度 r/min
    private const ushort CtrlReg = 0x1801;    // 控制字
    private const ushort StatusReg = 0x1003;  // 运行状态
    private const ushort AlarmReg = 0x2203;   // 当前报警
    private const ushort PeakCurrentReg = 0x0191;  // Pr5.00 峰值电流，单位 0.1A，范围 0–32
    private const ushort StandbyPctReg = 0x01D3;   // Pr5.33 待机电流百分比，0–100
    private const ushort MicrostepReg = 0x0001;    // Pr0.00 指令脉冲数/转（细分），200–51200
    private const int MicrostepMin = 200;
    private const int MicrostepMax = 51200;

    // DM2C-RS432 输出电流硬限：0.3–3.2A（写入寄存器值 3–32，单位 0.1A）
    private const int PeakCurrentMinRaw = 3;
    private const int PeakCurrentMaxRaw = 32;

    private const ushort Pr0Base = 0x6200;    // Pr9.00 路径0 运动模式（起始，连续 8 个寄存器）
    private const ushort TrigReg = 0x6002;    // Pr8.02 触发寄存器（0x0040=急停）
    private const ushort VelocityStop = 0x0040;

    private const ushort JogForward = 0x4001;
    private const ushort JogReverse = 0x4002;
    private const ushort ResetAlarm = 0x1111;
    private const ushort SaveEeprom = 0x2211;

    public StepperDriver(IModbusMaster bus, byte slave)
    {
        _bus = bus;
        _slave = slave;
    }

    /// <summary>
    /// 用 PR 路径0「速度模式」连续运转：一次触发后持续转，直到急停或改速。速度稳定，无需 50ms 保活。
    /// accel/decel 单位 ms/1000rpm（默认 200ms 到 1000rpm，平滑起停）。
    /// </summary>
    public Task RunVelocityAsync(int rpm, int accelMs = 200, int decelMs = 200)
    {
        ushort[] pr0 =
        {
            0x0002,             // Pr9.00 TYPE=2 速度运行（可插断、绝对、不跳转）
            0x0000, 0x0000,     // Pr9.01/9.02 位置（速度模式忽略）
            (ushort)rpm,        // Pr9.03 运行速度 rpm
            (ushort)accelMs,    // Pr9.04 加速时间 ms/1000rpm
            (ushort)decelMs,    // Pr9.05 减速时间 ms/1000rpm
            0x0000,             // Pr9.06 停顿时间
            0x0010              // Pr9.07 触发（映射 Pr8.02，写 0x10 立即运行路径0）
        };
        return _bus.WriteMultipleRegistersAsync(_slave, Pr0Base, pr0);
    }

    /// <summary>速度模式停止：减速停车（0x6002 写 0x0040 急停）。</summary>
    public Task StopVelocityAsync() => _bus.WriteSingleRegisterAsync(_slave, TrigReg, VelocityStop);

    /// <summary>手动设零：以当前位置为原点（0x6002 写 0x0021）。
    /// 若某些配置要求「已回零」才允许触发 PR，可在首次启动前调用一次（无机械动作）。</summary>
    public Task ManualSetZeroAsync() => _bus.WriteSingleRegisterAsync(_slave, TrigReg, 0x0021);

    /// <summary>
    /// 设置电机峰值电流（A）并存 EEPROM。写 Pr5.00(0x0191)，单位 0.1A。
    /// <b>硬夹到 0.3–3.2A</b>（驱动器规格上限），防止误输入烧电机/过流。返回实际写入的电流(A)。
    /// </summary>
    public async Task<double> SetPeakCurrentAsync(double amps)
    {
        int raw = Math.Clamp((int)Math.Round(amps * 10.0), PeakCurrentMinRaw, PeakCurrentMaxRaw);
        await _bus.WriteSingleRegisterAsync(_slave, PeakCurrentReg, (ushort)raw);
        await SaveAsync();
        return raw / 10.0;
    }

    /// <summary>读驱动器当前设定的峰值电流（A）。步进为恒流驱动，此值即电机实际运行电流
    /// （开环步进无实时电流采样，故没有"负载电流"反馈，只有设定/运行电流）。</summary>
    public async Task<double> ReadPeakCurrentAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, PeakCurrentReg, 1);
        return r[0] / 10.0;
    }

    /// <summary>读母线电压（V）。Pr4.27(0x0177)，单位 0.1V。</summary>
    public async Task<double> ReadBusVoltageAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, 0x0177, 1);
        return r[0] / 10.0;
    }

    /// <summary>设置细分 = 指令脉冲数/转（Pr0.00）并存 EEPROM。硬夹到 200–51200。返回实际写入值。
    /// 细分越高低速越平顺、越不易激起共振。</summary>
    public async Task<int> SetMicrostepAsync(int pulsesPerRev)
    {
        int v = Math.Clamp(pulsesPerRev, MicrostepMin, MicrostepMax);
        await _bus.WriteSingleRegisterAsync(_slave, MicrostepReg, (ushort)v);
        await SaveAsync();
        return v;
    }

    /// <summary>读当前细分（指令脉冲数/转）。</summary>
    public async Task<int> ReadMicrostepAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, MicrostepReg, 1);
        return r[0];
    }

    /// <summary>设置待机电流百分比（Pr5.33，0–100）并存 EEPROM。硬夹到 0–100。返回实际写入值。</summary>
    public async Task<int> SetStandbyPctAsync(int pct)
    {
        int v = Math.Clamp(pct, 0, 100);
        await _bus.WriteSingleRegisterAsync(_slave, StandbyPctReg, (ushort)v);
        await SaveAsync();
        return v;
    }

    /// <summary>设置 JOG 速度（r/min）。仅 JOG 保活方式用到。</summary>
    public Task SetSpeedAsync(int rpm) => _bus.WriteSingleRegisterAsync(_slave, SpeedReg, (ushort)rpm);

    /// <summary>发送一次正向 JOG 保活脉冲（需每 ≤50ms 调用一次；已改用速度模式，保留备用）。</summary>
    public Task KeepRunForwardAsync() => _bus.WriteSingleRegisterAsync(_slave, CtrlReg, JogForward);

    /// <summary>发送一次反向 JOG 保活脉冲。</summary>
    public Task KeepRunReverseAsync() => _bus.WriteSingleRegisterAsync(_slave, CtrlReg, JogReverse);

    /// <summary>复位当前报警。</summary>
    public Task ResetAlarmAsync() => _bus.WriteSingleRegisterAsync(_slave, CtrlReg, ResetAlarm);

    /// <summary>保存参数到 EEPROM。</summary>
    public Task SaveAsync() => _bus.WriteSingleRegisterAsync(_slave, CtrlReg, SaveEeprom);

    /// <summary>读运行状态（Bit0 故障, Bit1 使能, Bit2 运行, Bit4 指令完成, Bit5 路径完成, Bit6 回零完成）。</summary>
    public async Task<DriveStatus> ReadStatusAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, StatusReg, 1);
        return new DriveStatus(r[0]);
    }

    /// <summary>读当前报警码（0 为无故障）。</summary>
    public async Task<ushort> ReadAlarmAsync()
    {
        var r = await _bus.ReadHoldingRegistersAsync(_slave, AlarmReg, 1);
        return r[0];
    }
}

/// <summary>驱动器运行状态字（0x1003）。</summary>
public readonly struct DriveStatus
{
    private readonly ushort _bits;
    public DriveStatus(ushort bits) => _bits = bits;

    public bool Fault => (_bits & 0x01) != 0;
    public bool Enabled => (_bits & 0x02) != 0;
    public bool Running => (_bits & 0x04) != 0;
    public bool CommandDone => (_bits & 0x10) != 0;
    public bool PathDone => (_bits & 0x20) != 0;
    public bool HomeDone => (_bits & 0x40) != 0;
}
