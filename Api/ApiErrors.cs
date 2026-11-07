using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>§11 错误码 → HTTP 状态码，统一的 <c>{ok:false, error:{code,message,detail}}</c> 输出。</summary>
public static class ApiErrors
{
    public const string JsonContentType = "application/json; charset=utf-8";

    public static int StatusOf(string code) => code switch
    {
        OpCodes.BadRequest => 400,
        OpCodes.Unauthorized => 401,
        OpCodes.ForbiddenRole or OpCodes.IpNotAllowed or OpCodes.NotExposed => 403,
        OpCodes.NotFound or OpCodes.ChannelNotFound => 404,
        OpCodes.RateLimited or OpCodes.DownloadBusy => 429,
        OpCodes.TooManyClients or OpCodes.Starting or OpCodes.UiThreadTimeout or OpCodes.HwOffline => 503,
        OpCodes.OutcomeUnknown => 504,
        OpCodes.Internal => 500,
        _ => 409,
    };

    public static string Body(string code, string message, Dictionary<string, string>? detail = null)
        => ApiJson.Serialize(new ErrorDto(false, new ErrorBody(code, message, detail)));

    /// <summary>端点用：返回带状态码的错误 JSON。</summary>
    public static IResult Result(string code, string message, Dictionary<string, string>? detail = null)
        => Results.Text(Body(code, message, detail), "application/json", Encoding.UTF8, StatusOf(code));

    /// <summary>中间件用：直接写响应（已开始写入时不再动）。</summary>
    public static async Task WriteAsync(HttpResponse resp, string code, string message, Dictionary<string, string>? detail = null, CancellationToken ct = default)
    {
        if (resp.HasStarted) return;
        resp.StatusCode = StatusOf(code);
        resp.ContentType = JsonContentType;
        if (code is OpCodes.RateLimited or OpCodes.DownloadBusy) resp.Headers["Retry-After"] = "1";
        await resp.WriteAsync(Body(code, message, detail), ct).ConfigureAwait(false);
    }

    /// <summary>端点用：200 JSON。</summary>
    public static IResult Ok<T>(T dto) => Results.Text(ApiJson.Serialize(dto), "application/json", Encoding.UTF8, 200);
}

/// <summary>接口运行统计（/device/comm 的 api 段）。全部 Interlocked，任意线程读写。</summary>
public sealed class ApiStats
{
    private int _clients;
    private long _dropped, _droppedEvents, _frames, _events, _authFail, _requests;

    public int Clients => Volatile.Read(ref _clients);
    public long DroppedTelemetryFrames => Interlocked.Read(ref _dropped);
    /// <summary>中枢事件队列满而丢弃的事件 / 报警消息数（每次溢出同时断开全部连接，让客户端重同步）。</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);
    public long FramesSent => Interlocked.Read(ref _frames);
    public long EventsSent => Interlocked.Read(ref _events);
    public long AuthFailures => Interlocked.Read(ref _authFail);
    public long Requests => Interlocked.Read(ref _requests);
    public DateTime? StartedAt { get; set; }
    public string Bind { get; set; } = "";
    public int Port { get; set; }

    public int ClientConnected() => Interlocked.Increment(ref _clients);
    public int ClientDisconnected() => Interlocked.Decrement(ref _clients);
    public void Dropped(long n = 1) => Interlocked.Add(ref _dropped, n);
    public void EventDropped() => Interlocked.Increment(ref _droppedEvents);
    public void FrameSent() => Interlocked.Increment(ref _frames);
    public void EventSent() => Interlocked.Increment(ref _events);
    public void AuthFailed() => Interlocked.Increment(ref _authFail);
    public void Request() => Interlocked.Increment(ref _requests);

    public ApiStatsDto ToDto(int maxClients) => new(Clients, maxClients, DroppedTelemetryFrames, DroppedEvents, FramesSent, EventsSent, AuthFailures, Requests, StartedAt, Bind, Port);
}
