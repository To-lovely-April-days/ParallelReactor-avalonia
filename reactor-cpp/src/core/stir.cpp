#include "core/stir.hpp"

#include <algorithm>
#include <cmath>
#include <optional>
#include <vector>

namespace reactor::core {

namespace stir_gear {
int to_motor(int vessel_rpm, double ratio, int max_motor) {
    // C# Math.Round 默认是银行家舍入（.5 取偶）：std::nearbyint 在默认舍入模式下同样取偶
    double m = std::nearbyint(vessel_rpm * (ratio > 0 ? ratio : 1));
    return static_cast<int>(std::clamp(m, 0.0, static_cast<double>(max_motor)));
}

double to_vessel(double motor_rpm, double ratio) { return motor_rpm / (ratio > 0 ? ratio : 1); }
}  // namespace stir_gear

std::optional<StirHolder> StirShare::request(int rv, int rpm) {
    std::optional<StirHolder> prev;
    if (last_rpm_ && *last_rpm_ != rpm && last_setter_ && *last_setter_ != rv) prev = StirHolder{*last_setter_, *last_rpm_};
    holders_.erase(std::remove_if(holders_.begin(), holders_.end(), [&](const StirHolder& h) { return h.rv == rv; }),
                   holders_.end());
    holders_.push_back({rv, rpm});
    last_rpm_ = rpm;
    last_setter_ = rv;
    return prev;
}

bool StirShare::release(int rv) {
    holders_.erase(std::remove_if(holders_.begin(), holders_.end(), [&](const StirHolder& h) { return h.rv == rv; }),
                   holders_.end());
    if (last_setter_ && *last_setter_ == rv) {
        if (!holders_.empty()) {
            last_setter_ = holders_.back().rv;
            last_rpm_ = holders_.back().rpm;
        } else {
            last_setter_.reset();
            last_rpm_.reset();
        }
    }
    return !holders_.empty();
}

bool StirShare::holds(int rv) const {
    return std::any_of(holders_.begin(), holders_.end(), [&](const StirHolder& h) { return h.rv == rv; });
}

std::vector<int> StirShare::holders() const {
    std::vector<int> v;
    for (auto& h : holders_) v.push_back(h.rv);
    return v;
}

std::optional<StirHolder> StirShare::current() const {
    if (holders_.empty()) return std::nullopt;
    return holders_.back();
}

void StirShare::clear() {
    holders_.clear();
    last_setter_.reset();
    last_rpm_.reset();
}

}  // namespace reactor::core
