namespace ParallelReactor.Commands;

/// <summary>
/// 错误码常量，与对外通讯接口设计 §11 同名（测试里逐个核对拼写）。
/// <see cref="ModeRemote"/>、<see cref="ValveBusy"/> 只在本机使用，不出现在 HTTP 响应里。
/// </summary>
public static class OpCodes
{
    public const string Ok = "OK";

    // —— §11 ——
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string ForbiddenRole = "FORBIDDEN_ROLE";
    public const string IpNotAllowed = "IP_NOT_ALLOWED";
    public const string NotExposed = "NOT_EXPOSED";
    public const string SessionNotOwner = "SESSION_NOT_OWNER";
    public const string NotFound = "NOT_FOUND";
    public const string ChannelNotFound = "CHANNEL_NOT_FOUND";
    public const string ModeLocal = "MODE_LOCAL";
    public const string NotControlHolder = "NOT_CONTROL_HOLDER";
    public const string ControlLeaseExpired = "CONTROL_LEASE_EXPIRED";
    public const string ControlRequestPending = "CONTROL_REQUEST_PENDING";
    public const string ChannelNotInstalled = "CHANNEL_NOT_INSTALLED";
    public const string EstopActive = "ESTOP_ACTIVE";
    public const string GasBusy = "GAS_BUSY";
    public const string GasOwnedByOther = "GAS_OWNED_BY_OTHER";
    public const string ManualValveActive = "MANUAL_VALVE_ACTIVE";
    public const string ChannelBusy = "CHANNEL_BUSY";
    public const string ChannelStateInvalid = "CHANNEL_STATE_INVALID";
    public const string ChannelPressurized = "CHANNEL_PRESSURIZED";
    public const string ChannelAlarm = "CHANNEL_ALARM";
    public const string ChannelProgramOwned = "CHANNEL_PROGRAM_OWNED";
    public const string ProgramRunning = "PROGRAM_RUNNING";
    public const string CurveRunning = "CURVE_RUNNING";
    public const string AutotuneActive = "AUTOTUNE_ACTIVE";
    public const string StirProgramOwned = "STIR_PROGRAM_OWNED";
    public const string LeakTestRunning = "LEAK_TEST_RUNNING";
    public const string SessionActive = "SESSION_ACTIVE";
    public const string SystemBusy = "SYSTEM_BUSY";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string LocalEditing = "LOCAL_EDITING";
    public const string IdempotencyMismatch = "IDEMPOTENCY_MISMATCH";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string HwNotConfigured = "HW_NOT_CONFIGURED";
    public const string ParamOutOfRange = "PARAM_OUT_OF_RANGE";
    public const string ParamCrossCheck = "PARAM_CROSS_CHECK";
    public const string PreflightFailed = "PREFLIGHT_FAILED";
    public const string ProgramInvalid = "PROGRAM_INVALID";
    public const string ClientTooOld = "CLIENT_TOO_OLD";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string DownloadBusy = "DOWNLOAD_BUSY";
    public const string HardwareWriteFailed = "HARDWARE_WRITE_FAILED";
    /// <summary>只出现在 <see cref="OpResult.Warnings"/> 中，不是错误。</summary>
    public const string HwUnconfirmed = "HW_UNCONFIRMED";
    public const string HwOffline = "HW_OFFLINE";
    public const string TooManyClients = "TOO_MANY_CLIENTS";
    public const string Starting = "STARTING";
    public const string UiThreadTimeout = "UI_THREAD_TIMEOUT";
    public const string OutcomeUnknown = "OUTCOME_UNKNOWN";
    public const string Internal = "INTERNAL";

    // —— 只在本机使用（§11 备注）——
    /// <summary>远程控制中，触屏的启动 / 改设定被拒绝。</summary>
    public const string ModeRemote = "MODE_REMOTE";
    /// <summary>上一个手动阀操作还没完成。</summary>
    public const string ValveBusy = "VALVE_BUSY";
}
