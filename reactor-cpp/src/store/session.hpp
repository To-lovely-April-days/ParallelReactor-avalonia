// 测试会话的 manifest（C#: Contracts/.../Services/SessionManifest.cs + Service/SessionManifest.cs 的 SessionBook）。
// 落盘为 <data>/sessions/<id>.json（camelCase、缩进 2 空格、UTF-8 BOM），开始 / 打点 / 标签 / 结束时整文件重写。
// id = 会话文件主名 <testId>-<yyyyMMdd-HHmmss>（本机唯一）；testId 为调用方给的测试编号（可重复）。
// state：active / ended / interrupted（开机发现 active → interrupted，endedAt 取会话 CSV 的修改时间）。
//
// 线程：SessionBook 单写者（控制线程）；current() 返回快照副本，任意线程可读（接口的 /sessions/current）。
#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

struct SessionMarker {
    int id = 0;
    int64_t ts_ms = 0;
    int64_t seq = 0;
    std::string text;
    std::string source;
    std::optional<std::string> client;
};

struct SessionProgramRun {
    int rv = 0;
    int64_t started_at_ms = 0;
    std::string started_by;
};

struct SessionLeakTest {
    std::string run_id;
    int64_t started_at_ms = 0;
};

struct SessionManifest {
    static constexpr const char* kActive = "active";
    static constexpr const char* kEnded = "ended";
    static constexpr const char* kInterrupted = "interrupted";

    std::string id;
    std::string test_id;
    std::optional<std::string> note;
    int64_t started_at_ms = 0;
    std::optional<int64_t> ended_at_ms;
    std::string state = kActive;
    std::string source;                  // local / remote
    std::optional<std::string> client;
    bool simulated = false;
    int64_t start_event_seq = 0;
    std::optional<int64_t> end_event_seq;
    std::string file;                    // 会话 CSV 文件名（sessions/ 下）
    std::vector<SessionMarker> markers;
    std::vector<SessionProgramRun> program_runs;
    std::vector<SessionLeakTest> leak_tests;

    static std::string rel_path(const std::string& id) { return "sessions/" + id + ".json"; }
    std::string to_json() const;
    // 宽松读（属性名不区分大小写、允许注释，同 C# SessionJsonContext 选项）；读不了返回空
    static std::optional<SessionManifest> from_json(const std::string& json);
    static std::optional<SessionManifest> load(const std::string& path);
};

class SessionBook {
public:
    // write(rel_path, json)：主程序传 DataLogger::write_file（后台线程写，带 BOM）
    explicit SessionBook(std::function<void(const std::string&, const std::string&)> write);

    std::optional<SessionManifest> current() const;
    std::optional<std::string> current_id() const;

    SessionManifest begin(const std::string& id, const std::string& test_id, const std::string& note, int64_t now_utc_ms,
                          const std::string& source, const std::optional<std::string>& client, bool simulated, int64_t start_seq,
                          const std::string& file);
    // 打点：返回标记（含 id，从 1 起）；无会话返回空
    std::optional<SessionMarker> add_marker(const std::string& text, int64_t now_utc_ms, int64_t seq, const std::string& source,
                                            const std::optional<std::string>& client);
    void tag_program_run(int rv, int64_t now_utc_ms, const std::string& started_by);
    void tag_leak_test(const std::string& run_id, int64_t now_utc_ms);
    // 结束：state = ended，写最终版本；返回结束的 manifest（无会话为空）
    std::optional<SessionManifest> end(int64_t now_utc_ms, int64_t end_seq);

    // 开机恢复：数据目录下 state = active 的 manifest 改为 interrupted，endedAt = 会话 CSV 的修改时间
    //（没有 CSV 时用 manifest 自己的修改时间）。同步写（只在启动时调用一次）。返回处理的会话 id
    static std::vector<std::string> recover_interrupted(const std::string& data_dir);

private:
    void flush(const SessionManifest& m);

    std::function<void(const std::string&, const std::string&)> write_;
    mutable std::mutex mu_;
    std::optional<SessionManifest> current_;
};

}  // namespace reactor::store
