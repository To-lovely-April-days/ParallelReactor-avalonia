// 程序引擎（第二阶段）：9 种步骤端到端、PUT / 复制 / 下发守卫、中止路径与结束原因、快照新字段。
// 模拟硬件 + 可控时钟 + 简单的气路物理（ctl_phase2_util.hpp）。
#include "ctl_phase2_util.hpp"
#include <algorithm>
#include <cstdio>
#include <optional>
#include <string>
#include <vector>

using namespace ctltest;
using prog::ProgramRunPhase;
using prog::StepType;

namespace {

std::vector<prog::StepSnapshot> all_nine() {
    using std::string;
    return steps({
        {StepType::Leak, {{"pres", 50.0}, {"settle", 0.0}, {"hold", 5.0}, {"maxDrop", 2.0}, {"onFail", string("stop")}, {"after", string("vent")}}},
        {StepType::Exchange, {{"gas", string("inert")}, {"fill", 30.0}, {"release", 5.0}, {"cycles", 1.0}, {"lineFlush", 1.0}, {"maxSec", 60.0}}},
        {StepType::Stir, {{"rpm", 300.0}}},
        {StepType::Heat, {{"mode", string("fixed")}, {"temp", 40.0}, {"rate", 0.0}, {"band", 2.0}, {"wait", 0.2}, {"maxWait", 30.0}}},
        {StepType::Press, {{"gas", string("A")}, {"pres", 100.0}, {"tol", 2.0}, {"regulate", string("on")}, {"refill", 3.0}, {"maxSec", 180.0}}},
        {StepType::Hold, {{"until", string("time")}, {"time", 0.5}}},
        {StepType::React, {{"time", 1.0}, {"minTime", 0.0}, {"mmolOn", string("off")}, {"stallOn", string("off")}}},
        {StepType::Vent, {{"to", 5.0}, {"maxSec", 120.0}, {"flush", 1.0}, {"flushPres", 20.0}}},
        {StepType::End, {}},
    });
}

std::vector<prog::StepSnapshot> press_hold(double pres, double hold_min, const char* regulate = "off") {
    using std::string;
    return steps({
        {StepType::Press, {{"gas", string("A")}, {"pres", pres}, {"tol", 2.0}, {"regulate", string(regulate)}, {"refill", 3.0}, {"maxSec", 180.0}}},
        {StepType::Hold, {{"until", string("time")}, {"time", hold_min}}},
        {StepType::End, {}},
    });
}

}  // namespace

TEST(ctl_program, all_nine_step_types_end_to_end) {
    PRig r(fast_settings());
    r.pe().set_draft(1, all_nine(), "九步");
    auto res = r.pe().run(1, "local");
    if (!res.ok) std::printf("    下发被拒：%s\n", res.message.c_str());
    CHECK(res.ok);
    CHECK(res.accepted);
    CHECK(r.pe().is_owned(1));
    // 刚开始：气密检查取到气路（空闲 + 无人排队 → 立即取），通道升压中
    CHECK(r.c().gas_lease().owner() == core::GasOwner::Program);
    CHECK(r.c().channel(1).state == core::ReactorState::Pressing);
    bool saw_regulation = false, saw_counting = false;
    bool done = r.until(
        [&] {
            if (r.pe().gas_pending() == "1 号 程序 · 稳压") saw_regulation = true;
            if (r.c().channel(1).uptake.counting()) saw_counting = true;
            return r.run_ended(1);
        },
        40 * 60 * 1000, 1000);
    CHECK(done);
    auto* ri = r.pe().run_info(1);
    CHECK(ri != nullptr);
    if (!ri) return;
    if (ri->phase != ProgramRunPhase::Completed) std::printf("    结束：%s · %s\n", ri->phase_text.c_str(), ri->message.c_str());
    CHECK(ri->phase == ProgramRunPhase::Completed);
    CHECK_EQ(ri->index, 8);
    CHECK(ri->end_reason.has_value() && *ri->end_reason == "最长时间到");
    CHECK_EQ(ri->message, std::string("9 步全部完成 · 最长时间到"));
    CHECK(saw_regulation);
    CHECK(saw_counting);
    CHECK(r.has_title("1 号 气密通过"));
    CHECK(r.has_title("1 号 置换完成"));
    CHECK(r.has_title("1 号 程序 · 充压完成"));
    CHECK(r.has_title("1 号 稳压结束（进入 放空）"));
    CHECK(r.has_title("1 号 反应结束 · 最长时间到"));
    CHECK(r.has_title("1 号 程序完成"));
    // 收尾：通道已结束、阀全关、气路空闲、搅拌已释放（最后一个持有者 → 停）、计量已停
    CHECK(r.c().channel(1).state == core::ReactorState::Done);
    auto v = r.valves();
    CHECK(std::none_of(v.begin(), v.end(), [](bool b) { return b; }));
    CHECK(!r.c().gas_lease().busy());
    CHECK(!r.c().stir().on());
    CHECK(!r.c().channel(1).uptake.counting());
    CHECK(r.pe().stir_share().count() == 0);
    // 冻结步骤状态全部完成
    auto run = r.pe().program_run(1);
    CHECK_EQ(run["state"].get<std::string>(), std::string("completed"));
    CHECK_EQ(run["steps"].size(), 9u);
    for (const auto& s : run["stepStates"]) CHECK_EQ(s["state"].get<std::string>(), std::string("completed"));
    // 釜里放空过：压力低
    CHECK(r.phy.p[0] < 25);
}

TEST(ctl_program, replace_copy_and_guards) {
    PRig r(fast_settings());
    nlohmann::json js = nlohmann::json::parse(R"([
        {"type":"press","params":{"gas":"A","pres":100,"regulate":"off"}},
        {"type":"hold","params":{"until":"time","time":1}},
        {"type":"end","params":{}}])");
    auto res = r.pe().replace_program(2, js, std::string("测试程序"));
    CHECK(res.ok);
    CHECK_EQ(res.message, std::string("2 号 程序已更新（3 步）"));
    CHECK_EQ(res.data["steps"].size(), 3u);
    // 超全局限值：PARAM_OUT_OF_RANGE
    nlohmann::json bad = nlohmann::json::parse(R"([{"type":"press","params":{"gas":"A","pres":490}},{"type":"end","params":{}}])");
    auto rb = r.pe().replace_program(2, bad);
    CHECK(!rb.ok);
    CHECK_EQ(rb.code, std::string("PARAM_OUT_OF_RANGE"));
    // 复制到 3、4（2 自己跳过）
    auto cp = r.pe().copy_program(2, {2, 3, 4}, false);
    CHECK(cp.ok);
    CHECK_EQ(cp.data["to"].size(), 2u);
    CHECK_EQ(r.pe().draft(3).size(), 3u);
    // 预检 + 下发；程序执行中再 PUT → CHANNEL_PROGRAM_OWNED
    CHECK(r.pe().check_run(2).silent);
    CHECK(r.pe().run(2, "PC-1").accepted);
    auto locked = r.pe().replace_program(2, js);
    CHECK(!locked.ok);
    CHECK_EQ(locked.code, std::string("CHANNEL_PROGRAM_OWNED"));
    // 同一通道再下发：守卫拒绝 + 事件「程序下发被拒绝」
    auto again = r.pe().run(2, "local");
    CHECK(!again.ok);
    CHECK(again.message.rfind("下发被拒绝 · ", 0) == 0);
    CHECK(r.has_title("程序下发被拒绝"));
    // 快照：channels[1].program 带新字段
    r.advance(2000);
    auto s = r.c().snapshot();
    CHECK(s != nullptr);
    if (!s) return;
    const auto& p = s->channels[1].program;
    CHECK(p.is_object());
    if (!p.is_object()) return;
    CHECK_EQ(p["startedBy"].get<std::string>(), std::string("PC-1"));
    CHECK_EQ(p["name"].get<std::string>(), std::string("测试程序"));
    CHECK_EQ(p["stepNames"].size(), 3u);
    CHECK(p["remaining"].is_object());
    CHECK(s->program_runs[1].is_object());
    CHECK(s->programs["channels"].size() == 1u);
    // 中止（幂等）
    CHECK(r.pe().abort(2).ok);
    r.until([&] { return r.run_ended(2); }, 10000);
    CHECK(r.phase_is(2, ProgramRunPhase::Aborted));
    CHECK(r.pe().abort(2).silent);
}

TEST(ctl_program, abort_paths_and_end_reasons) {
    PRig r(fast_settings());
    // 1) 手动中止（保持中）
    r.pe().set_draft(1, press_hold(60, 10), "a");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return r.c().channel(1).state == core::ReactorState::Wait; }, 120000));
    auto ab = r.pe().abort(1);
    CHECK(ab.ok && ab.quiet);
    CHECK(r.until([&] { return r.run_ended(1); }, 10000));
    CHECK(r.phase_is(1, ProgramRunPhase::Aborted));
    CHECK_EQ(r.pe().run_info(1)->message, std::string("已停止当前程序，设备已完成安全收尾"));
    CHECK(r.has_title("1 号 程序手动停止"));
    CHECK(r.c().channel(1).state == core::ReactorState::Done);

    // 2) 单路停止（停用）：核心不再自己下发 At=4，程序收尾
    r.pe().set_draft(2, press_hold(60, 10), "b");
    CHECK(r.pe().run(2).accepted);
    CHECK(r.until([&] { return r.c().channel(2).state == core::ReactorState::Wait; }, 120000));
    bool cb = false;
    r.c().stop_channel(2, true, [&](const ctl::ChannelStopResult& res) { cb = res.program_aborted; });
    CHECK(r.until([&] { return r.run_ended(2); }, 10000));
    CHECK(cb);
    CHECK(r.phase_is(2, ProgramRunPhase::Aborted));
    CHECK(r.c().channel(2).state == core::ReactorState::Idle);  // 停用保持已停用

    // 3) 全部停止中途：程序中止计数、通道已结束
    r.pe().set_draft(3, press_hold(60, 10), "c");
    r.pe().set_draft(4, press_hold(60, 10), "d");
    CHECK(r.pe().run(3).accepted);
    CHECK(r.pe().run(4).accepted);
    r.advance(3000);
    ctl::StopAllResult sres;
    bool sdone = false;
    r.c().request_stop_all({}, [&](const ctl::StopAllResult& x) { sres = x; sdone = true; });
    CHECK(r.until([&] { return sdone && r.run_ended(3) && r.run_ended(4); }, 20000));
    CHECK_EQ(sres.programs_aborted, 2);
    CHECK(r.phase_is(3, ProgramRunPhase::Aborted));
    CHECK(r.phase_is(4, ProgramRunPhase::Aborted));
    CHECK(!r.c().gas_lease().busy());

    // 4) 执行前按当前全局限值再校验：下发后把压力上限调低 → 第 1 步失败
    r.pe().set_draft(6, steps({{StepType::Hold, {{"until", std::string("time")}, {"time", 0.1}}},
                               {StepType::Press, {{"gas", std::string("A")}, {"pres", 150.0}, {"regulate", std::string("off")}}},
                               {StepType::End, {}}}),
                     "f");
    CHECK(r.pe().run(6).accepted);
    r.c().update_settings([](store::SettingsState& s) { s.settings.p_max_sp = 100; });
    CHECK(r.until([&] { return r.run_ended(6); }, 60000));
    CHECK(r.phase_is(6, ProgramRunPhase::Failed));
    CHECK(r.pe().run_info(6)->message.find("参数超出全局限值") != std::string::npos);
    CHECK(r.has_title("6 号 程序中止"));
}

TEST(ctl_program, over_pressure_during_gas_step_fails_holder_only) {
    PRig r(fast_settings());
    r.phy.supply[1] = 600;
    r.phy.k = 0.3;  // 充得慢一点，好让超压发生在气路步骤中间
    r.c().channel_mut(3).set_state(core::ReactorState::Idle);
    r.pe().set_draft(1, press_hold(300, 1), "op");
    r.pe().set_draft(2, press_hold(60, 1), "q");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.pe().run(2).accepted);
    r.advance(1500);
    CHECK(r.c().gas_lease().owner_rv() == std::optional<int>(1));
    CHECK(r.phase_is(2, ProgramRunPhase::WaitingGas));
    CHECK_EQ(r.pe().programs()["waitingGas"].size(), 1u);
    // RV1 充压途中，停用的 RV3 釜压超过超压报警值：超压联锁 → 无条件中止气路 → 持有者 RV1 按失败收尾
    r.phy.p[2] = 520;
    CHECK(r.until([&] { return r.run_ended(1); }, 60000, 200));
    CHECK(r.phase_is(1, ProgramRunPhase::Failed));
    CHECK_EQ(r.pe().run_info(1)->message, std::string("气路被超压联锁中止"));
    CHECK(r.c().is_over_pressure_tripped(3));
    // 排队的 RV2 不受牵连：仍在等（RV3 超压未解除，公共检查不通过 → 继续等待，不立即故障）
    CHECK(r.pe().is_owned(2));
    CHECK(r.phase_is(2, ProgramRunPhase::WaitingGas));
    CHECK(r.has_title("2 号 程序 · 等待气路"));
    auto v = r.valves();
    for (int i = 0; i < 11; ++i) CHECK(!v[static_cast<size_t>(i)]);
    // 超压解除（< 98%）后 RV2 自行取到气路继续
    r.phy.p[2] = 100;
    CHECK(r.until([&] { return r.run_ended(2); }, 180000, 500));
    CHECK(r.phase_is(2, ProgramRunPhase::Completed));
}

TEST(ctl_program, heat_curve_and_rate_paths) {
    PRig r(fast_settings());
    using std::string;
    r.pe().set_draft(1, steps({
                            {StepType::Heat, {{"mode", string("curve")}, {"band", 2.0}, {"maxWait", 10.0}}, {prog::Segment{35, 5, 0.2}}},
                            {StepType::Heat, {{"mode", string("fixed")}, {"temp", 40.0}, {"rate", 5.0}, {"band", 2.0}, {"wait", 0.1}, {"maxWait", 10.0}}},
                            {StepType::End, {}},
                        }),
                     "curve");
    CHECK(r.pe().run(1).accepted);
    bool saw_curve_status = false;
    CHECK(r.until(
        [&] {
            auto* ri = r.pe().run_info(1);
            if (ri && ri->status.find("· 曲线 ") != std::string::npos) saw_curve_status = true;
            return r.run_ended(1);
        },
        20 * 60 * 1000, 1000));
    CHECK(r.phase_is(1, ProgramRunPhase::Completed));
    CHECK(saw_curve_status);
    CHECK_EQ(r.count_title("1 号 程序 · 升温曲线启动"), 2);
    // 收尾停加热：At=4
    CHECK_EQ(static_cast<int>(r.hw->sim_main->holding(1, at_reg(1))), 4);
}

TEST(ctl_program, stir_share_fallback_and_last_release_stops) {
    PRig r(fast_settings());
    using std::string;
    auto prog_with = [](double rpm, double hold) {
        return steps({{StepType::Stir, {{"rpm", rpm}}}, {StepType::Hold, {{"until", string("time")}, {"time", hold}}}, {StepType::End, {}}});
    };
    r.pe().set_draft(1, prog_with(300, 2), "a");
    r.pe().set_draft(2, prog_with(400, 0.5), "b");
    CHECK(r.pe().run(1).accepted);
    r.advance(1000);
    CHECK(r.c().stir().on());
    CHECK_EQ(r.c().stir().rpm(), 300);
    auto rr = r.pe().run(2);
    if (!rr.ok) std::printf("    %s\n", rr.message.c_str());
    CHECK(rr.accepted);
    r.advance(1000);
    CHECK_EQ(r.c().stir().rpm(), 400);
    CHECK(r.has_title("2 号 改了共用搅拌"));
    CHECK_EQ(r.pe().stir_step_rv().value_or(0), 1);
    // RV2 先结束：转速恢复为 RV1 的 300 并记事件，搅拌不停
    CHECK(r.until([&] { return r.run_ended(2); }, 120000));
    CHECK(r.c().stir().on());
    CHECK_EQ(r.c().stir().rpm(), 300);
    CHECK(r.has_title("搅拌转速恢复"));
    // RV1 结束：最后一个释放者 → 停搅拌
    CHECK(r.until([&] { return r.run_ended(1); }, 240000));
    CHECK(!r.c().stir().on());
    CHECK_EQ(r.pe().stir_share().count(), 0);
}

TEST(ctl_program, step_events_in_order) {
    PRig r(fast_settings());
    std::vector<std::string> ev;
    r.pe().on_step = [&](int rv, int i, const std::string& st, const std::string&) {
        ev.push_back(std::to_string(rv) + ":" + std::to_string(i) + ":" + st);
    };
    using std::string;
    r.pe().set_draft(1, steps({{StepType::Stir, {{"rpm", 200.0}}}, {StepType::Hold, {{"until", string("time")}, {"time", 0.1}}}, {StepType::End, {}}}), "e");
    CHECK(r.pe().run(1).accepted);
    CHECK(r.until([&] { return r.run_ended(1); }, 60000));
    std::string all;
    for (auto& e : ev) all += e + " ";
    CHECK_EQ(all, std::string("1:0:running 1:0:completed 1:1:running 1:1:completed 1:2:running 1:2:completed "));
}
