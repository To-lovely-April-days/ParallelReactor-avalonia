// 历史文件的白名单、路径安全、枚举与按时间段读取（C#: Api/SafePath.cs、Api/HistoryFiles.cs）。
// 全部只读；当天文件可能正被 DataLogger 追加，末尾可能有半行（进程被杀时留下），读取时跳过未以换行结束的最后一行。
// 线程：全部函数任意线程可调（首末行时间的缓存有锁）。
#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::store {

namespace safe_path {
constexpr const char* kTelemetry = "telemetry";
constexpr const char* kEvents = "events";
constexpr const char* kSessions = "sessions";
constexpr const char* kLeak = "leak";
const std::vector<std::string>& types();

// 文件名是否符合该类型的白名单：
//   telemetry  telemetry-YYYYMMDD[-N].csv
//   events     events-YYYYMMDD[-N].csv、events-YYYYMMDD.jsonl
//   sessions   不以点开头、不含 / \ NUL : * ? " < > | 和空白、≤200 字符的 .csv / .json
//   leak       leak-YYYYMMDD-HHMMSS(.csv|-summary.csv|-summary.json)
// 任何类型都拒绝 ".."、目录分隔符、NUL 和末尾换行；含组合附加符号（非 NFC 的典型情况）的名字也拒绝。
bool is_valid_name(const std::string& type, const std::string& name);
// 泄漏测试 runId：leak-yyyyMMdd-HHmmss
bool is_leak_run_id(const std::string& s);
// 该类型文件所在子目录（相对数据目录；根目录为 ""）
std::string sub_dir(const std::string& type, const std::string& name);
// 解析为绝对路径：类型未知 / 名字不合法 / 结果不在数据目录内 → 空。不检查文件是否存在
std::optional<std::string> try_resolve(const std::string& root, const std::string& type, const std::string& name);
}  // namespace safe_path

// GET /files 的一项（C#: Contracts/.../Api/HistoryFileInfo.cs）。from / to 为文件首末行时间（UTC 毫秒；解析不到为空）
struct HistoryFileInfo {
    std::string name;
    std::string type;
    int64_t bytes = 0;
    int64_t modified_at_ms = 0;
    std::optional<int64_t> from_ms, to_ms;
    std::optional<std::string> day;
};

namespace history_files {

constexpr size_t kTailChunk = 64 * 1024;

// telemetry-20261106-1.csv → ("20261106", 1)；不匹配为空
std::optional<std::pair<std::string, int>> day_of(const std::string& name);

// 列出某类文件（名称升序）。parse_range：读 CSV 首末行时间
std::vector<HistoryFileInfo> list(const std::string& data_dir, const std::string& type, bool parse_range = true);

// 选出覆盖 [from, to) 的日文件（按本机本地日期），含 -1 / -2 后缀，按日期、后缀升序。返回完整路径
std::vector<std::string> select_daily_files(const std::string& data_dir, int64_t from_ms, int64_t to_ms,
                                            const std::string& prefix = "telemetry");

// CSV 行首列的时间（带偏移的 ISO；无时区按 UTC）
std::optional<int64_t> parse_row_time(const std::string& line);
// 查询参数 from / to：必须带 Z 或 ±hh:mm / ±hhmm
bool try_parse_query_time(const std::string& s, int64_t& out_ms);

// 首行数据与最后一条完整行的时间（只读头两行和末尾一块；按（长度, 修改时间）缓存）
std::pair<std::optional<int64_t>, std::optional<int64_t>> first_last_timestamp(const std::string& path);
std::pair<std::optional<int64_t>, std::optional<int64_t>> compute_first_last(const std::string& path);
// 最后一条以换行结束的行（末尾半行跳过）；空文件为空
std::optional<std::string> last_complete_line(const std::string& path);

// 把多个日文件里时间 ∈ [from, to) 的行按序写出：先写 BOM 和第一个文件的表头；
// 后面文件表头不同时按列名映射到第一个表头（缺列留空、多出的列丢弃）。半行和解析失败的行跳过。
// out 返回 false = 取消（返回 -1）。返回写出的数据行数。
int64_t write_range(const std::vector<std::string>& files, int64_t from_ms, int64_t to_ms,
                    const std::function<bool(const std::string&)>& out);

// 目标表头每一列在源表头里的下标（没有为 -1）；完全相同返回空
std::optional<std::vector<int>> column_map(const std::vector<std::string>& target, const std::vector<std::string>& source);
std::string remap(const std::string& line, const std::vector<int>& map);

// sessions/<testId>-<yyyyMMdd-HHmmss>.csv → (testId, "yyyyMMdd-HHmmss")
std::optional<std::pair<std::string, std::string>> parse_session_name(const std::string& name);

}  // namespace history_files
}  // namespace reactor::store
