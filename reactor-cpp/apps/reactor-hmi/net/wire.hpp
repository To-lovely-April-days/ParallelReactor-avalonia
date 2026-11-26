// 网络层用的小工具：SHA-1、base64（WebSocket 握手）、随机数 / uuid（Idempotency-Key）、ISO-8601 时间、URL 编码、时钟。
// 全部自己实现（不链接 src/net，触屏是单独的进程）。任意线程可调。
#pragma once
#include <array>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::hmi::net {

std::array<uint8_t, 20> sha1(const std::string& data);
std::string base64(const uint8_t* p, size_t n);
inline std::string base64(const std::string& s) { return base64(reinterpret_cast<const uint8_t*>(s.data()), s.size()); }

std::string random_bytes(size_t n);   // /dev/urandom，读不了时退回 std::random_device
std::string uuid4();                  // 「6f0a7d2e-1b9c-4b0e-9f0e-1d2a3c4b5e6f」

// 「2026-11-06T08:12:30Z」「…30.1234567Z」「…30+08:00」→ UTC 毫秒；不认识 → nullopt
std::optional<int64_t> parse_iso_ms(const std::string& s);
std::string iso_ms(int64_t utc_ms);   // 「2026-11-06T08:12:30.120Z」

std::string url_encode(const std::string& s);  // 路径段 / 查询参数（RFC 3986 unreserved 以外的都编码）

int64_t mono_ms();   // 单调时钟（重连退避、超时）
int64_t wall_ms();   // UTC 毫秒

}  // namespace reactor::hmi::net
