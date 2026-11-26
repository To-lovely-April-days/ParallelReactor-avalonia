#include "api/api_runs.hpp"

#include <algorithm>
#include <cmath>

#include "api/api_util.hpp"
#include "core/num_format.hpp"
#include "core/program/program_json.hpp"
#include "core/program/step_fields.hpp"
#include "store/history_files.hpp"
#include "store/store_util.hpp"

namespace reactor::api {

namespace {

bool safe_id(const std::string& id) {
    if (id.empty() || id.size() > 80) return false;
    for (char c : id)
        if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '-' || c == '_')) return false;
    return true;
}

double planned_sec(const ojson& steps) {
    nlohmann::json plain = nlohmann::json::parse(steps.dump(), nullptr, false);
    if (plain.is_discarded()) return 0;
    auto r = core::program::program_json::parse(plain);
    if (!r.steps) return 0;
    return core::program::step_duration::total(*r.steps).minutes * 60.0;
}

std::optional<int64_t> parse_utc(const ojson& v) {
    if (!v.is_string()) return std::nullopt;
    int64_t ms = 0;
    if (!store::parse_time_loose(v.get<std::string>(), ms)) return std::nullopt;
    return ms;
}

}  // namespace

RunsBook::RunsBook(std::string data_dir) : dir_(store::join_path(data_dir, "runs")), data_dir_(std::move(data_dir)) {
    for (const auto& name : store::list_files(dir_)) {
        if (name.size() < 6 || name.compare(name.size() - 5, 5, ".json") != 0) continue;
        std::string text;
        if (!store::read_file(store::join_path(dir_, name), text)) continue;
        auto j = ojson::parse(text, nullptr, false);
        if (!j.is_object() || !j.contains("id") || !j["id"].is_string()) continue;
        if (j.value("_leakNote", false)) {
            leak_notes_[j["id"].get<std::string>()] = j.value("note", "");
            continue;
        }
        Rec r;
        if (j.contains("_tracks") && j["_tracks"].is_object())
            for (auto it = j["_tracks"].begin(); it != j["_tracks"].end(); ++it) {
                ChTrack t;
                t.started_at = it.value().value("startedAt", "");
                t.open = it.value().value("open", false);
                r.chs[std::atoi(it.key().c_str())] = t;
            }
        j.erase("_tracks");
        r.j = j;
        r.start_ms = parse_utc(j.value("start", ojson())).value_or(0);
        // 重启前没结束的运行：程序引擎不会接着跑（开机全部 At=4），按中断收尾
        if (r.j["end"].is_null() && r.j.value("result", "") == "running") {
            r.j["result"] = "abort";
            r.j["resultText"] = "服务重启 · 中断";
            r.j["end"] = r.j["start"];
            r.j["endReason"] = "reactord 重启时这次运行还没结束";
            for (auto& c : r.chs) c.second.open = false;
            save(r);
        }
        runs_[r.j["id"].get<std::string>()] = std::move(r);
    }
}

void RunsBook::save(const Rec& r) const {
    store::make_dirs(dir_);
    ojson out = r.j;
    ojson tr = ojson::object();
    for (const auto& c : r.chs) tr[std::to_string(c.first)] = ojson{{"startedAt", c.second.started_at}, {"open", c.second.open}};
    out["_tracks"] = tr;
    store::write_file_atomic(store::join_path(dir_, r.j["id"].get<std::string>() + ".json"), dump(out), false);
}

bool RunsBook::track(const ctl::Snapshot& s, const Limits& lim, int64_t now) {
    std::lock_guard<std::mutex> lk(mu_);
    bool changed = false;
    for (int ch = 1; ch <= 8; ++ch) {
        const ojson& run = s.program_runs[static_cast<size_t>(ch - 1)];
        if (!run.is_object()) continue;
        std::string started = run.value("startedAt", "");
        if (started.empty()) continue;
        bool running = run.value("running", false);
        // 已经有这一次（同一路、同一 startedAt）的记录？
        Rec* rec = nullptr;
        for (auto& kv : runs_) {
            auto it = kv.second.chs.find(ch);
            if (it != kv.second.chs.end() && it->second.started_at == started) {
                rec = &kv.second;
                break;
            }
        }
        if (!rec) {
            if (!running) continue;   // 开机前就结束了的不补记
            int64_t st = parse_utc(run["startedAt"]).value_or(now);
            // 同时开始（≤ 10 s）且还开着的运行：并进去
            for (auto& kv : runs_) {
                if (!kv.second.j["end"].is_null()) continue;
                if (std::llabs(kv.second.start_ms - st) <= 10000 && !kv.second.chs.count(ch)) {
                    rec = &kv.second;
                    break;
                }
            }
            if (!rec) {
                std::string base = "run-" + store::fmt_wall(store::WallTime::local(st), "yyyyMMdd-HHmm");
                int n = 1;
                while (runs_.count(base + "-" + std::to_string(n))) ++n;
                Rec r;
                r.start_ms = st;
                std::string id = base + "-" + std::to_string(n);
                ojson steps = run.value("steps", ojson::array());
                r.j = ojson{{"id", id},
                            {"programName", run.value("name", std::string("程序"))},
                            {"result", "running"},
                            {"resultText", "运行中"},
                            {"start", utc_iso(st)},
                            {"end", nullptr},
                            {"plannedSec", jnum(std::round(planned_sec(steps)))},
                            {"channels", ojson::array()},
                            {"endReason", ""},
                            {"avgMmol", nullptr},
                            {"rsdPct", nullptr},
                            {"tempDevC", nullptr},
                            {"pressPsi", nullptr},
                            {"gasStart", nullptr},
                            {"perCh", ojson::array()},
                            {"abort", nullptr},
                            {"note", ""},
                            {"program", ojson{{"name", run.value("name", std::string("程序"))}, {"steps", steps}}},
                            {"stepMinutes", ojson::array()}};
                for (size_t k = 0; k < steps.size(); ++k) {
                    r.j["stepMinutes"].push_back(0);
                    const auto& stp = steps[k];
                    if (r.j["pressPsi"].is_null() && stp.value("type", "") == "press" && stp.contains("params") &&
                        stp["params"].contains("pres") && stp["params"]["pres"].is_number())
                        r.j["pressPsi"] = stp["params"]["pres"];
                }
                runs_[id] = std::move(r);
                rec = &runs_[id];
            }
            ChTrack t;
            t.started_at = started;
            rec->chs[ch] = t;
            rec->j["channels"].push_back(ch);
            rec->j["perCh"].push_back(ojson{{"ch", ch}, {"mmol", nullptr}, {"maxRate", 0}, {"tempC", nullptr}, {"flagged", false}});
            save(*rec);
            changed = true;
        }
        auto& t = rec->chs[ch];
        const auto& c = s.channels[static_cast<size_t>(ch - 1)];
        if (t.open && c.gas_rate) t.max_rate = std::max(t.max_rate, *c.gas_rate);
        if (t.open && !running) {
            finish_channel(*rec, ch, run, c, lim, now);
            bool all = true;
            for (const auto& x : rec->chs) all = all && !x.second.open;
            if (all) finish_run(*rec, now);
            save(*rec);
            changed = true;
        }
    }
    return changed;
}

void RunsBook::finish_channel(Rec& r, int ch, const ojson& run, const ctl::ChannelSnap& c, const Limits& lim, int64_t now) {
    auto& t = r.chs[ch];
    t.open = false;
    std::string state = run.value("state", "");
    std::string reason = run.value("endReason", ojson("")).is_string() ? run.value("endReason", std::string()) : std::string();
    for (auto& p : r.j["perCh"]) {
        if (p["ch"].get<int>() != ch) continue;
        p["mmol"] = opt_num(c.gas_consumed);
        p["maxRate"] = jnum(t.max_rate);
        p["tempC"] = opt_num(c.temp_c);
        p["flagged"] = state != "completed" && state != "已完成";
        p["_state"] = state;
    }
    if (!reason.empty()) r.j["endReason"] = reason;
    bool failed = state.find("fail") != std::string::npos || state.find("abort") != std::string::npos || state == "已中止" || state == "失败";
    if (failed && r.j["abort"].is_null()) {
        std::string quantity = reason.find("超压") != std::string::npos ? "pressure" : reason.find("超温") != std::string::npos ? "temp" : "";
        bool pr = quantity == "pressure";
        r.j["abort"] = ojson{{"ch", ch},
                             {"quantity", quantity},
                             {"value", pr ? opt_num(c.pressure_psi) : opt_num(c.temp_c)},
                             {"setpoint", pr ? jnum(c.pressure_sp_psi) : jnum(c.temp_sp_c)},
                             {"limit", quantity.empty() ? ojson(nullptr) : jnum(pr ? lim.over_pressure : lim.over_temp)},
                             {"rangeHi", quantity.empty() ? ojson(nullptr) : jnum(pr ? lim.full_scale_psi : 400.0)},
                             {"at", utc_iso(now)},
                             {"title", quantity == "pressure" ? "超压，程序自动停止"
                                       : quantity == "temp"   ? "超温，程序自动停止"
                                                              : "程序已中止"},
                             {"did", reason.empty() ? "已停止加热、关闭进气阀" : reason}};
    }
}

void RunsBook::finish_run(Rec& r, int64_t now) {
    r.j["end"] = utc_iso(now);
    std::vector<double> mm, tc;
    bool any_fail = false;
    for (auto& p : r.j["perCh"]) {
        if (p["mmol"].is_number()) mm.push_back(p["mmol"].get<double>());
        if (p["tempC"].is_number()) tc.push_back(p["tempC"].get<double>());
        any_fail = any_fail || p.value("flagged", false);
        p.erase("_state");
    }
    auto mean = [](const std::vector<double>& v) {
        double s = 0;
        for (double x : v) s += x;
        return v.empty() ? 0.0 : s / static_cast<double>(v.size());
    };
    if (!mm.empty()) {
        double m = mean(mm), var = 0;
        for (double x : mm) var += (x - m) * (x - m);
        double sd = mm.size() > 1 ? std::sqrt(var / static_cast<double>(mm.size() - 1)) : 0;
        r.j["avgMmol"] = jnum(std::round(m * 1000) / 1000);
        r.j["rsdPct"] = m > 0 ? jnum(std::round(sd / m * 1000) / 10) : ojson(nullptr);
    }
    if (!tc.empty()) {
        double m = mean(tc), dev = 0;
        for (double x : tc) dev = std::max(dev, std::fabs(x - m));
        r.j["tempDevC"] = jnum(std::round(dev * 10) / 10);
    }
    if (!r.j["abort"].is_null()) {
        r.j["result"] = "abort";
        const auto& a = r.j["abort"];
        std::string q = a.value("quantity", "");
        r.j["resultText"] = q == "pressure" ? std::to_string(a["ch"].get<int>()) + " 号超压"
                            : q == "temp"   ? std::to_string(a["ch"].get<int>()) + " 号超温"
                                            : "已中止";
    } else if (any_fail) {
        r.j["result"] = "warn";
        r.j["resultText"] = "完成（有警告）";
    } else {
        r.j["result"] = "done";
        r.j["resultText"] = !mm.empty() && mean(mm) > 0 ? core::fmt_dec(mean(mm), 2, 2) + " mmol" : "完成";
    }
}

void RunsBook::on_step(int rv, int index, const std::string& state, int64_t now) {
    std::lock_guard<std::mutex> lk(mu_);
    for (auto& kv : runs_) {
        auto it = kv.second.chs.find(rv);
        if (it == kv.second.chs.end() || !it->second.open) continue;
        auto& r = kv.second;
        if (state == "running") {
            // 同一步会因为「等待气路 ↔ 正在执行」反复报 running：只记第一次（原来每次都覆盖，跑完的步骤用时全是 0）
            it->second.step_start.emplace(index, now);
            auto& steps = r.j["program"]["steps"];
            if (index >= 0 && static_cast<size_t>(index) < steps.size() && steps[static_cast<size_t>(index)].value("type", "") == "press" &&
                r.j["gasStart"].is_null())
                r.j["gasStart"] = utc_iso(now);
        } else if (state == "completed" || state == "failed" || state == "aborted") {
            auto s = it->second.step_start.find(index);
            if (s != it->second.step_start.end() && index >= 0 && static_cast<size_t>(index) < r.j["stepMinutes"].size()) {
                double m = std::round(static_cast<double>(now - s->second) / 60000.0 * 10) / 10;
                double cur = r.j["stepMinutes"][static_cast<size_t>(index)].get<double>();
                r.j["stepMinutes"][static_cast<size_t>(index)] = jnum(std::max(cur, m));
            }
        }
        return;
    }
}

std::vector<ojson> RunsBook::leak_runs() const {
    std::vector<ojson> out;
    std::map<std::string, std::string> notes;
    {
        std::lock_guard<std::mutex> lk(mu_);
        notes = leak_notes_;
    }
    std::string dir = store::join_path(data_dir_, "leak");
    const std::string suffix = "-summary.json";
    for (const auto& name : store::list_files(dir)) {
        if (name.size() <= suffix.size() || name.compare(name.size() - suffix.size(), suffix.size(), suffix) != 0) continue;
        std::string run = name.substr(0, name.size() - suffix.size());
        if (!store::safe_path::is_leak_run_id(run)) continue;
        std::string text;
        if (!store::read_file(store::join_path(dir, name), text)) continue;
        if (text.size() >= 3 && text.compare(0, 3, store::kUtf8Bom) == 0) text.erase(0, 3);
        auto j = ojson::parse(text, nullptr, false);
        if (!j.is_object()) continue;
        ojson chans = ojson::array();
        int pass = 0, total = 0;
        if (j.contains("channels") && j["channels"].is_array())
            for (const auto& c : j["channels"]) {
                int rv = c["Rv"].is_number() ? c["Rv"].get<int>() : 0;
                if (rv) chans.push_back(rv);
                ++total;
                if (c.value("Status", "") == "ok") ++pass;
            }
        auto start = parse_local_stamp(run.substr(5));
        auto st = store::stat_path(store::join_path(dir, name));
        bool aborted = j.contains("parameters") && j["parameters"].value("aborted", false);
        out.push_back(ojson{{"id", run},
                            {"programName", "泄漏测试"},
                            {"result", "leak"},
                            {"resultText", aborted ? std::string("已中止") : std::to_string(pass) + "/" + std::to_string(total) + " 通过"},
                            {"start", opt_utc(start)},
                            {"end", utc_iso(st.mtime_ms)},
                            {"plannedSec", nullptr},
                            {"channels", chans},
                            {"endReason", aborted ? "泄漏测试已中止" : ""},
                            {"avgMmol", nullptr},
                            {"rsdPct", nullptr},
                            {"tempDevC", nullptr},
                            {"pressPsi", j.contains("parameters") ? j["parameters"].value("testPsi", ojson()) : ojson()},
                            {"gasStart", nullptr},
                            {"perCh", ojson::array()},
                            {"abort", nullptr},
                            {"note", notes.count(run) ? notes[run] : std::string()}});
    }
    return out;
}

ojson RunsBook::list(int limit) const {
    std::vector<ojson> items;
    {
        std::lock_guard<std::mutex> lk(mu_);
        for (const auto& kv : runs_) {
            ojson j = kv.second.j;
            j.erase("program");
            j.erase("stepMinutes");
            items.push_back(j);
        }
    }
    for (auto& l : leak_runs()) items.push_back(l);
    std::sort(items.begin(), items.end(), [](const ojson& a, const ojson& b) {
        std::string sa = a.value("start", ojson("")).is_string() ? a["start"].get<std::string>() : "";
        std::string sb = b.value("start", ojson("")).is_string() ? b["start"].get<std::string>() : "";
        return sa > sb;
    });
    ojson arr = ojson::array();
    for (size_t i = 0; i < items.size() && static_cast<int>(i) < limit; ++i) arr.push_back(items[i]);
    return ojson{{"count", arr.size()}, {"items", arr}};
}

std::optional<ojson> RunsBook::get(const std::string& id) const {
    {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = runs_.find(id);
        if (it != runs_.end()) return it->second.j;
    }
    for (auto& l : leak_runs())
        if (l["id"] == id) {
            l["program"] = nullptr;
            l["stepMinutes"] = ojson::array();
            return l;
        }
    return std::nullopt;
}

bool RunsBook::set_note(const std::string& id, const std::string& note) {
    std::lock_guard<std::mutex> lk(mu_);
    auto it = runs_.find(id);
    if (it == runs_.end()) {
        // 泄漏测试的运行：建一条只存备注的记录
        if (!store::safe_path::is_leak_run_id(id) || !safe_id(id)) return false;
        if (!store::file_exists(store::join_path(store::join_path(data_dir_, "leak"), id + "-summary.json"))) return false;
        leak_notes_[id] = note;
        store::make_dirs(dir_);
        store::write_file_atomic(store::join_path(dir_, id + ".json"), dump(ojson{{"id", id}, {"note", note}, {"_leakNote", true}}), false);
        return true;
    }
    it->second.j["note"] = note;
    save(it->second);
    return true;
}

std::optional<RunsBook::Range> RunsBook::range(const std::string& id, int64_t now) const {
    std::lock_guard<std::mutex> lk(mu_);
    auto it = runs_.find(id);
    if (it == runs_.end()) return std::nullopt;
    Range r;
    r.start = it->second.start_ms;
    r.end = parse_utc(it->second.j.value("end", ojson())).value_or(now);
    for (const auto& c : it->second.j["channels"]) r.channels.push_back(c.get<int>());
    return r;
}

}  // namespace reactor::api
