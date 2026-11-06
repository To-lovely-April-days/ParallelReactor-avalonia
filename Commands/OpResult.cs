using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ParallelReactor.Commands;

/// <summary>命令完成方式（对外通讯接口设计 §7.1：200 confirmed / 202 accepted）。</summary>
public enum OpStatus { Confirmed, Accepted }

/// <summary>
/// 操作员命令的结果（C2）。触屏把它转成 Toast，远程接口把它转成 HTTP 响应。
/// 纯逻辑：不得引用 Avalonia / ViewModels（测试工程直接链接本文件）。
/// </summary>
public sealed record OpResult
{
    public bool Ok { get; init; }
    /// <summary>成功为 <see cref="OpCodes.Ok"/>；失败为 §11 错误码。</summary>
    public string Code { get; init; } = OpCodes.Ok;
    /// <summary>中文消息：直接用作 Toast 文字 / HTTP error.message。</summary>
    public string Message { get; init; } = "";
    /// <summary>Toast 级别 ok | warn | err（与现有 Toast(kind, …) 一一对应）。</summary>
    public string Kind { get; init; } = "ok";
    public OpStatus Status { get; init; } = OpStatus.Confirmed;
    /// <summary>成功：result；失败：error.detail。</summary>
    public object? Data { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>触屏不弹 Toast（今天本来就没有 Toast 的路径，例如 PID 写入）。</summary>
    public bool Quiet { get; init; }
    /// <summary>
    /// 随后的提示（按顺序）：一个命令在硬件上分几步完成、每步各有结果时用（例如停止通道：停加热的结果 + 关进气阀失败）。
    /// 触屏在主结果之后逐条 Toast，与今天各步各自提示一致；远程把它们并入 warnings。
    /// </summary>
    public IReadOnlyList<OpResult> Followups { get; init; } = Array.Empty<OpResult>();

    /// <summary>已完成（同步完成或硬件已确认）。</summary>
    public static OpResult Done(string msg = "", string kind = "ok", object? data = null)
        => new() { Ok = true, Message = msg, Kind = kind, Data = data };

    /// <summary>已下发，硬件结果稍后由事件通知。</summary>
    public static OpResult Accepted(string msg, string kind = "ok", object? data = null)
        => new() { Ok = true, Message = msg, Kind = kind, Data = data, Status = OpStatus.Accepted };

    /// <summary>被拒绝 / 失败。</summary>
    public static OpResult Fail(string code, string msg, string kind = "err", object? detail = null)
        => new() { Ok = false, Code = code, Message = msg, Kind = kind, Data = detail };

    /// <summary>成功且不提示。</summary>
    public static readonly OpResult Silent = new() { Ok = true, Quiet = true };

    /// <summary>同步结果包成已完成的 Task（「同步头」直接返回用）。</summary>
    public Task<OpResult> AsTask() => Task.FromResult(this);

    /// <summary>追加一条随后的提示（null 时原样返回）。</summary>
    public OpResult Then(OpResult? next)
        => next == null ? this : this with { Followups = [.. Followups, next] };

    /// <summary>异常 → INTERNAL（「命令执行异常 · 原因」）。AggregateException 只有一个内层时取内层。</summary>
    public static OpResult InternalOf(Exception? ex)
    {
        var e = ex is AggregateException { InnerExceptions.Count: 1 } ag ? ag.InnerExceptions[0] : ex;
        return Fail(OpCodes.Internal, $"命令执行异常 · {e?.Message ?? "已取消"}");
    }

    /// <summary>
    /// 调用一个 Try* 并把<b>同步头</b>抛出的异常（例如不在 UI 线程时 Admit 的 VerifyAccess）与尾部失败都转成 INTERNAL，
    /// 返回的 Task 不会出错。远程适配层的约定写法：在 UI 线程上 <c>OpResult.Guard(() =&gt; ops.TryX(src, …))</c>
    /// （TryEstop 例外：任意线程可直接调用）。触屏入口仍直接传 Task（同步头异常与今天一样冒泡）。
    /// </summary>
    public static Task<OpResult> Guard(Func<Task<OpResult>> call)
    {
        Task<OpResult> t;
        try { t = call(); }
        catch (Exception ex) { return InternalOf(ex).AsTask(); }
        return t.IsCompletedSuccessfully ? t : GuardTailAsync(t);
    }

    private static async Task<OpResult> GuardTailAsync(Task<OpResult> t)
    {
        try { return await t.ConfigureAwait(false); }
        catch (Exception ex) { return InternalOf(ex); }
    }

    /// <summary>附加告警（HW_UNCONFIRMED、被跳过的通道等）。</summary>
    public OpResult WithWarnings(params string[] warnings)
        => warnings.Length == 0 ? this : this with { Warnings = [.. Warnings, .. warnings] };
}

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
