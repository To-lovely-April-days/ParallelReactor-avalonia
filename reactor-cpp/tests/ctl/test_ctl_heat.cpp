// 加热：定值防超调逼近的下发、30 s 强制重写；16 段曲线的 SP 逐周期推进；阀门整帧的死区；吸气量计量。
#include <cmath>

#include "ctl_test_util.hpp"
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

using namespace ctltest;
using reactor::core::ReactorState;

TEST(ctl_heat, fixed_heat_full_chain_and_approach_writes) {
    store::SettingsState st;
    st.has_band_pids = true;  // 有存档 PID：开机不从仪表读种子，档位 PID = 默认值
    Rig r({}, st);
    r.ticks(6);
    r.mock().clear_writes();
    r.c().start_fixed_heat(1, 80.0, std::string("local"));
    r.c().settle();
    auto ws = r.mock().writes();
    // 全链：Srun=0 → At=0 → P/I/D（低温档 0–100：默认 P40 I100 D15）→ SP = 目标 − Δ(5) = 75.0
    CHECK(ws.size() >= 6);
    if (ws.size() >= 6) {
        CHECK(is_write_reg(ws[0], 0x0845, 0));
        CHECK(is_write_reg(ws[1], at_reg(1), 0));
        CHECK(is_write_reg(ws[2], 0x0060, 400));
        CHECK(is_write_reg(ws[3], 0x00C0, 1000));
        CHECK(is_write_reg(ws[4], 0x0120, 1500));
        CHECK(is_write_reg(ws[5], sp_reg(1), 750));
    }
    const auto& c = r.c().channel(1);
    CHECK(c.state == ReactorState::Heating);
    CHECK(c.started_by.value_or("") == "local");
    CHECK(c.approach.active());
    CHECK(c.approach.phase() == core::ApproachPhase::Settle);
    CHECK_EQ(r.c().temp().live_band(1), 0);
    CHECK(r.c().temp().ch(1).sp_known);
    // SP 不变时 30 s 强制重写一次（同档不再重写 PID）
    r.mock().clear_writes();
    r.ticks(27);  // 32.4 s
    int sp_writes = 0, pid_writes = 0;
    for (const auto& w : r.mock().writes()) {
        if (w.fc == hw::kFcWriteRegister && w.addr == sp_reg(1)) ++sp_writes;
        if (w.fc == hw::kFcWriteRegister && w.addr == 0x0060) ++pid_writes;
    }
    CHECK(sp_writes >= 1);
    CHECK_EQ(pid_writes, 0);
    // 改目标到高温档：逼近重规划，下次写 SP 前先写高温档 PID（P80 I150 D25）
    r.c().retarget(1, 210.0);
    r.mock().clear_writes();
    r.ticks(30);
    bool high_pid = false;
    for (const auto& w : r.mock().writes())
        if (is_write_reg(w, 0x0060, 800)) high_pid = true;
    CHECK(high_pid);
    CHECK_EQ(r.c().temp().live_band(1), 2);
}

TEST(ctl_heat, curve_sp_progression) {
    Rig r;
    r.ticks(6);
    double pv0 = r.c().channel(2).t;
    CHECK(pv0 > 20 && pv0 < 30);
    r.c().set_sp_mode(2, "curve");
    r.c().set_profile(2, {{50.0, 10.0}, {50.0, 10.0}});
    r.c().start_curve(2, std::string("local"));
    r.c().settle();
    CHECK(r.c().channel(2).profile.running());
    CHECK(r.c().channel(2).state == ReactorState::Heating);
    std::vector<double> sps;
    for (int k = 0; k < 250; ++k) {  // 5 min
        r.ticks(1);
        sps.push_back(r.mock().holding(1, sp_reg(2)) / 10.0);
    }
    // SP 单调上升（0.1 ℃ 量化），5 min ≈ 起点 + (50 − 起点) / 2
    for (size_t i = 1; i < sps.size(); ++i) CHECK(sps[i] + 1e-9 >= sps[i - 1]);
    double expect = pv0 + (50.0 - pv0) * 0.5;
    CHECK_NEAR(sps.back(), expect, 0.6);
    CHECK_NEAR(r.c().channel(2).tsp, expect, 0.6);
    // 快照：curve 通道的 profile 进帧
    auto s = r.c().snapshot();
    CHECK(s && s->channels[1].profile.running);
    CHECK_EQ(s->channels[1].profile.seg_count, 2);
    auto j = reactor::ctl::to_json(*s);
    CHECK(j["channels"][1]["profile"].is_object());
    CHECK(j["channels"][0]["profile"].is_null());
    // 报警 → 曲线自动停止
    r.c().channel_mut(2).set_state(ReactorState::Alarm);
    r.ticks(1);
    CHECK(!r.c().channel(2).profile.running());
}

TEST(ctl_valves, dead_time_inserted_between_vent_and_gas) {
    Rig r;
    r.ticks(6);
    bool ok1 = false;
    r.c().write_valves(core::valve_frame::vent_only({4}), {}, [&](bool ok, const std::string&) { ok1 = ok; });
    r.c().settle();
    CHECK(ok1);
    r.mock().clear_writes();
    bool ok2 = false;
    reactor::ctl::ValveWriteOpts o;
    o.dead_ms = 1000;  // 程序步骤的死区
    r.c().write_valves(core::valve_frame::gas(2, {4}), o, [&](bool ok, const std::string&) { ok2 = ok; });
    r.c().settle();
    // 先写了全关帧，目标帧还在等死区
    auto ws = r.mock().writes();
    CHECK_EQ(ws.size(), 1u);
    if (!ws.empty()) {
        bool any = false;
        for (bool b : ws[0].bits) any = any || b;
        CHECK(!any);
    }
    CHECK(!ok2);
    r.advance(999);
    CHECK_EQ(r.mock().writes().size(), 1u);
    r.advance(2);
    CHECK(ok2);
    CHECK_EQ(r.mock().writes().size(), 2u);
    CHECK(r.c().valves().shown() == core::valve_frame::gas(2, {4}));
    // 不安全帧（气源 + 排空）直接拒绝
    core::ValveFrame bad = core::valve_frame::gas(0);
    bad[11] = true;
    std::string err;
    r.c().write_valves(bad, {}, [&](bool, const std::string& e) { err = e; });
    CHECK(!err.empty());
    // 气路有占用时只能开参与通道的 SV
    r.c().begin_gas(core::GasOwner::Leak, "泄漏测试", {1}, {1});
    err.clear();
    r.c().write_valves(core::valve_frame::gas(0, {2}), {}, [&](bool, const std::string& e) { err = e; });
    CHECK(err.find("越权") != std::string::npos);
    // 保持心跳：每 1 s 重写当前帧
    r.c().end_gas(core::GasOwner::Leak);
    r.mock().clear_writes();
    r.c().set_valve_hold(core::valve_frame::gas(2, {4}));
    r.advance(3100);
    int frames = 0;
    for (const auto& w : r.mock().writes())
        if (w.fc == hw::kFcWriteCoils && w.count == 12) ++frames;
    CHECK_EQ(frames, 3);
    r.c().clear_valve_hold();
}

TEST(ctl_uptake, accumulates_drop_and_ignores_refill_with_sv_open) {
    std::shared_ptr<hw::MockModbusMaster> press;
    Rig r({}, {}, quiet_pressure_hw(press));
    r.ticks(6);
    auto set_p = [&](double psi) { press->set_input(1, 1, raw_of_psi(psi)); };
    set_p(300);
    r.ticks(2);
    r.c().channel_mut(2).vol = 0;  // 开机默认启用时体积给 3 mL；本用例按体积 0 计算
    CHECK(r.c().start_uptake(2, "测试"));
    CHECK(r.c().channel(2).uptake.counting());
    double v = r.c().gas_volume_ml(2);
    CHECK_NEAR(v, 10.0, 1e-9);  // 未标定：釜 10 − 溶液 0 + 管路 0
    r.ticks(3);                 // 开始后关阀静置 2 s 才取基准（GasUptakeMeter kSettleSec）
    // 关阀状态下压力从 300 降到 250 psi
    for (int k = 0; k < 10; ++k) {
        set_p(300 - 5.0 * (k + 1));
        r.ticks(1);
    }
    r.ticks(2);
    double t = r.c().channel(2).t;
    double expect = core::gas_uptake::gas_mmol(300, t, v) - core::gas_uptake::gas_mmol(250, t, v);
    double m1 = r.c().channel(2).uptake.mmol();
    CHECK_NEAR(m1, expect, 0.01);  // 原码量化 ≈0.047 psi
    CHECK(r.c().channel(2).uptake.valid());
    int64_t gen0 = r.c().sv_open_gen(2);
    // 开 SV2 补气到 320 psi：补进去的不算消耗
    r.c().write_valves(core::valve_frame::gas(1, {2}), {}, nullptr);
    r.c().settle();
    CHECK(r.c().sv_open_gen(2) > gen0);
    for (int k = 0; k < 4; ++k) {
        set_p(260 + 15.0 * (k + 1));
        r.ticks(1);
    }
    r.c().close_all_valves();
    r.ticks(4);  // 关满 2 s 后重取基准
    CHECK_NEAR(r.c().channel(2).uptake.mmol(), m1, 0.01);
    CHECK_EQ(r.c().channel(2).uptake.refills(), 1);
    // 再降 20 psi → 累计增加
    for (int k = 0; k < 4; ++k) {
        set_p(320 - 5.0 * (k + 1));
        r.ticks(1);
    }
    r.ticks(1);
    double add = core::gas_uptake::gas_mmol(320, t, v) - core::gas_uptake::gas_mmol(300, t, v);
    CHECK_NEAR(r.c().channel(2).uptake.mmol(), m1 + add, 0.02);
    // 快照 / 遥测带上吸气量
    auto s = r.c().snapshot();
    CHECK(s && s->channels[1].gas_consumed.has_value());
    r.c().stop_uptake(2, "测试结束");
    CHECK(!r.c().channel(2).uptake.counting());
    CHECK(r.has_title("2 号 吸气量计量结束"));
    // 气相体积不合理时不开始
    r.c().channel_mut(5).vol = 12;
    CHECK(!r.c().start_uptake(5, "测试"));
    CHECK(r.has_title("5 号 吸气量未计量"));
}
