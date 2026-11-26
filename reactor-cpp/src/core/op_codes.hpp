// 错误码常量，与对外通讯接口设计 §11 同名（逐字照抄）。
// C#: Contracts/ParallelReactor.Contracts/Commands/OpCodes.cs
// ESTOP_ACTIVE 保留拼写只为兼容旧客户端解析；新系统已删除急停（设计文档 §6），core 里不再产生它。
#pragma once
#include <ostream>
#include <string>

namespace reactor::core {

namespace op_codes {
inline constexpr const char* kOk = "OK";
inline constexpr const char* kBadRequest = "BAD_REQUEST";
inline constexpr const char* kUnauthorized = "UNAUTHORIZED";
inline constexpr const char* kForbiddenRole = "FORBIDDEN_ROLE";
inline constexpr const char* kIpNotAllowed = "IP_NOT_ALLOWED";
inline constexpr const char* kNotExposed = "NOT_EXPOSED";
inline constexpr const char* kSessionNotOwner = "SESSION_NOT_OWNER";
inline constexpr const char* kNotFound = "NOT_FOUND";
inline constexpr const char* kChannelNotFound = "CHANNEL_NOT_FOUND";
inline constexpr const char* kModeLocal = "MODE_LOCAL";
inline constexpr const char* kNotControlHolder = "NOT_CONTROL_HOLDER";
inline constexpr const char* kControlLeaseExpired = "CONTROL_LEASE_EXPIRED";
inline constexpr const char* kControlRequestPending = "CONTROL_REQUEST_PENDING";
inline constexpr const char* kChannelNotInstalled = "CHANNEL_NOT_INSTALLED";
inline constexpr const char* kEstopActive = "ESTOP_ACTIVE";
inline constexpr const char* kGasBusy = "GAS_BUSY";
inline constexpr const char* kGasOwnedByOther = "GAS_OWNED_BY_OTHER";
inline constexpr const char* kManualValveActive = "MANUAL_VALVE_ACTIVE";
inline constexpr const char* kChannelBusy = "CHANNEL_BUSY";
inline constexpr const char* kChannelStateInvalid = "CHANNEL_STATE_INVALID";
inline constexpr const char* kChannelPressurized = "CHANNEL_PRESSURIZED";
inline constexpr const char* kChannelAlarm = "CHANNEL_ALARM";
inline constexpr const char* kChannelProgramOwned = "CHANNEL_PROGRAM_OWNED";
inline constexpr const char* kProgramRunning = "PROGRAM_RUNNING";
inline constexpr const char* kCurveRunning = "CURVE_RUNNING";
inline constexpr const char* kAutotuneActive = "AUTOTUNE_ACTIVE";
inline constexpr const char* kStirProgramOwned = "STIR_PROGRAM_OWNED";
inline constexpr const char* kLeakTestRunning = "LEAK_TEST_RUNNING";
inline constexpr const char* kSessionActive = "SESSION_ACTIVE";
inline constexpr const char* kSystemBusy = "SYSTEM_BUSY";
inline constexpr const char* kRevisionConflict = "REVISION_CONFLICT";
inline constexpr const char* kLocalEditing = "LOCAL_EDITING";
inline constexpr const char* kIdempotencyMismatch = "IDEMPOTENCY_MISMATCH";
inline constexpr const char* kIdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
inline constexpr const char* kHwNotConfigured = "HW_NOT_CONFIGURED";
inline constexpr const char* kParamOutOfRange = "PARAM_OUT_OF_RANGE";
inline constexpr const char* kParamCrossCheck = "PARAM_CROSS_CHECK";
inline constexpr const char* kPreflightFailed = "PREFLIGHT_FAILED";
inline constexpr const char* kProgramInvalid = "PROGRAM_INVALID";
inline constexpr const char* kClientTooOld = "CLIENT_TOO_OLD";
inline constexpr const char* kPreconditionRequired = "PRECONDITION_REQUIRED";
inline constexpr const char* kRateLimited = "RATE_LIMITED";
inline constexpr const char* kDownloadBusy = "DOWNLOAD_BUSY";
inline constexpr const char* kHardwareWriteFailed = "HARDWARE_WRITE_FAILED";
inline constexpr const char* kHwUnconfirmed = "HW_UNCONFIRMED";  // 只出现在 warnings 里
inline constexpr const char* kHwOffline = "HW_OFFLINE";
inline constexpr const char* kTooManyClients = "TOO_MANY_CLIENTS";
inline constexpr const char* kStarting = "STARTING";
inline constexpr const char* kUiThreadTimeout = "UI_THREAD_TIMEOUT";
inline constexpr const char* kOutcomeUnknown = "OUTCOME_UNKNOWN";
inline constexpr const char* kInternal = "INTERNAL";
inline constexpr const char* kModeRemote = "MODE_REMOTE";  // 只在本机使用
inline constexpr const char* kValveBusy = "VALVE_BUSY";    // 只在本机使用
}  // namespace op_codes

// 守卫类检查的拒绝结果：错误码 + 中文原因（与 C# 逐字相同）。
struct GuardReject {
    std::string code;
    std::string message;
    bool operator==(const GuardReject& o) const { return code == o.code && message == o.message; }
};

inline std::ostream& operator<<(std::ostream& os, const GuardReject& r) { return os << r.code << " " << r.message; }

}  // namespace reactor::core
