// 程序暂停 / 继续（决定记录 2026-11-29「暂停时全部停下」）：
//   暂停 → 本路加热 At=4、稳压停、进气关（气路步骤安全中止、只释放自己的租约）、程序的搅拌持有释放；步骤计时冻结；
//   继续 → 重开加热 / 搅拌 / 稳压，定时等待按剩余时间接着走；暂停时正在用气路的步骤从头重做。
#include "ctl_phase2_util.hpp"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <optional>
#include <string>
#include <vector>

using namespace ctltest;
using prog::ProgramRunPhase;
using prog::StepType;

namespace {

std::vector<prog::StepSnapshot> stir_heat_hold(double hold_min) {
    using std::string;
    return steps({
        {StepType::Stir, {{"rpm", 300.0}}},
        {StepType::Heat, {{"mode", string("fixed")}, {"temp", 40.0}, {"rate", 0.0}, {"band", 2.0}, {"wait", 0.1}, {"maxWait", 30.0}}},
        {StepType::Hold, {{"until", string("time")}, {"time", hold_min}}},
        {StepType::End, {}},
    });
}

bool all_closed(PRig& r) {
    auto v = r.valves();
    return std::none_of(v.begin(), v.end(), [](bool b) { return b; });
}

}  // namespace

TEST(ctl_program_pause, heat_stir_stop_and_timer_freezes) {
    PRig r(fast_settings());
    r.pe().set_draft(1, stir_heat_hold(2.0), "暂停测试");
    CHECK(r.pe().run(1, "local").accepted);
    // 进入保持步骤（第 3 步）
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 2; }, 10 * 60 * 1000, 1000));
    CHECK(r.c().stir().on());
    CHECK_EQ(r.c().stir().rpm(), 300);
    CHECK(static_cast<int>(r.hw->sim_main->holding(1, at_reg(1))) != 4);
    r.advance(30 * 1000);  // 保持已走 30 s

    auto p = r.pe().pause(1, "触屏");
    CHECK(p.ok);
    CHECK(!p.silent);
    CHECK(r.pe().is_paused(1));
    r.advance(3000);
    // 全部停下：加热 At=4、搅拌停（只有本程序持有）、阀全关
    CHECK_EQ(static_cast<int>(r.hw->sim_main->holding(1, at_reg(1))), 4);
    CHECK(!r.c().stir().on());
    CHECK_EQ(r.pe().stir_share().count(), 0);
    CHECK(all_closed(r));
    CHECK(r.c().channel(1).state == core::ReactorState::Wait);
    // 仍算「有动作」（全部停止 → 手动停止）、仍占用通道
    CHECK(r.c().channel_has_activity(1));
    CHECK(r.pe().is_owned(1));
    CHECK(r.has_title("1 号 程序暂停"));
    // 快照：state = paused + pausedAt
    auto snap = r.c().snapshot();
    CHECK(snap != nullptr);
    if (snap) {
        const auto& j = snap->channels[0].program;
        CHECK_EQ(j["state"].get<std::string>(), std::string("paused"));
        CHECK(j["paused"].get<bool>());
        CHECK(j["pausedAt"].is_string());
        CHECK_EQ(j["pausedBy"].get<std::string>(), std::string("触屏"));
        CHECK(j["running"].get<bool>());
        CHECK_EQ(snap->program_runs[0]["state"].get<std::string>(), std::string("paused"));
    }
    // 再暂停：幂等
    CHECK(r.pe().pause(1).silent);
    // 暂停 10 min：计时冻结，程序不结束、步骤不前进
    r.advance(10 * 60 * 1000);
    CHECK(r.pe().is_owned(1));
    CHECK_EQ(r.pe().run_info(1)->index, 2);
    double step_el = r.pe().channel_program(1)["stepElapsedMin"].get<double>();
    CHECK(step_el > 0.4 && step_el < 0.7);   // 只算暂停前的 ~0.5 min

    // 继续：重开加热、重新请求搅拌（300 rpm），保持按剩余 ~90 s 接着走
    auto rs = r.pe().resume(1, "远程（PC-1）");
    CHECK(rs.ok);
    CHECK(!r.pe().is_paused(1));
    CHECK(r.has_title("1 号 程序继续"));
    r.advance(2000);
    CHECK(static_cast<int>(r.hw->sim_main->holding(1, at_reg(1))) != 4);
    CHECK(r.c().stir().on());
    CHECK_EQ(r.c().stir().rpm(), 300);
    CHECK(r.c().channel(1).state == core::ReactorState::Wait);   // 保持步骤原来的「等待中」
    CHECK(r.pe().resume(1).silent);                               // 没在暂停：幂等
    int64_t t0 = r.clock.mono_ms();
    CHECK(r.until([&] { return r.run_ended(1); }, 5 * 60 * 1000, 1000));
    double left_s = static_cast<double>(r.clock.mono_ms() - t0) / 1000.0;
    if (left_s < 75 || left_s > 100) std::printf("    继续后用时 %.1f s\n", left_s);
    CHECK(left_s > 75 && left_s < 100);
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    CHECK(!r.c().stir().on());
    CHECK_EQ(r.pe().channel_program(1)["state"].get<std::string>(), std::string("completed"));
    CHECK(!r.pe().channel_program(1)["paused"].get<bool>());
}

TEST(ctl_program_pause, gas_sequence_aborted_and_restarted) {
    PRig r(fast_settings());
    r.phy.k = 0.3;  // 充得慢，好让暂停落在充压中间
    using std::string;
    r.pe().set_draft(1, steps({
                            {StepType::Press, {{"gas", string("A")}, {"pres", 100.0}, {"tol", 2.0}, {"regulate", string("on")}, {"refill", 3.0}, {"maxSec", 180.0}}},
                            {StepType::Hold, {{"until", string("time")}, {"time", 1.0}}},
                            {StepType::End, {}},
                        }),
                     "充压");
    CHECK(r.pe().run(1).accepted);
    r.advance(1500);
    CHECK(r.c().gas_lease().owner_rv() == std::optional<int>(1));
    // 充压中暂停：气路序列安全中止（全关、释放租约）
    auto p = r.pe().pause(1, "触屏");
    CHECK(p.ok);
    CHECK(p.data["restartStep"].get<bool>());
    CHECK(r.until([&] { return !r.c().gas_lease().busy(); }, 5000, 100));
    CHECK(all_closed(r));
    double p_paused = r.phy.p[0];
    CHECK(p_paused < 95);
    r.advance(60 * 1000);
    CHECK(!r.c().gas_lease().busy());
    CHECK(std::fabs(r.phy.p[0] - p_paused) < 1);   // 暂停期间没再进气
    // 别的程序可以在暂停期间用气路
    r.pe().set_draft(2, steps({{StepType::Press, {{"gas", string("A")}, {"pres", 30.0}, {"regulate", string("off")}}}, {StepType::End, {}}}), "b");
    CHECK(r.pe().run(2).accepted);
    CHECK(r.until([&] { return r.run_ended(2); }, 120000));
    CHECK(r.phase_is(2, ProgramRunPhase::Completed));
    // 继续：本步从头重做（重新取气路充压），之后进入保持并稳压
    auto rs = r.pe().resume(1);
    CHECK(rs.ok);
    CHECK(rs.data["restartStep"].get<bool>());
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 1; }, 180000, 500));
    CHECK(r.phy.p[0] > 95);
    CHECK(r.pe().channel_program(1)["regulating"].get<bool>());
    // 保持中暂停：稳压停；继续：稳压重开
    CHECK(r.pe().pause(1).ok);
    CHECK(!r.pe().channel_program(1)["regulating"].get<bool>());
    CHECK(r.pe().pause(1).silent);
    r.advance(5000);
    CHECK(r.pe().resume(1).ok);
    CHECK(r.pe().channel_program(1)["regulating"].get<bool>());
    CHECK(r.until([&] { return r.run_ended(1); }, 180000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    auto t = r.titles_containing("程序继续");
    CHECK_EQ(t.size(), 2u);
}

TEST(ctl_program_pause, guards_abort_while_paused_and_stir_shared) {
    PRig r(fast_settings());
    // 没有程序：CHANNEL_STATE_INVALID；通道不存在：CHANNEL_NOT_FOUND
    auto np = r.pe().pause(3);
    CHECK(!np.ok);
    CHECK_EQ(np.code, std::string("CHANNEL_STATE_INVALID"));
    CHECK_EQ(r.pe().resume(3).code, std::string("CHANNEL_STATE_INVALID"));
    CHECK_EQ(r.pe().pause(9).code, std::string("CHANNEL_NOT_FOUND"));
    // 两路共用搅拌：暂停 RV1 → 释放持有，RV2 还在用，搅拌不停（转速恢复为 RV2 的）
    using std::string;
    auto prog_with = [](double rpm) {
        return steps({{StepType::Stir, {{"rpm", rpm}}}, {StepType::Hold, {{"until", string("time")}, {"time", 5.0}}}, {StepType::End, {}}});
    };
    r.pe().set_draft(2, prog_with(200), "b");
    r.pe().set_draft(1, prog_with(350), "a");
    CHECK(r.pe().run(2).accepted);
    r.advance(1000);
    CHECK(r.pe().run(1).accepted);
    r.advance(1000);
    CHECK_EQ(r.c().stir().rpm(), 350);
    CHECK(r.pe().pause(1).ok);
    r.advance(1000);
    CHECK(r.c().stir().on());
    CHECK_EQ(r.c().stir().rpm(), 200);
    CHECK(!r.pe().stir_share().holds(1));
    CHECK(r.pe().resume(1).ok);
    r.advance(1000);
    CHECK_EQ(r.c().stir().rpm(), 350);
    CHECK(r.pe().stir_share().holds(1));
    // 暂停中中止：正常收尾 → 手动停止，记录不再标「已暂停」
    CHECK(r.pe().pause(1).ok);
    CHECK(r.pe().abort(1).ok);
    CHECK(r.until([&] { return r.run_ended(1); }, 10000));
    CHECK(r.phase_is(1, ProgramRunPhase::Aborted));
    CHECK(r.c().channel(1).state == core::ReactorState::Stopped);
    CHECK_EQ(r.pe().channel_program(1)["state"].get<std::string>(), std::string("aborted"));
    CHECK(!r.pe().is_paused(1));
    // 暂停中全部停止：算有动作 → 手动停止
    CHECK(r.pe().pause(2).ok);
    ctl::StopAllResult sres;
    bool sdone = false;
    r.c().request_stop_all({}, [&](const ctl::StopAllResult& x) { sres = x; sdone = true; });
    CHECK(r.until([&] { return sdone && r.run_ended(2); }, 20000));
    CHECK(r.phase_is(2, ProgramRunPhase::Aborted));
    CHECK(r.c().channel(2).state == core::ReactorState::Stopped);
    CHECK_EQ(sres.programs_aborted, 1);
}

TEST(ctl_program_pause, resume_refused_while_alarm) {
    PRig r(fast_settings());
    r.pe().set_draft(1, stir_heat_hold(5.0), "x");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 2; }, 10 * 60 * 1000, 1000));
    CHECK(r.pe().pause(1).ok);
    r.c().channel_mut(1).set_state(core::ReactorState::Alarm);
    auto rs = r.pe().resume(1);
    CHECK(!rs.ok);
    CHECK_EQ(rs.code, std::string("CHANNEL_ALARM"));
    CHECK(r.pe().is_paused(1));
}

namespace {
std::vector<prog::StepSnapshot> leak_then_hold(double hold_min) {
    using std::string;
    return steps({
        {StepType::Leak, {{"pres", 50.0}, {"settle", 0.0}, {"hold", hold_min}, {"maxDrop", 2.0}, {"onFail", string("stop")}, {"after", string("vent")}}},
        {StepType::Hold, {{"until", string("time")}, {"time", 0.5}}},
        {StepType::End, {}},
    });
}
}  // namespace

// 气密判了不通过、放空中暂停：继续时只补做放空然后停止这一路，绝不重测（临界漏气的釜重测可能就过了）
TEST(ctl_program_pause, leak_fail_verdict_kept_across_pause) {
    PRig r(fast_settings());
    r.phy.k = 0.3;  // 放得慢，好让暂停落在放空中间
    r.phy.leak_psi_h[0] = 30;
    r.pe().set_draft(1, leak_then_hold(5.0), "气密");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return r.has_title("1 号 气密不通过"); }, 10 * 60 * 1000, 100));
    r.advance(300);
    CHECK(!r.run_ended(1));
    auto p = r.pe().pause(1, "触屏");
    CHECK(p.ok);
    CHECK(!p.data["restartStep"].get<bool>());
    CHECK(r.until([&] { return !r.c().gas_lease().busy(); }, 5000, 100));
    double p_paused = r.phy.p[0];
    CHECK(p_paused > 10);  // 还没放完
    r.phy.leak_psi_h[0] = 0;  // 这时候重测会「通过」
    r.advance(30 * 1000);
    auto rs = r.pe().resume(1);
    CHECK(rs.ok);
    CHECK(!rs.data["restartStep"].get<bool>());
    CHECK(rs.message.find("气密结果保留") != std::string::npos);
    double p_max = 0;
    CHECK(r.until([&] { p_max = std::max(p_max, r.phy.p[0]); return r.run_ended(1); }, 10 * 60 * 1000, 200));
    CHECK(p_max <= p_paused + 0.5);  // 没有重新充气
    CHECK(r.phy.p[0] < 5);           // 放空做完了
    CHECK(r.phase_is(1, ProgramRunPhase::Failed));
    CHECK_EQ(r.count_title("1 号 气密不通过"), 1);
    CHECK(!r.has_title("1 号 气密通过"));
    CHECK(all_closed(r));
}

// 气密通过后放空中暂停：继续时只补做放空，进入下一步，不再保压 5 分钟
TEST(ctl_program_pause, leak_pass_verdict_kept_across_pause) {
    PRig r(fast_settings());
    r.phy.k = 0.3;
    r.pe().set_draft(1, leak_then_hold(5.0), "气密");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return r.has_title("1 号 气密通过"); }, 10 * 60 * 1000, 100));
    r.advance(300);
    CHECK(r.pe().pause(1).ok);
    r.advance(10 * 1000);
    auto rs = r.pe().resume(1);
    CHECK(rs.ok);
    int64_t t0 = r.clock.mono_ms();
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 1; }, 10 * 60 * 1000, 200));
    CHECK(r.clock.mono_ms() - t0 < 3 * 60 * 1000);  // 没有重新保压 5 min
    CHECK_EQ(r.count_title("1 号 气密通过"), 1);
    CHECK(r.until([&] { return r.run_ended(1); }, 10 * 60 * 1000, 1000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
}

// 保压中暂停：暂停期间掉的压不能算进保压——继续时重新静置并重取 P0
TEST(ctl_program_pause, leak_hold_restarts_after_pause) {
    PRig r(fast_settings());
    r.pe().set_draft(1, leak_then_hold(5.0), "气密");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return !r.c().gas_lease().busy() && r.pe().run_info(1)->index == 0 && r.phy.p[0] > 45; }, 5 * 60 * 1000, 200));
    r.advance(2 * 60 * 1000);  // 保压走了 2 min
    CHECK(r.pe().pause(1).ok);
    r.phy.leak_psi_h[0] = 30;  // 暂停 10 min 期间掉 5 psi（比如有人动了接头）
    r.advance(10 * 60 * 1000);
    r.phy.leak_psi_h[0] = 0;
    auto rs = r.pe().resume(1);
    CHECK(rs.ok);
    CHECK(rs.message.find("重新静置") != std::string::npos);
    CHECK(r.until([&] { return r.run_ended(1); }, 20 * 60 * 1000, 1000));
    CHECK(r.has_title("1 号 气密通过"));
    CHECK(!r.has_title("1 号 气密不通过"));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
}

namespace {
std::vector<prog::StepSnapshot> heat_press_hold(double hold_min) {
    using std::string;
    return steps({
        {StepType::Heat, {{"mode", string("fixed")}, {"temp", 40.0}, {"rate", 0.0}, {"band", 2.0}, {"wait", 0.1}, {"maxWait", 30.0}}},
        {StepType::Press, {{"gas", string("A")}, {"pres", 100.0}, {"tol", 2.0}, {"regulate", string("on")}, {"refill", 3.0}, {"maxSec", 180.0}}},
        {StepType::Hold, {{"until", string("time")}, {"time", hold_min}}},
        {StepType::End, {}},
    });
}
double temp1(PRig& r) { return r.c().channel(1).t; }
}  // namespace

// 继续时釜凉着：先升温，到温后才重开稳压（凉的时候补满气，升温后会冲过目标）
TEST(ctl_program_pause, resume_cold_waits_for_heat_before_regulation) {
    PRig r(fast_settings());
    r.pe().set_draft(1, heat_press_hold(30.0), "热压");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 2; }, 20 * 60 * 1000, 1000));
    CHECK(r.pe().channel_program(1)["regulating"].get<bool>());
    CHECK(r.pe().pause(1).ok);
    r.advance(10 * 60 * 1000);  // 停了加热，凉下来
    CHECK(temp1(r) < 30);
    auto rs = r.pe().resume(1);
    CHECK(rs.ok);
    CHECK(rs.data["waitHeat"].get<bool>());
    r.advance(3000);
    CHECK(!r.pe().channel_program(1)["regulating"].get<bool>());  // 凉着不稳压
    double t_on = 0;
    CHECK(r.until([&] { t_on = temp1(r); return r.pe().channel_program(1)["regulating"].get<bool>(); }, 10 * 60 * 1000, 500));
    CHECK(t_on >= 37.5);
    CHECK(r.has_title("1 号 到温 · 稳压重开"));
    CHECK(r.pe().abort(1).ok);
}

// 暂停中出报警：程序按失败结束，不能再继续
TEST(ctl_program_pause, alarm_while_paused_fails_program) {
    PRig r(fast_settings());
    r.pe().set_draft(1, stir_heat_hold(30.0), "x");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { auto* ri = r.pe().run_info(1); return ri && ri->index == 2; }, 10 * 60 * 1000, 1000));
    CHECK(r.pe().pause(1).ok);
    r.advance(2000);
    r.hw->sim_main->set_holding(1, pv_reg(1), 3000);  // 300 ℃ ≥ 超温限
    CHECK(r.until([&] { return r.run_ended(1); }, 30000, 200));
    CHECK(r.phase_is(1, ProgramRunPhase::Failed));
    CHECK(r.has_title("1 号 暂停中报警 · 程序失败"));
    CHECK_EQ(r.pe().resume(1).code, std::string("CHANNEL_STATE_INVALID"));
}

// 继续前查气路：泄漏测试占着气路 → 拒绝继续（仍保持暂停）；释放后可以继续
TEST(ctl_program_pause, resume_refused_while_gas_busy) {
    PRig r(fast_settings());
    r.phy.k = 0.3;
    using std::string;
    r.pe().set_draft(1, steps({
                            {StepType::Press, {{"gas", string("A")}, {"pres", 100.0}, {"tol", 2.0}, {"regulate", string("off")}, {"maxSec", 180.0}}},
                            {StepType::End, {}},
                        }),
                     "充压");
    CHECK(r.pe().run(1).accepted);
    r.advance(1500);
    CHECK(r.pe().pause(1).ok);
    CHECK(r.until([&] { return !r.c().gas_lease().busy(); }, 5000, 100));
    int64_t gen = r.c().begin_gas(core::GasOwner::Leak, "泄漏测试", {2}, {2});
    auto rs = r.pe().resume(1);
    CHECK(!rs.ok);
    CHECK_EQ(rs.code, std::string("GAS_BUSY"));
    CHECK(r.pe().is_paused(1));
    r.c().end_gas(core::GasOwner::Leak, gen);
    CHECK(r.pe().resume(1).ok);
    CHECK(r.until([&] { return r.run_ended(1); }, 180000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
}
