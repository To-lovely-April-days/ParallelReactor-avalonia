// 事件存储（C#: Service/EventStore.cs + Contracts/.../Services/EventRecord.cs）。
// 每条事件分配全局单调 seq，追加写 <data>/events/events-YYYYMMDD.jsonl（一行一个 JSON，camelCase，中文不转义），
// 内存环保留最近 5000 条给 WebSocket 断线重同步。seq 跨重启连续：每次 flush 写 events/.seq，
// 开机取 .seq 与最新 JSONL 里能解析出的最大 seq 的较大者（损坏行 / 半行跳过）。seqEpoch 恒为 1。
//
// 线程：append 任意线程可调。分配 seq、入环、入写盘队列、通知订阅者四步在同一把锁内完成，
// 所以 JSONL 行序、环内容、推送顺序都严格按 seq 递增。订阅者回调在调用 append 的线程上、锁内执行：
// 只能做非阻塞入队，不得阻塞、不得回调本存储（包括 unsubscribe）。
// 写盘由内部一个后台线程完成：有内容后最迟 2 s flush（并写 .seq）；失败丢弃并计数，30 s 后重试打开，不抛给调用方。
// 查询（resync / read_after / query）任意线程可调。
#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "store/store_util.hpp"

namespace reactor::store {

// C#: EventRecord（JSONL 字段顺序 = 这里的顺序）
struct EventRecord {
    int64_t seq = 0;
    int seq_epoch = 1;
    int64_t ts_ms = 0;          // "ts"：UTC
    std::string ts_local;       // "tsLocal"：2026-11-06T16:12:30.120+08:00
    std::string level;          // info / warn / err
    std::string kind;
    std::optional<int> channel;
    std::string source;         // system / local / remote
    std::string origin;         // 中文来源列：RV3 / 系统 / 通讯 / 阀门 / 设置…
    std::optional<std::string> client;
    std::optional<std::string> op_id;
    std::string title;
    std::string message;

    bool operator==(const EventRecord& o) const;
};

std::string event_to_json(const EventRecord& r);
std::optional<EventRecord> event_from_json(const std::string& line);

// C#: EventKinds（推导表是过渡方案：新生产者显式给 kind；没给时按来源 + 标题推导，保持兼容）
namespace event_kinds {
std::optional<int> channel_of(const std::string& origin);   // "RV1".."RV8" → 1..8
// 来源不是通道（如「系统」记的程序事件）时，再看标题是不是以「n 号」开头（core::chan_name）。
// PC 端（AlarmCodes.ChannelOf）优先用 channel 字段，以前靠标题里的「RVn」认通道，标题改成「n 号」后靠这里补上。
std::optional<int> channel_of(const std::string& origin, const std::string& title);
std::string derive(const std::string& origin, const std::string& title);
}  // namespace event_kinds

struct ResyncResult {
    std::vector<EventRecord> items;
    bool gap = false;            // afterSeq 已不在环里（需走 GET /events 读文件）
    int64_t oldest_buffered = 0;
    int64_t head = 0;
};

// GET /events 的一页（C#: Api/Endpoints.cs /events：afterSeq 缺省 0、limit 缺省 500、上限 1000）
struct EventsPage {
    int seq_epoch = 1;
    int64_t head = 0;
    int64_t after_seq = 0;
    int count = 0;
    bool has_more = false;
    std::vector<EventRecord> items;
};

class EventStore {
public:
    static constexpr int kRingSize = 5000;
    static constexpr int kSeqEpoch = 1;
    static constexpr size_t kQueueCapacity = 50000;
    static constexpr int64_t kFlushIntervalMs = 2000;
    static constexpr int kDefaultLimit = 500;
    static constexpr int kMaxLimit = 1000;

    // data_dir：数据目录；JSONL 和 .seq 在 <data_dir>/events。on_error 在后台线程调用（同一故障只报一次）
    explicit EventStore(std::string data_dir, std::function<void(const std::string&)> on_error = {});
    ~EventStore();
    EventStore(const EventStore&) = delete;
    EventStore& operator=(const EventStore&) = delete;

    const std::string& dir() const { return dir_; }

    // 追加一条事件。kind 为空时按 origin / title 推导；channel 按 origin（RVn）推导。
    EventRecord append(const WallTime& ts, const std::string& level, const std::string& origin, const std::string& title,
                       const std::string& message, const std::string& source = "system",
                       const std::optional<std::string>& client = std::nullopt,
                       const std::optional<std::string>& op_id = std::nullopt, const std::string& kind = "");

    int64_t head() const;
    int seq_epoch() const { return kSeqEpoch; }
    int64_t oldest_buffered() const;
    int64_t dropped_lines() const;

    // 订阅：回调在 append 的线程上、存储锁内执行（只能非阻塞入队）。返回 id 给 unsubscribe
    int subscribe(std::function<void(const EventRecord&)> fn);
    void unsubscribe(int id);

    // 重同步：seq > after_seq 的最多 limit 条；after_seq 早于环里最旧一条 → gap
    ResyncResult resync(int64_t after_seq, int limit = 1000) const;
    // 先查环，环里没有（太旧）再扫 JSONL
    std::vector<EventRecord> read_after(int64_t after_seq, int limit = kDefaultLimit) const;
    // GET /events：after_seq 缺省 / 负数 → 0；limit 夹到 1..1000（缺省 500）；多取一条判断 hasMore
    EventsPage query(std::optional<int64_t> after_seq, std::optional<int> limit) const;

    // 等后台线程把此前入队的全部写完并 flush（测试、退出前用）
    void sync();
    // 停止：写完队列、flush、关闭文件。析构时自动调用
    void stop();

    // ---- 静态工具（测试 / 离线用）----
    static int64_t recover_last_seq(const std::string& events_dir);
    static int64_t max_seq_in(const std::string& path);
    static int64_t first_seq_in(const std::string& path);
    static std::vector<EventRecord> read_files(const std::string& events_dir, int64_t after_seq, int limit);

private:
    struct Job {
        EventRecord rec;
        std::string day;
    };

    void loop();
    void write_one(const Job& j);
    void flush_files();
    void report(const std::string& msg);

    std::string dir_;
    std::function<void(const std::string&)> on_error_;

    mutable std::mutex mu_;   // seq / 环 / 订阅者
    std::vector<std::optional<EventRecord>> ring_;
    int64_t head_ = 0;
    int64_t oldest_ = 0;
    std::vector<std::pair<int, std::function<void(const EventRecord&)>>> subs_;
    int next_sub_ = 1;
    std::atomic<int64_t> dropped_{0};

    std::mutex qmu_;   // 写盘队列（在 mu_ 内获取：先 mu_ 后 qmu_）
    std::condition_variable qcv_;
    std::condition_variable done_cv_;
    std::deque<Job> q_;
    bool stopping_ = false;
    uint64_t sync_req_ = 0, sync_done_ = 0;
    std::thread th_;

    // 只在后台线程用
    FILE* fp_ = nullptr;
    std::string fp_day_;
    int64_t retry_at_mono_ = 0;
    bool dirty_ = false;
    int64_t dirty_since_mono_ = 0;
    bool reported_ = false;
    int64_t flushed_head_ = 0;
};

}  // namespace reactor::store
