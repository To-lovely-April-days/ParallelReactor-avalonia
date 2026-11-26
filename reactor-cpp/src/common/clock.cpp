#include "common/clock.hpp"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <cstdint>
#include <string>

namespace reactor {

int64_t mono_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

int64_t wall_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

std::string iso_utc(int64_t ms) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    gmtime_r(&t, &tm);
    char buf[40];
    std::strftime(buf, sizeof buf, "%Y-%m-%dT%H:%M:%S", &tm);
    char out[48];
    std::snprintf(out, sizeof out, "%s.%03dZ", buf, static_cast<int>(((ms % 1000) + 1000) % 1000));
    return out;
}

std::string local_fmt(int64_t ms, const char* fmt) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char buf[64];
    std::strftime(buf, sizeof buf, fmt, &tm);
    return buf;
}

static int64_t days_from_civil(int y, unsigned m, unsigned d) {
    y -= m <= 2;
    const int era = (y >= 0 ? y : y - 399) / 400;
    const unsigned yoe = static_cast<unsigned>(y - era * 400);
    const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + static_cast<int>(doe) - 719468;
}

bool parse_iso(const std::string& s, int64_t& out) {
    int Y, M, D, h, m, sec;
    if (std::sscanf(s.c_str(), "%4d-%2d-%2dT%2d:%2d:%2d", &Y, &M, &D, &h, &m, &sec) != 6) return false;
    size_t p = 19;
    int frac_ms = 0;
    if (p < s.size() && s[p] == '.') {
        ++p;
        int digits = 0;
        while (p < s.size() && s[p] >= '0' && s[p] <= '9') {
            if (digits < 3) frac_ms = frac_ms * 10 + (s[p] - '0');
            ++digits; ++p;
        }
        for (; digits < 3; ++digits) frac_ms *= 10;
    }
    if (p >= s.size()) return false;  // 必须带时区
    int off_min = 0;
    if (s[p] == 'Z' || s[p] == 'z') {
        if (p + 1 != s.size()) return false;
    } else if (s[p] == '+' || s[p] == '-') {
        int oh, om;
        if (std::sscanf(s.c_str() + p + 1, "%2d:%2d", &oh, &om) != 2) return false;
        off_min = (oh * 60 + om) * (s[p] == '+' ? 1 : -1);
    } else {
        return false;
    }
    int64_t days = days_from_civil(Y, static_cast<unsigned>(M), static_cast<unsigned>(D));
    int64_t secs = days * 86400 + h * 3600 + m * 60 + sec - off_min * 60;
    out = secs * 1000 + frac_ms;
    return true;
}

}  // namespace reactor
