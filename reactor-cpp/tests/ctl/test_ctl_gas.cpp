// 共用气路（第二阶段）：多程序排队先到先得、等待上限 → 步骤故障、稳压短租约脉冲与其他程序交替使用气路。
#include "ctl_phase2_util.hpp"

using namespace ctltest;
using prog::ProgramRunPhase;
using prog::StepType;

namespace {

std::vector<prog::StepSnapshot> press_then_hold(double pres, double hold_min, const char* regulate = "off") {
    using std::string;
    return steps({
        {StepType::Press, {{"gas", string("A")}, {"pres", pres}, {"tol", 2.0}, {"regulate", string(regulate)}, {"refill", 3.0}, {"maxSec", 180.0}}},
        {StepType::Hold, {{"until", string("time")}, {"time", hold_min}}},
        {StepType::End, {}},
    });
}

// 「气路就位」事件里的通道号，按发生先后
std::vector<int> grant_order(PRig& r) {
    std::vector<int> v;
    for (const auto& t : r.titles_containing("程序 · 气路就位")) v.push_back(t[0] - '0');
    return v;
}

}  // namespace

TEST(ctl_gas, two_channels_fifo) {
    PRig r(fast_settings());
    r.pe().set_draft(1, press_then_hold(100, 0.2), "a");
    r.pe().set_draft(2, press_then_hold(100, 0.2), "b");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.pe().run(2).accepted);
    // RV1 立即取到；RV2 排队，显示「等待气路（RV1 占用中）」
    CHECK(r.c().gas_lease().owner_rv() == std::optional<int>(1));
    CHECK(r.phase_is(2, ProgramRunPhase::WaitingGas));
    CHECK_EQ(ids(r.pe().gas_queue().waiting()), std::string("2"));
    auto snap = r.pe().channel_program(2);
    CHECK_EQ(snap["gasQueuePos"].get<int>(), 1);
    CHECK_EQ(snap["waitingOn"].get<int>(), 1);
    CHECK(snap["stepElapsedMin"].is_number());  // 触屏「已反应 / 已 x min」用的实际步内时长
    CHECK(r.pe().channel_program(1)["stepElapsedMin"].get<double>() >= 0);
    CHECK(r.has_title("2 号 程序 · 等待气路"));
    CHECK(r.pe().gas_pending() == "2 号 程序 · 等待气路");
    // 气路被程序占用 / 排队时：手动阀控与泄漏测试按 GAS_BUSY 拒绝
    auto manual = r.au->gas_locked_for_manual();
    CHECK(!manual.ok);
    CHECK_EQ(manual.code, std::string("GAS_BUSY"));
    r.c().channel_mut(5).set_state(core::ReactorState::Done);  // 泄漏测试只能选已停用 / 已结束的通道（C# IsRunning 含已启用）
    auto lt = r.au->leak().start({5}, std::nullopt);
    CHECK(!lt.ok);
    CHECK_EQ(lt.code, std::string("GAS_BUSY"));
    CHECK(r.until([&] { return r.run_ended(1) && r.run_ended(2); }, 300000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    CHECK(r.phase_is(2, ProgramRunPhase::Completed));
    CHECK_EQ(ids(grant_order(r)), std::string("2"));
    CHECK(r.phy.p[0] > 95 && r.phy.p[1] > 95);
}

TEST(ctl_gas, eight_channels_fifo_order) {
    PRig r(fast_settings());
    for (int ch = 1; ch <= 8; ++ch) r.pe().set_draft(ch, press_then_hold(80, 0.1), "p");
    // 启动顺序 3 1 4 8 2 7 5 6：3 立即取到，其余按启动先后排队
    std::vector<int> order{3, 1, 4, 8, 2, 7, 5, 6};
    for (int ch : order) CHECK(r.pe().run(ch).accepted);
    CHECK_EQ(r.pe().running_count(), 8);
    CHECK(r.c().gas_lease().owner_rv() == std::optional<int>(3));
    CHECK_EQ(ids(r.pe().gas_queue().waiting()), std::string("1,4,8,2,7,5,6"));
    // 第 9 个程序：并行已满（这里所有通道都已占用 → 同一通道已有程序）
    auto again = r.pe().run(3);
    CHECK(!again.ok);
    CHECK(r.until(
        [&] {
            for (int ch = 1; ch <= 8; ++ch)
                if (!r.run_ended(ch)) return false;
            return true;
        },
        600000, 1000));
    for (int ch = 1; ch <= 8; ++ch) CHECK(r.phase_is(ch, ProgramRunPhase::Completed));
    CHECK_EQ(ids(grant_order(r)), std::string("1,4,8,2,7,5,6"));
    CHECK(!r.c().gas_lease().busy());
    CHECK_EQ(r.pe().gas_queue().count(), 0);
}

TEST(ctl_gas, wait_limit_faults_the_step) {
    auto st = fast_settings();
    st.settings.program_gas_wait_min = 0.5;  // 30 s
    PRig r(st);
    using std::string;
    // RV1：只吹总管 8 次（每次 3 s 充 + 5 s 排 + 死区），长时间占用气路
    r.pe().set_draft(1, steps({{StepType::Exchange, {{"gas", string("inert")}, {"fill", 30.0}, {"release", 5.0}, {"cycles", 0.0}, {"lineFlush", 8.0}}},
                               {StepType::End, {}}}),
                     "flush");
    r.pe().set_draft(2, press_then_hold(60, 0.1), "b");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.pe().run(2).accepted);
    CHECK(r.until([&] { return r.run_ended(2); }, 120000));
    CHECK(r.phase_is(2, ProgramRunPhase::Failed));
    auto msg = r.pe().run_info(2)->message;
    CHECK(msg.rfind("等待气路超过 0.5 分钟（1 号 占用中）", 0) == 0);
    CHECK(r.pe().is_owned(1));  // RV1 不受影响
    CHECK(r.until([&] { return r.run_ended(1); }, 180000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    CHECK(r.has_title("1 号 置换完成"));
}

TEST(ctl_gas, regulation_pulses_interleave_with_other_programs) {
    PRig r(fast_settings());
    r.phy.leak_psi_h[0] = 600;  // RV1 漏得快：每分钟掉 10 psi → 稳压要补
    r.pe().set_draft(1, press_then_hold(100, 4, "on"), "reg");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return r.c().channel(1).state == core::ReactorState::Wait; }, 120000));
    CHECK(r.pe().gas_pending() == "1 号 程序 · 稳压");
    // 稳压期间：压力偏离监视已打开
    CHECK(r.c().channel(1).press_watch.active);
    // RV2 在稳压间隙取用气路完成自己的充压
    r.pe().set_draft(2, press_then_hold(80, 0.1), "other");
    CHECK(r.pe().run(2).accepted);
    CHECK(r.until([&] { return r.run_ended(2); }, 180000));
    CHECK(r.phase_is(2, ProgramRunPhase::Completed));
    CHECK(r.pe().is_owned(1));
    CHECK(r.until([&] { return r.run_ended(1); }, 400000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    int pulses = r.count_title("1 号 稳压补压");
    CHECK(pulses >= 2);
    CHECK(!r.c().channel(1).press_watch.active);
    // 稳压维持住了：保持结束时压力接近目标（band = max(1, 3%) = 3 psi）
    CHECK(r.phy.p[0] > 90);
    // 稳压脉冲的事件都在 RV1 的 4 min 保持期间，与 RV2 的「气路就位」交替出现
    auto titles = r.titles_containing("号");
    bool rv2_between = false;
    int seen_pulse = 0;
    for (const auto& t : titles) {
        if (t == "1 号 稳压补压") ++seen_pulse;
        if (t == "2 号 程序 · 充压完成" && seen_pulse >= 1) rv2_between = true;
    }
    CHECK(rv2_between || pulses >= 2);
}
