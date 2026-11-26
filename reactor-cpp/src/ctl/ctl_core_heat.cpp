// 控制核心 · 加热：定值防超调逼近、16 段曲线、SP 下发、启动 / 停止加热。
// C#: ViewModels/MainViewModel.cs（StartFixedHeat / SetFixedTarget / WriteTempSpSafeAsync / WriteCurveSpAsync / StepApproaches /
//     Tick 里的曲线引擎 / StopHeatCoreAsync / ReportHeatStartFailed）+ Commands/OperatorCommands.Channels.cs（TryStartProfile 的硬件部分）
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>

#include "core/num_format.hpp"
#include "ctl/ctl_core.hpp"

namespace reactor::ctl {

using core::ReactorState;
using core::fmt_dec;

namespace {
std::string rv(int id) { return "RV" + std::to_string(id); }

core::ApproachParams approach_params(const store::AppSettings& s) {
    core::ApproachParams p;
    p.delta = s.approach_delta;
    p.rate = s.approach_rate;
    p.settle_band = s.approach_settle_band;
    p.settle_sec = s.approach_settle_sec;
    p.fast_rate = s.approach_fast_rate;
    p.final_zone = s.approach_final_zone;
    return p;
}

void enter_heating(Channel& c, const std::optional<std::string>& started_by) {
    if (!started_by) return;
    if (c.state == ReactorState::Idle || c.state == ReactorState::Done || c.state == ReactorState::Ready)
        c.set_state(ReactorState::Heating);  // 加热真的开始了才显示「升温中」
    c.started_by = started_by;
}
}  // namespace

void ControlCore::report_heat_start_failed(int id) {
    notice("err", core::chan_name(id) + " 启动加热失败（温控通讯）· 请检查温控后重新启动");
    alarms_.log("err", rv(id), "启动加热失败", "AI-8 通讯失败，加热输出未启动", "alarm.heatStartFailed");
}

void ControlCore::start_heat_chain(int id, double band_temp, double initial_sp, std::function<void(bool)> cb) {
    temp_.start_channel(id, band_temp, initial_sp, stop_.current(), std::move(cb));
}

void ControlCore::write_temp_sp(int id, double sp, double band_temp, std::function<void(bool)> cb) {
    temp_.write_sp_safe(id, sp, band_temp, std::move(cb));
}

void ControlCore::start_fixed_heat(int id, std::optional<double> target, std::optional<std::string> started_by) {
    auto& c = ch_[idx(id)];
    if (target) c.tsp = *target;
    double pv = c.t_online && c.t > 1 ? c.t : 25;  // PV 无效（温控离线 / 读数为 0）按室温规划
    int64_t now = clock_->mono_ms();
    double sp = c.approach.start(c.tsp, pv, now, approach_params(settings_.settings));
    c.approach.write_in_flight = true;
    enter_heating(c, started_by);
    begin_heat_watch(id, c.tsp, opt_.heat_band_c);
    double band_temp = c.tsp;
    temp_.start_channel(id, band_temp, sp, stop_.current(), [this, id, sp](bool ok) {
        auto& ch = ch_[idx(id)];
        ch.approach.mark_written(sp, clock_->mono_ms());  // SP 通讯失败也会在 30 s 强制重写时补发（同 C#）
        ch.approach.write_in_flight = false;
        if (!ok) report_heat_start_failed(id);
    });
}

void ControlCore::retarget(int id, double target) {
    auto& c = ch_[idx(id)];
    c.tsp = target;
    if (c.approach.active()) {
        c.approach.retarget(c.tsp, c.t, clock_->mono_ms(), approach_params(settings_.settings));
        if (c.heat_watch.active) begin_heat_watch(id, target, c.heat_watch.band);
    } else {
        temp_.write_sp_safe(id, c.tsp, c.tsp, nullptr);
    }
}

void ControlCore::start_band_heat(int id, double sp) {
    temp_.start_channel(id, sp, sp, stop_.current(), [this, id](bool ok) {
        if (!ok) report_heat_start_failed(id);
    });
}

void ControlCore::set_sp_mode(int id, const std::string& mode) {
    auto& c = ch_[idx(id)];
    if (mode != "fixed" && mode != "curve") return;
    if (c.sp_mode == mode) return;
    if (mode == "fixed" && c.profile.running()) c.profile.stop();
    if (mode == "curve") c.approach.stop();
    c.sp_mode = mode;
}

void ControlCore::set_profile(int id, const std::vector<core::ProfileSegment>& segs) {
    auto& c = ch_[idx(id)];
    int n = std::min<int>(static_cast<int>(segs.size()), core::temp_profile::kMaxSegments);
    for (int i = 0; i < n; ++i) c.profile.segments[static_cast<size_t>(i)] = segs[static_cast<size_t>(i)];
    if (n > 0) c.profile.seg_count = n;
}

void ControlCore::start_curve(int id, std::optional<std::string> started_by) {
    auto& c = ch_[idx(id)];
    double start_pv = c.t_online && c.t > 1 ? c.t : 25;  // 段 1 从室温起步，避免从 0℃ 开始爬
    double band_temp = start_pv;
    for (int i = 1; i <= core::temp_profile::kMaxSegments; ++i)
        if (c.profile.enabled(i)) band_temp = std::max(band_temp, c.profile.segments[static_cast<size_t>(i - 1)].temp);
    c.profile.start(start_pv, clock_->mono_ms());
    enter_heating(c, started_by);
    // 硬件链放在曲线启动之后：全链没成功就停曲线，避免「曲线在跑、仪表不出力」
    temp_.start_channel(id, band_temp, start_pv, stop_.current(), [this, id](bool ok) {
        if (ok) return;
        auto& ch = ch_[idx(id)];
        if (ch.profile.running()) ch.profile.stop();
        report_heat_start_failed(id);
    });
}

void ControlCore::stop_curve(int id) { ch_[idx(id)].profile.stop(); }

void ControlCore::stop_heat(int id, std::function<void(bool)> cb) {
    auto& c = ch_[idx(id)];
    if (c.profile.running()) c.profile.stop();
    c.approach.stop();
    end_heat_watch(id);
    if (!temp_.configured()) { if (cb) cb(true); return; }
    temp_.stop_channel(id, [this, id, cb](bool ok) {
        if (!ok) alarms_.log("err", rv(id), "停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作", "alarm.heatStopFailed");
        if (cb) cb(ok);
    });
}

// 曲线升温引擎：AI-8 无原生多段程序，按分段线性插值周期下发 SP（C# Tick）
void ControlCore::step_curves() {
    int64_t now = clock_->mono_ms();
    for (auto& c : ch_) {
        if (c.sp_mode != "curve" || !c.profile.running()) continue;
        if (c.state == ReactorState::Alarm) {  // 超温 / 超压报警：自动停止曲线，仪表保持最后成功下发的给定值
            c.profile.stop();
            notice("err", core::chan_name(c.id) + " 报警，曲线升温已自动停止");
            continue;
        }
        double target = c.profile.current_target(now);
        c.tsp = target;  // 目标温度跟随曲线当前值
        if (c.curve_write_in_flight || !c.profile.should_write(target, now)) continue;
        c.curve_write_in_flight = true;
        int id = c.id;
        temp_.write_sp_safe(id, target, target, [this, id, target](bool ok) {
            auto& ch = ch_[idx(id)];
            ch.curve_write_in_flight = false;
            if (ok) ch.profile.mark_written(target, clock_->mono_ms());  // 仅在写成功后标记，失败下个周期重试
        });
    }
}

// 定值加热防超调逼近（C# StepApproaches）
void ControlCore::step_approaches(bool real_t) {
    int64_t now = clock_->mono_ms();
    auto ap = approach_params(settings_.settings);
    for (auto& c : ch_) {
        auto& a = c.approach;
        if (!a.active()) continue;
        if (c.sp_mode != "fixed") { a.stop(); continue; }
        if (c.state == ReactorState::Alarm) {
            a.stop();
            notice("err", core::chan_name(c.id) + " 报警，防超调逼近已停止 · 保持当前给定值");
            continue;
        }
        if (std::fabs(c.tsp - a.target()) > 1e-6) a.retarget(c.tsp, c.t, now, ap);  // 目标被其他途径改了：重新规划
        if (!real_t) continue;  // 温控离线：PV / 输出是旧值，不推进
        double op = temp_.ch(c.id).output_pct;
        switch (a.step(now, c.t, op, ap)) {
            case core::ApproachEvent::PreheatDone:
                alarms_.log("info", rv(c.id), "预热完成 · 实测冲温 " + fmt_dec(a.preheat_overshoot(), 1, 1) + "℃",
                            "开始逼近 " + fmt_dec(a.current_sp(), 1, 1) + " → " + fmt_dec(a.target(), 1, 1) + "℃ · 当前预留温差 Δ=" +
                                fmt_dec(ap.delta, 1) + "℃",
                            "heat.preheatDone");
                break;
            case core::ApproachEvent::ExoPause:
                notice("warn", core::chan_name(c.id) + " 检测到放热（输出≈0 仍在升温）· 暂停上推");
                alarms_.log("info", rv(c.id), "逼近中检测到放热，暂停上推",
                            "PV " + fmt_dec(c.t, 1, 1) + "℃ / 当前 SP " + fmt_dec(a.current_sp(), 1, 1) + "℃ / 目标 " +
                                fmt_dec(a.target(), 1, 1) + "℃",
                            "heat.exoPause");
                break;
            case core::ApproachEvent::OverLimit:
                notice("err", core::chan_name(c.id) + " 放热超出控温能力 · 输出已为 0，温度仍高于目标 +" + fmt_dec(core::heat_approach::kOverLimit, 1) + "℃");
                alarms_.log("warn", rv(c.id), "放热超出控温能力",
                            "PV " + fmt_dec(c.t, 1, 1) + "℃ / 目标 " + fmt_dec(a.target(), 1, 1) + "℃ · 加热输出已为 0（无冷却，无法压回）",
                            "heat.overLimit");
                break;
            default:
                break;
        }
        if (a.write_in_flight || !a.should_write(now)) continue;
        double sp = a.sp_to_write();
        a.write_in_flight = true;
        int id = c.id;
        temp_.write_sp_safe(id, sp, a.target(), [this, id, sp](bool ok) {
            auto& ch = ch_[idx(id)];
            ch.approach.write_in_flight = false;
            if (ok) ch.approach.mark_written(sp, clock_->mono_ms());
        });
    }
}

}  // namespace reactor::ctl
