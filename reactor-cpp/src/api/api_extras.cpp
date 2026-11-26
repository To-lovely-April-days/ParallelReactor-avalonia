// 接口契约（doc/接口契约-新增.md）里不属于单个命令的部分：
//   §2.1 快照扩展（channels[i].bandC / antiOvershoot / heatOn、system、revs）  §2.2 WS progress  §2.3 报警扩展
//   §3.8 /runs  §3.9 /trend  §3.11 /leak-tests/current/samples  §3.13 /device/comm 的 modules / test / reconnect  §3.16 /storage  §3.17 /users
// 中枢线程每帧调 on_frame：更新修订号、趋势环、泄漏采样、运行记录，再推 progress。
#include <sys/statvfs.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <future>

#include "api/api_routes.hpp"
#include "api/api_runs.hpp"
#include "api/api_system.hpp"
#include "common/clock.hpp"
#include "core/program/program_json.hpp"
#include "core/program/step_fields.hpp"
#include "core/units.hpp"
#include "store/history_files.hpp"
#include "store/store_util.hpp"

namespace reactor::api {

namespace {

std::string rvs(int id) { return std::to_string(id) + " 号"; }

int64_t file_day_age_days(const std::string& name, int64_t now_ms) {
    auto d = store::history_files::day_of(name);
    if (!d) return -1;
    const std::string& ymd = d->first;
    if (ymd.size() != 8) return -1;
    int y = std::atoi(ymd.substr(0, 4).c_str()), m = std::atoi(ymd.substr(4, 2).c_str()), dd = std::atoi(ymd.substr(6, 2).c_str());
    int64_t day = store::days_from_civil(y, m, dd);
    int64_t today = (now_ms + store::local_offset_min(now_ms) * 60000LL) / 86400000LL;
    return today - day;
}

}  // namespace

// ===================== §2.1 快照扩展 =====================

ojson ApiService::revs_json(const ctl::Snapshot* s) {
    return ojson{{"settings", s ? s->settings.revision : 0},
                 {"programs", revs_programs_.load()},
                 {"library", revs_library_.load()},
                 {"runs", revs_runs_.load()},
                 {"files", revs_files_.load()},
                 {"leak", revs_leak_.load()},
                 {"calib", revs_calib_.load()},
                 {"network", revs_network_.load()},
                 {"usb", revs_usb_.load()},
                 {"users", revs_users_.load()},
                 {"device", revs_device_.load()},
                 {"pid", revs_pid_.load()}};
}

ojson ApiService::augment_frame(const ctl::Snapshot& s) {
    ojson f = ctl::to_json(s);
    if (f.contains("channels") && f["channels"].is_array())
        for (auto& c : f["channels"]) {
            int id = c.value("id", 0);
            if (id < 1 || id > 8) continue;
            auto p = commands_.prefs(id);
            const auto& cs = s.channels[static_cast<size_t>(id - 1)];
            c["bandC"] = jnum(p.band_c);
            c["antiOvershoot"] = p.anti_overshoot;
            c["heatOn"] = cs.temp_online && cs.at_mode && *cs.at_mode != 4;
        }
    f["system"] = ojson{{"languageCode", cmd::wire_language(s.settings.settings.language)}, {"bootAt", utc_iso(boot_wall_ms_)}};
    f["revs"] = revs_json(&s);
    return f;
}

// ===================== §2.3 报警扩展 =====================

ojson ApiService::alarm_json(const ctl::AlarmItem& a, const ctl::Snapshot* s) const {
    ojson j = ctl::alarm_to_json(a);
    auto starts = [&](const char* p) { return a.key.rfind(p, 0) == 0; };
    std::string quantity = (starts("op:") || starts("opv:") || starts("oph:") || starts("pd:")) ? "pressure"
                           : (starts("ot:") || starts("ht:"))                                    ? "temp"
                                                                                                  : "";
    std::string source = a.channel ? "channel" : (starts("comm:") || a.rv == "通讯") ? "comm" : "system";
    j["tier"] = a.ui_level;
    j["source"] = source;
    j["quantity"] = quantity;
    ojson value = nullptr, sp = nullptr, limit = nullptr, lo = nullptr, hi = nullptr;
    ojson did = ojson::array(), next = ojson::array();
    std::string context;
    if (s && a.channel && *a.channel >= 1 && *a.channel <= 8) {
        int id = *a.channel;
        const auto& c = s->channels[static_cast<size_t>(id - 1)];
        const auto& st = s->settings.settings;
        if (quantity == "pressure") {
            value = opt_num(c.pressure_psi);
            sp = jnum(c.pressure_sp_psi);
            limit = jnum(st.over_pressure);
            lo = 0;
            hi = jnum(core::mpa_to_psi(st.press_full_scale_mpa));
        } else if (quantity == "temp") {
            value = opt_num(c.temp_c);
            sp = jnum(c.temp_sp_c);
            limit = jnum(st.over_temp);
            lo = 0;
            hi = 400;
        }
        if (starts("op:")) {
            did.push_back("关闭 " + rvs(id) + "进气阀和三路气源");
            if (st.stop_heat_on_over_pressure) did.push_back("停止 " + rvs(id) + "加热");
            next.push_back("等压力降到 " + core::format_p(st.over_pressure * 0.98, d_.unit) + " 以下自动解除");
            next.push_back("确认后到主页手动复位");
        } else if (starts("ot:")) {
            did.push_back("停止 " + rvs(id) + "加热输出");
            next.push_back("等温度降到 " + core::fmt_dec(st.over_temp - 2, 1) + " ℃ 以下自动解除");
            next.push_back("确认后到主页手动复位");
        } else if (starts("opv:") || starts("oph:")) {
            did.push_back("每 5 s 重发一次联锁动作");
            next.push_back("检查 IO16R / AI-8 通讯，必要时点主页的「全部停止」或到现场断电");
        } else if (starts("ht:")) {
            next.push_back("检查加热器、热电偶和目标温度");
        } else if (starts("pd:")) {
            next.push_back("检查气路、进气阀和气源压力");
        }
        const auto& prog = c.program;
        if (prog.is_object() && prog.value("running", false)) {
            int idx = prog.value("stepIndex", -1);
            std::string name = prog.value("name", ojson("")).is_string() ? prog.value("name", std::string()) : std::string();
            std::string step = prog.value("stepName", ojson("")).is_string() ? prog.value("stepName", std::string()) : std::string();
            context = "程序「" + name + "」第 " + std::to_string(idx + 1) + " 步 " + step;
        }
    }
    if (source == "comm") {
        did.push_back("读数标为无效，不参与报警判定");
        next.push_back("检查接线与串口，恢复后自动解除");
    }
    j["value"] = value;
    j["setpoint"] = sp;
    j["limit"] = limit;
    j["rangeLo"] = lo;
    j["rangeHi"] = hi;
    j["did"] = did;
    j["next"] = next;
    j["context"] = context;
    j["downSince"] = source == "comm" && a.state != "cleared" && a.state != "done" ? ojson(utc_iso(a.raised_at_ms)) : ojson(nullptr);
    j["hint"] = "";
    j["highlight"] = "";
    return j;
}

// ===================== 每帧（中枢线程） =====================

void ApiService::record_trend(const ctl::Snapshot& s) {
    TrendPt p;
    p.ts = s.ts_ms;
    for (size_t i = 0; i < 8; ++i) {
        const auto& c = s.channels[i];
        if (c.temp_c) { p.t[i] = static_cast<float>(*c.temp_c); p.valid |= 1u << i; }
        if (c.pressure_psi) { p.p[i] = static_cast<float>(*c.pressure_psi); p.valid |= 1u << (8 + i); }
        if (c.gas_consumed) { p.g[i] = static_cast<float>(*c.gas_consumed); p.valid |= 1u << (16 + i); }
    }
    std::lock_guard<std::mutex> lk(trend_mu_);
    trend_.push_back(p);
    while (!trend_.empty() && trend_.front().ts < s.ts_ms - 24LL * 3600 * 1000) trend_.pop_front();
}

void ApiService::record_leak(const ctl::Snapshot& s) {
    const auto& l = s.leak_test;
    if (!l.is_object()) return;
    std::string stage = l.value("stage", ojson("")).is_string() ? l.value("stage", std::string()) : std::string();
    std::string run = l.value("runId", ojson("")).is_string() ? l.value("runId", std::string()) : std::string();
    std::lock_guard<std::mutex> lk(leak_mu_);
    if (stage != "measure" || run.empty()) {
        if (leak_samples_) leak_samples_->measuring = false;
        return;
    }
    if (!leak_samples_ || leak_samples_->run_id != run || !leak_samples_->measuring) {
        if (!leak_samples_ || leak_samples_->run_id != run) {
            LeakSamples ls;
            ls.run_id = run;
            ls.measure_start_ms = s.ts_ms;
            leak_samples_ = ls;
        }
        leak_samples_->measuring = true;
    }
    auto& ls = *leak_samples_;
    if (!ls.points.empty() && s.ts_ms - ls.last_ms < static_cast<int64_t>(ls.sample_sec * 1000)) return;
    ojson dp = ojson::array();
    std::array<std::optional<double>, 8> cur{};
    if (l.contains("channels") && l["channels"].is_array())
        for (const auto& c : l["channels"]) {
            int id = c.value("id", 0);
            if (id >= 1 && id <= 8 && c.contains("pressurePsi") && c["pressurePsi"].is_number())
                cur[static_cast<size_t>(id - 1)] = c["pressurePsi"].get<double>();
        }
    for (size_t i = 0; i < 8; ++i) {
        if (!ls.p0[i] && cur[i]) ls.p0[i] = cur[i];
        if (cur[i] && ls.p0[i]) dp.push_back(jnum(std::round((*cur[i] - *ls.p0[i]) * 1000) / 1000));
        else dp.push_back(nullptr);
    }
    ls.points.push_back(ojson{{"min", jnum(std::round((s.ts_ms - ls.measure_start_ms) / 600.0) / 100.0)}, {"dpPsi", dp}});
    ls.last_ms = s.ts_ms;
    revs_leak_.fetch_add(1);   // 每采一个测量点
}

void ApiService::poll_slow(int64_t now) {
    if (now - last_slow_ms_ < 5000) return;
    last_slow_ms_ = now;
    std::string fs;
    for (const char* t : {"telemetry", "events"})
        for (const auto& f : store::history_files::list(d_.data_dir, t, false)) fs += f.name + "\n";
    std::string us;
    for (const auto& u : list_usb(d_.usb_root)) us += u.path + "\n";
    {
        std::lock_guard<std::mutex> lk(sig_mu_);
        if (fs != files_sig_) { files_sig_ = fs; revs_files_.fetch_add(1); }
        if (us != usb_sig_) { usb_sig_ = us; revs_usb_.fetch_add(1); }
    }
    // 数据保留（接口契约 §3.1 storage.retentionDays）：每 6 h 清一次过期的每日 CSV（telemetry-* / events-*）
    if (now - last_clean_ms_ < 6LL * 3600 * 1000) return;
    last_clean_ms_ = now;
    auto cur = snap();
    if (!cur) return;
    double keep = std::clamp(cur->settings.settings.storage_retention_days, 7.0, 3650.0);
    int removed = 0;
    for (const char* t : {"telemetry", "events"})
        for (const auto& f : store::history_files::list(d_.data_dir, t, false)) {
            int64_t age = file_day_age_days(f.name, now_wall());
            if (age < 0 || age <= keep) continue;
            auto path = store::safe_path::try_resolve(d_.data_dir, t, f.name);
            if (path && std::remove(path->c_str()) == 0) ++removed;
        }
    if (removed > 0) {
        audit_plain("info", "已清理过期数据文件", std::to_string(removed) + " 个每日文件超过保留期 " + core::fmt_dec(keep, 0) + " 天");
        revs_files_.fetch_add(1);
    }
}

void ApiService::on_frame(const ctl::Snapshot& s) {
    // 修订号：泄漏 / 标定 / PID 按内容签名判断有没有变化
    std::string leak_sig, calib_sig = dump(s.calib), pid_sig;
    if (s.leak_test.is_object()) {
        const auto& l = s.leak_test;
        leak_sig = dump(ojson{{"r", l.value("runId", ojson())}, {"s", l.value("stage", ojson())}, {"d", l.value("done", ojson())},
                              {"l", l.value("lastRunId", ojson())}, {"x", l.value("running", ojson())}});
    }
    {
        ojson p = pid_json(s);
        p.erase("seq");
        p.erase("ts");
        pid_sig = dump(p);
    }
    {
        std::lock_guard<std::mutex> lk(sig_mu_);
        if (leak_sig != leak_sig_) { leak_sig_ = leak_sig; revs_leak_.fetch_add(1); }
        if (calib_sig != calib_sig_) { calib_sig_ = calib_sig; revs_calib_.fetch_add(1); }
        if (pid_sig != pid_sig_) { pid_sig_ = pid_sig; revs_pid_.fetch_add(1); }
    }
    record_trend(s);
    record_leak(s);
    if (runs_) {
        RunsBook::Limits lim;
        lim.over_pressure = s.settings.settings.over_pressure;
        lim.over_temp = s.settings.settings.over_temp;
        lim.full_scale_psi = core::mpa_to_psi(s.settings.settings.press_full_scale_mpa);
        if (runs_->track(s, lim, now_wall())) revs_runs_.fetch_add(1);
    }
    poll_slow(now_mono());
}

// ===================== §2.2 progress =====================

namespace {

ojson step_target(const ojson& step) {
    if (!step.is_object() || !step.contains("params")) return nullptr;
    const auto& p = step["params"];
    std::string t = step.value("type", "");
    auto num = [&](const char* k) -> std::optional<double> {
        if (p.contains(k) && p[k].is_number()) return p[k].get<double>();
        return std::nullopt;
    };
    auto q = [](const char* quantity, std::optional<double> v) {
        return v ? ojson{{"quantity", quantity}, {"value", jnum(*v)}} : ojson(nullptr);
    };
    if (t == "heat") {
        if (p.value("mode", "fixed") == "curve" && p.contains("segments") && p["segments"].is_array() && !p["segments"].empty()) {
            const auto& last = p["segments"].back();
            if (last.contains("temp") && last["temp"].is_number()) return q("temp", last["temp"].get<double>());
        }
        return q("temp", num("temp"));
    }
    if (t == "press" || t == "leak") return q("pressure", num("pres"));
    if (t == "stir") return q("rpm", num("rpm"));
    if (t == "react") {
        if (p.value("mmolOn", "off") == "on") return q("mmol", num("mmol"));
        return q("time", num("time"));
    }
    if (t == "hold") {
        std::string until = p.value("until", "time");
        if (until == "time") return q("time", num("time"));
        if (until == "tempAbove" || until == "tempBelow") return q("temp", num("temp"));
        return q("pressure", num("pres"));
    }
    return nullptr;
}

// 方案：每步的相对起止、计划温度 / 压力拐点（同一份步骤只算一次）
ojson plan_of(const ojson& steps) {
    static std::mutex mu;
    static std::map<std::string, ojson> cache;
    std::string key = steps.dump();
    {
        std::lock_guard<std::mutex> lk(mu);
        auto it = cache.find(key);
        if (it != cache.end()) return it->second;
    }
    ojson segs = ojson::array(), temp = ojson::array(), press = ojson::array();
    double t_cur = 25, p_cur = 0, at = 0;
    temp.push_back(ojson::array({0, 25}));
    press.push_back(ojson::array({0, 0}));
    nlohmann::json plain = nlohmann::json::parse(key, nullptr, false);
    auto parsed = plain.is_discarded() ? core::program::ProgramParseResult{} : core::program::program_json::parse(plain);
    for (size_t k = 0; k < steps.size(); ++k) {
        const auto& st = steps[k];
        double dur = 0;
        bool open = false;
        double earliest = 0;
        if (parsed.steps && k < parsed.steps->size()) {
            auto e = core::program::step_duration::of((*parsed.steps)[k]);
            dur = e.minutes * 60;
            open = e.upper_bound;
        }
        std::string type = st.value("type", "");
        const auto& p = st.contains("params") ? st["params"] : ojson::object();
        if (type == "react" && p.contains("minTime") && p["minTime"].is_number()) earliest = at + p["minTime"].get<double>() * 60;
        else earliest = at + dur;
        segs.push_back(ojson{{"kind", type}, {"name", st.value("name", type)}, {"startSec", jnum(std::round(at))},
                             {"endSec", jnum(std::round(at + dur))}, {"openEnded", open}, {"earliestEndSec", jnum(std::round(earliest))}});
        at += dur;
        auto tg = step_target(st);
        if (type == "heat" && tg.is_object()) {
            t_cur = tg["value"].get<double>();
            temp.push_back(ojson::array({jnum(std::round(at)), jnum(t_cur)}));
        } else if ((type == "press") && tg.is_object()) {
            p_cur = tg["value"].get<double>();
            press.push_back(ojson::array({jnum(std::round(at)), jnum(p_cur)}));
        } else if (type == "vent" || type == "leak") {
            if (p_cur != 0) press.push_back(ojson::array({jnum(std::round(at)), 0}));
            p_cur = 0;
        }
    }
    temp.push_back(ojson::array({jnum(std::round(at)), jnum(t_cur)}));
    press.push_back(ojson::array({jnum(std::round(at)), jnum(p_cur)}));
    ojson out{{"segs", segs}, {"temp", temp}, {"press", press}, {"totalSec", jnum(std::round(at))}};
    std::lock_guard<std::mutex> lk(mu);
    if (cache.size() > 64) cache.clear();
    cache[key] = out;
    return out;
}

ojson ids_of(const ojson& v) {
    ojson a = ojson::array();
    if (v.is_number_integer()) a.push_back(v);
    else if (v.is_array()) {
        for (const auto& x : v)
            if (x.is_number_integer()) a.push_back(x);
            else if (x.is_string()) {
                std::string s = x.get<std::string>();
                std::string d;
                for (char c : s)
                    if (c >= '0' && c <= '9') d += c;
                if (!d.empty()) a.push_back(std::atoi(d.c_str()));
            }
    } else if (v.is_string()) {
        std::string s = v.get<std::string>(), d;
        for (char c : s) {
            if (c >= '0' && c <= '9') d += c;
            else if (!d.empty()) { a.push_back(std::atoi(d.c_str())); d.clear(); }
        }
        if (!d.empty()) a.push_back(std::atoi(d.c_str()));
    }
    return a;
}

}  // namespace

ojson ApiService::progress_json(const ctl::Snapshot& s) {
    ojson chans = ojson::array();
    std::array<ProgInfo, 8> info;
    {
        std::lock_guard<std::mutex> lk(prog_mu_);
        info = prog_;
    }
    for (int id = 1; id <= 8; ++id) {
        size_t i = static_cast<size_t>(id - 1);
        const ojson& prog = s.channels[i].program;
        const ojson& run = s.program_runs[i];
        bool running = prog.is_object() && prog.value("running", false);
        const auto& pi = info[i];
        if (!running && !pi.edited_not_sent) continue;
        ojson steps = running && run.is_object() ? run.value("steps", ojson::array()) : pi.steps;
        ojson names = ojson::array();
        for (const auto& st : steps) names.push_back(ojson{{"type", st.value("type", "")}, {"name", st.value("name", st.value("type", ""))}});
        int idx = running ? prog.value("stepIndex", 0) : 0;
        if (idx < 0) idx = 0;
        ojson plan = plan_of(steps);
        int64_t t0 = now_wall();
        if (running && run.is_object() && run["startedAt"].is_string()) {
            int64_t ms = 0;
            if (store::parse_time_loose(run["startedAt"].get<std::string>(), ms)) t0 = ms;
        }
        ojson remaining = nullptr;
        if (running && prog.contains("remaining") && prog["remaining"].is_object() && prog["remaining"]["minutes"].is_number())
            remaining = jnum(std::round(prog["remaining"]["minutes"].get<double>() * 60));
        double elapsed = running && prog.contains("elapsedMin") && prog["elapsedMin"].is_number() ? prog["elapsedMin"].get<double>() * 60 : 0;
        ojson pos = running && prog.contains("gasQueuePos") && prog["gasQueuePos"].is_number() ? prog["gasQueuePos"] : ojson(nullptr);
        // 当前步完成比例：按计划的起止估（不知道 = 0）
        double frac = 0;
        if (running && static_cast<size_t>(idx) < plan["segs"].size()) {
            const auto& sg = plan["segs"][static_cast<size_t>(idx)];
            double a = sg["startSec"].get<double>(), b = sg["endSec"].get<double>();
            if (b > a) frac = std::clamp((elapsed - a) / (b - a), 0.0, 1.0);
        }
        chans.push_back(ojson{
            {"id", id},
            {"programName", running ? prog.value("name", ojson(pi.name)) : ojson(pi.name)},
            {"sourceId", opt_str(pi.source_id)},
            {"steps", names},
            {"stepIndex", idx},
            {"stepTarget", static_cast<size_t>(idx) < steps.size() ? step_target(steps[static_cast<size_t>(idx)]) : ojson(nullptr)},
            {"stepFraction", jnum(std::round(frac * 100) / 100)},
            {"elapsedSec", jnum(std::round(elapsed))},
            {"remainingSec", remaining},
            {"gasQueueRank", pos},
            {"waitingFor", running && prog.contains("waitingOn") ? ids_of(prog["waitingOn"]) : ojson::array()},
            {"waitedSec", pos.is_null() ? ojson(nullptr) : ojson(0)},
            {"draft", ojson{{"editedNotSent", pi.edited_not_sent}, {"stepCount", pi.steps.size()},
                            {"totalSec", pi.edited_not_sent ? plan_of(pi.steps)["totalSec"] : ojson(0)}}},
            {"plan", ojson{{"t0", utc_iso(t0)}, {"segs", plan["segs"]}, {"temp", plan["temp"]}, {"press", plan["press"]}}}});
    }
    ojson at = ojson::array();
    if (s.autotune.is_object() && s.autotune.contains("channels"))
        for (const auto& a : s.autotune["channels"])
            if (a.value("running", false)) {
                double el = a.contains("elapsedSec") && a["elapsedSec"].is_number() ? a["elapsedSec"].get<double>() : 0;
                // 宇电 AI-8 自整定一般 2–3 个振荡周期、约 20–40 min：没有回读，按 30 min 估
                double frac = std::clamp(el / 1800.0, 0.0, 0.99);
                at.push_back(ojson{{"id", a.value("channel", 0)}, {"cycle", 0}, {"cycles", 0},
                                   {"leftSec", jnum(std::max(0.0, std::round(1800 - el)))}, {"fraction", jnum(std::round(frac * 100) / 100)}});
            }
    return ojson{{"type", "progress"}, {"ts", utc_iso(s.ts_ms)}, {"channels", chans}, {"autotune", at}};
}

// ===================== §3.9 趋势 =====================

ojson ApiService::trend(int64_t from, int64_t to, int64_t step, const std::vector<std::string>& fields, const std::vector<int>& channels) {
    int64_t t0 = (from + step - 1) / step * step;
    if (from < 0) t0 = from / step * step;
    int64_t count = to > t0 ? (to - t0 + step - 1) / step : 0;
    struct Arr { std::vector<std::optional<float>> v; };
    std::vector<std::array<Arr, 3>> data(8);
    for (auto& d : data)
        for (auto& a : d) a.v.assign(static_cast<size_t>(count), std::nullopt);
    auto put = [&](int64_t ts, int ch, int f, float v) {
        if (ts < from || ts >= to) return;   // [from, to)
        if (ts < t0) return;
        int64_t k = (ts - t0) / step;
        if (k < 0 || k >= count) return;
        data[static_cast<size_t>(ch)][static_cast<size_t>(f)].v[static_cast<size_t>(k)] = v;   // 按时间顺序写入：最后一个有效值留下
    };
    int64_t ring_oldest = INT64_MAX;
    {
        std::lock_guard<std::mutex> lk(trend_mu_);
        if (!trend_.empty()) ring_oldest = trend_.front().ts;
    }
    // 环里没有的那段：读每日遥测 CSV
    if (from < ring_oldest && count > 0) {
        int64_t csv_to = std::min(to, ring_oldest);
        auto files = store::history_files::select_daily_files(d_.data_dir, from, csv_to);
        for (const auto& path : files) {
            std::string text;
            if (!store::read_file(path, text)) continue;
            size_t pos = 0;
            std::vector<std::array<int, 3>> col(8, {-1, -1, -1});
            bool header = true;
            while (pos < text.size()) {
                size_t e = text.find('\n', pos);
                if (e == std::string::npos) break;   // 末尾半行跳过
                std::string line = text.substr(pos, e - pos);
                pos = e + 1;
                if (!line.empty() && line.back() == '\r') line.pop_back();
                if (header) {
                    if (line.compare(0, 3, store::kUtf8Bom) == 0) line.erase(0, 3);
                    std::vector<std::string> h;
                    size_t a = 0;
                    while (true) {
                        size_t c = line.find(',', a);
                        h.push_back(line.substr(a, c == std::string::npos ? std::string::npos : c - a));
                        if (c == std::string::npos) break;
                        a = c + 1;
                    }
                    for (int ch = 0; ch < 8; ++ch) {
                        std::string pre = "RV" + std::to_string(ch + 1) + "_";
                        for (size_t k = 0; k < h.size(); ++k) {
                            if (h[k] == pre + "PV(℃)") col[static_cast<size_t>(ch)][0] = static_cast<int>(k);
                            if (h[k] == pre + "压力(psi)") col[static_cast<size_t>(ch)][1] = static_cast<int>(k);
                            if (h[k] == pre + "吸气量(mmol)") col[static_cast<size_t>(ch)][2] = static_cast<int>(k);
                        }
                    }
                    header = false;
                    continue;
                }
                auto ts = store::history_files::parse_row_time(line);
                if (!ts || *ts < from || *ts >= csv_to) continue;
                std::vector<std::string> f;
                size_t a = 0;
                while (true) {
                    size_t c = line.find(',', a);
                    f.push_back(line.substr(a, c == std::string::npos ? std::string::npos : c - a));
                    if (c == std::string::npos) break;
                    a = c + 1;
                }
                for (int ch = 0; ch < 8; ++ch)
                    for (int k = 0; k < 3; ++k) {
                        int ci = col[static_cast<size_t>(ch)][static_cast<size_t>(k)];
                        if (ci < 0 || static_cast<size_t>(ci) >= f.size() || f[static_cast<size_t>(ci)].empty()) continue;
                        char* endp = nullptr;
                        double v = std::strtod(f[static_cast<size_t>(ci)].c_str(), &endp);
                        if (endp && *endp == 0) put(*ts, ch, k, static_cast<float>(v));
                    }
            }
        }
    }
    {
        std::lock_guard<std::mutex> lk(trend_mu_);
        auto it = std::lower_bound(trend_.begin(), trend_.end(), from, [](const TrendPt& p, int64_t t) { return p.ts < t; });
        for (; it != trend_.end() && it->ts < to; ++it)
            for (int ch = 0; ch < 8; ++ch) {
                if (it->valid & (1u << ch)) put(it->ts, ch, 0, it->t[static_cast<size_t>(ch)]);
                if (it->valid & (1u << (8 + ch))) put(it->ts, ch, 1, it->p[static_cast<size_t>(ch)]);
                if (it->valid & (1u << (16 + ch))) put(it->ts, ch, 2, it->g[static_cast<size_t>(ch)]);
            }
    }
    static const char* kNames[] = {"tempC", "pressurePsi", "gasMmol"};
    static const double kRound[] = {10, 100, 1000};
    ojson chans = ojson::array();
    for (int ch : channels) {
        if (ch < 1 || ch > 8) continue;
        ojson c{{"id", ch}};
        for (int k = 0; k < 3; ++k) {
            if (std::find(fields.begin(), fields.end(), kNames[k]) == fields.end()) continue;
            ojson a = ojson::array();
            for (const auto& v : data[static_cast<size_t>(ch - 1)][static_cast<size_t>(k)].v)
                a.push_back(v ? jnum(std::round(*v * kRound[k]) / kRound[k]) : ojson(nullptr));
            c[kNames[k]] = a;
        }
        chans.push_back(c);
    }
    return ojson{{"t0", utc_iso(t0)}, {"stepMs", step}, {"count", count}, {"channels", chans}};
}

// ===================== 路由 =====================

void Routes::extras(ApiService& s, net::Router& r) {
    using net::Request;
    using net::RouteOptions;
    ApiService* self = &s;
    const std::string P = "/api/v1";
    RouteOptions local{net::RateClass::Auto, false, true};

    // §3.9 GET /trend（两边）
    r.get(P + "/trend", [self](const Request& q) {
        int64_t f = 0, t = 0;
        if (!store::history_files::try_parse_query_time(q.query_param("from"), f) ||
            !store::history_files::try_parse_query_time(q.query_param("to"), t))
            return api_error("BAD_REQUEST", "from / to 必须是带时区偏移或 Z 的 ISO-8601 时间，例如 2026-11-26T00:00:00.000Z");
        if (t <= f) return api_error("BAD_REQUEST", "to 必须晚于 from（半开区间 [from, to)）");
        int64_t step = 10000;
        if (q.has_query("stepMs") && !q.query_param("stepMs").empty()) {
            char* e = nullptr;
            long long v = std::strtoll(q.query_param("stepMs").c_str(), &e, 10);
            if (!e || *e || v < 1000 || v > 3600000) return api_error("BAD_REQUEST", "stepMs 须为 1000–3600000 的整数");
            step = v;
        }
        if ((t - f) / step > 10000) return api_error("BAD_REQUEST", "点数超过 10000 · 请缩短时间段或加大 stepMs");
        return ok_json(self->trend(f, t, step, {"tempC", "pressurePsi", "gasMmol"}, {1, 2, 3, 4, 5, 6, 7, 8}));
    });

    // §3.11 GET /leak-tests/current/samples（两边）
    r.get(P + "/leak-tests/current/samples", [self](const Request&) {
        std::lock_guard<std::mutex> lk(self->leak_mu_);
        if (!self->leak_samples_) return api_error("NOT_FOUND", "本次开机还没有做过泄漏测试");
        const auto& l = *self->leak_samples_;
        return ok_json(ojson{{"runId", l.run_id}, {"sampleSec", jnum(l.sample_sec)}, {"points", l.points}});
    });

    // §3.8 /runs（两边）
    r.get(P + "/runs", [self](const Request& q) {
        int limit = 50;
        if (q.has_query("limit") && !q.query_param("limit").empty()) {
            limit = std::atoi(q.query_param("limit").c_str());
            if (limit < 1 || limit > 200) return api_error("BAD_REQUEST", "limit 须为 1–200 的整数");
        }
        return ok_json(self->runs_->list(limit));
    });
    r.get(P + "/runs/{id}", [self](const Request& q) {
        std::string id = q.param("id");
        auto j = self->runs_->get(id);
        if (!j) return api_error("NOT_FOUND", "运行记录 " + id + " 不存在");
        auto rg = self->runs_->range(id, self->now_wall());
        if (rg && rg->end > rg->start) {
            int64_t step = 60000;
            int64_t to = rg->end + 1;
            if ((to - rg->start) / step > 10000) step = (to - rg->start) / 10000 + 1;
            (*j)["trend"] = self->trend(rg->start, to, step, {"gasMmol"}, rg->channels);
        } else {
            (*j)["trend"] = ojson{{"t0", (*j)["start"]}, {"stepMs", 60000}, {"count", 0}, {"channels", ojson::array()}};
        }
        if (!j->contains("program")) (*j)["program"] = nullptr;
        if (!j->contains("stepMinutes")) (*j)["stepMinutes"] = ojson::array();
        return ok_json(*j);
    });
    auto W = [self](WriteSpec spec, Binder b) { return [self, spec, b](const Request& q) { return self->command(q, spec, b); }; };
    WriteSpec note_spec{"run.note", cmd::ActionClass::Record};
    note_spec.inline_exec = true;
    r.put(P + "/runs/{id}/note", W(note_spec, [self](const nlohmann::json* body, const Request& q, const cmd::Source&) {
        Params p(body);
        auto note = p.str("note");
        if (!p.bad() && !note) p.fail("缺少 note");
        if (p.bad()) return Bound::bad(p.error);
        std::string id = q.param("id"), n = trim(*note);
        if (utf16_len(n) > 200) return Bound::reject("PARAM_OUT_OF_RANGE", "备注最多 200 个字");
        Bound b;
        b.exec = [self, id, n](cmd::Commands&, const cmd::Source&, cmd::Done d) {
            if (!self->runs_->set_note(id, n)) return d(ctl::OpResult::fail("NOT_FOUND", "运行记录 " + id + " 不存在"));
            self->bump_runs();
            d(ctl::OpResult::done("备注已保存", "ok", ojson{{"id", id}, {"note", n}}));
        };
        return b;
    }));

    // §3.16 GET /storage（本地）
    r.get(P + "/storage", [self](const Request&) {
        struct statvfs fs {};
        int64_t total = 0, free_b = 0;
        if (statvfs(self->d_.data_dir.c_str(), &fs) == 0) {
            total = static_cast<int64_t>(fs.f_blocks) * static_cast<int64_t>(fs.f_frsize);
            free_b = static_cast<int64_t>(fs.f_bavail) * static_cast<int64_t>(fs.f_frsize);
        }
        int64_t data_bytes = 0, today_t = 0, today_e = 0;
        std::string today = store::fmt_wall(store::WallTime::local(self->now_wall()), "yyyyMMdd");
        for (const char* t : {"telemetry", "events", "sessions", "leak"})
            for (const auto& f : store::history_files::list(self->d_.data_dir, t, false)) {
                data_bytes += f.bytes;
                auto d = store::history_files::day_of(f.name);
                if (d && d->first == today) (std::string(t) == "telemetry" ? today_t : today_e) += std::string(t) == "leak" ? 0 : f.bytes;
            }
        int leak_runs = 0;
        for (const auto& n : store::list_files(store::join_path(self->d_.data_dir, "leak")))
            if (n.size() > 13 && n.compare(n.size() - 13, 13, "-summary.json") == 0) ++leak_runs;
        return ok_json(ojson{{"diskTotal", total},
                             {"diskFree", free_b},
                             {"systemBytes", std::max<int64_t>(0, total - free_b - data_bytes)},
                             {"dataBytes", data_bytes},
                             {"settingsPath", store::join_path(self->d_.config_dir.empty() ? "/etc/reactord" : self->d_.config_dir, "settings.json")},
                             {"dataDir", self->d_.data_dir},
                             {"todayTelemetryBytes", today_t},
                             {"todayEventsBytes", today_e},
                             {"leakRuns", leak_runs}});
    }, local);

    // §3.17 GET /users（本地）
    r.get(P + "/users", [](const Request&) { return ok_json(ojson{{"mode", "single"}, {"role", "admin"}}); }, local);

    // §3.13 POST /device/comm/{module}/test（本地，R）：读一次该模块
    WriteSpec test_spec{"comm.test", cmd::ActionClass::Record};
    test_spec.inline_exec = true;
    r.post(P + "/device/comm/{module}/test", W(test_spec, [self](const nlohmann::json*, const Request& q, const cmd::Source&) {
        std::string m = q.param("module");
        if (m != "temp" && m != "pressure" && m != "io" && m != "stir") return Bound::reject("NOT_FOUND", "没有这个模块：" + m + "（temp / pressure / io / stir）");
        Bound b;
        b.exec = [self, m](cmd::Commands&, const cmd::Source&, cmd::Done d) {
            hw::Hardware* hw = self->d_.hw;
            bool configured = hw && (m == "temp" ? hw->temp_configured() : m == "pressure" ? hw->press_configured()
                                     : m == "io"  ? hw->io_configured()
                                                  : hw->stir_configured());
            hw::Bus* bus = !hw ? nullptr : m == "temp" ? hw->temp_bus.get() : m == "pressure" ? hw->press_bus.get() : m == "io" ? hw->io_bus.get() : hw->stir_bus.get();
            if (!configured || !bus) return d(ctl::OpResult::fail("HW_NOT_CONFIGURED", "该模块没有配置串口（hardware.json）"));
            hw::ModbusRequest req = m == "temp" ? hw->temp.req_read_pv_block() : m == "pressure" ? hw->press.req_read_raw()
                                    : m == "io"  ? hw->io.req_read_coils()
                                                 : hw->stir.req_read_status();
            auto prom = std::make_shared<std::promise<hw::ModbusResult>>();
            auto fut = prom->get_future();
            int64_t t0 = reactor::mono_ms();
            bus->submit(std::move(req), hw::Priority::Normal, [prom](const hw::ModbusResult& res) { prom->set_value(res); });
            if (fut.wait_for(std::chrono::seconds(3)) != std::future_status::ready)
                return d(ctl::OpResult::done("读取超时（3 s 内没有回应）", "warn", ojson{{"ok", false}, {"ms", 3000}, {"message", "读取超时（3 s 内没有回应）"}}));
            auto res = fut.get();
            int64_t ms = reactor::mono_ms() - t0;
            static const std::map<std::string, std::string> what{{"temp", "读到 8 路温度"}, {"pressure", "读到 8 路压力"},
                                                                 {"io", "读到 12 路阀门状态"}, {"stir", "读到驱动器状态"}};
            std::string msg = res.ok ? what.at(m) + "，用时 " + std::to_string(ms) + " ms" : "读取失败：" + res.error;
            d(ctl::OpResult::done(msg, res.ok ? "ok" : "warn", ojson{{"ok", res.ok}, {"ms", ms}, {"message", msg}}));
        };
        return b;
    }), local);
    // POST /device/comm/{module}/reconnect（本地，C）：串口由总线线程持有、出错时自己重开；这里只核对配置并清连续失败（下一轮轮询起算）
    WriteSpec rc_spec{"comm.reconnect", cmd::ActionClass::Control};
    rc_spec.inline_exec = true;
    r.post(P + "/device/comm/{module}/reconnect", W(rc_spec, [self](const nlohmann::json*, const Request& q, const cmd::Source&) {
        std::string m = q.param("module");
        if (m != "temp" && m != "pressure" && m != "io" && m != "stir") return Bound::reject("NOT_FOUND", "没有这个模块：" + m + "（temp / pressure / io / stir）");
        Bound b;
        b.exec = [self, m](cmd::Commands&, const cmd::Source&, cmd::Done d) {
            hw::Hardware* hw = self->d_.hw;
            bool configured = hw && (m == "temp" ? hw->temp_configured() : m == "pressure" ? hw->press_configured()
                                     : m == "io"  ? hw->io_configured()
                                                  : hw->stir_configured());
            if (!configured) return d(ctl::OpResult::fail("HW_NOT_CONFIGURED", "该模块没有配置串口（hardware.json）"));
            self->audit_plain("info", "通讯模块重连", m + " · 触屏请求（串口由总线线程在失败后自动重开）");
            d(ctl::OpResult::done("已请求重连", "ok", ojson::object()));
        };
        return b;
    }), local);
}

}  // namespace reactor::api
