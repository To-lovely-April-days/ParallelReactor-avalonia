// 存储模块共用的小工具：带时区偏移的时间（对应 C# DateTimeOffset）、时间格式化 / 解析、
// 文件读写（原子写：tmp + fsync + rename）、目录、UTF-8、JSON 输出辅助。
// 全部是无状态函数，任意线程可调。
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace reactor::store {

// ============================ 时间 ============================

// 一个时刻 + 当时的时区偏移（分钟），等价于 C# 的 DateTimeOffset：
// CSV 时间列写成本地时间 + 偏移（2026-11-06T14:03:21.123+08:00），日文件按这个偏移下的日期滚动。
struct WallTime {
    int64_t ms = 0;       // UNIX 毫秒（UTC）
    int offset_min = 0;   // 相对 UTC 的偏移（东八区 = 480）

    // 用本机时区的偏移（生产代码都用这个；测试用 with_offset 固定偏移）
    static WallTime local(int64_t utc_ms);
    static WallTime with_offset(int64_t utc_ms, int offset_min) { return WallTime{utc_ms, offset_min}; }
};

// 本机时区在该时刻的偏移（分钟，含夏令时）
int local_offset_min(int64_t utc_ms);

// C# Csv.TimeFormat「yyyy-MM-ddTHH:mm:ss.fffzzz」，如 2026-11-06T14:03:21.123+08:00
std::string fmt_offset_time(const WallTime& t);

// 按 t 自己的偏移格式化。pattern 只认 yyyy MM dd HH mm ss fff 这几个记号，其他字符原样输出
//（"yyyyMMdd"、"yyyyMMdd-HHmmss"、"yyyy-MM-ddTHH:mm:ss"）。
std::string fmt_wall(const WallTime& t, const char* pattern);

// C# System.Text.Json 写 UTC DateTime 的样子：2026-11-18T02:00:00Z / 2026-11-18T02:00:00.12Z（小数去尾零）
std::string fmt_utc_json(int64_t utc_ms);

// 宽松解析 ISO-8601：YYYY-MM-DD[T| ]HH:MM[:SS[.f…]][Z|±hh:mm|±hhmm]。
// 没有时区时按 UTC（和 C# 里各处 SpecifyKind(Utc) 的处理一致），had_zone 置 false。
bool parse_time_loose(const std::string& s, int64_t& out_ms, bool* had_zone = nullptr);

// 公历 ↔ 天数（1970-01-01 = 0）
int64_t days_from_civil(int y, int m, int d);
void civil_from_days(int64_t z, int& y, int& m, int& d);

// ============================ 文件 ============================

extern const char kUtf8Bom[];   // "\xEF\xBB\xBF"

std::string join_path(const std::string& a, const std::string& b);
std::string dir_name(const std::string& path);    // 最后一个 '/' 之前；没有为 "."
std::string base_name(const std::string& path);   // 最后一个 '/' 之后

struct FileStat {
    bool exists = false;
    bool is_dir = false;
    int64_t size = 0;
    int64_t mtime_ms = 0;   // UTC 毫秒
};
FileStat stat_path(const std::string& path);
bool file_exists(const std::string& path);   // 普通文件
bool dir_exists(const std::string& path);

// mkdir -p；已存在返回 true
bool make_dirs(const std::string& dir, std::string* err = nullptr);

// 目录里的普通文件名（不含 . / ..，不递归，未排序）；目录不存在返回空
std::vector<std::string> list_files(const std::string& dir);

// 整文件读（二进制原样）
bool read_file(const std::string& path, std::string& out);

// 原子写：同目录 <path>.tmp-<pid>-<n> → fsync → rename → fsync 目录。目录不存在时先建。
// do_fsync=false 时跳过 fsync（.seq 这类可以从别处恢复的小文件）。
bool write_file_atomic(const std::string& path, const std::string& data, bool do_fsync = true, std::string* err = nullptr);

// 非空文件的最后一个字节是否为 '\n'（空文件返回 true；打不开返回 false）
bool ends_with_newline(const std::string& path);

// 第一行（去 BOM、去行尾 \r）；文件不存在 / 空返回 nullopt
std::optional<std::string> read_first_line(const std::string& path);

// 去掉开头的 UTF-8 BOM
std::string strip_bom(const std::string& s);

// ============================ UTF-8 / 文本 ============================

// 解码 UTF-8 为码点；非法字节按 U+FFFD 处理
std::vector<uint32_t> utf8_decode(const std::string& s);
std::string utf8_encode(const std::vector<uint32_t>& cps);
// UTF-16 码元个数（C# string.Length）
size_t utf16_length(const std::string& s);
// C# char.IsWhiteSpace 的码点集合
bool is_unicode_space(uint32_t cp);
// C# string.Trim()（去两端 Unicode 空白）
std::string trim(const std::string& s);
// ASCII 不区分大小写比较
bool iequals_ascii(const std::string& a, const std::string& b);

// ============================ JSON ============================

using ojson = nlohmann::ordered_json;

// 数值写进 JSON：整数值写成整数（200 而不是 200.0，和 C# System.Text.Json 一样），其他写 double
ojson json_num(double v);
// 不会因非法 UTF-8 抛异常的 dump（非法字节替换为 U+FFFD；中文不转义，同 C# UnsafeRelaxedJsonEscaping）
std::string dump_json(const ojson& j, int indent = -1);
std::string dump_json(const nlohmann::json& j, int indent = -1);

}  // namespace reactor::store
