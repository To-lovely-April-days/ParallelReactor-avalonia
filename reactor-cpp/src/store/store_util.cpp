#include "store/store_util.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <atomic>
#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

const char kUtf8Bom[] = "\xEF\xBB\xBF";

// ============================ 时间 ============================

int64_t days_from_civil(int y, int m, int d) {
    y -= m <= 2;
    const int64_t era = (y >= 0 ? y : y - 399) / 400;
    const int64_t yoe = y - era * 400;
    const int64_t doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    const int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

void civil_from_days(int64_t z, int& y, int& m, int& d) {
    z += 719468;
    const int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    const int64_t doe = z - era * 146097;
    const int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    const int64_t yy = yoe + era * 400;
    const int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const int64_t mp = (5 * doy + 2) / 153;
    d = static_cast<int>(doy - (153 * mp + 2) / 5 + 1);
    m = static_cast<int>(mp < 10 ? mp + 3 : mp - 9);
    y = static_cast<int>(yy + (m <= 2));
}

static int64_t floor_div(int64_t a, int64_t b) { return a / b - ((a % b != 0) && ((a < 0) != (b < 0))); }

int local_offset_min(int64_t utc_ms) {
    std::time_t t = static_cast<std::time_t>(floor_div(utc_ms, 1000));
    std::tm tm{};
    localtime_r(&t, &tm);
    return static_cast<int>(tm.tm_gmtoff / 60);
}

WallTime WallTime::local(int64_t utc_ms) { return WallTime{utc_ms, local_offset_min(utc_ms)}; }

namespace {
struct Fields { int y, mo, d, h, mi, s, ms; };
Fields fields_of(int64_t local_ms) {
    int64_t days = floor_div(local_ms, 86400000);
    int64_t rem = local_ms - days * 86400000;
    Fields f{};
    civil_from_days(days, f.y, f.mo, f.d);
    f.h = static_cast<int>(rem / 3600000);
    f.mi = static_cast<int>(rem / 60000 % 60);
    f.s = static_cast<int>(rem / 1000 % 60);
    f.ms = static_cast<int>(rem % 1000);
    return f;
}
}  // namespace

std::string fmt_wall(const WallTime& t, const char* pattern) {
    Fields f = fields_of(t.ms + static_cast<int64_t>(t.offset_min) * 60000);
    std::string out;
    char buf[16];
    for (const char* p = pattern; *p;) {
        auto starts = [&](const char* tok) { return std::strncmp(p, tok, std::strlen(tok)) == 0; };
        if (starts("yyyy")) { std::snprintf(buf, sizeof buf, "%04d", f.y); out += buf; p += 4; }
        else if (starts("MM")) { std::snprintf(buf, sizeof buf, "%02d", f.mo); out += buf; p += 2; }
        else if (starts("dd")) { std::snprintf(buf, sizeof buf, "%02d", f.d); out += buf; p += 2; }
        else if (starts("HH")) { std::snprintf(buf, sizeof buf, "%02d", f.h); out += buf; p += 2; }
        else if (starts("mm")) { std::snprintf(buf, sizeof buf, "%02d", f.mi); out += buf; p += 2; }
        else if (starts("ss")) { std::snprintf(buf, sizeof buf, "%02d", f.s); out += buf; p += 2; }
        else if (starts("fff")) { std::snprintf(buf, sizeof buf, "%03d", f.ms); out += buf; p += 3; }
        else { out += *p; ++p; }
    }
    return out;
}

std::string fmt_offset_time(const WallTime& t) {
    std::string s = fmt_wall(t, "yyyy-MM-ddTHH:mm:ss.fff");
    int off = t.offset_min;
    char sign = off < 0 ? '-' : '+';
    if (off < 0) off = -off;
    char buf[16];
    std::snprintf(buf, sizeof buf, "%c%02d:%02d", sign, off / 60, off % 60);
    return s + buf;
}

std::string fmt_utc_json(int64_t utc_ms) {
    WallTime t{utc_ms, 0};
    std::string s = fmt_wall(t, "yyyy-MM-ddTHH:mm:ss");
    int ms = static_cast<int>(((utc_ms % 1000) + 1000) % 1000);
    if (ms != 0) {
        char buf[8];
        std::snprintf(buf, sizeof buf, ".%03d", ms);
        std::string frac = buf;
        while (frac.back() == '0') frac.pop_back();
        s += frac;
    }
    return s + "Z";
}

bool parse_time_loose(const std::string& in, int64_t& out, bool* had_zone) {
    std::string s = trim(in);
    auto digits = [&](size_t pos, size_t n, int& v) {
        if (pos + n > s.size()) return false;
        v = 0;
        for (size_t i = 0; i < n; ++i) {
            char c = s[pos + i];
            if (c < '0' || c > '9') return false;
            v = v * 10 + (c - '0');
        }
        return true;
    };
    int Y, M, D, h, mi, sec = 0;
    if (!digits(0, 4, Y) || s.size() < 16 || s[4] != '-' || !digits(5, 2, M) || s[7] != '-' || !digits(8, 2, D)) return false;
    if (s[10] != 'T' && s[10] != 't' && s[10] != ' ') return false;
    if (!digits(11, 2, h) || s[13] != ':' || !digits(14, 2, mi)) return false;
    size_t p = 16;
    if (p < s.size() && s[p] == ':') {
        if (!digits(p + 1, 2, sec)) return false;
        p += 3;
    }
    int frac_ms = 0;
    if (p < s.size() && s[p] == '.') {
        ++p;
        int nd = 0;
        size_t start = p;
        while (p < s.size() && s[p] >= '0' && s[p] <= '9') {
            if (nd < 3) frac_ms = frac_ms * 10 + (s[p] - '0');
            ++nd;
            ++p;
        }
        if (p == start) return false;
        for (; nd < 3; ++nd) frac_ms *= 10;
    }
    if (M < 1 || M > 12 || D < 1 || D > 31 || h > 23 || mi > 59 || sec > 60) return false;
    int off_min = 0;
    bool zone = false;
    if (p < s.size()) {
        if ((s[p] == 'Z' || s[p] == 'z') && p + 1 == s.size()) {
            zone = true;
        } else if (s[p] == '+' || s[p] == '-') {
            int oh, om;
            if (!digits(p + 1, 2, oh)) return false;
            size_t q = p + 3;
            if (q < s.size() && s[q] == ':') ++q;
            if (!digits(q, 2, om) || q + 2 != s.size()) return false;
            off_min = (oh * 60 + om) * (s[p] == '+' ? 1 : -1);
            zone = true;
        } else {
            return false;
        }
    }
    if (had_zone) *had_zone = zone;
    int64_t secs = days_from_civil(Y, M, D) * 86400 + h * 3600 + mi * 60 + sec - static_cast<int64_t>(off_min) * 60;
    out = secs * 1000 + frac_ms;
    return true;
}

// ============================ 文件 ============================

std::string join_path(const std::string& a, const std::string& b) {
    if (a.empty()) return b;
    if (b.empty()) return a;
    if (a.back() == '/') return a + b;
    return a + "/" + b;
}

std::string dir_name(const std::string& path) {
    auto p = path.find_last_of('/');
    if (p == std::string::npos) return ".";
    if (p == 0) return "/";
    return path.substr(0, p);
}

std::string base_name(const std::string& path) {
    auto p = path.find_last_of('/');
    return p == std::string::npos ? path : path.substr(p + 1);
}

FileStat stat_path(const std::string& path) {
    FileStat fs;
    struct stat st{};
    if (::stat(path.c_str(), &st) != 0) return fs;
    fs.exists = true;
    fs.is_dir = S_ISDIR(st.st_mode);
    fs.size = static_cast<int64_t>(st.st_size);
#if defined(__APPLE__)
    fs.mtime_ms = static_cast<int64_t>(st.st_mtimespec.tv_sec) * 1000 + st.st_mtimespec.tv_nsec / 1000000;
#else
    fs.mtime_ms = static_cast<int64_t>(st.st_mtim.tv_sec) * 1000 + st.st_mtim.tv_nsec / 1000000;
#endif
    return fs;
}

bool file_exists(const std::string& path) {
    auto s = stat_path(path);
    return s.exists && !s.is_dir;
}

bool dir_exists(const std::string& path) {
    auto s = stat_path(path);
    return s.exists && s.is_dir;
}

bool make_dirs(const std::string& dir, std::string* err) {
    if (dir.empty() || dir_exists(dir)) return true;
    std::string cur;
    size_t i = 0;
    if (dir[0] == '/') { cur = "/"; i = 1; }
    while (i <= dir.size()) {
        size_t j = dir.find('/', i);
        if (j == std::string::npos) j = dir.size();
        std::string part = dir.substr(i, j - i);
        if (!part.empty()) {
            cur = cur.empty() || cur == "/" ? cur + part : cur + "/" + part;
            if (::mkdir(cur.c_str(), 0755) != 0 && errno != EEXIST) {
                if (err) *err = std::string("mkdir ") + cur + ": " + std::strerror(errno);
                return false;
            }
            if (!dir_exists(cur)) {
                if (err) *err = cur + " 不是目录";
                return false;
            }
        }
        i = j + 1;
    }
    return true;
}

std::vector<std::string> list_files(const std::string& dir) {
    std::vector<std::string> out;
    DIR* d = ::opendir(dir.c_str());
    if (!d) return out;
    while (dirent* e = ::readdir(d)) {
        std::string n = e->d_name;
        if (n == "." || n == "..") continue;
        if (file_exists(join_path(dir, n))) out.push_back(n);
    }
    ::closedir(d);
    return out;
}

bool read_file(const std::string& path, std::string& out) {
    out.clear();
    int fd = ::open(path.c_str(), O_RDONLY);
    if (fd < 0) return false;
    char buf[65536];
    for (;;) {
        ssize_t n = ::read(fd, buf, sizeof buf);
        if (n < 0) {
            if (errno == EINTR) continue;
            ::close(fd);
            return false;
        }
        if (n == 0) break;
        out.append(buf, static_cast<size_t>(n));
    }
    ::close(fd);
    return true;
}

bool write_file_atomic(const std::string& path, const std::string& data, bool do_fsync, std::string* err) {
    static std::atomic<unsigned> counter{0};
    std::string dir = dir_name(path);
    if (!make_dirs(dir, err)) return false;
    std::string tmp = path + ".tmp-" + std::to_string(::getpid()) + "-" + std::to_string(counter++);
    int fd = ::open(tmp.c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) {
        if (err) *err = std::string("open ") + tmp + ": " + std::strerror(errno);
        return false;
    }
    size_t off = 0;
    while (off < data.size()) {
        ssize_t n = ::write(fd, data.data() + off, data.size() - off);
        if (n < 0) {
            if (errno == EINTR) continue;
            if (err) *err = std::string("write ") + tmp + ": " + std::strerror(errno);
            ::close(fd);
            ::unlink(tmp.c_str());
            return false;
        }
        off += static_cast<size_t>(n);
    }
    if (do_fsync && ::fsync(fd) != 0) {
        if (err) *err = std::string("fsync ") + tmp + ": " + std::strerror(errno);
        ::close(fd);
        ::unlink(tmp.c_str());
        return false;
    }
    ::close(fd);
    if (::rename(tmp.c_str(), path.c_str()) != 0) {
        if (err) *err = std::string("rename ") + path + ": " + std::strerror(errno);
        ::unlink(tmp.c_str());
        return false;
    }
    if (do_fsync) {
        int dfd = ::open(dir.c_str(), O_RDONLY);
        if (dfd >= 0) {
            (void)::fsync(dfd);
            ::close(dfd);
        }
    }
    return true;
}

bool ends_with_newline(const std::string& path) {
    int fd = ::open(path.c_str(), O_RDONLY);
    if (fd < 0) return false;
    off_t len = ::lseek(fd, 0, SEEK_END);
    if (len <= 0) {
        ::close(fd);
        return len == 0;
    }
    char c = 0;
    bool ok = ::pread(fd, &c, 1, len - 1) == 1 && c == '\n';
    ::close(fd);
    return ok;
}

std::optional<std::string> read_first_line(const std::string& path) {
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return std::nullopt;
    std::string line;
    int c;
    bool any = false;
    while ((c = std::fgetc(f)) != EOF) {
        any = true;
        if (c == '\n') break;
        line += static_cast<char>(c);
    }
    std::fclose(f);
    if (!any) return std::nullopt;
    if (!line.empty() && line.back() == '\r') line.pop_back();
    return strip_bom(line);
}

std::string strip_bom(const std::string& s) {
    if (s.size() >= 3 && s.compare(0, 3, kUtf8Bom) == 0) return s.substr(3);
    return s;
}

// ============================ UTF-8 ============================

std::vector<uint32_t> utf8_decode(const std::string& s) {
    std::vector<uint32_t> out;
    out.reserve(s.size());
    size_t i = 0;
    const auto* b = reinterpret_cast<const unsigned char*>(s.data());
    while (i < s.size()) {
        unsigned char c = b[i];
        uint32_t cp;
        int n;
        if (c < 0x80) { cp = c; n = 0; }
        else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; n = 1; }
        else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; n = 2; }
        else if ((c & 0xF8) == 0xF0) { cp = c & 0x07; n = 3; }
        else { out.push_back(0xFFFD); ++i; continue; }
        bool ok = true;
        for (int k = 1; k <= n; ++k) {
            if (i + k >= s.size() || (b[i + k] & 0xC0) != 0x80) { ok = false; break; }
            cp = (cp << 6) | (b[i + k] & 0x3F);
        }
        if (!ok) { out.push_back(0xFFFD); ++i; continue; }
        out.push_back(cp);
        i += static_cast<size_t>(n) + 1;
    }
    return out;
}

std::string utf8_encode(const std::vector<uint32_t>& cps) {
    std::string out;
    for (uint32_t cp : cps) {
        if (cp < 0x80) out += static_cast<char>(cp);
        else if (cp < 0x800) { out += static_cast<char>(0xC0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (cp >> 18));
            out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        }
    }
    return out;
}

size_t utf16_length(const std::string& s) {
    size_t n = 0;
    for (uint32_t cp : utf8_decode(s)) n += cp >= 0x10000 ? 2 : 1;
    return n;
}

bool is_unicode_space(uint32_t cp) {
    if (cp == ' ' || (cp >= 0x09 && cp <= 0x0D)) return true;
    switch (cp) {
        case 0x85: case 0xA0: case 0x1680: case 0x2028: case 0x2029: case 0x202F: case 0x205F: case 0x3000:
            return true;
        default:
            return cp >= 0x2000 && cp <= 0x200A;
    }
}

std::string trim(const std::string& s) {
    auto cps = utf8_decode(s);
    size_t a = 0, b = cps.size();
    while (a < b && is_unicode_space(cps[a])) ++a;
    while (b > a && is_unicode_space(cps[b - 1])) --b;
    if (a == 0 && b == cps.size()) return s;
    return utf8_encode(std::vector<uint32_t>(cps.begin() + static_cast<long>(a), cps.begin() + static_cast<long>(b)));
}

bool iequals_ascii(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

// ============================ JSON ============================

ojson json_num(double v) {
    if (v == static_cast<double>(static_cast<int64_t>(v)) && v > -1e15 && v < 1e15 && !(v == 0 && std::signbit(v)))
        return ojson(static_cast<int64_t>(v));
    return ojson(v);
}

std::string dump_json(const ojson& j, int indent) {
    return j.dump(indent, ' ', false, ojson::error_handler_t::replace);
}

std::string dump_json(const nlohmann::json& j, int indent) {
    return j.dump(indent, ' ', false, nlohmann::json::error_handler_t::replace);
}

}  // namespace reactor::store
