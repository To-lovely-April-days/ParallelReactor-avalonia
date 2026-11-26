#include "cmd/cmd_util.hpp"

#include <cstdio>
#include <random>
#include <vector>
#include <cstdint>
#include <string>

namespace reactor::cmd {

namespace {

void fill_random(unsigned char* p, size_t n) {
    size_t got = 0;
    if (FILE* f = std::fopen("/dev/urandom", "rb")) {
        got = std::fread(p, 1, n, f);
        std::fclose(f);
    }
    if (got < n) {
        std::random_device rd;
        for (size_t i = got; i < n; ++i) p[i] = static_cast<unsigned char>(rd() & 0xFF);
    }
}

const char kHex[] = "0123456789abcdef";

// 解一个 UTF-8 码点；非法字节按 U+FFFD 处理并前进 1
uint32_t next_cp(const std::string& s, size_t& i) {
    unsigned char c = static_cast<unsigned char>(s[i]);
    auto cont = [&](size_t k) { return k < s.size() && (static_cast<unsigned char>(s[k]) & 0xC0) == 0x80; };
    if (c < 0x80) { ++i; return c; }
    if ((c & 0xE0) == 0xC0 && cont(i + 1)) {
        uint32_t cp = ((c & 0x1Fu) << 6) | (static_cast<unsigned char>(s[i + 1]) & 0x3Fu);
        i += 2;
        return cp;
    }
    if ((c & 0xF0) == 0xE0 && cont(i + 1) && cont(i + 2)) {
        uint32_t cp = ((c & 0x0Fu) << 12) | ((static_cast<unsigned char>(s[i + 1]) & 0x3Fu) << 6) |
                      (static_cast<unsigned char>(s[i + 2]) & 0x3Fu);
        i += 3;
        return cp;
    }
    if ((c & 0xF8) == 0xF0 && cont(i + 1) && cont(i + 2) && cont(i + 3)) {
        uint32_t cp = ((c & 0x07u) << 18) | ((static_cast<unsigned char>(s[i + 1]) & 0x3Fu) << 12) |
                      ((static_cast<unsigned char>(s[i + 2]) & 0x3Fu) << 6) | (static_cast<unsigned char>(s[i + 3]) & 0x3Fu);
        i += 4;
        return cp;
    }
    ++i;
    return 0xFFFD;
}

void put_cp(std::string& out, uint32_t cp) {
    if (cp < 0x80) out += static_cast<char>(cp);
    else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

bool is_space(uint32_t cp) {
    // char.IsWhiteSpace + SpaceSeparator（含 \t \n \v \f \r、NEL、NBSP、U+1680、U+2000–200A、U+202F、U+205F、U+3000、2028/2029）
    return (cp >= 0x09 && cp <= 0x0D) || cp == 0x20 || cp == 0x85 || cp == 0xA0 || cp == 0x1680 || (cp >= 0x2000 && cp <= 0x200A) ||
           cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000;
}

bool is_dropped(uint32_t cp) {
    if (cp < 0x20 || (cp >= 0x7F && cp <= 0x9F)) return true;   // Control
    if (cp > 0xFFFF) return true;                                // C# 里是代理项（Surrogate）
    if (cp >= 0xD800 && cp <= 0xDFFF) return true;
    if (cp >= 0xE000 && cp <= 0xF8FF) return true;               // PrivateUse
    if (cp == 0xFFFD) return false;
    // Format（Cf）：软连字符、阿拉伯格式符、零宽、双向控制、BOM、行间注解
    static const uint32_t ranges[][2] = {{0x00AD, 0x00AD}, {0x0600, 0x0605}, {0x061C, 0x061C}, {0x06DD, 0x06DD},
                                         {0x070F, 0x070F}, {0x08E2, 0x08E2}, {0x180E, 0x180E}, {0x200B, 0x200F},
                                         {0x202A, 0x202E}, {0x2060, 0x2064}, {0x2066, 0x206F}, {0xFEFF, 0xFEFF},
                                         {0xFFF9, 0xFFFB}, {0xFFFE, 0xFFFF}};
    for (auto& r : ranges)
        if (cp >= r[0] && cp <= r[1]) return true;
    return false;
}

}  // namespace

std::string random_hex(int bytes) {
    std::vector<unsigned char> b(static_cast<size_t>(bytes));
    fill_random(b.data(), b.size());
    std::string out;
    out.reserve(b.size() * 2);
    for (unsigned char c : b) {
        out += kHex[c >> 4];
        out += kHex[c & 0xF];
    }
    return out;
}

std::string new_guid() {
    unsigned char b[16];
    fill_random(b, 16);
    b[6] = static_cast<unsigned char>((b[6] & 0x0F) | 0x40);
    b[8] = static_cast<unsigned char>((b[8] & 0x3F) | 0x80);
    std::string out;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) out += '-';
        out += kHex[b[i] >> 4];
        out += kHex[b[i] & 0xF];
    }
    return out;
}

std::string sanitize_client_name(const std::string& name) {
    std::string out;
    int units = 0;
    bool pending_space = false;
    size_t i = 0;
    while (i < name.size()) {
        uint32_t cp = next_cp(name, i);
        if (is_space(cp)) { pending_space = units > 0; continue; }
        if (is_dropped(cp)) continue;
        if (pending_space) {
            out += ' ';
            ++units;
            pending_space = false;
        }
        put_cp(out, cp);
        if (++units >= kClientNameMax) break;
    }
    return out;
}

}  // namespace reactor::cmd
