// 接口层用到的几个小算法：SHA-1（WebSocket 握手）、SHA-256（令牌哈希、幂等哈希）、base64、十六进制、定时比较。
// 纯函数，线程安全，零依赖。
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::net {

std::array<uint8_t, 20> sha1(const void* data, size_t len);
std::array<uint8_t, 32> sha256(const void* data, size_t len);
inline std::array<uint8_t, 20> sha1(const std::string& s) { return sha1(s.data(), s.size()); }
inline std::array<uint8_t, 32> sha256(const std::string& s) { return sha256(s.data(), s.size()); }

// 小写十六进制
std::string to_hex(const uint8_t* data, size_t len);
template <size_t N> std::string to_hex(const std::array<uint8_t, N>& a) { return to_hex(a.data(), N); }
// SHA-256 → 64 位小写十六进制（C#: TokenAuth.Hash）
std::string sha256_hex(const std::string& s);

// 标准 base64（带 = 填充）
std::string base64_encode(const void* data, size_t len);
inline std::string base64_encode(const std::string& s) { return base64_encode(s.data(), s.size()); }
// 严格解码（只接受标准字母表和正确填充；空白不接受）；失败返回 nullopt
std::optional<std::string> base64_decode(const std::string& s);

// 定时比较：长度不同直接 false（长度不是秘密）；相同长度时耗时与内容无关（C#: CryptographicOperations.FixedTimeEquals）
bool constant_time_equal(const std::string& a, const std::string& b);

}  // namespace reactor::net
