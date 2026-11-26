// 只读端点（GET，任何角色）。C#: Api/Endpoints.cs、Api/Endpoints.Sessions.cs（GET 部分）、Api/Hmi/HmiApiSources.cs
// 除 /preflight、/alarms、草稿外都只读快照 / 事件存储 / 文件，不进控制线程。
#include <algorithm>
#include "core/chan_name.hpp"

#include "api/api_routes.hpp"
#include "store/history_files.hpp"
#include "store/store_util.hpp"
#include <cctype>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::api {

namespace {

using net::Request;
using net::Response;
using net::RouteOptions;
using net::RateClass;

Response channel_not_found(const std::string& id) {
    return api_error("CHANNEL_NOT_FOUND", "通道 " + id + " 不存在（有效范围 1–8）");
}

std::string content_type_of(const std::string& name) {
    auto ends = [&](const char* suf) {
        std::string s(suf);
        if (name.size() < s.size()) return false;
        for (size_t i = 0; i < s.size(); ++i)
            if (std::tolower(static_cast<unsigned char>(name[name.size() - s.size() + i])) != s[i]) return false;
        return true;
    };
    if (ends(".json")) return "application/json; charset=utf-8";
    if (ends(".jsonl")) return "application/x-ndjson; charset=utf-8";
    return "text/csv; charset=utf-8";
}

Response file_result(const Request& q, const std::string& data_dir, const std::string& type, const std::string& name) {
    auto path = store::safe_path::try_resolve(data_dir, type, name);
    if (!path || !store::file_exists(*path)) return api_error("NOT_FOUND", "文件不存在或名称不合法");
    return net::file_response(q, *path, content_type_of(name), name);
}

ojson file_info_json(const store::HistoryFileInfo& f) {
    return ojson{{"name", f.name}, {"type", f.type}, {"bytes", f.bytes}, {"modifiedAt", utc_iso(f.modified_at_ms)},
                 {"from", opt_utc(f.from_ms)}, {"to", opt_utc(f.to_ms)}, {"day", opt_str(f.day)}};
}

// C# SessionId 正则：^(?!\.)[^/\\\0:*?"<>|\s]{1,200}\z，且不含 ".."
bool valid_session_id(const std::string& id) {
    if (id.empty() || id[0] == '.' || id.find("..") != std::string::npos || utf16_len(id) > 200) return false;
    for (unsigned char c : id)
        if (c == '/' || c == '\\' || c == 0 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ||
            c == ' ' || (c >= 0x09 && c <= 0x0D))
            return false;
    // 全角空格等 Unicode 空白
    if (id.find("\xE3\x80\x80") != std::string::npos || id.find("\xC2\xA0") != std::string::npos) return false;
    return true;
}

std::vector<std::string> session_files(const std::string& data_dir, const std::string& id) {
    std::string dir = store::join_path(data_dir, "sessions");
    std::vector<std::string> out;
    for (const auto& n : store::list_files(dir)) {
        bool match = n == id + ".json" || n == id + ".csv";
        if (!match && n.size() > id.size() + 5 && n.compare(0, id.size() + 1, id + "-") == 0 && n.size() >= 4 &&
            n.compare(n.size() - 4, 4, ".csv") == 0) {
            std::string mid = n.substr(id.size() + 1, n.size() - id.size() - 5);
            match = !mid.empty() && mid.size() <= 3 && std::all_of(mid.begin(), mid.end(), [](char c) { return c >= '0' && c <= '9'; });
        }
        if (match) out.push_back("sessions/" + n);
    }
    std::sort(out.begin(), out.end());
    return out;
}

ojson manifest_json(const store::SessionManifest& m) {
    ojson j = ojson::parse(m.to_json(), nullptr, false);
    return j.is_discarded() ? ojson(nullptr) : j;
}

ojson strings(const std::vector<std::string>& v) {
    ojson a = ojson::array();
    for (const auto& s : v) a.push_back(s);
    return a;
}

}  // namespace

void Routes::read(ApiService& s, net::Router& r) {
    ApiService* self = &s;
    const std::string P = "/api/v1";

    // —— 系统 ——
    r.get(P + "/health", [self](const Request&) {
        return ok_json(ojson{{"ok", true}, {"apiVersion", kApiVersion}, {"simulated", self->d_.hw && self->d_.hw->simulated},
                             {"bootId", self->boot_id_}, {"serverTime", utc_iso(self->now_wall())}});
    }, RouteOptions{RateClass::None, true, false});

    r.get(P + "/device", [self](const Request&) { return ok_json(self->device_json()); });

    r.get(P + "/snapshot", [self](const Request&) {
        auto b = self->snap();
        return b ? ok_json(self->augment_frame(*b)) : starting_response();
    });

    r.get(P + "/preflight", [self](const Request& q) {
        cmd::Source src = q.auth.local ? cmd::Source::local() : cmd::Source::remote(q.auth.name, q.auth.name);
        auto j = self->on_control<ojson>([self, src] { return self->commands_.preflight(src); });
        if (!j) return control_timeout_response();
        return ok_json(*j);
    }, RouteOptions{RateClass::Preflight, false, false});

    r.get(P + "/device/comm", [self](const Request&) {
        auto b = self->snap();
        ojson api;
        if (self->srv_) {
            auto& st = self->srv_->stats();
            api = ojson{{"clients", st.clients.load()},
                        {"maxClients", self->d_.cfg.max_clients},
                        {"droppedTelemetryFrames", st.dropped_telemetry_frames.load()},
                        {"droppedEvents", st.dropped_events.load()},
                        {"framesSent", st.frames_sent.load()},
                        {"eventsSent", st.events_sent.load()},
                        {"authFailures", st.auth_failures.load()},
                        {"requests", st.requests.load()},
                        {"startedAt", self->started_at_ms_ ? ojson(utc_iso(self->started_at_ms_)) : ojson(nullptr)},
                        {"bind", self->d_.cfg.bind},
                        {"port", self->srv_->tcp_port()}};
        } else {
            api = ojson{{"clients", 0}, {"maxClients", self->d_.cfg.max_clients}, {"droppedTelemetryFrames", 0}, {"droppedEvents", 0},
                        {"framesSent", 0}, {"eventsSent", 0}, {"authFailures", 0}, {"requests", 0}, {"startedAt", nullptr},
                        {"bind", self->d_.cfg.bind}, {"port", 0}};
        }
        auto* ev = self->d_.events;
        ojson j;
        j["ts"] = utc_iso(self->now_wall());
        j["snapshotSeq"] = b ? b->seq : 0;
        j["temp"] = b ? ctl::module_comm_to_json(b->temp_comm) : ojson(nullptr);
        j["pressure"] = b ? ctl::module_comm_to_json(b->press_comm) : ojson(nullptr);
        j["stir"] = b ? ctl::module_comm_to_json(b->stir_comm) : ojson(nullptr);
        j["io"] = b ? ctl::module_comm_to_json(b->io_comm) : ojson(nullptr);
        j["tick"] = b ? ctl::tick_stats_to_json(b->tick) : ojson(nullptr);
        j["api"] = api;
        j["logger"] = ojson{{"droppedLines", b ? b->logger_dropped_lines : 0},
                            {"eventDroppedLines", ev->dropped_lines()},
                            {"eventSeqHead", ev->head()},
                            {"eventRingOldest", ev->oldest_buffered()}};
        // 接口契约 §3.13
        j["pollSec"] = 1.2;
        j["offlineAfter"] = 3;
        ojson mods = ojson::array();
        struct M { const char *id, *name, *model, *detail; };
        const M ms[] = {{"temp", "温控", "宇电 AI-8", "8 路 PID · 1 拖 8"}, {"pressure", "压力", "8AI", "8 路压力变送器"},
                        {"io", "阀门", "IO16R", "8 路进气阀 + 3 路气源 + 排空"}, {"stir", "搅拌", "DM2C", "步进驱动 · 全部反应釜共用"}};
        for (const auto& m : ms) {
            std::string id = m.id;
            const ctl::ModuleCommSnap* mc = !b ? nullptr : id == "temp" ? &b->temp_comm : id == "pressure" ? &b->press_comm
                                                                       : id == "io"       ? &b->io_comm
                                                                                          : &b->stir_comm;
            ojson port = nullptr, baud = nullptr;
            if (auto* hw = self->d_.hw) {
                const auto& bc = id == "temp" ? hw->cfg.temp : id == "pressure" ? hw->cfg.press : id == "io" ? hw->cfg.io : hw->cfg.stir;
                port = hw->simulated ? ojson("sim") : bc.port.empty() ? ojson(nullptr) : ojson(bc.port);
                baud = bc.baud;
            }
            ojson mj = mc ? ctl::module_comm_to_json(*mc) : ojson::object();
            mods.push_back(ojson{{"id", id}, {"name", m.name}, {"model", m.model}, {"detail", m.detail},
                                 {"configured", mc ? mc->configured : false}, {"online", mc ? mc->online : false},
                                 {"port", port}, {"baud", baud}, {"polls", mc ? mc->polls : 0}, {"fails", mc ? mc->fails : 0},
                                 {"lastOkAt", mj.value("lastOkAt", ojson())}, {"offlineSince", nullptr}, {"failStreak", nullptr}});
        }
        j["modules"] = mods;
        return ok_json(j);
    });

    // —— 通道 ——
    r.get(P + "/channels", [self](const Request&) {
        auto b = self->snap();
        if (!b) return starting_response();
        ojson frame = self->augment_frame(*b);
        ojson inst = ojson::array();
        for (int x : b->installed) inst.push_back(x);
        return ok_json(ojson{{"seq", b->seq}, {"ts", utc_iso(b->ts_ms)}, {"installedChannels", inst}, {"channels", frame["channels"]}});
    });

    r.get(P + "/channels/{id}", [self](const Request& q) {
        std::string id = q.param("id");
        auto n = parse_channel(id);
        if (!n) return channel_not_found(id);
        auto b = self->snap();
        if (!b) return starting_response();
        return ok_json(ojson{{"seq", b->seq}, {"ts", utc_iso(b->ts_ms)},
                             {"channel", ctl::channel_to_json(b->channels[static_cast<size_t>(*n - 1)], true)}});
    });

    r.get(P + "/channels/{id}/program/run", [self](const Request& q) {
        std::string id = q.param("id");
        auto n = parse_channel(id);
        if (!n) return channel_not_found(id);
        auto b = self->snap();
        if (!b) return starting_response();
        const auto& run = b->program_runs[static_cast<size_t>(*n - 1)];
        if (run.is_null()) return api_error("NOT_FOUND", core::chan_name(static_cast<int>(*n)) + " 本次开机以来没有执行过程序");
        return ok_json(ojson{{"seq", b->seq}, {"ts", utc_iso(b->ts_ms)}, {"run", run}});
    });

    // 接口契约 §3.3：这一路当前的程序（运行中 = 下发时的副本），§6.9 格式（psi）
    r.get(P + "/channels/{id}/program", [self](const Request& q) {
        std::string id = q.param("id");
        auto n = parse_channel(id);
        if (!n) return channel_not_found(id);
        int ch = *n;
        auto b = self->snap();
        if (!b) return starting_response();
        ApiService::ProgInfo info;
        {
            std::lock_guard<std::mutex> lk(self->prog_mu_);
            info = self->prog_[static_cast<size_t>(ch - 1)];
        }
        const ojson& run = b->program_runs[static_cast<size_t>(ch - 1)];
        ojson steps, name;
        if (run.is_object() && run.value("running", false)) {
            steps = run.value("steps", ojson::array());
            name = run.value("name", ojson(info.name));
        } else {
            auto d = self->on_control<ojson>([self, ch] { return self->d_.automation->programs().draft_json(ch); });
            if (!d) return control_timeout_response();
            steps = d->value("steps", ojson::array());
            name = d->contains("name") && (*d)["name"].is_string() && !(*d)["name"].get<std::string>().empty() ? (*d)["name"] : ojson(info.name);
        }
        return ok_json(ojson{{"seq", b->seq}, {"ts", utc_iso(b->ts_ms)}, {"channel", ch}, {"revision", info.revision}, {"name", name},
                             {"sourceId", opt_str(info.source_id)}, {"modified", opt_utc(info.modified_ms)}, {"steps", steps}});
    });

    r.get(P + "/pid", [self](const Request&) {
        auto b = self->snap();
        return b ? ok_json(self->pid_json(*b)) : starting_response();
    });

    // —— 事件 / 报警 ——
    r.get(P + "/events", [self](const Request& q) {
        std::optional<int64_t> after;
        std::optional<int> limit;
        if (q.has_query("afterSeq") && !q.query_param("afterSeq").empty()) {
            try {
                size_t pos = 0;
                after = std::stoll(q.query_param("afterSeq"), &pos);
                if (pos != q.query_param("afterSeq").size()) throw std::invalid_argument("x");
            } catch (...) {
                return api_error("BAD_REQUEST", "afterSeq 须为整数");
            }
        }
        if (q.has_query("limit") && !q.query_param("limit").empty()) {
            try {
                size_t pos = 0;
                limit = std::stoi(q.query_param("limit"), &pos);
                if (pos != q.query_param("limit").size()) throw std::invalid_argument("x");
            } catch (...) {
                return api_error("BAD_REQUEST", "limit 须为整数");
            }
        }
        auto page = self->d_.events->query(after, limit);
        std::string items;
        for (const auto& e : page.items) {
            if (!items.empty()) items += ',';
            items += store::event_to_json(e);
        }
        // C# EventsPageDto(seqEpoch, eventSeqHead, afterSeq, count, more, items)
        std::string body = "{\"seqEpoch\":" + std::to_string(page.seq_epoch) + ",\"eventSeqHead\":" + std::to_string(page.head) +
                           ",\"afterSeq\":" + std::to_string(page.after_seq) + ",\"count\":" + std::to_string(page.count) +
                           ",\"more\":" + (page.has_more ? "true" : "false") + ",\"items\":[" + items + "]}";
        return json_response(200, body);
    });

    r.get(P + "/alarms", [self](const Request& q) {
        std::string state = q.query_param("state");
        if (state.empty()) state = "all";
        auto items = self->on_control<std::vector<ctl::AlarmItem>>([self, state] { return self->d_.core->alarms().list(state); }, 2000);
        if (!items) {   // 控制线程忙：退回快照里的副本
            auto b = self->snap();
            if (!b) return starting_response();
            items = std::vector<ctl::AlarmItem>();
            for (const auto& a : b->alarms)
                if (state == "all" || a.state == state) items->push_back(a);
        }
        ojson arr = ojson::array();
        auto cur = self->snap();
        for (const auto& a : *items) arr.push_back(self->alarm_json(a, cur.get()));
        return ok_json(ojson{{"count", arr.size()}, {"items", arr}});
    });

    // —— 设置 / 程序 schema ——
    r.get(P + "/settings", [self](const Request&) {
        auto b = self->snap();
        return b ? ok_json(self->settings_json(b->settings)) : starting_response();
    });
    r.get(P + "/program-schema", [self](const Request&) { return json_response(200, self->schema_json()); });

    // —— 历史与文件 ——
    r.get(P + "/files", [self](const Request& q) {
        std::string type = q.query_param("type");
        const auto& types = store::safe_path::types();
        if (type.empty() || std::find(types.begin(), types.end(), type) == types.end()) {
            std::string all;
            for (const auto& t : types) all += (all.empty() ? "" : ",") + t;
            return api_error("BAD_REQUEST", "type 必须是 telemetry / events / sessions / leak 之一", ojson{{"types", all}});
        }
        auto items = store::history_files::list(self->d_.data_dir, type);
        ojson arr = ojson::array();
        for (const auto& f : items) {
            ojson j = file_info_json(f);
            j["desc"] = nullptr;   // 接口契约 §3.10：另起文件的原因（后端暂不记录）
            j["lines"] = nullptr;
            if (type == "events") {
                std::string text;
                if (auto pth = store::safe_path::try_resolve(self->d_.data_dir, type, f.name); pth && store::read_file(*pth, text)) {
                    int64_t n = std::count(text.begin(), text.end(), '\n');
                    j["lines"] = n > 0 ? n - 1 : 0;   // 减去表头
                }
            } else if (auto d = store::history_files::day_of(f.name); d && d->second > 0) {
                j["desc"] = "表头变了，另起第 " + std::to_string(d->second + 1) + " 个文件";
            }
            arr.push_back(j);
        }
        return ok_json(ojson{{"type", type}, {"count", arr.size()}, {"items", arr}});
    });

    r.get(P + "/files/{type}/{name}", [self](const Request& q) { return file_result(q, self->d_.data_dir, q.param("type"), q.param("name")); },
          RouteOptions{RateClass::Download, false, false});

    r.add("GET", P + "/telemetry", [self](const Request& q, net::Responder res) {
        int64_t f = 0, t = 0;
        if (!store::history_files::try_parse_query_time(q.query_param("from"), f) ||
            !store::history_files::try_parse_query_time(q.query_param("to"), t)) {
            res.send(api_error("BAD_REQUEST", "from / to 必须是带时区偏移或 Z 的 ISO-8601 时间，例如 2026-11-06T08:00:00Z"));
            return;
        }
        if (t <= f) {
            res.send(api_error("BAD_REQUEST", "to 必须晚于 from（半开区间 [from, to)）"));
            return;
        }
        auto files = store::history_files::select_daily_files(self->d_.data_dir, f, t);
        auto stamp = [](int64_t ms) {
            std::string s = utc_iso(ms);   // 2026-11-06T08:00:00Z / …00.5Z
            std::string o;
            for (char c : s.substr(0, 19))
                if (c != '-' && c != ':') o += c;
            return o + "Z";
        };
        net::Headers h;
        h.set("Content-Type", "text/csv; charset=utf-8");
        h.set("Cache-Control", "no-store");
        h.set("Content-Disposition", "attachment; filename=telemetry-" + stamp(f) + "-" + stamp(t) + ".csv");
        auto w = res.stream(200, std::move(h));
        if (!w) return;
        int64_t n = store::history_files::write_range(files, f, t, [&w](const std::string& chunk) { return w->write(chunk); });
        if (n >= 0) w->end();
    }, RouteOptions{RateClass::Download, false, false});

    r.get(P + "/leak-tests", [self](const Request&) {
        std::string dir = store::join_path(self->d_.data_dir, "leak");
        std::vector<ojson> items;
        const std::string suffix = "-summary.json";
        for (const auto& name : store::list_files(dir)) {
            if (name.size() <= suffix.size() || name.compare(name.size() - suffix.size(), suffix.size(), suffix) != 0) continue;
            std::string run = name.substr(0, name.size() - suffix.size());
            if (!store::safe_path::is_leak_run_id(run)) continue;
            auto start = parse_local_stamp(run.substr(5));
            auto st = store::stat_path(store::join_path(dir, name));
            auto rel = [&](const std::string& f) {
                return store::file_exists(store::join_path(dir, f)) ? ojson("leak/" + f) : ojson(nullptr);
            };
            // 接口契约 §3.11：测了哪几路、是否全部通过、备注
            ojson chs = ojson::array(), pass = nullptr;
            std::string text;
            if (store::read_file(store::join_path(dir, name), text)) {
                if (text.compare(0, 3, store::kUtf8Bom) == 0) text.erase(0, 3);
                auto sj = ojson::parse(text, nullptr, false);
                if (sj.is_object() && sj.contains("channels") && sj["channels"].is_array()) {
                    bool all = true, done = !(sj.contains("parameters") && sj["parameters"].value("aborted", false));
                    for (const auto& c : sj["channels"]) {
                        if (c["Rv"].is_number()) chs.push_back(c["Rv"]);
                        std::string stv = c.value("Status", "");
                        if (stv.empty()) done = false;
                        all = all && stv == "ok";
                    }
                    if (done) pass = all;
                }
            }
            std::string note;
            if (auto rr = self->runs_->get(run)) note = rr->value("note", "");
            items.push_back(ojson{{"runId", run}, {"start", opt_utc(start)}, {"bytes", st.size},
                                  {"files", ojson{{"samples", rel(run + ".csv")}, {"summaryCsv", rel(run + "-summary.csv")},
                                                  {"summaryJson", rel(run + "-summary.json")}}},
                                  {"channels", chs}, {"pass", pass}, {"note", note}});
        }
        std::sort(items.begin(), items.end(),
                  [](const ojson& a, const ojson& b) { return a["runId"].get<std::string>() > b["runId"].get<std::string>(); });
        ojson arr = ojson::array();
        for (auto& i : items) arr.push_back(i);
        return ok_json(ojson{{"count", arr.size()}, {"items", arr}});
    });

    auto leak_file = [self](const std::string& suffix) {
        return [self, suffix](const Request& q) {
            std::string run = q.param("runId");
            if (!store::safe_path::is_leak_run_id(run)) return api_error("NOT_FOUND", "runId 格式应为 leak-yyyyMMdd-HHmmss");
            return file_result(q, self->d_.data_dir, "leak", run + suffix);
        };
    };
    r.get(P + "/leak-tests/{runId}", leak_file("-summary.json"));
    r.get(P + "/leak-tests/{runId}/data.csv", leak_file(".csv"), RouteOptions{RateClass::Download, false, false});
    r.get(P + "/leak-tests/{runId}/summary.csv", leak_file("-summary.csv"), RouteOptions{RateClass::Download, false, false});

    // —— 会话（GET） ——
    r.get(P + "/sessions", [self](const Request&) {
        auto files = store::history_files::list(self->d_.data_dir, "sessions");
        std::vector<ojson> items;
        for (const auto& f : files) {
            auto p = store::history_files::parse_session_name(f.name);
            std::optional<int64_t> started;
            if (p) started = parse_local_stamp(p->second);
            std::string stem = f.name;
            auto dot = stem.rfind('.');
            if (dot != std::string::npos) stem = stem.substr(0, dot);
            items.push_back(ojson{{"testId", p ? p->first : f.name}, {"startedAt", opt_utc(started)}, {"file", f.name}, {"bytes", f.bytes},
                                  {"from", opt_utc(f.from_ms)}, {"to", opt_utc(f.to_ms)}, {"id", stem}});
        }
        std::sort(items.begin(), items.end(),
                  [](const ojson& a, const ojson& b) { return a["file"].get<std::string>() > b["file"].get<std::string>(); });
        ojson arr = ojson::array();
        for (auto& i : items) arr.push_back(i);
        auto cur = self->d_.sessions ? self->d_.sessions->current() : std::nullopt;
        ojson current{{"active", cur.has_value()}, {"id", cur ? ojson(cur->id) : ojson(nullptr)},
                      {"testId", cur ? ojson(cur->test_id) : ojson(nullptr)}, {"file", cur ? ojson(cur->file) : ojson(nullptr)}};
        return ok_json(ojson{{"count", arr.size()}, {"items", arr}, {"current", current}});
    });

    r.get(P + "/sessions/current", [self](const Request&) {
        auto m = self->d_.sessions ? self->d_.sessions->current() : std::nullopt;
        if (!m) return api_error("NOT_FOUND", "当前没有活动会话");
        return ok_json(ojson{{"active", true}, {"id", m->id}, {"testId", m->test_id}, {"file", m->file}, {"manifest", manifest_json(*m)},
                             {"files", strings(session_files(self->d_.data_dir, m->id))}});
    });

    r.get(P + "/sessions/{id}", [self](const Request& q) {
        std::string id = q.param("id");
        if (!valid_session_id(id)) return api_error("NOT_FOUND", "会话 id 不合法");
        auto cur = self->d_.sessions ? self->d_.sessions->current() : std::nullopt;
        if (cur && cur->id == id)
            return ok_json(ojson{{"active", true}, {"id", cur->id}, {"testId", cur->test_id}, {"file", cur->file},
                                 {"manifest", manifest_json(*cur)}, {"files", strings(session_files(self->d_.data_dir, id))}});
        auto m = store::SessionManifest::load(store::join_path(store::join_path(self->d_.data_dir, "sessions"), id + ".json"));
        auto files = session_files(self->d_.data_dir, id);
        if (!m && files.empty()) return api_error("NOT_FOUND", "会话 " + id + " 不存在");
        auto p = store::history_files::parse_session_name(id + ".csv");
        ojson file = nullptr;
        if (m) file = m->file;
        else
            for (const auto& f : files)
                if (f.size() > 4 && f.compare(f.size() - 4, 4, ".csv") == 0) { file = store::base_name(f); break; }
        ojson test_id = m ? ojson(m->test_id) : p ? ojson(p->first) : ojson(nullptr);
        return ok_json(ojson{{"active", false}, {"id", id}, {"testId", test_id}, {"file", file},
                             {"manifest", m ? manifest_json(*m) : ojson(nullptr)}, {"files", strings(files)}});
    });
}

}  // namespace reactor::api
