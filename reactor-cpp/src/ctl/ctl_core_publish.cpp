// 控制核心 · 每周期末的输出：不可变快照（C# Api/Hmi/SnapshotBuilder.cs）+ 遥测行（C# MainViewModel.BuildTelemetryRow）。
// 读取规则同 C#：温控 / 压力模块离线时读数为空；压力取最近一帧（原码 + psi 同属一次读取）。
#include <cmath>
#include <algorithm>
#include <memory>

#include "ctl/ctl_core.hpp"
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::ctl {

using core::ReactorState;

namespace {

std::optional<double> fin(double v) { return std::isfinite(v) ? std::optional<double>(v) : std::nullopt; }

const char* approach_phase_code(core::ApproachPhase p) {
    switch (p) {
        case core::ApproachPhase::Settle: return "settle";
        case core::ApproachPhase::Creep: return "creep";
        case core::ApproachPhase::Hold: return "hold";
        default: return "off";
    }
}

std::string pascal(const char* code) {
    std::string s = code;
    if (!s.empty() && s[0] >= 'a' && s[0] <= 'z') s[0] = static_cast<char>(s[0] - 'a' + 'A');
    return s;
}

ModuleCommSnap module(bool configured, bool online, const hw::CommStats& s, std::optional<int64_t> values_at = std::nullopt) {
    ModuleCommSnap m;
    m.configured = configured;
    m.online = online;
    m.polls = s.polls();
    m.fails = s.fails();
    if (s.last_ok_ms() != 0) m.last_ok_at_ms = s.last_ok_ms();
    m.values_at_ms = values_at;
    return m;
}

store::TelemetryModule tmodule(bool configured, bool online, const hw::CommStats& s) {
    store::TelemetryModule m;
    m.configured = configured;
    m.online = online;
    m.polls = s.polls();
    m.fails = s.fails();
    if (s.last_ok_ms() != 0) m.last_ok_at = store::WallTime::local(s.last_ok_ms());
    return m;
}

}  // namespace

void ControlCore::publish_snapshot() {
    auto s = std::make_shared<Snapshot>();
    s->seq = ++snap_seq_;
    s->ts_ms = clock_->wall_ms();
    s->simulated = hw_->simulated;
    s->sim_refused = hw_->sim_refused;
    s->event_seq_applied = logger_ && logger_->events() ? logger_->events()->head() : 0;
    s->mode = mode_->mode();
    bool real_t = temp_.online();
    bool real_p = press_.online();
    const auto& ps = press_.last();
    bool p_valid = real_p && ps.valid();
    s->comm = CommSnap{real_t, real_p, stir_.online(), valves_.online()};
    s->installed = opt_.installed;
    const auto& shown = valves_.shown();
    for (size_t i = 0; i < 8; ++i) {
        const auto& c = ch_[i];
        const auto& tc = temp_.ch(c.id);
        bool t_online = real_t && tc.online;
        auto& o = s->channels[i];
        o.id = c.id;
        o.installed = std::find(opt_.installed.begin(), opt_.installed.end(), c.id) != opt_.installed.end();
        o.state = core::state_code(c.state);
        if (c.state == ReactorState::Alarm) o.alarm_cause = c.over_temp ? "overTemp" : "overPressure";
        o.over_temp_tripped = ot_guard_.is_tripped(static_cast<int>(i));
        o.over_pressure_tripped = op_guard_.is_tripped(static_cast<int>(i));
        if (c.state != ReactorState::Idle) o.started_by = c.started_by;
        if (t_online) o.temp_c = fin(tc.pv);
        o.temp_sp_c = c.tsp;
        if (tc.sp_known) o.temp_sp_device_c = fin(tc.sp);
        if (t_online) o.heat_output_pct = fin(tc.output_pct);
        o.temp_online = t_online;
        o.autotuning = tc.autotuning;
        if (t_online && tc.mode >= 0) o.at_mode = tc.mode;
        o.sp_mode = c.sp_mode;
        const auto& a = c.approach;
        o.approach.phase = approach_phase_code(a.phase());
        if (a.active()) {
            o.approach.target_c = fin(a.target());
            o.approach.current_sp_c = fin(a.current_sp());
        }
        o.approach.status_text = a.status_text();
        const auto& pr = c.profile;
        int64_t now = clock_->mono_ms();
        o.profile.running = pr.running();
        o.profile.seg_count = pr.seg_count;
        for (int k = 1; k <= core::temp_profile::kMaxSegments; ++k) {
            const auto& seg = pr.segments[static_cast<size_t>(k - 1)];
            o.profile.segments.push_back(ProfileSegmentSnap{k, seg.temp, seg.minutes, pr.enabled(k), pr.active_segment() == k});
        }
        o.profile.elapsed_min = std::round(pr.elapsed_min(now) * 100.0) / 100.0;
        o.profile.total_minutes = pr.total_minutes();
        o.profile.complete = pr.running() && pr.is_complete(now);
        o.profile.status_text = pr.status_text();
        if (p_valid) {
            o.pressure_psi = fin(ps.psi[i]);
            o.pressure_raw = ps.raw[i];
        }
        o.pressure_sp_psi = c.psp;
        if (c.uptake.valid()) o.gas_consumed = fin(c.uptake.mmol());
        o.valve_open = shown[i];
        o.program_owned = programs_->is_owned(c.id);
        o.has_activity = channel_has_activity(c.id);
        o.program = programs_->channel_program(c.id);
        if (auto r = c.uptake.rate_mmol_per_min()) o.gas_rate = fin(*r);
        o.solution_volume = c.vol;
        s->live_band[i] = temp_.live_band(c.id);
        s->program_runs[i] = programs_->program_run(c.id);
    }
    s->stir.on = stir_.on();
    s->stir.rpm_sp = stir_.rpm();
    s->stir.online = stir_.online();
    if (stir_.motor_current_a() > 0) s->stir.motor_current_a = stir_.motor_current_a();
    if (stir_.bus_voltage_v() > 0) s->stir.bus_voltage_v = stir_.bus_voltage_v();
    if (!stir_.fault_text().empty()) s->stir.fault = stir_.fault_text();
    s->stir.alarm_code = stir_.alarm_code();
    s->stir.ramp_ms_per_1000rpm = stir_.ramp_ms();
    for (size_t g = 0; g < 3; ++g) s->gas.inlets[g] = shown[8 + g];
    s->gas.vent_open = shown[11];
    s->gas.manual_valve = manual_valve_;
    if (lease_.busy()) {
        s->gas.task = lease_.task();
        switch (lease_.owner()) {
            case core::GasOwner::Program: s->gas.owner = "program"; break;
            case core::GasOwner::Leak: s->gas.owner = "leak"; break;
            case core::GasOwner::Calib: s->gas.owner = "calib"; break;
            default: break;
        }
    }
    s->program = programs_->legacy_program();
    s->programs = programs_->programs();
    s->leak_test = activity_->leak_test();
    s->calib = activity_->calib();
    s->autotune = activity_->autotune();
    if (logger_) {
        auto key = logger_->session_key();
        auto sid = logger_->session_id();
        s->session.active = sid.has_value();
        s->session.id = key;
        s->session.test_id = sid;
        if (auto p = logger_->session_path()) {
            auto slash = p->find_last_of('/');
            s->session.file = slash == std::string::npos ? *p : p->substr(slash + 1);
        }
        s->logger_dropped_lines = logger_->dropped_lines();
    }
    s->settings = settings_;
    s->tick = TickStatsSnap{static_cast<double>(gate_.interval_ms()), gate_.entered(), gate_.skipped(), gate_.overruns(),
                            std::round(gate_.last_took_ms() * 10) / 10, std::round(gate_.max_took_ms() * 10) / 10,
                            std::round(gate_.last_overrun_ms() * 10) / 10};
    s->temp_comm = module(temp_.configured(), temp_.online(), temp_.stats(), temp_.values_at());
    s->press_comm = module(press_.configured(), press_.online(), press_.stats(),
                           p_valid ? std::optional<int64_t>(ps.at_wall_ms) : std::nullopt);
    s->stir_comm = module(stir_.configured(), stir_.online(), stir_.stats());
    s->io_comm = module(valves_.configured(), valves_.online(), valves_.stats());
    s->alarms.assign(alarms_.items().begin(), alarms_.items().end());
    snaps_.publish(std::move(s));
}

store::TelemetryRow ControlCore::build_telemetry_row() const {
    store::TelemetryRow row;
    row.time = store::WallTime::local(clock_->wall_ms());
    row.sim = hw_->simulated;
    bool real_t = temp_.online();
    bool real_p = press_.online();
    const auto& ps = press_.last();
    bool p_valid = real_p && ps.valid();
    const auto& shown = valves_.shown();
    for (size_t i = 0; i < 8; ++i) {
        const auto& c = ch_[i];
        const auto& tc = temp_.ch(c.id);
        auto& t = row.ch[i];
        if (real_t && tc.online) {  // 离线时 PV / 输出 / 模式都是旧值：留空
            t.pv = tc.pv;
            if (tc.pv_raw) t.pv_raw = *tc.pv_raw;
            t.output_pct = tc.output_pct;
            if (tc.mode >= 0) t.at = tc.mode;
        }
        if (tc.sp_known) t.sp_written = tc.sp;
        t.target_sp = c.tsp;
        t.approach = pascal(approach_phase_code(c.approach.phase()));  // C# Phase.ToString()：Off / Settle / Creep / Hold
        if (c.approach.active()) t.approach_sp = c.approach.current_sp();
        if (c.sp_mode == "curve" && c.profile.running()) t.curve_sp = c.tsp;
        if (p_valid) {
            t.press_raw = ps.raw[i];
            t.press_psi = ps.psi[i];
        }
        t.state = pascal(core::state_code(c.state));  // C# State.ToString()
        t.over_temp = ot_guard_.is_tripped(static_cast<int>(i));
        t.valve = shown[i];
        t.program_step = programs_->telemetry_step(c.id);
        if (c.uptake.valid()) t.gas_mmol = c.uptake.mmol();
    }
    if (real_t) {
        if (temp_.srun() >= 0) row.srun = temp_.srun();
        if (temp_.ctn() >= 0) row.ctn = temp_.ctn();
    }
    row.stir_on = stir_.on();
    row.stir_rpm_set = stir_.rpm();
    row.stir_motor_rpm = stir_.to_motor(stir_.rpm());
    row.stir_ramp_ms = stir_.ramp_ms();
    if (stir_.configured() && stir_.online()) {
        if (stir_.alarm_code()) row.stir_alarm = *stir_.alarm_code();
        if (stir_.bus_voltage_v() > 0) row.bus_voltage = stir_.bus_voltage_v();
    }
    for (size_t g = 0; g < 3; ++g) row.gas[g] = shown[8 + g];
    row.vent = shown[11];
    row.estopped = false;  // 「急停」列保留，恒为 0
    row.temp = tmodule(temp_.configured(), temp_.online(), temp_.stats());
    if (temp_.values_at()) row.temp.last_ok_at = store::WallTime::local(*temp_.values_at());
    row.press = tmodule(press_.configured(), press_.online(), press_.stats());
    row.io = tmodule(valves_.configured(), valves_.online(), valves_.stats());
    row.stir = tmodule(stir_.configured(), stir_.online(), stir_.stats());
    return row;
}

}  // namespace reactor::ctl
