#include "core/over_pressure_guard.hpp"

#include <cmath>
#include <cstdint>
#include <optional>

namespace reactor::core {

// ---------------- ActuatorRetry ----------------

void ActuatorRetry::reset() {
    seq_++;
    in_flight_ = false;
    acked_ = false;
    confirmed_ = false;
    sends_ = 0;
    reopen_pending_ = false;
    last_send_ = over_temp::kNeverMs;
}

void ActuatorRetry::send_now(int64_t now_ms) {
    seq_++;
    in_flight_ = true;
    last_send_ = now_ms;
    sends_++;
}

ActuatorStep ActuatorRetry::step(std::optional<bool> done, int64_t now_ms) {
    if (!in_flight_) {
        if (!confirmed_) {
            if (done == true || (!done.has_value() && acked_)) {
                confirmed_ = true;
                reopen_pending_ = false;
                sends_ = 0;
                return ActuatorStep::Confirmed;
            }
        } else if (done == false) {
            confirmed_ = false;
            acked_ = false;
            reopen_pending_ = true;
        }
    }

    if (confirmed_ || in_flight_ || now_ms - last_send_ < over_temp::kRetryIntervalMs) return ActuatorStep::None;

    if (reopen_pending_) {
        reopen_pending_ = false;
        sends_ = 0;
        send_now(now_ms);
        return ActuatorStep::Reopened;
    }
    send_now(now_ms);
    return ActuatorStep::Retry;
}

void ActuatorRetry::on_result(bool ok, bool active, std::optional<int> seq) {
    if (seq && *seq != seq_) return;
    in_flight_ = false;
    if (active && ok) acked_ = true;
}

// ---------------- OverPressureGuard ----------------

OverPressureGuard::OverPressureGuard(int channels)
    : tripped_(static_cast<size_t>(channels > 0 ? channels : 0), false),
      valves_(static_cast<size_t>(channels > 0 ? channels : 0)),
      heat_(static_cast<size_t>(channels > 0 ? channels : 0)) {}

OverPressureActions OverPressureGuard::evaluate(int ch, double p, bool online, double limit, int64_t now_ms,
                                                bool valves_closed_shown, bool valves_enabled,
                                                std::optional<bool> heat_stopped, bool heat_enabled) {
    OverPressureActions a;
    if (!online || std::isnan(p)) return a;
    size_t i = static_cast<size_t>(ch);

    if (!tripped_[i]) {
        if (p < limit) return a;
        tripped_[i] = true;
        valves_[i].reset();
        heat_[i].reset();
        a.trip = true;
        if (valves_enabled) { valves_[i].send_now(now_ms); a.send_valves = true; }
        if (heat_enabled) { heat_[i].send_now(now_ms); a.send_heat = true; }
        return a;
    }

    if (p < limit * over_pressure::kRearmRatio) {
        tripped_[i] = false;
        valves_[i].reset();
        heat_[i].reset();
        a.cleared = true;
        return a;
    }

    if (valves_enabled) {
        // 阀门没有独立回读：界面显示打开 = 明确未关（false）；显示全关时以写入回显为准（nullopt）
        std::optional<bool> done = valves_closed_shown ? std::nullopt : std::optional<bool>(false);
        switch (valves_[i].step(done, now_ms)) {
            case ActuatorStep::Confirmed: a.valves_confirmed = true; break;
            case ActuatorStep::Retry: a.send_valves = a.valves_retry = true; break;
            case ActuatorStep::Reopened: a.send_valves = a.valves_reopened = true; break;
            default: break;
        }
    }
    if (heat_enabled) {
        switch (heat_[i].step(heat_stopped, now_ms)) {
            case ActuatorStep::Confirmed: a.heat_confirmed = true; break;
            case ActuatorStep::Retry: a.send_heat = a.heat_retry = true; break;
            case ActuatorStep::Reopened: a.send_heat = a.heat_reopened = true; break;
            default: break;
        }
    }
    return a;
}

void OverPressureGuard::on_valve_result(int ch, bool ok, std::optional<int> seq) {
    valves_[static_cast<size_t>(ch)].on_result(ok, tripped_[static_cast<size_t>(ch)], seq);
}

void OverPressureGuard::on_heat_result(int ch, bool ok, std::optional<int> seq) {
    heat_[static_cast<size_t>(ch)].on_result(ok, tripped_[static_cast<size_t>(ch)], seq);
}

}  // namespace reactor::core
