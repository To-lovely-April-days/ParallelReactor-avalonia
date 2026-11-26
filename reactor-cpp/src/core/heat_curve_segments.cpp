#include "core/heat_curve_segments.hpp"

#include <cmath>
#include <vector>

namespace reactor::core {

std::vector<ProfileSegment> heat_curve_to_profile(const std::vector<HeatSegment>& segs, double start_temp, int max) {
    std::vector<ProfileSegment> conv;
    double prev = start_temp;
    for (const auto& s : segs) {
        if (static_cast<int>(conv.size()) >= max) break;
        double dt = std::fabs(s.temp - prev);
        double ramp = dt < 0.01 ? 0 : s.rate > 0 ? dt / s.rate : 0.01;
        if (ramp > 0) conv.push_back({s.temp, ramp});
        prev = s.temp;
        if (static_cast<int>(conv.size()) < max && s.hold > 0) conv.push_back({s.temp, s.hold});
    }
    return conv;
}

}  // namespace reactor::core
