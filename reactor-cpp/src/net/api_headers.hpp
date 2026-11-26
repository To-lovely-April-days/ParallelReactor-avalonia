// 接口的自定义 HTTP 头与前缀。C#: Contracts/ParallelReactor.Contracts/Api/ApiHeaders.cs（PC 客户端共用，逐字照抄）。
#pragma once

namespace reactor::net::headers {

inline constexpr const char* kLease = "X-Control-Lease";              // 受控写命令必带：当前租约 id
inline constexpr const char* kIdempotency = "Idempotency-Key";        // 幂等键（≤ 128 字符）
inline constexpr const char* kRequestKey = "X-Control-Request-Key";   // GET /control/requests/{id} 带它才返回 leaseId
inline constexpr const char* kIdempotentReplay = "Idempotent-Replay"; // 响应头：同键重放时为 "true"
inline constexpr const char* kClientVersion = "X-Client-Version";     // 客户端自报版本（可选）
inline constexpr const char* kPrefix = "/api/v1";
inline constexpr const char* kWsPath = "/api/v1/ws";

}  // namespace reactor::net::headers
