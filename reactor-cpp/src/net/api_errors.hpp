// 错误码 → HTTP 状态码，和统一的错误正文 {ok:false, error:{code, message, detail?}}。
// C#: Api/ApiErrors.cs、Contracts/ParallelReactor.Contracts/Api/ApiErrorStatus.cs（PC 客户端共用此表，未列出的码一律 409）。
#pragma once
#include <string>

#include "nlohmann/json.hpp"

namespace reactor::net {

// 接口层用到的通用码（领域码由 api/ 自己定义，status_of 同样适用）。C#: Contracts/.../Commands/OpCodes.cs
namespace codes {
inline constexpr const char* kBadRequest = "BAD_REQUEST";
inline constexpr const char* kUnauthorized = "UNAUTHORIZED";
inline constexpr const char* kForbiddenRole = "FORBIDDEN_ROLE";
inline constexpr const char* kIpNotAllowed = "IP_NOT_ALLOWED";
inline constexpr const char* kNotExposed = "NOT_EXPOSED";
inline constexpr const char* kSessionNotOwner = "SESSION_NOT_OWNER";
inline constexpr const char* kNotFound = "NOT_FOUND";
inline constexpr const char* kChannelNotFound = "CHANNEL_NOT_FOUND";
inline constexpr const char* kParamOutOfRange = "PARAM_OUT_OF_RANGE";
inline constexpr const char* kParamCrossCheck = "PARAM_CROSS_CHECK";
inline constexpr const char* kPreflightFailed = "PREFLIGHT_FAILED";
inline constexpr const char* kProgramInvalid = "PROGRAM_INVALID";
inline constexpr const char* kClientTooOld = "CLIENT_TOO_OLD";
inline constexpr const char* kPreconditionRequired = "PRECONDITION_REQUIRED";
inline constexpr const char* kRateLimited = "RATE_LIMITED";
inline constexpr const char* kDownloadBusy = "DOWNLOAD_BUSY";
inline constexpr const char* kHardwareWriteFailed = "HARDWARE_WRITE_FAILED";
inline constexpr const char* kTooManyClients = "TOO_MANY_CLIENTS";
inline constexpr const char* kStarting = "STARTING";
inline constexpr const char* kUiThreadTimeout = "UI_THREAD_TIMEOUT";
inline constexpr const char* kHwOffline = "HW_OFFLINE";
inline constexpr const char* kOutcomeUnknown = "OUTCOME_UNKNOWN";
inline constexpr const char* kInternal = "INTERNAL";
inline constexpr const char* kIdempotencyMismatch = "IDEMPOTENCY_MISMATCH";
inline constexpr const char* kIdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
// 以下几个是 C++ 服务器新加的传输层码（C# 由 Kestrel 直接回无正文的状态码）；状态码在 error_response 里显式给出
inline constexpr const char* kMethodNotAllowed = "METHOD_NOT_ALLOWED";   // 405
inline constexpr const char* kPayloadTooLarge = "PAYLOAD_TOO_LARGE";     // 413
inline constexpr const char* kHeadersTooLarge = "HEADERS_TOO_LARGE";     // 431
inline constexpr const char* kRangeNotSatisfiable = "RANGE_NOT_SATISFIABLE";  // 416
}  // namespace codes

// C#: ApiErrorStatus.Of —— 未列出的码一律 409
int status_of(const std::string& code);

// {ok:false, error:{code, message, detail?}}；detail 为 null 时省略
std::string error_body(const std::string& code, const std::string& message,
                       const nlohmann::json& detail = nlohmann::json());

}  // namespace reactor::net
