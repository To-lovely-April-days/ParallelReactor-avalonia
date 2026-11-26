#include "core/program/text_format.hpp"

#include <cmath>
#include <cstdio>
#include <string>

namespace reactor::core::program {

std::string fmt_n(double v) {
    if (!std::isfinite(v)) return fmt_dec(v, 0);
    if (v == std::floor(v) && std::fabs(v) < 9.2e18) return std::to_string(static_cast<long long>(v));
    return fmt_dec(v, 3, 0);
}

std::string fmt_00(int i) {
    char b[16];
    std::snprintf(b, sizeof b, "%02d", i);
    return b;
}

double round_even(double x) { return std::nearbyint(x); }

double round_even(double x, int digits) {
    double k = std::pow(10.0, digits);
    return std::nearbyint(x * k) / k;
}

}  // namespace reactor::core::program
