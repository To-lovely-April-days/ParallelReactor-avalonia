using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;

namespace ParallelReactor.PerfTest.Engine;

/// <summary>一条写命令的结局（HTTP 响应或本地失败）。</summary>
public sealed record ApiOutcome(bool Ok, string? OpId, string Status, int HttpStatus, string? Code, string? Message, long OpEventSeq = 0)
{
    public static ApiOutcome Fail(string code, string message, int http = 0) => new(false, null, "failed", http, code, message);
    public override string ToString() => Ok ? $"{HttpStatus} {Status} opId={OpId}" : $"{HttpStatus} {Code}: {Message}";
}

/// <summary>
/// 引擎看到的接口（对 Client 的薄抽象，测试用假实现）。停止类不需要租约；控制类由实现决定如何带租约。
/// 全部异常须转为 <see cref="ApiOutcome"/>（Ok=false），只有取消向上抛。
/// </summary>
public interface IReactorApi
{
    Task<DeviceInfoDto?> GetDeviceAsync(CancellationToken ct);
    Task<SettingsDto?> GetSettingsAsync(CancellationToken ct);
    Task<TelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct);
    Task<CommDto?> GetCommAsync(CancellationToken ct);

    Task<ApiOutcome> EnableChannelAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> DisableChannelAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> StopChannelAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> StartHeatAsync(int rv, double? tempC, CancellationToken ct);
    Task<ApiOutcome> SetSetpointAsync(int rv, double tempC, CancellationToken ct);
    Task<ApiOutcome> StopHeatAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> SetStirAsync(bool? on, int? rpm, CancellationToken ct);
    Task<ApiOutcome> StopStirAsync(CancellationToken ct);
    Task<ApiOutcome> ReplaceProgramAsync(int rv, string programJson, CancellationToken ct);
    Task<ApiOutcome> RunProgramAsync(int rv, string? sessionId, string idem, CancellationToken ct);
    Task<ApiOutcome> AbortProgramAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> StartAutotuneAsync(int rv, double tempC, CancellationToken ct);
    Task<ApiOutcome> CancelAutotuneAsync(int rv, CancellationToken ct);
    Task<ApiOutcome> StartLeakTestAsync(int[] channels, double? testPsi, string idem, CancellationToken ct);
    Task<ApiOutcome> AbortLeakTestAsync(CancellationToken ct);
    Task<ApiOutcome> EstopAsync(string? note, CancellationToken ct);
    Task<ApiOutcome> StopAllAsync(bool stopStir, CancellationToken ct);
    /// <summary>（重新）申请控制权并等到终态；已持有直接 Ok。</summary>
    Task<ApiOutcome> RequestControlAsync(CancellationToken ct);

    /// <summary>POST /sessions；Code=SESSION_ACTIVE 表示已有活动会话。成功时 OpId 之外还要给出会话 id（放在 Message）。</summary>
    Task<ApiOutcome> BeginSessionAsync(string testId, string? note, string idem, CancellationToken ct);
    Task<ApiOutcome> EndSessionAsync(CancellationToken ct);
    Task<ApiOutcome> AddMarkerAsync(string text, string idem, CancellationToken ct);
    /// <summary>GET /files/sessions/&lt;id&gt;.csv → dest；不存在返回 false。</summary>
    Task<bool> DownloadSessionCsvAsync(string sessionId, Stream dest, CancellationToken ct);

    /// <summary>当前是否持有控制权（引擎开始前要求 true；运行中变 false 触发安全钩子）。</summary>
    bool CanControl { get; }
}

/// <summary>安全事件种类。</summary>
public enum SafetyKind { Alarm, Estop, ControlLost, CommLost, PressureDropAlert, OverTempAlert }

/// <summary>遥测 / 事件推送到引擎的安全事件。</summary>
public sealed record SafetyEvent(SafetyKind Kind, string Title, string Detail, DateTime At, int? Channel = null);

/// <summary>遥测源：每帧一次 <see cref="Frame"/>；安全事件一次 <see cref="Safety"/>。</summary>
public interface ITelemetryFeed
{
    event Action<TelemetrySnapshot>? Frame;
    event Action<SafetyEvent>? Safety;
    TelemetrySnapshot? Latest { get; }
}

/// <summary>人工确认请求。</summary>
public sealed record ConfirmRequest(string ItemId, string StepId, string Title, string Text, IReadOnlyList<string>? Checklist, bool TwoSigners, IReadOnlyList<int> Channels);
/// <summary>人工确认结果：Confirmed=false 表示操作员选择跳过 / 拒绝；Checked 为检查表勾选情况；签名可空。</summary>
public sealed record ConfirmResult(bool Confirmed, IReadOnlyList<bool>? Checked = null, string? Signer1 = null, string? Signer2 = null, string? Note = null)
{
    public static readonly ConfirmResult Yes = new(true);
    public static readonly ConfirmResult No = new(false);
}

/// <summary>一个待录入字段（已按通道 / 重复展开前的定义 + 本次要求的通道列表）。</summary>
public sealed record EntryField(string Key, string Label, string Unit, string Instrument, int Repeat, bool PerChannel, double? Min, double? Max, bool IsText, bool Optional);
public sealed record EntryRequest(string ItemId, string StepId, string Title, string? Text, IReadOnlyList<EntryField> Fields, IReadOnlyList<int> Channels, string? ValidationError);
/// <summary>录入结果：Values[key] = 数值列表（按通道 × 重复展开，顺序：通道升序，每通道 Repeat 个）；Texts[key] = 文字。Cancelled=true 跳过该步。</summary>
public sealed record EntryResult(bool Cancelled, IReadOnlyDictionary<string, IReadOnlyList<double>>? Values, IReadOnlyDictionary<string, string>? Texts = null, string? Note = null);

/// <summary>安全提示的操作员决定。</summary>
public enum SafetyDecision { Continue, AbortRun }
public sealed record SafetyPrompt(string ItemId, string? StepId, SafetyEvent Event, string Recommendation);

/// <summary>操作员交互（界面实现；测试 / 截图模式自动应答）。全部异步，引擎在上面等待。</summary>
public interface IOperatorPrompt
{
    Task<ConfirmResult> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
    Task<EntryResult> EnterAsync(EntryRequest request, CancellationToken ct);
    /// <summary>安全事件后（引擎已自动暂停）：继续或中止。绝不自动继续。</summary>
    Task<SafetyDecision> SafetyAsync(SafetyPrompt prompt, CancellationToken ct);
}
