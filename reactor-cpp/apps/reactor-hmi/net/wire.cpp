#include "wire.hpp"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <random>
#include <array>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::hmi::net {

// ------------------------------------------------------------ SHA-1（RFC 3174）
namespace {
inline uint32_t rol(uint32_t v, int n) { return (v << n) | (v >> (32 - n)); }
}  // namespace

std::array<uint8_t, 20> sha1(const std::string& data) {
    uint32_t h[5] = {0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0};
    std::string m = data;
    uint64_t bits = static_cast<uint64_t>(data.size()) * 8;
    m.push_back(static_cast<char>(0x80));
    while (m.size() % 64 != 56) m.push_back('\0');
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<char>((bits >> (8 * i)) & 0xFF));
    for (size_t off = 0; off < m.size(); off += 64) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i) {
            const auto* p = reinterpret_cast<const uint8_t*>(m.data() + off + 4 * i);
            w[i] = (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | uint32_t(p[3]);
        }
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            uint32_t f, k;
            if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
            else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
            else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6; }
            uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d; d = c; c = rol(b, 30); b = a; a = t;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
    }
    std::array<uint8_t, 20> out{};
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j) out[static_cast<size_t>(4 * i + j)] = static_cast<uint8_t>(h[i] >> (24 - 8 * j));
    return out;
}

std::string base64(const uint8_t* p, size_t n) {
    static const char* T = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string o;
    o.reserve((n + 2) / 3 * 4);
    for (size_t i = 0; i < n; i += 3) {
        uint32_t v = uint32_t(p[i]) << 16;
        if (i + 1 < n) v |= uint32_t(p[i + 1]) << 8;
        if (i + 2 < n) v |= p[i + 2];
        o.push_back(T[(v >> 18) & 63]);
        o.push_back(T[(v >> 12) & 63]);
        o.push_back(i + 1 < n ? T[(v >> 6) & 63] : '=');
        o.push_back(i + 2 < n ? T[v & 63] : '=');
    }
    return o;
}

std::string random_bytes(size_t n) {
    std::string out(n, '\0');
    std::ifstream f("/dev/urandom", std::ios::binary);
    if (f && f.read(&out[0], static_cast<std::streamsize>(n))) return out;
    static std::random_device rd;
    for (auto& c : out) c = static_cast<char>(rd() & 0xFF);
    return out;
}

std::string uuid4() {
    std::string r = random_bytes(16);
    r[6] = static_cast<char>((r[6] & 0x0F) | 0x40);
    r[8] = static_cast<char>((r[8] & 0x3F) | 0x80);
    static const char* H = "0123456789abcdef";
    std::string o;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) o.push_back('-');
        o.push_back(H[(static_cast<uint8_t>(r[static_cast<size_t>(i)]) >> 4) & 15]);
        o.push_back(H[static_cast<uint8_t>(r[static_cast<size_t>(i)]) & 15]);
    }
    return o;
}

// ------------------------------------------------------------ 时间
namespace {
// 公历日期 → 1970-01-01 起的天数（Howard Hinnant 的 days_from_civil）
int64_t days_from_civil(int64_t y, unsigned m, unsigned d) {
    y -= m <= 2;
    const int64_t era = (y >= 0 ? y : y - 399) / 400;
    const unsigned yoe = static_cast<unsigned>(y - era * 400);
    const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + static_cast<int64_t>(doe) - 719468;
}
void civil_from_days(int64_t z, int& y, unsigned& m, unsigned& d) {
    z += 719468;
    const int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    const unsigned doe = static_cast<unsigned>(z - era * 146097);
    const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    const int64_t yy = static_cast<int64_t>(yoe) + era * 400;
    const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const unsigned mp = (5 * doy + 2) / 153;
    d = doy - (153 * mp + 2) / 5 + 1;
    m = mp < 10 ? mp + 3 : mp - 9;
    y = static_cast<int>(yy + (m <= 2));
}
bool digits(const std::string& s, size_t pos, size_t n, int& out) {
    if (pos + n > s.size()) return false;
    out = 0;
    for (size_t i = pos; i < pos + n; ++i) {
        if (s[i] < '0' || s[i] > '9') return false;
        out = out * 10 + (s[i] - '0');
    }
    return true;
}
}  // namespace

std::optional<int64_t> parse_iso_ms(const std::string& s) {
    int Y, M, D, h = 0, mi = 0, se = 0;
    if (!digits(s, 0, 4, Y) || s.size() < 10 || s[4] != '-' || !digits(s, 5, 2, M) || s[7] != '-' || !digits(s, 8, 2, D))
        return std::nullopt;
    size_t p = 10;
    int64_t frac_ms = 0;
    if (p < s.size() && (s[p] == 'T' || s[p] == ' ')) {
        if (!digits(s, p + 1, 2, h) || s.size() < p + 6 || s[p + 3] != ':' || !digits(s, p + 4, 2, mi)) return std::nullopt;
        p += 6;
        if (p < s.size() && s[p] == ':') {
            if (!digits(s, p + 1, 2, se)) return std::nullopt;
            p += 3;
        }
        if (p < s.size() && (s[p] == '.' || s[p] == ',')) {
            ++p;
            int scale = 100, n = 0;
            while (p < s.size() && s[p] >= '0' && s[p] <= '9') {
                if (n < 3) frac_ms += (s[p] - '0') * scale;
                scale /= 10;
                ++n;
                ++p;
            }
        }
    }
    int64_t off_min = 0;
    if (p < s.size()) {
        if (s[p] == 'Z' || s[p] == 'z') ++p;
        else if (s[p] == '+' || s[p] == '-') {
            int oh, om = 0;
            if (!digits(s, p + 1, 2, oh)) return std::nullopt;
            size_t q = p + 3;
            if (q < s.size() && s[q] == ':') ++q;
            if (q < s.size() && !digits(s, q, 2, om)) return std::nullopt;
            off_min = (oh * 60 + om) * (s[p] == '-' ? -1 : 1);
            p = q + 2;
        }
    }
    if (M < 1 || M > 12 || D < 1 || D > 31 || h > 23 || mi > 59 || se > 60) return std::nullopt;
    int64_t days = days_from_civil(Y, static_cast<unsigned>(M), static_cast<unsigned>(D));
    int64_t secs = days * 86400 + h * 3600 + mi * 60 + se - off_min * 60;
    return secs * 1000 + frac_ms;
}

std::string iso_ms(int64_t ms) {
    int64_t secs = ms >= 0 ? ms / 1000 : (ms - 999) / 1000;
    int msec = static_cast<int>(ms - secs * 1000);
    int64_t days = secs >= 0 ? secs / 86400 : (secs - 86399) / 86400;
    int64_t rem = secs - days * 86400;
    int y;
    unsigned m, d;
    civil_from_days(days, y, m, d);
    char b[40];
    std::snprintf(b, sizeof b, "%04d-%02u-%02uT%02d:%02d:%02d.%03dZ", y, m, d, static_cast<int>(rem / 3600),
                  static_cast<int>(rem / 60 % 60), static_cast<int>(rem % 60), msec);
    return b;
}

std::string url_encode(const std::string& s) {
    static const char* H = "0123456789ABCDEF";
    std::string o;
    for (unsigned char c : s) {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            o.push_back(static_cast<char>(c));
        else {
            o.push_back('%');
            o.push_back(H[c >> 4]);
            o.push_back(H[c & 15]);
        }
    }
    return o;
}

int64_t mono_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}
int64_t wall_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

}  // namespace reactor::hmi::net
