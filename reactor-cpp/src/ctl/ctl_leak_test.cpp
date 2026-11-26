#include "ctl/ctl_leak_test.hpp"
#include "core/chan_name.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>

#include "core/num_format.hpp"
#include "core/program/text_format.hpp"
#include "ctl/ctl_program_engine.hpp"
#include "store/settings.hpp"

namespace reactor::ctl {

using core::fmt_dec;

namespace {
std::string mmss(double sec) {
    int s = static_cast<int>(std::max(0.0, core::program::round_even(sec)));
    char b[16];
    std::snprintf(b, sizeof b, "%02d:%02d", s / 60, s % 60);
    return b;
}
ojson num_or_null(std::optional<double> v) {
    return v && std::isfinite(*v) ? ojson(store::json_num(*v)) : ojson(nullptr);
}
double round_to(double v, int dec) {
    double f = std::pow(10.0, dec);
    return std::round(v * f) / f;
}
}  // namespace

LeakTest::LeakTest(ControlCore& core, const ProgramEngine* programs, store::DataLogger* logger, store::SessionBook* sessions,
                   core::PressureUnit unit)
    : core_(core), programs_(programs), logger_(logger), sessions_(sessions), unit_(unit),
      ops_(std::make_unique<GasOps>(core, unit)) {
    for (int i = 0; i < 8; ++i) cells_[static_cast<size_t>(i)].id = i + 1;
    ops_->set_fault([this](const std::string& why) { fault(why); });
    ops_->set_owner(core::GasOwner::Leak);
    listener_ = core_.add_gas_abort_listener([this](core::GasAbortCause c, std::optional<int> ch) { on_gas_abort(c, ch); });
}

LeakTest::~LeakTest() { core_.remove_gas_abort_listener(listener_); }

std::string LeakTest::rvs(const std::vector<int>& ids) const {
    std::string s;
    s += core::chan_names(ids);
    return s;
}

// ============================ 开始 / 中止（C# OperatorCommands.Leak） ============================

OpResult LeakTest::start(const std::vector<int>& channels, std::optional<double> test_psi, const std::string& started_by) {
    if (running_)
        return OpResult::fail(core::op_codes::kLeakTestRunning, "泄漏测试正在进行 · 请等待完成或先中止", "warn").set_quiet();
    std::vector<int> ids;
    for (int id : channels)
        if (std::find(ids.begin(), ids.end(), id) == ids.end()) ids.push_back(id);
    if (ids.empty()) return OpResult::fail(core::op_codes::kBadRequest, "请至少选择一个测试通道", "warn");
    for (int id : ids)
        if (id < 1 || id > 8)
            return OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(id) + " 不存在（有效范围 1–8）");
    std::vector<int> busy;
    for (int id : ids)
        if (core::state_is_running(core_.channel(id).state) || (programs_ && programs_->is_owned(id))) busy.push_back(id);
    if (!busy.empty())
        return OpResult::fail(core::op_codes::kChannelBusy, rvs(busy) + " 已在运行或正在执行程序 · 请重新打开泄漏测试选择通道");
    if (auto gb = gas_ops::gas_block(core_, true, std::nullopt, false, {}, programs_ ? programs_->gas_pending() : std::string()))
        return OpResult::fail(gb->code, gb->message);
    const auto& s = core_.settings().settings;
    double target = test_psi.value_or(s.leak_test_psi);
    if (test_psi)
        if (auto bad = store::setting_specs::validate(store::SettingKey::LeakTestPsi, *test_psi))
            return OpResult::fail(bad->code, bad->message, "err", ojson{{"min", bad->min}, {"max", bad->max}, {"value", bad->value}});
    double max = s.over_pressure * store::setting_specs::kLeakTestMaxRatio;
    if (target >= max)
        return OpResult::fail(core::op_codes::kParamCrossCheck, "测试压力须低于超压报警值的 90% · 请在「设置 → 参数与报警」调整", "err",
                              ojson{{"testPsi", target}, {"max", max}});
    run(ids, target, started_by);
    ojson chans = ojson::array();
    for (int id : ids) chans.push_back(id);
    return OpResult::accept("", "ok", ojson{{"runId", run_id() ? ojson(*run_id()) : ojson(nullptr)}, {"channels", chans}, {"testPsi", target}});
}

OpResult LeakTest::abort(bool strict) {
    const auto& l = core_.gas_lease();
    if (l.owner() != core::GasOwner::Leak || !running_) {
        if (strict && l.busy())
            return OpResult::fail(core::op_codes::kGasOwnedByOther, "气路由「" + l.task() + "」占用 · 当前没有泄漏测试");
        return OpResult::none();
    }
    cancelled();
    return OpResult::done("泄漏测试已中止 · 正在全关阀", "warn").set_quiet();
}

void LeakTest::on_gas_abort(core::GasAbortCause, std::optional<int> channel) {
    if (!running_ || ending_) return;
    if (core_.gas_lease().owner() != core::GasOwner::Leak) return;
    if (channel && !core_.gas_lease().includes(*channel)) return;
    cancelled();  // 全部停止 / 超压联锁 / 退出 / 参与通道被停止：同 C# 取消令牌 → 「测试已中止」
}

// ============================ 流程 ============================

void LeakTest::enter(const char* stage, std::optional<double> duration_sec) {
    stage_ = stage;
    stage_started_mono_ = core_.clock().mono_ms();
    if (duration_sec) stage_ends_mono_ = stage_started_mono_ + static_cast<int64_t>(std::llround(std::max(0.0, *duration_sec) * 1000.0));
    else stage_ends_mono_.reset();
}

void LeakTest::run(const std::vector<int>& ids, double target, const std::string& by) {
    const auto& s = core_.settings().settings;
    target_ = target;
    thr_ = s.leak_rate;
    settle_min_ = s.leak_settle_min;
    measure_min_ = s.leak_measure_min;
    started_by_ = by;
    picked_ = ids;
    tested_.clear();
    over_fill_.clear();
    rates_.reset();
    fail_.reset();
    aborted_ = false;
    ending_ = false;
    vented_.reset();
    running_ = true;
    done_ = failed_ = leak_failed_ = false;
    result_title_.clear();
    result_text_.clear();
    progress_ = 0;
    for (auto& c : cells_) {
        bool can = !core::state_is_running(core_.channel(c.id).state) && !(programs_ && programs_->is_owned(c.id));
        c.picked = std::find(ids.begin(), ids.end(), c.id) != ids.end();
        c.pressure = core_.channel(c.id).p;
        if (c.picked) { c.status = ""; c.detail = "…"; }
        else { c.status = "skip"; c.detail = can ? "不测" : "运行中"; }
    }
    core_.begin_gas(core::GasOwner::Leak, "泄漏测试", ids, ids);
    // LeakActivity.Begin
    act_picked_ = std::set<int>(ids.begin(), ids.end());
    rate_.fill(std::nullopt);
    started_at_ = core_.clock().wall_ms();
    finished_at_.reset();
    settle_sec_ = std::max(0.0, settle_min_ * 60);
    measure_sec_ = std::max(0.0, measure_min_ * 60);
    act_vented_.reset();
    act_aborted_ = false;
    act_fail_.reset();
    act_failed_ = false;
    final_progress_ = 0;
    enter(leak_stage::kPrep);
    rec_ = std::make_unique<store::LeakRecord>(store::WallTime::local(core_.clock().wall_ms()));
    if (logger_) logger_->add_marker("泄漏测试开始 " + rec_->run_id());
    std::optional<std::string> session = sessions_ ? sessions_->current_id() : std::nullopt;
    core_.alarms().log("info", "系统", "泄漏测试开始",
                       rvs(ids) + " · 惰性气体 " + core::format_p(target, unit_) + " · 平衡 " + fmt_dec(settle_min_, 1) + " min / 测量 " +
                           fmt_dec(measure_min_, 1) + " min · 逐点数据 leak/" + rec_->run_id() + ".csv" +
                           (session ? " · 会话 " + *session : ""),
                       "leak.started");
    if (session) sessions_->tag_leak_test(rec_->run_id(), core_.clock().wall_ms());

    // 0. 关闭全部阀门，读取初始压力
    phase_ = "关闭全部阀门，读取初始压力…";
    auto closed = core::valve_frame::all_closed();
    ops_->apply(closed, [this, closed] {
        ops_->hold(closed, core::purge_plan::kDeadSec, [this] {
            sample(store::LeakRecord::kPrep, picked_, [this](const PressureSample& ps) {
                std::vector<int> fill;
                for (int id : picked_) {
                    auto& c = cell(id);
                    c.pressure = ps.psi[static_cast<size_t>(id - 1)];
                    // 釜压已明显高于测试压力：软件不一定能泄压（取决于进气单向阀方向），跳过
                    if (c.pressure > target_ * 1.25) { c.status = "skip"; c.detail = "初始压力过高"; }
                    else fill.push_back(id);
                }
                if (fill.empty()) { fault("所选通道初始压力均高于测试压力，请先泄压"); return; }
                enter(leak_stage::kFill);
                // FillAsync 初始化
                double tol = std::max(0.5, target_ * 0.02);
                fill_ok_.clear();
                fill_pend_.clear();
                gain_.clear();
                stall_.clear();
                for (int id : fill) {
                    auto& c = cell(id);
                    if (c.pressure >= target_ - tol) { fill_ok_.push_back(id); c.detail = "已在测试压力"; }
                    else { fill_pend_.push_back(id); c.detail = "充压中"; }
                }
                tested_ = fill;  // 充压阶段逐点数据记全部待充通道
                fill_t0_ = core_.clock().mono_ms();
                fill_round();
            });
        });
    });
}

void LeakTest::sample(const char* phase, const std::vector<int>& ids, std::function<void(const PressureSample&)> then) {
    std::string ph = phase;
    ops_->read([this, ph, ids, then](const PressureSample& ps) {
        if (rec_)
            for (int id : ids)
                rec_->add(id, ph, store::WallTime::local(ps.at_wall_ms), static_cast<int64_t>(ps.raw[static_cast<size_t>(id - 1)]),
                          ps.psi[static_cast<size_t>(id - 1)], gas_ops::temp_or_nan(core_, id));
        then(ps);
    });
}

// C# FillAsync 的一轮：各通道开阀时长（首轮 0.2 s 试探，之后按增益只补剩余的 60%），同开惰性气体 + 未到压通道的 SV，到时逐路关
void LeakTest::fill_round() {
    if (fill_pend_.empty()) { fill_after(); return; }
    if (static_cast<double>(core_.clock().mono_ms() - fill_t0_) / 1000.0 > kFillTimeoutSec) {
        for (int id : fill_pend_) { cell(id).status = "bad"; cell(id).detail = "充压超时"; }
        fill_pend_.clear();
        fill_after();
        return;
    }
    plan_.clear();
    before_.clear();
    for (int id : fill_pend_) {
        auto it = gain_.find(id);
        double sec = it != gain_.end() && it->second > 0.05 ? 0.6 * (target_ - cell(id).pressure) / it->second : 0.2;
        plan_.emplace_back(id, std::clamp(sec, 0.1, 2.0));
        before_[id] = cell(id).pressure;
    }
    std::stable_sort(plan_.begin(), plan_.end(), [](const auto& a, const auto& b) { return a.second < b.second; });
    open_.clear();
    for (auto& p : plan_) open_.push_back(p.first);
    round_t0_ = core_.clock().mono_ms();
    ops_->apply(core::valve_frame::gas(0, open_), [this] { fill_close(0); });
}

void LeakTest::fill_close(size_t k) {
    if (k >= plan_.size()) {
        // 关阀后静置 1 s 再读，避开充气瞬间的动态压力
        ops_->hold(core::valve_frame::all_closed(), 1.0, [this] {
            std::vector<int> all = tested_;
            sample(store::LeakRecord::kFill, all, [this](const PressureSample& ps) {
                if (!ops_->check()) return;
                double tol = std::max(0.5, target_ * 0.02);
                for (auto& [id, sec] : plan_) {
                    auto& c = cell(id);
                    c.pressure = ps.psi[static_cast<size_t>(id - 1)];
                    double rise = c.pressure - before_[id];
                    gain_[id] = rise / sec;
                    stall_[id] = rise < 0.3 ? stall_[id] + 1 : 0;
                    int rid = id;
                    auto erase = [this, rid] { fill_pend_.erase(std::remove(fill_pend_.begin(), fill_pend_.end(), rid), fill_pend_.end()); };
                    if (c.pressure >= target_ - tol) {
                        erase();
                        fill_ok_.push_back(id);
                        c.detail = c.pressure > target_ * 1.25 ? "超出测试压力" : "已到压";
                    } else if (stall_[id] >= 4) {
                        erase();
                        if (c.pressure >= target_ * 0.8) { fill_ok_.push_back(id); c.detail = "减压阀限压"; }
                        else { c.status = "bad"; c.detail = "充压不上"; }
                    } else {
                        c.detail = "充压 " + fmt_dec(std::clamp(c.pressure / target_ * 100, 0.0, 99.0), 0) + "%";
                    }
                }
                int n = static_cast<int>(tested_.size());
                int done_n = n - static_cast<int>(fill_pend_.size());
                progress_ = 10.0 * done_n / std::max(1, n);
                phase_ = "充压中 · " + std::to_string(done_n) + "/" + std::to_string(n) + " 路完成";
                fill_round();
            });
        });
        return;
    }
    int cid = plan_[k].first;
    double wait = plan_[k].second - static_cast<double>(core_.clock().mono_ms() - round_t0_) / 1000.0;
    auto close_one = [this, k, cid] {
        open_.erase(std::remove(open_.begin(), open_.end(), cid), open_.end());
        auto f = open_.empty() ? core::valve_frame::all_closed() : core::valve_frame::gas(0, open_);
        ops_->apply(f, [this, k] { fill_close(k + 1); });
    };
    if (wait > 0) ops_->hold(core::valve_frame::gas(0, open_), wait, close_one);  // 保持期间同样做超压监视 + 心跳
    else close_one();
}

void LeakTest::fill_after() {
    // 充压完成：tested = 到压（含限压接受）的通道
    std::vector<int> ok;
    for (int id : tested_)
        if (std::find(fill_ok_.begin(), fill_ok_.end(), id) != fill_ok_.end()) ok.push_back(id);
    tested_ = ok;
    if (tested_.empty()) { fault("没有通道充压成功"); return; }
    over_fill_.clear();
    for (int id : tested_)
        if (cell(id).pressure > target_ * 1.25) over_fill_.push_back(id);
    line_vent();
}

// 总管泄压：关气源后开排空，避免总管余压经 SV 补进釜里、掩盖泄漏
void LeakTest::line_vent() {
    enter(leak_stage::kLineVent);
    phase_ = "总管泄压…";
    auto closed = core::valve_frame::all_closed();
    auto vent = core::valve_frame::vent_only();
    ops_->apply(closed, [this, closed, vent] {
        ops_->hold(closed, core::purge_plan::kDeadSec, [this, closed, vent] {
            ops_->apply(vent, [this, closed, vent] {
                ops_->hold(vent, 3, [this, closed] {
                    ops_->apply(closed, [this] {
                        progress_ = 12;
                        enter(leak_stage::kSettle, settle_min_ * 60);
                        for (int id : tested_) cell(id).detail = "平衡中";
                        phase_t0_ = core_.clock().mono_ms();
                        settle_loop();
                    });
                });
            });
        });
    });
}

// 平衡：全关静置，周期读压（同时做超压监视）
void LeakTest::settle_loop() {
    double total = settle_min_ * 60;
    double el = static_cast<double>(core_.clock().mono_ms() - phase_t0_) / 1000.0;
    sample(store::LeakRecord::kEquilibrate, tested_, [this, total, el](const PressureSample& ps) {
        if (!ops_->check()) return;
        for (int id : tested_) cell(id).pressure = ps.psi[static_cast<size_t>(id - 1)];
        progress_ = 12 + 23 * std::min(1.0, total > 0 ? el / total : 1.0);
        phase_ = "平衡中 · 剩余 " + mmss(total - el);
        if (el >= total) { measure_begin(); return; }
        ops_->hold(core::valve_frame::all_closed(), std::min(kSampleSec, total - el), [this] { settle_loop(); });
    });
}

void LeakTest::measure_begin() {
    series_.clear();
    t_ref_.clear();
    for (int id : tested_) {
        series_[id] = {};
        t_ref_[id] = gas_ops::temp_or_nan(core_, id);
    }
    if (rec_)
        for (auto& kv : t_ref_) rec_->t_ref()[kv.first] = kv.second;  // 逐点数据的补偿参考温度 = 判读用的同一参考
    phase_t0_ = core_.clock().mono_ms();
    enter(leak_stage::kMeasure, measure_min_ * 60);
    measure_loop();
}

// 测量：周期采样，按釜温折算到测量起点温度后最小二乘拟合压降速率（psi/hr）
void LeakTest::measure_loop() {
    double total = measure_min_ * 60;
    double el = static_cast<double>(core_.clock().mono_ms() - phase_t0_) / 1000.0;
    sample(store::LeakRecord::kMeasure, tested_, [this, total, el](const PressureSample& ps) {
        if (!ops_->check()) return;
        double vmin = el / 60;
        for (int id : tested_) {
            double psi = ps.psi[static_cast<size_t>(id - 1)];
            double t = gas_ops::temp_or_nan(core_, id), tr = t_ref_[id];
            double comp = std::isnan(t) || std::isnan(tr) ? psi : core::leak_math::compensate(psi, t, tr);
            auto& list = series_[id];
            list.emplace_back(vmin, comp);
            auto& c = cell(id);
            c.pressure = psi;
            std::optional<double> live;
            if (vmin >= 3) live = std::max(0.0, core::leak_math::drop_rate_per_hour(list));
            rate_[static_cast<size_t>(id)] = live;  // 快照 ratePsiH：前 3 分钟还没有
            // 文案按设置的压力单位（触屏 / 事件里不再出现 psi）
            // 前 3 min 还没有速率：显示相对测量开始的压降（比压力多一位小数，MPa 下才看得出几百 Pa 的变化）
            int dd = core::pressure_decimals(unit_) + 1;
            c.detail = live ? "≈ " + core::format_rate(*live, unit_)
                            : "降 " + core::fmt_dec(core::psi_to_unit(std::max(0.0, list[0].second - comp), unit_), dd, dd) + " " +
                                  core::pressure_label(unit_);
        }
        progress_ = 35 + 62 * std::min(1.0, total > 0 ? el / total : 1.0);
        phase_ = "测量中 · 剩余 " + mmss(total - el);
        if (el >= total) {
            std::map<int, double> r;
            for (auto& kv : series_) r[kv.first] = core::leak_math::drop_rate_per_hour(kv.second);
            rates_ = r;
            judge_and_vent();
            return;
        }
        ops_->hold(core::valve_frame::all_closed(), std::min(kSampleSec, total - el), [this] { measure_loop(); });
    });
}

// 判读 + 结束后尝试经排空阀泄压（开排空 + 被测通道 SV 6 s，再看釜压是否明显下降）
void LeakTest::judge_and_vent() {
    enter(leak_stage::kVent);
    for (int id : tested_) {
        double r = (*rates_)[id];
        auto& c = cell(id);
        c.status = r < thr_ ? "ok" : "bad";
        c.detail = core::format_rate(std::max(0.0, r), unit_);
        rate_[static_cast<size_t>(id)] = std::max(0.0, r);
    }
    phase_ = "尝试经排空阀泄压…";
    sample(store::LeakRecord::kVent, tested_, [this](const PressureSample& before) {
        vent_before_.clear();
        for (int id : tested_) vent_before_[id] = before.psi[static_cast<size_t>(id - 1)];
        auto f = core::valve_frame::vent_only(tested_);
        auto closed = core::valve_frame::all_closed();
        ops_->apply(f, [this, f, closed] {
            ops_->hold(f, 6, [this, closed] {
                ops_->apply(closed, [this, closed] {
                    ops_->hold(closed, 0.5, [this] {
                        sample(store::LeakRecord::kVent, tested_, [this](const PressureSample& after) {
                            bool all = true;
                            for (int id : tested_) {
                                double p = after.psi[static_cast<size_t>(id - 1)];
                                cell(id).pressure = p;
                                if (!(p < std::max(2.0, vent_before_[id] * 0.2))) all = false;
                            }
                            vented_ = all;
                            cleanup();
                        });
                    });
                });
            });
        });
    });
}

void LeakTest::fault(const std::string& why) {
    if (!running_ || ending_) return;
    fail_ = why;
    cleanup();
}

void LeakTest::cancelled() {
    if (!running_ || ending_) return;
    aborted_ = true;
    cleanup();
}

// C# finally：全关阀 → 释放气路 → 结束显示；之后 Finish / Activity.End / SaveRecord
void LeakTest::cleanup() {
    if (ending_) return;
    ending_ = true;
    ops_->cancel();
    ops_->when_idle([this] {
        core_.close_all_valves([this](bool, const std::string&) {
            core_.end_gas(core::GasOwner::Leak);
            running_ = false;
            finalize();
        });
    });
}

void LeakTest::finalize() {
    done_ = true;
    if (rates_) {
        core_.update_settings([now = core_.clock().wall_ms()](store::SettingsState& s) { s.settings.last_leak_test_utc_ms = now; });
        std::vector<int> judged, bad;
        for (int id : picked_) {
            const auto& c = cell(id);
            if (c.status == "ok" || c.status == "bad") judged.push_back(id);
            if (c.status == "bad") bad.push_back(id);
        }
        leak_failed_ = !bad.empty();
        failed_ = leak_failed_;
        progress_ = 100;
        phase_ = "测试结束";
        result_title_ = leak_failed_ ? "✕ 测试不通过" : "✓ 测试通过";
        result_text_ = leak_failed_ ? std::to_string(judged.size() - bad.size()) + "/" + std::to_string(judged.size()) + " 路通过；" + rvs(bad) +
                                          " 压降超标或充压失败。常见原因：面密封 O 圈、注射口密封、夹具未拧紧、进气接头松动。"
                                    : "全部 " + std::to_string(judged.size()) + " 路压降均 < " + core::format_rate(thr_, unit_) + "，密封良好。";
        if (!over_fill_.empty()) result_text_ += " " + rvs(over_fill_) + " 充压超出测试压力，请检查惰性气体减压阀是否调得过高。";
        double max_p = 0;
        for (int id : tested_) max_p = std::max(max_p, cell(id).pressure);
        if (vented_ && *vented_) result_text_ += " 测试后已经排空阀泄压。";
        else if (vented_)
            result_text_ += " 注意：经排空阀泄压后釜压未明显下降，仍约 " + core::format_p(max_p, unit_) +
                            "（进气单向阀很可能只允许气体由总管进釜），拆釜前请先手动泄压。";
        else result_text_ += " 泄压未完成，釜内仍有测试压力，拆釜前请先泄压。";
        if (fail_) result_text_ += " 泄压阶段出错：" + *fail_ + "。";
        std::string detail;
        for (size_t i = 0; i < judged.size(); ++i)
            detail += (i ? " · " : "") + core::chan_name(judged[i]) + " " + cell(judged[i]).detail;
        core_.alarms().log(leak_failed_ ? "warn" : "info", "系统", leak_failed_ ? "泄漏测试不通过" : "泄漏测试通过", detail,
                           leak_failed_ ? "leak.failed" : "leak.passed");
        core_.emit_notice(leak_failed_ ? "err" : "ok", leak_failed_ ? "泄漏测试不通过 · " + rvs(bad) : "泄漏测试通过");
    } else {
        failed_ = true;
        leak_failed_ = false;
        for (int id : picked_)
            if (cell(id).status.empty()) cell(id).detail = "未完成";
        phase_ = aborted_ ? "已中止" : "已停止";
        result_title_ = aborted_ ? "测试已中止" : "测试失败";
        result_text_ = (aborted_ ? std::string("已按要求中止，阀门已全关。") : *fail_ + "。阀门已全关。") + "如已充压，釜内仍有压力，拆釜前请先泄压。";
        // 泄漏测试失败是一次操作的结果，按「提醒」记（warn），不当报警
        core_.alarms().log("warn", "系统", aborted_ ? "泄漏测试中止" : "泄漏测试失败",
                           aborted_ ? "手动中止 · 阀门已全关" : *fail_ + " · 阀门已全关", aborted_ ? "leak.aborted" : "leak.error");
        core_.emit_notice(aborted_ ? "warn" : "err", aborted_ ? "泄漏测试已中止 · 阀门全关" : "泄漏测试失败 · " + *fail_);
    }
    // LeakActivity.End
    enter(leak_stage::kDone);
    act_vented_ = vented_;
    act_aborted_ = aborted_;
    act_fail_ = fail_;
    finished_at_ = core_.clock().wall_ms();
    act_failed_ = failed_;
    final_progress_ = progress_;
    save_record();
}

void LeakTest::save_record() {
    if (!rec_) return;
    std::vector<store::LeakCell> cells;
    for (int id : picked_) cells.push_back(store::LeakCell{id, cell(id).status, cell(id).detail});
    auto rows = rec_->summarize(cells, rates_ ? &*rates_ : nullptr);
    store::LeakParams p;
    p.test_psi = target_;
    p.threshold_psi_per_h = thr_;
    p.settle_min = settle_min_;
    p.measure_min = measure_min_;
    p.sample_sec = kSampleSec;
    p.aborted = aborted_;
    p.fail = fail_;
    if (logger_) {
        for (auto& f : rec_->files(rows, p)) logger_->write_file(f.first, f.second);
        logger_->add_marker("泄漏测试结束 " + rec_->run_id());
    }
    last_run_id_ = rec_->run_id();
    rec_.reset();
}

// ============================ 快照（C# LeakActivity.ToSnap） ============================

std::optional<double> LeakTest::stage_left_sec() const {
    bool active = stage_ && *stage_ != leak_stage::kDone;
    if (!active || !stage_ends_mono_) return std::nullopt;
    return core::program::round_even(std::max(0.0, static_cast<double>(*stage_ends_mono_ - core_.clock().mono_ms()) / 1000.0));
}

double LeakTest::est_sec(const std::string& st) const {
    if (st == leak_stage::kPrep) return kPrepEstSec;
    if (st == leak_stage::kFill) return kFillEstSec;
    if (st == leak_stage::kLineVent) return kLineVentEstSec;
    if (st == leak_stage::kSettle) return settle_sec_;
    if (st == leak_stage::kMeasure) return measure_sec_;
    if (st == leak_stage::kVent) return kVentEstSec;
    return 0;
}

std::optional<double> LeakTest::total_left_sec() const {
    static const char* kOrder[] = {leak_stage::kPrep, leak_stage::kFill, leak_stage::kLineVent, leak_stage::kSettle, leak_stage::kMeasure,
                                   leak_stage::kVent};
    if (!stage_ || *stage_ == leak_stage::kDone) return std::nullopt;
    int idx = -1;
    for (int i = 0; i < 6; ++i)
        if (*stage_ == kOrder[i]) idx = i;
    if (idx < 0) return std::nullopt;
    double el = std::max(0.0, static_cast<double>(core_.clock().mono_ms() - stage_started_mono_) / 1000.0);
    double left;
    if (*stage_ == leak_stage::kFill) left = std::max(kFillMinLeftSec, kFillEstSec - el);
    else if (*stage_ == leak_stage::kSettle || *stage_ == leak_stage::kMeasure) left = stage_left_sec().value_or(0);
    else left = std::max(0.0, est_sec(*stage_) - el);
    for (int i = idx + 1; i < 6; ++i) left += est_sec(kOrder[i]);
    return core::program::round_even(left);
}

std::string LeakTest::status_code(bool picked, bool finished, const std::string& status, const std::string& detail) {
    if (!picked) return "untested";
    if (status == "ok") return "pass";
    if (status == "bad") return detail == "充压不上" || detail == "充压超时" ? "fillFail" : "fail";
    if (status == "skip") return detail == "初始压力过高" ? "tooHigh" : "untested";
    return finished ? "untested" : "testing";
}

ojson LeakTest::snapshot() const {
    std::optional<std::string> run_id = rec_ ? std::optional<std::string>(rec_->run_id()) : std::nullopt;
    if (!stage_) {
        ojson j = ActivityHooks().leak_test();
        j["running"] = running_;
        j["progress"] = store::json_num(round_to(progress_, 1));
        j["phase"] = running_ ? ojson(phase_) : ojson(nullptr);
        j["runId"] = run_id ? ojson(*run_id) : ojson(nullptr);
        return j;
    }
    bool finished = *stage_ == leak_stage::kDone;
    double prog = finished && !running_ ? final_progress_ : progress_;
    ojson chans = ojson::array();
    for (int id = 1; id <= 8; ++id) {
        const auto& c = cells_[static_cast<size_t>(id - 1)];
        bool picked = act_picked_.count(id) > 0;
        std::string st = picked ? c.status : "skip", det = picked ? c.detail : c.detail;
        ojson o;
        o["id"] = id;
        o["status"] = status_code(picked, finished, st, det);
        o["pressurePsi"] = picked && std::isfinite(c.pressure) ? ojson(store::json_num(round_to(c.pressure, 3))) : ojson(nullptr);
        auto r = rate_[static_cast<size_t>(id)];
        o["ratePsiH"] = picked && r ? ojson(store::json_num(round_to(*r, 3))) : ojson(nullptr);
        o["detail"] = det;
        chans.push_back(o);
    }
    ojson j;
    j["running"] = running_;
    j["progress"] = store::json_num(round_to(prog, 1));
    j["phase"] = running_ ? ojson(phase_) : ojson(nullptr);
    j["runId"] = run_id ? ojson(*run_id) : ojson(nullptr);
    j["done"] = finished;
    j["failed"] = finished && act_failed_;
    j["stage"] = *stage_;
    j["stageLeftSec"] = num_or_null(stage_left_sec());
    j["totalLeftSec"] = num_or_null(total_left_sec());
    j["startedAt"] = started_at_ ? ojson(store::fmt_utc_json(*started_at_)) : ojson(nullptr);
    j["startedBy"] = started_by_;
    j["targetPsi"] = store::json_num(target_);
    j["thresholdPsiH"] = store::json_num(thr_);
    j["channels"] = chans;
    j["vented"] = act_vented_ ? ojson(*act_vented_) : ojson(nullptr);
    j["aborted"] = act_aborted_;
    j["failReason"] = act_fail_ ? ojson(*act_fail_) : ojson(nullptr);
    j["finishedAt"] = finished_at_ ? ojson(store::fmt_utc_json(*finished_at_)) : ojson(nullptr);
    // ---- 新字段（第二阶段）----
    j["lastRunId"] = last_run_id_.empty() ? ojson(nullptr) : ojson(last_run_id_);
    j["phaseText"] = phase_.empty() ? ojson(nullptr) : ojson(phase_);
    j["resultTitle"] = result_title_.empty() ? ojson(nullptr) : ojson(result_title_);
    j["resultText"] = result_text_.empty() ? ojson(nullptr) : ojson(result_text_);
    return j;
}

}  // namespace reactor::ctl
