// 雷赛 DM2C-RS432 驱控一体步进驱动器（全机 1 台搅拌电机）。C#: Hardware/StepperDriver.cs + Models/StirGear.cs
//
// 连续搅拌用 PR 路径0「速度模式」：0x10 写 0x6200 起 8 个寄存器（Pr9.00=2 速度运行、Pr9.03=电机 rpm、
// Pr9.04/9.05 加/减速 ms/1000rpm、Pr9.07=0x10 立即触发），一次触发后持续匀速运转，不用保活。
// 停止：0x6002 写 0x0040（减速停）。控制字 0x1801：复位报警 0x1111、存 EEPROM 0x2211、JOG 0x4001/0x4002（备用）。
// 状态 0x1003、报警 0x2203、峰值电流 0x0191（0.1 A，硬夹 0.3–3.2 A）、待机电流 % 0x01D3（0–100）、
// 细分 0x0001（200–51200）、母线电压 0x0177（0.1 V）、JOG 速度 0x01E1。参数 32 位，只用低 16 位。
//
// 传动：界面 / 程序 / 接口的转速都是**釜里**的转速；电机 = 釜 × 85/38 ≈ 2.237，限幅 3000。
// 不做的事（留给 ctl）：开/关状态、当前转速、RampMs（C# StirController 默认 1000）、急停锁、在线统计、通讯错误节流。
// 对象不可变，任何线程可用。
#pragma once
#include <cstdint>
#include <optional>
#include <vector>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

namespace stir_gear {
constexpr double kDefaultRatio = 85.0 / 38.0;  // C#: StirGear.DefaultRatio
constexpr int kMaxMotorRpm = 3000;             // C#: StirController.MaxMotorRpm
// 釜转速 → 电机转速（五成双舍入，限幅 0..max_motor；传动比非正按 1）
int to_motor(int vessel_rpm, double ratio, int max_motor = kMaxMotorRpm);
// 电机转速 → 釜转速
double to_vessel(double motor_rpm, double ratio);
}  // namespace stir_gear

// 运行状态字（0x1003）
struct DriveStatus {
    uint16_t bits = 0;
    bool fault() const { return bits & 0x01; }
    bool enabled() const { return bits & 0x02; }
    bool running() const { return bits & 0x04; }
    bool command_done() const { return bits & 0x10; }
    bool path_done() const { return bits & 0x20; }
    bool home_done() const { return bits & 0x40; }
};

class StepperDriver {
public:
    static constexpr uint16_t kJogSpeedReg = 0x01E1;      // Pr6.00 JOG 速度 r/min
    static constexpr uint16_t kCtrlReg = 0x1801;          // 控制字
    static constexpr uint16_t kStatusReg = 0x1003;
    static constexpr uint16_t kAlarmReg = 0x2203;
    static constexpr uint16_t kPeakCurrentReg = 0x0191;   // Pr5.00，0.1 A
    static constexpr uint16_t kStandbyPctReg = 0x01D3;    // Pr5.33，%
    static constexpr uint16_t kMicrostepReg = 0x0001;     // Pr0.00
    static constexpr uint16_t kBusVoltageReg = 0x0177;    // Pr4.27，0.1 V
    static constexpr uint16_t kPr0Base = 0x6200;          // Pr9.00 起 8 个
    static constexpr uint16_t kTrigReg = 0x6002;          // Pr8.02
    static constexpr uint16_t kVelocityStop = 0x0040;
    static constexpr uint16_t kManualZero = 0x0021;
    static constexpr uint16_t kJogForward = 0x4001;
    static constexpr uint16_t kJogReverse = 0x4002;
    static constexpr uint16_t kResetAlarm = 0x1111;
    static constexpr uint16_t kSaveEeprom = 0x2211;
    static constexpr int kMicrostepMin = 200;
    static constexpr int kMicrostepMax = 51200;
    static constexpr int kPeakCurrentMinRaw = 3;   // 0.3 A
    static constexpr int kPeakCurrentMaxRaw = 32;  // 3.2 A

    explicit StepperDriver(uint8_t slave = 1) : slave_(slave) {}
    uint8_t slave() const { return slave_; }

    // ---- 夹值（C# Math.Clamp + Math.Round） ----
    static int clamp_peak_current_raw(double amps);  // 返回 3..32（0.1 A）
    static int clamp_microstep(int ppr);             // 200..51200
    static int clamp_standby_pct(int pct);           // 0..100

    // ---- 请求 ----
    // motor_rpm 是**电机**转速（先用 stir_gear::to_motor 换算）；accel/decel 单位 ms/1000rpm（C# 默认 200）
    ModbusRequest req_run_velocity(int motor_rpm, int accel_ms = 200, int decel_ms = 200) const;
    ModbusRequest req_stop() const { return req_write_register(slave_, kTrigReg, kVelocityStop); }
    ModbusRequest req_manual_set_zero() const { return req_write_register(slave_, kTrigReg, kManualZero); }
    ModbusRequest req_reset_alarm() const { return req_write_register(slave_, kCtrlReg, kResetAlarm); }
    ModbusRequest req_save() const { return req_write_register(slave_, kCtrlReg, kSaveEeprom); }
    ModbusRequest req_jog_forward() const { return req_write_register(slave_, kCtrlReg, kJogForward); }
    ModbusRequest req_jog_reverse() const { return req_write_register(slave_, kCtrlReg, kJogReverse); }
    ModbusRequest req_set_jog_speed(int rpm) const { return req_write_register(slave_, kJogSpeedReg, reg_from_u16(rpm)); }
    // 启动搅拌：复位报警 → 速度模式触发（C# StirController.StartAsync 的两笔）
    std::vector<ModbusRequest> req_start(int motor_rpm, int ramp_ms) const {
        return {req_reset_alarm(), req_run_velocity(motor_rpm, ramp_ms, ramp_ms)};
    }
    // 设参数并存 EEPROM（两笔：写参数、写 0x1801=0x2211）
    std::vector<ModbusRequest> req_set_peak_current(double amps) const;
    std::vector<ModbusRequest> req_set_microstep(int ppr) const;
    std::vector<ModbusRequest> req_set_standby_pct(int pct) const;
    ModbusRequest req_read_peak_current() const { return req_read_holding(slave_, kPeakCurrentReg, 1); }
    ModbusRequest req_read_microstep() const { return req_read_holding(slave_, kMicrostepReg, 1); }
    ModbusRequest req_read_standby_pct() const { return req_read_holding(slave_, kStandbyPctReg, 1); }
    ModbusRequest req_read_bus_voltage() const { return req_read_holding(slave_, kBusVoltageReg, 1); }
    ModbusRequest req_read_status() const { return req_read_holding(slave_, kStatusReg, 1); }
    ModbusRequest req_read_alarm() const { return req_read_holding(slave_, kAlarmReg, 1); }
    ModbusRequest req_read_control_word() const { return req_read_holding(slave_, kCtrlReg, 1); }

    // ---- 解码 ----
    static std::optional<double> decode_tenths(const ModbusResult& r);  // 峰值电流 A / 母线电压 V
    static std::optional<int> decode_u16(const ModbusResult& r);        // 细分 / 待机% / 报警码 / 控制字
    static std::optional<DriveStatus> decode_status(const ModbusResult& r);

    // ---- 同步便捷 ----
    bool run_velocity(ModbusMaster& m, int motor_rpm, int accel_ms = 200, int decel_ms = 200) const {
        return m.execute(req_run_velocity(motor_rpm, accel_ms, decel_ms)).ok;
    }
    bool stop(ModbusMaster& m) const { return m.execute(req_stop()).ok; }
    bool reset_alarm(ModbusMaster& m) const { return m.execute(req_reset_alarm()).ok; }
    bool save(ModbusMaster& m) const { return m.execute(req_save()).ok; }
    // 返回实际写入值（夹过的），通讯失败为空
    std::optional<double> set_peak_current(ModbusMaster& m, double amps) const;
    std::optional<int> set_microstep(ModbusMaster& m, int ppr) const;
    std::optional<int> set_standby_pct(ModbusMaster& m, int pct) const;
    std::optional<double> read_peak_current(ModbusMaster& m) const { return decode_tenths(m.execute(req_read_peak_current())); }
    std::optional<double> read_bus_voltage(ModbusMaster& m) const { return decode_tenths(m.execute(req_read_bus_voltage())); }
    std::optional<int> read_microstep(ModbusMaster& m) const { return decode_u16(m.execute(req_read_microstep())); }
    std::optional<int> read_alarm(ModbusMaster& m) const { return decode_u16(m.execute(req_read_alarm())); }
    std::optional<DriveStatus> read_status(ModbusMaster& m) const { return decode_status(m.execute(req_read_status())); }

private:
    uint8_t slave_;
};

}  // namespace reactor::hw
