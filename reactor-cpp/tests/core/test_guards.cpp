// C#: tests/ParallelReactor.Tests/HeatGuardTests.cs、GasGuardTests.cs、ProgramConcurrencyTests.cs（GasLeasePerChannelTests 的 GasGuard 部分）
// 急停分支已删除（设计文档 §6），对应的断言（EstopActive 文案、「急停优先」顺序）不翻译，其余照抄。
#include <string>
#include <vector>

#include "core/gas_guard.hpp"
#include "core/heat_guard.hpp"
#include "core/units.hpp"
#include "testing.hpp"

using namespace reactor::core;
using RS = ReactorState;
namespace oc = reactor::core::op_codes;

namespace {
HeatGuardInput heat_ok() {
    HeatGuardInput s;
    s.id = 3;
    s.temp_configured = true;
    s.temp_online = true;
    s.stop_heat_on_over_pressure = true;
    s.over_temp = 250;
    s.over_pressure = 1000;
    return s;
}
GuardReject R(const char* c, const std::string& m) { return GuardReject{c, m}; }
}  // namespace

TEST(core_heat_guard, all_clear) { CHECK(!heat_guard_check(heat_ok()).has_value()); }

TEST(core_heat_guard, each_branch_code_and_text) {
    auto s = heat_ok();
    s.temp_configured = false;
    CHECK_EQ(heat_guard_check(s).value(), R(oc::kHwNotConfigured, "温控未配置串口（PR_TEMP_PORT），无法加热"));
    s = heat_ok();
    s.temp_online = false;
    CHECK_EQ(heat_guard_check(s).value(), R(oc::kHwOffline, "温控离线（AI-8 通讯失败），无法启动加热"));
    s = heat_ok();
    s.over_temp_tripped = true;
    CHECK_EQ(heat_guard_check(s).value(), R(oc::kChannelAlarm, "3 号 超温切断未解除（需低于 248℃），不可启动加热"));
    s.over_temp = 249.5;
    CHECK_EQ(heat_guard_check(s).value(), R(oc::kChannelAlarm, "3 号 超温切断未解除（需低于 247.5℃），不可启动加热"));
    s = heat_ok();
    s.over_pressure_tripped = true;
    CHECK_EQ(heat_guard_check(s).value(),
             R(oc::kChannelAlarm, "3 号 超压联锁未解除（需低于 " + format_p(980, PressureUnit::MPa) + "），不可启动加热"));
    CHECK_EQ(format_p(980, PressureUnit::MPa), std::string("6.757 MPa"));
    s.pressure_unit = PressureUnit::Psi;
    CHECK_EQ(heat_guard_check(s).value().message, std::string("3 号 超压联锁未解除（需低于 980.0 psi），不可启动加热"));
}

TEST(core_heat_guard, over_pressure_only_when_stop_heat_enabled) {
    auto s = heat_ok();
    s.over_pressure_tripped = true;
    s.stop_heat_on_over_pressure = false;
    CHECK(!heat_guard_check(s).has_value());
}

TEST(core_heat_guard, channel_checks_skipped_without_id) {
    auto s = heat_ok();
    s.id.reset();
    s.over_temp_tripped = true;
    s.over_pressure_tripped = true;
    CHECK(!heat_guard_check(s).has_value());
}

TEST(core_heat_guard, order_config_then_online_then_over_temp) {
    auto all = heat_ok();
    all.temp_configured = false;
    all.temp_online = false;
    all.over_temp_tripped = true;
    all.over_pressure_tripped = true;
    CHECK_EQ(heat_guard_check(all).value().code, std::string(oc::kHwNotConfigured));
    all.temp_configured = true;
    CHECK_EQ(heat_guard_check(all).value().code, std::string(oc::kHwOffline));
    all.temp_online = true;
    CHECK(heat_guard_check(all).value().message.find("超温") != std::string::npos);
}

// ---------------- GasGuard ----------------

namespace {
std::vector<GasChannel> idle8() {
    std::vector<GasChannel> v;
    for (int i = 1; i <= 8; i++) v.push_back({i, RS::Idle, false, false});
    return v;
}
GasGuardInput gas_ok() {
    GasGuardInput s;
    s.io_configured = true;
    s.need_pressure = true;
    s.analog_configured = true;
    s.press_online = true;
    s.channels = idle8();
    return s;
}
GasGuardInput with(std::initializer_list<GasChannel> changed) {
    auto s = gas_ok();
    for (auto& c : changed) s.channels[static_cast<size_t>(c.id - 1)] = c;
    return s;
}
std::string code_of(const GasGuardInput& s) {
    auto r = gas_guard_check(s);
    return r ? r->code : std::string();
}
}  // namespace

TEST(core_gas_guard, all_clear) { CHECK(!gas_guard_check(gas_ok()).has_value()); }

TEST(core_gas_guard, each_branch_code_and_text) {
    auto s = gas_ok();
    s.io_configured = false;
    CHECK_EQ(gas_guard_check(s).value(), R(oc::kHwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法执行气路操作"));
    s = gas_ok();
    s.analog_configured = false;
    CHECK_EQ(gas_guard_check(s).value(),
             R(oc::kHwNotConfigured, "压力采集未配置串口（PR_PRESS_PORT），无法闭环充压与超压监视"));
    s = gas_ok();
    s.press_online = false;
    CHECK_EQ(gas_guard_check(s).value(), R(oc::kHwOffline, "压力采集离线（8AI 通讯失败），请检查接线与串口后再试"));
    s = gas_ok();
    s.gas_task = "泄漏测试";
    CHECK_EQ(gas_guard_check(s).value(), R(oc::kGasBusy, "泄漏测试进行中，请等待完成或先停止"));
    s = gas_ok();
    s.manual_valve = true;
    CHECK_EQ(gas_guard_check(s).value(),
             R(oc::kManualValveActive, "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行"));
    CHECK_EQ(gas_guard_check(with({{2, RS::Alarm, true, false}, {5, RS::Done, true, false}})).value(),
             R(oc::kChannelAlarm, "2、5 号 超压未解除（需低于超压报警值的 98%），禁止气路操作"));
    CHECK_EQ(gas_guard_check(with({{1, RS::Pressing, false, false}, {4, RS::Alarm, false, false}})).value(),
             R(oc::kChannelPressurized, "1、4 号 正在加压 / 反应 / 报警，需先停止"));
}

TEST(core_gas_guard, pressure_checks_skipped_when_not_needed) {
    auto s = gas_ok();
    s.need_pressure = false;
    s.analog_configured = false;
    s.press_online = false;
    CHECK(!gas_guard_check(s).has_value());
}

TEST(core_gas_guard, self_id_exempts_pressing_and_react_not_alarm) {
    auto pressing = with({{3, RS::React, false, false}});
    CHECK(gas_guard_check(pressing).has_value());
    pressing.self_id = 3;
    CHECK(!gas_guard_check(pressing).has_value());
    auto alarm = with({{3, RS::Alarm, false, false}});
    alarm.self_id = 3;
    CHECK_EQ(code_of(alarm), std::string(oc::kChannelPressurized));
}

TEST(core_gas_guard, idle_over_temp_alarm_does_not_block) {
    CHECK(!gas_guard_check(with({{6, RS::Alarm, false, true}})).has_value());
}

TEST(core_gas_guard, program_wait_skips_busy_only_for_program_holder) {
    auto held = gas_ok();
    held.gas_task = "1 号 程序 · 升压";
    held.gas_held_by_program = true;
    CHECK_EQ(code_of(held), std::string(oc::kGasBusy));
    held.wait_instead_of_busy = true;
    CHECK(!gas_guard_check(held).has_value());
    auto leak = gas_ok();
    leak.gas_task = "泄漏测试";
    leak.gas_held_by_program = false;
    leak.wait_instead_of_busy = true;
    CHECK_EQ(gas_guard_check(leak).value(), R(oc::kGasBusy, "泄漏测试进行中，请等待完成或先停止"));
}

TEST(core_gas_guard, other_program_channels_pressing_or_react_not_blocking_alarm_still_blocks) {
    auto i = gas_ok();
    i.channels[1] = {2, RS::React, false, false};
    i.channels[2] = {3, RS::Pressing, false, false};
    i.self_id = 1;
    CHECK_EQ(code_of(i), std::string(oc::kChannelPressurized));
    auto j = i;
    j.program_owned_ids = {2, 3};
    CHECK(!gas_guard_check(j).has_value());
    auto k = i;
    k.program_owned_ids = {2};
    CHECK_EQ(code_of(k), std::string(oc::kChannelPressurized));  // RV3 不是程序通道
    j.channels[1] = {2, RS::Alarm, false, false};
    auto r = gas_guard_check(j);
    CHECK_EQ(r.value().code, std::string(oc::kChannelPressurized));
    CHECK(r.value().message.find("2 号") != std::string::npos);
}

TEST(core_gas_guard, program_wait_other_channel_alarm_does_not_block_over_pressure_still_blocks) {
    auto prog = gas_ok();
    prog.channels[2] = {3, RS::Alarm, false, false};
    prog.self_id = 1;
    prog.wait_instead_of_busy = true;
    CHECK(!gas_guard_check(prog).has_value());
    auto leak = prog;
    leak.wait_instead_of_busy = false;
    CHECK_EQ(code_of(leak), std::string(oc::kChannelPressurized));
    prog.channels[2] = {3, RS::Alarm, true, false};
    CHECK_EQ(code_of(prog), std::string(oc::kChannelAlarm));
    prog.channels[2] = {3, RS::Idle, false, false};
    prog.channels[0] = {1, RS::Alarm, false, false};
    CHECK_EQ(code_of(prog), std::string(oc::kChannelPressurized));
}

TEST(core_gas_guard, program_gas_pending_blocks_non_program_requesters_only) {
    auto pending = gas_ok();
    pending.program_gas_pending = "1 号 程序 · 稳压";
    CHECK_EQ(gas_guard_check(pending).value(), R(oc::kGasBusy, "1 号 程序 · 稳压进行中，请等待完成或先停止"));
    auto other = pending;
    other.wait_instead_of_busy = true;
    other.self_id = 2;
    CHECK(!gas_guard_check(other).has_value());
    auto empty = gas_ok();
    empty.program_gas_pending = "";
    CHECK(!gas_guard_check(empty).has_value());
    auto busy = pending;
    busy.gas_task = "泄漏测试";
    CHECK_EQ(gas_guard_check(busy).value().message, std::string("泄漏测试进行中，请等待完成或先停止"));
}
