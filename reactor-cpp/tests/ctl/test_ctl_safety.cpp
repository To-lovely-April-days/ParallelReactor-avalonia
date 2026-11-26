// 每周期安全判定：超温切断（触发 / 确认 / 重发 / 被重开 / 解除）、超压联锁（合并关阀帧、排空保持、中止气路）、
// 模块掉线 / 恢复（开机 5 个周期宽限）、升温超时与压力偏离两种三级警告。
#include "ctl_test_util.hpp"

using namespace ctltest;
using reactor::core::ReactorState;

namespace {
int count_at4(const std::vector<hw::ModbusRequest>& ws, int ch) {
    int n = 0;
    for (const auto& w : ws)
        if (is_write_reg(w, at_reg(ch), 4)) ++n;
    return n;
}
}  // namespace

TEST(ctl_safety, over_temp_trip_confirm_clear) {
    Rig r;
    r.ticks(6);
    CHECK(r.c().temp().online());
    r.mock().clear_writes();
    r.mock().set_holding(1, pv_reg(2), 3000);  // RV2 300 ℃ ≥ 250
    r.ticks(1);                                // 本周期轮询读到高 PV
    r.ticks(1);                                // 下一周期判定 → 切断
    CHECK(r.c().is_over_temp_tripped(2));
    CHECK(r.c().channel(2).state == ReactorState::Alarm);
    CHECK(r.c().channel(2).over_temp);
    CHECK_EQ(count_at4(r.mock().writes(), 2), 1);
    CHECK_EQ(r.count_active("ot:2"), 1);
    CHECK_EQ(r.c().alarms().items().front().ui_level >= 0, true);
    // 模拟仪表已停：回读 At=4 → 确认
    r.ticks(2);
    CHECK(r.has_title("超温切断已确认"));
    CHECK_EQ(count_at4(r.mock().writes(), 2), 1);  // 确认后不再重发
    // PV 回落到 248 以下 → 解除；通道仍保持报警（需人工复位）
    r.mock().set_holding(1, pv_reg(2), 2400);
    r.ticks(2);
    CHECK(!r.c().is_over_temp_tripped(2));
    CHECK_EQ(r.count_active("ot:2"), 0);
    CHECK(r.has_title("超温 · 解除"));
    CHECK(r.c().channel(2).state == ReactorState::Alarm);
}

namespace {
// 温控总线换成过滤主站：对 RV5 的 At=4 写入按 act 处理
std::unique_ptr<hw::Hardware> filtered_temp_hw(FilterMaster::Act act, std::shared_ptr<FilterMaster>& fm) {
    auto h = hw::make_hardware(hw::HardwareConfig::for_sim());
    fm = std::make_shared<FilterMaster>(h->sim_main, [act](const hw::ModbusRequest& q) {
        return is_write_reg(q, at_reg(5), 4) ? act : FilterMaster::Act::Pass;
    });
    h->temp_bus = std::make_unique<hw::Bus>("温控", fm);
    return h;
}
}  // namespace

TEST(ctl_safety, over_temp_resend_every_5s_and_escalate) {
    // 写入回显正常、但仪表回读一直不是 At=4（写被吞掉）→ 未确认 → 每 5 s 重发，第 3 次仍未确认升级报警
    std::shared_ptr<FilterMaster> fm;
    Rig r({}, {}, filtered_temp_hw(FilterMaster::Act::Swallow, fm));
    r.ticks(6);
    r.mock().set_holding(1, at_reg(5), 0);
    for (int k = 0; k < 16; ++k) {
        r.mock().set_holding(1, pv_reg(5), 3000);
        r.ticks(1);
    }
    int sends = static_cast<int>(fm->hits().size());
    CHECK(sends >= 3);
    CHECK(sends <= 5);  // 16 × 1.2 s ≈ 19 s：首发 + 每 5 s 一次
    CHECK(r.has_title("超温切断多次未确认 · 请点主页的「全部停止」"));
    CHECK(!r.has_title("超温切断已确认"));
}

TEST(ctl_safety, over_temp_write_failure_raises_unconfirmed) {
    std::shared_ptr<FilterMaster> fm;
    Rig r({}, {}, filtered_temp_hw(FilterMaster::Act::Fail, fm));
    r.ticks(6);
    for (int k = 0; k < 8; ++k) {
        r.mock().set_holding(1, pv_reg(5), 3000);
        r.ticks(1);
    }
    CHECK(fm->hits().size() >= 2);
    CHECK(r.has_title("超温切断未确认"));
    CHECK(r.c().is_over_temp_tripped(5));
    CHECK(r.c().channel(5).t_online);  // 只有这一笔失败，温控仍在线
}

TEST(ctl_safety, over_temp_reopened_after_confirm) {
    Rig r;
    r.ticks(6);
    r.mock().set_holding(1, pv_reg(1), 3000);
    r.ticks(4);
    CHECK(r.has_title("超温切断已确认"));
    r.mock().clear_writes();
    // 有人把输出重新打开（At=0），仍超温 → 重新置报警 + 重发
    r.mock().set_holding(1, at_reg(1), 0);
    r.mock().set_holding(1, pv_reg(1), 3000);
    r.ticks(3);
    CHECK(r.has_title("超温切断后输出被重新打开 · 重新切断"));
    CHECK(count_at4(r.mock().writes(), 1) >= 1);
}

TEST(ctl_safety, over_pressure_merged_close_keeps_vent_and_aborts_gas) {
    Rig r;
    r.ticks(6);
    // 先开排空 + SV5（排空某路），并登记一个泄漏测试租约（参与 RV3）
    bool wrote = false;
    r.c().write_valves(core::valve_frame::vent_only({5}), {}, [&](bool ok, const std::string&) { wrote = ok; });
    r.c().settle();
    CHECK(wrote);
    std::vector<core::GasAbortCause> causes;
    r.c().add_gas_abort_listener([&](core::GasAbortCause c, std::optional<int>) {
        causes.push_back(c);
        r.c().close_all_valves();  // 序列收尾：超压中止期间排空保持
        r.c().end_gas(core::GasOwner::Leak);
    });
    r.c().begin_gas(core::GasOwner::Leak, "泄漏测试", {3, 5}, {3});
    r.mock().clear_writes();
    r.mock().set_input(1, 2, raw_of_psi(560));  // RV3 560 psi ≥ 510
    r.ticks(2);
    CHECK(r.c().is_over_pressure_tripped(3));
    CHECK(r.c().channel(3).state == ReactorState::Alarm);
    CHECK_EQ(causes.size(), 1u);
    CHECK(causes.size() == 1 && causes[0] == core::GasAbortCause::OverPressure);
    CHECK(!r.c().gas_lease().busy());
    auto ws = r.mock().writes();
    bool gas_frame = false, sv3 = false, touched_vent = false, touched_sv5 = false, close11 = false;
    for (const auto& w : ws) {
        if (w.fc == hw::kFcWriteCoils && w.addr == 8 && w.count == 3) gas_frame = !w.bits[0] && !w.bits[1] && !w.bits[2];
        if (w.fc == hw::kFcWriteCoil && w.addr == 2 && !w.bits[0]) sv3 = true;
        if (w.fc == hw::kFcWriteCoil && w.addr == 11) touched_vent = true;
        if (w.fc == hw::kFcWriteCoil && w.addr == 4) touched_sv5 = true;
        if (w.fc == hw::kFcWriteCoils && w.addr + w.count > 11 && !(w.addr == 0 && w.count == 11)) touched_vent = true;
        if (w.fc == hw::kFcWriteCoils && w.addr == 0 && w.count == 11) close11 = true;  // 序列收尾：0–10 全关、排空不写
    }
    CHECK(gas_frame);
    CHECK(sv3);
    CHECK(!touched_vent);
    CHECK(!touched_sv5);
    CHECK(close11);
    CHECK(r.mock().coil(1, 11));  // 排空仍开着
    CHECK(count_at4(ws, 3) >= 1);  // 超压时停加热（默认开）
    CHECK_EQ(r.count_active("op:3"), 1);
    // 回落到 98% 以下 → 解除，通道保持报警
    r.mock().set_input(1, 2, raw_of_psi(400));
    r.ticks(2);
    CHECK(!r.c().is_over_pressure_tripped(3));
    CHECK_EQ(r.count_active("op:3"), 0);
    CHECK(r.c().channel(3).state == ReactorState::Alarm);
}

TEST(ctl_safety, over_pressure_unconfirmed_valve_resends_and_escalates) {
    // 阀门总线单独一个模拟主站，好单独让它断线
    auto hw = hw::make_hardware(hw::HardwareConfig::for_sim());
    auto io_mock = std::make_shared<hw::MockModbusMaster>();
    hw->io_bus = std::make_unique<hw::Bus>("阀门", io_mock);
    Rig r({}, {}, std::move(hw));
    r.ticks(6);
    io_mock->set_fail_all(true);
    int64_t before = io_mock->txn_count();
    r.mock().set_input(1, 6, raw_of_psi(600));  // RV7
    r.ticks(1);
    r.ticks(1);  // 判定 → 触发：关阀写不进去
    CHECK(r.c().is_over_pressure_tripped(7));
    CHECK(r.has_title("超压联锁 · 关阀未确认"));
    for (int k = 0; k < 15; ++k) {
        r.mock().set_input(1, 6, raw_of_psi(600));
        r.ticks(1);
    }
    // 触发 1 次 + 每 5 s 重发；每次 = 一帧气源 + 一笔 SV7
    int64_t txns = io_mock->txn_count() - before;
    CHECK(txns >= 8);
    CHECK(txns <= 12);
    CHECK(r.has_title("超压联锁多次未确认 · 请点主页的「全部停止」"));
    CHECK_EQ(r.count_active("opv:7") >= 1, true);
    // 通讯恢复 → 关阀确认 → 「未确认」报警解除
    io_mock->set_fail_all(false);
    for (int k = 0; k < 6; ++k) {
        r.mock().set_input(1, 6, raw_of_psi(600));
        r.ticks(1);
    }
    CHECK_EQ(r.count_active("opv:7"), 0);
    CHECK(r.has_title("超压联锁 · 关阀已确认"));
}

TEST(ctl_safety, comm_lost_and_restored_with_startup_grace) {
    Rig r;
    r.mock().set_fail_all(true);
    r.ticks(3);
    CHECK_EQ(r.count_raised("comm:0"), 0);  // 开机前 5 个周期不判
    r.ticks(3);
    CHECK_EQ(r.count_active("comm:0"), 1);  // 温控「开机后未连通」
    CHECK_EQ(r.count_active("comm:1"), 1);  // 压力
    CHECK(r.has_title("温控 AI-8 离线"));
    CHECK_EQ(r.c().alarms().items().front().ui_level, 2);
    r.mock().set_fail_all(false);
    r.ticks(2);
    CHECK_EQ(r.count_active("comm:0"), 0);
    CHECK(r.has_title("温控 AI-8 通讯恢复 · 报警解除"));
    CHECK(r.c().temp().online());
    // 在线后再断：连续 3 轮失败 → 离线报警；读数变无效，不自动停机
    r.mock().set_fail_all(true);
    r.ticks(4);
    CHECK_EQ(r.count_active("comm:0"), 1);
    CHECK(!r.c().channel(1).t_online);
    auto s = r.c().snapshot();
    CHECK(s && !s->channels[0].temp_c.has_value());
    r.mock().set_fail_all(false);
    r.ticks(2);
    CHECK_EQ(r.count_active("comm:0"), 0);
}

TEST(ctl_safety, heat_timeout_warns_once_then_clears) {
    store::SettingsState st;
    st.settings.heat_timeout = 1;  // 1 分钟
    Rig r({}, st);
    r.ticks(6);
    r.c().start_fixed_heat(4, 180.0, std::string("local"));
    r.ticks(40);  // 48 s：还没到
    CHECK_EQ(r.count_raised("ht:4"), 0);
    r.ticks(20);  // 72 s：PV 每周期最多涨 0.5 ℃，远未到位 → 警告一次
    CHECK_EQ(r.count_raised("ht:4"), 1);
    CHECK_EQ(r.count_active("ht:4"), 1);
    r.ticks(20);
    CHECK_EQ(r.count_raised("ht:4"), 1);  // 只警告一次
    const reactor::ctl::AlarmItem* w = nullptr;
    for (const auto& a : r.c().alarms().items())
        if (a.key == "ht:4") w = &a;
    CHECK(w && w->ui_level == 3 && w->level == "warn");
    r.c().stop_heat(4);
    r.c().settle();
    CHECK_EQ(r.count_active("ht:4"), 0);
}

TEST(ctl_safety, pressure_deviation_after_30s_and_auto_clear) {
    std::shared_ptr<hw::MockModbusMaster> press;
    Rig r({}, {}, quiet_pressure_hw(press));
    r.ticks(6);
    press->set_input(1, 3, raw_of_psi(250));
    r.ticks(1);
    r.c().begin_pressure_watch(4, 300.0);  // 偏离 50 psi > 8
    r.ticks(20);                           // 24 s：不到 30 s
    CHECK_EQ(r.count_raised("pd:4"), 0);
    r.ticks(8);
    CHECK_EQ(r.count_active("pd:4"), 1);
    press->set_input(1, 3, raw_of_psi(301));
    r.ticks(2);
    CHECK_EQ(r.count_active("pd:4"), 0);
    CHECK(r.has_title("压力偏离 · 解除"));
}
