#include "net/crypto.hpp"

#include <cstring>
#include <array>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::net {

namespace {

inline uint32_t rol(uint32_t x, int n) { return (x << n) | (x >> (32 - n)); }
inline uint32_t ror(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

// 两个哈希共用的 Merkle–Damgård 填充：消息 + 0x80 + 0… + 64 位大端比特长度
template <class Block>
void md_pad_and_run(const uint8_t* p, size_t len, Block&& block) {
    size_t full = len / 64;
    for (size_t i = 0; i < full; ++i) block(p + i * 64);
    uint8_t tail[128];
    size_t rem = len % 64;
    std::memset(tail, 0, sizeof tail);
    if (rem) std::memcpy(tail, p + full * 64, rem);
    tail[rem] = 0x80;
    size_t tlen = rem + 1 + 8 <= 64 ? 64 : 128;
    uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 0; i < 8; ++i) tail[tlen - 1 - i] = static_cast<uint8_t>(bits >> (8 * i));
    block(tail);
    if (tlen == 128) block(tail + 64);
}

inline uint32_t be32(const uint8_t* b) {
    return (uint32_t(b[0]) << 24) | (uint32_t(b[1]) << 16) | (uint32_t(b[2]) << 8) | uint32_t(b[3]);
}

const uint32_t K256[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
};

const char kB64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

}  // namespace

std::array<uint8_t, 20> sha1(const void* data, size_t len) {
    uint32_t h[5] = {0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0};
    md_pad_and_run(static_cast<const uint8_t*>(data), len, [&](const uint8_t* b) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i) w[i] = be32(b + 4 * i);
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], bb = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            uint32_t f, k;
            if (i < 20) { f = (bb & c) | (~bb & d); k = 0x5A827999; }
            else if (i < 40) { f = bb ^ c ^ d; k = 0x6ED9EBA1; }
            else if (i < 60) { f = (bb & c) | (bb & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = bb ^ c ^ d; k = 0xCA62C1D6; }
            uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d; d = c; c = rol(bb, 30); bb = a; a = t;
        }
        h[0] += a; h[1] += bb; h[2] += c; h[3] += d; h[4] += e;
    });
    std::array<uint8_t, 20> out{};
    for (int i = 0; i < 20; ++i) out[i] = static_cast<uint8_t>(h[i / 4] >> (24 - 8 * (i % 4)));
    return out;
}

std::array<uint8_t, 32> sha256(const void* data, size_t len) {
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    md_pad_and_run(static_cast<const uint8_t*>(data), len, [&](const uint8_t* b) {
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) w[i] = be32(b + 4 * i);
        for (int i = 16; i < 64; ++i) {
            uint32_t s0 = ror(w[i - 15], 7) ^ ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = ror(w[i - 2], 17) ^ ror(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], bb = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = ror(e, 6) ^ ror(e, 11) ^ ror(e, 25);
            uint32_t ch = (e & f) ^ (~e & g);
            uint32_t t1 = hh + S1 + ch + K256[i] + w[i];
            uint32_t S0 = ror(a, 2) ^ ror(a, 13) ^ ror(a, 22);
            uint32_t mj = (a & bb) ^ (a & c) ^ (bb & c);
            uint32_t t2 = S0 + mj;
            hh = g; g = f; f = e; e = d + t1; d = c; c = bb; bb = a; a = t1 + t2;
        }
        h[0] += a; h[1] += bb; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    });
    std::array<uint8_t, 32> out{};
    for (int i = 0; i < 32; ++i) out[i] = static_cast<uint8_t>(h[i / 4] >> (24 - 8 * (i % 4)));
    return out;
}

std::string to_hex(const uint8_t* data, size_t len) {
    static const char d[] = "0123456789abcdef";
    std::string s(len * 2, '0');
    for (size_t i = 0; i < len; ++i) { s[2 * i] = d[data[i] >> 4]; s[2 * i + 1] = d[data[i] & 15]; }
    return s;
}

std::string sha256_hex(const std::string& s) { return to_hex(sha256(s)); }

std::string base64_encode(const void* data, size_t len) {
    const uint8_t* p = static_cast<const uint8_t*>(data);
    std::string out;
    out.reserve((len + 2) / 3 * 4);
    size_t i = 0;
    for (; i + 3 <= len; i += 3) {
        uint32_t v = (uint32_t(p[i]) << 16) | (uint32_t(p[i + 1]) << 8) | p[i + 2];
        out += kB64[v >> 18]; out += kB64[(v >> 12) & 63]; out += kB64[(v >> 6) & 63]; out += kB64[v & 63];
    }
    if (len - i == 1) {
        uint32_t v = uint32_t(p[i]) << 16;
        out += kB64[v >> 18]; out += kB64[(v >> 12) & 63]; out += "==";
    } else if (len - i == 2) {
        uint32_t v = (uint32_t(p[i]) << 16) | (uint32_t(p[i + 1]) << 8);
        out += kB64[v >> 18]; out += kB64[(v >> 12) & 63]; out += kB64[(v >> 6) & 63]; out += '=';
    }
    return out;
}

std::optional<std::string> base64_decode(const std::string& s) {
    if (s.size() % 4 != 0) return std::nullopt;
    auto val = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    std::string out;
    out.reserve(s.size() / 4 * 3);
    for (size_t i = 0; i < s.size(); i += 4) {
        bool last = i + 4 == s.size();
        int v[4];
        int pad = 0;
        for (int k = 0; k < 4; ++k) {
            char c = s[i + k];
            if (c == '=') {
                if (!last || k < 2) return std::nullopt;
                v[k] = 0; ++pad;
            } else {
                if (pad) return std::nullopt;   // '=' 之后不能再有字符
                v[k] = val(c);
                if (v[k] < 0) return std::nullopt;
            }
        }
        uint32_t x = (uint32_t(v[0]) << 18) | (uint32_t(v[1]) << 12) | (uint32_t(v[2]) << 6) | uint32_t(v[3]);
        out += static_cast<char>(x >> 16);
        if (pad < 2) out += static_cast<char>((x >> 8) & 0xFF);
        if (pad < 1) out += static_cast<char>(x & 0xFF);
    }
    return out;
}

bool constant_time_equal(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    volatile uint8_t diff = 0;
    for (size_t i = 0; i < a.size(); ++i) diff = diff | (static_cast<uint8_t>(a[i]) ^ static_cast<uint8_t>(b[i]));
    return diff == 0;
}

}  // namespace reactor::net
