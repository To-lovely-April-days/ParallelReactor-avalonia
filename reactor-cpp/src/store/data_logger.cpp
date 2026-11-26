#include "store/data_logger.hpp"

#include <chrono>
#include <cstdio>

#include "common/clock.hpp"
#include <cstdint>
#include <functional>
#include <string>
#include <thread>
#include <vector>

namespace reactor::store {

DataLogger::DataLogger(std::string dir, std::function<void(const std::string&)> on_error, EventStore* events)
    : dir_(std::move(dir)), on_error_(std::move(on_error)), events_(events) {
    tel_.name = "tel";
    tel_.header = telemetry_csv::header();
    evt_.name = "evt";
    evt_.header = telemetry_csv::event_header();
    ses_.name = "ses";
    ses_.header = telemetry_csv::header();
    last_flush_mono_ = mono_ms();
    th_ = std::thread([this] { loop(); });
}

DataLogger::~DataLogger() { stop(); }

std::string DataLogger::safe_name(const std::string& s) {
    std::vector<uint32_t> out;
    for (uint32_t cp : utf8_decode(s)) {
        bool bad = cp == 0 || cp == '/' || cp == '\\' || cp == ':' || cp == '*' || cp == '?' || cp == '"' || cp == '<' ||
                   cp == '>' || cp == '|' || is_unicode_space(cp);
        out.push_back(bad ? '_' : cp);
    }
    return utf8_encode(out);
}

// ============================ 调用方 API ============================

void DataLogger::telemetry(TelemetryRow row) {
    std::optional<std::string> spath;
    {
        std::lock_guard<std::mutex> lk(smu_);
        if (marker_) {
            row.marker = row.marker.empty() ? *marker_ : row.marker + " | " + *marker_;
            marker_.reset();
        }
        if (!row.session) row.session = session_;
        spath = session_path_;
    }
    std::string line = telemetry_csv::format(row);
    std::string day = fmt_wall(row.time, "yyyyMMdd");
    enqueue(Job{JobType::Row, day, std::move(line), spath.value_or("")});
}

std::optional<EventRecord> DataLogger::event(const WallTime& t, const std::string& level, const std::string& origin,
                                             const std::string& title, const std::string& detail, const std::string& event_source,
                                             const std::optional<std::string>& client, const std::optional<std::string>& op_id,
                                             const std::string& kind) {
    std::optional<EventRecord> rec;
    if (events_) {
        try { rec = events_->append(t, level, origin, title, detail, event_source, client, op_id, kind); } catch (...) {}
    }
    enqueue(Job{JobType::Event, fmt_wall(t, "yyyyMMdd"), telemetry_csv::format_event(t, level, origin, title, detail), ""});
    return rec;
}

std::string DataLogger::begin_session(const std::string& test_id, const WallTime& now) {
    std::string id = trim(test_id).empty() ? "test" : trim(test_id);
    std::string stem = safe_name(id) + "-" + fmt_wall(now, "yyyyMMdd-HHmmss");
    std::string path = join_path(join_path(dir_, "sessions"), stem + ".csv");
    std::lock_guard<std::mutex> lk(smu_);
    session_ = id;
    session_path_ = path;
    session_key_ = stem;
    return path;
}

void DataLogger::end_session(std::optional<TelemetryRow> final_row, const std::string& end_marker) {
    std::optional<std::string> id, path;
    {
        std::lock_guard<std::mutex> lk(smu_);
        if (!session_path_) return;
        id = session_;
        path = session_path_;
        session_.reset();
        session_path_.reset();
        session_key_.reset();
    }
    if (final_row) {
        final_row->session = id;
        if (!end_marker.empty()) final_row->marker = final_row->marker.empty() ? end_marker : final_row->marker + " | " + end_marker;
        enqueue(Job{JobType::SessionRow, "", telemetry_csv::format(*final_row), *path});
    }
    enqueue(Job{JobType::SessionClose, "", "", ""});
}

void DataLogger::add_marker(const std::string& text) {
    if (trim(text).empty()) return;
    std::lock_guard<std::mutex> lk(smu_);
    marker_ = marker_ ? *marker_ + " | " + text : text;
}

std::optional<std::string> DataLogger::session_id() const {
    std::lock_guard<std::mutex> lk(smu_);
    return session_;
}
std::optional<std::string> DataLogger::session_key() const {
    std::lock_guard<std::mutex> lk(smu_);
    return session_key_;
}
std::optional<std::string> DataLogger::session_path() const {
    std::lock_guard<std::mutex> lk(smu_);
    return session_path_;
}

void DataLogger::write_file(const std::string& rel_path, const std::string& content) {
    enqueue(Job{JobType::File, "", content, join_path(dir_, rel_path)});
}

void DataLogger::enqueue(Job j) {
    {
        std::lock_guard<std::mutex> lk(qmu_);
        if (stopping_ || q_.size() >= kQueueCapacity) {
            ++dropped_;
            return;
        }
        q_.push_back(std::move(j));
    }
    qcv_.notify_one();
}

void DataLogger::sync() {
    std::unique_lock<std::mutex> lk(qmu_);
    if (stopping_ || !th_.joinable()) return;
    uint64_t r = ++sync_req_;
    qcv_.notify_one();
    done_cv_.wait(lk, [&] { return sync_done_ >= r || stopping_; });
}

void DataLogger::stop() {
    {
        std::lock_guard<std::mutex> lk(qmu_);
        if (stopping_ && !th_.joinable()) return;
        stopping_ = true;
    }
    qcv_.notify_all();
    if (th_.joinable()) th_.join();
    done_cv_.notify_all();
}

// ============================ 后台写盘 ============================

void DataLogger::loop() {
    std::unique_lock<std::mutex> lk(qmu_);
    for (;;) {
        if (!q_.empty()) {
            std::deque<Job> batch;
            batch.swap(q_);
            lk.unlock();
            for (auto& j : batch) {
                process(j);
                if (dirty_ && mono_ms() - last_flush_mono_ >= kFlushIntervalMs) flush_all();
            }
            lk.lock();
            continue;
        }
        if (sync_req_ != sync_done_) {
            uint64_t r = sync_req_;
            lk.unlock();
            flush_all();
            lk.lock();
            sync_done_ = r;
            done_cv_.notify_all();
            continue;
        }
        if (stopping_) break;
        if (dirty_) {
            int64_t due = last_flush_mono_ + kFlushIntervalMs - mono_ms();
            if (due <= 0) {
                lk.unlock();
                flush_all();
                lk.lock();
                continue;
            }
            qcv_.wait_for(lk, std::chrono::milliseconds(due));
        } else {
            qcv_.wait(lk);
        }
    }
    lk.unlock();
    flush_all();
    close(tel_);
    close(evt_);
    close(ses_);
    lk.lock();
    sync_done_ = sync_req_;
    done_cv_.notify_all();
}

void DataLogger::process(const Job& j) {
    switch (j.type) {
        case JobType::Row:
            append(tel_, join_path(dir_, "telemetry-" + j.key + ".csv"), j.key, j.line);
            if (!j.path.empty()) append(ses_, j.path, j.path, j.line);
            break;
        case JobType::SessionRow:
            append(ses_, j.path, j.path, j.line);
            break;
        case JobType::Event:
            append(evt_, join_path(dir_, "events-" + j.key + ".csv"), j.key, j.line);
            break;
        case JobType::File: {
            std::string err;
            if (write_file_atomic(j.path, std::string(kUtf8Bom) + j.line, true, &err)) reported_.erase("file");
            else report("file", "写入 " + j.path + " 失败：" + err);
            break;
        }
        case JobType::SessionClose:
            close(ses_);
            break;
    }
}

std::string DataLogger::open_path(const std::string& path, const std::string& header, bool& fresh) {
    std::string dir = dir_name(path);
    std::string base = base_name(path);
    auto dot = base.find_last_of('.');
    std::string stem = dot == std::string::npos ? base : base.substr(0, dot);
    std::string ext = dot == std::string::npos ? "" : base.substr(dot);
    for (int k = 0;; ++k) {
        std::string p = k == 0 ? path : join_path(dir, stem + "-" + std::to_string(k) + ext);
        auto st = stat_path(p);
        if (!st.exists || st.size == 0) { fresh = true; return p; }
        if (read_first_line(p) == header) { fresh = false; return p; }
        if (k > 999) { fresh = true; return p; }   // 防御：不会走到
    }
}

void DataLogger::append(Sink& s, const std::string& path, const std::string& key, const std::string& line) {
    if (!s.fp || s.key != key) {
        close(s);
        if (mono_ms() < s.retry_at) { ++dropped_; return; }
        std::string err;
        FILE* f = nullptr;
        if (make_dirs(dir_name(path), &err)) {
            bool fresh = false;
            s.path = open_path(path, s.header, fresh);
            bool partial = !fresh && !ends_with_newline(s.path);   // 上次异常退出（断电 / kill）留下半行
            f = std::fopen(s.path.c_str(), "ab");
            if (f) {
                if (fresh) {
                    std::fputs(kUtf8Bom, f);
                    std::fputs(s.header.c_str(), f);
                    std::fputc('\n', f);
                } else if (partial) {
                    std::fputc('\n', f);   // 先把半行收尾，新行不拼进去
                }
            } else {
                err = "打不开 " + s.path;
            }
        }
        if (!f) {
            s.retry_at = mono_ms() + kRetryOpenAfterMs;
            ++dropped_;
            report(s.name, "写入 " + path + " 失败：" + err);
            return;
        }
        s.fp = f;
        s.key = key;
    }
    std::string l = line + "\n";
    if (std::fwrite(l.data(), 1, l.size(), s.fp) != l.size()) {
        close(s);
        s.retry_at = mono_ms() + kRetryOpenAfterMs;
        ++dropped_;
        report(s.name, "写入 " + path + " 失败");
        return;
    }
    dirty_ = true;
    reported_.erase(s.name);
}

void DataLogger::flush_all() {
    for (Sink* s : {&tel_, &evt_, &ses_}) {
        if (!s->fp) continue;
        if (std::fflush(s->fp) != 0) {
            std::string p = s->path;
            close(*s);
            s->retry_at = mono_ms() + kRetryOpenAfterMs;
            report(s->name, "写入 " + p + " 失败");
        }
    }
    dirty_ = false;
    last_flush_mono_ = mono_ms();
}

void DataLogger::close(Sink& s) {
    if (s.fp) std::fclose(s.fp);
    s.fp = nullptr;
    s.key.clear();
}

void DataLogger::report(const std::string& key, const std::string& msg) {
    if (!reported_.insert(key).second) return;
    int64_t now = mono_ms();
    if (ever_reported_ && now - last_report_mono_ < kReportGapMs) return;
    ever_reported_ = true;
    last_report_mono_ = now;
    if (on_error_) {
        try { on_error_(msg); } catch (...) {}
    }
}

}  // namespace reactor::store
