#include "ctl/ctl_volume_calib.hpp"
#include "core/chan_name.hpp"

#include <algorithm>
#include <cmath>

#include "core/num_format.hpp"
#include "core/value_rules.hpp"
#include "core/volume_calib.hpp"
#include "ctl/ctl_leak_test.hpp"
#include "ctl/ctl_program_engine.hpp"
#include "store/settings.hpp"
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <utility>

namespace reactor::ctl {

using core::fmt_dec;
namespace vc = core::volume_calib;

VolumeCalib::VolumeCalib(ControlCore& core, const ProgramEngine* programs, const LeakTest* leak, store::DataLogger* logger,
                         core::PressureUnit unit)
    : core_(core), programs_(programs), leak_(leak), logger_(logger), unit_(unit), ops_(std::make_unique<GasOps>(core, unit)) {
    ops_->set_fault([this](const std::string& why) { fault(why); });
    ops_->set_owner(core::GasOwner::Calib);
    listener_ = core_.add_gas_abort_listener([this](core::GasAbortCause c, std::optional<int> ch) { on_gas_abort(c, ch); });
}

VolumeCalib::~VolumeCalib() { core_.remove_gas_abort_listener(listener_); }

bool VolumeCalib::two_runs() const {
    auto m = core_.settings().settings.manifold_volume;
    bool known = m && *m > 0;
    return !known || redo_manifold_;
}

// ============================ 开始 / 中止（C# OperatorCommands.Calib） ============================

OpResult VolumeCalib::start(int ch, bool with_water, double water_ml, bool redo_manifold, const std::string& started_by) {
    if (running_) return OpResult::fail(core::op_codes::kSystemBusy, "容积标定正在进行 · 请等待完成或先中止", "warn").set_quiet();
    if (leak_ && leak_->running())
        return OpResult::fail(core::op_codes::kLeakTestRunning, "泄漏测试正在进行 · 请等待完成后再标定", "warn");
    if (ch < 1 || ch > 8) return OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(ch) + " 不存在（有效范围 1–8）");
    if (core::state_is_running(core_.channel(ch).state) || (programs_ && programs_->is_owned(ch)))
        return OpResult::fail(core::op_codes::kChannelBusy, core::chan_name(ch) + " 正在运行或正在执行程序 · 请选一个空闲通道");
    if (with_water) {
        if (first_pass_channel() != ch)
            return OpResult::fail(core::op_codes::kBadRequest, core::chan_name(ch) + " 还没有做空釜一遍 · 请先开始标定", "warn");
        if (auto bad = core::value_rules::range("加水体积", water_ml, 0.5, core_.settings().settings.vol_max, "mL"))
            return OpResult::invalid(*bad);
    }
    if (auto gb = gas_ops::gas_block(core_, true, std::nullopt, false, {}, programs_ ? programs_->gas_pending() : std::string()))
        return OpResult::fail(gb->code, gb->message);
    if (!with_water) redo_manifold_ = redo_manifold;
    run(ch, with_water, water_ml, started_by);
    return OpResult::accept("", "ok", ojson{{"channel", ch}, {"withWater", with_water}, {"waterMl", water_ml}});
}

OpResult VolumeCalib::abort() {
    if (core_.gas_lease().owner() != core::GasOwner::Calib || !running_) return OpResult::none();
    cancelled();
    return OpResult::done("容积标定已中止 · 正在全关阀", "warn").set_quiet();
}

OpResult VolumeCalib::discard_first() {
    if (running_) return OpResult::none();
    first_.reset();
    await_water_ = false;
    has_result_ = false;
    phase_.clear();
    stage_.reset();  // CalibActivity.Clear：不再有等加水的标定
    waiting_since_.reset();
    return OpResult::none();
}

void VolumeCalib::on_gas_abort(core::GasAbortCause, std::optional<int> channel) {
    if (!running_ || ending_) return;
    if (core_.gas_lease().owner() != core::GasOwner::Calib) return;
    if (channel && !core_.gas_lease().includes(*channel)) return;
    cancelled();
}

// ============================ 流程（C# CalibViewModel.RunAsync） ============================

void VolumeCalib::run(int ch, bool with_water, double water_ml, const std::string& by) {
    channel_ = ch;
    running_ = true;
    ending_ = false;
    has_result_ = false;
    failed_ = false;
    progress_ = 0;
    with_water_ = with_water;
    pass_water_ = water_ml;
    steps_.clear();
    fail_.reset();
    aborted_ = false;
    std::string rv = core::chan_name(ch);
    core_.begin_gas(core::GasOwner::Calib, "容积标定 " + rv, {ch}, {ch});
    // CalibActivity.Begin
    if (!with_water || !started_at_ || act_channel_ != ch) {
        started_at_ = core_.clock().wall_ms();
        first_pass_ml_.reset();
    }
    stage_ = with_water ? calib_stage::kSecond : calib_stage::kFirst;
    act_channel_ = ch;
    started_by_ = by;
    act_two_runs_ = with_water || two_runs();
    used_water_ = with_water ? std::optional<double>(water_ml) : std::nullopt;
    waiting_since_.reset();
    result_ml_.reset();
    act_text_.reset();
    final_progress_ = 0;
    final_phase_.reset();

    std::string pass = with_water ? "第二遍（加水 " + fmt_dec(water_ml, 2) + " mL）" : "空釜";
    if (logger_) logger_->add_marker("容积标定开始 " + rv + " " + pass);
    core_.alarms().log("info", "系统", rv + " 容积标定开始", pass + " · 惰性气体 · " + std::to_string(vc::kSteps) + " 次充放", "calib.started");
    t0_ = core_.channel(ch).t;
    cap_ = core_.settings().settings.over_pressure * store::setting_specs::kLeakTestMaxRatio;
    phase_ = "关闭全部阀门，读取初始压力…";
    auto closed = core::valve_frame::all_closed();
    ops_->apply(closed, [this, closed, ch] {
        ops_->hold(closed, core::purge_plan::kDeadSec, [this, ch] {
            ops_->read([this, ch](const PressureSample& ps) {
                p_ = ps.psi[static_cast<size_t>(ch - 1)];
                if (p_ > cap_ * kMaxStartRatio) {
                    fault(core::chan_name(ch) + " 初始压力 " + P(p_) + " 偏高 · 请先把釜泄到常压");
                    return;
                }
                t0_ = core_.channel(ch).t;
                step(1);
            });
        });
    });
}

void VolumeCalib::step(int k) {
    int ch = channel_;
    std::string rv = core::chan_name(ch);
    if (k > vc::kSteps) {
        const auto& c = core_.channel(ch);
        if (c.t_online && std::isfinite(t0_) && std::fabs(c.t - t0_) > kMaxTempDrift) {
            fault("釜温变化 " + fmt_dec(std::fabs(c.t - t0_), 1, 1) + " °C（> " + fmt_dec(kMaxTempDrift, 1) + " °C）· 请在常温、不加热时标定");
            return;
        }
        cleanup();
        return;
    }
    std::string n = std::to_string(k) + "/" + std::to_string(vc::kSteps);
    phase_ = "第 " + n + " 次 · 总管充气";
    auto gas = core::valve_frame::gas(0);  // 只开惰性气源：全部 SV 关
    auto closed = core::valve_frame::all_closed();
    auto sv = closed;
    sv[static_cast<size_t>(ch - 1)] = true;  // 只开该路 SV：总管里的气进釜
    ops_->apply(gas, [this, gas, closed, sv, k, ch, rv, n] {
        ops_->hold(gas, kChargeSec, [this, closed, sv, k, ch, rv, n] {
            ops_->apply(closed, [this, closed, sv, k, ch, rv, n] {
                ops_->hold(closed, core::purge_plan::kDeadSec, [this, closed, sv, k, ch, rv, n] {
                    phase_ = "第 " + n + " 次 · 放进 " + rv + "，等压力稳定";
                    ops_->apply(sv, [this, closed, sv, k, ch, rv] {
                        ops_->hold(sv, kEqualizeSec, [this, closed, k, ch, rv] {
                            ops_->read([this, closed, k, ch, rv](const PressureSample& ps) {
                                double after = ps.psi[static_cast<size_t>(ch - 1)];
                                ops_->apply(closed, [this, closed, k, ch, rv, after] {
                                    ops_->hold(closed, core::purge_plan::kDeadSec, [this, k, ch, rv, after] {
                                        if (!ops_->check()) return;
                                        steps_.emplace_back(p_, after);
                                        const auto& c = core_.channel(ch);
                                        std::string tt = c.t_online ? fmt_dec(c.t, 1, 1) : "—";
                                        core_.alarms().log("info", "系统", rv + " 容积标定 · 第 " + std::to_string(k) + " 次",
                                                           P(p_) + " → " + P(after) + " · 釜温 " + tt + " °C", "calib.step");
                                        if (after >= cap_) {
                                            fault("压力已到 " + P(after) + "，接近超压上限 · 请把惰性气体减压阀调低到 100 psi 左右");
                                            return;
                                        }
                                        double rise = after - p_;
                                        p_ = after;
                                        progress_ = 100.0 * k / vc::kSteps;
                                        // 已接近减压阀压力（再充也升不了多少）且有效数据够了：提前结束（10 mL 小釜常见）
                                        int good = 0;
                                        for (auto& s : steps_)
                                            if (s.second - s.first >= vc::kMinRisePsi) ++good;
                                        if (rise < vc::kMinRisePsi && good >= vc::kMinSteps) {
                                            progress_ = 100;
                                            step(vc::kSteps + 1);
                                            return;
                                        }
                                        step(k + 1);
                                    });
                                });
                            });
                        });
                    });
                });
            });
        });
    });
}

void VolumeCalib::fault(const std::string& why) {
    if (!running_ || ending_) return;
    fail_ = why;
    cleanup();
}

void VolumeCalib::cancelled() {
    if (!running_ || ending_) return;
    aborted_ = true;
    cleanup();
}

void VolumeCalib::cleanup() {
    if (ending_) return;
    ending_ = true;
    ops_->cancel();
    ops_->when_idle([this] {
        core_.close_all_valves([this](bool, const std::string&) {
            core_.end_gas(core::GasOwner::Calib);
            running_ = false;
            if (logger_) logger_->add_marker("容积标定结束 " + core::chan_name(channel_));
            finish();
            sync_activity();
        });
    });
}

void VolumeCalib::set_fail(const std::string& title, const std::string& text) {
    failed_ = true;
    result_title_ = title;
    result_text_ = text;
    phase_ = title;
}

// C# CalibViewModel.Finish
void VolumeCalib::finish() {
    int ch = channel_;
    std::string rv = core::chan_name(ch);
    has_result_ = true;
    std::string residual = steps_.empty() ? std::string() : " 釜内现有约 " + P(steps_.back().second) + " 惰性气体，开釜前请先泄压。";
    if (aborted_ || fail_) {
        set_fail(aborted_ ? "标定已中止" : "标定失败", (aborted_ ? std::string("已按要求中止，阀门已全关。") : *fail_ + "。阀门已全关。") + residual);
        core_.alarms().log(aborted_ ? "warn" : "err", "系统", rv + " 容积标定" + (aborted_ ? "中止" : "失败"), fail_ ? *fail_ : "手动中止",
                           aborted_ ? "calib.aborted" : "calib.failed");
        if (with_water_) await_water_ = true;  // 第二遍失败：保留第一遍，可以重做第二遍
        return;
    }
    auto fr = vc::fit_steps(steps_);
    std::string pts;
    for (size_t i = 0; i < steps_.size(); ++i)
        pts += (i ? " · " : "") + fmt_dec(steps_[i].first, 2, 2) + "→" + fmt_dec(steps_[i].second, 2, 2);
    if (!fr.fit) {
        std::string why = fr.why.value_or("拟合失败");
        set_fail("标定失败", why + "。" + residual);
        core_.alarms().log("err", "系统", rv + " 容积标定失败", why + " · " + pts + " psi", "calib.failed");
        if (with_water_) await_water_ = true;
        return;
    }
    const auto& fit = *fr.fit;
    std::string fit_text = "比值 " + fmt_dec(fit.ratio(), 3) + " · R² " + fmt_dec(fit.r2, 4, 4) + " · 减压阀约 " + P(fit.supply_psi());
    if (!with_water_ && two_runs()) {
        first_ = std::make_pair(ch, fit.ratio());
        await_water_ = true;
        failed_ = false;
        result_title_ = "第一遍完成（空釜）";
        result_text_ = fit_text + "。" + residual;
        phase_ = "等待加水";
        core_.alarms().log("info", "系统", rv + " 容积标定 · 空釜完成", fit_text + " · " + pts + " psi", "calib.firstPass");
        core_.emit_notice("ok", rv + " 空釜一遍完成 · 请按提示加水后继续第二遍");
        return;
    }
    double veff = 0;
    std::optional<double> vm;
    if (with_water_) {
        if (!first_ || first_->first != ch) {
            set_fail("标定失败", "找不到这一路的空釜结果 · 请重新开始");
            await_water_ = false;
            return;
        }
        auto two = vc::solve_two_runs(first_->second, fit.ratio(), pass_water_);
        if (!two.effective_ml) {
            set_fail("标定失败", two.why.value_or("") + "。" + residual);
            await_water_ = true;
            core_.alarms().log("err", "系统", rv + " 容积标定失败", two.why.value_or(""), "calib.failed");
            return;
        }
        veff = *two.effective_ml;
        vm = two.manifold_ml;
    } else {
        auto one = vc::solve_one_run(fit.ratio(), core_.settings().settings.manifold_volume.value_or(0));
        if (!one.effective_ml) {
            set_fail("标定失败", one.why.value_or("") + "。" + residual);
            core_.alarms().log("err", "系统", rv + " 容积标定失败", one.why.value_or(""), "calib.failed");
            return;
        }
        veff = *one.effective_ml;
    }
    // 保存：设置里的有效容积 / 标定时间（/ 总管体积），吸气量计算立即改用
    std::optional<double> old = core_.settings().settings.effective_volume[static_cast<size_t>(ch - 1)];
    int64_t now = core_.clock().wall_ms();
    core_.update_settings([ch, veff, vm, now](store::SettingsState& st) {
        st.settings.effective_volume[static_cast<size_t>(ch - 1)] = std::round(veff * 100.0) / 100.0;
        st.settings.volume_calib_utc_ms[static_cast<size_t>(ch - 1)] = now;
        if (vm) st.settings.manifold_volume = std::round(*vm * 1000.0) / 1000.0;
    });
    first_.reset();
    await_water_ = false;
    redo_manifold_ = false;
    failed_ = false;
    result_title_ = "✓ " + rv + " 有效容积 " + fmt_dec(veff, 1, 1) + " mL";
    result_text_ = fit_text + (vm ? " · 总管体积 " + fmt_dec(*vm, 2, 2) + " mL" : std::string()) +
                   (old ? " · 原来 " + fmt_dec(*old, 1, 1) + " mL" : std::string()) +
                   "。吸气量计算已改用此值（气相体积 = 有效容积 − 溶液体积）。" + residual;
    phase_ = "标定完成";
    auto mv = core_.settings().settings.manifold_volume;
    core_.alarms().log("info", "系统", rv + " 容积标定完成",
                       "有效容积 " + fmt_dec(veff, 2, 2) + " mL" +
                           (vm ? " · 总管 " + fmt_dec(*vm, 3, 3) + " mL" : " · 按总管 " + fmt_dec(mv.value_or(0), 3, 3) + " mL") + " · " +
                           fit_text + " · " + pts + " psi",
                       "calib.done");
    core_.emit_notice("ok", rv + " 容积标定完成 · " + fmt_dec(veff, 1, 1) + " mL");
}

// C# CalibViewModel.SyncActivity
void VolumeCalib::sync_activity() {
    std::string title = result_title_;
    while (!title.empty() && (title.rfind("✓", 0) == 0 || title[0] == ' ')) title = title.rfind("✓", 0) == 0 ? title.substr(3) : title.substr(1);
    std::string text = title + "：" + result_text_;
    if (failed_) {
        stage_ = calib_stage::kFailed;
        waiting_since_.reset();
        act_text_ = text;
        final_progress_ = progress_;
        final_phase_ = phase_;
    } else if (await_water_) {
        stage_ = calib_stage::kWaitWater;
        waiting_since_ = core_.clock().wall_ms();
        first_pass_ml_.reset();  // 两遍法的第一遍只得出比值，还没有容积
        act_text_ = text;
    } else {
        auto v = core_.settings().settings.effective_volume[static_cast<size_t>(channel_ - 1)];
        stage_ = calib_stage::kDone;
        waiting_since_.reset();
        result_ml_ = v;
        if (!act_two_runs_) first_pass_ml_ = v;
        act_text_ = text;
        final_progress_ = progress_;
        final_phase_ = phase_;
    }
}

ojson VolumeCalib::snapshot() const {
    if (!stage_) return nullptr;
    bool ended = !running_ && (*stage_ == calib_stage::kDone || *stage_ == calib_stage::kFailed);
    std::optional<double> water;
    if (act_two_runs_) water = used_water_ ? *used_water_ : water_ml_;
    auto opt = [](const std::optional<double>& v) { return v ? ojson(store::json_num(*v)) : ojson(nullptr); };
    double prog = ended ? final_progress_ : progress_;
    ojson j;
    j["running"] = running_;
    j["channel"] = act_channel_ ? ojson(*act_channel_) : ojson(nullptr);
    j["stage"] = *stage_;
    j["waitingOperator"] = *stage_ == calib_stage::kWaitWater;
    j["waterMl"] = opt(water);
    j["firstPassMl"] = opt(first_pass_ml_);
    j["resultMl"] = opt(result_ml_);
    j["progress"] = store::json_num(std::round(prog * 10.0) / 10.0);
    std::optional<std::string> ph = ended ? final_phase_ : (phase_.empty() ? std::nullopt : std::optional<std::string>(phase_));
    j["phaseText"] = ph ? ojson(*ph) : ojson(nullptr);
    j["startedAt"] = started_at_ ? ojson(store::fmt_utc_json(*started_at_)) : ojson(nullptr);
    j["waitingSince"] = waiting_since_ ? ojson(store::fmt_utc_json(*waiting_since_)) : ojson(nullptr);
    j["startedBy"] = started_by_;
    j["done"] = *stage_ == calib_stage::kDone;
    j["failed"] = *stage_ == calib_stage::kFailed;
    j["resultText"] = act_text_ ? ojson(*act_text_) : ojson(nullptr);
    // ---- 新字段（第二阶段）----
    j["twoRuns"] = act_two_runs_;
    j["resultTitle"] = result_title_.empty() ? ojson(nullptr) : ojson(result_title_);
    ojson pts = ojson::array();
    for (const auto& s : steps_) pts.push_back(ojson::array({store::json_num(s.first), store::json_num(s.second)}));
    j["points"] = pts;
    return j;
}

}  // namespace reactor::ctl
