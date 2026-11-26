// 总线：紧急优先、同级 FIFO、不打断在途、串行、序列、停止、通讯统计 / 离线判定、同步视图、全部停止快速路径。
// 前几条照 C# tests/ParallelReactor.Tests/PriorityGateTests.cs 逐条翻译（用地址当事务名字）。
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <future>
#include <mutex>
#include <thread>
#include <vector>

#include "hw/bus.hpp"
#include "hw/hardware.hpp"
#include "hw/mock_master.hpp"
#include "testing.hpp"

using namespace reactor::hw;

namespace {

// 每笔事务开始时记下地址，然后等测试「放行」一笔才返回。用来精确控制在途 / 排队状态。
class StepMaster : public ModbusMaster {
public:
    ModbusResult execute(const ModbusRequest& r) override {
        std::unique_lock<std::mutex> lk(mu_);
        order_.push_back(r.addr);
        urgent_flags_.push_back(r.urgent);
        cv_.notify_all();
        cv_.wait(lk, [this] { return permits_ > 0; });
        --permits_;
        ModbusResult res;
        res.ok = true;
        return res;
    }
    void release(int n = 1) {
        std::lock_guard<std::mutex> lk(mu_);
        permits_ += n;
        cv_.notify_all();
    }
    bool wait_started(size_t n, int ms = 2000) {
        std::unique_lock<std::mutex> lk(mu_);
        return cv_.wait_for(lk, std::chrono::milliseconds(ms), [&] { return order_.size() >= n; });
    }
    std::vector<uint16_t> order() {
        std::lock_guard<std::mutex> lk(mu_);
        return order_;
    }
    std::vector<bool> urgent_flags() {
        std::lock_guard<std::mutex> lk(mu_);
        return urgent_flags_;
    }

private:
    std::mutex mu_;
    std::condition_variable cv_;
    std::vector<uint16_t> order_;
    std::vector<bool> urgent_flags_;
    int permits_ = 0;
};

ModbusRequest R(uint16_t addr) { return req_read_holding(1, addr, 1); }

}  // namespace

TEST(hw_bus, free_bus_runs_immediately) {
    auto m = std::make_shared<MockModbusMaster>();
    Bus bus("t", m);
    CHECK(bus.running());
    CHECK(bus.idle());
    auto f = bus.submit_future(req_write_register(1, 5, 42));
    CHECK(f.get().ok);
    CHECK_EQ(m->holding(1, 5), static_cast<uint16_t>(42));
    auto r = bus.submit_future(R(5), Priority::Urgent).get();
    CHECK(r.ok && r.regs.size() == 1 && r.regs[0] == 42);
    CHECK(bus.wait_idle(1000));
}

// C#: UrgentJumpsQueuedNormals_ThenNormalsFifo
TEST(hw_bus, urgent_jumps_queued_normals_then_normals_fifo) {
    auto m = std::make_shared<StepMaster>();
    Bus bus("t", m);
    bus.submit(R(0), Priority::Normal, nullptr);  // 在途事务
    CHECK(m->wait_started(1));
    bus.submit(R(1), Priority::Normal, nullptr);
    bus.submit(R(2), Priority::Normal, nullptr);
    bus.submit(R(3), Priority::Normal, nullptr);
    bus.submit(R(100), Priority::Urgent, nullptr);
    CHECK_EQ(bus.queued(Priority::Urgent), static_cast<size_t>(1));
    CHECK_EQ(bus.queued(Priority::Normal), static_cast<size_t>(3));
    CHECK(m->order().size() == 1);  // 在途事务未结束：谁也拿不到
    m->release(5);
    CHECK(bus.wait_idle(2000));
    CHECK(m->order() == (std::vector<uint16_t>{0, 100, 1, 2, 3}));
    auto fl = m->urgent_flags();
    CHECK(!fl[0] && fl[1] && !fl[2]);  // 紧急队列的请求带 urgent 标记（追踪日志用）
}

// C#: NormalCopyAfterUrgent_LandsAfterStaleQueuedNormal
// 「紧急一遍 + 普通一遍」：按下前已排队的普通帧（如开阀）落在紧急停止帧之后，但之后再排的普通停止帧一定在它之后。
TEST(hw_bus, normal_copy_after_urgent_lands_after_stale_normal) {
    auto m = std::make_shared<StepMaster>();
    Bus bus("t", m);
    bus.submit(R(0), Priority::Normal, nullptr);  // 在途：一次轮询
    CHECK(m->wait_started(1));
    bus.submit(R(1), Priority::Normal, nullptr);    // stale-open
    bus.submit(R(100), Priority::Urgent, nullptr);  // urgent-stop
    m->release(1);
    CHECK(m->wait_started(2));                      // 紧急停止帧拿到总线
    bus.submit(R(2), Priority::Normal, nullptr);    // normal-stop
    m->release(3);
    CHECK(bus.wait_idle(2000));
    auto o = m->order();
    CHECK(o == (std::vector<uint16_t>{0, 100, 1, 2}));
    CHECK(o.back() == 2);
}

// C#: Urgents_AreFifoAmongThemselves
TEST(hw_bus, urgents_fifo_among_themselves) {
    auto m = std::make_shared<StepMaster>();
    Bus bus("t", m);
    bus.submit(R(0), Priority::Normal, nullptr);
    CHECK(m->wait_started(1));
    bus.submit(R(1), Priority::Normal, nullptr);    // n1
    bus.submit(R(100), Priority::Urgent, nullptr);  // u1
    m->release(1);
    CHECK(m->wait_started(2));                      // u1 在途
    bus.submit(R(101), Priority::Urgent, nullptr);  // u2
    bus.submit(R(2), Priority::Normal, nullptr);    // n2
    m->release(4);
    CHECK(bus.wait_idle(2000));
    CHECK(m->order() == (std::vector<uint16_t>{0, 100, 101, 1, 2}));
}

// C#: Stress_NeverTwoHolders
TEST(hw_bus, stress_never_two_in_flight) {
    struct ConcMaster : ModbusMaster {
        std::atomic<int> cur{0}, max{0}, done{0};
        ModbusResult execute(const ModbusRequest&) override {
            int c = ++cur;
            int m = max.load();
            while (c > m && !max.compare_exchange_weak(m, c)) {
            }
            std::this_thread::yield();
            --cur;
            ++done;
            ModbusResult r;
            r.ok = true;
            return r;
        }
    };
    auto m = std::make_shared<ConcMaster>();
    Bus bus("t", m);
    std::atomic<int> callbacks{0};
    std::vector<std::thread> th;
    for (int t = 0; t < 8; ++t)
        th.emplace_back([&, t] {
            for (int i = 0; i < 25; ++i)
                bus.submit(R(static_cast<uint16_t>(t * 100 + i)), (i % 7 == 0) ? Priority::Urgent : Priority::Normal,
                           [&](const ModbusResult& r) { if (r.ok) ++callbacks; });
        });
    for (auto& x : th) x.join();
    CHECK(bus.wait_idle(5000));
    CHECK_EQ(m->max.load(), 1);
    CHECK_EQ(m->done.load(), 200);
    CHECK_EQ(callbacks.load(), 200);
    CHECK_EQ(bus.stats().polls(), static_cast<int64_t>(200));
}

TEST(hw_bus, sequence_reenters_queue_tail) {
    auto m = std::make_shared<StepMaster>();
    Bus bus("t", m);
    bus.submit(R(0), Priority::Normal, nullptr);
    CHECK(m->wait_started(1));
    std::promise<std::vector<ModbusResult>> p;
    bus.submit_seq({R(10), R(11), R(12)}, Priority::Normal, true,
                   [&](const std::vector<ModbusResult>& rs) { p.set_value(rs); });
    bus.submit(R(1), Priority::Normal, nullptr);
    m->release(1);
    CHECK(m->wait_started(2));                      // 10 在途
    bus.submit(R(100), Priority::Urgent, nullptr);  // 紧急帧插在序列两步之间
    m->release(10);
    auto rs = p.get_future().get();
    CHECK(bus.wait_idle(2000));
    CHECK_EQ(rs.size(), static_cast<size_t>(3));
    // 10 做完时 1 已在队里，11 排在 1 后面（同 C# 每步重新 await 同一把 PriorityGate）
    CHECK(m->order() == (std::vector<uint16_t>{0, 10, 100, 1, 11, 12}));
}

TEST(hw_bus, sequence_stop_on_fail) {
    auto m = std::make_shared<MockModbusMaster>();
    Bus bus("t", m);
    m->fail_next(0);
    std::promise<std::vector<ModbusResult>> p1;
    // 第二笔非法（站号 0）→ 失败即停，第三笔不做
    ModbusRequest bad = R(2);
    bad.slave = 0;
    bus.submit_seq({R(1), bad, R(3)}, Priority::Normal, true, [&](const std::vector<ModbusResult>& rs) { p1.set_value(rs); });
    auto rs = p1.get_future().get();
    CHECK_EQ(rs.size(), static_cast<size_t>(2));
    CHECK(rs[0].ok && !rs[1].ok);
    // 不停：全部做完
    std::promise<std::vector<ModbusResult>> p2;
    bus.submit_seq({R(1), bad, R(3)}, Priority::Normal, false, [&](const std::vector<ModbusResult>& rs2) { p2.set_value(rs2); });
    auto rs2 = p2.get_future().get();
    CHECK_EQ(rs2.size(), static_cast<size_t>(3));
    CHECK(rs2[2].ok);
    // 空序列立即回调
    bool called = false;
    bus.submit_seq({}, Priority::Normal, true, [&](const std::vector<ModbusResult>& r) { called = r.empty(); });
    CHECK(called);
}

TEST(hw_bus, stop_fails_queued_and_rejects_new) {
    auto m = std::make_shared<StepMaster>();
    auto bus = std::make_unique<Bus>("t", m);
    std::atomic<int> ok{0}, closed{0};
    auto cb = [&](const ModbusResult& r) {
        if (r.ok) ++ok;
        else if (r.error.find("总线已关闭") != std::string::npos) ++closed;
    };
    bus->submit(R(0), Priority::Normal, cb);
    CHECK(m->wait_started(1));
    bus->submit(R(1), Priority::Normal, cb);
    bus->submit(R(2), Priority::Urgent, cb);
    std::thread stopper([&] { bus->stop(); });
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    m->release(1);  // 在途那笔做完（不被打断）
    stopper.join();
    CHECK_EQ(ok.load(), 1);
    CHECK_EQ(closed.load(), 2);
    CHECK(!bus->running());
    bool rejected_cb = false;
    CHECK(!bus->submit(R(3), Priority::Urgent, [&](const ModbusResult& r) { rejected_cb = !r.ok; }));
    CHECK(rejected_cb);
    CHECK(!bus->submit_future(R(4)).get().ok);
    bus->stop();  // 可重复
    CHECK(m->order() == std::vector<uint16_t>{0});
}

TEST(hw_bus, stop_drain_runs_queued) {
    auto m = std::make_shared<StepMaster>();
    Bus bus("t", m);
    std::atomic<int> ok{0};
    auto cb = [&](const ModbusResult& r) { if (r.ok) ++ok; };
    bus.submit(R(0), Priority::Normal, cb);
    CHECK(m->wait_started(1));
    bus.submit(R(1), Priority::Normal, cb);
    bus.submit(R(2), Priority::Normal, cb);
    m->release(3);
    bus.stop(true);
    CHECK_EQ(ok.load(), 3);
}

TEST(hw_bus, comm_stats_offline_after_three) {
    CommStats s;
    CHECK(!s.online());  // 初始离线（C# `_fails = 3`）
    CHECK_EQ(s.last_ok_ms(), static_cast<int64_t>(0));
    s.ok(1000);
    CHECK(s.online());
    CHECK_EQ(s.last_ok_ms(), static_cast<int64_t>(1000));
    s.fail();
    s.fail();
    CHECK(s.online());  // 2 次还在线
    s.fail();
    CHECK(!s.online());  // 连续 3 次 = 离线
    s.fail();
    CHECK_EQ(s.consecutive_fails(), 3);  // 封顶
    CHECK_EQ(s.polls(), static_cast<int64_t>(5));
    CHECK_EQ(s.fails(), static_cast<int64_t>(4));
    s.ok(2000);
    CHECK(s.online());
}

TEST(hw_bus, offline_detection_through_bus) {
    auto m = std::make_shared<MockModbusMaster>();
    Bus bus("t", m);
    CHECK(!bus.stats().online());
    CHECK(bus.submit_future(R(0)).get().ok);
    CHECK(bus.stats().online());
    CHECK(bus.stats().last_ok_ms() > 0);
    m->set_fail_all(true);
    CHECK(!bus.submit_future(R(0)).get().ok);
    CHECK(!bus.submit_future(R(0)).get().ok);
    CHECK(bus.stats().online());
    auto r = bus.submit_future(R(0)).get();
    CHECK(!r.ok && !r.error.empty());
    CHECK(!bus.stats().online());
    m->set_fail_all(false);
    CHECK(bus.submit_future(R(0)).get().ok);
    CHECK(bus.stats().online());
    CHECK_EQ(bus.stats().fails(), static_cast<int64_t>(3));

    // 未配置端口：每笔失败，永远离线
    Bus off("off", std::make_shared<OfflineModbusMaster>("阀门 IO16R（hardware.json io.port）"));
    for (int i = 0; i < 4; ++i) CHECK(!off.submit_future(R(0)).get().ok);
    CHECK(!off.stats().online());
    CHECK_EQ(off.stats().fails(), static_cast<int64_t>(4));
}

TEST(hw_bus, bus_master_sync_view) {
    auto m = std::make_shared<MockModbusMaster>();
    Bus bus("t", m);
    BusMaster bm(bus);
    CHECK(bm.write_register(1, 7, 99).ok);
    auto r = bm.read_holding(1, 7, 1);
    CHECK(r.ok && r.regs[0] == 99);
    // 在总线线程上同步等本总线 → 直接失败，不死锁
    std::promise<bool> p;
    bus.submit(R(0), Priority::Normal, [&](const ModbusResult&) { p.set_value(bm.read_holding(1, 7, 1).ok); });
    CHECK(!p.get_future().get());
    // 回调里再 submit 是允许的
    std::promise<bool> p2;
    bus.submit(R(0), Priority::Normal, [&](const ModbusResult&) {
        bus.submit(R(7), Priority::Normal, [&](const ModbusResult& r2) { p2.set_value(r2.ok && r2.regs[0] == 99); });
    });
    CHECK(p2.get_future().get());
}

TEST(hw_bus, urgent_stop_all_sim) {
    auto hw = make_hardware(HardwareConfig::for_sim());
    CHECK(hw->simulated && !hw->sim_refused);
    CHECK(hw->temp_configured() && hw->io_configured() && hw->stir_configured() && hw->press_configured());
    for (int i = 0; i < 8; ++i) hw->sim_main->set_holding(1, static_cast<uint16_t>(0x0300 + i), 0);
    for (int i = 0; i < 12; ++i) hw->sim_main->set_coil(1, static_cast<uint16_t>(i), true);
    hw->sim_main->set_coil(1, 13, true);
    std::promise<StopAllOutcome> p;
    hw->urgent_stop_all(true, [&](const StopAllOutcome& o) { p.set_value(o); });
    auto o = p.get_future().get();
    CHECK(o.heat_ok && o.valves_ok && o.stir_ok && o.stir_requested);
    for (int i = 0; i < 8; ++i) CHECK_EQ(hw->sim_main->holding(1, static_cast<uint16_t>(0x0300 + i)), static_cast<uint16_t>(4));
    for (int i = 0; i < 12; ++i) CHECK(!hw->sim_main->coil(1, static_cast<uint16_t>(i)));
    CHECK(hw->sim_main->coil(1, 13));  // 范围外不碰
    CHECK_EQ(hw->sim_stir->holding(1, 0x6002), static_cast<uint16_t>(0x0040));
    CHECK_EQ(hw->sim_main->holding(1, 0x0845), static_cast<uint16_t>(0));  // 不用 Srun 全局闸

    // 不停搅拌
    hw->sim_stir->set_holding(1, 0x6002, 0);
    std::promise<StopAllOutcome> p2;
    hw->urgent_stop_all(false, [&](const StopAllOutcome& x) { p2.set_value(x); });
    auto o2 = p2.get_future().get();
    CHECK(o2.heat_ok && o2.valves_ok && o2.stir_ok && !o2.stir_requested);
    CHECK_EQ(hw->sim_stir->holding(1, 0x6002), static_cast<uint16_t>(0));

    // 模拟推进 + 经总线的同步驱动调用
    hw->simulate_step();
    BusMaster tm(*hw->temp_bus);
    auto poll = hw->temp.poll(tm);
    CHECK(poll.has_value());
    if (poll) CHECK_EQ(poll->ctn, 8);
    BusMaster pm(*hw->press_bus);
    auto raw = hw->press.read_raw(pm);
    CHECK(raw && raw->size() == 8);
}

TEST(hw_bus, hardware_offline_and_refused) {
    HardwareConfig c;
    c.temp.port = c.stir.port = c.press.port = c.io.port = "";
    auto hw = make_hardware(c);
    CHECK(!hw->simulated && !hw->temp_configured() && !hw->io_configured());
    std::promise<StopAllOutcome> p;
    hw->urgent_stop_all(true, [&](const StopAllOutcome& o) { p.set_value(o); });
    auto o = p.get_future().get();
    CHECK(!o.heat_ok && !o.valves_ok && !o.stir_ok);
    CHECK(!hw->temp_bus->stats().online());
    BusMaster bm(*hw->io_bus);
    auto r = bm.read_coils(1, 0, 16);
    CHECK(!r.ok && r.error.find("未配置串口") != std::string::npos);

    HardwareConfig refused;  // 默认端口 + sim=true → 拒绝模拟
    refused.sim = true;
    CHECK(refused.sim_refused() && !refused.simulated());
}
