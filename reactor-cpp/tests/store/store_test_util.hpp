// store 测试共用：临时目录（mkdtemp，结束时递归删除）、时刻构造、读文件行。
#pragma once

#include <ftw.h>
#include <stdlib.h>
#include <unistd.h>

#include <cstdint>
#include <ctime>
#include <fstream>
#include <string>
#include <vector>

#include "store/store_util.hpp"

namespace store_test {

inline int rm_entry(const char* p, const struct stat*, int, struct FTW*) { return ::remove(p); }

struct TempDir {
    std::string path;
    TempDir() {
        char tmpl[] = "/tmp/reactor-store-test-XXXXXX";
        char* p = ::mkdtemp(tmpl);
        path = p ? p : "/tmp/reactor-store-test-fallback";
    }
    ~TempDir() { ::nftw(path.c_str(), rm_entry, 16, FTW_DEPTH | FTW_PHYS); }
    std::string operator/(const std::string& rel) const { return path + "/" + rel; }
};

// 指定偏移下的本地时刻 → UTC 毫秒
inline int64_t ms_at(int y, int mo, int d, int h, int mi, int s, int ms = 0, int offset_min = 0) {
    return reactor::store::days_from_civil(y, mo, d) * 86400000LL + h * 3600000LL + mi * 60000LL + s * 1000LL + ms -
           offset_min * 60000LL;
}

// 本机时区的本地时刻 → UTC 毫秒（选日文件的测试必须用本机时区才不依赖运行机器）
inline int64_t local_ms(int y, int mo, int d, int h, int mi, int s, int ms = 0) {
    std::tm tm{};
    tm.tm_year = y - 1900;
    tm.tm_mon = mo - 1;
    tm.tm_mday = d;
    tm.tm_hour = h;
    tm.tm_min = mi;
    tm.tm_sec = s;
    tm.tm_isdst = -1;
    return static_cast<int64_t>(std::mktime(&tm)) * 1000 + ms;
}

inline reactor::store::WallTime at8(int y, int mo, int d, int h, int mi, int s, int ms = 0) {
    return reactor::store::WallTime::with_offset(ms_at(y, mo, d, h, mi, s, ms, 480), 480);
}

inline std::string slurp(const std::string& path) {
    std::string s;
    reactor::store::read_file(path, s);
    return s;
}

// 按行读（去 BOM、去 \r；同 C# File.ReadAllLines）
inline std::vector<std::string> lines_of(const std::string& path) {
    std::vector<std::string> out;
    std::ifstream in(path, std::ios::binary);
    std::string l;
    bool first = true;
    while (std::getline(in, l)) {
        if (!l.empty() && l.back() == '\r') l.pop_back();
        if (first) { l = reactor::store::strip_bom(l); first = false; }
        out.push_back(l);
    }
    return out;
}

inline void spit(const std::string& path, const std::string& text) {
    std::ofstream o(path, std::ios::binary);
    o << text;
}

inline bool contains(const std::string& hay, const std::string& needle) { return hay.find(needle) != std::string::npos; }

}  // namespace store_test
