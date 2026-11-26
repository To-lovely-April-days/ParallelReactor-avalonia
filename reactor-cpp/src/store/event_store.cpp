#include "store/event_store.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>

#include "common/clock.hpp"

namespace reactor::store {

using nlohmann::json;

bool EventRecord::operator==(const EventRecord& o) const {
    return seq == o.seq && seq_epoch == o.seq_epoch && ts_ms == o.ts_ms && ts_local == o.ts_local && level == o.level &&
           kind == o.kind && channel == o.channel && source == o.source && origin == o.origin && client == o.client &&
           op_id == o.op_id && title == o.title && message == o.message;
}

std::string event_to_json(const EventRecord& r) {
    ojson o = ojson::object();
    o["seq"] = r.seq;
    o["seqEpoch"] = r.seq_epoch;
    o["ts"] = fmt_utc_json(r.ts_ms);
    o["tsLocal"] = r.ts_local;
    o["level"] = r.level;
    o["kind"] = r.kind;
    o["channel"] = r.channel ? ojson(*r.channel) : ojson(nullptr);
    o["source"] = r.source;
    o["origin"] = r.origin;
    o["client"] = r.client ? ojson(*r.client) : ojson(nullptr);
    o["opId"] = r.op_id ? ojson(*r.op_id) : ojson(nullptr);
    o["title"] = r.title;
    o["message"] = r.message;
    return dump_json(o);
}

std::optional<EventRecord> event_from_json(const std::string& line) {
    bool blank = std::all_of(line.begin(), line.end(), [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; });
    if (blank) return std::nullopt;
    try {
        json j = json::parse(line);
        if (!j.is_object()) return std::nullopt;
        EventRecord r;
        auto str = [&](const char* k, std::string& out) {
            auto it = j.find(k);
            if (it == j.end() || it->is_null()) return true;
            if (!it->is_string()) return false;
            out = it->get<std::string>();
            return true;
        };
        auto ostr = [&](const char* k, std::optional<std::string>& out) {
            auto it = j.find(k);
            if (it == j.end() || it->is_null()) return true;
            if (!it->is_string()) return false;
            out = it->get<std::string>();
            return true;
        };
        auto it = j.find("seq");
        if (it == j.end() || !it->is_number_integer()) return std::nullopt;
        r.seq = it->get<int64_t>();
        if (auto e = j.find("seqEpoch"); e != j.end() && !e->is_null()) {
            if (!e->is_number_integer()) return std::nullopt;
            r.seq_epoch = e->get<int>();
        }
        std::string ts;
        if (!str("ts", ts)) return std::nullopt;
        if (!ts.empty() && !parse_time_loose(ts, r.ts_ms)) return std::nullopt;
        if (auto c = j.find("channel"); c != j.end() && !c->is_null()) {
            if (!c->is_number_integer()) return std::nullopt;
            r.channel = c->get<int>();
        }
        if (!str("tsLocal", r.ts_local) || !str("level", r.level) || !str("kind", r.kind) || !str("source", r.source) ||
            !str("origin", r.origin) || !ostr("client", r.client) || !ostr("opId", r.op_id) || !str("title", r.title) ||
            !str("message", r.message))
            return std::nullopt;
        return r;
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

// ============================ kind 推导 ============================

namespace event_kinds {

std::optional<int> channel_of(const std::string& origin) {
    if (origin.size() == 3 && origin[0] == 'R' && origin[1] == 'V' && origin[2] >= '1' && origin[2] <= '8') return origin[2] - '0';
    return std::nullopt;
}

std::optional<int> channel_of(const std::string& origin, const std::string& title) {
    if (auto c = channel_of(origin)) return c;
    static const std::string kHao = " 号";
    if (title.size() >= 1 + kHao.size() && title[0] >= '1' && title[0] <= '8' && title.compare(1, kHao.size(), kHao) == 0)
        return title[0] - '0';
    return std::nullopt;
}

// C#: Service/EventStore.cs EventKinds.Derive（逐条照抄，顺序不能变）
std::string derive(const std::string& origin, const std::string& title) {
    auto has = [&](const char* s) { return title.find(s) != std::string::npos; };
    if (origin == "接口") {
        if (has("鉴权")) return "api.authFailed";
        if (has("绑定失败")) return "system.apiBindFailed";
        if (has("已启动")) return "system.apiStarted";
        if (has("已停止")) return "system.apiStopped";
        if (has("已连接")) return "system.clientConnected";
        if (has("已断开")) return "system.clientDisconnected";
        return "api.generic";
    }
    if (origin == "阀门") return "valve.frame";
    if (origin == "设置") return "system.settingsChanged";
    if (origin == "通讯") return has("恢复") ? "alarm.commRestored" : "alarm.commLost";
    if (has("搅拌电机故障")) return has("解除") ? "alarm.stirFaultCleared" : "alarm.stirFault";
    if (has("超温")) {
        if (has("解除")) return "alarm.overTempCleared";
        if (has("已确认")) return "alarm.overTempStopConfirmed";
        if (has("重新打开")) return "alarm.overTempReopened";
        if (has("未确认")) return "alarm.overTempStopUnconfirmed";
        return "alarm.overTemp";
    }
    if (has("超压")) {
        if (has("解除")) return "alarm.overPressureCleared";
        return "alarm.overPressure";
    }
    if (has("急停")) {
        if (has("开机")) return "estop.restoredOnBoot";
        if (has("复位")) return "estop.reset";
        if (has("锁存")) return "estop.latch";
        if (has("已确认") || has("未确认")) return "estop.part";
        return "estop.triggered";
    }
    if (has("测试会话开始")) return "session.begin";
    if (has("测试会话结束")) return "session.end";
    if (title == "标记") return "session.marker";
    if (has("泄漏测试")) return has("开始") ? "leak.started" : "leak.finished";
    if (has("程序启动")) return "system.boot";
    if (has("程序退出")) return "system.shutdown";
    if (has("模拟模式")) return "system.simulated";
    if (has("程序")) {
        if (has("中止")) return "program.aborted";
        if (has("完成")) return "program.finished";
        if (has("失败") || has("拒绝")) return "program.fault";
        if (has("开始") || has("启动")) return "program.started";
        return "program.generic";
    }
    if (has("自整定")) return has("取消") ? "autotune.cancelled" : has("完成") ? "autotune.finished" : "autotune.started";
    if (has("启动加热失败")) return "alarm.heatStartFailed";
    if (has("停止加热未确认")) return "alarm.heatStopFailed";
    if (has("写盘失败")) return "alarm.loggerWriteFailed";
    if (has("关闭失败") || has("写入失败")) return "alarm.valveWriteFailed";
    if (has("控制循环超时")) return "system.tickOverrun";
    return "event.generic";
}

}  // namespace event_kinds

// ============================ 文件读取 ============================

namespace {

// 逐行读（去 BOM、去 \r）；fn 返回 false 停止
template <class F>
void for_each_line(const std::string& path, F fn) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return;
    std::string line;
    bool first = true;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (first) { line = strip_bom(line); first = false; }
        if (!fn(line)) break;
    }
}

bool is_jsonl_name(const std::string& n) {
    return n.size() > 13 && n.compare(0, 7, "events-") == 0 && n.compare(n.size() - 6, 6, ".jsonl") == 0;
}

std::vector<std::string> jsonl_files(const std::string& dir) {
    std::vector<std::string> v;
    for (auto& n : list_files(dir))
        if (is_jsonl_name(n)) v.push_back(n);
    std::sort(v.begin(), v.end());
    return v;
}

}  // namespace

int64_t EventStore::max_seq_in(const std::string& path) {
    int64_t mx = 0;
    for_each_line(path, [&](const std::string& l) {
        if (auto r = event_from_json(l); r && r->seq > mx) mx = r->seq;
        return true;
    });
    return mx;
}

int64_t EventStore::first_seq_in(const std::string& path) {
    int64_t first = 0;
    int n = 0;
    for_each_line(path, [&](const std::string& l) {
        if (auto r = event_from_json(l)) { first = r->seq; return false; }
        return ++n < 20;   // 开头全是坏行：当作未知，不跳过该文件
    });
    return first;
}

int64_t EventStore::recover_last_seq(const std::string& dir) {
    int64_t from_seq = 0, from_tail = 0;
    std::string text;
    if (read_file(join_path(dir, ".seq"), text)) {
        std::string t = trim(text);
        char* end = nullptr;
        long long v = std::strtoll(t.c_str(), &end, 10);
        if (!t.empty() && end && *end == '\0' && v > 0) from_seq = v;
    }
    auto files = jsonl_files(dir);
    if (!files.empty()) from_tail = max_seq_in(join_path(dir, files.back()));
    return std::max(from_seq, from_tail);
}

std::vector<EventRecord> EventStore::read_files(const std::string& dir, int64_t after, int limit) {
    std::vector<EventRecord> list;
    auto files = jsonl_files(dir);
    size_t start = 0;
    for (size_t i = files.size(); i-- > 1;) {
        int64_t first = first_seq_in(join_path(dir, files[i]));
        if (first > 0 && first <= after + 1) { start = i; break; }
    }
    auto by_seq = [](const EventRecord& a, const EventRecord& b) { return a.seq < b.seq; };
    for (size_t i = start; i < files.size(); ++i) {
        bool full = false;
        for_each_line(join_path(dir, files[i]), [&](const std::string& l) {
            auto r = event_from_json(l);
            if (!r || r->seq <= after) return true;
            list.push_back(std::move(*r));
            if (static_cast<int>(list.size()) >= limit) { full = true; return false; }
            return true;
        });
        if (full) break;
    }
    std::sort(list.begin(), list.end(), by_seq);
    return list;
}

// ============================ EventStore ============================

EventStore::EventStore(std::string data_dir, std::function<void(const std::string&)> on_error)
    : dir_(join_path(data_dir, "events")), on_error_(std::move(on_error)), ring_(kRingSize) {
    head_ = recover_last_seq(dir_);
    flushed_head_ = 0;
    th_ = std::thread([this] { loop(); });
}

EventStore::~EventStore() { stop(); }

int64_t EventStore::head() const {
    std::lock_guard<std::mutex> lk(mu_);
    return head_;
}

int64_t EventStore::oldest_buffered() const {
    std::lock_guard<std::mutex> lk(mu_);
    return oldest_;
}

int64_t EventStore::dropped_lines() const { return dropped_.load(); }

int EventStore::subscribe(std::function<void(const EventRecord&)> fn) {
    std::lock_guard<std::mutex> lk(mu_);
    int id = next_sub_++;
    subs_.emplace_back(id, std::move(fn));
    return id;
}

void EventStore::unsubscribe(int id) {
    std::lock_guard<std::mutex> lk(mu_);
    subs_.erase(std::remove_if(subs_.begin(), subs_.end(), [&](auto& p) { return p.first == id; }), subs_.end());
}

EventRecord EventStore::append(const WallTime& ts, const std::string& level, const std::string& origin, const std::string& title,
                               const std::string& message, const std::string& source, const std::optional<std::string>& client,
                               const std::optional<std::string>& op_id, const std::string& kind) {
    EventRecord rec;
    rec.seq_epoch = kSeqEpoch;
    rec.ts_ms = ts.ms;
    rec.ts_local = fmt_offset_time(ts);
    rec.level = level.empty() ? "info" : level;
    rec.kind = trim(kind).empty() ? event_kinds::derive(origin, title) : kind;
    rec.channel = event_kinds::channel_of(origin, title);
    rec.source = source;
    rec.origin = origin;
    rec.client = client;
    rec.op_id = op_id;
    rec.title = title;
    rec.message = message;
    std::string day = fmt_wall(ts, "yyyyMMdd");

    std::lock_guard<std::mutex> lk(mu_);
    rec.seq = ++head_;
    ring_[static_cast<size_t>(rec.seq % kRingSize)] = rec;
    if (oldest_ == 0) oldest_ = rec.seq;   // 本次运行写入的第一条（重启后环是空的）
    else if (rec.seq - oldest_ >= kRingSize) oldest_ = rec.seq - kRingSize + 1;
    {
        std::lock_guard<std::mutex> ql(qmu_);
        if (stopping_ || q_.size() >= kQueueCapacity) ++dropped_;
        else q_.push_back(Job{rec, day});
    }
    qcv_.notify_one();
    for (auto& s : subs_) {
        try { s.second(rec); } catch (...) { /* 订阅者异常不影响记录 */ }
    }
    return rec;
}

ResyncResult EventStore::resync(int64_t after, int limit) const {
    std::lock_guard<std::mutex> lk(mu_);
    ResyncResult r;
    r.head = head_;
    r.oldest_buffered = oldest_;
    if (after >= head_ || head_ == 0) return r;
    if (oldest_ == 0 || after + 1 < oldest_) { r.gap = true; return r; }
    for (int64_t s = after + 1; s <= head_ && static_cast<int>(r.items.size()) < limit; ++s) {
        auto& slot = ring_[static_cast<size_t>(s % kRingSize)];
        if (!slot || slot->seq != s) { r.gap = true; return r; }
        r.items.push_back(*slot);
    }
    return r;
}

std::vector<EventRecord> EventStore::read_after(int64_t after, int limit) const {
    auto r = resync(after, limit);
    if (!r.gap) return r.items;
    return read_files(dir_, after, limit);
}

EventsPage EventStore::query(std::optional<int64_t> after_seq, std::optional<int> limit) const {
    EventsPage p;
    p.after_seq = std::max<int64_t>(0, after_seq.value_or(0));
    int lim = std::min(std::max(limit.value_or(kDefaultLimit), 1), kMaxLimit);
    p.items = read_after(p.after_seq, lim + 1);
    p.has_more = static_cast<int>(p.items.size()) > lim;
    if (p.has_more) p.items.resize(static_cast<size_t>(lim));
    p.count = static_cast<int>(p.items.size());
    p.seq_epoch = kSeqEpoch;
    p.head = head();
    return p;
}

void EventStore::sync() {
    std::unique_lock<std::mutex> lk(qmu_);
    if (stopping_ || !th_.joinable()) return;
    uint64_t r = ++sync_req_;
    qcv_.notify_one();
    done_cv_.wait(lk, [&] { return sync_done_ >= r || stopping_; });
}

void EventStore::stop() {
    {
        std::lock_guard<std::mutex> lk(qmu_);
        if (stopping_ && !th_.joinable()) return;
        stopping_ = true;
    }
    qcv_.notify_all();
    if (th_.joinable()) th_.join();
    done_cv_.notify_all();
}

void EventStore::loop() {
    std::unique_lock<std::mutex> lk(qmu_);
    for (;;) {
        if (!q_.empty()) {
            std::deque<Job> batch;
            batch.swap(q_);
            lk.unlock();
            for (auto& j : batch) write_one(j);
            lk.lock();
            continue;
        }
        if (sync_req_ != sync_done_) {
            uint64_t r = sync_req_;
            lk.unlock();
            flush_files();
            lk.lock();
            sync_done_ = r;
            done_cv_.notify_all();
            continue;
        }
        if (stopping_) break;
        if (dirty_) {
            int64_t now = mono_ms();
            int64_t deadline = dirty_since_mono_ + kFlushIntervalMs;
            if (now >= deadline) {
                lk.unlock();
                flush_files();
                lk.lock();
                continue;
            }
            qcv_.wait_for(lk, std::chrono::milliseconds(deadline - now));
        } else {
            qcv_.wait(lk);
        }
    }
    lk.unlock();
    flush_files();
    if (fp_) std::fclose(fp_);
    fp_ = nullptr;
    lk.lock();
    sync_done_ = sync_req_;
    done_cv_.notify_all();
}

void EventStore::write_one(const Job& j) {
    if (!fp_ || fp_day_ != j.day) {
        if (fp_) std::fclose(fp_);
        fp_ = nullptr;
        if (mono_ms() < retry_at_mono_) { ++dropped_; return; }
        std::string err;
        std::string path = join_path(dir_, "events-" + j.day + ".jsonl");
        FILE* f = nullptr;
        if (make_dirs(dir_, &err)) {
            auto st = stat_path(path);
            bool partial = st.exists && st.size > 0 && !ends_with_newline(path);
            f = std::fopen(path.c_str(), "ab");
            if (f && partial) std::fputc('\n', f);   // 上次异常退出留下的半行先收尾（该行本身仍是坏行，读取时跳过）
            if (!f) err = "打不开 " + path;
        }
        if (!f) {
            retry_at_mono_ = mono_ms() + 30000;
            ++dropped_;
            report("写入事件 JSONL 失败：" + err);
            return;
        }
        fp_ = f;
        fp_day_ = j.day;
    }
    std::string line = event_to_json(j.rec);
    line += '\n';
    if (std::fwrite(line.data(), 1, line.size(), fp_) != line.size()) {
        std::fclose(fp_);
        fp_ = nullptr;
        retry_at_mono_ = mono_ms() + 30000;
        ++dropped_;
        report("写入事件 JSONL 失败");
        return;
    }
    if (!dirty_) dirty_since_mono_ = mono_ms();
    dirty_ = true;
    reported_ = false;
}

void EventStore::flush_files() {
    if (dirty_) {
        dirty_ = false;
        if (fp_ && std::fflush(fp_) != 0) {
            std::fclose(fp_);
            fp_ = nullptr;
            retry_at_mono_ = mono_ms() + 30000;
            report("事件 JSONL flush 失败");
            return;
        }
    }
    int64_t h = head();
    if (h == flushed_head_ || h == 0) return;
    if (write_file_atomic(join_path(dir_, ".seq"), std::to_string(h), false)) flushed_head_ = h;
    else report("写入 events/.seq 失败");
}

void EventStore::report(const std::string& msg) {
    if (reported_) return;
    reported_ = true;
    if (on_error_) {
        try { on_error_(msg); } catch (...) {}
    }
}

}  // namespace reactor::store
