#include "net/api_errors.hpp"

namespace reactor::net {

int status_of(const std::string& c) {
    // C#: Contracts/ParallelReactor.Contracts/Api/ApiErrorStatus.cs
    if (c == codes::kBadRequest) return 400;
    if (c == codes::kUnauthorized) return 401;
    if (c == codes::kForbiddenRole || c == codes::kIpNotAllowed || c == codes::kNotExposed || c == codes::kSessionNotOwner) return 403;
    if (c == codes::kNotFound || c == codes::kChannelNotFound) return 404;
    if (c == codes::kParamOutOfRange || c == codes::kParamCrossCheck || c == codes::kPreflightFailed || c == codes::kProgramInvalid) return 422;
    if (c == codes::kClientTooOld) return 426;
    if (c == codes::kPreconditionRequired) return 428;
    if (c == codes::kRateLimited || c == codes::kDownloadBusy) return 429;
    if (c == codes::kHardwareWriteFailed) return 502;
    if (c == codes::kTooManyClients || c == codes::kStarting || c == codes::kUiThreadTimeout || c == codes::kHwOffline) return 503;
    if (c == codes::kOutcomeUnknown) return 504;
    if (c == codes::kInternal) return 500;
    // 传输层新增码（C# 无对应；不在 PC 客户端表里，PC 端按 HTTP 状态处理）
    if (c == codes::kMethodNotAllowed) return 405;
    if (c == codes::kPayloadTooLarge) return 413;
    if (c == codes::kRangeNotSatisfiable) return 416;
    if (c == codes::kHeadersTooLarge) return 431;
    return 409;
}

std::string error_body(const std::string& code, const std::string& message, const nlohmann::json& detail) {
    nlohmann::json err = {{"code", code}, {"message", message}};
    if (!detail.is_null()) err["detail"] = detail;
    nlohmann::json j = {{"ok", false}, {"error", std::move(err)}};
    return j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
}

}  // namespace reactor::net
