// 程序引擎 · 快照字段。C#: Api/Hmi/SnapshotBuilder.cs（ChannelProgram / RunRecord / ProgramSnap / ProgramsSnap）+ 遥测「程序步」列。
// C# 已有的字段名 / 顺序一个不改；新加的字段（name、stepName、stepNames、phase、status、remainingMin …）一律追加在后面。
#include <algorithm>
#include <cmath>

#include "core/program/program_run_info.hpp"
#include "core/program/step_fields.hpp"
#include "ctl/ctl_program_engine_impl.hpp"
#include "store/store_util.hpp"
#include <map>
#include <memory>
#include <string>

namespace reactor::ctl {

using namespace core::program;

namespace {

ojson round1(double v) { return std::isfinite(v) ? ojson(store::json_num(std::round(v * 10.0) / 10.0)) : ojson(nullptr); }

program_run_set::Runs infos_of(const std::map<int, std::shared_ptr<ProgramEngine::Run>>& runs) {
    program_run_set::Runs m;
    for (const auto& kv : runs)
        if (kv.second->info.is_active()) m.emplace(kv.first, kv.second->info);
    return m;
}

}  // namespace

// 剩余时间估算（新字段）：当前步骤估算时长 − 已用时间（不小于 0）+ 之后各步骤的估算（StepFields 的 DurationEstimate）
ojson ProgramEngine::remaining_json(const Run& r) const {
    if (!r.info.is_active()) return nullptr;
    int i = std::max(0, r.current_index);
    int n = static_cast<int>(r.steps.size());
    DurationEstimate left;
    if (i < n) {
        auto cur = step_duration::of(r.steps[static_cast<size_t>(i)]);
        double el = static_cast<double>(core_.clock().mono_ms() - r.step_started_mono) / 60000.0;
        left.minutes = std::max(0.0, cur.minutes - (r.current_index >= 0 ? el : 0.0));
        left.approx = cur.approx;
        left.upper_bound = cur.upper_bound;
        for (int k = i + 1; k < n; ++k) left = left + step_duration::of(r.steps[static_cast<size_t>(k)]);
    }
    ojson j;
    j["minutes"] = round1(left.minutes);
    j["text"] = left.text();
    j["approx"] = left.approx;
    j["upperBound"] = left.upper_bound;
    return j;
}

ojson ProgramEngine::channel_program_json(const Run& r) const {
    const auto& ri = r.info;
    ojson j;
    // C# ChannelProgramSnap（字段与顺序不改）
    j["running"] = ri.is_active();
    j["stepIndex"] = ri.index;
    j["stepCount"] = ri.step_count;
    j["state"] = program_run_set::phase_name(ri.phase);
    j["message"] = ri.message;
    j["startedBy"] = ri.started_by;
    j["startedAt"] = store::fmt_utc_json(ri.started_at_wall_ms);
    j["waitingGas"] = ri.waiting_gas();
    // ---- 新字段（第二阶段）----
    j["name"] = r.name;
    j["phase"] = ri.phase_text;
    j["status"] = ri.status;
    int i = ri.index;
    j["stepName"] = i >= 0 && i < static_cast<int>(r.steps.size()) ? ojson(r.steps[static_cast<size_t>(i)].name()) : ojson(nullptr);
    ojson names = ojson::array();
    for (const auto& s : r.steps) names.push_back(s.name());
    j["stepNames"] = names;
    j["progress"] = r.progress_text;
    j["elapsedMin"] = round1(static_cast<double>(core_.clock().mono_ms() - r.started_mono) / 60000.0);
    j["remaining"] = remaining_json(r);
    auto w = queue_.waiting();
    auto it = std::find(w.begin(), w.end(), r.rv);
    j["gasQueuePos"] = ri.is_active() && it != w.end() ? ojson(static_cast<int>(it - w.begin()) + 1) : ojson(nullptr);
    j["waitingOn"] = ri.waiting_on ? ojson(*ri.waiting_on) : ojson(nullptr);
    j["regulating"] = ri.is_active() && r.reg.active;
    j["endReason"] = ri.end_reason ? ojson(*ri.end_reason) : ojson(nullptr);
    // 新增（触屏用，PC 忽略）：当前步实际已进行多久。方案里的计划起点和实际可能差很多（排队等气路、升温慢），
    // 触屏「已反应 x min」原来按计划起点算，反应提前开始时一直是 0
    j["stepElapsedMin"] = ri.is_active() ? ojson(round1(static_cast<double>(core_.clock().mono_ms() - r.step_started_mono) / 60000.0))
                                         : ojson(nullptr);
    return j;
}

ojson ProgramEngine::channel_program(int ch) const {
    auto it = runs_.find(ch);
    if (it != runs_.end()) return channel_program_json(*it->second);
    auto l = last_.find(ch);
    return l != last_.end() ? channel_program_json(*l->second) : ojson(nullptr);
}

ojson ProgramEngine::program_run(int ch) const {
    const Run* r = nullptr;
    if (auto it = runs_.find(ch); it != runs_.end()) r = it->second.get();
    else if (auto l = last_.find(ch); l != last_.end()) r = l->second.get();
    if (!r) return nullptr;
    const auto& ri = r->info;
    ojson j;
    // C# ProgramRunSnap（字段与顺序不改）
    j["channel"] = ch;
    j["running"] = ri.is_active();
    j["state"] = program_run_set::phase_name(ri.phase);
    j["stepIndex"] = ri.index;
    j["stepCount"] = ri.step_count;
    j["phase"] = ri.phase_text;
    j["message"] = ri.message;
    j["startedBy"] = ri.started_by;
    j["startedAt"] = store::fmt_utc_json(ri.started_at_wall_ms);
    j["waitingGas"] = ri.waiting_gas();
    j["steps"] = r->steps_json;
    j["endReason"] = ri.end_reason ? ojson(*ri.end_reason) : ojson(nullptr);
    // ---- 新字段 ----
    j["name"] = r->name;
    j["status"] = ri.status;
    ojson states = ojson::array();
    for (size_t k = 0; k < r->recs.size(); ++k)
        states.push_back(ojson{{"index", static_cast<int>(k)}, {"state", r->recs[k].state}, {"message", r->recs[k].message}});
    j["stepStates"] = states;
    j["remaining"] = remaining_json(*r);
    return j;
}

ojson ProgramEngine::legacy_program() const {
    auto infos = infos_of(runs_);
    const ProgramRunInfo* first = program_run_set::first_running(infos);
    if (!first && last_ended_) first = &last_ended_->info;
    if (!first) return ProgramHooks::legacy_program();
    ojson j;
    j["running"] = first->is_active();
    j["channel"] = first->is_active() ? ojson(first->rv) : ojson(nullptr);
    j["status"] = first->status;
    j["stepIndex"] = first->index;
    j["stepCount"] = first->step_count;
    j["phase"] = first->phase_text;
    j["detail"] = first->message;
    return j;
}

ojson ProgramEngine::programs() const {
    auto infos = infos_of(runs_);
    ojson chans = ojson::array();
    for (int id : program_run_set::running_ids(infos)) chans.push_back(id);
    ojson waiting = ojson::array();
    ojson queue = ojson::array();
    for (int rv : queue_.waiting()) {
        queue.push_back(rv);
        auto it = infos.find(rv);
        if (it != infos.end() && it->second.waiting_gas()) waiting.push_back(rv);  // 稳压脉冲的静默排队不算
    }
    const auto& l = core_.gas_lease();
    ojson j;
    j["running"] = static_cast<int>(infos.size());
    j["channels"] = chans;
    j["gasHolder"] = l.owner() == core::GasOwner::Program && l.owner_rv() ? ojson(*l.owner_rv()) : ojson(nullptr);
    j["waitingGas"] = waiting;
    j["summary"] = program_run_set::summary(infos);
    // ---- 新字段 ----
    j["gasQueue"] = queue;  // 全部排队（含稳压脉冲），先后顺序
    std::string pend = gas_pending();
    j["gasPending"] = pend.empty() ? ojson(nullptr) : ojson(pend);
    return j;
}

std::string ProgramEngine::telemetry_step(int ch) const {
    auto r = active(ch);
    if (!r) return "";
    return std::to_string(r->info.index + 1) + "/" + program_run_set::phase_name(r->info.phase);
}

}  // namespace reactor::ctl
