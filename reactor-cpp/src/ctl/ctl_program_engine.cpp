// 程序引擎 · 草稿、下发守卫、执行主流程、中止与收尾。C#: ViewModels/ProgramViewModel.Run.cs（ExecuteAsync / RunStepAsync /
// StepIo / AbortProgram / ReleaseStir / WaitSliceAsync / ThrowIfStepUnsafe）、ProgramRun.cs、Commands/OperatorCommands.Program.cs。
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>

#include "core/num_format.hpp"
#include "core/program/program_json.hpp"
#include "core/program/program_limits.hpp"
#include "core/program/program_run_guard.hpp"
#include "core/program/program_step.hpp"
#include "core/program/program_step_format.hpp"
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


// C# Reactor.IsWorking：真正在干活（升温 / 反应 / 升压 / 超压 / 等待 / 搅拌 / 降温）
bool is_working(ReactorState s) {
    switch (s) {
        case ReactorState::React:
        case ReactorState::Heating:
        case ReactorState::Pressing:
        case ReactorState::Alarm:
        case ReactorState::Wait:
        case ReactorState::Stir:
        case ReactorState::Cooling: return true;
        default: return false;
    }
}

bool valid_ch(int ch) { return ch >= 1 && ch <= 8; }

OpResult channel_not_found(int ch) {
    return OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(ch) + " 不存在（有效范围 1–8）");
}

// 占用共用气路的步骤：气密检查、置换、充压、放空（STEPS-SPEC §0）
bool has_gas_steps(const ProgramEngine::Steps& s) {
    for (const auto& x : s)
        if (x.type() == StepType::Leak || x.type() == StepType::Exchange || x.type() == StepType::Press || x.type() == StepType::Vent)
            return true;
    return false;
}

// 需要压力读数的气路步骤：气密检查、釜置换（次数 > 0）、充压、放空
bool needs_pressure(const ProgramEngine::Steps& s) {
    for (const auto& x : s)
        if (x.type() == StepType::Leak || x.type() == StepType::Press || x.type() == StepType::Vent ||
            (x.type() == StepType::Exchange && x.num("cycles", 3) > 0))
            return true;
    return false;
}

// 重新编号（快照只读：重建）
ProgramEngine::Steps renumber(const ProgramEngine::Steps& in, core::PressureUnit unit) {
    ProgramEngine::Steps out;
    out.reserve(in.size());
    for (size_t i = 0; i < in.size(); ++i) {
        auto ps = ProgramStep::from_snapshot(in[i]);
        ps.set_ordinal(static_cast<int>(i) + 1);
        out.push_back(ps.to_snapshot(unit));
    }
    return out;
}

}  // namespace

// ============================ 构造 ============================

ProgramEngine::ProgramEngine(ControlCore& core, store::SessionBook* sessions, core::PressureUnit unit)
    : core_(core), sessions_(sessions), unit_(unit) {
    // 开机每一路都是空的（没有程序）：用户自己编辑 / 从程序库选了才有。旧版本会预置「加氢筛选」，
    // 现场看到 8 路都「有程序」容易误会（2026-11-27 用户）。模板仍在程序库里（内置模板）。
    gas_listener_ = core_.add_gas_abort_listener(
        [this](core::GasAbortCause cause, std::optional<int> channel) { on_gas_abort(cause, channel); });
}

ProgramEngine::~ProgramEngine() {
    core_.remove_gas_abort_listener(gas_listener_);
    for (auto& kv : runs_) {
        auto& r = kv.second;
        if (r->timer) core_.loop().cancel(r->timer);
        if (r->curve_timer) core_.loop().cancel(r->curve_timer);
        if (r->reg.timer) core_.loop().cancel(r->reg.timer);
        if (r->acq.timer) core_.loop().cancel(r->acq.timer);
        if (r->reg_acq.timer) core_.loop().cancel(r->reg_acq.timer);
    }
}

ProgramLimitSettings ProgramEngine::limits() const {
    const auto& s = core_.settings().settings;
    return ProgramLimitSettings{s.t_max_sp, s.over_temp, s.rpm_max, s.p_max_sp, s.over_pressure};
}

void ProgramEngine::log(const std::string& level, const std::string& title, const std::string& detail, const std::string& kind) {
    core_.alarms().log(level, "系统", title, detail, kind);
}

std::shared_ptr<ProgramEngine::Run> ProgramEngine::active(int ch) const {
    auto it = runs_.find(ch);
    if (it == runs_.end() || !it->second->info.is_active()) return nullptr;
    return it->second;
}

bool ProgramEngine::is_owned(int ch) const { return active(ch) != nullptr; }

int ProgramEngine::running_count() const {
    int n = 0;
    for (const auto& kv : runs_)
        if (kv.second->info.is_active()) ++n;
    return n;
}

std::vector<int> ProgramEngine::running_ids() const {
    std::vector<int> ids;
    for (const auto& kv : runs_)
        if (kv.second->info.is_active()) ids.push_back(kv.first);
    return ids;
}

std::string ProgramEngine::gas_pending() const {
    for (const auto& kv : runs_)
        if (kv.second->info.is_active() && kv.second->reg.active) return core::chan_name(kv.first) + " 程序 · 稳压";
    auto q = queue_.waiting();
    return q.empty() ? std::string() : core::chan_name(q.front()) + " 程序 · 等待气路";
}

std::optional<int> ProgramEngine::stir_step_rv() const {
    for (int rv : running_ids())
        if (stir_.holds(rv)) return rv;
    return std::nullopt;
}

const ProgramRunInfo* ProgramEngine::run_info(int ch) const {
    auto it = runs_.find(ch);
    if (it != runs_.end()) return &it->second->info;
    auto l = last_.find(ch);
    return l != last_.end() ? &l->second->info : nullptr;
}

std::optional<core::GuardReject> ProgramEngine::gas_block_for(int rv, bool need_pressure) const {
    return gas_ops::gas_block(core_, need_pressure, rv, true, running_ids(), gas_pending());
}

std::vector<core::StirHolder> ProgramEngine::stir_holders() const {
    std::vector<core::StirHolder> v;
    for (int rv : stir_.holders()) {
        auto it = stir_rpm_.find(rv);
        v.push_back(core::StirHolder{rv, it != stir_rpm_.end() ? it->second : 0});
    }
    return v;
}

// ============================ 草稿 ============================

std::optional<OpResult> ProgramEngine::edit_locked(int ch) const {
    if (is_owned(ch))
        return OpResult::fail(core::op_codes::kChannelProgramOwned, "程序执行中 · 已锁定为只读", "warn").set_quiet();
    if (is_working(core_.channel(ch).state))
        return OpResult::fail(core::op_codes::kChannelBusy, "该通道正在运行 · 程序已锁定为只读", "warn").set_quiet();
    return std::nullopt;
}

OpResult ProgramEngine::replace_program(int ch, const nlohmann::json& steps, std::optional<std::string> name) {
    if (!valid_ch(ch)) return channel_not_found(ch);
    if (auto locked = edit_locked(ch)) return *locked;
    auto parsed = program_json::parse(steps, core_.settings().settings.leak_rate, unit_);
    if (parsed.error) return OpResult::fail(parsed.error->code, parsed.error->message, "err", parsed.error->detail);
    auto list = renumber(*parsed.steps, unit_);
    auto errors = program_limits::validate(list, limits(), unit_);
    if (!errors.empty()) {
        ojson errs = ojson::array();
        for (const auto& e : errors) errs.push_back(e);
        return OpResult::fail(core::op_codes::kParamOutOfRange,
                              errors.size() == 1 ? errors[0]
                                                 : errors[0] + "（另有 " + std::to_string(errors.size() - 1) + " 项超限）",
                              "err", ojson{{"errors", errs}});
    }
    auto& d = drafts_[idx(ch)];
    d.steps = list;
    if (name) d.name = *name;
    d.migrated = parsed.migrated;
    d.notes = parsed.notes;
    std::vector<std::string> warnings;
    if (parsed.migrated) warnings.push_back("已从旧格式转换，请检查（STEPS-SPEC §3）");
    warnings.insert(warnings.end(), parsed.notes.begin(), parsed.notes.end());
    ojson data{{"rv", ch}, {"steps", program_step_format::to_json(program_step_format::format(list))}, {"migrated", parsed.migrated}};
    return OpResult::done(core::chan_name(ch) + " 程序已更新（" + std::to_string(list.size()) + " 步）" + (parsed.migrated ? " · 已从旧格式转换" : ""),
                          "ok", data)
        .with_warnings(warnings);
}

OpResult ProgramEngine::copy_program(int from, const std::vector<int>& to, bool strict) {
    if (!valid_ch(from)) return channel_not_found(from);
    if (auto locked = edit_locked(from)) return *locked;
    std::vector<int> targets;
    std::vector<std::string> warnings;
    std::vector<int> seen;
    for (int id : to) {
        if (std::find(seen.begin(), seen.end(), id) != seen.end()) continue;
        seen.push_back(id);
        if (id == from) continue;
        if (!valid_ch(id)) return channel_not_found(id);
        if (is_owned(id)) continue;
        auto st = core_.channel(id).state;
        if (strict && (st == ReactorState::Idle || is_working(st))) {
            warnings.push_back(core::chan_name(id) + " " + (st == ReactorState::Idle ? "已停用" : "正在运行") + " · 已跳过");
            continue;
        }
        targets.push_back(id);
    }
    if (targets.empty()) return OpResult::fail(core::op_codes::kBadRequest, "请先选择目标通道", "warn").with_warnings(warnings);
    const auto& src = drafts_[idx(from)];
    for (int id : targets) {
        auto& d = drafts_[idx(id)];
        d.steps = src.steps;
        d.name = src.name;
        d.migrated = src.migrated;
        d.notes = src.notes;
    }
    ojson tos = ojson::array();
    for (int id : targets) tos.push_back(id);
    return OpResult::done("已将 " + core::chan_name(from) + " 的 " + std::to_string(src.steps.size()) + " 步程序应用到 " +
                              std::to_string(targets.size()) + " 个通道",
                          "ok", ojson{{"from", from}, {"to", tos}})
        .with_warnings(warnings);
}

ojson ProgramEngine::draft_json(int ch) const {
    if (!valid_ch(ch)) return nullptr;
    const auto& d = drafts_[idx(ch)];
    ojson j;
    j["rv"] = ch;
    j["name"] = d.name;
    j["steps"] = program_step_format::to_json(program_step_format::format(d.steps));
    j["migrated"] = d.migrated;
    j["locked"] = edit_locked(ch).has_value();
    j["estimate"] = step_duration::total(d.steps).text();
    return j;
}

void ProgramEngine::set_draft(int ch, Steps steps, std::string name) {
    if (!valid_ch(ch)) return;
    auto& d = drafts_[idx(ch)];
    d.steps = std::move(steps);
    d.name = std::move(name);
    d.migrated = false;
    d.notes.clear();
}

// ============================ 下发守卫 / 下发 / 中止 ============================

std::optional<core::GuardReject> ProgramEngine::run_guard(int ch, const Steps& steps) const {
    ProgramRunInput in;
    in.has_channel = valid_ch(ch);
    in.program_running = in.has_channel && is_owned(ch);
    in.running_count = running_count();
    in.step_count = static_cast<int>(steps.size());
    if (in.has_channel) {
        in.state = core_.channel(ch).state;
        in.over_temp_tripped = core_.is_over_temp_tripped(ch);
        in.over_pressure_tripped = core_.is_over_pressure_tripped(ch);
    }
    in.estop_locked = false;
    const auto& lease = core_.gas_lease();
    in.gas_busy = lease.busy() && lease.owner() != core::GasOwner::Program;  // 泄漏测试 / 标定占用才拒绝；另一程序占用由气路步骤排队
    in.gas_task = lease.task();
    in.needs_gas = has_gas_steps(steps);
    if (in.needs_gas && in.has_channel)
        if (auto gb = gas_block_for(ch, needs_pressure(steps))) {
            in.gas_block = gb->message;
            in.gas_block_code = gb->code;
        }
    in.limit_errors = program_limits::pre_run(steps, limits(), unit_).errors;
    return program_run_guard::check(in);
}

OpResult ProgramEngine::run_blocked(const core::GuardReject& why) {
    log("warn", "程序下发被拒绝", why.message, "program.runRejected");
    return OpResult::fail(why.code, "下发被拒绝 · " + why.message);
}

OpResult ProgramEngine::check_run(int ch) {
    static const Steps kEmpty;
    const Steps& s = valid_ch(ch) && !is_owned(ch) ? drafts_[idx(ch)].steps : kEmpty;
    if (auto why = run_guard(ch, s)) return run_blocked(*why);
    return OpResult::none();
}

OpResult ProgramEngine::run(int ch, const std::string& started_by) {
    static const Steps kEmpty;
    Steps snap = valid_ch(ch) ? drafts_[idx(ch)].steps : kEmpty;  // 此刻草稿的冻结副本（C3）
    if (auto why = run_guard(ch, snap)) return run_blocked(*why);
    auto warns = program_limits::pre_run(snap, limits(), unit_).warnings;
    int n = static_cast<int>(snap.size());
    start(ch, std::move(snap), started_by, drafts_[idx(ch)].name);
    return OpResult::accept("", "ok", ojson{{"rv", ch}, {"steps", n}}).with_warnings(warns);
}

OpResult ProgramEngine::abort(int ch) {
    auto r = valid_ch(ch) ? active(ch) : nullptr;
    if (!r) return OpResult::none();
    bool stir = core_.stir().on();
    for (int h : stir_.holders())
        if (h != ch) stir = false;
    request_abort(r, AbortReason::ChannelStop);
    auto res = OpResult::done(core::chan_name(ch) + " 程序已中止 · 正在安全停止", "warn", ojson{{"rv", ch}}).set_quiet();
    if (stir) res.warnings.push_back("中止程序将停止全局搅拌");
    return res;
}

// ============================ 暂停 / 继续 ============================

int64_t ProgramEngine::vnow(const Run& r) const {
    int64_t now = core_.clock().mono_ms();
    return now - r.paused_total_ms - (r.paused ? now - r.paused_at_mono : 0);
}

bool ProgramEngine::is_paused(int ch) const {
    auto r = valid_ch(ch) ? active(ch) : nullptr;
    return r && r->paused;
}

OpResult ProgramEngine::pause(int ch, const std::string& source) {
    if (!valid_ch(ch)) return channel_not_found(ch);
    auto r = active(ch);
    if (!r || r->cancelled || r->finishing)
        return OpResult::fail(core::op_codes::kChannelStateInvalid, core::chan_name(ch) + " 没有正在执行的程序 · 无法暂停", "warn");
    if (r->paused) return OpResult::none();
    int rv = ch;
    auto& c = core_.channel_mut(rv);
    int i = r->current_index;
    r->paused = true;
    r->paused_at_mono = core_.clock().mono_ms();
    ++r->gen;  // 之前排的定时器、取气路 / 吹总管 / 曲线的回调全部作废
    if (r->timer) core_.loop().cancel(r->timer);
    r->timer = 0;

    // 1) 气路：本步正在用气路 / 排队等气路 / 吹总管 / 跑曲线 → 这一段安全中止，继续时从头重做本步
    bool gas = r->runner && r->runner->gas_held();
    bool waiting = r->runner && r->runner->waiting() != Await::None;
    r->restart_on_resume = !r->runner || gas || waiting || r->acq.active || r->curve.has_value();
    acquire_end(r, r->acq);
    // 已有结论的步骤（气密已判定、在放空）：继续时从断点接着走，不从头重测
    r->resume_point.reset();
    if (r->restart_on_resume && r->runner) r->resume_point = r->runner->resume_point();
    if (r->restart_on_resume) {
        r->ops->cancel();
        r->runner.reset();
    }
    if (gas) {
        // 同中止收尾（finish）：等在途的写阀回来 → 全关（含排空）→ 只释放自己这段租约
        int64_t gen = r->gas_gen;
        r->ops->when_idle([this, gen, rv] {
            const auto& lease = core_.gas_lease();
            if (lease.owner() != core::GasOwner::Program || lease.owner_rv() != std::optional<int>(rv) || lease.generation() != gen) return;
            core_.clear_valve_hold();
            core_.close_all_valves([this, gen](bool, const std::string&) { core_.end_gas(core::GasOwner::Program, gen); });
        });
    } else if (core_.valves().sv_open(rv)) {
        core_.close_sv(rv);  // 关本路进气
    }
    // 2) 稳压：停（暂停期间不补气），记下参数，继续时重开
    r->resume_reg.reset();
    if (r->reg.active) r->resume_reg = Run::RegResume{r->reg.gas, r->reg.target, r->reg.refill};
    stop_regulation(r);
    // 3) 加热：停曲线 / 逼近 + 本路 At=4（同手动停止）
    r->curve.reset();
    if (r->curve_timer) core_.loop().cancel(r->curve_timer);
    r->curve_timer = 0;
    stop_heat_for(r, "", nullptr);
    // 4) 搅拌：释放程序的持有（别的程序还在用就保留；操作员手动开的搅拌程序本来就不持有，不碰）
    r->resume_stir_rpm.reset();
    if (stir_.holds(rv)) {
        auto it = stir_rpm_.find(rv);
        r->resume_stir_rpm = it != stir_rpm_.end() ? it->second : core_.stir().rpm();
        release_stir(rv);
    }
    // 5) 状态：干活中的通道显示「等待中」（报警保持报警）；程序记录标「已暂停」
    r->state_before_pause = c.state;
    if (core::state_is_active(c.state)) c.set_state(ReactorState::Wait);
    r->info.paused = true;
    r->info.paused_at_wall_ms = core_.clock().wall_ms();
    r->info.paused_by = source;
    r->info.phase = ProgramRunPhase::Running;
    r->info.waiting_on.reset();
    std::string step_name = i >= 0 && i < static_cast<int>(r->steps.size()) ? r->steps[static_cast<size_t>(i)].name() : std::string();
    r->info.phase_text = "第 " + std::to_string(i + 1) + " 步 · 已暂停";
    set_status(r, "已暂停 · 加热、进气、搅拌已停");
    set_rec(r, i, "running", "已暂停");
    std::string what = std::string("加热已停、进气已关") + (r->resume_reg ? "、稳压已停" : "") + (r->resume_stir_rpm ? "、搅拌已释放" : "");
    bool redo = r->restart_on_resume && !r->resume_point;
    std::string then = r->resume_point ? " · " + r->resume_point->note : redo ? std::string(" · 继续时从头重做本步") : std::string();
    log("warn", core::chan_name(rv) + " 程序暂停",
        "第 " + std::to_string(i + 1) + " 步 " + step_name + " · 来源 " + source + " · " + what + then, "program.paused");
    return OpResult::done(core::chan_name(rv) + " 程序已暂停 · " + what, "warn",
                          ojson{{"rv", rv}, {"paused", true}, {"restartStep", redo}});
}

OpResult ProgramEngine::resume(int ch, const std::string& source) {
    if (!valid_ch(ch)) return channel_not_found(ch);
    auto r = active(ch);
    if (!r || r->cancelled || r->finishing)
        return OpResult::fail(core::op_codes::kChannelStateInvalid, core::chan_name(ch) + " 没有正在执行的程序 · 无法继续", "warn");
    if (!r->paused) return OpResult::none();
    int rv = ch;
    auto& c = core_.channel_mut(rv);
    // 报警（超温 / 超压联锁未解除）时不能重新加热 / 进气
    if (c.state == ReactorState::Alarm || core_.is_over_temp_tripped(rv) || core_.is_over_pressure_tripped(rv))
        return OpResult::fail(core::op_codes::kChannelAlarm, core::chan_name(rv) + " 报警未解除 · 不能继续程序", "warn");
    int64_t now = core_.clock().mono_ms();
    int64_t dur = now - r->paused_at_mono;
    r->paused_total_ms += dur;
    r->paused = false;
    ++r->gen;
    r->info.paused = false;
    r->info.paused_at_wall_ms.reset();
    int i = std::max(0, r->current_index);
    bool restart = r->restart_on_resume || !r->runner;
    r->restart_on_resume = false;
    std::optional<core::program::StepResumePoint> from = restart ? r->resume_point : std::nullopt;
    r->resume_point.reset();
    bool redo = restart && !from;
    // 1) 状态恢复（重做的步骤会自己再设）
    if (c.state == ReactorState::Wait && r->state_before_pause != ReactorState::Alarm) c.set_state(r->state_before_pause);
    // 2) 加热：按暂停前的目标重开（重做的是升温步骤时由步骤自己开）
    bool step_heats = restart && r->steps[static_cast<size_t>(i)].type() == StepType::Heat;
    if (r->heat_on && !step_heats) {
        core_.set_sp_mode(rv, "fixed");
        core_.start_fixed_heat(rv, r->heat_target);
        core_.begin_heat_watch(rv, r->heat_target, r->heat_band);
    }
    // 3) 搅拌：重新请求暂停前的转速
    if (r->resume_stir_rpm) {
        int rpm = *r->resume_stir_rpm;
        stir_request(rv, rpm);
        if (!core_.stir().on()) core_.stir_start(rpm);
    }
    r->resume_stir_rpm.reset();
    // 4) 稳压：按原参数重开
    if (r->resume_reg) start_regulation(r, r->resume_reg->gas, r->resume_reg->target, r->resume_reg->refill);
    r->resume_reg.reset();
    std::string step_name = r->steps[static_cast<size_t>(i)].name();
    // 本步没作废、接着走：步骤自己可能要调整（气密静置 / 保压中被暂停 → 重新静置并重取 P0）
    std::optional<std::string> adjust;
    if (!restart) adjust = r->runner->on_resume();
    std::string how = from ? " · " + from->note : redo ? std::string(" · 本步从头重做") : adjust ? " · " + *adjust : std::string();
    log("info", core::chan_name(rv) + " 程序继续",
        "第 " + std::to_string(i + 1) + " 步 " + step_name + " · 来源 " + source + " · 暂停 " +
            fmt_dec(static_cast<double>(dur) / 60000.0, 1) + " 分" + how,
        "program.resumed");
    // 5) 步骤：重做 / 从断点开始 / 从停下的地方接着走（马上 tick 一次：没到点的定时等待按剩余时间继续，等条件的重新判断）
    if (restart) {
        begin_step(r, i, from);
    } else {
        set_rec(r, i, "running", "正在执行");
        r->info.phase_text = "执行第 " + std::to_string(i + 1) + " 步 · " + step_name;
        set_status(r, "继续执行 · " + step_name);
        schedule(r, vnow(*r));
    }
    std::string msg = core::chan_name(rv) + " 程序继续执行";
    if (from) msg += " · 气密结果保留，只补做放空";
    else if (redo) msg += " · 第 " + std::to_string(i + 1) + " 步从头重做";
    else if (adjust) msg += " · 重新静置后再保压";
    return OpResult::done(msg, "ok", ojson{{"rv", rv}, {"paused", false}, {"restartStep", redo}});
}

int ProgramEngine::abort_all(AbortReason why) {
    int n = 0;
    for (int rv : running_ids()) {
        auto r = active(rv);
        if (!r) continue;
        if (why == AbortReason::StopAll || why == AbortReason::Shutdown) r->skip_outputs = true;
        if (!r->cancelled && !r->finishing) ++n;
        request_abort(r, why);
    }
    return n;
}

bool ProgramEngine::abort_channel(int ch, AbortReason why) {
    auto r = active(ch);
    if (!r) return false;
    request_abort(r, why);
    return true;
}

void ProgramEngine::clear_stir_share() {
    stir_.clear();
    stir_rpm_.clear();
}

void ProgramEngine::on_tick(int64_t) {}

// ============================ 执行 ============================

void ProgramEngine::start(int ch, Steps steps, const std::string& started_by, const std::string& name) {
    auto r = std::make_shared<Run>(ch, std::move(steps), started_by, core_.clock().wall_ms());
    r->name = name;
    r->steps_json = program_step_format::to_json(program_step_format::format(r->steps));
    r->progress_text = "0 / " + std::to_string(r->steps.size());
    r->info.message = core::chan_name(ch) + " 程序正在准备启动";
    r->started_mono = core_.clock().mono_ms();
    r->ops = std::make_unique<GasOps>(core_, unit_);
    r->ops->set_fault([this, r](const std::string& why) { step_fault(r, why); });
    r->ops->set_owner(core::GasOwner::Program, ch);
    r->reg.ops = std::make_unique<GasOps>(core_, unit_);
    r->reg.ops->set_owner(core::GasOwner::Program, ch);
    runs_[ch] = r;
    core_.reset_uptake(ch);  // 新程序：吸气量回到「—」，充压完成后重新开始计量
    r->info.phase = ProgramRunPhase::Running;
    std::optional<std::string> session = sessions_ ? sessions_->current_id() : std::nullopt;
    log("info", core::chan_name(ch) + " 程序下发",
        "共 " + std::to_string(r->steps.size()) + " 步，开始执行 · 来源 " + started_by + (session ? " · 会话 " + *session : ""),
        "program.started");
    if (session) sessions_->tag_program_run(ch, core_.clock().wall_ms(), started_by);
    for (const auto& w : program_limits::pre_run(r->steps, limits(), unit_).warnings) log("warn", core::chan_name(ch) + " 程序提示", w, "program.hint");
    next_step(r);
}

void ProgramEngine::set_status(const std::shared_ptr<Run>& r, const std::string& text) {
    r->info.status = text;
    bool blank = std::all_of(text.begin(), text.end(), [](char c) { return c == ' ' || c == '\t'; });
    if (!blank) r->info.message = text;  // C# ProgramRun.OnStatusChanged
}

void ProgramEngine::set_rec(const std::shared_ptr<Run>& r, int i, const std::string& state, const std::string& message) {
    if (i < 0 || i >= static_cast<int>(r->recs.size())) return;
    auto& rec = r->recs[static_cast<size_t>(i)];
    if (rec.state == state && rec.message == message) return;
    rec = StepRec{state, message};
    if (on_step) on_step(r->rv, i, state, message);
}

void ProgramEngine::mark_waiting(const std::shared_ptr<Run>& r, const std::optional<std::string>& reason) {
    int i = r->current_index;
    if (i < 0 || i >= static_cast<int>(r->recs.size())) return;
    if (reason) {
        r->info.phase = ProgramRunPhase::WaitingGas;
        set_rec(r, i, "running", "等待气路（" + *reason + "）");
        r->info.phase_text = "第 " + std::to_string(i + 1) + " 步 · 等待气路";
    } else {
        r->info.phase = ProgramRunPhase::Running;
        r->info.waiting_on.reset();
        set_rec(r, i, "running", "正在执行");
        r->info.phase_text = "执行第 " + std::to_string(i + 1) + " 步 · " + r->steps[static_cast<size_t>(i)].name();
    }
}

void ProgramEngine::next_step(const std::shared_ptr<Run>& r) {
    if (r->cancelled || r->finishing) return;
    r->runner.reset();
    int i = r->current_index + 1;
    int n = static_cast<int>(r->steps.size());
    if (i >= n) {
        finish(r);
        return;
    }
    // 每步开始前查取消（中止后不能再进入下一步：下一步若是升温会同步重新打开加热）——取消由 request_abort 直接收尾
    begin_step(r, i);
}

// 开始（或暂停后从头重做 / 从断点接着走）第 i 步
void ProgramEngine::begin_step(const std::shared_ptr<Run>& r, int i, std::optional<core::program::StepResumePoint> from) {
    int n = static_cast<int>(r->steps.size());
    r->runner.reset();
    r->current_index = i;
    r->info.index = i;
    set_rec(r, i, "running", "正在执行");
    r->progress_text = std::to_string(i) + " / " + std::to_string(n);
    const auto& s = r->steps[static_cast<size_t>(i)];
    r->info.phase_text = "执行第 " + std::to_string(i + 1) + " 步 · " + s.name();
    if (!from) r->step_started_mono = vnow(*r);  // 从断点接着走：本步计时不清零
    // 执行前按当前全局限值再校验一次（C4：设置可能在下发后被调低）
    if (auto bad = !from ? program_limits::check_step(s, limits(), unit_) : std::nullopt) {
        step_fault(r, "第 " + fmt_00(s.ordinal()) + " 步 " + s.name() + " 参数超出全局限值：" + *bad);
        return;
    }
    std::string head = fmt_00(s.ordinal()) + "/" + fmt_00(n) + " " + s.name();
    r->runner = std::make_unique<StepRunner>(s, head, RunnerConfig{r->rv, unit_});
    if (from) r->runner->start_at(*from);
    tick(r, StepEvent::None, std::nullopt, false);
}

void ProgramEngine::schedule(const std::shared_ptr<Run>& r, int64_t at_ms) {
    if (r->timer) core_.loop().cancel(r->timer);
    uint64_t g = r->gen;
    int64_t delay = std::max<int64_t>(0, at_ms - vnow(*r));  // at_ms 是步骤的虚拟时刻（暂停时间不算）
    r->timer = core_.loop().after(delay, [this, r, g] {
        r->timer = 0;
        if (g != r->gen) return;
        wake(r, StepEvent::None);
    });
}

// 定时等待到点：先等在途的写阀回来，再做安全检查（C# WaitSliceAsync / HoldAsync），需要时即时读压，再 tick
void ProgramEngine::wake(const std::shared_ptr<Run>& r, StepEvent ev) {
    if (r->cancelled || r->finishing || r->paused || !r->runner) return;
    uint64_t g = r->gen;
    if (r->ops->in_flight() > 0) {
        r->ops->when_idle([this, r, g, ev] {
            if (g == r->gen) wake(r, ev);
        });
        return;
    }
    const auto& c = core_.channel(r->rv);
    if (r->runner->gas_held()) {
        // 持有气路：C# HoldAsync 每秒 ThrowIfGasUnsafe
        if (auto why = gas_ops::gas_unsafe(core_, unit_)) {
            step_fault(r, *why);
            return;
        }
    } else {
        StepType t = r->runner->step().type();
        if (t == StepType::Leak || t == StepType::Heat || t == StepType::Hold || t == StepType::React) {
            // C# ThrowIfStepUnsafe：本通道报警 → 失败；被外部置停（全部停止 / 停用 / 抽屉停止）→ 手动中止
            if (c.state == ReactorState::Alarm) { step_fault(r, core::chan_name(r->rv) + " 超温 / 超压报警"); return; }
            if (core::state_is_ended(c.state) || c.state == ReactorState::Idle) { cancel_flow(r, true); return; }
        }
    }
    if (!r->want_fresh) {
        tick(r, ev, std::nullopt, false);
        return;
    }
    // C# ReadPressureAsync：立刻读一次 8AI（失败再读一次；仍失败按读不到交给步骤 → 「RVn 压力读取失败」）
    int rv = r->rv;
    core_.pressure_mut().read_now([this, r, g, ev, rv](bool ok) {
        if (g != r->gen) return;
        if (ok) { tick(r, ev, core_.pressure().last().psi[static_cast<size_t>(rv - 1)], true); return; }
        core_.pressure_mut().read_now([this, r, g, ev, rv](bool ok2) {
            if (g != r->gen) return;
            tick(r, ev, ok2 ? std::optional<double>(core_.pressure().last().psi[static_cast<size_t>(rv - 1)]) : std::nullopt, true);
        });
    });
}

StepInput ProgramEngine::input(const std::shared_ptr<Run>& r, StepEvent ev, std::optional<double> p, bool fresh) const {
    const auto& c = core_.channel(r->rv);
    StepInput in;
    in.now_ms = vnow(*r);  // 暂停的时间不计入步骤计时
    in.limits = limits();
    if (c.t_online) in.temp_c = c.t;
    if (fresh) in.pressure_psi = p;
    else if (c.p_online) in.pressure_psi = gas_ops::pressure_of(core_, r->rv).value_or(c.p);
    in.regulation_active = r->reg.active;
    in.uptake_counting = c.uptake.counting();
    in.uptake_valid = c.uptake.valid();
    in.uptake_mmol = c.uptake.mmol();
    in.gas_rate = c.uptake.rate_mmol_per_min();
    if (auto b = gas_block_for(r->rv, true)) in.gas_block_vessel = b->message;
    if (auto b = gas_block_for(r->rv, false)) in.gas_block_line = b->message;
    if (auto b = gas_ops::heat_block(core_, r->rv, unit_)) in.heat_block = b->message;
    in.approach_text = c.approach.status_text();
    in.stir_rpm = core_.stir().rpm();
    in.stir_on = core_.stir().on();
    in.stir_holders = stir_holders();
    in.end_reason = r->info.end_reason;
    in.event = ev;
    return in;
}

void ProgramEngine::tick(const std::shared_ptr<Run>& r, StepEvent ev, std::optional<double> fresh_p, bool fresh_read) {
    if (r->cancelled || r->finishing || r->paused || !r->runner) return;
    auto out = r->runner->tick(input(r, ev, fresh_p, fresh_read));
    uint64_t g = r->gen;
    for (const auto& a : out.actions) {
        act(r, a);
        if (g != r->gen || r->finishing) return;  // 动作里出了错（写阀被拒等）已经收尾
    }
    int i = r->current_index;
    switch (out.status) {
        case StepStatus::Done:
            set_rec(r, i, "completed", "已完成");
            r->progress_text = std::to_string(i + 1) + " / " + std::to_string(r->steps.size());
            next_step(r);
            return;
        case StepStatus::Fault:
            step_fault(r, out.fault);
            return;
        case StepStatus::Running:
            if (out.waiting == Await::None) {
                r->want_fresh = out.fresh_pressure;
                schedule(r, out.wake_at_ms);
            }
            return;  // 等外部事件：取气路 / 吹总管 / 曲线已在动作里发起，完成时带事件再 tick
    }
}

void ProgramEngine::act(const std::shared_ptr<Run>& r, const Action& a) {
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    switch (a.kind) {
        case ActionKind::Status: set_status(r, a.text); break;
        case ActionKind::Log: log(a.level, a.text, a.detail, "program.step"); break;
        case ActionKind::SetState: c.set_state(a.state); break;
        case ActionKind::SetPressureSp: c.psp = a.value; break;
        case ActionKind::StopRegulation: stop_regulation(r); break;
        case ActionKind::StartRegulation: start_regulation(r, a.gas, a.value, a.value2); break;
        case ActionKind::StartUptake: core_.start_uptake(rv, a.text); break;
        case ActionKind::StopUptake: core_.stop_uptake(rv, a.text); break;
        case ActionKind::RequestGas: {
            uint64_t g = r->gen;
            acquire(
                r, r->acq, a.text, a.flag, false,
                [this, r, g](int64_t) {
                    if (g == r->gen) tick(r, StepEvent::GasGranted, std::nullopt, false);
                },
                [this, r, g](const std::string& why) {
                    if (g == r->gen) step_fault(r, why);
                },
                [this, r, g] {
                    if (g == r->gen) cancel_flow(r, true);
                });
            break;
        }
        case ActionKind::ReleaseGas: release_gas(r); break;
        case ActionKind::Valves: {
            core::ValveFrame f = core::valve_frame::all_closed();
            if (a.valve == ValveCmd::GasToVessel) f = core::valve_frame::gas(a.gas, {rv});
            else if (a.valve == ValveCmd::VentVessel) f = core::valve_frame::vent_only({rv});
            r->ops->apply(f, nullptr, gas_ops::kProgramDeadMs);
            break;
        }
        case ActionKind::LineFlush: line_flush(r, a); break;
        case ActionKind::StartFixedHeat: {
            // C# StartFixedHeatFor：停掉曲线 / 逼近后全链启动（SP 走防超调逼近，由控制周期推进）
            r->curve.reset();
            if (c.profile.running()) c.profile.stop();
            c.approach.stop();
            core_.set_sp_mode(rv, "fixed");
            core_.start_fixed_heat(rv, a.value);
            core_.begin_heat_watch(rv, a.value, r->runner ? r->runner->step().num("band", 2) : 2.0);
            if (r->cancelled) r->heat_touched_after_abort = true;
            r->heat_on = true;
            r->heat_target = a.value;
            r->heat_band = r->runner ? r->runner->step().num("band", 2) : 2.0;
            break;
        }
        case ActionKind::RunCurve: {
            r->heat_on = true;
            r->heat_target = a.segments.empty() ? c.tsp : a.segments.back().temp;  // 曲线跑完后停在终温
            r->heat_band = r->runner ? r->runner->step().num("band", 2) : 2.0;
            run_curve(r, a);
            break;
        }
        case ActionKind::StopHeat:
            r->heat_on = false;
            r->curve.reset();
            stop_heat_for(r, a.text, nullptr);
            break;
        case ActionKind::StirRequest: stir_request(rv, a.count); break;
        case ActionKind::StirSetOn:
            if (a.flag) {
                if (!core_.stir().on()) core_.stir_start(core_.stir().rpm());
            } else if (core_.stir().on()) {
                core_.stir_stop();
            }
            break;
        case ActionKind::StirRelease: release_stir(rv); break;
        case ActionKind::CloseOwnValve:
            if (core_.valves().sv_open(rv)) core_.close_sv(rv);
            break;
        case ActionKind::SetEndReason: r->info.end_reason = a.text; break;
    }
}

void ProgramEngine::stir_request(int rv, int rpm) {
    stir_.request(rv, rpm);
    stir_rpm_[rv] = rpm;
    core_.stir_set_rpm(rpm);  // C# WriteProgramStir：全局转速写入即下发（开着时）
}

// C# ReleaseStir：仍有其他程序持有就保留搅拌；没有了才真正停。本程序改写过全局转速的，释放时恢复为剩余最近登记者的请求值并记事件
void ProgramEngine::release_stir(int rv) {
    int before = core_.stir().rpm();
    bool others = stir_.release(rv);
    stir_rpm_.erase(rv);
    if (!others) {
        if (core_.stir().on()) core_.stir_stop();
        return;
    }
    if (auto cur = stir_.current()) {
        int want = stir_rpm_.count(cur->rv) ? stir_rpm_[cur->rv] : cur->rpm;
        if (want != before) {
            log("warn", "搅拌转速恢复",
                "搅拌转速由 " + core::chan_name(rv) + " 程序 " + std::to_string(before) + " rpm 恢复为 " + std::to_string(want) + " rpm（" +
                    core::chan_name(cur->rv) + " 程序）",
                "program.stirRestored");
            core_.stir_set_rpm(want);
        }
    }
}

void ProgramEngine::stop_heat_for(const std::shared_ptr<Run>& r, const std::string& ok_msg, std::function<void(bool)> then) {
    int rv = r->rv;
    core_.stop_heat(rv, [this, rv, ok_msg, then](bool ok) {
        // C# StopHeatAsync(id, okMsg)：成功提示 okMsg（warn），失败核心已记「停止加热未确认」报警
        if (ok) {
            if (!ok_msg.empty()) core_.emit_notice("warn", ok_msg);
        } else {
            core_.emit_notice("err", core::chan_name(rv) + " 停止加热未确认（温控通讯失败）· 请点主页的「全部停止」或到设备现场确认");
        }
        if (then) then(ok);
    });
}

void ProgramEngine::release_gas(const std::shared_ptr<Run>& r, std::function<void()> then) {
    int64_t gen = r->gas_gen;
    // 等在途的写阀都回来，再全关并只释放自己这段租约（C# StepGas.DisposeAsync：CloseAllValves → EndGas(Program, gen)）
    r->ops->when_idle([this, gen, then] {
        const auto& lease = core_.gas_lease();
        if (lease.owner() == core::GasOwner::Program && (gen == 0 || lease.generation() == gen)) {
            core_.clear_valve_hold();
            core_.close_all_valves();
            core_.end_gas(core::GasOwner::Program, gen);
        }
        if (then) then();
    });
}

// ============================ 失败 / 中止 / 收尾 ============================

void ProgramEngine::step_fault(const std::shared_ptr<Run>& r, const std::string& why) {
    if (r->finishing) return;
    int i = r->current_index;
    if (i >= 0 && i < static_cast<int>(r->recs.size())) {
        set_rec(r, i, "failed", why);
        r->info.phase_text = "第 " + std::to_string(i + 1) + " 步执行失败";
    }
    if (!r->fail) r->fail = why;
    finish(r);
}

// 流程被取消（中止按钮 / 外部停止 / 稳压故障 / 超压联锁）：C# catch (OperationCanceledException) 的归类
void ProgramEngine::cancel_flow(const std::shared_ptr<Run>& r, bool) {
    if (r->finishing) return;
    int i = r->current_index;
    if (i >= 0 && i < static_cast<int>(r->recs.size())) {
        set_rec(r, i, "aborted", "已中止");
        r->info.phase_text = "程序已手动中止";
    }
    if (r->pending_fault) r->fail = r->pending_fault;
    else r->aborted = true;
    finish(r);
}

// C# AbortProgram：先同步停曲线 / 逼近、释放搅拌、立即下发停加热，再取消流程与它自己的气路占用，然后收尾
void ProgramEngine::request_abort(const std::shared_ptr<Run>& r, AbortReason why) {
    if (r->cancelled || r->finishing) return;
    r->cancelled = true;
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    r->curve.reset();
    if (c.profile.running()) c.profile.stop();
    c.approach.stop();
    if (stir_.holds(rv)) release_stir(rv);
    if (!r->skip_outputs && why != AbortReason::StopAll && why != AbortReason::Shutdown) {
        r->abort_stop_sent = true;
        stop_heat_for(r, core::chan_name(rv) + " 程序已立即停止 · 加热输出已关闭", [r](bool) {
            r->abort_stop_done = true;
            auto w = std::move(r->abort_stop_waiters);
            r->abort_stop_waiters.clear();
            for (auto& f : w) f();
        });
    }
    int i = r->current_index;
    r->info.message = "正在停止加热、搅拌并完成设备收尾…";
    // C# 先标「停止指令已下发」，流程收到取消后 MarkStepAborted「已中止」；这里两步合在一起
    if (i >= 0 && i < static_cast<int>(r->recs.size())) set_rec(r, i, "aborted", "已中止");
    r->info.phase_text = "程序已手动中止";
    if (r->pending_fault) r->fail = r->pending_fault;
    else r->aborted = true;
    finish(r);
}

// C# ExecuteAsync 的 finally（顺序照抄）：停稳压 → 只收拾自己的气路占用（全关阀 → 释放）→ 停曲线 / 逼近 → 停加热 → 关 SV →
// 释放搅拌 → 停计量 → 通道转已结束 → 运行登记移到「最近一次」→ 记事件。不自动放空
void ProgramEngine::finish(const std::shared_ptr<Run>& r) {
    if (r->finishing) return;
    r->finishing = true;
    ++r->gen;
    if (r->timer) core_.loop().cancel(r->timer);
    r->timer = 0;
    if (r->runner) r->runner->abort();  // 持有气路的 ReleaseGas 由下面统一收拾
    acquire_end(r, r->acq);
    r->info.phase = ProgramRunPhase::Finishing;
    stop_regulation(r);
    r->ops->cancel();
    // 等两套写阀都回来，再看气路是否还归自己这段租约
    r->ops->when_idle([this, r] {
        r->reg.ops->when_idle([this, r] {
            const auto& lease = core_.gas_lease();
            bool mine = lease.owner() == core::GasOwner::Program && lease.owner_rv() == r->rv && r->gas_gen != 0 &&
                        lease.generation() == r->gas_gen;
            if (!mine) {
                finish_outputs(r);
                return;
            }
            core_.clear_valve_hold();
            int64_t gen = r->gas_gen;
            core_.close_all_valves([this, r, gen](bool, const std::string&) {
                core_.end_gas(core::GasOwner::Program, gen);
                finish_outputs(r);
            });
        });
    });
}

void ProgramEngine::finish_outputs(const std::shared_ptr<Run>& r) {
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    r->curve.reset();
    if (r->curve_timer) core_.loop().cancel(r->curve_timer);
    r->curve_timer = 0;
    if (c.profile.running()) c.profile.stop();
    c.approach.stop();
    if (r->skip_outputs) {  // 全部停止 / 退出：加热与阀门已由快速路径停掉（同 C# 急停时跳过）
        finish_tail(r);
        return;
    }
    auto close_sv = [this, r, rv] {
        if (!core_.valves().sv_open(rv)) { finish_tail(r); return; }
        core_.close_sv(rv, [this, r, rv](bool ok, const std::string&) {
            if (!ok) core_.emit_notice("err", "SV" + std::to_string(rv) + " 关闭失败（IO16R 通讯）· 阀门可能仍开启");
            finish_tail(r);
        });
    };
    std::string msg = core::chan_name(rv) + " 程序收尾 · 加热输出已停止";
    if (r->abort_stop_sent) {
        // 中止时已发出的停加热：之后没再碰过加热输出就复用，否则重新下发一次
        auto after = [this, r, msg, close_sv] {
            if (r->heat_touched_after_abort) stop_heat_for(r, msg, [close_sv](bool) { close_sv(); });
            else close_sv();
        };
        if (r->abort_stop_done) after();
        else r->abort_stop_waiters.push_back(after);
        return;
    }
    stop_heat_for(r, msg, [close_sv](bool) { close_sv(); });
}

void ProgramEngine::finish_tail(const std::shared_ptr<Run>& r) {
    int rv = r->rv;
    auto& c = core_.channel_mut(rv);
    if (stir_.holds(rv)) release_stir(rv);  // 搅拌转速保持到程序结束；中止时已释放，这里兜底
    core_.stop_uptake(rv, r->fail ? "程序中止" : r->aborted ? "程序手动停止" : "程序结束");
    // 正常完成 / 失败 → 已结束；用户中止（停止、全部停止、中止程序）→ 手动停止（决定记录 2026-11-27）
    if (c.state != ReactorState::Idle) c.set_state(r->aborted && !r->fail ? ReactorState::Stopped : ReactorState::Done);
    c.rpm_sp = 0;
    int n = static_cast<int>(r->steps.size());
    if (r->paused) {  // 暂停中被中止 / 停止：计时停在暂停那一刻，记录不再标「已暂停」
        r->paused_total_ms += core_.clock().mono_ms() - r->paused_at_mono;
        r->paused = false;
    }
    r->info.paused = false;
    r->info.paused_at_wall_ms.reset();
    r->info.status = "";
    r->info.index = r->current_index;
    std::string er = r->info.end_reason ? " · " + *r->info.end_reason : std::string();
    r->info.phase_text = r->fail ? "程序执行失败" : r->aborted ? "程序已手动中止" : "程序执行完成";
    r->info.message = r->fail ? *r->fail : r->aborted ? "已停止当前程序，设备已完成安全收尾" : std::to_string(n) + " 步全部完成" + er;
    r->info.phase = r->fail ? ProgramRunPhase::Failed : r->aborted ? ProgramRunPhase::Aborted : ProgramRunPhase::Completed;
    auto it = runs_.find(rv);
    if (it != runs_.end() && it->second == r) runs_.erase(it);
    last_[rv] = r;
    last_ended_ = r;
    if (r->fail) {
        core_.emit_notice("err", core::chan_name(rv) + " 程序已中止 · " + *r->fail);
        log("err", core::chan_name(rv) + " 程序中止", *r->fail, "program.failed");
    } else if (r->aborted) {
        core_.emit_notice("warn", core::chan_name(rv) + " 程序已手动停止 · 加热已停，阀门全关");
        log("warn", core::chan_name(rv) + " 程序手动停止", "加热已停，阀门全关", "program.aborted");
    } else {
        core_.emit_notice("ok", core::chan_name(rv) + " 程序完成 · " + std::to_string(n) + " 步全部执行" + er);
        log("info", core::chan_name(rv) + " 程序完成", std::to_string(n) + " 步全部执行" + er, "program.completed");
    }
    if (on_finished) on_finished(rv, r->info.phase);
}

}  // namespace reactor::ctl
