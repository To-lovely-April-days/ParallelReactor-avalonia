// tests/ctl 第二阶段公用：模拟硬件 + 可控时钟 + 简单的气路物理（阀门线圈 → 釜压），驱动程序引擎 / 泄漏测试 / 容积标定 / 自整定。
//   PRig r(settings);          // 压力总线换成不抖动的模拟主站，读数由 Physics 按阀态积分得出
//   r.phy.supply = {60, 200, 200};   // 惰性 / A / B 的减压阀出口压力 psi
//   r.advance(ms) / r.until(pred, max_ms)
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdlib>
#include <unistd.h>
#include <functional>
#include <memory>
#include <string>

#include "core/program/program_step.hpp"
#include "ctl/ctl_automation.hpp"
#include "ctl_test_util.hpp"
#include "store/data_logger.hpp"
#include <cstdint>
#include <vector>

namespace ctltest {

using namespace reactor;
namespace prog = reactor::core::program;

// 总管 + 8 个釜的理想气体模型（表压 psi）：开气源 → 总管很快到减压阀压力；开排空 → 总管很快到 0；
// 开 SVn → 釜与总管按 k 交换（没开气源 / 排空时总管按体积比守恒）；每路可设泄漏 psi/h。
struct Physics {
    std::array<double, 3> supply{60, 200, 200};
    std::array<double, 8> p{};
    double pm = 0;
    double vm = 2.0;                                       // 总管体积 mL
    std::array<double, 8> vg{8, 8, 8, 8, 8, 8, 8, 8};      // 气相体积 mL
    std::array<double, 8> leak_psi_h{};
    double k = 1.5;                                        // 1/s

    void step(double dt, const std::array<bool, 12>& v) {
        int gas = -1;
        for (int g = 0; g < 3; ++g)
            if (v[static_cast<size_t>(8 + g)]) gas = g;
        bool vent = v[11];
        double a = std::min(1.0, dt / 0.05);
        if (gas >= 0) pm += (supply[static_cast<size_t>(gas)] - pm) * a;
        else if (vent) pm += (0 - pm) * a;
        for (size_t i = 0; i < 8; ++i) {
            if (v[i]) {
                double d = k * dt * (pm - p[i]);
                p[i] += d;
                if (gas < 0 && !vent) pm -= d * vg[i] / vm;
            }
            p[i] = std::max(0.0, p[i] - leak_psi_h[i] / 3600.0 * dt);
        }
    }
};

struct PRig {
    ctl::ManualClock clock;
    std::shared_ptr<hw::MockModbusMaster> press;
    std::unique_ptr<hw::Hardware> hw;
    std::unique_ptr<store::DataLogger> logger;
    std::unique_ptr<ctl::ControlCore> core;
    std::unique_ptr<ctl::Automation> au;
    Physics phy;
    int64_t phy_ms = 0;
    std::string dir;

    explicit PRig(store::SettingsState st = {}, bool with_logger = false) {
        hw = quiet_pressure_hw(press);
        if (with_logger) {
            char tmpl[] = "/tmp/reactor-ctl2-test-XXXXXX";
            if (char* d = mkdtemp(tmpl)) dir = d;
            logger = std::make_unique<store::DataLogger>(dir);
        }
        ctl::CoreOptions opt;
        opt.threaded = false;
        opt.watchdog = false;
        ctl::CoreDeps d;
        d.hw = hw.get();
        d.clock = &clock;
        d.logger = logger.get();
        d.settings = st;
        core = std::make_unique<ctl::ControlCore>(std::move(d), opt);
        ctl::AutomationDeps ad;
        ad.core = core.get();
        ad.logger = logger.get();
        au = std::make_unique<ctl::Automation>(ad);
        core->start();
        phy_ms = clock.mono_ms();
        sync();
        core->settle();
        advance(1200 * 6);  // 过开机宽限期，模块在线
        for (int id = 1; id <= 8; ++id) core->channel_mut(id).set_state(core::ReactorState::Ready);  // 已启用
    }
    ~PRig() {
        au.reset();
        if (core) core->shutdown(false);
        if (logger) logger->stop();
        if (!dir.empty() && std::system(("rm -rf '" + dir + "'").c_str()) != 0) {}
    }

    ctl::ControlCore& c() { return *core; }
    ctl::ProgramEngine& pe() { return au->programs(); }

    std::array<bool, 12> valves() const {
        std::array<bool, 12> v{};
        for (int i = 0; i < 12; ++i) v[static_cast<size_t>(i)] = hw->io.open_from(hw->sim_main->coil(hw->io.slave(), static_cast<uint16_t>(i)));
        return v;
    }
    void sync() {
        for (int i = 0; i < 8; ++i) {
            double psi = std::clamp(phy.p[static_cast<size_t>(i)], 0.0, 600.0);
            press->set_input(1, static_cast<uint16_t>(i), raw_of_psi(psi));
        }
    }
    void integrate(int64_t t) {
        auto v = valves();
        while (phy_ms < t) {
            int64_t d = std::min<int64_t>(10, t - phy_ms);
            phy.step(static_cast<double>(d) / 1000.0, v);
            phy_ms += d;
        }
        sync();
    }
    void advance(int64_t ms) {
        int64_t target = clock.mono_ms() + ms;
        core->settle();
        for (;;) {
            auto due = core->loop().next_due();
            bool last = !due || *due > target;
            int64_t t = last ? target : std::max(*due, clock.mono_ms());
            integrate(t);
            clock.set_mono(t);
            if (last) break;
            core->loop().fire_due(t);
            core->settle();
        }
        core->settle();
    }
    // 推进直到 pred() 成立（每 step_ms 查一次），返回是否成立
    bool until(const std::function<bool()>& pred, int64_t max_ms, int64_t step_ms = 500) {
        for (int64_t t = 0; t < max_ms; t += step_ms) {
            if (pred()) return true;
            advance(step_ms);
        }
        return pred();
    }
    bool phase_is(int ch, prog::ProgramRunPhase ph) {
        auto* ri = pe().run_info(ch);
        return ri && ri->phase == ph;
    }
    bool run_ended(int ch) {
        auto* ri = pe().run_info(ch);
        return ri && !ri->is_active();
    }
    int count_title(const std::string& t) {
        int n = 0;
        for (const auto& a : core->alarms().items())
            if (a.title == t) ++n;
        return n;
    }
    bool has_title(const std::string& t) { return count_title(t) > 0; }
    // 标题按发生先后
    std::vector<std::string> titles_containing(const std::string& part) {
        std::vector<std::string> v;
        for (const auto& a : core->alarms().items())
            if (a.title.find(part) != std::string::npos) v.push_back(a.title);
        std::reverse(v.begin(), v.end());
        return v;
    }
};

// 一步：类型 + 参数（psi）→ StepSnapshot（编号由 steps() 统一给）
struct S {
    prog::StepType t;
    prog::Params p;
    std::vector<prog::Segment> segs = {};
};

inline std::vector<prog::StepSnapshot> steps(const std::vector<S>& in) {
    std::vector<prog::StepSnapshot> out;
    int ord = 1;
    for (const auto& s : in) {
        auto ps = prog::ProgramStep::create(s.t);
        for (const auto& kv : s.p) ps.with(kv.first, kv.second);
        if (!s.segs.empty()) ps.segments() = s.segs;
        ps.set_ordinal(ord++);
        out.push_back(ps.to_snapshot());
    }
    return out;
}

inline std::string ids(const std::vector<int>& v) {
    std::string s;
    for (size_t i = 0; i < v.size(); ++i) s += (i ? "," : "") + std::to_string(v[i]);
    return s;
}

inline store::SettingsState fast_settings() {
    store::SettingsState st;
    st.settings.approach_delta = 1;     // 定值升温：首个 SP = 目标 − 1 ℃，模拟 PV 每周期 0.5 ℃ 很快到位
    st.settings.leak_settle_min = 1;
    st.settings.leak_measure_min = 5;
    st.settings.leak_test_psi = 50;
    return st;
}

}  // namespace ctltest
