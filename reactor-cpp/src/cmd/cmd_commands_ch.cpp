// 命令层：通道 / 温度 / PID / 自整定 / 搅拌 / 阀门 / 程序 / 泄漏测试 / 容积标定。
// C#: Commands/OperatorCommands.Channels.cs、.Temp.cs、.Stir.cs、.Valves.cs、.Program.cs、.Leak.cs、.Calib.cs
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>
#include <memory>
#include <set>

#include "cmd/cmd_commands.hpp"
#include "common/clock.hpp"
#include "core/num_format.hpp"
#include "core/op_codes.hpp"
#include "core/program/program_step_format.hpp"
#include "core/value_rules.hpp"
#include "core/valve_frame.hpp"
#include "store/store_util.hpp"
#include "ctl/ctl_gas_ops.hpp"
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace reactor::cmd {

namespace oc = core::op_codes;
namespace vr = core::value_rules;
using core::ReactorState;
using core::fmt_dec;

namespace {
ojson jnum(double v) { return core::program::json_number(v); }
// C# OperatorCommands.Fmt（设置事件里的数值）："0.###" + 单位
std::string fmt_unit(double v, const std::string& unit) {
    std::string n = fmt_dec(v, 3);
    return unit.empty() ? n : n + " " + unit;
}
std::optional<OpResult> invalid(const std::optional<core::RuleViolation>& v) {
    if (!v) return std::nullopt;
    return OpResult::invalid(*v);
}
}  // namespace

// ==================== 公共守卫 ====================

std::optional<OpResult> Commands::channel_or(int id) const {
    if (id >= 1 && id <= 8) return std::nullopt;
    return OpResult::fail(oc::kChannelNotFound, "通道 " + core::chan_name(id) + " 不存在（有效范围 1–8 号）");
}

std::optional<OpResult> Commands::program_owned(int id) const {
    if (!programs().is_owned(id)) return std::nullopt;
    return OpResult::fail(oc::kChannelProgramOwned, core::chan_name(id) + " 正在执行程序 · 请先中止程序", "warn");
}

std::optional<OpResult> Commands::heat_blocked(int id) const {
    if (auto b = ctl::gas_ops::heat_block(core(), id, d_.unit)) return OpResult::fail(b->code, b->message);
    return std::nullopt;
}

bool Commands::gas_busy_not_program() const {
    const auto& l = core().gas_lease();
    return l.busy() && l.owner() != core::GasOwner::Program;
}

std::string Commands::gas_task() const { return core().gas_lease().task(); }

void Commands::log_setting(const std::string& key, const std::string& old_v, const std::string& new_v) {
    if (old_v == new_v) return;
    core().alarms().log("info", "设置", "修改 · " + key, old_v + " → " + new_v, "settings.changed");
}

// ==================== 启用 / 停止 / 停用 ====================

OpResult Commands::enable_channel(const Source& src, int id, bool default_volume) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    if (gas_busy_not_program()) return OpResult::fail(oc::kGasBusy, gas_task() + "进行中 · 请等待完成或先停止", "warn");
    auto& c = core().channel_mut(id);
    if (CommandPolicy::strict(src) &&
        (c.state == ReactorState::Alarm || core().is_over_temp_tripped(id) || core().is_over_pressure_tripped(id)))
        return OpResult::fail(oc::kChannelAlarm, core::chan_name(id) + " 存在报警或超温 / 超压未解除 · 请先处理");
    if (default_volume && c.vol == 0) c.vol = 3;
    c.set_state(ReactorState::Ready);
    c.started_by = started_by_of(src);
    return OpResult::done(core::chan_name(id) + " 已启用");
}

OpResult Commands::start_channel(const Source& src, int id, std::optional<double> temp_c) {
    // /channels/{id}/start = 启用 + 启动加热（同一次控制线程调用）：启用失败直接返回；加热结果为主结果
    auto en = enable_channel(src, id, true);
    if (!en.ok) return en;
    return start_heat(src, id, temp_c);
}

void Commands::stop_channel(const Source& src, int id, bool disable, Done done) {
    if (auto d = admit(src, ActionClass::Stop)) return done(*d);
    if (auto nf = channel_or(id)) return done(*nf);
    std::string what = disable ? "已停用，不参与运行" : "已停止（含加热输出）";
    core().stop_channel(id, disable, [id, what, done](const ctl::ChannelStopResult& r) {
        OpResult res = r.program_aborted ? OpResult::none()
                       : r.heat_ok       ? OpResult::done(core::chan_name(id) + " " + what, "warn")
                                         : OpResult::fail(oc::kHardwareWriteFailed, r.heat_error, "err",
                                                          ojson{{"device", "temp"}, {"channel", id}});
        if (!r.valve_ok) res.warnings.push_back(r.valve_error);   // C# r.Then(v).WithWarnings(v.Message)：主结果不变
        if (res.ok) {
            res.silent = false;
            res.data = ojson{{"programAborted", r.program_aborted},
                             {"gasAborted", r.gas_aborted ? ojson(*r.gas_aborted) : ojson(nullptr)}};
        }
        if (r.gas_aborted) res.warnings.push_back("已中止气路序列：" + *r.gas_aborted);
        done(res);
    });
}

OpResult Commands::copy_channel_config(const Source& src, int from, const std::optional<std::vector<int>>& to) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(from)) return *nf;
    const auto& st = core().settings().settings;
    const auto& s = core().channel(from);
    bool strict = CommandPolicy::strict(src);
    if (strict) {
        if (auto bad = invalid(vr::temp_sp(s.tsp, st.t_max_sp))) return *bad;
        if (auto bad = invalid(vr::strict_temp_sp(s.tsp, st.over_temp))) return *bad;
        if (auto bad = invalid(vr::volume(s.vol, st.vol_max))) return *bad;
    }
    std::vector<int> targets;
    std::vector<std::string> warnings;
    if (!to) {
        for (int i = 1; i <= 8; ++i)
            if (core().channel(i).state != ReactorState::Idle && !programs().is_owned(i)) targets.push_back(i);
    } else {
        std::set<int> seen;
        for (int id : *to) {
            if (!seen.insert(id).second) continue;
            if (id == from) continue;
            if (id < 1 || id > 8) return OpResult::fail(oc::kChannelNotFound, "通道 " + core::chan_name(id) + " 不存在（有效范围 1–8 号）");
            if (programs().is_owned(id)) { warnings.push_back(core::chan_name(id) + " 正在执行程序 · 已跳过"); continue; }
            targets.push_back(id);
        }
    }
    ojson channels = ojson::array(), replanned = ojson::array();
    for (int x : targets) {
        auto& c = core().channel_mut(x);
        if (strict && x != from && core::state_is_active(c.state)) { warnings.push_back(core::chan_name(x) + " 运行中 · 已跳过"); continue; }
        c.tsp = s.tsp;
        c.psp = s.psp;
        c.rpm_sp = s.rpm_sp;
        c.vol = s.vol;
        if (x != from) {   // 接口契约 §3.3：到位范围、防超调、升温方式一起复制
            core().set_sp_mode(x, s.sp_mode);
            std::lock_guard<std::mutex> lk(prefs_mu_);
            prefs_[static_cast<size_t>(x - 1)] = prefs_[static_cast<size_t>(from - 1)];
        }
        if (x != from) {
            channels.push_back(x);
            if (c.approach.active()) replanned.push_back(x);
        }
    }
    save_prefs();
    auto r = OpResult::done("已复制本配置到其余通道", "ok",
                            ojson{{"from", from}, {"channels", channels}, {"approachReplanned", replanned}});
    r.with_warnings(warnings);
    return r;
}

OpResult Commands::set_volume(const Source& src, int id, double ml) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto bad = invalid(vr::volume(ml, core().settings().settings.vol_max))) return *bad;
    if (CommandPolicy::strict(src))
        if (auto o = program_owned(id)) return *o;
    ml = vr::round_volume(ml);
    core().channel_mut(id).vol = ml;
    double gv = std::round(core().gas_volume_ml(id) * 100.0) / 100.0;
    auto r = OpResult::none();
    r.data = ojson{{"channel", id}, {"ml", jnum(ml)}, {"gasVolumeMl", jnum(gv)}};
    return r;
}

OpResult Commands::reset_gas_counter(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    core().reset_uptake(id);
    bool started = core().start_uptake(id, "清零后重新计量");
    double gv = core().gas_volume_ml(id);
    if (!started)
        return OpResult::fail(oc::kParamCrossCheck,
                              core::chan_name(id) + " 气相体积不合理（" + fmt_dec(gv, 1) + " mL）· 请检查溶液体积与设置页「气体消耗计算」", "warn");
    return OpResult::done(core::chan_name(id) + " 吸气量已清零，开始计量 · 关阀状态下的压降计入，开阀补气不计入", "ok",
                          ojson{{"gasVolumeMl", jnum(gv)}});
}

// ==================== 每路手动设定（接口契约 §3.3） ====================

ChannelPrefs Commands::prefs(int id) const {
    std::lock_guard<std::mutex> lk(prefs_mu_);
    return id >= 1 && id <= 8 ? prefs_[static_cast<size_t>(id - 1)] : ChannelPrefs{};
}

void Commands::save_prefs() const {
    if (d_.prefs_path.empty()) return;
    ojson a = ojson::array();
    {
        std::lock_guard<std::mutex> lk(prefs_mu_);
        for (size_t i = 0; i < 8; ++i)
            a.push_back(ojson{{"id", i + 1}, {"bandC", jnum(prefs_[i].band_c)}, {"antiOvershoot", prefs_[i].anti_overshoot}});
    }
    store::write_file_atomic(d_.prefs_path, store::dump_json(ojson{{"channels", a}}, 2), true);
}

OpResult Commands::set_band(const Source& src, int id, double band_c) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    if (auto bad = invalid(vr::range("到位范围", band_c, 0.5, 10, "℃"))) return *bad;
    {
        std::lock_guard<std::mutex> lk(prefs_mu_);
        prefs_[static_cast<size_t>(id - 1)].band_c = band_c;
    }
    save_prefs();
    const auto& c = core().channel(id);
    if (c.heat_watch.active) core().begin_heat_watch(id, c.heat_watch.target, band_c);
    return OpResult::done(core::chan_name(id) + " 到位范围已设为 ±" + fmt_dec(band_c, 1) + " ℃", "ok", ojson{{"channel", id}, {"bandC", jnum(band_c)}});
}

OpResult Commands::set_anti_overshoot(const Source& src, int id, bool on) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    {
        std::lock_guard<std::mutex> lk(prefs_mu_);
        prefs_[static_cast<size_t>(id - 1)].anti_overshoot = on;
    }
    save_prefs();
    return OpResult::done(core::chan_name(id) + (on ? " 防超调已开启" : " 防超调已关闭 · 下次启动加热直接写目标温度"), "ok",
                          ojson{{"channel", id}, {"antiOvershoot", on}});
}

void Commands::heat_channel(int id) {
    auto p = prefs(id);
    const auto& c = core().channel(id);
    if (p.anti_overshoot || c.sp_mode != "fixed") {
        core().start_fixed_heat(id);
    } else {
        core().start_band_heat(id, c.tsp);   // 防超调关：全链启动后直接写目标温度
    }
    if (c.sp_mode == "fixed") core().begin_heat_watch(id, c.tsp, p.band_c);
}

// ==================== 温度 ====================

OpResult Commands::start_heat(const Source& src, int id, std::optional<double> temp_c) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    if (auto b = heat_blocked(id)) return *b;
    const auto& st = core().settings().settings;
    if (temp_c) {
        if (auto bad = invalid(vr::temp_sp(*temp_c, st.t_max_sp))) return *bad;
        if (CommandPolicy::strict(src))
            if (auto hot = invalid(vr::strict_temp_sp(*temp_c, st.over_temp))) return *hot;
    }
    auto& c = core().channel_mut(id);
    if (temp_c.value_or(c.tsp) < 35) return OpResult::fail(oc::kParamOutOfRange, "请先设置目标温度（≥35℃）再启动加热");
    if (CommandPolicy::strict(src) &&
        !(c.state == ReactorState::Ready || c.state == ReactorState::Done || c.state == ReactorState::Heating))
        return OpResult::fail(oc::kChannelStateInvalid, core::chan_name(id) + " 当前状态「" + core::state_zh(c.state, c.over_temp) +
                                                            "」不可启动加热 · 请先启用通道");
    if (temp_c) c.tsp = *temp_c;
    if (c.state == ReactorState::Idle || c.state == ReactorState::Done || c.state == ReactorState::Ready)
        c.set_state(ReactorState::Heating);   // 加热真的开始了才显示「升温中」
    c.started_by = started_by_of(src);
    heat_channel(id);
    return OpResult::accept(core::chan_name(id) + " 加热已启动 · 目标 " + fmt_dec(c.tsp, 0) + "℃ · 防超调逼近", "ok",
                            ojson{{"tempC", jnum(c.tsp)}});
}

void Commands::stop_heat(const Source& src, int id, Done done) {
    if (auto d = admit(src, ActionClass::Stop)) return done(*d);
    if (auto nf = channel_or(id)) return done(*nf);
    if (programs().is_owned(id)) {
        auto r = programs().abort(id);
        if (r.silent) return done(OpResult::none());
        return done(OpResult::done(core::chan_name(id) + " 停止加热 · 正在执行的程序已中止", "warn", ojson{{"programAborted", true}}));
    }
    auto& c = core().channel_mut(id);
    if (c.state == ReactorState::Heating) c.set_state(ReactorState::Ready);
    core().stop_heat(id, [id, done](bool ok) {
        if (ok) return done(OpResult::done(core::chan_name(id) + " 加热已停止（输出关闭）", "warn"));
        done(OpResult::fail(oc::kHardwareWriteFailed, core::chan_name(id) + " 停止加热未确认（温控通讯失败）· 请点主页的「全部停止」或到设备现场确认",
                            "err", ojson{{"device", "temp"}, {"channel", id}}));
    });
}

OpResult Commands::set_setpoint(const Source& src, int id, double temp_c) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    const auto& st = core().settings().settings;
    if (auto bad = invalid(vr::temp_sp(temp_c, st.t_max_sp))) return *bad;
    if (CommandPolicy::strict(src))
        if (auto hot = invalid(vr::strict_temp_sp(temp_c, st.over_temp))) return *hot;
    core().retarget(id, temp_c);
    bool replanned = core().channel(id).approach.active();
    // C# HmiRemoteCommands.SetSetpointAsync：SP 由逼近在下一个周期写入仪表（不在这里等硬件）
    return OpResult::done(core::chan_name(id) + " 目标温度已设为 " + fmt_dec(temp_c, 1) + " ℃", "ok",
                          ojson{{"channel", id}, {"tempSpC", jnum(temp_c)}, {"applied", false}, {"coalesced", false},
                                {"approachReplanned", replanned}});
}

OpResult Commands::set_sp_mode(const Source& src, int id, const std::string& mode) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    if (mode != "fixed" && mode != "curve")
        return OpResult::fail(oc::kParamOutOfRange, "升温方式须为 fixed（定值）或 curve（曲线）", "err", ojson{{"value", mode}});
    const auto& c = core().channel(id);
    if (CommandPolicy::strict(src) && mode != c.sp_mode && (c.state == ReactorState::Heating || c.profile.running()))
        return OpResult::fail(oc::kChannelBusy, core::chan_name(id) + " 加热 / 曲线运行中 · 请先停止再切换升温方式");
    core().set_sp_mode(id, mode);
    auto r = OpResult::none();
    r.data = ojson{{"mode", mode}};
    return r;
}

std::optional<OpResult> Commands::profile_locked(const Source& src, int id) const {
    if (!CommandPolicy::strict(src)) return std::nullopt;
    if (auto o = program_owned(id)) return o;
    if (core().channel(id).profile.running())
        return OpResult::fail(oc::kCurveRunning, core::chan_name(id) + " 曲线升温运行中 · 请先停止曲线再修改");
    return std::nullopt;
}

OpResult Commands::replace_profile(const Source& src, int id, const std::vector<std::pair<double, double>>& segs) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto bad = invalid(vr::profile_seg_count(static_cast<double>(segs.size()), core::temp_profile::kMaxSegments))) return *bad;
    const auto& st = core().settings().settings;
    for (size_t i = 0; i < segs.size(); ++i) {
        auto v = vr::profile_segment(segs[i].first, segs[i].second, st.t_max_sp);
        if (!v && CommandPolicy::strict(src)) v = vr::strict_temp_sp(segs[i].first, st.over_temp);
        if (v)
            return OpResult::fail(v->code, "第 " + std::to_string(i + 1) + " 段：" + v->message, "err",
                                  ojson{{"index", i + 1}, {"min", jnum(v->min)}, {"max", jnum(v->max)}, {"value", jnum(v->value)}});
    }
    if (auto l = profile_locked(src, id)) return *l;
    std::vector<core::ProfileSegment> ps;
    for (const auto& s : segs) ps.push_back(core::ProfileSegment{s.first, s.second});
    core().set_profile(id, ps);
    return OpResult::done(core::chan_name(id) + " 曲线已更新 · 共 " + std::to_string(segs.size()) + " 段", "ok",
                          ojson{{"segCount", segs.size()}});
}

OpResult Commands::start_profile(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    if (auto o = program_owned(id)) return *o;
    if (auto b = heat_blocked(id)) return *b;
    const auto& c = core().channel(id);
    if (CommandPolicy::strict(src) && c.state == ReactorState::Alarm)
        return OpResult::fail(oc::kChannelAlarm, core::chan_name(id) + " 报警中 · 请先处理并停止 / 复位");
    double start_pv = c.t_online && c.t > 1 ? c.t : 25;
    core().start_curve(id, started_by_of(src));
    int n = core().channel(id).profile.seg_count;
    return OpResult::accept(core::chan_name(id) + " 曲线升温已启动 · 共 " + std::to_string(n) + " 段", "ok",
                            ojson{{"segCount", n}, {"startPv", jnum(start_pv)}});
}

OpResult Commands::stop_profile(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Stop)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    core().stop_curve(id);
    auto r = OpResult::done(core::chan_name(id) + " 曲线升温已停止 · 保持当前给定值", "warn");
    const auto& c = core().channel(id);
    if (c.state == ReactorState::Heating) r.warnings.push_back("加热仍在按 " + fmt_dec(c.tsp, 1) + " ℃ 运行");
    return r;
}

// ==================== PID / 自整定 ====================

void Commands::set_pid(const Source& src, int ch, int band, double p, double i, double d, Done done) {
    if (auto deny = admit(src, ActionClass::Control)) return done(*deny);
    if (auto nf = channel_or(ch)) return done(*nf);
    if (auto bad = invalid(vr::pid(band, p, i, d, store::kBandCount))) return done(*bad);
    auto old = core().settings().band_pids[static_cast<size_t>(ch - 1)][static_cast<size_t>(band)];
    std::string name = core::chan_name(ch);
    std::vector<std::tuple<std::string, std::string, std::string>> changes;
    auto diff = [&](const std::string& key, double o, double n, const std::string& unit) {
        if (fmt_unit(o, unit) != fmt_unit(n, unit)) changes.emplace_back(key, fmt_unit(o, unit), fmt_unit(n, unit));
    };
    diff("P", old.p, p, "");
    diff("I", old.i, i, "s");
    diff("D", old.d, d, "s");
    int64_t now = core().clock().wall_ms();
    core().update_settings([&](store::SettingsState& s) {
        s.band_pids[static_cast<size_t>(ch - 1)][static_cast<size_t>(band)] = store::Pid{p, i, d};
        s.has_band_pids = true;
        store::tune_mark_manual(s, ch - 1, band, now);
    });
    // 仪表当前生效的就是这一档：标记「档未知」，下一次写 SP 时按档重写 PID（C# 立即写仪表；见报告「和 C# 不一样」）
    bool live = core().temp().live_band(ch) == band;
    core().temp_mut().bump_pid_gen(ch);
    if (live) core().temp_mut().forget_live_band(ch);
    for (auto& c : changes) log_setting(name + " " + std::get<0>(c), std::get<1>(c), std::get<2>(c));
    auto r = OpResult::done(live ? name + " PID 已写入仪表" : name + " PID 已保存 · 切到该档时生效", "ok",
                            ojson{{"live", live}, {"band", band}});
    r.set_quiet();
    done(r);
}

void Commands::start_autotune(const Source& src, int id, double temp_c, std::optional<int> band, Done done) {
    if (auto d = admit(src, ActionClass::Control)) return done(*d);
    if (auto nf = channel_or(id)) return done(*nf);
    d_.automation->tuner().start(id, temp_c, band, CommandPolicy::strict(src), done);
}

void Commands::cancel_autotune(const Source& src, int id, Done done) {
    if (auto d = admit(src, ActionClass::Stop)) return done(*d);
    if (auto nf = channel_or(id)) return done(*nf);
    d_.automation->tuner().cancel(id, done);
}

// ==================== 搅拌 ====================

void Commands::set_stir(const Source& src, std::optional<bool> on, std::optional<int> rpm, Done done) {
    // C# HmiRemoteCommands.SetStirAsync：rpm 先于 on 应用
    std::optional<OpResult> r1;
    if (rpm) {
        int v = *rpm;
        if (auto d = admit(src, ActionClass::Control)) return done(*d);
        if (auto bad = invalid(vr::rpm(v, core().settings().settings.rpm_max))) return done(*bad);
        if (CommandPolicy::strict(src) && programs().running_count() > 0)
            return done(OpResult::fail(oc::kStirProgramOwned, "程序执行中 · 搅拌转速由程序控制"));
        log_setting("搅拌转速", std::to_string(core().stir().rpm()) + " rpm", std::to_string(v) + " rpm");
        bool was_on = core().stir().on();
        core().stir_set_rpm(v);
        if (was_on) r1 = OpResult::done("搅拌转速已设为 " + std::to_string(v) + " rpm · 全部反应釜", "ok", ojson{{"rpm", v}});
        else {
            r1 = OpResult::none();
            r1->data = ojson{{"rpm", v}};
        }
        if (!on) return done(*r1);
    }
    bool b = *on;
    if (auto d = admit(src, b ? ActionClass::Control : ActionClass::Stop)) return done(*d);
    if (CommandPolicy::strict(src) && b) {
        if (!core().stir().configured())
            return done(OpResult::fail(oc::kHwNotConfigured, "搅拌未配置串口（hardware.json stir.port），无法启动搅拌"));
        if (auto srv = programs().stir_step_rv())
            return done(OpResult::fail(oc::kStirProgramOwned, core::chan_name(*srv) + " 程序搅拌步骤进行中 · 搅拌由程序控制"));
    }
    int cur_rpm = core().stir().rpm();
    std::string msg = b ? "搅拌已开启 · 全部反应釜 " + std::to_string(cur_rpm) + " rpm" : "搅拌已停止 · 全部反应釜";
    std::string kind = b ? "ok" : "warn";
    OpResult r;
    if (core().stir().on() == b) {
        r = OpResult::done(msg, kind);
    } else {
        if (b) core().stir_start(cur_rpm);
        else core().stir_stop();
        core().file_event("info", "搅拌", b ? "搅拌开启" : "搅拌停止",
                          std::to_string(cur_rpm) + " rpm · " + (src.is_remote() ? src.label() : std::string("工具栏")),
                          b ? "stir.on" : "stir.off");
        r = OpResult::done(msg, kind, ojson{{"on", b}, {"rpm", cur_rpm}});
    }
    if (r1) {
        r.with_warnings(r1->warnings);
        r.data = ojson{{"on", b}, {"rpm", core().stir().rpm()}};
    }
    done(r);
}

// ==================== 阀门 ====================

std::optional<OpResult> Commands::local_manual_gate(const Source& src, const std::string& need_mode) const {
    if (src.is_remote()) return std::nullopt;
    // 触屏：本机身份即管理员；还要先打开「手动阀控」开关（同 C#）
    if (!core().manual_valve()) return OpResult::fail(oc::kChannelStateInvalid, need_mode, "warn");
    return std::nullopt;
}

OpResult Commands::set_manual_valve_mode(const Source& src, bool on) {
    if (auto d = admit(src, on ? ActionClass::Control : ActionClass::Stop, Exposure::LocalOnly)) return *d;
    auto locked = d_.automation->gas_locked_for_manual();
    if (!locked.ok) return locked;
    if (on && !core().manual_valve() && !core().valves().configured())
        return OpResult::fail(oc::kHwNotConfigured, "阀门模块未配置串口（hardware.json io.port），无法手动阀控");
    bool was = core().manual_valve();
    core().set_manual_valve(on);
    // 关手动阀控：顺手把手动开着的阀全关（C# ToggleManual）
    if (!on && was) {
        bool any = false;
        for (bool v : core().valves().shown()) any = any || v;
        if (any) core().close_all_valves();
    }
    return on ? OpResult::done("手动阀控已开启 · 点击阀门可手动操作", "warn", ojson{{"manualValve", on}})
              : OpResult::done("手动阀控已关闭", "ok", ojson{{"manualValve", on}});
}

void Commands::manual_valve_tail(const Source& src, const std::string& name, bool open, const core::ValveFrame& frame,
                                 const std::string& open_kind, Done done) {
    if (!core().valves().configured())
        return done(OpResult::fail(oc::kHwNotConfigured, "阀门模块未配置串口（hardware.json io.port），无法操作"));
    if (manual_valve_busy_) return done(OpResult::fail(oc::kValveBusy, "上一个阀门操作还在进行，请稍候", "warn"));
    if (!core::valve_frame::is_safe(frame)) {
        auto why = core::valve_frame::check_apply(frame, false, {}, "");
        return done(OpResult::fail(oc::kChannelStateInvalid,
                                   name + (open ? "不能打开" : "不能关闭") + " · " + why.value_or("阀门组合不安全"), "warn"));
    }
    manual_valve_busy_ = true;
    std::string label = src.is_remote() ? src.label() : std::string("手动阀控");
    core().write_valves(frame, ctl::ValveWriteOpts{}, [this, name, open, open_kind, label, done](bool ok, const std::string& err) {
        manual_valve_busy_ = false;
        if (!ok) {
            core().alarms().log("err", "阀门", std::string("手动") + (open ? "打开" : "关闭") + " " + name + " 失败",
                                "IO16R 通讯异常 · 阀门状态未改变", "alarm.manualValveFailed");
            return done(OpResult::fail(oc::kHardwareWriteFailed,
                                       name + (open ? "打开" : "关闭") + "失败（IO16R 通讯）· 阀门状态未改变", "err",
                                       ojson{{"device", "io"}, {"valve", name}, {"error", err}}));
        }
        core().alarms().log(open ? "warn" : "info", "阀门", std::string("手动") + (open ? "打开" : "关闭") + " " + name,
                            label + " · 写入已确认", open ? "valve.manualOpen" : "valve.manualClose");
        done(OpResult::done(name + " " + (open ? "已开" : "已关"), open ? open_kind : "ok", ojson{{"valve", name}, {"open", open}}));
    });
}

void Commands::set_reactor_valve(const Source& src, int id, bool open, Done done) {
    if (auto d = admit(src, open ? ActionClass::Control : ActionClass::Stop)) return done(*d);
    if (auto nf = channel_or(id)) return done(*nf);
    auto locked = d_.automation->gas_locked_for_manual();
    if (!locked.ok) return done(locked);
    if (auto g = local_manual_gate(src, "请先打开 1 号左侧的「手动阀控」开关")) return done(*g);
    const auto& c = core().channel(id);
    if (open && core().is_over_pressure_tripped(id))
        return done(OpResult::fail(oc::kChannelAlarm, core::chan_name(id) + " 超压联锁未解除 · 禁止打开 SV" + std::to_string(id)));
    if (open && (c.state == ReactorState::Pressing || c.state == ReactorState::React || c.state == ReactorState::Alarm))
        return done(OpResult::fail(oc::kChannelBusy, "加压/反应中禁止手动开阀（" + core::chan_name(id) + "）"));
    auto frame = core().valves().shown();
    frame[static_cast<size_t>(id - 1)] = open;
    manual_valve_tail(src, "SV" + std::to_string(id), open, frame, "ok", done);
}

void Commands::set_gas_inlet(const Source& src, int index, bool open, Done done) {
    if (auto d = admit(src, open ? ActionClass::Control : ActionClass::Stop)) return done(*d);
    auto locked = d_.automation->gas_locked_for_manual();
    if (!locked.ok) return done(locked);
    if (auto g = local_manual_gate(src, "请先开启「手动阀控」")) return done(*g);
    if (index < 0 || index >= 3)
        return done(OpResult::fail(oc::kNotFound, "气源 " + std::to_string(index + 1) + " 不存在（有效范围 1–3）"));
    if (open && core().any_over_pressure_tripped())
        return done(OpResult::fail(oc::kChannelAlarm, "有通道超压联锁未解除 · 禁止打开气源"));
    auto frame = core().valves().shown();
    frame[static_cast<size_t>(core::valve_frame::kGasBase + index)] = open;
    manual_valve_tail(src, core::valve_frame::valve_name(core::valve_frame::kGasBase + index) + " 进气阀", open, frame, "ok", done);
}

void Commands::set_vent(const Source& src, bool open, Done done) {
    if (auto d = admit(src, open ? ActionClass::Control : ActionClass::Stop)) return done(*d);
    auto locked = d_.automation->gas_locked_for_manual();
    if (!locked.ok) return done(locked);
    if (auto g = local_manual_gate(src, "请先开启「手动阀控」")) return done(*g);
    auto frame = core().valves().shown();
    frame[static_cast<size_t>(core::valve_frame::kVent)] = open;
    manual_valve_tail(src, "排空阀", open, frame, "warn", done);
}

// ==================== 泄漏测试 / 程序 / 容积标定 ====================

OpResult Commands::start_leak_test(const Source& src, const std::vector<int>& channels, std::optional<double> test_psi) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    return d_.automation->leak().start(channels, test_psi, src.label());
}

OpResult Commands::abort_leak_test(const Source& src) {
    if (auto d = admit(src, ActionClass::Stop)) return *d;
    return d_.automation->leak().abort(CommandPolicy::strict(src));
}

OpResult Commands::replace_program(const Source& src, int id, const nlohmann::json& steps, std::optional<std::string> name) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    return programs().replace_program(id, steps, std::move(name));
}

OpResult Commands::check_program_run(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    return programs().check_run(id);
}

OpResult Commands::run_program(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    return programs().run(id, started_by_of(src));
}

OpResult Commands::abort_program(const Source& src, int id) {
    if (auto d = admit(src, ActionClass::Stop)) return *d;
    if (auto nf = channel_or(id)) return *nf;
    return programs().abort(id);
}

OpResult Commands::copy_program(const Source& src, int from, const std::vector<int>& to) {
    if (auto d = admit(src, ActionClass::Control)) return *d;
    if (auto nf = channel_or(from)) return *nf;
    return programs().copy_program(from, to, CommandPolicy::strict(src));
}

OpResult Commands::start_calib(const Source& src, int id, bool with_water, double water_ml, bool redo_manifold) {
    if (auto d = admit(src, ActionClass::Control, Exposure::LocalOnly)) return *d;
    return d_.automation->volume_calib().start(id, with_water, water_ml, redo_manifold, src.label());
}

OpResult Commands::abort_calib(const Source& src) {
    if (auto d = admit(src, ActionClass::Stop, Exposure::LocalOnly)) return *d;
    return d_.automation->volume_calib().abort();
}

OpResult Commands::discard_calib(const Source& src) {
    if (auto d = admit(src, ActionClass::Control, Exposure::LocalOnly)) return *d;
    return d_.automation->volume_calib().discard_first();
}

}  // namespace reactor::cmd
