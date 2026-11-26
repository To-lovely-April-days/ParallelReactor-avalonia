// C#: tests/ParallelReactor.Tests/ValueRulesTests.cs
#include <cmath>
#include <cstdio>
#include <functional>
#include <limits>
#include <string>
#include <vector>

#include "core/op_codes.hpp"
#include "core/value_rules.hpp"
#include "testing.hpp"
#include <optional>
#include <utility>

using namespace reactor::core;
namespace vr = reactor::core::value_rules;

namespace {
constexpr double Eps = 1e-6;
struct RangeCase {
    const char* name;
    double min, max;
    std::function<std::optional<RuleViolation>(double)> check;
};
// TMaxSp = 200, VolMax = 20, RpmMax = 1500
std::vector<RangeCase> ranges() {
    return {
        {"TempSp", 10, 200, [](double v) { return vr::temp_sp(v, 200); }},
        {"Volume", 1, 20, [](double v) { return vr::volume(v, 20); }},
        {"Rpm", 0, 1500, [](double v) { return vr::rpm(v, 1500); }},
        {"SegCount", 1, 16, [](double v) { return vr::profile_seg_count(v); }},
        {"SegTemp", 0, 200, [](double v) { return vr::profile_segment(v, std::nullopt, 200); }},
        {"SegMinutes", 0.1, 6000, [](double v) { return vr::profile_segment(std::nullopt, v, 200); }},
        {"AutotuneSp", 40, 200, [](double v) { return vr::autotune_sp(v, 200); }},
        {"PidP", 0, 3200, [](double v) { return vr::pid(0, v, 100, 10); }},
        {"PidI", 0, 3200, [](double v) { return vr::pid(0, 50, v, 10); }},
        {"PidD", 0, 327, [](double v) { return vr::pid(0, 50, 100, v); }},
        {"Bound1", 10, 140, [](double v) { return vr::band_bound(1, v, 100, 150); }},
        {"Bound2", 110, 390, [](double v) { return vr::band_bound(2, v, 100, 150); }},
    };
}
}  // namespace

TEST(core_value_rules, boundaries) {
    const double inf = std::numeric_limits<double>::infinity();
    for (auto& c : ranges()) {
        CHECK(!c.check(c.min).has_value());
        CHECK(!c.check(c.max).has_value());
        CHECK(!c.check((c.min + c.max) / 2).has_value());
        for (double bad : {c.min - Eps, c.max + Eps, std::nan(""), inf, -inf}) {
            auto v = c.check(bad);
            CHECK(v.has_value());
            if (!v) { std::printf("    case %s value %g\n", c.name, bad); continue; }
            CHECK_EQ(v->code, std::string(op_codes::kParamOutOfRange));
            CHECK_EQ(v->min, c.min);
            CHECK_EQ(v->max, c.max);
        }
    }
}

TEST(core_value_rules, message_carries_range_and_unit) {
    CHECK_EQ(vr::temp_sp(5, 200)->message, std::string("温度 SP 须在 10–200 °C 之间"));
    CHECK_EQ(vr::volume(0, 20)->message, std::string("溶液体积须在 1–20 mL 之间"));
    CHECK_EQ(vr::pid(0, 1, 1, 400)->message, std::string("微分时间 D 须在 0–327 s 之间"));
    CHECK_EQ(vr::profile_segment(std::nullopt, 0, 200)->message, std::string("段时长须在 0.1–6000 分钟 之间"));
}

TEST(core_value_rules, volume_accepts_decimals_and_rounds_step_noise) {
    CHECK(!vr::volume(3.5, 8).has_value());
    CHECK(!vr::volume(1.0, 8).has_value());
    CHECK(vr::volume(0.9, 8).has_value());
    CHECK(vr::volume(8.05, 8).has_value());
    CHECK(vr::volume(std::nan(""), 8).has_value());
    CHECK_EQ(vr::round_volume(3.2 + 0.1), 3.3);
    CHECK_EQ(vr::round_volume(3.456), 3.46);
    CHECK_EQ(vr::volume(9, 7.5)->message, std::string("溶液体积须在 1–7.5 mL 之间"));
}

TEST(core_value_rules, detail_has_min_max_value) {
    auto d = vr::rpm(2000, 1500);
    CHECK_EQ(d->min, 0.0);
    CHECK_EQ(d->max, 1500.0);
    CHECK_EQ(d->value, 2000.0);
}

TEST(core_value_rules, pid_band_out_of_range_reported_first) {
    CHECK(vr::pid(3, 99999, 1, 1)->message.find("PID 档位") != std::string::npos);
    CHECK(vr::pid(-1, 1, 1, 1).has_value());
    CHECK(!vr::pid(2, 1, 1, 1).has_value());
}

TEST(core_value_rules, profile_segment_both_null_is_valid) {
    CHECK(!vr::profile_segment(std::nullopt, std::nullopt, 200).has_value());
}

TEST(core_value_rules, strict_temp_sp_must_be_below_over_temp) {
    CHECK(!vr::strict_temp_sp(199.9, 200).has_value());
    CHECK(vr::strict_temp_sp(200, 200).has_value());
    CHECK(vr::strict_temp_sp(std::nan(""), 200).has_value());
    CHECK(vr::strict_temp_sp(250, 200)->message.find("超温报警值 200") != std::string::npos);
}

TEST(core_value_rules, band_bound_unknown_index_rejected) { CHECK(vr::band_bound(3, 100, 100, 150).has_value()); }

// autotune_band（C# 无直接单测，按源码规则补充）
TEST(core_value_rules, autotune_band_inside_zone) {
    std::vector<std::pair<double, double>> bands{{0, 100}, {100, 150}, {150, 400}};
    CHECK(!vr::autotune_band(0, 80, bands, 300).has_value());
    CHECK(!vr::autotune_band(2, 300, bands, 300).has_value());
    auto r = vr::autotune_band(1, 150, bands, 300);
    CHECK_EQ(r->message, std::string("整定目标温度 150 °C 不在档位 1 的温区内（100–150 °C（不含 150））"));
    CHECK_EQ(vr::autotune_band(2, 120, bands, 300)->message,
             std::string("整定目标温度 120 °C 不在档位 2 的温区内（≥ 150 °C）"));
    CHECK(vr::autotune_band(3, 120, bands, 300)->message.find("PID 档位") != std::string::npos);
}
