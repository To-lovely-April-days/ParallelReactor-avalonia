using System;
using System.Net.Http;
using ParallelReactor.Commands;

namespace ParallelReactor.Client;

/// <summary>
/// WebSocket 连不上的原因（M3 增补，R19）：<see cref="TelemetryWsClient.LastError"/> 里放的就是它（原始异常在 <see cref="Exception.InnerException"/>）。
/// <see cref="TelemetryWsClient.Error"/> 事件仍收到原始异常，现有订阅方的行为不变。
/// <para>握手被拒时 <c>ClientWebSocket</c> 只给状态码、不给正文，所以 <see cref="Code"/> 按 HMI 在 /api/v1/ws 上会回的错误推断
/// （Api/ApiHost.cs 的白名单 / 鉴权中间件、Api/WsHub.cs 的连接数上限）：503 只可能是 TOO_MANY_CLIENTS（每台 HMI 默认最多 4 个 WS 客户端）。</para>
/// <para>文字（<see cref="Exception.Message"/>）不在样稿里：样稿没有连接失败原因的写法；措辞沿用 HMI 自己的错误文字，末尾带状态码与错误码，
/// 界面要显示时自己决定放不放（PORT-PLAN §5.0：屏幕文字以样稿为准）。</para>
/// </summary>
public sealed class WsConnectException : Exception
{
    /// <summary>没有拿到 HTTP 响应（拒绝连接 / 找不到主机 / 超时 / TLS）时的 <see cref="Code"/>。</summary>
    public const string NetworkCode = "NETWORK";

    public WsConnectException(int? httpStatus, string code, string message, Exception? inner = null) : base(message, inner)
    {
        HttpStatus = httpStatus;
        Code = code;
    }

    /// <summary>握手响应的 HTTP 状态码；null = 没有拿到 HTTP 响应。</summary>
    public int? HttpStatus { get; }
    /// <summary>§11 错误码（推断）：TOO_MANY_CLIENTS / UNAUTHORIZED / IP_NOT_ALLOWED / RATE_LIMITED / NOT_FOUND / CLIENT_TOO_OLD / BAD_REQUEST / HTTP_&lt;n&gt; / NETWORK。</summary>
    public string Code { get; }

    /// <summary>503 TOO_MANY_CLIENTS：这台 HMI 的 WS 连接数已满（R19）。</summary>
    public bool IsTooManyClients => Code == OpCodes.TooManyClients;
    /// <summary>401：令牌无效或缺失。</summary>
    public bool IsUnauthorized => HttpStatus == 401;
    /// <summary>403：本机地址不在 HMI 允许的网段内。</summary>
    public bool IsForbidden => HttpStatus == 403;
    /// <summary>没有拿到 HTTP 响应（网络层失败）。</summary>
    public bool IsNetwork => HttpStatus == null;

    /// <summary>把一次连接失败归类。<paramref name="handshakeStatus"/> 取自 <see cref="IWsTransport.HandshakeStatusCode"/>（null / 0 / 101 = 没有被拒的 HTTP 响应）。</summary>
    public static WsConnectException From(Exception error, int? handshakeStatus)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (handshakeStatus is int s && s != 0 && s != 101)
        {
            var (code, text) = s switch
            {
                400 => (OpCodes.BadRequest, "WebSocket 升级请求被拒"),
                401 => (OpCodes.Unauthorized, "缺少或无效的 Bearer 令牌"),
                403 => (OpCodes.IpNotAllowed, "本机地址不在设备允许的网段内"),
                404 => (OpCodes.NotFound, "设备没有 WebSocket 接口 /api/v1/ws"),
                426 => (OpCodes.ClientTooOld, "客户端版本过旧"),
                429 => (OpCodes.RateLimited, "鉴权失败过多 · 稍后再试"),
                503 => (OpCodes.TooManyClients, "WebSocket 连接数已达上限 · 请关闭该设备上的其他客户端"),
                _ => ("HTTP_" + s, "WebSocket 握手失败"),
            };
            return new WsConnectException(s, code, $"{text}（HTTP {s} {code}）", error);
        }
        return new WsConnectException(null, NetworkCode, "无法建立 WebSocket 连接 · " + Innermost(error).Message, error);
    }

    private static Exception Innermost(Exception e)
    {
        // ClientWebSocket 把网络层原因包在 HttpRequestException（"Connection refused (host:port)"，带地址）/ SocketException 里，
        // 外层 "Unable to connect to the remote server" 没有信息量：优先取 HttpRequestException，其次最里层
        for (var cur = e; cur != null; cur = cur.InnerException)
            if (cur is HttpRequestException) return cur;
        var last = e;
        while (last.InnerException != null) last = last.InnerException;
        return last;
    }

    public override string ToString() => $"WsConnectException {(HttpStatus is int s ? s.ToString() : "-")} {Code}: {Message}";
}
