// C#: tests/ParallelReactor.Tests/ProgramRunGuardTests.cs（含 ProgramRunGuardCodeTests）、ProgramConcurrencyTests.cs 的 ProgramRunSetTests
#include "core/program/program_run_guard.hpp"
#include "core/program/program_run_info.hpp"
#include "testing.hpp"

using namespace reactor::core;
using namespace reactor::core::program;
using program_run_guard::check;
using program_run_guard::reason;

namespace {

ProgramRunInput ok() {
    ProgramRunInput i;
    i.has_channel = true;
    i.step_count = 3;
    i.state = ReactorState::Ready;
    i.gas_task = "";
    return i;
}

template <class F>
ProgramRunInput ok_with(F f) {
    ProgramRunInput i = ok();
    f(i);
    return i;
}

bool has(const std::optional<std::string>& r, const char* part) { return r && r->find(part) != std::string::npos; }

}  // namespace

TEST(core_program_run_guard, AllClear_ReadyAndDoneAccepted) {
    CHECK(!reason(ok()).has_value());
    CHECK(!reason(ok_with([](auto& i) { i.state = ReactorState::Done; })).has_value());
}

TEST(core_program_run_guard, EachReason) {
    CHECK(has(reason(ok_with([](auto& i) { i.has_channel = false; })), "未选中"));
    CHECK(has(reason(ok_with([](auto& i) { i.program_running = true; })), "已有程序"));
    CHECK(has(reason(ok_with([](auto& i) { i.step_count = 0; })), "空程序"));
    CHECK(has(reason(ok_with([](auto& i) { i.estop_locked = true; })), "急停"));
    CHECK(has(reason(ok_with([](auto& i) { i.state = ReactorState::Alarm; })), "报警"));
    CHECK(has(reason(ok_with([](auto& i) { i.over_temp_tripped = true; })), "超温"));
    CHECK(has(reason(ok_with([](auto& i) { i.over_pressure_tripped = true; })), "超压"));
    CHECK(has(reason(ok_with([](auto& i) { i.state = ReactorState::Idle; })), "停用"));
    CHECK(has(reason(ok_with([](auto& i) { i.state = ReactorState::Heating; })), "正在运行"));
    CHECK(has(reason(ok_with([](auto& i) { i.state = ReactorState::Pressing; })), "正在运行"));
}

TEST(core_program_run_guard, ProgramRunning_IsPerChannel_AndConcurrencyCap) {
    CHECK_EQ(*reason(ok_with([](auto& i) { i.program_running = true; })), std::string("该通道已有程序在执行 · 请等待完成或先中止"));
    CHECK(!reason(ok_with([](auto& i) { i.running_count = 7; })).has_value());   // 其他通道在跑不阻塞本通道
    auto full = check(ok_with([](auto& i) { i.running_count = program_run_set::kMaxConcurrent; }));
    CHECK_EQ(full->code, std::string(op_codes::kProgramRunning));
    CHECK(full->message.find("并行程序已达 8 个") != std::string::npos);
}

TEST(core_program_run_guard, GasBusy_BlocksEvenWithoutGasSteps) {
    auto r = reason(ok_with([](auto& i) { i.gas_busy = true; i.gas_task = "泄漏测试"; i.needs_gas = false; }));
    CHECK_EQ(*r, std::string("泄漏测试进行中，请等待完成或先停止"));
    CHECK_EQ(*reason(ok_with([](auto& i) { i.gas_busy = true; })), std::string("气路序列进行中，请等待完成或先停止"));
}

TEST(core_program_run_guard, GasBlock_OnlyWhenGasNeeded) {
    CHECK(!reason(ok_with([](auto& i) { i.gas_block = "阀门未配置"; i.needs_gas = false; })).has_value());
    CHECK_EQ(*reason(ok_with([](auto& i) { i.gas_block = "阀门未配置"; i.needs_gas = true; })), std::string("阀门未配置"));
}

TEST(core_program_run_guard, LimitErrors_ReportFirst) {
    CHECK_EQ(*reason(ok_with([](auto& i) { i.limit_errors = {"第 01 步 升温：x"}; })), std::string("第 01 步 升温：x"));
    auto r = reason(ok_with([](auto& i) { i.limit_errors = {"A", "B", "C"}; }));
    CHECK(r->rfind("A", 0) == 0);
    CHECK(has(r, "另有 2 项"));
    CHECK(!reason(ok_with([](auto& i) { i.limit_errors = {}; })).has_value());
}

TEST(core_program_run_guard, Precedence) {
    auto all = ok_with([](auto& i) {
        i.program_running = true; i.step_count = 0; i.estop_locked = true; i.state = ReactorState::Alarm;
        i.gas_busy = true; i.gas_task = "泄漏测试"; i.needs_gas = true; i.gas_block = "g"; i.limit_errors = {"L"};
    });
    CHECK(has(reason(all), "已有程序"));
    all.program_running = false;
    CHECK(has(reason(all), "空程序"));
    all.step_count = 2;
    CHECK(has(reason(all), "急停"));
    all.estop_locked = false;
    CHECK(has(reason(all), "报警"));
    auto s = all;
    s.state = ReactorState::Ready;
    CHECK(has(reason(s), "泄漏测试"));
    auto s2 = s; s2.gas_busy = false;
    CHECK_EQ(*reason(s2), std::string("g"));
    auto s3 = s2; s3.gas_block.reset();
    CHECK_EQ(*reason(s3), std::string("L"));
    auto s4 = s; s4.state = ReactorState::Heating;   // 状态（运行中）优先于气路
    CHECK(has(reason(s4), "正在运行"));
}

TEST(core_program_run_guard, AllClear_IsNull) { CHECK(!check(ok()).has_value()); }

TEST(core_program_run_guard, EachBranch_HasCode) {
    using namespace op_codes;
    const std::pair<ProgramRunInput, const char*> rows[] = {
        {ok_with([](auto& i) { i.has_channel = false; }), kChannelNotFound},
        {ok_with([](auto& i) { i.program_running = true; }), kProgramRunning},
        {ok_with([](auto& i) { i.step_count = 0; }), kProgramInvalid},
        {ok_with([](auto& i) { i.estop_locked = true; }), kEstopActive},
        {ok_with([](auto& i) { i.state = ReactorState::Alarm; }), kChannelAlarm},
        {ok_with([](auto& i) { i.over_temp_tripped = true; }), kChannelAlarm},
        {ok_with([](auto& i) { i.state = ReactorState::Idle; }), kChannelStateInvalid},
        {ok_with([](auto& i) { i.state = ReactorState::Heating; }), kChannelBusy},
        {ok_with([](auto& i) { i.gas_busy = true; i.gas_task = "泄漏测试"; }), kGasBusy},
        {ok_with([](auto& i) { i.limit_errors = {"升温 超限"}; }), kParamOutOfRange},
    };
    for (const auto& r : rows) {
        auto c = check(r.first);
        CHECK(c.has_value());
        if (!c) continue;
        CHECK_EQ(c->code, std::string(r.second));
        CHECK_EQ(c->message, *reason(r.first));   // 文字与 reason 同源
    }
}

TEST(core_program_run_guard, GasBlock_CarriesGuardCode_DefaultsToGasBusy) {
    auto i = ok_with([](auto& x) { x.needs_gas = true; x.gas_block = "阀门离线"; x.gas_block_code = op_codes::kHwOffline; });
    CHECK_EQ(check(i)->code, std::string(op_codes::kHwOffline));
    i.gas_block_code.reset();
    CHECK_EQ(check(i)->code, std::string(op_codes::kGasBusy));
}

// ============ ProgramRunSetTests ============

namespace {
program_run_set::Runs runs(std::initializer_list<std::pair<int, ProgramRunPhase>> xs) {
    program_run_set::Runs d;
    for (const auto& x : xs) {
        ProgramRunInfo r(x.first, 3, "local", 0);
        r.phase = x.second;
        d.emplace(x.first, r);
    }
    return d;
}
}  // namespace

TEST(core_program_run_set, Empty) {
    auto r = runs({});
    CHECK(!program_run_set::is_owned(r, 1));
    CHECK(program_run_set::first_running(r) == nullptr);
    CHECK_EQ(program_run_set::summary(r), std::string(""));
    CHECK(program_run_set::running_ids(r).empty());
}

TEST(core_program_run_set, SingleAndMultiple) {
    auto r = runs({{3, ProgramRunPhase::Running}, {1, ProgramRunPhase::WaitingGas}, {5, ProgramRunPhase::Completed}});
    CHECK(program_run_set::is_owned(r, 1));
    CHECK(program_run_set::is_owned(r, 3));
    CHECK(!program_run_set::is_owned(r, 5));   // 已结束不算占用
    CHECK(!program_run_set::is_owned(r, 2));
    CHECK_EQ(program_run_set::first_running(r)->rv, 1);
    CHECK_EQ(program_run_set::summary(r), std::string("正在运行：1、3 号"));
    CHECK(program_run_set::running_ids(r) == (std::vector<int>{1, 3}));
}

TEST(core_program_run_set, PhaseNames_AreCamelCase) {
    CHECK_EQ(std::string(program_run_set::phase_name(ProgramRunPhase::WaitingGas)), std::string("waitingGas"));
    CHECK_EQ(std::string(program_run_set::phase_name(ProgramRunPhase::Running)), std::string("running"));
    ProgramRunInfo a(1, 1, "local", 0);
    a.phase = ProgramRunPhase::Finishing;
    CHECK(a.is_active());
    a.phase = ProgramRunPhase::Aborted;
    CHECK(!a.is_active());
    a.phase = ProgramRunPhase::WaitingGas;
    CHECK(a.waiting_gas());
    CHECK_EQ(a.phase_text, std::string("准备执行"));
    CHECK_EQ(a.index, -1);
}
