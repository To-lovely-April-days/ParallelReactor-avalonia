// 控制线程底座：事件循环、定时器、门闸、停止代号、报警表。
#include <atomic>
#include <thread>

#include "ctl/ctl_alarm_table.hpp"
#include "ctl/ctl_event_loop.hpp"
#include "ctl/ctl_io.hpp"
#include "ctl/ctl_tick_gate.hpp"
#include "testing.hpp"

using namespace reactor::ctl;

TEST(ctl_loop, manual_timers_fire_in_due_order) {
    ManualClock clk;
    EventLoop lp(clk);
    lp.bind_current_thread();
    std::vector<int> order;
    lp.every(1200, [&] { order.push_back(1); });
    lp.after(500, [&] { order.push_back(2); });
    auto t3 = lp.after(700, [&] { order.push_back(3); });
    lp.cancel(t3);
    CHECK_EQ(*lp.next_due(), clk.mono_ms() + 500);
    clk.advance(2400);
    lp.fire_due(clk.mono_ms());
    // 单次 500 先到；周期定时器落后 2 个周期只触发一次（不补发）
    CHECK_EQ(order.size(), 2u);
    CHECK_EQ(order[0], 2);
    CHECK_EQ(order[1], 1);
    CHECK_EQ(*lp.next_due(), clk.mono_ms() + 1200);
}

TEST(ctl_loop, post_from_other_threads_runs_fifo_on_loop_thread) {
    SystemClock clk;
    EventLoop lp(clk);
    lp.start_thread();
    std::vector<int> got;
    std::thread::id loop_tid;
    for (int i = 0; i < 100; ++i) lp.post([&, i] { got.push_back(i); loop_tid = std::this_thread::get_id(); });
    auto f = lp.call([&] { return static_cast<int>(got.size()); });
    CHECK_EQ(f.get(), 100);
    CHECK(loop_tid != std::this_thread::get_id());
    for (int i = 0; i < 100; ++i) CHECK_EQ(got[static_cast<size_t>(i)], i);
    // 定时器在线程模式下按真实时钟触发
    std::atomic<int> fired{0};
    lp.post([&] { lp.after(20, [&] { ++fired; }); });
    for (int i = 0; i < 100 && fired == 0; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    CHECK_EQ(fired.load(), 1);
    lp.stop();
    lp.join();
    CHECK(!lp.post([] {}));  // 停止后拒绝
}

TEST(ctl_loop, task_exception_does_not_kill_loop) {
    ManualClock clk;
    EventLoop lp(clk);
    lp.bind_current_thread();
    int n = 0;
    lp.post([] { throw std::runtime_error("boom"); });
    lp.post([&] { ++n; });
    CHECK_EQ(lp.run_pending(), 2u);
    CHECK_EQ(n, 1);
}

TEST(ctl_loop, tick_gate_counts_like_csharp) {
    // C# TickGateTests 的语义：重入跳过、超时计数、超时日志每分钟最多一条
    TickGate g(1200);
    CHECK(g.try_enter());
    CHECK(!g.try_enter());
    CHECK_EQ(g.skipped(), 1);
    CHECK(!g.exit(800, 0));
    CHECK(g.try_enter());
    CHECK(g.exit(1500, 1000));   // 超时，首次记录
    CHECK(g.try_enter());
    CHECK(!g.exit(1600, 30000));  // 不到 1 分钟不再记录
    CHECK(g.try_enter());
    CHECK(g.exit(1300, 61001));
    CHECK_EQ(g.overruns(), 3);
    CHECK_EQ(g.entered(), 4);
    CHECK_NEAR(g.max_took_ms(), 1600, 1e-9);
    CHECK_NEAR(g.last_overrun_ms(), 1300, 1e-9);
}

TEST(ctl_loop, stop_epoch_blocks_stale_submissions) {
    StopEpoch s;
    uint64_t e = s.current();
    int ran = 0;
    CHECK(s.submit_if(e, [&] { ++ran; }));
    s.bump_and([&] { ran += 10; });
    CHECK(!s.submit_if(e, [&] { ++ran; }));
    CHECK_EQ(ran, 11);
    CHECK(s.submit_if(s.current(), [&] { ++ran; }));
}

TEST(ctl_alarms, raise_clear_ack_and_levels) {
    int64_t now = 1000;
    std::vector<std::string> kinds;
    AlarmTable t([&] { return now; }, [&](const std::string&, const std::string&, const std::string&, const std::string&,
                                          const std::string& kind) { kinds.push_back(kind); });
    int changes = 0;
    t.on_change = [&](const AlarmItem&) { ++changes; };
    auto& a = t.raise("ot:3", "err", "RV3", "超温 · 切断加热", "x", "alarm.overTemp");
    CHECK_EQ(a.ui_level, 1);
    CHECK_EQ(a.channel.value_or(0), 3);
    std::string id = a.id;
    t.raise("comm:0", "err", "通讯", "温控 AI-8 离线", "y", "alarm.commLost");
    t.raise("ht:2", "warn", "RV2", "升温超时", "z", "alarm.heatTimeout");
    t.log("info", "系统", "程序启动", "", "system.boot");
    CHECK_EQ(t.items()[0].ui_level, 0);
    CHECK_EQ(t.items()[0].state, std::string("done"));
    CHECK_EQ(t.items()[1].ui_level, 3);
    CHECK_EQ(t.items()[2].ui_level, 2);
    CHECK_EQ(t.active_count(), 3);
    CHECK(t.ack(id));
    CHECK_EQ(t.find(id)->state, std::string("ack"));
    now = 5000;
    CHECK_EQ(t.clear("ot:3", "RV3", "超温 · 解除", "PV 低了", "alarm.overTempCleared"), 1);
    CHECK_EQ(t.find(id)->state, std::string("done"));  // ack → done
    CHECK_EQ(t.find(id)->cleared_at_ms.value_or(0), 5000);
    CHECK(!t.has_active("ot:3"));
    CHECK_EQ(t.items()[0].message, std::string("PV 低了 · 自动解除 1 条报警"));
    CHECK_EQ(kinds.back(), std::string("alarm.overTempCleared"));
    CHECK_EQ(t.ack_all(), 2);
    auto j = alarm_to_json(*t.find(id));
    std::vector<std::string> keys;
    for (auto it = j.begin(); it != j.end(); ++it) keys.push_back(it.key());
    // C# AlarmDto(Id, Level, Rv, Channel, Title, Message, RaisedAt, State, ClearedAt) + uiLevel
    std::vector<std::string> want{"id", "level", "rv", "channel", "title", "message", "raisedAt", "state", "clearedAt", "uiLevel"};
    CHECK(keys == want);
    CHECK(changes > 5);
}
