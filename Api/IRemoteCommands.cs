using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>一条远程命令的调用方：令牌名（= 仲裁器里的 ClientId）、客户端名（申请控制权时给的 clientName）、本次 opId。</summary>
public readonly record struct RemoteCaller(string TokenName, string ClientName, string OpId);

/// <summary>命令在 UI 线程上的结局（设计稿 §13.1 / §7.1）。</summary>
public enum OutcomeKind
{
    /// <summary>Try* 已返回（同步头 + 尾部都完成）：按 <see cref="OpResult"/> 映射 200 / 202 / 4xx / 5xx。</summary>
    Completed,
    /// <summary>同步头已执行、尾部（等硬件）在 2 s 内未完成：202 accepted + warnings HW_UNCONFIRMED，最终结果走事件。</summary>
    HwUnconfirmed,
    /// <summary>5 s 内没进 UI 线程且已撤销，确定未执行：503 UI_THREAD_TIMEOUT，可安全重试。</summary>
    UiThreadTimeout,
    /// <summary>已进入 UI 线程但再等 5 s 仍未返回：504 OUTCOME_UNKNOWN，命令继续执行，结果回填幂等表与事件。</summary>
    OutcomeUnknown,
}

/// <summary>命令结局。<see cref="Pending"/>：HwUnconfirmed / OutcomeUnknown 时仍在跑的内层任务（完成后记 op.*.result 事件、回填幂等表）。</summary>
public sealed record CommandOutcome(OutcomeKind Kind, OpResult? Result, Task<OpResult>? Pending)
{
    public static CommandOutcome Done(OpResult r) => new(OutcomeKind.Completed, r, null);
    public static CommandOutcome Unconfirmed(Task<OpResult> pending) => new(OutcomeKind.HwUnconfirmed, null, pending);
    public static readonly CommandOutcome UiTimeout = new(OutcomeKind.UiThreadTimeout, null, null);
    public static CommandOutcome Unknown(Task<OpResult> pending) => new(OutcomeKind.OutcomeUnknown, null, pending);
}

/// <summary>管线前段拒绝（带 §11 错误码；状态码由 <see cref="ApiErrors.StatusOf"/> 决定）。写端点在参数解析阶段抛出，由公共管线统一转成响应。</summary>
public class ApiRejectException : Exception
{
    public string Code { get; }
    public ApiRejectException(string code, string message) : base(message) => Code = code;
}

/// <summary>参数绑定失败（400 BAD_REQUEST）。</summary>
public sealed class ApiBadRequestException : ApiRejectException
{
    public ApiBadRequestException(string message) : base(OpCodes.BadRequest, message) { }
}

/// <summary>
/// 远程写命令（设计稿 §7 / 命令层设计 §12.1 映射表）：每个端点一个方法，签名只用基本类型，与 <c>OperatorCommands.Try*</c> 一一对应。
/// HMI 实现（Api/Hmi/HmiRemoteCommands）把每个调用经 <c>UiCommandBridge</c> 投递到 UI 线程执行
/// <c>Ops.TryX(CommandSource.Remote(c.TokenName, c.ClientName), …)</c>（急停例外：任意线程直接调用）。
/// 测试工程用脚本化的假实现。接口层（Kestrel 线程）只认这个接口，不认识 ViewModel。
/// </summary>
public interface IRemoteCommands
{
    // —— §7.3 急停 / 全局停止 ——
    Task<CommandOutcome> EstopAsync(RemoteCaller c, string? note);
    Task<CommandOutcome> StopAllAsync(RemoteCaller c, bool stopStir, CancellationToken ct);
    // —— §7.4 运行 / 通道 ——
    Task<CommandOutcome> RunAsync(RemoteCaller c, CancellationToken ct);
    Task<CommandOutcome> EnableChannelAsync(RemoteCaller c, int id, CancellationToken ct);
    /// <summary>/channels/{id}/start：启用 + 启动加热（同一次 UI 线程调用）。</summary>
    Task<CommandOutcome> StartChannelAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct);
    Task<CommandOutcome> StopChannelAsync(RemoteCaller c, int id, bool disable, CancellationToken ct);
    Task<CommandOutcome> CopyChannelConfigAsync(RemoteCaller c, int from, IReadOnlyList<int>? to, CancellationToken ct);
    Task<CommandOutcome> ResetGasCounterAsync(RemoteCaller c, int id, CancellationToken ct);
    // —— §7.5 温度 ——
    Task<CommandOutcome> StartHeatAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct);
    Task<CommandOutcome> StopHeatAsync(RemoteCaller c, int id, CancellationToken ct);
    Task<CommandOutcome> SetSetpointAsync(RemoteCaller c, int id, double tempC, CancellationToken ct);
    Task<CommandOutcome> SetSpModeAsync(RemoteCaller c, int id, string mode, CancellationToken ct);
    Task<CommandOutcome> ReplaceProfileAsync(RemoteCaller c, int id, IReadOnlyList<(double TempC, double Minutes)> segments, CancellationToken ct);
    Task<CommandOutcome> StartProfileAsync(RemoteCaller c, int id, CancellationToken ct);
    Task<CommandOutcome> StopProfileAsync(RemoteCaller c, int id, CancellationToken ct);
    // —— §7.6 PID / 自整定 ——
    Task<CommandOutcome> SetPidAsync(RemoteCaller c, int id, int band, double p, double i, double d, CancellationToken ct);
    Task<CommandOutcome> StartAutotuneAsync(RemoteCaller c, int id, double tempC, CancellationToken ct);
    Task<CommandOutcome> CancelAutotuneAsync(RemoteCaller c, int id, CancellationToken ct);
    // —— §7.7 搅拌 ——
    /// <summary>PUT /stir：rpm 先于 on 应用；两个都可选（至少一个由端点保证）。</summary>
    Task<CommandOutcome> SetStirAsync(RemoteCaller c, bool? on, int? rpm, CancellationToken ct);
    // —— §7.8 泄漏测试 ——
    Task<CommandOutcome> StartLeakTestAsync(RemoteCaller c, IReadOnlyList<int> channels, double? testPsi, CancellationToken ct);
    Task<CommandOutcome> AbortLeakTestAsync(RemoteCaller c, CancellationToken ct);
    // —— §7.9 程序 ——
    /// <summary>PUT /channels/{id}/program：steps 为 §6.9 的 JSON 数组（枚举码），转换与校验在 HMI 侧（ProgramJson）。</summary>
    Task<CommandOutcome> ReplaceProgramAsync(RemoteCaller c, int id, JsonElement steps, CancellationToken ct);
    Task<CommandOutcome> RunProgramAsync(RemoteCaller c, int id, CancellationToken ct);
    Task<CommandOutcome> AbortProgramAsync(RemoteCaller c, int id, CancellationToken ct);
    Task<CommandOutcome> CopyProgramAsync(RemoteCaller c, int from, IReadOnlyList<int> to, CancellationToken ct);
    // —— §7.10 报警 ——
    Task<CommandOutcome> AckAlarmAsync(RemoteCaller c, string alarmId, CancellationToken ct);
    // —— §10 会话 ——
    Task<CommandOutcome> BeginSessionAsync(RemoteCaller c, string testId, string? note, CancellationToken ct);
    Task<CommandOutcome> EndSessionAsync(RemoteCaller c, CancellationToken ct);
    Task<CommandOutcome> AddMarkerAsync(RemoteCaller c, string text, CancellationToken ct);
}
