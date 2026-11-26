#include "store/session.hpp"

#include "store/store_util.hpp"
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

using nlohmann::json;

namespace {

ojson opt_str(const std::optional<std::string>& s) { return s ? ojson(*s) : ojson(nullptr); }

// 属性名不区分大小写（C# SessionJsonContext PropertyNameCaseInsensitive = true）
const json* field(const json& o, const char* name) {
    auto it = o.find(name);
    if (it != o.end()) return &*it;
    for (auto i = o.begin(); i != o.end(); ++i)
        if (iequals_ascii(i.key(), name)) return &*i;
    return nullptr;
}

struct Bad {};

std::string get_str(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return "";
    if (!f->is_string()) throw Bad{};
    return f->get<std::string>();
}
std::optional<std::string> get_ostr(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return std::nullopt;
    if (!f->is_string()) throw Bad{};
    return f->get<std::string>();
}
int64_t get_int(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return 0;
    if (!f->is_number_integer()) throw Bad{};
    return f->get<int64_t>();
}
std::optional<int64_t> get_oint(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return std::nullopt;
    if (!f->is_number_integer()) throw Bad{};
    return f->get<int64_t>();
}
std::optional<int64_t> get_otime(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return std::nullopt;
    int64_t ms = 0;
    if (!f->is_string() || !parse_time_loose(f->get<std::string>(), ms)) throw Bad{};
    return ms;
}
int64_t get_time(const json& o, const char* n) { return get_otime(o, n).value_or(0); }
const json* get_arr(const json& o, const char* n) {
    auto* f = field(o, n);
    if (!f || f->is_null()) return nullptr;
    if (!f->is_array()) throw Bad{};
    return f;
}

}  // namespace

std::string SessionManifest::to_json() const {
    ojson o = ojson::object();
    o["id"] = id;
    o["testId"] = test_id;
    o["note"] = opt_str(note);
    o["startedAt"] = fmt_utc_json(started_at_ms);
    o["endedAt"] = ended_at_ms ? ojson(fmt_utc_json(*ended_at_ms)) : ojson(nullptr);
    o["state"] = state;
    o["source"] = source;
    o["client"] = opt_str(client);
    o["simulated"] = simulated;
    o["startEventSeq"] = start_event_seq;
    o["endEventSeq"] = end_event_seq ? ojson(*end_event_seq) : ojson(nullptr);
    o["file"] = file;
    ojson mk = ojson::array();
    for (auto& m : markers) {
        ojson x = ojson::object();
        x["id"] = m.id;
        x["ts"] = fmt_utc_json(m.ts_ms);
        x["seq"] = m.seq;
        x["text"] = m.text;
        x["source"] = m.source;
        x["client"] = opt_str(m.client);
        mk.push_back(x);
    }
    o["markers"] = mk;
    ojson pr = ojson::array();
    for (auto& p : program_runs) {
        ojson x = ojson::object();
        x["rv"] = p.rv;
        x["startedAt"] = fmt_utc_json(p.started_at_ms);
        x["startedBy"] = p.started_by;
        pr.push_back(x);
    }
    o["programRuns"] = pr;
    ojson lt = ojson::array();
    for (auto& l : leak_tests) {
        ojson x = ojson::object();
        x["runId"] = l.run_id;
        x["startedAt"] = fmt_utc_json(l.started_at_ms);
        lt.push_back(x);
    }
    o["leakTests"] = lt;
    return dump_json(o, 2);
}

std::optional<SessionManifest> SessionManifest::from_json(const std::string& text) {
    try {
        json o = json::parse(strip_bom(text), nullptr, true, /*ignore_comments=*/true);
        if (!o.is_object()) return std::nullopt;
        SessionManifest m;
        m.id = get_str(o, "id");
        m.test_id = get_str(o, "testId");
        m.note = get_ostr(o, "note");
        m.started_at_ms = get_time(o, "startedAt");
        m.ended_at_ms = get_otime(o, "endedAt");
        m.state = get_str(o, "state");
        m.source = get_str(o, "source");
        m.client = get_ostr(o, "client");
        if (auto* f = field(o, "simulated"); f && !f->is_null()) {
            if (!f->is_boolean()) return std::nullopt;
            m.simulated = f->get<bool>();
        }
        m.start_event_seq = get_int(o, "startEventSeq");
        m.end_event_seq = get_oint(o, "endEventSeq");
        m.file = get_str(o, "file");
        if (auto* a = get_arr(o, "markers"))
            for (auto& x : *a) {
                if (!x.is_object()) return std::nullopt;
                SessionMarker mk;
                mk.id = static_cast<int>(get_int(x, "id"));
                mk.ts_ms = get_time(x, "ts");
                mk.seq = get_int(x, "seq");
                mk.text = get_str(x, "text");
                mk.source = get_str(x, "source");
                mk.client = get_ostr(x, "client");
                m.markers.push_back(mk);
            }
        if (auto* a = get_arr(o, "programRuns"))
            for (auto& x : *a) {
                if (!x.is_object()) return std::nullopt;
                m.program_runs.push_back({static_cast<int>(get_int(x, "rv")), get_time(x, "startedAt"), get_str(x, "startedBy")});
            }
        if (auto* a = get_arr(o, "leakTests"))
            for (auto& x : *a) {
                if (!x.is_object()) return std::nullopt;
                m.leak_tests.push_back({get_str(x, "runId"), get_time(x, "startedAt")});
            }
        return m;
    } catch (...) {
        return std::nullopt;
    }
}

std::optional<SessionManifest> SessionManifest::load(const std::string& path) {
    std::string text;
    if (!file_exists(path) || !read_file(path, text)) return std::nullopt;
    return from_json(text);
}

// ============================ SessionBook ============================

SessionBook::SessionBook(std::function<void(const std::string&, const std::string&)> write) : write_(std::move(write)) {}

std::optional<SessionManifest> SessionBook::current() const {
    std::lock_guard<std::mutex> lk(mu_);
    return current_;
}

std::optional<std::string> SessionBook::current_id() const {
    std::lock_guard<std::mutex> lk(mu_);
    if (!current_) return std::nullopt;
    return current_->id;
}

void SessionBook::flush(const SessionManifest& m) {
    if (!write_) return;
    try { write_(SessionManifest::rel_path(m.id), m.to_json()); } catch (...) { /* 写盘失败由 DataLogger 上报 */ }
}

SessionManifest SessionBook::begin(const std::string& id, const std::string& test_id, const std::string& note, int64_t now,
                                   const std::string& source, const std::optional<std::string>& client, bool simulated,
                                   int64_t start_seq, const std::string& file) {
    SessionManifest m;
    m.id = id;
    m.test_id = test_id;
    std::string n = trim(note);
    if (!n.empty()) m.note = n;
    m.started_at_ms = now;
    m.state = SessionManifest::kActive;
    m.source = source;
    m.client = client;
    m.simulated = simulated;
    m.start_event_seq = start_seq;
    m.file = file;
    {
        std::lock_guard<std::mutex> lk(mu_);
        current_ = m;
    }
    flush(m);
    return m;
}

std::optional<SessionMarker> SessionBook::add_marker(const std::string& text, int64_t now, int64_t seq, const std::string& source,
                                                     const std::optional<std::string>& client) {
    SessionManifest m;
    SessionMarker mk;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!current_) return std::nullopt;
        mk = SessionMarker{static_cast<int>(current_->markers.size()) + 1, now, seq, text, source, client};
        current_->markers.push_back(mk);
        m = *current_;
    }
    flush(m);
    return mk;
}

void SessionBook::tag_program_run(int rv, int64_t now, const std::string& started_by) {
    SessionManifest m;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!current_) return;
        current_->program_runs.push_back({rv, now, started_by});
        m = *current_;
    }
    flush(m);
}

void SessionBook::tag_leak_test(const std::string& run_id, int64_t now) {
    SessionManifest m;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!current_) return;
        current_->leak_tests.push_back({run_id, now});
        m = *current_;
    }
    flush(m);
}

std::optional<SessionManifest> SessionBook::end(int64_t now, int64_t end_seq) {
    SessionManifest m;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!current_) return std::nullopt;
        m = *current_;
        current_.reset();
    }
    m.ended_at_ms = now;
    m.end_event_seq = end_seq;
    m.state = SessionManifest::kEnded;
    flush(m);
    return m;
}

std::vector<std::string> SessionBook::recover_interrupted(const std::string& data_dir) {
    std::vector<std::string> done;
    std::string dir = join_path(data_dir, "sessions");
    for (auto& name : list_files(dir)) {
        if (name.size() < 6 || name.compare(name.size() - 5, 5, ".json") != 0) continue;
        std::string f = join_path(dir, name);
        auto m = SessionManifest::load(f);
        if (!m || m->state != SessionManifest::kActive) continue;
        std::string csv = m->file.empty() ? "" : join_path(dir, base_name(m->file));
        int64_t ended = (!csv.empty() && file_exists(csv)) ? stat_path(csv).mtime_ms : stat_path(f).mtime_ms;
        m->state = SessionManifest::kInterrupted;
        m->ended_at_ms = ended;
        if (write_file_atomic(f, std::string(kUtf8Bom) + m->to_json(), true)) done.push_back(m->id);
    }
    return done;
}

}  // namespace reactor::store
