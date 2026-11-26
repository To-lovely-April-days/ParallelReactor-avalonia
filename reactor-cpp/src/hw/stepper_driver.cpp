#include "hw/stepper_driver.hpp"

#include <algorithm>

namespace reactor::hw {

namespace stir_gear {
int to_motor(int vessel_rpm, double ratio, int max_motor) {
    int64_t v = round_even(vessel_rpm * (ratio > 0 ? ratio : 1.0));
    return static_cast<int>(std::clamp<int64_t>(v, 0, max_motor));
}
double to_vessel(double motor_rpm, double ratio) { return motor_rpm / (ratio > 0 ? ratio : 1.0); }
}  // namespace stir_gear

int StepperDriver::clamp_peak_current_raw(double amps) {
    return static_cast<int>(std::clamp<int64_t>(round_even(amps * 10.0), kPeakCurrentMinRaw, kPeakCurrentMaxRaw));
}
int StepperDriver::clamp_microstep(int ppr) { return std::clamp(ppr, kMicrostepMin, kMicrostepMax); }
int StepperDriver::clamp_standby_pct(int pct) { return std::clamp(pct, 0, 100); }

ModbusRequest StepperDriver::req_run_velocity(int motor_rpm, int accel_ms, int decel_ms) const {
    std::vector<uint16_t> pr0{
        0x0002,                  // Pr9.00 TYPE=2 速度运行（可插断、绝对、不跳转）
        0x0000, 0x0000,          // Pr9.01/9.02 位置（速度模式忽略）
        reg_from_u16(motor_rpm), // Pr9.03 运行速度 rpm
        reg_from_u16(accel_ms),  // Pr9.04 加速时间 ms/1000rpm
        reg_from_u16(decel_ms),  // Pr9.05 减速时间 ms/1000rpm
        0x0000,                  // Pr9.06 停顿时间
        0x0010,                  // Pr9.07 触发（映射 Pr8.02，写 0x10 立即运行路径0）
    };
    return req_write_registers(slave_, kPr0Base, std::move(pr0));
}

std::vector<ModbusRequest> StepperDriver::req_set_peak_current(double amps) const {
    return {req_write_register(slave_, kPeakCurrentReg, static_cast<uint16_t>(clamp_peak_current_raw(amps))), req_save()};
}
std::vector<ModbusRequest> StepperDriver::req_set_microstep(int ppr) const {
    return {req_write_register(slave_, kMicrostepReg, static_cast<uint16_t>(clamp_microstep(ppr))), req_save()};
}
std::vector<ModbusRequest> StepperDriver::req_set_standby_pct(int pct) const {
    return {req_write_register(slave_, kStandbyPctReg, static_cast<uint16_t>(clamp_standby_pct(pct))), req_save()};
}

std::optional<double> StepperDriver::decode_tenths(const ModbusResult& r) {
    if (!r.ok || r.regs.size() != 1) return std::nullopt;
    return r.regs[0] / 10.0;
}
std::optional<int> StepperDriver::decode_u16(const ModbusResult& r) {
    if (!r.ok || r.regs.size() != 1) return std::nullopt;
    return static_cast<int>(r.regs[0]);
}
std::optional<DriveStatus> StepperDriver::decode_status(const ModbusResult& r) {
    if (!r.ok || r.regs.size() != 1) return std::nullopt;
    return DriveStatus{r.regs[0]};
}

static bool all_ok(const std::vector<ModbusResult>& rs, size_t n) { return rs.size() == n && rs.back().ok; }

std::optional<double> StepperDriver::set_peak_current(ModbusMaster& m, double amps) const {
    if (!all_ok(execute_seq(m, req_set_peak_current(amps)), 2)) return std::nullopt;
    return clamp_peak_current_raw(amps) / 10.0;
}
std::optional<int> StepperDriver::set_microstep(ModbusMaster& m, int ppr) const {
    if (!all_ok(execute_seq(m, req_set_microstep(ppr)), 2)) return std::nullopt;
    return clamp_microstep(ppr);
}
std::optional<int> StepperDriver::set_standby_pct(ModbusMaster& m, int pct) const {
    if (!all_ok(execute_seq(m, req_set_standby_pct(pct)), 2)) return std::nullopt;
    return clamp_standby_pct(pct);
}

}  // namespace reactor::hw
