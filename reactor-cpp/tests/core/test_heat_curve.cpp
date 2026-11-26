// C#: tests/ParallelReactor.Tests/ProgramStepTests.cs（HeatCurvePlanTests）
// TempProfile / HeatApproach 在 C# 没有单测（依赖 ObservableObject），这里按源码规则补充。
#include <string>
#include <vector>

#include "core/heat_approach.hpp"
#include "core/heat_curve_segments.hpp"
#include "core/temp_profile.hpp"
#include "testing.hpp"

using namespace reactor::core;

namespace {
bool seg_eq(const ProfileSegment& s, double t, double m) { return std::fabs(s.temp - t) < 1e-9 && std::fabs(s.minutes - m) < 1e-9; }
}  // namespace

TEST(core_heat_curve_plan, ramp_and_hold_produces_ramp_then_hold) {
    auto conv = heat_curve_to_profile({{80, 5, 10}}, 25);
    CHECK_EQ(conv.size(), size_t(2));
    CHECK(seg_eq(conv[0], 80, 11));
    CHECK(seg_eq(conv[1], 80, 10));
}

TEST(core_heat_curve_plan, chained_segments_ramp_starts_from_previous_target) {
    auto conv = heat_curve_to_profile({{80, 5, 0}, {130, 2.5, 20}}, 25);
    CHECK_EQ(conv.size(), size_t(3));
    CHECK(seg_eq(conv[0], 80, 11));
    CHECK(seg_eq(conv[1], 130, 20));
    CHECK(seg_eq(conv[2], 130, 20));
}

TEST(core_heat_curve_plan, zero_rate_with_temp_change_is_step_change) {
    auto conv = heat_curve_to_profile({{100, 0, 0}}, 25);
    CHECK_EQ(conv.size(), size_t(1));
    CHECK(seg_eq(conv[0], 100, 0.01));
}

TEST(core_heat_curve_plan, same_temp_skips_ramp) {
    auto conv = heat_curve_to_profile({{25, 5, 30}}, 25);
    CHECK_EQ(conv.size(), size_t(1));
    CHECK(seg_eq(conv[0], 25, 30));
}

TEST(core_heat_curve_plan, no_ramp_no_hold_returns_empty) { CHECK(heat_curve_to_profile({{25, 5, 0}}, 25).empty()); }

TEST(core_heat_curve_plan, over_max_segments_is_truncated) {
    std::vector<HeatSegment> segs;
    for (int i = 0; i < 10; i++) segs.push_back({50.0 + i * 10, 100, 5});
    CHECK_EQ(heat_curve_to_profile(segs, 25).size(), size_t(16));
}

// ---------------- TempProfile ----------------

TEST(core_temp_profile, interpolates_segments_and_holds_last) {
    TempProfile p;
    p.seg_count = 2;
    p.segments[0] = {100, 10};
    p.segments[1] = {100, 5};
    CHECK_EQ(p.status_text(), std::string("未启动"));
    CHECK(p.enabled(2) && !p.enabled(3));
    const int64_t t0 = 5000;
    p.start(20, t0);
    CHECK(p.running());
    CHECK_EQ(p.status_text(), std::string("第 1/2 段"));
    CHECK_NEAR(p.current_target(t0), 20, 1e-9);
    CHECK_NEAR(p.current_target(t0 + 5 * 60000), 60, 1e-9);
    CHECK_EQ(p.active_segment(), 1);
    CHECK_EQ(p.status_text(), std::string("第 1/2 段 · 剩 5 分"));
    CHECK_NEAR(p.current_target(t0 + 12 * 60000), 100, 1e-9);
    CHECK_EQ(p.active_segment(), 2);
    CHECK(!p.is_complete(t0 + 12 * 60000));
    CHECK_NEAR(p.total_minutes(), 15, 1e-9);
    CHECK_NEAR(p.current_target(t0 + 16 * 60000), 100, 1e-9);
    CHECK_EQ(p.active_segment(), 0);
    CHECK_EQ(p.status_text(), std::string("已完成 · 保持 100 ℃"));
    CHECK(p.is_complete(t0 + 15 * 60000));
    p.stop();
    CHECK_EQ(p.status_text(), std::string("已停止"));
    CHECK_NEAR(p.elapsed_min(t0 + 60000), 0, 1e-12);
}

TEST(core_temp_profile, should_write_debounce_and_force_rewrite) {
    TempProfile p;
    p.start(20, 0);
    CHECK(p.should_write(20, 0));
    p.mark_written(20, 0);
    CHECK(!p.should_write(20.05, 1000));
    CHECK(p.should_write(20.1, 1000));
    CHECK(!p.should_write(20, 29999));
    CHECK(p.should_write(20, 30000));
}

// ---------------- HeatApproach ----------------

TEST(core_heat_approach, delta_zero_or_already_near_goes_straight_to_hold) {
    HeatApproach a;
    ApproachParams p;
    p.delta = 0;
    CHECK_NEAR(a.start(100, 25, 0, p), 100, 1e-9);
    CHECK(a.phase() == ApproachPhase::Hold);
    CHECK_EQ(a.status_text(), std::string("恒温 100.0℃"));
    ApproachParams q;
    a.start(100, 100.5, 0, q);  // 已在目标之上
    CHECK(a.phase() == ApproachPhase::Hold);
}

TEST(core_heat_approach, far_target_settles_at_target_minus_delta_then_creeps_to_hold) {
    HeatApproach a;
    ApproachParams p;  // Δ 5、判稳 ±0.2 / 90 s、快段 1 ℃/min、末段 1 ℃ @ 0.2 ℃/min
    double sp = a.start(100, 25, 0, p);
    CHECK_NEAR(sp, 95, 1e-9);
    CHECK(a.phase() == ApproachPhase::Settle);
    CHECK_EQ(a.status_text(), std::string("预热至 95.0℃"));
    CHECK(a.should_write(0));
    a.mark_written(sp, 0);
    CHECK(!a.should_write(1000));

    // PV 停在 95.0：在判稳带内 90 s → 预热结束，进入逼近
    int64_t t = 0;
    ApproachEvent ev = ApproachEvent::None;
    for (int i = 1; i <= 100 && ev != ApproachEvent::PreheatDone; i++) {
        t = i * 1000;
        ev = a.step(t, 95.0, 50, p);
    }
    CHECK(ev == ApproachEvent::PreheatDone);
    CHECK(a.phase() == ApproachPhase::Creep);
    CHECK_NEAR(a.preheat_overshoot(), 0, 1e-9);

    // PV 紧跟 SP：快段 1 ℃/min 推到 99，末段 0.2 ℃/min 推到 100 → 恒温
    for (int i = 0; i < 20000 && a.phase() != ApproachPhase::Hold; i++) {
        t += 1000;
        a.step(t, a.current_sp(), 50, p);
    }
    CHECK(a.phase() == ApproachPhase::Hold);
    CHECK_NEAR(a.sp_to_write(), 100, 1e-9);
}

TEST(core_heat_approach, over_limit_reported_after_10s_with_heater_off) {
    HeatApproach a;
    ApproachParams p;
    p.delta = 0;
    a.start(100, 25, 0, p);
    CHECK(a.step(1000, 100.5, 0, p) == ApproachEvent::None);
    CHECK(a.step(10999, 100.5, 0, p) == ApproachEvent::None);
    CHECK(a.step(11000, 100.5, 0, p) == ApproachEvent::OverLimit);
    CHECK(a.step(12000, 100.5, 0, p) == ApproachEvent::None);  // 只报一次
    CHECK_EQ(a.status_text(), std::string("恒温 100.0℃ · 放热超出控温能力"));
    a.stop();
    CHECK(!a.active());
    CHECK(a.step(13000, 100.5, 0, p) == ApproachEvent::None);
}

TEST(core_heat_approach, sp_to_write_floors_to_0_1) {
    HeatApproach a;
    ApproachParams p;
    p.delta = 5.05;
    CHECK_NEAR(a.start(100, 25, 0, p), 94.9, 1e-9);  // floor1(94.95) = 94.9
}
