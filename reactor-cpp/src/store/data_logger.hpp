// 统一数据记录（C#: Service/DataLogger.cs），全部落到数据目录：
//   telemetry-YYYYMMDD.csv           每个控制周期一行（列见 telemetry_csv），按行时间戳的本地日期滚动；
//   events-YYYYMMDD.csv              报警与事件（时间, 级别, 来源, 标题, 详情；旧格式，PC 端下载）；
//   sessions/<testId>-<yyyyMMdd-HHmmss>.csv   begin_session 期间额外写一份同列的运行记录；
//   leak/… 等                         write_file 整文件写（UTF-8 BOM，覆盖）。
// CSV 都是 UTF-8 带 BOM，表头每个文件只写一次；同名文件已存在但表头不同时另起 -1、-2… 新文件。
// 上次异常退出留下的半行先补一个换行再追加。
//
// 线程：telemetry / event / add_marker / begin_session / end_session / write_file 任意线程可调
// （行在调用方线程格式化，然后入队；会话 / 标记状态有锁）。单个后台线程写盘，至少每 10 s flush 一次，
// stop() 写完队列再 flush 关闭。写盘失败不抛给调用方：丢弃该行并计数，30 s 后重试打开，经 on_error 按文件上报一次
// （两次上报至少隔 10 分钟）。队列上限 20000，满了丢弃新内容并计数。
#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <functional>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>

#include "store/event_store.hpp"
#include "store/telemetry_csv.hpp"

namespace reactor::store {

class DataLogger {
public:
    static constexpr size_t kQueueCapacity = 20000;
    static constexpr int64_t kFlushIntervalMs = 10000;
    static constexpr int64_t kRetryOpenAfterMs = 30000;
    static constexpr int64_t kReportGapMs = 10 * 60 * 1000;

    // dir：数据目录（C# 默认 ~/ParallelReactor-data；新系统 /var/lib/reactord，由 apps/reactord 传入）。
    // on_error 在后台线程调用。events 不为空时 event() 同时写入事件存储（分配 seq / JSONL / 环）。
    explicit DataLogger(std::string dir, std::function<void(const std::string&)> on_error = {}, EventStore* events = nullptr);
    ~DataLogger();
    DataLogger(const DataLogger&) = delete;
    DataLogger& operator=(const DataLogger&) = delete;

    const std::string& dir() const { return dir_; }
    EventStore* events() const { return events_; }

    // 入队一行运行记录：自动填入待写标记（消费掉）与当前会话 testId
    void telemetry(TelemetryRow row);

    // 入队一条事件（CSV）；配置了事件存储时同时写入它，返回分配的记录
    std::optional<EventRecord> event(const WallTime& t, const std::string& level, const std::string& origin,
                                     const std::string& title, const std::string& detail, const std::string& event_source = "system",
                                     const std::optional<std::string>& client = std::nullopt,
                                     const std::optional<std::string>& op_id = std::nullopt, const std::string& kind = "");

    // 开始测试会话：之后的运行记录同时写入 sessions/<SafeName(testId)>-<yyyyMMdd-HHmmss>.csv。返回会话文件完整路径
    std::string begin_session(const std::string& test_id, const WallTime& now);
    // 结束会话。final_row 不为空时作为会话文件最后一行（只写会话文件），其标记列追加 end_marker
    void end_session(std::optional<TelemetryRow> final_row = std::nullopt, const std::string& end_marker = "");
    // 标记：出现在下一行运行记录的「标记」列（多个以 " | " 连接）
    void add_marker(const std::string& text);

    std::optional<std::string> session_id() const;    // 当前会话 testId
    std::optional<std::string> session_key() const;   // 会话文件主名（对外的会话 id）
    std::optional<std::string> session_path() const;

    // 整文件写入（UTF-8 带 BOM，原子替换）。rel_path 相对数据目录
    void write_file(const std::string& rel_path, const std::string& content);

    // 等此前入队的内容全部写盘并 flush
    void sync();
    // 停止：不再接收新内容，写完队列、flush 并关闭全部文件（不停事件存储）
    void stop();

    int64_t dropped_lines() const { return dropped_.load(); }

    // 文件名安全化：非法字符与空白替换为下划线（C#: DataLogger.SafeName）
    static std::string safe_name(const std::string& s);

private:
    enum class JobType { Row, SessionRow, Event, File, SessionClose };
    struct Job {
        JobType type;
        std::string key;    // 日期（yyyyMMdd）
        std::string line;   // 一行 / 整文件内容
        std::string path;   // 会话文件 / 整文件路径
    };
    struct Sink {
        std::string name;     // tel / evt / ses（上报去重用）
        std::string header;
        std::string key;
        std::string path;
        FILE* fp = nullptr;
        int64_t retry_at = 0;
    };

    void enqueue(Job j);
    void loop();
    void process(const Job& j);
    void append(Sink& s, const std::string& path, const std::string& key, const std::string& line);
    void flush_all();
    static void close(Sink& s);
    void report(const std::string& key, const std::string& msg);
    static std::string open_path(const std::string& path, const std::string& header, bool& fresh);

    std::string dir_;
    std::function<void(const std::string&)> on_error_;
    EventStore* events_;

    mutable std::mutex smu_;   // 会话 / 标记
    std::optional<std::string> marker_;
    std::optional<std::string> session_, session_path_, session_key_;

    std::mutex qmu_;
    std::condition_variable qcv_, done_cv_;
    std::deque<Job> q_;
    bool stopping_ = false;
    uint64_t sync_req_ = 0, sync_done_ = 0;
    std::atomic<int64_t> dropped_{0};
    std::thread th_;

    // 后台线程独占
    Sink tel_, evt_, ses_;
    bool dirty_ = false;
    int64_t last_flush_mono_ = 0;
    std::set<std::string> reported_;
    int64_t last_report_mono_ = 0;
    bool ever_reported_ = false;
};

}  // namespace reactor::store
