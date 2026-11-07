using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;

namespace ParallelReactor.Client;

/// <summary>
/// REST 客户端（设计稿 §7 / §13；使用说明 §2 / §5.2）。类型化返回 Contracts 的 DTO；非 2xx 抛 <see cref="ApiException"/>。
/// <list type="bullet">
/// <item>每个写命令都带 Idempotency-Key（调用方可传；为 null 时生成），网络异常 / 504 OUTCOME_UNKNOWN / 409 IDEMPOTENCY_IN_PROGRESS 同键重发；</item>
/// <item>受控写命令由调用方传 lease（<see cref="ControlLeaseClient"/> 持有）；停止类（heat/stop、stop、stop-all、estop…）不需要租约；</item>
/// <item>写命令经一个信号量串行 + 最小间隔（服务端按令牌限流）；/estop 与心跳不进该信号量；429 读 Retry-After 重试一次。</item>
/// </list>
/// 线程安全；全部 ConfigureAwait(false)。
/// </summary>
public sealed class ReactorApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientOptions _opt;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    /// <summary>停止类命令的独立信号量：不被正在重试的受控写（最长 ≈ 1 min）排队。</summary>
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private DateTime _lastWriteAt = DateTime.MinValue;
    private long _requests, _failures;

    public ReactorApiClient(ClientOptions options, HttpMessageHandler? handler = null, IClock? clock = null)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? SystemClock.Instance;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = _opt.ApiBase;
        _http.Timeout = _opt.HttpTimeout;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opt.Token);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(ApiHeaders.ClientVersion, _opt.ClientVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public ClientOptions Options => _opt;
    public long Requests => Interlocked.Read(ref _requests);
    public long Failures => Interlocked.Read(ref _failures);

    // ======================================================================
    // 读
    // ======================================================================

    public Task<HealthDto> HealthAsync(CancellationToken ct) => GetAsync<HealthDto>("health", ct);
    public Task<DeviceInfoDto> DeviceAsync(CancellationToken ct) => GetAsync<DeviceInfoDto>("device", ct);
    public Task<TelemetrySnapshot> SnapshotAsync(CancellationToken ct) => GetAsync<TelemetrySnapshot>("snapshot", ct);
    public Task<ChannelsDto> ChannelsAsync(CancellationToken ct) => GetAsync<ChannelsDto>("channels", ct);
    public Task<ChannelDetailDto> ChannelAsync(int id, CancellationToken ct) => GetAsync<ChannelDetailDto>($"channels/{id}", ct);
    public Task<EventsPageDto> EventsAsync(long afterSeq, int limit, CancellationToken ct)
        => GetAsync<EventsPageDto>($"events?afterSeq={afterSeq}&limit={limit}", ct);
    public Task<AlarmsDto> AlarmsAsync(string? state, CancellationToken ct)
        => GetAsync<AlarmsDto>(state == null ? "alarms" : "alarms?state=" + Uri.EscapeDataString(state), ct);
    public Task<SettingsDto> SettingsAsync(CancellationToken ct) => GetAsync<SettingsDto>("settings", ct);
    public Task<ProgramSchemaDto> ProgramSchemaAsync(CancellationToken ct) => GetAsync<ProgramSchemaDto>("program-schema", ct);
    public Task<PreflightDto> PreflightAsync(CancellationToken ct) => GetAsync<PreflightDto>("preflight", ct);
    public Task<CommDto> CommAsync(CancellationToken ct) => GetAsync<CommDto>("device/comm", ct);
    public Task<FilesDto> FilesAsync(string type, CancellationToken ct) => GetAsync<FilesDto>("files?type=" + Uri.EscapeDataString(type), ct);
    public Task<LeakTestsDto> LeakTestsAsync(CancellationToken ct) => GetAsync<LeakTestsDto>("leak-tests", ct);
    public Task<JsonElement> LeakTestSummaryAsync(string runId, CancellationToken ct) => GetAsync<JsonElement>($"leak-tests/{Uri.EscapeDataString(runId)}", ct);
    public Task<SessionsDto> SessionsAsync(CancellationToken ct) => GetAsync<SessionsDto>("sessions", ct);
    public Task<SessionDetailDto> SessionAsync(string id, CancellationToken ct) => GetAsync<SessionDetailDto>($"sessions/{Uri.EscapeDataString(id)}", ct);
    public Task<ControlStateDto> ControlAsync(CancellationToken ct) => GetAsync<ControlStateDto>("control", ct);

    /// <summary>GET /sessions/current；没有活动会话（404）返回 null。</summary>
    public async Task<SessionDetailDto?> SessionCurrentAsync(CancellationToken ct)
    {
        try { return await GetAsync<SessionDetailDto>("sessions/current", ct).ConfigureAwait(false); }
        catch (ApiException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>GET /control/requests/{id}（带 X-Control-Request-Key 才给 leaseId）。404 抛 ApiException(NOT_FOUND)。</summary>
    public async Task<ControlRequestStatusDto> RequestStatusAsync(string requestId, string? requestKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"control/requests/{Uri.EscapeDataString(requestId)}");
        if (!string.IsNullOrEmpty(requestKey)) req.Headers.TryAddWithoutValidation(ApiHeaders.RequestKey, requestKey);
        using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
        var body = await ReadAsync(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw ApiException.Parse((int)resp.StatusCode, body, "查询申请失败");
        return ApiJson.Deserialize<ControlRequestStatusDto>(body) ?? throw new ApiException("BAD_RESPONSE", 200, "响应不是 ControlRequestStatus");
    }

    /// <summary>下载历史文件（GET /files/{type}/{name}）到流。</summary>
    public Task DownloadFileAsync(string type, string name, Stream dest, CancellationToken ct)
        => DownloadAsync($"files/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(name)}", dest, ct);
    /// <summary>GET /telemetry?from&amp;to → CSV 到流（UTC ISO 8601）。</summary>
    public Task TelemetryRangeAsync(DateTime fromUtc, DateTime toUtc, Stream dest, CancellationToken ct)
        => DownloadAsync($"telemetry?from={Uri.EscapeDataString(Iso(fromUtc))}&to={Uri.EscapeDataString(Iso(toUtc))}", dest, ct);
    public Task LeakTestDataAsync(string runId, Stream dest, CancellationToken ct) => DownloadAsync($"leak-tests/{Uri.EscapeDataString(runId)}/data.csv", dest, ct);
    public Task LeakTestSummaryCsvAsync(string runId, Stream dest, CancellationToken ct) => DownloadAsync($"leak-tests/{Uri.EscapeDataString(runId)}/summary.csv", dest, ct);

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ======================================================================
    // 控制权（记录类：不需要租约头；/control/request 必带幂等键）
    // ======================================================================

    /// <summary>POST /control/request {clientName}。autoGrant 时 result.result=="granted" 直接带 leaseId；否则 pending（带 requestId / requestKey）。</summary>
    public Task<CommandResult<ControlRequestResultDto>> RequestControlAsync(string? clientName, string? idempotencyKey, CancellationToken ct)
        => CommandAsync<ControlRequestResultDto>(HttpMethod.Post, "control/request", new { clientName = clientName ?? _opt.ClientName }, null, idempotencyKey ?? NewKey(), ct);

    /// <summary>POST /control/heartbeat（带 X-Control-Lease）。独立限流，不进写信号量。</summary>
    public async Task<HeartbeatResultDto> HeartbeatAsync(string leaseId, CancellationToken ct)
    {
        var r = await SendCommandOnceAsync<HeartbeatResultDto>(HttpMethod.Post, "control/heartbeat", "{}", leaseId, null, ct).ConfigureAwait(false);
        return r.Result ?? throw new ApiException("BAD_RESPONSE", r.HttpStatus, "心跳响应缺少 result");
    }

    /// <summary>POST /control/release（带 X-Control-Lease）。</summary>
    public Task<CommandResult<ReleaseResult>> ReleaseControlAsync(string leaseId, CancellationToken ct)
        => CommandAsync<ReleaseResult>(HttpMethod.Post, "control/release", null, leaseId, NewKey(), ct);

    // ======================================================================
    // 停止类（任何模式、无租约）
    // ======================================================================

    /// <summary>POST /estop（202）。不进写信号量、不重试、不受 writeRateLimit 影响（服务端单独 5 次/秒）。</summary>
    public Task<CommandResult<JsonElement>> EstopAsync(string? note, CancellationToken ct)
        => SendCommandOnceAsync<JsonElement>(HttpMethod.Post, "estop", ClientJson.Body(note == null ? null : new { note }), null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StopAllAsync(bool? stopStir, CancellationToken ct)
        => StopCommandAsync<JsonElement>(HttpMethod.Post, "stop-all", stopStir == null ? null : new { stopStir }, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StopChannelAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/stop", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> DisableChannelAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/disable", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StopHeatAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/heat/stop", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StopProfileAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/profile/stop", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> CancelAutotuneAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Delete, $"channels/{id}/autotune", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> AbortProgramAsync(int id, CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Delete, $"channels/{id}/program/run", null, null, NewKey(), ct);
    public Task<CommandResult<JsonElement>> AbortLeakTestAsync(CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Delete, "leak-tests/current", null, null, NewKey(), ct);
    /// <summary>PUT /stir {on:false}（唯一的停止类搅拌写法）。</summary>
    public Task<CommandResult<JsonElement>> StopStirAsync(CancellationToken ct) => StopCommandAsync<JsonElement>(HttpMethod.Put, "stir", new { on = false }, null, NewKey(), ct);

    // ======================================================================
    // 控制类（带租约）
    // ======================================================================

    public Task<CommandResult<JsonElement>> RunAsync(string lease, string? idem, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, "run", null, lease, idem ?? NewKey(), ct);
    public Task<CommandResult<JsonElement>> EnableChannelAsync(string lease, int id, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/enable", null, lease, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StartChannelAsync(string lease, int id, double? tempC, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/start", tempC == null ? null : new { tempC }, lease, NewKey(), ct);
    public Task<CommandResult<JsonElement>> CopyChannelConfigAsync(string lease, int from, int[]? to, string? idem, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, "channels/copy-config", to == null ? new { from } : new { from, to }, lease, idem ?? NewKey(), ct);
    public Task<CommandResult<JsonElement>> ResetGasCounterAsync(string lease, int id, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/gas-counter/reset", null, lease, NewKey(), ct);
    /// <summary>POST /channels/{id}/heat/start {tempC?}（202 accepted）。</summary>
    public Task<CommandResult<JsonElement>> StartHeatAsync(string lease, int id, double? tempC, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/heat/start", tempC == null ? null : new { tempC }, lease, NewKey(), ct);
    public Task<CommandResult<SetpointResult>> SetSetpointAsync(string lease, int id, double tempC, CancellationToken ct)
        => CommandAsync<SetpointResult>(HttpMethod.Put, $"channels/{id}/temperature/setpoint", new { tempC }, lease, NewKey(), ct);
    /// <summary>PUT /channels/{id}/temperature/mode {spMode: fixed|curve}。</summary>
    public Task<CommandResult<JsonElement>> SetSpModeAsync(string lease, int id, string spMode, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Put, $"channels/{id}/temperature/mode", new { spMode }, lease, NewKey(), ct);
    public Task<CommandResult<JsonElement>> ReplaceProfileAsync(string lease, int id, IEnumerable<(double TempC, double Minutes)> segments, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Put, $"channels/{id}/profile", new { segments = segments.Select(s => new { tempC = s.TempC, minutes = s.Minutes }).ToArray() }, lease, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StartProfileAsync(string lease, int id, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/profile/start", null, lease, NewKey(), ct);
    /// <summary>PUT /channels/{id}/pid/{band} {p,i,d}（admin）。</summary>
    public Task<CommandResult<JsonElement>> SetPidAsync(string lease, int id, int band, double p, double i, double d, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Put, $"channels/{id}/pid/{band}", new { p, i, d }, lease, NewKey(), ct);
    public Task<CommandResult<JsonElement>> StartAutotuneAsync(string lease, int id, double tempC, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/autotune", new { tempC }, lease, NewKey(), ct);
    /// <summary>PUT /stir {on?, rpm?}（带 rpm 或 on=true 为控制类）。</summary>
    public Task<CommandResult<JsonElement>> SetStirAsync(string lease, bool? on, int? rpm, CancellationToken ct)
    {
        var body = new Dictionary<string, object>();
        if (on != null) body["on"] = on.Value;
        if (rpm != null) body["rpm"] = rpm.Value;
        if (body.Count == 0) throw new ArgumentException("on / rpm 至少给一个");
        return CommandAsync<JsonElement>(HttpMethod.Put, "stir", body, lease, NewKey(), ct);
    }
    public Task<CommandResult<LeakTestStartResult>> StartLeakTestAsync(string lease, int[] channels, double? testPsi, string? idem, CancellationToken ct)
        => CommandAsync<LeakTestStartResult>(HttpMethod.Post, "leak-tests", testPsi == null ? new { channels } : new { channels, testPsi }, lease, idem ?? NewKey(), ct);
    /// <summary>PUT /channels/{id}/program {steps[]}。</summary>
    public Task<CommandResult<JsonElement>> ReplaceProgramAsync(string lease, int id, ProgramDoc program, CancellationToken ct)
        => CommandRawAsync<JsonElement>(HttpMethod.Put, $"channels/{id}/program", program.ToJson(), lease, NewKey(), ct, _writeGate);
    /// <summary>POST /channels/{id}/program/run {sessionId?}（202）。</summary>
    public Task<CommandResult<ProgramRunResult>> RunProgramAsync(string lease, int id, string? sessionId, string? idem, CancellationToken ct)
        => CommandAsync<ProgramRunResult>(HttpMethod.Post, $"channels/{id}/program/run", sessionId == null ? null : new { sessionId }, lease, idem ?? NewKey(), ct);
    public Task<CommandResult<JsonElement>> CopyProgramAsync(string lease, int id, int[] to, string? idem, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"channels/{id}/program/copy", new { to }, lease, idem ?? NewKey(), ct);

    // ======================================================================
    // 记录类（无租约）
    // ======================================================================

    public Task<CommandResult<JsonElement>> AckAlarmAsync(string alarmId, CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Post, $"alarms/{Uri.EscapeDataString(alarmId)}/ack", null, null, NewKey(), ct);
    /// <summary>POST /sessions {testId, note?}（Idem）。已有会话 → 409 SESSION_ACTIVE。</summary>
    public Task<CommandResult<SessionBeginResult>> BeginSessionAsync(string testId, string? note, string? idem, CancellationToken ct)
        => CommandAsync<SessionBeginResult>(HttpMethod.Post, "sessions", note == null ? new { testId } : new { testId, note }, null, idem ?? NewKey(), ct);
    public Task<CommandResult<JsonElement>> EndSessionAsync(CancellationToken ct)
        => CommandAsync<JsonElement>(HttpMethod.Delete, "sessions/current", null, null, NewKey(), ct);
    public Task<CommandResult<MarkerResult>> AddMarkerAsync(string text, string? idem, CancellationToken ct)
        => CommandAsync<MarkerResult>(HttpMethod.Post, "sessions/current/markers", new { text }, null, idem ?? NewKey(), ct);

    // ======================================================================
    // 管线
    // ======================================================================

    private string NewKey() => _opt.IdempotencyKeyFactory();

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
            var body = await ReadAsync(resp, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                await _clock.Delay(RetryAfter(resp), ct).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode) throw ApiException.Parse((int)resp.StatusCode, body, $"GET {path} 失败");
            if (typeof(T) == typeof(JsonElement)) return (T)(object)JsonDocument.Parse(body).RootElement.Clone();
            return ApiJson.Deserialize<T>(body) ?? throw new ApiException("BAD_RESPONSE", (int)resp.StatusCode, $"GET {path}：响应为空");
        }
    }

    private async Task DownloadAsync(string path, Stream dest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _requests);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await ReadAsync(resp, ct).ConfigureAwait(false);
            throw ApiException.Parse((int)resp.StatusCode, body, $"GET {path} 失败");
        }
        await resp.Content.CopyToAsync(dest, ct).ConfigureAwait(false);
    }

    private Task<CommandResult<T>> CommandAsync<T>(HttpMethod method, string path, object? body, string? lease, string idem, CancellationToken ct)
        => CommandRawAsync<T>(method, path, ClientJson.Body(body), lease, idem, ct, _writeGate);

    /// <summary>停止类（无租约）：走 <see cref="_stopGate"/>，同样的同键重试规则，但不排在受控写后面。</summary>
    private Task<CommandResult<T>> StopCommandAsync<T>(HttpMethod method, string path, object? body, string? lease, string idem, CancellationToken ct)
        => CommandRawAsync<T>(method, path, ClientJson.Body(body), lease, idem, ct, _stopGate);

    /// <summary>写命令：串行 + 最小间隔 + 同键重试规则（§13.3）。</summary>
    private async Task<CommandResult<T>> CommandRawAsync<T>(HttpMethod method, string path, string json, string? lease, string idem, CancellationToken ct, SemaphoreSlim gate)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gap = _opt.MinWriteGap - (_clock.UtcNow - _lastWriteAt);
            if (gap > TimeSpan.Zero) await _clock.Delay(gap, ct).ConfigureAwait(false);
            int unknown = 0, busy = 0;
            while (true)
            {
                try
                {
                    var r = await SendCommandOnceAsync<T>(method, path, json, lease, idem, ct).ConfigureAwait(false);
                    _lastWriteAt = _clock.UtcNow;
                    return r;
                }
                catch (ApiException ex) when (ex.Code is OpCodes.OutcomeUnknown or OpCodes.IdempotencyInProgress && unknown < _opt.WriteRetries)
                {
                    unknown++;
                }
                catch (ApiException ex) when (ex.Code is OpCodes.Starting or OpCodes.UiThreadTimeout && busy < _opt.BusyRetries)
                {
                    busy++;
                }
                catch (HttpRequestException) when (unknown < _opt.WriteRetries)
                {
                    unknown++;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && unknown < _opt.WriteRetries)
                {
                    unknown++;   // HttpClient 超时（不是调用方取消）→ 结局未知，同键重发
                }
                await _clock.Delay(_opt.WriteRetryDelay, ct).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>发一次写命令（不经信号量、不重试；/estop 与心跳直接用它）。429 读 Retry-After 重试一次。</summary>
    private async Task<CommandResult<T>> SendCommandOnceAsync<T>(HttpMethod method, string path, string json, string? lease, string? idem, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(lease)) req.Headers.TryAddWithoutValidation(ApiHeaders.Lease, lease);
            if (!string.IsNullOrEmpty(idem)) req.Headers.TryAddWithoutValidation(ApiHeaders.Idempotency, idem);
            using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
            var body = await ReadAsync(resp, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                await _clock.Delay(RetryAfter(resp), ct).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _failures);
                throw ApiException.Parse((int)resp.StatusCode, body, $"{method} {path} 失败");
            }
            bool replayed = resp.Headers.TryGetValues(ApiHeaders.IdempotentReplay, out var v) && v.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));
            var dto = ApiJson.Deserialize<CommandResponseDto>(body) ?? throw new ApiException("BAD_RESPONSE", (int)resp.StatusCode, $"{method} {path}：响应不是命令结果");
            return CommandResult<T>.From(dto, replayed, (int)resp.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
        => resp.Content == null ? "" : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    private static TimeSpan RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } at) { var w = at - DateTimeOffset.UtcNow; return w > TimeSpan.Zero ? w : TimeSpan.FromSeconds(1); }
        return TimeSpan.FromSeconds(1);
    }

    public void Dispose()
    {
        _http.Dispose();
        _writeGate.Dispose();
        _stopGate.Dispose();
    }
}
