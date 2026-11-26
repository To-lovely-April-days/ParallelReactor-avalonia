// 全部停止：紧急帧先落地、普通补发盖住按下前已排队的旧开阀帧；进行中的启动序列被停止代号截断；控制线程收尾。
// 单通道停止 / 停用。
#include <thread>

#include "ctl_test_util.hpp"
#include <cstdint>
#include <string>
#include <vector>

using namespace ctltest;
using reactor::core::ReactorState;

TEST(ctl_stop, stop_all_urgent_then_normal_over_stale_frames) {
    GatedHw g;
    auto io_gate = g.io_gate;
    auto temp_gate = g.temp_gate;
    Rig r({}, {}, std::move(g.hw));
    r.ticks(6);
    // 开机默认全部启用：只留 1 号运行，其余停用
    for (int i = 2; i <= 8; ++i) r.c().channel_mut(i).set_state(ReactorState::Idle);
    r.c().channel_mut(1).set_state(ReactorState::Ready);

    // 阀门总线：先卡住一笔，再排一帧「旧的开阀帧」（按下全部停止之前已通过判定、排在普通队列里）
    io_gate->close();
    io_gate->clear();
    r.c().write_valves(core::valve_frame::gas(1, {2}), {}, nullptr);  // 气体 A + SV2（第一笔，卡在闸门上）
    CHECK(io_gate->wait_blocked());
    r.c().write_valves(core::valve_frame::gas(1, {2, 3}), {}, nullptr);  // 旧帧：排在普通队列
    // 温控总线：启动加热全链进行中（Srun=0 卡在闸门上，后面还有 At=0 / PID / SP）
    temp_gate->close();
    temp_gate->clear();
    r.c().start_fixed_heat(1, 80.0, std::string("local"));
    CHECK(temp_gate->wait_blocked());

    bool done = false;
    reactor::ctl::StopAllResult res;
    std::thread other([&] {  // 快速路径可以从任何线程调
        r.c().request_stop_all({false, "测试"}, [&](const reactor::ctl::StopAllResult& x) { res = x; done = true; });
    });
    other.join();
    io_gate->open();
    temp_gate->open();
    r.c().settle();
    CHECK(done);
    CHECK(res.heat_ok);
    CHECK(res.valves_ok);
    CHECK(res.urgent_heat_ok);
    CHECK(res.urgent_valves_ok);

    // 阀门总线的执行顺序：[卡住的那笔] → 紧急全关 → 旧开阀帧 → 普通补发全关（最后落地的是全关）
    auto io = io_gate->log();
    CHECK(io.size() >= 4);
    std::vector<int> close_idx, open_idx;
    for (size_t i = 0; i < io.size(); ++i) {
        const auto& w = io[i];
        if (w.fc != hw::kFcWriteCoils) continue;
        bool any = false;
        for (bool b : w.bits) any = any || b;
        (any ? open_idx : close_idx).push_back(static_cast<int>(i));
    }
    CHECK_EQ(close_idx.size(), 2u);
    CHECK(open_idx.size() == 2);
    if (close_idx.size() == 2 && open_idx.size() == 2) {
        CHECK(io[static_cast<size_t>(close_idx[0])].urgent);
        CHECK(!io[static_cast<size_t>(close_idx[1])].urgent);
        CHECK(close_idx[0] < open_idx[1]);   // 紧急全关插在旧帧之前
        CHECK(open_idx[1] < close_idx[1]);   // 普通补发排在旧帧之后
        CHECK_EQ(close_idx[1], static_cast<int>(io.size()) - 1);
    }
    for (int i = 0; i < 12; ++i) CHECK(!r.mock().coil(1, static_cast<uint16_t>(i)));

    // 温控总线：Srun=0 已做，之后的 At=0 被停止代号截断，从未写下去；8 路 At=4 先紧急一遍、再普通一遍
    auto tw = temp_gate->log();
    int at0 = 0, at4_urgent = 0, at4_normal = 0;
    for (const auto& w : tw) {
        if (is_write_reg(w, at_reg(1), 0)) ++at0;
        for (int ch = 1; ch <= 8; ++ch)
            if (is_write_reg(w, at_reg(ch), 4)) (w.urgent ? at4_urgent : at4_normal)++;
    }
    CHECK_EQ(at0, 0);
    CHECK_EQ(at4_urgent, 8);
    CHECK_EQ(at4_normal, 8);
    for (int ch = 1; ch <= 8; ++ch) CHECK_EQ(r.mock().holding(1, at_reg(ch)), 4);

    // 控制线程收尾
    CHECK(r.c().channel(1).state == ReactorState::Done);
    CHECK(!r.c().channel(1).approach.active());
    CHECK(!r.c().channel(1).started_by.has_value());
    CHECK_EQ(res.channels.size(), 1u);
    CHECK(r.c().valves().shown() == core::valve_frame::all_closed());
}

TEST(ctl_stop, stop_all_with_stir_and_offline_reporting) {
    Rig r;
    r.ticks(6);
    bool started = false;
    r.c().stir_start(300, [&](bool ok) { started = ok; });
    r.c().settle();
    CHECK(started);
    CHECK(r.c().stir().on());
    bool done = false;
    reactor::ctl::StopAllResult res;
    r.c().request_stop_all({true, "测试"}, [&](const reactor::ctl::StopAllResult& x) { res = x; done = true; });
    r.c().settle();
    CHECK(done);
    CHECK(res.stir_requested && res.stir_ok && res.stir_stopped);
    CHECK(!r.c().stir().on());
    // 停搅拌帧：紧急 + 普通各一笔 0x6002=0x40
    int stops = 0;
    for (const auto& w : r.hw->sim_stir->writes())
        if (is_write_reg(w, 0x6002, 0x40)) ++stops;
    CHECK_EQ(stops, 2);

    // 温控断线时：结果报告未确认 + 报警
    r.mock().set_fail_all(true);
    done = false;
    r.c().request_stop_all({false, "测试"}, [&](const reactor::ctl::StopAllResult& x) { res = x; done = true; });
    r.c().settle();
    CHECK(done);
    CHECK(!res.heat_ok);
    CHECK(!res.valves_ok);
    CHECK(r.has_title("全部停止 · 停止加热未确认"));
    CHECK(r.has_title("阀门全关写入失败"));
}

TEST(ctl_stop, stop_and_disable_single_channel) {
    Rig r;
    r.ticks(6);
    r.c().start_fixed_heat(3, 90.0, std::string("local"));
    r.c().write_valves(core::valve_frame::gas(1, {3}), {}, nullptr);
    r.c().settle();
    CHECK(r.c().channel(3).state == ReactorState::Heating);
    CHECK(r.c().valves().sv_open(3));
    r.mock().clear_writes();
    bool got = false;
    reactor::ctl::ChannelStopResult res;
    r.c().stop_channel(3, false, [&](const reactor::ctl::ChannelStopResult& x) { res = x; got = true; });
    r.c().settle();
    CHECK(got && res.heat_ok && res.valve_ok && !res.program_aborted);
    CHECK(r.c().channel(3).state == ReactorState::Done);
    CHECK(!r.c().channel(3).approach.active());
    bool at4 = false, sv_off = false;
    for (const auto& w : r.mock().writes()) {
        if (is_write_reg(w, at_reg(3), 4)) at4 = true;
        if (w.fc == hw::kFcWriteCoil && w.addr == 2 && !w.bits[0]) sv_off = true;
    }
    CHECK(at4);
    CHECK(sv_off);
    CHECK(!r.c().valves().sv_open(3));
    CHECK(r.c().valves().shown()[9]);  // 气源 A 不动（只关该路 SV）
    r.c().stop_channel(3, true, nullptr);
    r.c().settle();
    CHECK(r.c().channel(3).state == ReactorState::Idle);
}
