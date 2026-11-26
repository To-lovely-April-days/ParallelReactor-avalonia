#include "core/value_rules.hpp"

#include <cmath>
#include <limits>

#include "core/num_format.hpp"
#include "core/op_codes.hpp"

namespace reactor::core::value_rules {

namespace {
std::string n(double v) { return fmt_dec(v, 2, 0); }  // C#: N(v) = "0.##"
}  // namespace

std::optional<RuleViolation> range(const std::string& label, double v, double min, double max,
                                   const std::string& unit) {
    if (std::isfinite(v) && v >= min && v <= max) return std::nullopt;
    std::string u = unit.empty() ? "" : " " + unit;
    return RuleViolation{op_codes::kParamOutOfRange, label + "须在 " + n(min) + "–" + n(max) + u + " 之间", min, max, v};
}

std::optional<RuleViolation> temp_sp(double v, double t_max_sp) {
    return range("温度 SP ", v, kTempSpMin, t_max_sp, "°C");
}

std::optional<RuleViolation> strict_temp_sp(double v, double over_temp) {
    if (std::isfinite(v) && v < over_temp) return std::nullopt;
    return RuleViolation{op_codes::kParamOutOfRange, "温度 SP 须低于超温报警值 " + n(over_temp) + " °C",
                         -std::numeric_limits<double>::infinity(), over_temp, v};
}

std::optional<RuleViolation> volume(double ml, double vol_max) {
    return range("溶液体积", ml, kVolumeMin, vol_max, "mL");
}

double round_volume(double ml) {
    double k = std::pow(10.0, kVolumeDecimals);
    return std::round(ml * k) / k;  // std::round = 远离零（C#: MidpointRounding.AwayFromZero）
}

std::optional<RuleViolation> rpm(double r, double rpm_max) { return range("搅拌转速", r, 0, rpm_max, "rpm"); }

std::optional<RuleViolation> pid(int band, double p, double i, double d, int band_count) {
    if (auto r = range("PID 档位", band, 0, band_count - 1)) return r;
    if (auto r = range("比例带 P ", p, 0, kPidPMax)) return r;
    if (auto r = range("积分时间 I ", i, 0, kPidIMax, "s")) return r;
    return range("微分时间 D ", d, 0, kPidDMax, "s");
}

std::optional<RuleViolation> profile_seg_count(double cnt, int max) {
    return range("曲线段数", cnt, kProfileSegMin, max, "段");
}

std::optional<RuleViolation> profile_segment(std::optional<double> temp_c, std::optional<double> minutes,
                                             double t_max_sp) {
    if (temp_c)
        if (auto r = range("段目标温度", *temp_c, 0, t_max_sp, "°C")) return r;
    if (minutes) return range("段时长", *minutes, kSegMinutesMin, kSegMinutesMax, "分钟");
    return std::nullopt;
}

std::optional<RuleViolation> autotune_sp(double v, double t_max_sp) {
    return range("整定目标温度", v, kAutotuneSpMin, t_max_sp, "°C");
}

std::optional<RuleViolation> autotune_band(int band, double temp_c, const std::vector<std::pair<double, double>>& bands,
                                           double t_max_sp) {
    if (auto bad = range("PID 档位", band, 0, static_cast<double>(bands.size()) - 1)) return bad;
    double lo = bands[static_cast<size_t>(band)].first, hi = bands[static_cast<size_t>(band)].second;
    bool last = band == static_cast<int>(bands.size()) - 1;
    bool inside = std::isfinite(temp_c) && temp_c >= lo && (last ? temp_c <= t_max_sp : temp_c < hi);
    if (inside) return std::nullopt;
    double max = last ? t_max_sp : hi;
    std::string rg = last ? "≥ " + n(lo) + " °C" : n(lo) + "–" + n(hi) + " °C（不含 " + n(hi) + "）";
    return RuleViolation{op_codes::kParamOutOfRange,
                         "整定目标温度 " + n(temp_c) + " °C 不在档位 " + std::to_string(band) + " 的温区内（" + rg + "）",
                         lo, max, temp_c};
}

std::optional<RuleViolation> band_bound(int which, double value, double low_hi, double mid_hi) {
    switch (which) {
        case 1: return range("低温 / 中温 分界", value, kBandBoundMin, mid_hi - kBandGap, "℃");
        case 2: return range("中温 / 高温 分界", value, low_hi + kBandGap, kBandBoundMax, "℃");
        default: return range("档位分界编号", which, 1, 2);
    }
}

}  // namespace reactor::core::value_rules
