// 程序引擎 · 气路排队、吹总管、升温曲线、后台稳压、超压中止。C#: ViewModels/ProgramViewModel.Run.cs
// （AcquireGasAsync / ThrowIfWaitUnsafe / HolderText / RunCurveFor / StartRegulation / RegulateAsync / StopRegulationAsync）
// + StepGas.LineFlushAsync（MainViewModel.RunPurgeAsync）。
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>

#include "core/heat_curve_segments.hpp"
#include "core/num_format.hpp"
#include "core/program/program_step.hpp"
#include "core/program/step_fields.hpp"
#include "ctl/ctl_program_engine_impl.hpp"
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::ctl {

using core::ReactorState;
using core::fmt_dec;
using namespace core::program;

namespace {
std::string rvs(int rv) { return "RV" + std::to_string(rv); }
constexpr int64_t kGasPollMs = 500;      // C# AcquireGasAsync：每 0.5 s 再看一次
constexpr int64_t kRegPeriodMs = 2000;   // C# RegulateAsync：每 2 s 读一次压力
constexpr int64_t kCurveSliceMs = 2000;  // C# RunCurveFor：每 2 s 推进一次 SP
}  // namespace

// ============================ 取气路（排队） ============================

std::string ProgramEngine::holder_text() const {
    const auto& l = core_.gas_lease();
    if (auto h = l.owner_rv()) return core::chan_name(*h);
    return l.busy() ? l.task() : std::string("其他程序");
}

void ProgramEngine::acquire(const std::shared_ptr<Run>& r, Acquire& a, const std::string& task, bool vessel, bool quiet,
                            std::function<void(int64_t)> granted, std::function<void(const std::string&)> failed,
                            std::function<void()> aborted) {
    acquire_end(r, a);
    a = Acquire{};
    a.active = true;
    a.gen = r->gen;
    a.task = task;
    a.vessel = vessel;
    a.quiet = quiet;
    a.granted = std::move(granted);
    a.failed = std::move(failed);
    a.aborted = std::move(aborted);
    const auto& lease = core_.gas_lease();
    std::vector<int> parts{r->rv};
    std::vector<int> watch = vessel ? std::vector<int>{r->rv} : std::vector<int>{};
    // 1) 气路由泄漏测试 / 标定占用 → 立即故障（泄漏测试优先级不变）
    if (lease.busy() && lease.owner() != core::GasOwner::Program) {
        auto f = a.failed;
        std::string msg = lease.task() + "进行中，请等待完成或先停止";
        a.active = false;
        f(msg);
        return;
    }
    // 2) 气路空闲且无人排队 → 立即取（单程序时与原先完全一致，不多出任何事件）
    if (!lease.busy() && queue_.count() == 0) {
        r->gas_gen = core_.begin_gas(core::GasOwner::Program, task, parts, watch, r->rv);
        auto g = a.granted;
        a.active = false;
        g(r->gas_gen);
        return;
    }
    // 3) 排队
    int64_t now = core_.clock().mono_ms();
    a.ticket = queue_.enqueue(r->rv, now);
    a.queued = true;
    a.since = now;
    a.state_at = core_.channel(r->rv).state;
    a.holder = holder_text();
    a.base_status = r->info.status;
    acquire_poll(r, a);
}

void ProgramEngine::acquire_poll(const std::shared_ptr<Run>& r, Acquire& a) {
    if (!a.active || a.gen != r->gen) return;
    const auto& lease = core_.gas_lease();
    int rv = r->rv;
    auto fault = [this, r, &a](const std::string& msg) {
        auto f = a.failed;
        acquire_end(r, a);
        f(msg);
    };
    // 等待期间泄漏测试占到了气路：按入口同一规则立即故障
    if (lease.busy() && lease.owner() != core::GasOwner::Program) {
        fault(lease.task() + "进行中，请等待完成或先停止");
        return;
    }
    int64_t now = core_.clock().mono_ms();
    std::optional<std::string> why;
    if (!lease.busy() && queue_.is_head(a.ticket)) {
        auto gb = gas_block_for(rv, a.vessel);
        if (gb) why = gb->message;
        else {
            queue_.remove(a.ticket);
            a.queued = false;
            std::vector<int> parts{rv};
            std::vector<int> watch = a.vessel ? std::vector<int>{rv} : std::vector<int>{};
            r->gas_gen = core_.begin_gas(core::GasOwner::Program, a.task, parts, watch, rv);
            if (a.announced) {
                if (!a.quiet) mark_waiting(r, std::nullopt);
                log("info", core::chan_name(rv) + " 程序 · 气路就位",
                    "等待 " + fmt_dec(static_cast<double>(now - a.since) / 1000.0, 0) + "s（此前 " + a.holder + " 占用）· " + a.task,
                    "program.gasGranted");
            }
            auto g = a.granted;
            int64_t gen = r->gas_gen;
            acquire_end(r, a);
            g(gen);
            return;
        }
    }
    if (!a.announced) {
        a.announced = true;
        a.holder = holder_text();
        r->info.waiting_on = lease.owner_rv();
        if (!a.quiet) mark_waiting(r, why ? *why : a.holder + " 占用中");
        if (!why) {
            auto w = queue_.waiting();
            int pos = static_cast<int>(std::find(w.begin(), w.end(), rv) - w.begin()) + 1;
            log("info", core::chan_name(rv) + " 程序 · 等待气路", a.task + " · " + a.holder + " 占用中，排队第 " + std::to_string(pos) + " 位",
                "program.gasWaiting");
        } else {
            log("info", core::chan_name(rv) + " 程序 · 等待气路", a.task + " · " + *why + "，等待", "program.gasWaiting");
        }
        a.blocked_why = why;
    } else if (why && why != a.blocked_why) {
        // 排到队首但公共检查不通过：记一次原因（变化时再记），继续等待
        a.blocked_why = why;
        if (!a.quiet) mark_waiting(r, *why);
        log("warn", core::chan_name(rv) + " 程序 · 等待气路", a.task + " · " + *why + "，继续等待", "program.gasWaiting");
    }
    double max_min = core_.settings().settings.program_gas_wait_min;
    std::string reason = why ? *why : holder_text() + " 占用中";
    if (queue_.timed_out(a.ticket, now, static_cast<int64_t>(std::llround(max_min * 60000.0)))) {
        fault("等待气路超过 " + fmt_dec(max_min, 1) + " 分钟（" + reason + "）");
        return;
    }
    if (!a.quiet)
        set_status(r, a.base_status + " · 等待气路（" + reason + "）· 已等 " + fmt_dec(static_cast<double>(now - a.since) / 1000.0, 0) + "s");
    uint64_t g = r->gen;
    a.timer = core_.loop().after(kGasPollMs, [this, r, &a, g] {
        a.timer = 0;
        if (!a.active || g != r->gen) return;
        // C# ThrowIfWaitUnsafe：本通道报警 → 失败；从非 Done/Idle 转入 Done/Idle（被外部停止）→ 中止
        const auto& c = core_.channel(r->rv);
        if (c.state == ReactorState::Alarm) {
            auto f = a.failed;
            acquire_end(r, a);
            f(core::chan_name(r->rv) + " 超温 / 超压报警");
            return;
        }
        if ((c.state == ReactorState::Done || c.state == ReactorState::Idle) && c.state != a.state_at) {
            auto f = a.aborted;
            acquire_end(r, a);
            f();
            return;
        }
        acquire_poll(r, a);
    });
}

void ProgramEngine::acquire_end(const std::shared_ptr<Run>& r, Acquire& a) {
    if (a.timer) core_.loop().cancel(a.timer);
    a.timer = 0;
    if (a.queued) queue_.remove(a.ticket);
    a.queued = false;
    if (a.active) {
        r->info.waiting_on.reset();
        if (a.announced && r->info.waiting_gas()) mark_waiting(r, std::nullopt);
    }
    a.active = false;
}

// ============================ 吹总管 ============================

void ProgramEngine::line_flush(const std::shared_ptr<Run>& r, const Action& a) {
    uint64_t g = r->gen;
    std::string prefix = a.text;
    r->ops->purge(
        a.gas, a.count, step_engine::kFlushFillSec, step_engine::kFlushVentSec,
        [this, r, prefix](int c, int n, const std::string& ph) {
            set_status(r, prefix + " " + std::to_string(c) + "/" + std::to_string(n) + " · " + ph);
        },
        [this, r, g] {
            if (g == r->gen) tick(r, StepEvent::LineFlushDone, std::nullopt, false);
        });
}

// ============================ 升温曲线 ============================

void ProgramEngine::run_curve(const std::shared_ptr<Run>& r, const Action& a) {
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    if (c.profile.running()) c.profile.stop();
    c.approach.stop();
    double start_pv = c.t_online && c.t > 1 ? c.t : 25;
    auto conv = core::heat_curve_to_profile(to_heat_segments(a.segments), start_pv);
    if (conv.empty()) {  // 没有有效段：什么也不启动（步骤改按定值）
        tick(r, StepEvent::CurveRejected, std::nullopt, false);
        return;
    }
    core::TempProfile prof;
    prof.seg_count = static_cast<int>(conv.size());
    double band_temp = start_pv;
    for (size_t i = 0; i < conv.size(); ++i) {
        prof.segments[i] = conv[i];
        band_temp = std::max(band_temp, conv[i].temp);
    }
    uint64_t g = r->gen;
    std::string head = a.text;
    core_.start_heat_chain(rv, band_temp, start_pv, [this, r, g, prof, start_pv, head, rv](bool ok) mutable {
        if (r->cancelled) {
            // 全链启动期间被中止：中止时的停加热可能先于这次启动落到仪表，收尾必须再停一次
            r->heat_touched_after_abort = true;
            return;
        }
        if (g != r->gen) return;
        if (!ok) {
            core_.emit_notice("err", core::chan_name(rv) + " 启动加热失败（温控通讯）· 请检查温控后重新启动");
            core_.alarms().log("err", rvs(rv), "启动加热失败", "AI-8 通讯失败，加热输出未启动", "alarm.heatStartFailed");
            step_fault(r, "升温曲线启动失败（温控通讯）");
            return;
        }
        int64_t now = core_.clock().mono_ms();
        prof.start(start_pv, now);
        r->curve = prof;
        r->curve_head = head;
        log("info", core::chan_name(rv) + " 程序 · 升温曲线启动",
            std::to_string(prof.seg_count) + " 段 · 终温 " + fmt_n(prof.segments[static_cast<size_t>(prof.seg_count - 1)].temp) +
                "℃ · ≈ " + fmt_n(round_even(prof.total_minutes())) + " min",
            "program.curve");
        curve_slice(r, g);
    });
}

void ProgramEngine::curve_slice(const std::shared_ptr<Run>& r, uint64_t g) {
    if (g != r->gen || !r->curve) return;
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    int64_t now = core_.clock().mono_ms();
    if (r->curve->is_complete(now)) {
        r->curve.reset();
        tick(r, StepEvent::CurveFinished, std::nullopt, false);
        return;
    }
    double target = r->curve->current_target(now);
    c.tsp = target;  // 界面目标温度跟随曲线当前值
    if (!r->curve_inflight && r->curve->should_write(target, now)) {
        r->curve_inflight = true;
        core_.write_temp_sp(rv, target, target, [this, r, g, target](bool ok) {
            r->curve_inflight = false;
            if (g != r->gen || !r->curve) return;
            if (ok) r->curve->mark_written(target, core_.clock().mono_ms());
        });
    }
    std::string pv = c.t_online ? fmt_dec(c.t, 1, 1) + "℃" : "—";
    set_status(r, r->curve_head + " · 曲线 " + r->curve->status_text() + " · 当前 " + pv);
    r->curve_timer = core_.loop().after(kCurveSliceMs, [this, r, g] {
        r->curve_timer = 0;
        if (g != r->gen || !r->curve) return;
        // C# WaitSliceAsync 的 ThrowIfStepUnsafe
        const auto& ch = core_.channel(r->rv);
        if (ch.state == ReactorState::Alarm) { step_fault(r, core::chan_name(r->rv) + " 超温 / 超压报警"); return; }
        if (ch.state == ReactorState::Done || ch.state == ReactorState::Idle) { cancel_flow(r, true); return; }
        curve_slice(r, g);
    });
}

// ============================ 后台稳压（2 s 状态机） ============================

void ProgramEngine::start_regulation(const std::shared_ptr<Run>& r, int gas, double target, double refill_pct) {
    stop_regulation(r);
    auto& g = r->reg;
    g.active = true;
    g.gas = gas;
    g.target = target;
    g.band = std::max(1.0, target * refill_pct / 100.0);
    g.gain.reset();
    g.blocked_since.reset();
    g.blocked_why.reset();
    g.pulsing = false;
    ++g.gen;
    core_.begin_pressure_watch(r->rv, target);  // 压力偏离三级警告（设计文档 §6）：充压完成、稳压打开时开始监视
    reg_schedule(r);
}

void ProgramEngine::stop_regulation(const std::shared_ptr<Run>& r) {
    auto& g = r->reg;
    if (!g.active) return;
    g.active = false;
    ++g.gen;
    if (g.timer) core_.loop().cancel(g.timer);
    g.timer = 0;
    acquire_end(r, r->reg_acq);
    g.ops->cancel();
    core_.end_pressure_watch(r->rv);
    if (g.pulsing) {
        g.pulsing = false;
        int64_t gen = r->gas_gen;
        // 脉冲进行中被停：只有气路仍归本段租约时才全关并释放（被超压联锁 / 全部停止中止后可能已被释放）
        g.ops->when_idle([this, gen] {
            const auto& l = core_.gas_lease();
            if (l.owner() == core::GasOwner::Program && l.generation() == gen) {
                core_.clear_valve_hold();
                core_.close_all_valves();
                core_.end_gas(core::GasOwner::Program, gen);
            }
        });
    }
}

void ProgramEngine::reg_schedule(const std::shared_ptr<Run>& r) {
    auto& g = r->reg;
    uint64_t gen = g.gen;
    g.timer = core_.loop().after(kRegPeriodMs, [this, r, gen] {
        r->reg.timer = 0;
        reg_tick(r, gen);
    });
}

void ProgramEngine::reg_fault(const std::shared_ptr<Run>& r, const std::string& why, bool log_err) {
    if (r->finishing) return;
    // 稳压是程序的一部分，压力无人维持不能让程序继续加热：按程序故障收尾（C# PendingFault + 取消流程令牌）
    r->pending_fault = "稳压：" + why;
    if (log_err) log("err", core::chan_name(r->rv) + " 稳压中止", why, "program.regulationFailed");
    r->reg.pulsing = false;
    cancel_flow(r, false);
}

void ProgramEngine::reg_tick(const std::shared_ptr<Run>& r, uint64_t gen) {
    auto& g = r->reg;
    if (!g.active || gen != g.gen || r->finishing) return;
    int rv = r->rv;
    const auto& c = core_.channel(rv);
    // 本通道报警 / 被外部置停：稳压退出即可，主流程的安全检查会按原文中止程序
    if (c.state == ReactorState::Alarm || c.state == ReactorState::Done || c.state == ReactorState::Idle) {
        g.active = false;
        ++g.gen;
        core_.end_pressure_watch(rv);
        return;
    }
    g.ops->set_fault([this, r](const std::string& why) { reg_fault(r, why, true); });
    g.ops->read([this, r, gen, rv](const PressureSample& s) {
        auto& g2 = r->reg;
        if (!g2.active || gen != g2.gen) return;
        double p = s.psi[static_cast<size_t>(rv - 1)];
        if (p >= g2.target - g2.band) { reg_schedule(r); return; }
        int64_t now = core_.clock().mono_ms();
        if (auto why = gas_block_for(rv, true)) {
            // 公共检查不通过：本周期不取气路（不能整帧写阀覆盖手动阀态 / 不能在超压未解除时补气），等待；超过上限按程序故障
            if (!g2.blocked_since) g2.blocked_since = now;
            if (why->message != g2.blocked_why) {
                g2.blocked_why = why->message;
                log("warn", core::chan_name(rv) + " 稳压 · 等待气路",
                    why->message + " · 压力 " + core::format_p(p, unit_) + " 低于 " + core::format_p(g2.target - g2.band, unit_) + "，暂不补压",
                    "program.regulation");
            }
            double max_min = core_.settings().settings.program_gas_wait_min;
            if (max_min > 0 && static_cast<double>(now - *g2.blocked_since) / 60000.0 >= max_min) {
                reg_fault(r, "等待气路超过 " + fmt_dec(max_min, 1) + " 分钟（" + why->message + "）", true);
                return;
            }
            reg_schedule(r);
            return;
        }
        if (g2.blocked_since) {
            log("info", core::chan_name(rv) + " 稳压 · 气路恢复",
                "等待 " + fmt_dec(static_cast<double>(now - *g2.blocked_since) / 1000.0, 0) + "s 后继续补压", "program.regulation");
            g2.blocked_since.reset();
            g2.blocked_why.reset();
        }
        acquire(
            r, r->reg_acq, core::chan_name(rv) + " 程序 · 稳压", true, true,
            [this, r, gen](int64_t gas_gen) { reg_pulse(r, gen, gas_gen); },
            [this, r, gen](const std::string& why) {
                if (gen == r->reg.gen) reg_fault(r, why, true);
            },
            [this, r, gen] {
                if (gen == r->reg.gen) cancel_flow(r, true);
            });
    });
}

// 一次补压脉冲（短租约）：开阀 → 保持 sec → 关阀静置 1 s → 读压 → 释放。sec 按实测增益补 60% 差值（0.1–1.5 s），首次 0.2 s
void ProgramEngine::reg_pulse(const std::shared_ptr<Run>& r, uint64_t gen, int64_t gas_gen) {
    auto& g = r->reg;
    if (!g.active || gen != g.gen) {
        // 取到时稳压已停：立即还回去
        const auto& l = core_.gas_lease();
        if (l.owner() == core::GasOwner::Program && l.generation() == gas_gen) core_.end_gas(core::GasOwner::Program, gas_gen);
        return;
    }
    g.pulsing = true;
    int rv = r->rv;
    auto end_pulse = [this, r, gen, gas_gen] {
        auto& g3 = r->reg;
        g3.pulsing = false;
        g3.ops->when_idle([this, gas_gen] {
            const auto& l = core_.gas_lease();
            if (l.owner() == core::GasOwner::Program && l.generation() == gas_gen) {
                core_.clear_valve_hold();
                core_.close_all_valves();
                core_.end_gas(core::GasOwner::Program, gas_gen);
            }
        });
        if (g3.active && gen == g3.gen) reg_schedule(r);
    };
    if (!g.ops->check()) return;
    g.ops->read([this, r, gen, rv, end_pulse](const PressureSample& s) {
        auto& g2 = r->reg;
        if (gen != g2.gen) return;
        double p = s.psi[static_cast<size_t>(rv - 1)];  // 排队期间压力可能变化：按取到租约时的读数补
        if (p >= g2.target - g2.band) { end_pulse(); return; }
        double sec = g2.gain && *g2.gain > 0.05 ? std::clamp(0.6 * (g2.target - p) / *g2.gain, 0.1, 1.5) : 0.2;
        core::ValveFrame f = core::valve_frame::gas(g2.gas, {rv});
        core::ValveFrame closed = core::valve_frame::all_closed();
        auto* ops = g2.ops.get();
        ops->apply(f, [this, r, gen, rv, end_pulse, sec, f, closed, p, ops] {
            ops->hold(f, sec, [this, r, gen, rv, end_pulse, sec, closed, p, ops] {
                ops->apply(closed, [this, r, gen, rv, end_pulse, sec, closed, p, ops] {
                    ops->hold(closed, 1.0, [this, r, gen, rv, end_pulse, sec, p, ops] {
                        ops->read([this, r, gen, rv, end_pulse, sec, p](const PressureSample& s2) {
                            auto& g4 = r->reg;
                            if (gen != g4.gen) return;
                            double after = s2.psi[static_cast<size_t>(rv - 1)];
                            g4.gain = std::max(0.01, (after - p) / sec);
                            log("info", core::chan_name(rv) + " 稳压补压",
                                core::format_p(p, unit_) + " → " + core::format_p(after, unit_) + " · 目标 " + core::format_p(g4.target, unit_),
                                "program.regulation");
                            end_pulse();
                        });
                    });
                }, gas_ops::kProgramDeadMs);
            });
        }, gas_ops::kProgramDeadMs);
    });
}

// ============================ 无条件中止气路（超压联锁 / 全部停止 / 退出） ============================

void ProgramEngine::on_gas_abort(core::GasAbortCause cause, std::optional<int> channel) {
    if (channel) return;                                   // 单路停止：程序通道由 abort_channel 处理
    if (cause != core::GasAbortCause::OverPressure) return;  // 全部停止（None）随后 abort_all；退出（Shutdown）已先 abort_all
    const auto& lease = core_.gas_lease();
    if (lease.owner() != core::GasOwner::Program || !lease.owner_rv()) return;
    auto r = active(*lease.owner_rv());
    if (!r || r->finishing) return;
    // 超压联锁打到当前持有者：持有气路的程序按失败收尾（其他程序的非气路步骤不受影响）
    r->pending_fault = r->reg.pulsing ? "稳压：气路被超压联锁中止" : "气路被超压联锁中止";
    int64_t gen = lease.generation();
    r->ops->cancel();
    r->reg.ops->cancel();
    r->reg.pulsing = false;
    // 回调里同步收尾：先全关（超压中止期间保持排空），再释放租约（end_gas 会清掉「保持排空」，所以顺序不能反）
    auto close_end = [this, gen] {
        const auto& l = core_.gas_lease();
        if (l.owner() != core::GasOwner::Program || l.generation() != gen) return;
        core_.close_all_valves();
        core_.end_gas(core::GasOwner::Program, gen);
    };
    if (r->ops->in_flight() == 0 && r->reg.ops->in_flight() == 0) close_end();
    else r->ops->when_idle([r, close_end] { r->reg.ops->when_idle(close_end); });
    cancel_flow(r, false);
}

}  // namespace reactor::ctl
