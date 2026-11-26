#include "ctl/ctl_pressure_service.hpp"

#include "hw/analog_module.hpp"

namespace reactor::ctl {

void PressureService::read(bool skip_if_busy, std::function<void(bool)> cb) {
    if (skip_if_busy && in_flight_ > 0) return;  // C# PollAsync(skipIfBusy: true)
    ++in_flight_;
    io_.submit(*io_.hw->press_bus, io_.hw->press.req_read_raw(), [this, cb](const hw::ModbusResult& r) {
        --in_flight_;
        auto raw = io_.hw->press.decode_raw(r);
        if (!raw) {
            stats_.fail();
            if (cb) cb(false);
            return;
        }
        PressureSample s;
        for (size_t i = 0; i < 8 && i < raw->size(); ++i) {
            s.raw[i] = (*raw)[i];
            s.psi[i] = hw::AnalogModule::to_psi((*raw)[i], fullscale_psi_, sig_);
        }
        s.at_wall_ms = io_.wall();
        last_ = s;
        stats_.ok(s.at_wall_ms);
        if (cb) cb(true);
    });
}

}  // namespace reactor::ctl
