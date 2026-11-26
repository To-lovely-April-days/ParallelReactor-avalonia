// 控制核心 · 每周期安全判定：超温切断、超压联锁、模块掉线 / 恢复、搅拌故障，以及新的三级警告（升温超时、压力偏离）。
// C#: ViewModels/MainViewModel.Logging.cs（CheckOverTemp / SendOverTempStopAsync / CheckOverPressure / FlushOverPressureValves /
//     SendOverPressureValvesAsync / SendOverPressureHeatAsync / TrackModules / TrackStirFault），文案逐字照抄，
//     只把「请使用急停 / 按「急停」」改成「点主页的全部停止」（设计文档 §6：急停已删除）。
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>
#include <cstdio>

#include "core/num_format.hpp"
#include "ctl/ctl_core.hpp"

namespace reactor::ctl {

using core::ReactorState;
using core::fmt_dec;

namespace {
std::string rv(int id) { return "RV" + std::to_string(id); }
std::string ot_key(int id) { return "ot:" + std::to_string(id); }
std::string op_key(int id) { return "op:" + std::to_string(id); }
std::string opv_key(int id) { return "opv:" + std::to_string(id); }
std::string oph_key(int id) { return "oph:" + std::to_string(id); }
}  // namespace

// ============================ 模块掉线 / 恢复 ============================

void ControlCore::track_modules() {
    if (++mod_ticks_ < opt_.comm_grace_ticks) return;  // 开机头几个 Tick 首轮通讯还没回来，不判
    track_module(0, "温控 AI-8", temp_.configured(), temp_.online(), temp_.stats());
    track_module(1, "压力 8AI", press_.configured(), press_.online(), press_.stats());
    track_module(2, "阀门 IO16R", valves_.configured(), valves_.online(), valves_.stats());
    track_module(3, "搅拌 DM2C", stir_.configured(), stir_.online(), stir_.stats());
}

void ControlCore::track_module(int k, const char* name, bool configured, bool online, const hw::CommStats& s) {
    if (!configured) return;  // 未配置串口的模块恒为离线，不记
    auto prev = mod_prev_[static_cast<size_t>(k)];
    mod_prev_[static_cast<size_t>(k)] = online;
    std::string stat = "累计事务 " + std::to_string(s.polls()) + " · 失败 " + std::to_string(s.fails()) + " · 最近响应 " +
                       (s.last_ok_ms() != 0 ? local_time(s.last_ok_ms()) : std::string("无"));
    std::string key = "comm:" + std::to_string(k);
    if (!prev) {
        if (!online) alarms_.raise(key, "err", "通讯", std::string(name) + " 离线", "开机后未连通 · " + stat, "alarm.commLost");
        return;
    }
    if (*prev == online) return;
    if (online) alarms_.clear(key, "通讯", std::string(name) + " 通讯恢复 · 报警解除", stat, "alarm.commRestored");
    else alarms_.raise(key, "err", "通讯", std::string(name) + " 离线", "连续 3 次通讯失败 · " + stat, "alarm.commLost");
}

// ============================ 搅拌故障（C# StirFaultWatch） ============================

void ControlCore::on_stir_alarm_code(int code) {
    int prev = stir_fault_code_;
    stir_fault_code_ = code;
    if (code == prev) return;
    char hex[16];
    std::snprintf(hex, sizeof(hex), "0x%X", static_cast<unsigned>(code));
    if (code != 0)
        alarms_.raise("stir:fault", "err", "搅拌", std::string("搅拌电机故障 · 代码 ") + hex + " · 在触屏清除",
                      StirService::fault_name(code) + " · 驱动器报警码 " + hex + " · 排查后在触屏搅拌栏点「清除」", "alarm.stirFault");
    else
        alarms_.clear("stir:fault", "搅拌", "搅拌电机故障已清除 · 报警解除", "驱动器报警码回到 0", "alarm.stirFaultCleared");
}

// ============================ 超温切断 ============================

void ControlCore::set_over_temp_alarm(Channel& c) {
    if (c.state != ReactorState::Alarm) c.ot_idle_alarm = c.state == ReactorState::Idle || c.state == ReactorState::Done;
    c.set_state(ReactorState::Alarm);
    c.over_temp = true;
}

void ControlCore::check_over_temp(Channel& c, bool real_t) {
    int i = c.id - 1;
    const auto& tc = temp_.ch(c.id);
    double limit = settings_.settings.over_temp;
    std::optional<bool> stopped;
    if (tc.mode >= 0) stopped = tc.mode == hw::TempController::kAtStop;
    auto act = ot_guard_.evaluate(i, tc.pv, real_t && tc.online, limit, clock_->mono_ms(), stopped);
    // 先下发（在任何文案 / 记录代码之前）
    if (act == core::OverTempAction::TripSend || act == core::OverTempAction::RetrySend || act == core::OverTempAction::Reopened)
        send_over_temp_stop(c.id, act);
    std::string pv = fmt_dec(tc.pv, 1, 1);
    std::string rearm = fmt_dec(limit - core::over_temp::kHysteresis, 1);
    switch (act) {
        case core::OverTempAction::TripSend: {
            std::string was = core::state_zh(c.state, c.over_temp);
            bool tuning = tc.autotuning;
            set_over_temp_alarm(c);
            alarms_.raise(ot_key(c.id), "err", rv(c.id), "超温 · 切断加热",
                          "PV " + pv + "℃ ≥ 超温限值 " + fmt_dec(limit, 1) + "℃ · 原状态 " + was + (tuning ? " · 自整定中（已取消）" : "") +
                              " · 下发 At=4",
                          "alarm.overTemp");
            notice("err", core::chan_name(c.id) + " 超温（" + pv + "℃）· 已切断加热输出");
            return;
        }
        case core::OverTempAction::Reopened:
            set_over_temp_alarm(c);
            alarms_.raise(ot_key(c.id), "err", rv(c.id), "超温切断后输出被重新打开 · 重新切断",
                          "PV " + pv + "℃ · 回读 At=" + std::to_string(tc.mode) + "（≠4）· 重发 At=4", "alarm.overTempReopened");
            notice("err", core::chan_name(c.id) + " 仍超温且加热输出被重新打开 · 已重新切断");
            return;
        case core::OverTempAction::Confirmed:
            alarms_.log("info", rv(c.id), "超温切断已确认", "回读 At=4，加热输出已停止 · PV " + pv + "℃", "alarm.overTempStopConfirmed");
            break;
        case core::OverTempAction::Cleared:
            // 只清报警列表条目；通道保持「超温」报警态，需操作员停止 / 复位
            alarms_.clear(ot_key(c.id), rv(c.id), "超温 · 解除",
                          "PV " + pv + "℃ < " + rearm + "℃（限值 − 回差 " + fmt_dec(core::over_temp::kHysteresis, 1) +
                              "℃）· 通道状态需手动复位",
                          "alarm.overTempCleared");
            return;
        default:
            break;
    }
    // 触发未解除、但通道已被复位出报警态：重新置报警
    if (ot_guard_.is_tripped(i) && c.state != ReactorState::Alarm) {
        std::string was = core::state_zh(c.state, c.over_temp);
        set_over_temp_alarm(c);
        alarms_.raise(ot_key(c.id), "warn", rv(c.id), "仍超温 · 重新置报警",
                      "PV " + pv + "℃（解除需低于 " + rearm + "℃）· 原状态 " + was, "alarm.overTemp");
    }
}

void ControlCore::send_over_temp_stop(int id, core::OverTempAction why) {
    int64_t t0 = clock_->wall_ms();
    temp_.over_temp_stop(id, [this, id, why, t0](bool ok) {
        int i = id - 1;
        ot_guard_.on_stop_result(i, ok, clock_->mono_ms());
        int sends = ot_guard_.unconfirmed_sends(i);
        bool retry = why == core::OverTempAction::RetrySend;
        std::string what = retry ? "重发 At=4（第 " + std::to_string(sends) + " 次）" : "下发 At=4";
        file_event(ok ? "info" : "err", rv(id), ok ? "超温切断 · 写入回显正常" : "超温切断 · 写入失败",
                   what + " · 下发 " + local_time(t0) + (ok ? " · 等待回读 At=4 确认" : " · AI-8 通讯失败"),
                   ok ? "alarm.overTemp" : "alarm.overTempStopUnconfirmed");
        int64_t now = clock_->mono_ms();
        auto& last = ot_fail_log_at_[static_cast<size_t>(i)];
        if (!ok && (!retry || last == 0 || now - last >= 60000)) {
            last = now;
            alarms_.raise(ot_key(id), "err", rv(id), "超温切断未确认",
                          what + "失败（AI-8 通讯）· 超温期间约每 5 秒重发 · 必要时点主页的「全部停止」", "alarm.overTempStopUnconfirmed");
        }
        if (retry && sends == core::over_temp::kEscalateAfterSends && ot_guard_.is_tripped(i) && !ot_guard_.is_confirmed(i)) {
            alarms_.raise(ot_key(id), "err", rv(id), "超温切断多次未确认 · 请点主页的「全部停止」",
                          "已下发 " + std::to_string(sends) + " 次 At=4 仍未回读到停止（At=" + std::to_string(temp_.ch(id).mode) +
                              "）· 加热输出可能仍在工作，请点主页的「全部停止」或到现场断电",
                          "alarm.overTempStopUnconfirmed");
            notice("err", core::chan_name(id) + " 超温切断多次未确认 · 请点主页的「全部停止」或到现场断电");
        }
    });
}

// ============================ 超压联锁 ============================

void ControlCore::check_over_pressure(Channel& c, bool real_p) {
    int i = c.id - 1;
    double lim = settings_.settings.over_pressure;
    const auto& tc = temp_.ch(c.id);
    bool heat_on = settings_.settings.stop_heat_on_over_pressure && temp_.configured();
    std::optional<bool> heat_stopped;
    if (temp_.online() && tc.online && tc.mode >= 0) heat_stopped = tc.mode == hw::TempController::kAtStop;
    const auto& shown = valves_.shown();
    bool closed_shown = !shown[static_cast<size_t>(i)] && !shown[8] && !shown[9] && !shown[10];
    bool v_conf_before = op_guard_.is_valves_confirmed(i), h_conf_before = op_guard_.is_heat_confirmed(i);
    auto a = op_guard_.evaluate(i, c.p, real_p, lim, clock_->mono_ms(), closed_shown, valves_.configured(), heat_stopped, heat_on);
    if (!a.any() && !op_guard_.is_tripped(i)) return;

    // 先下发：阀门登记到本 Tick 末合并下发，加热立即下发；带上本次写入序号（过期结果不算确认）
    if (a.send_valves) op_valve_pending_.push_back({c.id, a.trip ? "触发" : a.valves_reopened ? "复关" : "重发", op_guard_.valve_seq(i)});
    if (a.send_heat) send_over_pressure_heat(c.id, a.trip ? "触发" : a.heat_reopened ? "复切" : "重发", op_guard_.heat_seq(i));

    std::string p = fmt_p(c.p);
    if (a.trip) {
        std::string was = core::state_zh(c.state, c.over_temp);
        std::string gas = lease_.busy() ? lease_.task() : std::string();
        // 序列收尾时只关 SV 与气源，排空阀保持原样
        if (lease_.busy()) abort_keep_vent_ = true;
        abort_all_gas(core::GasAbortCause::OverPressure);
        c.ot_idle_alarm = false;
        c.set_state(ReactorState::Alarm);
        std::string acts = (valves_.configured() ? "关闭 SV" + std::to_string(c.id) + " 与全部气源（排空阀保持）" : std::string("阀门未配置串口（无法关阀）")) +
                           " · " + (gas.empty() ? std::string("无气路序列") : "中止气路序列「" + gas + "」") + " · " +
                           (heat_on ? std::string("停止加热 At=4")
                                    : settings_.settings.stop_heat_on_over_pressure ? std::string("温控未配置（不停加热）") : std::string("不停加热（设置）"));
        alarms_.raise(op_key(c.id), "err", rv(c.id), "超压 · 联锁动作",
                      "压力 " + p + " ≥ 超压报警值 " + fmt_p(lim) + " · 原状态 " + was + " · " + acts + "（结果见后续事件）",
                      "alarm.overPressure");
        notice("err", core::chan_name(c.id) + " 超压（" + p + "）· 已关闭进气与气源" + (gas.empty() ? "" : "、中止气路序列") + (heat_on ? "、停止加热" : ""));
        return;
    }
    if (a.valves_reopened) {
        const auto& f = valves_.shown();
        alarms_.raise(op_key(c.id), "err", rv(c.id), "超压联锁后阀门被重新打开 · 重新关闭",
                      "压力 " + p + " · 界面阀态 SV" + std::to_string(c.id) + " " + (f[static_cast<size_t>(i)] ? "开" : "关") + " · 气源 " +
                          (f[8] ? "开" : "关") + "/" + (f[9] ? "开" : "关") + "/" + (f[10] ? "开" : "关"),
                      "alarm.overPressure");
    }
    if (a.heat_reopened)
        alarms_.raise(op_key(c.id), "err", rv(c.id), "超压联锁后加热输出被重新打开 · 重新切断",
                      "回读 At=" + std::to_string(tc.mode) + "（≠4）· 重发 At=4", "alarm.overPressure");
    if (a.valves_confirmed) {
        std::string sub = "SV" + std::to_string(c.id) + " 与三路气源均为关（写入回显正常）";
        if (alarms_.has_active(opv_key(c.id))) alarms_.clear(opv_key(c.id), rv(c.id), "超压联锁 · 关阀已确认", sub, "alarm.overPressure");
        else file_event("info", rv(c.id), "超压联锁 · 关阀已确认", sub, "alarm.overPressure");
    }
    if (a.heat_confirmed) {
        std::string sub = heat_stopped == true ? "回读 At=4，加热输出已停止" : "写入回显正常（温控回读未知）";
        if (alarms_.has_active(oph_key(c.id))) alarms_.clear(oph_key(c.id), rv(c.id), "超压联锁 · 停止加热已确认", sub, "alarm.overPressure");
        else alarms_.log("info", rv(c.id), "超压联锁 · 停止加热已确认", sub, "alarm.overPressure");
    }
    if (a.cleared) {
        alarms_.clear(op_key(c.id), rv(c.id), "超压 · 解除",
                      "压力 " + p + " < 超压报警值的 " + fmt_dec(core::over_pressure::kRearmRatio * 100, 0) + "%（" +
                          fmt_p(lim * core::over_pressure::kRearmRatio) + "）· 通道状态需手动复位",
                      "alarm.overPressureCleared");
        std::string pend;
        if (valves_.configured() && !v_conf_before) pend += "关闭 SV" + std::to_string(c.id) + " 与气源";
        if (heat_on && !h_conf_before) pend += (pend.empty() ? "" : "、") + std::string("停止加热 At=4");
        if (!pend.empty())
            alarms_.log("warn", rv(c.id), "超压已解除 · 联锁动作仍未确认",
                        pend + " 未确认 · 已停止重发 · 「未确认」报警保留，请到现场确认阀门 / 加热状态", "alarm.overPressure");
        return;
    }
    if (op_guard_.is_tripped(i) && c.state != ReactorState::Alarm) {
        std::string was = core::state_zh(c.state, c.over_temp);
        c.ot_idle_alarm = false;
        c.set_state(ReactorState::Alarm);
        alarms_.raise(op_key(c.id), "warn", rv(c.id), "仍超压 · 重新置报警",
                      "压力 " + p + "（解除需低于 " + fmt_p(lim * core::over_pressure::kRearmRatio) + "）· 原状态 " + was, "alarm.overPressure");
    }
}

void ControlCore::flush_over_pressure_valves() {
    if (op_valve_pending_.empty()) return;
    auto ids = op_valve_pending_;
    op_valve_pending_.clear();
    // 只发关闭指令：一帧 0x0F 关三路气源（线圈 8–10），再逐个 0x05 关触发通道的 SV；其余线圈（其他 SV、排空）一律不碰
    struct Agg {
        std::vector<OpPending> ids;
        int64_t t0 = 0;
        int pending = 0;
        bool gas_ok = false;
        std::vector<std::pair<int, bool>> sv_ok;
        std::vector<std::string> errs;
    };
    auto agg = std::make_shared<Agg>();
    agg->ids = ids;
    agg->t0 = clock_->wall_ms();
    std::vector<int> uniq;
    for (const auto& x : ids)
        if (std::find(uniq.begin(), uniq.end(), x.id) == uniq.end()) uniq.push_back(x.id);
    agg->pending = 1 + static_cast<int>(uniq.size());
    auto done = [this, agg] {
        if (--agg->pending > 0) return;
        bool all_ok = agg->gas_ok;
        std::string svs;
        for (auto& kv : agg->sv_ok) {
            all_ok = all_ok && kv.second;
            svs += (svs.empty() ? "" : " ") + std::string("SV") + std::to_string(kv.first) + (kv.second ? " 已关" : " 失败");
        }
        std::string who;
        for (const auto& x : agg->ids) who += (who.empty() ? "" : "、") + core::chan_name(x.id) + "（" + x.why + "）";
        std::string errs;
        for (const auto& e : agg->errs) errs += (errs.empty() ? "" : "；") + e;
        file_event(all_ok ? "info" : "err", "阀门", all_ok ? "超压联锁 · 关阀写入正常" : "超压联锁 · 关阀写入失败",
                   who + " · 下发 " + local_time(agg->t0) + " · 三路气源 " + (agg->gas_ok ? "已关" : "失败") + " · " + svs + " · 排空阀不动" +
                       (all_ok ? "" : " · IO16R 通讯失败：" + errs),
                   all_ok ? "valve.frame" : "alarm.valveWriteFailed");
        for (const auto& x : agg->ids) {
            int i = x.id - 1;
            bool sv = false;
            for (auto& kv : agg->sv_ok)
                if (kv.first == x.id) sv = kv.second;
            bool ok = agg->gas_ok && sv;
            op_guard_.on_valve_result(i, ok, x.seq);
            if (ok || !op_guard_.is_tripped(i) || x.seq != op_guard_.valve_seq(i)) continue;  // 已解除 / 过期的结果不再报警
            int sends = op_guard_.valve_sends(i);
            int64_t now = clock_->mono_ms();
            auto& last = op_valve_fail_log_at_[static_cast<size_t>(i)];
            if (x.why != "重发" || last == 0 || now - last >= 60000) {
                last = now;
                alarms_.raise(opv_key(x.id), "err", rv(x.id), "超压联锁 · 关阀未确认",
                              "关闭 SV" + std::to_string(x.id) + " 与气源失败（IO16R 通讯）· 超压期间约每 5 秒重发 · 必要时点主页的「全部停止」或手动关闭气源",
                              "alarm.valveWriteFailed");
            }
            if (x.why == "重发" && sends == core::over_temp::kEscalateAfterSends) {
                alarms_.raise(opv_key(x.id), "err", rv(x.id), "超压联锁多次未确认 · 请点主页的「全部停止」",
                              "已下发 " + std::to_string(sends) + " 次关阀仍未确认 · 进气阀 / 气源可能仍开启，请点主页的「全部停止」或到现场关闭气源",
                              "alarm.valveWriteFailed");
                notice("err", core::chan_name(x.id) + " 超压关阀多次未确认 · 请点主页的「全部停止」或到现场关闭气源");
            }
        }
    };
    valves_.close_gas_sources([agg, done](bool ok, const std::string& err) {
        agg->gas_ok = ok;
        if (!ok) agg->errs.push_back("气源：" + err);
        done();
    });
    for (int id : uniq)
        valves_.close_sv(id, [agg, done, id](bool ok, const std::string& err) {
            agg->sv_ok.emplace_back(id, ok);
            if (!ok) agg->errs.push_back("SV" + std::to_string(id) + "：" + err);
            done();
        });
}

void ControlCore::send_over_pressure_heat(int id, const std::string& why, int seq) {
    int64_t t0 = clock_->wall_ms();
    temp_.over_temp_stop(id, [this, id, why, seq, t0](bool ok) {
        int i = id - 1;
        op_guard_.on_heat_result(i, ok, seq);
        bool current = op_guard_.is_tripped(i) && seq == op_guard_.heat_seq(i);
        int sends = op_guard_.heat_sends(i);
        file_event(ok ? "info" : "err", rv(id), ok ? "超压联锁 · 停止加热写入正常" : "超压联锁 · 停止加热写入失败",
                   why + " At=4（第 " + std::to_string(sends) + " 次）· 下发 " + local_time(t0) + (ok ? " · 等待回读 At=4 确认" : " · AI-8 通讯失败"),
                   ok ? "alarm.overPressure" : "alarm.heatStopFailed");
        if (ok || !current) return;
        int64_t now = clock_->mono_ms();
        auto& last = op_heat_fail_log_at_[static_cast<size_t>(i)];
        if (why != "重发" || last == 0 || now - last >= 60000) {
            last = now;
            alarms_.raise(oph_key(id), "err", rv(id), "超压联锁 · 停止加热未确认",
                          "At=4 写入失败（AI-8 通讯）· 超压期间约每 5 秒重发 · 必要时点主页的「全部停止」", "alarm.heatStopFailed");
        }
        if (why == "重发" && sends == core::over_temp::kEscalateAfterSends && !op_guard_.is_heat_confirmed(i)) {
            alarms_.raise(oph_key(id), "err", rv(id), "超压联锁多次未确认 · 请点主页的「全部停止」",
                          "已下发 " + std::to_string(sends) + " 次 At=4 仍未确认（At=" + std::to_string(temp_.ch(id).mode) +
                              "）· 加热输出可能仍在工作，请点主页的「全部停止」或到现场断电",
                          "alarm.heatStopFailed");
            notice("err", core::chan_name(id) + " 超压停止加热多次未确认 · 请点主页的「全部停止」或到现场断电");
        }
    });
}

// ============================ 新三级警告（设计文档 §6，C# 没有判定代码） ============================

// 升温超时：加热开始后超过 HeatTimeout（分钟）还没进入 目标 ± 到位范围 → 警告一次（三级，不弹窗、不停机）；
// 之后进入范围 / 停止加热 / 改目标时解除。温控离线时读数无效，不判。
void ControlCore::check_heat_timeouts(bool real_t) {
    int64_t now = clock_->mono_ms();
    int64_t limit_ms = static_cast<int64_t>(settings_.settings.heat_timeout * 60000.0);
    for (auto& c : ch_) {
        auto& w = c.heat_watch;
        if (!w.active || w.reached) continue;
        if (!real_t || !temp_.ch(c.id).online) continue;
        bool in_band = std::fabs(c.t - w.target) <= w.band;
        if (in_band) {
            w.reached = true;
            if (w.warned)
                alarms_.clear("ht:" + std::to_string(c.id), rv(c.id), "升温超时 · 解除",
                              "PV " + fmt_dec(c.t, 1, 1) + "℃ 已进入目标 " + fmt_dec(w.target, 1) + "℃ ±" + fmt_dec(w.band, 1) + "℃",
                              "alarm.heatTimeoutCleared");
            w.warned = false;
            continue;
        }
        if (w.warned || limit_ms <= 0 || now - w.since_ms < limit_ms) continue;
        w.warned = true;
        alarms_.raise("ht:" + std::to_string(c.id), "warn", rv(c.id), "升温超时",
                      "加热已 " + fmt_dec((now - w.since_ms) / 60000.0, 0) + " min 仍未到位 · PV " + fmt_dec(c.t, 1, 1) + "℃ / 目标 " +
                          fmt_dec(w.target, 1) + "℃ ±" + fmt_dec(w.band, 1) + "℃（升温超时设置 " +
                          fmt_dec(settings_.settings.heat_timeout, 1) + " min）· 请检查加热器与热电偶",
                      "alarm.heatTimeout");
    }
}

// 压力偏离：稳压开着时压力偏离设定超过 PressDeviation（psi）持续 30 s → 警告；回到范围内自动解除。压力离线不判。
void ControlCore::check_pressure_deviation(bool real_p) {
    int64_t now = clock_->mono_ms();
    double dev = settings_.settings.press_deviation;
    for (auto& c : ch_) {
        auto& w = c.press_watch;
        if (!w.active || !real_p) continue;
        bool out = std::fabs(c.p - w.target_psi) > dev;
        if (!out) {
            w.out_since_ms.reset();
            if (w.warned) {
                w.warned = false;
                alarms_.clear("pd:" + std::to_string(c.id), rv(c.id), "压力偏离 · 解除",
                              "压力 " + fmt_p(c.p) + " 已回到设定 " + fmt_p(w.target_psi) + " ±" + fmt_p(dev) + " 内",
                              "alarm.pressureDeviationCleared");
            }
            continue;
        }
        if (!w.out_since_ms) w.out_since_ms = now;
        if (w.warned || now - *w.out_since_ms < opt_.press_deviation_hold_ms) continue;
        w.warned = true;
        alarms_.raise("pd:" + std::to_string(c.id), "warn", rv(c.id), "压力偏离",
                      "压力 " + fmt_p(c.p) + " 偏离设定 " + fmt_p(w.target_psi) + " 超过 " + fmt_p(dev) + " 已持续 " +
                          fmt_dec(opt_.press_deviation_hold_ms / 1000.0, 0) + " s · 请检查气源与密封",
                      "alarm.pressureDeviation");
    }
}

}  // namespace reactor::ctl
