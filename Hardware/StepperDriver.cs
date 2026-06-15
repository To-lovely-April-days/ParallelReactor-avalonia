using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 雷赛 DM2C-RS432 驱控一体步进驱动器（全局搅拌电机）。功能码 0x03/0x06/0x10。
/// 速度运行用 JOG：写 0x1801=0x4001(正)/0x4002(反)，<b>必须每 ≤50ms 重发一次</b>才连续转，停发即停。
/// 速度 Pr6.00=0x01E1(r/min)。状态 0x1003、报警 0x2203、保存 0x1801=0x2211、复位报警 0x1801=0x1111。
/// 参数为 32 位，实际只用低 16 位寄存器。
/// </summary>
public sealed class StepperDriver
{
    private readonly IModbusMaster _bus;
    private readonly byte _slave;

    private const ushort SpeedReg = 0x01E1;   // Pr6.00 JOG 速度 r/min
    private const ushort CtrlReg = 0x1801;    // 控制字
    private const ushort StatusReg = 0x1003;  // 运行状态
    private const ushort AlarmReg = 0x2203;   // 当前报警

    private const ushort JogForward = 0x4001;
    private const ushort JogReverse = 0x4002;
    private const ushort ResetAlarm = 0x1111;
    private const ushort SaveEeprom = 0x2211;

    public StepperDriver(IModbusMaster bus, byte slave)
    {
        _bus = bus;
        _slave = slave;
    }

    /// <summary>设置 JOG 速度（r/min）。</summary>
    public Task SetSpeedAsync(int rpm) => _bus.WriteSingleRegisterAsync(_slave, SpeedReg, (ushort)rpm);

    /// <summary>发送一次正向 JOG 保活脉冲（需每 ≤50ms 调用一次以保持连续运转，停止调用即停转）。</summary>
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
