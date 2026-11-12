using System;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Commands;

namespace ParallelReactor.Client;

/// <summary>服务端返回非 2xx（§7.1 <c>{ok:false, error{code,message,detail}}</c>，写命令另带 opId / opEventSeq）。</summary>
public sealed class ApiException : Exception
{
    public ApiException(string code, int status, string message, string? opId = null, JsonElement? detail = null, long opEventSeq = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
        OpId = opId;
        Detail = detail;
        OpEventSeq = opEventSeq;
    }

    /// <summary>§11 错误码。</summary>
    public string Code { get; }
    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }
    /// <summary>写命令的 opId（读端点为 null）。</summary>
    public string? OpId { get; }
    public JsonElement? Detail { get; }
    public long OpEventSeq { get; }

    public bool IsConflict => Status == 409;
    /// <summary>租约类拒绝：须重新申请控制权（§5.4）。</summary>
    public bool IsLeaseError => Code is OpCodes.ModeLocal or OpCodes.NotControlHolder or OpCodes.ControlLeaseExpired;
    /// <summary>结局未知（§13.3）：同键重发可拿到真实结果。</summary>
    public bool IsOutcomeUnknown => Code == OpCodes.OutcomeUnknown;
    public bool IsRateLimited => Status == 429;

    // —— M3 增补（只加不改；R9 / R19）——
    /// <summary>403 FORBIDDEN_ROLE：令牌角色不够——viewer（只读）令牌发写命令 / 没有急停权限位 / 需要 admin（HMI 的原话在 Message 里）。R9：界面据此学习「只读令牌」。</summary>
    public bool IsForbiddenRole => Code == OpCodes.ForbiddenRole;
    /// <summary>任何 403（FORBIDDEN_ROLE / IP_NOT_ALLOWED / NOT_EXPOSED / SESSION_NOT_OWNER）。</summary>
    public bool IsForbidden => Status == 403;
    /// <summary>401 UNAUTHORIZED：令牌无效或缺失。</summary>
    public bool IsUnauthorized => Status == 401;
    /// <summary>503 TOO_MANY_CLIENTS：HMI 的 WS 连接数已满（R19；REST 不会回这个码，WS 握手失败见 <see cref="WsConnectException"/>）。</summary>
    public bool IsTooManyClients => Code == OpCodes.TooManyClients;

    /// <summary>从响应正文解析（写命令格式优先，其次读端点格式；都不是时用状态码造一个）。</summary>
    public static ApiException Parse(int status, string body, string fallbackMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                string code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "HTTP_" + status;
                string msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : fallbackMessage;
                JsonElement? detail = err.TryGetProperty("detail", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
                string? opId = root.TryGetProperty("opId", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                long seq = root.TryGetProperty("opEventSeq", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv) ? sv : 0;
                return new ApiException(code, status, msg, opId, detail, seq);
            }
        }
        catch (JsonException) { }
        return new ApiException("HTTP_" + status, status, string.IsNullOrWhiteSpace(body) ? fallbackMessage : fallbackMessage + " · " + Truncate(body), null, null, 0);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public override string ToString() => $"ApiException {Status} {Code}: {Message}" + (OpId != null ? $" (opId {OpId})" : "");
}

/// <summary>
/// 一次 403 FORBIDDEN_ROLE（M3 增补，R9）：哪个请求被拒（<see cref="Method"/> + 相对 /api/v1/ 的 <see cref="Path"/>，如 POST channels/1/stop）、
/// HMI 的原话（如「viewer 令牌只能读取 · 写命令需要 operator 及以上角色」「该令牌没有急停权限（estop）」）。<see cref="At"/> 为本机时钟。
/// 由 <see cref="ReactorApiClient.Forbidden"/> / <see cref="IReactorConnection.ForbiddenRole"/> 发出；Client 内部吞掉的 403（心跳、申请轮询）也会发。
/// </summary>
public sealed record ForbiddenNotice(string Method, string Path, string Code, string Message, DateTime At);
