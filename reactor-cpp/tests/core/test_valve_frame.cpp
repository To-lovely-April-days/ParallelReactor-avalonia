// ValveFrame / PurgePlan / LeakMath 和下发前检查。C# 里对应的断言散在 StepEngineTests（帧必须 IsSafe、气源与排空帧计数），
// 那些依赖程序引擎，这里按 Models/GasSequence.cs 与 MainViewModel.Gas.cs 的规则直接测。
#include <string>

#include "core/units.hpp"
#include "core/num_format.hpp"
#include "core/reactor_state.hpp"
#include "core/valve_frame.hpp"
#include "testing.hpp"

using namespace reactor::core;
namespace vf = reactor::core::valve_frame;

TEST(core_valve_frame, builders_and_safety) {
    auto c = vf::all_closed();
    for (bool b : c) CHECK(!b);
    auto g = vf::gas(1, {1, 3, 9, 0});
    CHECK(g[0] && g[2] && !g[1] && g[vf::kGasBase + 1] && !g[vf::kVent]);
    CHECK(vf::gas(7)[vf::kGasBase + 2]);   // 越界夹到 2
    CHECK(vf::gas(-1)[vf::kGasBase]);      // 越界夹到 0
    CHECK(vf::vent_only({2})[1] && vf::vent_only()[vf::kVent]);
    CHECK(vf::is_safe(c) && vf::is_safe(g) && vf::is_safe(vf::vent_only({1})));
    auto two = vf::gas(0);
    two[vf::kGasBase + 1] = true;
    CHECK(!vf::is_safe(two));             // 两路气源
    auto gv = vf::gas(2);
    gv[vf::kVent] = true;
    CHECK(!vf::is_safe(gv));              // 气源 + 排空
}

TEST(core_valve_frame, check_apply_rejects_unsafe_and_foreign_sv) {
    auto gv = vf::gas(0);
    gv[vf::kVent] = true;
    CHECK_EQ(vf::check_apply(gv, false, {}, "").value_or(""),
             std::string("阀态不安全（多路气源同开，或气源与排空同开），已拒绝下发"));
    CHECK(!vf::check_apply(vf::gas(0, {1, 2}), true, {1, 2}, "泄漏测试").has_value());
    CHECK_EQ(vf::check_apply(vf::gas(0, {1, 3}), true, {1}, "RV1 程序 · 升压").value_or(""),
             std::string("阀态越权：SV3 不属于「RV1 程序 · 升压」的参与通道，已拒绝下发"));
    CHECK(!vf::check_apply(vf::gas(0, {3}), false, {}, "").has_value());  // 无租约：只查安全
}

TEST(core_valve_frame, dead_time_between_source_switches) {
    CHECK(vf::needs_dead_time(vf::gas(0), vf::vent_only()));        // 气源 → 排空
    CHECK(vf::needs_dead_time(vf::vent_only(), vf::gas(1)));        // 排空 → 气源
    CHECK(vf::needs_dead_time(vf::gas(0), vf::gas(1)));             // 换气源
    CHECK(!vf::needs_dead_time(vf::gas(0), vf::gas(0, {1})));       // 同一气源加开 SV
    CHECK(!vf::needs_dead_time(vf::all_closed(), vf::gas(2)));      // 从全关开始
    CHECK(!vf::needs_dead_time(vf::gas(0), vf::all_closed()));      // 关掉
    CHECK(!vf::needs_dead_time(vf::gas(0, {1}), vf::all_closed()));
}

TEST(core_valve_frame, close_all_keeps_vent_on_over_pressure_abort) {
    auto f = vf::close_all(true, true);
    CHECK(f[vf::kVent]);
    for (int i = 0; i < vf::kVent; i++) CHECK(!f[static_cast<size_t>(i)]);
    CHECK(!vf::close_all(true, false)[vf::kVent]);
    CHECK(!vf::close_all(false, true)[vf::kVent]);
}

TEST(core_valve_frame, names_and_change_text) {
    CHECK_EQ(vf::valve_name(0), std::string("SV1"));
    CHECK_EQ(vf::valve_name(8), std::string("惰性气体"));
    CHECK_EQ(vf::valve_name(10), std::string("气体B"));
    CHECK_EQ(vf::valve_name(11), std::string("排空"));
    CHECK(!vf::change_text(vf::gas(0), vf::gas(0), "x").has_value());
    CHECK_EQ(vf::change_text(vf::all_closed(), vf::gas(1, {2}), "RV2 程序 · 升压").value_or(""),
             std::string("SV2开 气体A开 · 当前开启：SV2、气体A · RV2 程序 · 升压"));
    CHECK_EQ(vf::change_text(vf::vent_only(), vf::all_closed(), "").value_or(""), std::string("排空关 · 当前开启：无"));
}

TEST(core_purge_plan, build_inserts_dead_time_around_every_switch) {
    CHECK_EQ(purge_plan::gas_index("气体 A"), 1);
    CHECK_EQ(purge_plan::gas_index("气体 B"), 2);
    CHECK_EQ(purge_plan::gas_index("惰性气体"), 0);
    auto plan = purge_plan::build(1, 3, 5, 4);
    CHECK_EQ(plan.size(), size_t(1 + 3 * 4));
    CHECK_EQ(plan[0].phase, std::string("准备"));
    CHECK_NEAR(plan[0].seconds, purge_plan::kDeadSec, 1e-12);
    CHECK_EQ(plan[1].phase, std::string("充气"));
    CHECK(plan[1].valves[vf::kGasBase + 1]);
    CHECK_NEAR(plan[1].seconds, 5, 1e-12);
    CHECK_EQ(plan[3].phase, std::string("排气"));
    CHECK_NEAR(plan[3].seconds, 4, 1e-12);
    CHECK_EQ(plan.back().cycle, 3);
    for (size_t i = 0; i < plan.size(); i++) {
        CHECK(vf::is_safe(plan[i].valves));
        for (int sv = 0; sv < 8; sv++) CHECK(!plan[i].valves[static_cast<size_t>(sv)]);  // 只吹扫总管
        if (i > 0) CHECK(!vf::needs_dead_time(plan[i - 1].valves, plan[i].valves));      // 切换都隔了全关
    }
}

TEST(core_leak_math, compensate_and_drop_rate) {
    CHECK_NEAR(leak_math::compensate(50, 30, 30), 50, 1e-12);
    double p_abs = (50 + leak_math::kAtmPsi) * (28 + 273.15) / (30 + 273.15);
    CHECK_NEAR(leak_math::compensate(p_abs - leak_math::kAtmPsi, 28, 30), 50, 1e-9);
    CHECK_EQ(leak_math::drop_rate_per_hour({{0, 1}}), 0.0);
    CHECK_EQ(leak_math::drop_rate_per_hour({{1, 1}, {1, 2}}), 0.0);
    CHECK_NEAR(leak_math::drop_rate_per_hour({{0, 50}, {1, 49.9}, {2, 49.8}}), 6.0, 1e-9);  // 每分钟降 0.1 → 6 psi/h
}

// ---------------- 小工具：格式化 / 单位 / 状态码 ----------------

TEST(core_num_format, csharp_custom_format_equivalents) {
    CHECK_EQ(fmt_dec(248, 1), std::string("248"));
    CHECK_EQ(fmt_dec(247.5, 1), std::string("247.5"));
    CHECK_EQ(fmt_dec(0.25, 1), std::string("0.3"));      // .NET 远离零舍入
    CHECK_EQ(fmt_dec(-0.25, 1), std::string("-0.3"));
    CHECK_EQ(fmt_dec(-0.01, 1), std::string("0"));
    CHECK_EQ(fmt_dec(95, 1, 1), std::string("95.0"));
    CHECK_EQ(fmt_dec(9.995, 2), std::string("10"));      // 15 位有效数字后再舍：9.995 → 10
    CHECK_EQ(fmt_dec(0.999999, 4, 4), std::string("1.0000"));
    CHECK_EQ(fmt_dec(1234567.891, 2), std::string("1234567.89"));
    CHECK_EQ(fmt_dec(1e20, 0), std::string("100000000000000000000"));
    CHECK_EQ(fmt_dec(0.000123, 3), std::string("0"));
    CHECK_EQ(fmt_dec(0.0006, 3), std::string("0.001"));
    CHECK_EQ(fmt_dec(0.01, 15), std::string("0.01"));
}

TEST(core_units, pressure_conversion_and_format) {
    CHECK(parse_pressure_unit("psi") == PressureUnit::Psi);
    CHECK(parse_pressure_unit("bar") == PressureUnit::Bar);
    CHECK(parse_pressure_unit("xx") == PressureUnit::MPa);
    CHECK_EQ(format_p(50, PressureUnit::Psi), std::string("50.0 psi"));
    CHECK_EQ(format_p(50.0377, PressureUnit::MPa), std::string("0.345 MPa"));
    CHECK_EQ(format_p(50.0377, PressureUnit::Bar), std::string("3.45 bar"));
    CHECK_NEAR(unit_to_psi(psi_to_unit(123.4, PressureUnit::Bar), PressureUnit::Bar), 123.4, 1e-9);
    CHECK_NEAR(mpa_to_psi(1), 145.037738, 1e-9);
    CHECK_NEAR(psi_to_mpa(145.037738), 1, 1e-12);
    CHECK_EQ(pressure_decimals(PressureUnit::MPa), 3);
}

TEST(core_reactor_state, codes_and_zh) {
    CHECK_EQ(std::string(state_code(ReactorState::Pressing)), std::string("pressing"));
    CHECK(parse_state_code("cooling") == ReactorState::Cooling);
    CHECK(!parse_state_code("bogus").has_value());
    CHECK_EQ(std::string(state_zh(ReactorState::Alarm, true)), std::string("超温"));
    CHECK_EQ(std::string(state_zh(ReactorState::Alarm)), std::string("超压"));
    CHECK_EQ(std::string(state_zh(ReactorState::Idle)), std::string("停用"));
    CHECK(state_is_running(ReactorState::Ready) && !state_is_running(ReactorState::Done));
    CHECK(state_is_active(ReactorState::Stir) && !state_is_active(ReactorState::Ready));
}
