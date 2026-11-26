#include "ctl/ctl_gas_ops.hpp"
#include "core/chan_name.hpp"

#include <algorithm>
#include <cmath>
#include <memory>

#include "core/gas_guard.hpp"
#include "core/heat_guard.hpp"
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace reactor::ctl {

using core::ReactorState;

namespace gas_ops {

std::optional<core::GuardReject> gas_block(const ControlCore& core, bool need_pressure, std::optional<int> self_id, bool program_wait,
                                           const std::vector<int>& program_owned, const std::string& program_gas_pending) {
    core::GasGuardInput in;
    in.io_configured = core.valves().configured();
    in.need_pressure = need_pressure;
    in.analog_configured = core.pressure().configured();
    in.press_online = core.pressure().online();
    const auto& lease = core.gas_lease();
    in.gas_task = lease.busy() ? lease.task() : std::string();
    in.manual_valve = core.manual_valve();
    in.self_id = self_id;
    for (int id = 1; id <= 8; ++id) {
        core::GasChannel c;
        c.id = id;
        c.state = core.channel(id).state;
        c.over_pressure_tripped = core.is_over_pressure_tripped(id);
        c.idle_over_temp_alarm = core.is_idle_over_temp_alarm(id);
        in.channels.push_back(c);
    }
    in.gas_held_by_program = lease.owner() == core::GasOwner::Program;
    in.wait_instead_of_busy = program_wait;
    if (program_wait)
        for (int id : program_owned)
            if (!self_id || id != *self_id) in.program_owned_ids.push_back(id);
    in.program_gas_pending = program_gas_pending;
    return core::gas_guard_check(in);
}

std::optional<core::GuardReject> heat_block(const ControlCore& core, std::optional<int> id, core::PressureUnit unit) {
    core::HeatGuardInput in;
    in.id = id;
    in.temp_configured = core.temp().configured();
    in.temp_online = core.temp().online();
    in.over_temp_tripped = id && core.is_over_temp_tripped(*id);
    in.over_pressure_tripped = id && core.is_over_pressure_tripped(*id);
    const auto& s = core.settings().settings;
    in.stop_heat_on_over_pressure = s.stop_heat_on_over_pressure;
    in.over_temp = s.over_temp;
    in.over_pressure = s.over_pressure;
    in.pressure_unit = unit;
    return core::heat_guard_check(in);
}

std::optional<double> pressure_of(const ControlCore& core, int id) {
    const auto& ps = core.pressure().last();
    if (!core.pressure().online() || !ps.valid()) return std::nullopt;
    return ps.psi[static_cast<size_t>(id - 1)];
}

double temp_or_nan(const ControlCore& core, int id) {
    const auto& tc = core.temp().ch(id);
    if (!core.temp().online() || !tc.online || !(tc.pv > 1)) return NAN;
    return tc.pv;
}

std::optional<std::string> gas_unsafe(const ControlCore& core, core::PressureUnit unit) {
    const auto& lease = core.gas_lease();
    const auto& watch = lease.watch();
    const auto& parts = lease.participants();
    auto has = [](const std::vector<int>& v, int id) { return std::find(v.begin(), v.end(), id) != v.end(); };
    bool program = lease.owner() == core::GasOwner::Program;
    // 停用 / 已结束通道的超温报警只在该通道参与本序列（充压监视）时才中止；程序租约：不参与的其他通道报警，只要不是超压未解除就不中止
    for (int id = 1; id <= 8; ++id) {
        const auto& c = core.channel(id);
        if (c.state != ReactorState::Alarm) continue;
        if (core.is_idle_over_temp_alarm(id) && !has(watch, id)) continue;
        if (program && !has(watch, id) && !has(parts, id) && !core.is_over_pressure_tripped(id)) continue;
        return core::chan_name(id) + " 报警";
    }
    double op = core.settings().settings.over_pressure;
    for (int id : watch) {
        if (id < 1 || id > 8) continue;
        auto p = pressure_of(core, id);
        double v = p ? *p : core.channel(id).p;
        if (v >= op) return core::chan_name(id) + " 超压（" + core::format_p(v, unit) + "）";
    }
    return lease.fault_reason([&core](int id) -> std::optional<ReactorState> {
        if (id < 1 || id > 8) return std::nullopt;
        return core.channel(id).state;
    });
}

}  // namespace gas_ops

void GasOps::fail(const std::string& why) {
    cancel();
    if (fault_) {
        auto f = fault_;  // fault 回调里可能重设 fault_
        f(why);
    }
}

bool GasOps::check() {
    if (extra_)
        if (auto why = extra_()) { fail(*why); return false; }
    if (auto why = gas_ops::gas_unsafe(core_, unit_)) { fail(*why); return false; }
    return true;
}

void GasOps::apply(const core::ValveFrame& f, Then then, int64_t dead_ms) {
    uint64_t ep = epoch_;
    ++inflight_;
    ValveWriteOpts o;
    o.dead_ms = dead_ms;
    core_.write_valves(f, o, [this, ep, f, then](bool ok, const std::string& err) {
        --inflight_;
        if (inflight_ == 0 && !idle_.empty()) {
            auto w = std::move(idle_);
            idle_.clear();
            for (auto& fn : w) fn();
        }
        if (ep != epoch_) return;
        if (!ok) { fail(err.empty() ? std::string("阀门写入失败（IO16R 通讯）") : err); return; }
        core_.set_valve_hold(f);  // C# HoldAsync 的 IO16R 心跳：保持期间每 1 s 重写当前帧
        if (then) then();
    });
}

void GasOps::hold(const core::ValveFrame& f, double sec, Then then) {
    int64_t end = core_.clock().mono_ms() + static_cast<int64_t>(std::llround(std::max(0.0, sec) * 1000.0));
    hold_slice(f, end, epoch_, std::move(then));
}

void GasOps::hold_slice(core::ValveFrame f, int64_t end_ms, uint64_t ep, Then then) {
    if (ep != epoch_) return;
    int64_t left = end_ms - core_.clock().mono_ms();
    if (left <= 0) {
        if (then) then();
        return;
    }
    timer_ = core_.loop().after(std::min<int64_t>(1000, left), [this, f, end_ms, ep, then] {
        timer_ = 0;
        if (ep != epoch_) return;
        if (!check()) return;
        hold_slice(f, end_ms, ep, then);
    });
}

void GasOps::delay(double sec, Then then) {
    uint64_t ep = epoch_;
    timer_ = core_.loop().after(static_cast<int64_t>(std::llround(std::max(0.0, sec) * 1000.0)), [this, ep, then] {
        timer_ = 0;
        if (ep != epoch_) return;
        if (then) then();
    });
}

void GasOps::read(std::function<void(const PressureSample&)> then) {
    uint64_t ep = epoch_;
    core_.pressure_mut().read_now([this, ep, then](bool ok) {
        if (ep != epoch_) return;
        if (ok) { then(core_.pressure().last()); return; }
        core_.pressure_mut().read_now([this, ep, then](bool ok2) {
            if (ep != epoch_) return;
            if (!ok2) { fail("压力读取失败（8AI 通讯）"); return; }
            then(core_.pressure().last());
        });
    });
}

void GasOps::purge(int gas, int cycles, double fill_sec, double vent_sec, std::function<void(int, int, const std::string&)> progress,
                   Then then) {
    auto plan = std::make_shared<std::vector<core::PurgeAction>>(core::purge_plan::build(gas, cycles, fill_sec, vent_sec));
    purge_next(plan, 0, cycles, std::move(progress), std::move(then));
}

void GasOps::purge_next(std::shared_ptr<std::vector<core::PurgeAction>> plan, size_t i, int cycles,
                        std::function<void(int, int, const std::string&)> progress, Then then) {
    if (i >= plan->size()) {
        if (then) then();
        return;
    }
    if (!check()) return;
    const auto& a = (*plan)[i];
    if (progress) progress(a.cycle, cycles, a.phase);
    core::ValveFrame f = a.valves;
    double sec = a.seconds;
    apply(f, [this, plan, i, cycles, progress, then, f, sec] {
        hold(f, sec, [this, plan, i, cycles, progress, then] { purge_next(plan, i + 1, cycles, progress, then); });
    });
}

void GasOps::cancel() {
    ++epoch_;
    if (timer_) core_.loop().cancel(timer_);
    timer_ = 0;
    if (!core_.valves().holding()) return;
    const auto& l = core_.gas_lease();
    bool mine = owner_ == core::GasOwner::None || !l.busy() ||
                (l.owner() == owner_ && (!owner_rv_ || l.owner_rv() == owner_rv_));
    if (mine) core_.clear_valve_hold();
}

void GasOps::when_idle(Then then) {
    if (inflight_ == 0) {
        if (then) then();
        return;
    }
    idle_.push_back(std::move(then));
}

}  // namespace reactor::ctl
