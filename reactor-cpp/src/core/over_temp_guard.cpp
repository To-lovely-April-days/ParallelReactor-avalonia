#include "core/over_temp_guard.hpp"

#include <cmath>

namespace reactor::core {

OverTempGuard::OverTempGuard(int channels) : ch_(static_cast<size_t>(channels > 0 ? channels : 0)) {}

OverTempAction OverTempGuard::evaluate(int ch, double pv, bool online, double limit, int64_t now_ms,
                                       std::optional<bool> output_stopped) {
    if (!online || std::isnan(pv)) return OverTempAction::None;
    Ch& c = ch_[static_cast<size_t>(ch)];

    if (!c.tripped) {
        if (pv < limit) return OverTempAction::None;
        c.tripped = true;
        c.confirmed = false;
        c.acked = false;
        c.reopen_pending = false;
        return send(ch, now_ms, OverTempAction::TripSend);
    }

    if (pv < limit - over_temp::kHysteresis) {
        c.tripped = false;
        c.confirmed = false;
        c.in_flight = false;
        c.acked = false;
        c.reopen_pending = false;
        c.sends = 0;
        return OverTempAction::Cleared;
    }

    if (!c.in_flight) {
        if (!c.confirmed) {
            if (output_stopped == true || (!output_stopped.has_value() && c.acked)) {
                c.confirmed = true;
                c.reopen_pending = false;
                c.sends = 0;
                return OverTempAction::Confirmed;
            }
        } else if (output_stopped == false) {
            // 已确认后回读显示输出没停：重新切断
            c.confirmed = false;
            c.acked = false;
            c.reopen_pending = true;
        }
    }

    if (c.confirmed || c.in_flight || now_ms - c.last_send < over_temp::kRetryIntervalMs) return OverTempAction::None;

    if (c.reopen_pending) {
        c.reopen_pending = false;
        c.sends = 0;
        return send(ch, now_ms, OverTempAction::Reopened);
    }
    return send(ch, now_ms, OverTempAction::RetrySend);
}

OverTempAction OverTempGuard::send(int ch, int64_t now_ms, OverTempAction a) {
    Ch& c = ch_[static_cast<size_t>(ch)];
    c.in_flight = true;
    c.last_send = now_ms;
    c.sends++;
    return a;
}

void OverTempGuard::on_stop_result(int ch, bool ok, int64_t) {
    Ch& c = ch_[static_cast<size_t>(ch)];
    c.in_flight = false;
    if (!c.tripped) return;
    if (ok) c.acked = true;
}

}  // namespace reactor::core
