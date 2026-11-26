#include "store/history_files.hpp"

#include <fcntl.h>
#include <unistd.h>

#include <algorithm>
#include <fstream>
#include <map>
#include <mutex>
#include <regex>
#include <tuple>

#include "store/csv.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::store {

// ============================ SafePath ============================

namespace safe_path {

const std::vector<std::string>& types() {
    static const std::vector<std::string> t = {kTelemetry, kEvents, kSessions, kLeak};
    return t;
}

namespace {

const std::regex& re_telemetry() { static const std::regex r(R"(telemetry-\d{8}(-\d{1,3})?\.csv)"); return r; }
const std::regex& re_events_csv() { static const std::regex r(R"(events-\d{8}(-\d{1,3})?\.csv)"); return r; }
const std::regex& re_events_jsonl() { static const std::regex r(R"(events-\d{8}\.jsonl)"); return r; }
const std::regex& re_leak() { static const std::regex r(R"(leak-\d{8}-\d{6}(\.csv|-summary\.csv|-summary\.json))"); return r; }
const std::regex& re_leak_run() { static const std::regex r(R"(leak-\d{8}-\d{6})"); return r; }

// 组合附加符号（NFD 分解形式里出现的典型字符）：这类名字一律拒绝，避免「同名不同字节」绕过白名单。
// C# 用 string.IsNormalized(FormC)；这里没有完整的 Unicode 规范化表，按码点范围近似（只会多拒，不会多放）。
bool is_combining(uint32_t cp) {
    return (cp >= 0x0300 && cp <= 0x036F) || (cp >= 0x1AB0 && cp <= 0x1AFF) || (cp >= 0x1DC0 && cp <= 0x1DFF) ||
           (cp >= 0x20D0 && cp <= 0x20FF) || (cp >= 0xFE20 && cp <= 0xFE2F) || (cp >= 0x3099 && cp <= 0x309A) ||
           (cp >= 0x1160 && cp <= 0x11FF);   // 韩文字母（分解的谚文音节）
}

// ^(?!\.)[^/\\\0:*?"<>|\s]{1,200}\.(csv|json)\z（长度按 UTF-16 码元计，同 C#）
bool session_name_ok(const std::string& name) {
    std::string stem;
    if (name.size() > 4 && name.compare(name.size() - 4, 4, ".csv") == 0) stem = name.substr(0, name.size() - 4);
    else if (name.size() > 5 && name.compare(name.size() - 5, 5, ".json") == 0) stem = name.substr(0, name.size() - 5);
    else return false;
    if (stem.empty() || stem[0] == '.') return false;
    size_t units = 0;
    for (uint32_t cp : utf8_decode(stem)) {
        if (cp == '/' || cp == '\\' || cp == 0 || cp == ':' || cp == '*' || cp == '?' || cp == '"' || cp == '<' || cp == '>' ||
            cp == '|' || cp == 0xFFFD || is_unicode_space(cp))
            return false;
        units += cp >= 0x10000 ? 2 : 1;
    }
    return units >= 1 && units <= 200;
}

}  // namespace

bool is_valid_name(const std::string& type, const std::string& name) {
    if (name.empty() || name.find("..") != std::string::npos) return false;
    if (name.find('/') != std::string::npos || name.find('\\') != std::string::npos || name.find('\0') != std::string::npos)
        return false;
    for (uint32_t cp : utf8_decode(name))
        if (is_combining(cp)) return false;
    if (type == kTelemetry) return std::regex_match(name, re_telemetry());
    if (type == kEvents) return std::regex_match(name, re_events_csv()) || std::regex_match(name, re_events_jsonl());
    if (type == kSessions) return session_name_ok(name);
    if (type == kLeak) return std::regex_match(name, re_leak());
    return false;
}

bool is_leak_run_id(const std::string& s) { return std::regex_match(s, re_leak_run()); }

std::string sub_dir(const std::string& type, const std::string& name) {
    if (type == kEvents) return name.size() >= 6 && name.compare(name.size() - 6, 6, ".jsonl") == 0 ? "events" : "";
    if (type == kSessions) return "sessions";
    if (type == kLeak) return "leak";
    return "";
}

std::optional<std::string> try_resolve(const std::string& root_in, const std::string& type, const std::string& name) {
    if (!is_valid_name(type, name)) return std::nullopt;
    std::string root = root_in;
    if (root.empty()) return std::nullopt;
    if (root[0] != '/') {
        char cwd[4096];
        if (!::getcwd(cwd, sizeof cwd)) return std::nullopt;
        root = join_path(cwd, root);
    }
    while (root.size() > 1 && root.back() == '/') root.pop_back();
    std::string prefix = root == "/" ? "/" : root + "/";
    std::string sub = sub_dir(type, name);
    std::string full = prefix + (sub.empty() ? "" : sub + "/") + name;
    if (full.compare(0, prefix.size(), prefix) != 0) return std::nullopt;
    return full;
}

}  // namespace safe_path

// ============================ HistoryFiles ============================

namespace history_files {

namespace {

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

bool ends_with(const std::string& s, const char* suf) {
    size_t n = std::char_traits<char>::length(suf);
    return s.size() >= n && s.compare(s.size() - n, n, suf) == 0;
}
bool starts_with(const std::string& s, const char* pre) {
    size_t n = std::char_traits<char>::length(pre);
    return s.size() >= n && s.compare(0, n, pre) == 0;
}

std::vector<std::string> enum_files(const std::string& dir, const char* prefix, const char* suffix) {
    std::vector<std::string> out;
    for (auto& n : list_files(dir))
        if (starts_with(n, prefix) && ends_with(n, suffix)) out.push_back(join_path(dir, n));
    return out;
}

std::optional<std::string> last_complete_line_slow(const std::string& path) {
    bool complete = ends_with_newline(path);
    std::optional<std::string> prev, prev_prev;
    for_each_line(path, [&](const std::string& l) {
        prev_prev = prev;
        prev = l;
        return true;
    });
    return complete ? prev : prev_prev;
}

}  // namespace

std::optional<std::pair<std::string, int>> day_of(const std::string& name) {
    static const std::regex re(R"((telemetry|events)-(\d{8})(?:-(\d{1,3}))?\.(csv|jsonl))");
    std::smatch m;
    if (!std::regex_match(name, m, re)) return std::nullopt;
    int suffix = m[3].matched ? std::stoi(m[3].str()) : 0;
    return std::make_pair(m[2].str(), suffix);
}

std::vector<HistoryFileInfo> list(const std::string& data_dir, const std::string& type, bool parse_range) {
    std::vector<HistoryFileInfo> out;
    std::vector<std::string> files;
    if (type == safe_path::kTelemetry) files = enum_files(data_dir, "telemetry-", ".csv");
    else if (type == safe_path::kEvents) {
        files = enum_files(data_dir, "events-", ".csv");
        auto j = enum_files(join_path(data_dir, "events"), "events-", ".jsonl");
        files.insert(files.end(), j.begin(), j.end());
    } else if (type == safe_path::kSessions) files = enum_files(join_path(data_dir, "sessions"), "", ".csv");
    else if (type == safe_path::kLeak) files = enum_files(join_path(data_dir, "leak"), "leak-", "");
    for (auto& f : files) {
        std::string name = base_name(f);
        if (!safe_path::is_valid_name(type, name)) continue;
        auto st = stat_path(f);
        if (!st.exists || st.is_dir) continue;
        HistoryFileInfo info;
        info.name = name;
        info.type = type;
        info.bytes = st.size;
        info.modified_at_ms = st.mtime_ms;
        if (parse_range && ends_with(name, ".csv") &&
            (type == safe_path::kTelemetry || type == safe_path::kEvents || type == safe_path::kSessions)) {
            auto r = first_last_timestamp(f);
            info.from_ms = r.first;
            info.to_ms = r.second;
        }
        if (auto d = day_of(name)) info.day = d->first;
        out.push_back(std::move(info));
    }
    std::sort(out.begin(), out.end(), [](const HistoryFileInfo& a, const HistoryFileInfo& b) { return a.name < b.name; });
    return out;
}

std::vector<std::string> select_daily_files(const std::string& data_dir, int64_t from_ms, int64_t to_ms, const std::string& prefix) {
    std::vector<std::tuple<std::string, int, std::string>> found;
    if (to_ms <= from_ms || !dir_exists(data_dir)) return {};
    std::string d_from = fmt_wall(WallTime::local(from_ms), "yyyyMMdd");
    std::string d_to = fmt_wall(WallTime::local(to_ms - 1), "yyyyMMdd");
    for (auto& f : enum_files(data_dir, (prefix + "-").c_str(), ".csv")) {
        auto d = day_of(base_name(f));
        if (!d || d->first < d_from || d->first > d_to) continue;
        found.emplace_back(d->first, d->second, f);
    }
    std::sort(found.begin(), found.end());
    std::vector<std::string> out;
    for (auto& t : found) out.push_back(std::get<2>(t));
    return out;
}

std::optional<int64_t> parse_row_time(const std::string& line) {
    if (line.empty()) return std::nullopt;
    auto comma = line.find(',');
    std::string s = comma == std::string::npos ? line : line.substr(0, comma);
    size_t a = 0, b = s.size();
    while (a < b && s[a] == '"') ++a;
    while (b > a && s[b - 1] == '"') --b;
    s = s.substr(a, b - a);
    if (s.size() < 19) return std::nullopt;
    int64_t ms = 0;
    if (!parse_time_loose(s, ms)) return std::nullopt;
    return ms;
}

bool try_parse_query_time(const std::string& in, int64_t& out) {
    std::string s = trim(in);
    if (s.empty()) return false;
    static const std::regex zone(R"([+-]\d{2}:?\d{2}$)");
    bool has_zone = s.back() == 'Z' || s.back() == 'z' || std::regex_search(s, zone);
    if (!has_zone) return false;
    bool had = false;
    return parse_time_loose(s, out, &had) && had;
}

std::optional<std::string> last_complete_line(const std::string& path) {
    int fd = ::open(path.c_str(), O_RDONLY);
    if (fd < 0) return std::nullopt;
    off_t len = ::lseek(fd, 0, SEEK_END);
    if (len <= 0) {
        ::close(fd);
        return std::nullopt;
    }
    size_t take = static_cast<size_t>(std::min<off_t>(static_cast<off_t>(kTailChunk), len));
    std::string buf(take, '\0');
    size_t got = 0;
    while (got < take) {
        ssize_t n = ::pread(fd, &buf[got], take - got, len - static_cast<off_t>(take) + static_cast<off_t>(got));
        if (n <= 0) break;
        got += static_cast<size_t>(n);
    }
    ::close(fd);
    bool whole = static_cast<off_t>(take) == len;
    long end = static_cast<long>(got);
    auto last_nl = [&](long from) -> long {   // 从 from（含）往前找 '\n'
        for (long i = from; i >= 0; --i)
            if (buf[static_cast<size_t>(i)] == '\n') return i;
        return -1;
    };
    if (end > 0 && buf[static_cast<size_t>(end - 1)] == '\n') {
        --end;   // 以换行结束：末行完整
    } else {
        long cut = last_nl(end - 1);   // 末行是半行：丢掉它，取前面那一行
        if (cut < 0) return whole ? std::nullopt : last_complete_line_slow(path);
        end = cut;
    }
    long start = end > 0 ? last_nl(end - 1) + 1 : 0;
    if (start == 0 && !whole) return last_complete_line_slow(path);   // 这一行可能被块边界截断
    if (whole && start == 0 && got >= 3 && buf.compare(0, 3, kUtf8Bom) == 0) start = 3;
    std::string line = start <= end ? buf.substr(static_cast<size_t>(start), static_cast<size_t>(end - start)) : std::string();
    while (!line.empty() && line.back() == '\r') line.pop_back();
    return line;
}

std::pair<std::optional<int64_t>, std::optional<int64_t>> compute_first_last(const std::string& path) {
    std::optional<int64_t> first, last;
    std::optional<std::string> header;
    int n = 0;
    for_each_line(path, [&](const std::string& l) {
        if (n++ == 0) { header = l; return true; }
        first = parse_row_time(l);
        return false;
    });
    auto ll = last_complete_line(path);
    if (ll && ll != header) last = parse_row_time(*ll);
    return {first, last ? last : first};
}

std::pair<std::optional<int64_t>, std::optional<int64_t>> first_last_timestamp(const std::string& path) {
    struct Entry { int64_t len, mtime; std::optional<int64_t> from, to; };
    static std::mutex mu;
    static std::map<std::string, Entry> cache;
    auto st = stat_path(path);
    {
        std::lock_guard<std::mutex> lk(mu);
        auto it = cache.find(path);
        if (it != cache.end() && it->second.len == st.size && it->second.mtime == st.mtime_ms) return {it->second.from, it->second.to};
    }
    auto r = compute_first_last(path);
    std::lock_guard<std::mutex> lk(mu);
    if (cache.size() > 2000) cache.clear();
    cache[path] = Entry{st.size, st.mtime_ms, r.first, r.second};
    return r;
}

std::optional<std::vector<int>> column_map(const std::vector<std::string>& target, const std::vector<std::string>& source) {
    if (target == source) return std::nullopt;
    std::vector<int> map(target.size(), -1);
    for (size_t i = 0; i < target.size(); ++i) {
        auto it = std::find(source.begin(), source.end(), target[i]);
        if (it != source.end()) map[i] = static_cast<int>(it - source.begin());
    }
    return map;
}

std::string remap(const std::string& line, const std::vector<int>& map) {
    auto src = csv::split(line);
    std::vector<std::string> out(map.size());
    for (size_t i = 0; i < map.size(); ++i)
        out[i] = map[i] >= 0 && static_cast<size_t>(map[i]) < src.size() ? src[static_cast<size_t>(map[i])] : "";
    return csv::line(out);
}

int64_t write_range(const std::vector<std::string>& files, int64_t from_ms, int64_t to_ms,
                    const std::function<bool(const std::string&)>& out) {
    int64_t rows = 0;
    bool header_done = false;
    std::vector<std::string> header;
    bool cancelled = false;
    auto emit = [&](const std::string& line, const std::optional<std::vector<int>>& map) {
        auto t = parse_row_time(line);
        if (!t || *t < from_ms || *t >= to_ms) return;
        if (!out((map ? remap(line, *map) : line) + "\n")) { cancelled = true; return; }
        ++rows;
    };
    for (auto& f : files) {
        bool complete = ends_with_newline(f);
        std::optional<std::string> pending;
        std::optional<std::vector<int>> map;
        int n = 0;
        for_each_line(f, [&](const std::string& line) {
            if (++n == 1) {
                if (!header_done) {
                    if (!out(std::string(kUtf8Bom) + line + "\n")) { cancelled = true; return false; }
                    header_done = true;
                    header = csv::split(line);
                } else {
                    map = column_map(header, csv::split(line));
                }
                return true;
            }
            if (pending) emit(*pending, map);
            pending = line;
            return !cancelled;
        });
        if (cancelled) return -1;
        if (pending && complete) emit(*pending, map);
        if (cancelled) return -1;
    }
    if (!header_done && !out(kUtf8Bom)) return -1;
    return rows;
}

std::optional<std::pair<std::string, std::string>> parse_session_name(const std::string& name) {
    static const std::regex re(R"((.+)-(\d{8}-\d{6})\.csv)");
    std::smatch m;
    if (!std::regex_match(name, m, re)) return std::nullopt;
    return std::make_pair(m[1].str(), m[2].str());
}

}  // namespace history_files
}  // namespace reactor::store
