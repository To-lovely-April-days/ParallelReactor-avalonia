// tests/ctl 公用：在模拟硬件（hw::MockModbusMaster）上手动驱动控制核心，时钟可控。
//   Rig r;                 // 模拟硬件 + ManualClock + ControlCore（不起线程，测试线程就是控制线程）
//   r.ticks(6);            // 推进 6 个控制周期（定时器按 ManualClock 到期顺序触发，每次触发后等总线空闲、执行完回包）
//   r.advance(500);        // 推进任意毫秒
//   r.mock()               // 温控 / 压力 / 阀门共用的模拟主站（后门：set_holding / set_input / writes / set_fail_all）
#pragma once
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

#include "ctl/ctl_core.hpp"
#include "hw/hardware.hpp"
#include "hw/mock_master.hpp"
#include "testing.hpp"

namespace ctltest {

using namespace reactor;

// AI-8 寄存器（回路 1 起）
inline uint16_t pv_reg(int ch) { return static_cast<uint16_t>(0x0600 + ch - 1); }
inline uint16_t at_reg(int ch) { return static_cast<uint16_t>(0x0300 + ch - 1); }
inline uint16_t sp_reg(int ch) { return static_cast<uint16_t>(0x0000 + ch - 1); }
// 4-20 mA、满量程 600 psi：psi → 8AI 原码
inline uint16_t raw_of_psi(double psi) { return static_cast<uint16_t>(3200 + psi / 600.0 * 12800.0 + 0.5); }

inline bool is_write_reg(const hw::ModbusRequest& r, uint16_t addr, int value) {
    return r.fc == hw::kFcWriteRegister && r.addr == addr && !r.regs.empty() && r.regs[0] == static_cast<uint16_t>(value);
}

struct Rig {
    ctl::ManualClock clock;
    std::unique_ptr<hw::Hardware> hw;
    std::unique_ptr<ctl::ControlCore> core;

    explicit Rig(ctl::CoreOptions opt = {}, store::SettingsState st = {}, std::unique_ptr<hw::Hardware> custom = nullptr) {
        hw = custom ? std::move(custom) : hw::make_hardware(hw::HardwareConfig::for_sim());
        opt.threaded = false;
        opt.watchdog = false;
        ctl::CoreDeps d;
        d.hw = hw.get();
        d.clock = &clock;
        d.settings = st;
        core = std::make_unique<ctl::ControlCore>(std::move(d), opt);
        core->start();
        core->settle();
    }
    ~Rig() {
        if (core) core->shutdown(false);
    }

    hw::MockModbusMaster& mock() { return *hw->sim_main; }
    ctl::ControlCore& c() { return *core; }

    void advance(int64_t ms) {
        int64_t target = clock.mono_ms() + ms;
        core->settle();
        for (;;) {
            auto due = core->loop().next_due();
            if (!due || *due > target) break;
            if (*due > clock.mono_ms()) clock.set_mono(*due);
            core->loop().fire_due(clock.mono_ms());
            core->settle();
        }
        clock.set_mono(target);
        core->settle();
    }
    void ticks(int n) { advance(1200LL * n); }

    int count_active(const std::string& key) {
        int n = 0;
        for (const auto& a : core->alarms().items())
            if (a.key == key && a.state == "active") ++n;
        return n;
    }
    int count_raised(const std::string& key) {
        int n = 0;
        for (const auto& a : core->alarms().items())
            if (a.key == key) ++n;
        return n;
    }
    bool has_title(const std::string& title) {
        for (const auto& a : core->alarms().items())
            if (a.title == title) return true;
        return false;
    }
};

// 可以「关闸」的主站：关闸时 execute 阻塞，用来让事务在队列里排队；同时记下每笔请求（含是否走紧急队列）。
class GateMaster : public hw::ModbusMaster {
public:
    explicit GateMaster(std::shared_ptr<hw::ModbusMaster> inner) : inner_(std::move(inner)) {}
    hw::ModbusResult execute(const hw::ModbusRequest& req) override {
        {
            std::unique_lock<std::mutex> lk(mu_);
            ++waiting_;
            cv_.notify_all();
            cv_.wait(lk, [this] { return open_; });
            --waiting_;
            log_.push_back(req);
        }
        return inner_->execute(req);
    }
    void close() { std::lock_guard<std::mutex> lk(mu_); open_ = false; }
    void open() {
        { std::lock_guard<std::mutex> lk(mu_); open_ = true; }
        cv_.notify_all();
    }
    // 等到有一笔事务卡在闸门上
    bool wait_blocked(int ms = 2000) {
        std::unique_lock<std::mutex> lk(mu_);
        return cv_.wait_for(lk, std::chrono::milliseconds(ms), [this] { return waiting_ > 0; });
    }
    std::vector<hw::ModbusRequest> log() const { std::lock_guard<std::mutex> lk(mu_); return log_; }
    void clear() { std::lock_guard<std::mutex> lk(mu_); log_.clear(); }

private:
    std::shared_ptr<hw::ModbusMaster> inner_;
    mutable std::mutex mu_;
    std::condition_variable cv_;
    bool open_ = true;
    int waiting_ = 0;
    std::vector<hw::ModbusRequest> log_;
};

// 按请求决定「照常 / 回失败 / 吞掉（回成功但不写进去）」的主站，并记下被拦的请求。
class FilterMaster : public hw::ModbusMaster {
public:
    enum class Act { Pass, Fail, Swallow };
    FilterMaster(std::shared_ptr<hw::ModbusMaster> inner, std::function<Act(const hw::ModbusRequest&)> f)
        : inner_(std::move(inner)), f_(std::move(f)) {}
    hw::ModbusResult execute(const hw::ModbusRequest& req) override {
        Act a = f_(req);
        if (a == Act::Pass) return inner_->execute(req);
        {
            std::lock_guard<std::mutex> lk(mu_);
            hit_.push_back(req);
        }
        if (a == Act::Fail) return hw::fail_result("测试：通讯失败");
        hw::ModbusResult r;
        r.ok = true;
        return r;
    }
    std::vector<hw::ModbusRequest> hits() const { std::lock_guard<std::mutex> lk(mu_); return hit_; }

private:
    std::shared_ptr<hw::ModbusMaster> inner_;
    std::function<Act(const hw::ModbusRequest&)> f_;
    mutable std::mutex mu_;
    std::vector<hw::ModbusRequest> hit_;
};

// 模拟硬件，压力总线换成一个不抖动的模拟主站（读数严格等于 set_input 的值）
inline std::unique_ptr<hw::Hardware> quiet_pressure_hw(std::shared_ptr<hw::MockModbusMaster>& press) {
    auto h = hw::make_hardware(hw::HardwareConfig::for_sim());
    press = std::make_shared<hw::MockModbusMaster>();  // analog_slave = 0：不模拟压力
    for (int i = 0; i < 8; ++i) press->set_input(1, static_cast<uint16_t>(i), 8000);
    h->press_bus = std::make_unique<hw::Bus>("压力", press);
    return h;
}

// 模拟硬件，但温控 / 阀门总线前面各加一道闸门
struct GatedHw {
    std::shared_ptr<GateMaster> temp_gate, io_gate;
    std::unique_ptr<hw::Hardware> hw;
    GatedHw() {
        hw = std::make_unique<hw::Hardware>();
        hw->cfg = hw::HardwareConfig::for_sim();
        hw->simulated = true;
        hw::MockModbusMaster::Options o;
        o.temp_slaves = {1};
        o.temp_loops = 8;
        o.analog_slave = 1;
        o.seed = 7;
        hw->sim_main = std::make_shared<hw::MockModbusMaster>(o);
        hw->sim_stir = std::make_shared<hw::MockModbusMaster>();
        temp_gate = std::make_shared<GateMaster>(hw->sim_main);
        io_gate = std::make_shared<GateMaster>(hw->sim_main);
        hw->temp_bus = std::make_unique<hw::Bus>("温控", temp_gate);
        hw->press_bus = std::make_unique<hw::Bus>("压力", hw->sim_main);
        hw->io_bus = std::make_unique<hw::Bus>("阀门", io_gate);
        hw->stir_bus = std::make_unique<hw::Bus>("搅拌", hw->sim_stir);
    }
};

}  // namespace ctltest
