#include "ctl/ctl_stir_service.hpp"

#include <cstdio>

namespace reactor::ctl {

std::string StirService::fault_name(int code) {
    switch (code) {
        case 0: return "";
        case 0x01: return "过流";
        case 0x02: return "过压";
        case 0x40: return "电流采样故障";
        case 0x80: return "锁轴/缺相（堵转）";
        case 0x100: return "参数自整定故障";
        case 0x200: return "EEPROM 故障";
        default: {
            char b[32];
            std::snprintf(b, sizeof(b), "故障 0x%X", static_cast<unsigned>(code));
            return b;
        }
    }
}

void StirService::track(bool ok, const std::string& err, bool report) {
    if (ok) { stats_.ok(io_.wall()); return; }
    stats_.fail();
    if (!report) return;
    int64_t now = io_.mono();
    if (now - last_report_ < kCommReportGapMs) return;
    last_report_ = now;
    if (on_comm_error) on_comm_error(err);
}

void StirService::init(std::function<void(int)> on_microstep) {
    if (!configured()) return;
    auto& d = io_.hw->stir;
    io_.submit(*io_.hw->stir_bus, d.req_read_peak_current(), [this](const hw::ModbusResult& r) {
        track(r.ok, r.error, true);
        if (auto a = hw::StepperDriver::decode_tenths(r); a && *a > 0) motor_current_ = *a;
    });
    io_.submit(*io_.hw->stir_bus, d.req_read_alarm(), [this](const hw::ModbusResult& r) {
        track(r.ok, r.error, false);
        if (auto c = hw::StepperDriver::decode_u16(r)) {
            alarm_code_ = *c;
            fault_text_ = fault_name(*c);
            if (on_alarm_code) on_alarm_code(*c);
        }
    });
    io_.submit(*io_.hw->stir_bus, d.req_read_microstep(), [this, on_microstep](const hw::ModbusResult& r) {
        track(r.ok, r.error, false);
        if (auto m = hw::StepperDriver::decode_u16(r); m && *m >= 200 && on_microstep) on_microstep(*m);
    });
}

void StirService::start(int rpm, uint64_t guard_epoch, DoneFn cb) {
    rpm_ = rpm;
    on_ = true;
    io_.chain(*io_.hw->stir_bus, io_.hw->stir.req_start(to_motor(rpm), ramp_ms_), true, guard_epoch,
              [this, cb](const std::vector<hw::ModbusResult>& rs, bool aborted) {
                  bool ok = !aborted && rs.size() == 2 && rs[0].ok && rs[1].ok;
                  for (const auto& r : rs) track(r.ok, r.error, true);
                  if (!ok) on_ = false;
                  if (cb) cb(ok);
              });
}

void StirService::stop(DoneFn cb) {
    on_ = false;
    io_.submit(*io_.hw->stir_bus, io_.hw->stir.req_stop(), [this, cb](const hw::ModbusResult& r) {
        track(r.ok, r.error, true);
        if (cb) cb(r.ok);
    });
}

void StirService::set_rpm(int rpm, uint64_t guard_epoch, DoneFn cb) {
    rpm_ = rpm;
    if (!on_) { if (cb) cb(true); return; }
    io_.chain(*io_.hw->stir_bus, {io_.hw->stir.req_run_velocity(to_motor(rpm), ramp_ms_, ramp_ms_)}, true, guard_epoch,
              [this, cb](const std::vector<hw::ModbusResult>& rs, bool aborted) {
                  bool ok = !aborted && rs.size() == 1 && rs[0].ok;
                  for (const auto& r : rs) track(r.ok, r.error, true);
                  if (cb) cb(ok);
              });
}

void StirService::clear_alarm(DoneFn cb) {
    io_.submit(*io_.hw->stir_bus, io_.hw->stir.req_reset_alarm(), [this, cb](const hw::ModbusResult& r) {
        track(r.ok, r.error, true);
        if (cb) cb(r.ok);
    });
}

void StirService::tick_poll() {
    if (!configured()) return;
    if (fault_tick_++ % kFaultPollEvery != 0) return;
    auto& d = io_.hw->stir;
    io_.submit(*io_.hw->stir_bus, d.req_read_alarm(), [this](const hw::ModbusResult& r) {
        track(r.ok, r.error, false);
        if (auto c = hw::StepperDriver::decode_u16(r)) {
            alarm_code_ = *c;
            fault_text_ = fault_name(*c);
            if (on_alarm_code) on_alarm_code(*c);
        }
    });
    io_.submit(*io_.hw->stir_bus, d.req_read_bus_voltage(), [this](const hw::ModbusResult& r) {
        track(r.ok, r.error, false);
        if (auto v = hw::StepperDriver::decode_tenths(r)) bus_voltage_ = *v;
    });
}

}  // namespace reactor::ctl
