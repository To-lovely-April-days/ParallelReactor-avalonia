#include "ctl/ctl_snapshot.hpp"

#include <cmath>

#include "store/store_util.hpp"

namespace reactor::ctl {

namespace {

ojson num(double v) { return std::isfinite(v) ? ojson(store::json_num(v)) : ojson(nullptr); }
ojson opt_num(const std::optional<double>& v) { return v ? num(*v) : ojson(nullptr); }
ojson opt_int(const std::optional<int>& v) { return v ? ojson(*v) : ojson(nullptr); }
ojson opt_str(const std::optional<std::string>& v) { return v ? ojson(*v) : ojson(nullptr); }
ojson opt_time(const std::optional<int64_t>& v) { return v ? ojson(store::fmt_utc_json(*v)) : ojson(nullptr); }
ojson or_null(const ojson& j) { return j.is_discarded() ? ojson(nullptr) : j; }

ojson profile_to_json(const ProfileSnap& p) {
    ojson segs = ojson::array();
    for (const auto& s : p.segments)
        segs.push_back(ojson{{"index", s.index}, {"tempC", num(s.temp_c)}, {"minutes", num(s.minutes)}, {"enabled", s.enabled},
                             {"active", s.active}});
    ojson j;
    j["running"] = p.running;
    j["segCount"] = p.seg_count;
    j["segments"] = segs;
    j["elapsedMin"] = num(p.elapsed_min);
    j["totalMinutes"] = num(p.total_minutes);
    j["complete"] = p.complete;
    j["statusText"] = p.status_text;
    return j;
}

}  // namespace

ojson channel_to_json(const ChannelSnap& c, bool with_profile) {
    ojson j;
    j["id"] = c.id;
    j["installed"] = c.installed;
    j["state"] = c.state;
    j["alarmCause"] = opt_str(c.alarm_cause);
    j["overTempTripped"] = c.over_temp_tripped;
    j["overPressureTripped"] = c.over_pressure_tripped;
    j["startedBy"] = opt_str(c.started_by);
    j["tempC"] = opt_num(c.temp_c);
    j["tempSpC"] = num(c.temp_sp_c);
    j["tempSpDeviceC"] = opt_num(c.temp_sp_device_c);
    j["heatOutputPct"] = opt_num(c.heat_output_pct);
    j["tempOnline"] = c.temp_online;
    j["autotuning"] = c.autotuning;
    j["atMode"] = opt_int(c.at_mode);
    j["spMode"] = c.sp_mode;
    j["approach"] = ojson{{"phase", c.approach.phase}, {"targetC", opt_num(c.approach.target_c)},
                          {"currentSpC", opt_num(c.approach.current_sp_c)}, {"statusText", c.approach.status_text}};
    j["profile"] = with_profile ? profile_to_json(c.profile) : ojson(nullptr);
    j["pressurePsi"] = opt_num(c.pressure_psi);
    j["pressureRaw"] = opt_int(c.pressure_raw);
    j["pressureSpPsi"] = num(c.pressure_sp_psi);
    j["gasConsumed"] = opt_num(c.gas_consumed);
    j["valveOpen"] = c.valve_open;
    j["programOwned"] = c.program_owned;
    j["program"] = c.program.is_null() ? ojson(nullptr) : c.program;
    j["gasRate"] = opt_num(c.gas_rate);
    j["solutionVolume"] = opt_num(c.solution_volume);
    return j;
}

ojson tick_stats_to_json(const TickStatsSnap& t) {
    return ojson{{"intervalMs", num(t.interval_ms)}, {"entered", t.entered}, {"skipped", t.skipped}, {"overruns", t.overruns},
                 {"lastTookMs", num(t.last_took_ms)}, {"maxTookMs", num(t.max_took_ms)}, {"lastOverrunMs", num(t.last_overrun_ms)}};
}

ojson module_comm_to_json(const ModuleCommSnap& m) {
    return ojson{{"configured", m.configured}, {"online", m.online}, {"polls", m.polls}, {"fails", m.fails},
                 {"lastOkAt", opt_time(m.last_ok_at_ms)}, {"valuesAt", opt_time(m.values_at_ms)}};
}

ojson to_json(const Snapshot& s) {
    ojson j;
    j["type"] = "telemetry";
    j["seq"] = s.seq;
    j["ts"] = store::fmt_utc_json(s.ts_ms);
    j["simulated"] = s.simulated;
    j["eventSeqApplied"] = s.event_seq_applied;
    j["mode"] = s.mode.is_null() ? ojson{{"control", "local"}, {"holder", nullptr}} : s.mode;
    // 设计文档 §5.3：急停删除，字段保留给旧客户端，恒为未触发
    j["estop"] = ojson{{"active", false},  {"heat", "pending"},  {"stir", "pending"},       {"valves", "pending"},
                       {"since", nullptr}, {"latched", false}, {"restoredOnBoot", false}};
    j["comm"] = ojson{{"temp", s.comm.temp}, {"pressure", s.comm.pressure}, {"stir", s.comm.stir}, {"io", s.comm.io}};
    ojson chs = ojson::array();
    for (const auto& c : s.channels) chs.push_back(channel_to_json(c, c.sp_mode == "curve"));
    j["channels"] = chs;
    j["stir"] = ojson{{"on", s.stir.on},
                      {"rpmSp", s.stir.rpm_sp},
                      {"online", s.stir.online},
                      {"locked", false},
                      {"motorCurrentA", opt_num(s.stir.motor_current_a)},
                      {"busVoltageV", opt_num(s.stir.bus_voltage_v)},
                      {"fault", opt_str(s.stir.fault)},
                      {"alarmCode", opt_int(s.stir.alarm_code)},
                      {"rampMsPer1000Rpm", s.stir.ramp_ms_per_1000rpm}};
    static const char* kGasIds[3] = {"inert", "A", "B"};
    ojson inlets = ojson::array();
    for (int g = 0; g < 3; ++g) inlets.push_back(ojson{{"id", kGasIds[g]}, {"open", s.gas.inlets[static_cast<size_t>(g)]}});
    j["gas"] = ojson{{"inlets", inlets}, {"ventOpen", s.gas.vent_open}, {"manualValve", s.gas.manual_valve},
                     {"task", opt_str(s.gas.task)}, {"owner", opt_str(s.gas.owner)}};
    j["program"] = or_null(s.program);
    j["programs"] = or_null(s.programs);
    j["leakTest"] = or_null(s.leak_test);
    j["session"] = ojson{{"active", s.session.active}, {"id", opt_str(s.session.id)}, {"testId", opt_str(s.session.test_id)},
                         {"file", opt_str(s.session.file)}};
    j["calib"] = or_null(s.calib);
    j["autotune"] = or_null(s.autotune);  // 新字段（第二阶段），只加在末尾
    return j;
}

void SnapshotStore::replace_latest(Ptr s) { std::atomic_store(&cur_, std::move(s)); }

void SnapshotStore::publish(Ptr s) {
    std::atomic_store(&cur_, s);
    std::vector<std::function<void(const Ptr&)>> subs;
    {
        std::lock_guard<std::mutex> lk(sub_mu_);
        for (auto& kv : subs_) subs.push_back(kv.second);
    }
    for (auto& f : subs) f(s);
}

SnapshotStore::Ptr SnapshotStore::latest() const { return std::atomic_load(&cur_); }

int SnapshotStore::subscribe(std::function<void(const Ptr&)> fn) {
    std::lock_guard<std::mutex> lk(sub_mu_);
    int id = next_id_++;
    subs_.emplace_back(id, std::move(fn));
    return id;
}

void SnapshotStore::unsubscribe(int id) {
    std::lock_guard<std::mutex> lk(sub_mu_);
    for (auto it = subs_.begin(); it != subs_.end(); ++it)
        if (it->first == id) { subs_.erase(it); return; }
}

}  // namespace reactor::ctl
