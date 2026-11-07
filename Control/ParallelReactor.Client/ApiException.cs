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
