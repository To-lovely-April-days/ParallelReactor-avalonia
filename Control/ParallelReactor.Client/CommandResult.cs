using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ParallelReactor.Api;

namespace ParallelReactor.Client;

/// <summary>写命令成功响应（§7.1：200 confirmed / 202 accepted）。<typeparamref name="T"/> 为 result 的类型（未知形状用 <see cref="JsonElement"/>）。</summary>
public sealed record CommandResult<T>(bool Ok, string OpId, string Status, T? Result, string[] Warnings, long OpEventSeq, bool Replayed, int HttpStatus)
{
    public const string Confirmed = "confirmed", Accepted = "accepted";
    public bool IsConfirmed => Status == Confirmed;
    public bool IsAccepted => Status == Accepted;

    /// <summary>从 DTO 转换；result 为 null 时 Result 为 default。</summary>
    public static CommandResult<T> From(CommandResponseDto dto, bool replayed, int httpStatus)
        => new(dto.Ok, dto.OpId, dto.Status, dto.Result is { } el ? ClientJson.FromElement<T>(el) : default, dto.Warnings ?? Array.Empty<string>(), dto.OpEventSeq, replayed, httpStatus);
}

/// <summary>
/// 客户端自己的 JSON 规则：Contracts 里有的类型走 <see cref="ApiJson"/>（源生成，字段名零漂移）；
/// 服务端 result 里的匿名对象（会话 / 打点 / 程序运行…）用下面的小 record + 反射（camelCase、大小写不敏感）。
/// </summary>
public static class ClientJson
{
    public static readonly JsonSerializerOptions Reflection = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JsonElement → T：T 为 JsonElement 直接返回；Contracts 类型用 ApiJson；其余反射。</summary>
    public static T? FromElement<T>(JsonElement el)
    {
        if (typeof(T) == typeof(JsonElement)) return (T)(object)el.Clone();
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return default;
        var raw = el.GetRawText();
        if (ApiJson.Options.TypeInfoResolver?.GetTypeInfo(typeof(T), ApiJson.Options) != null)
            return ApiJson.Deserialize<T>(raw);
        return JsonSerializer.Deserialize<T>(raw, Reflection);
    }

    /// <summary>请求正文：匿名对象 / 字典 → JSON（camelCase）。</summary>
    public static string Body(object? o) => o == null ? "{}" : JsonSerializer.Serialize(o, o.GetType(), Reflection);
}

// —— 服务端 result 的常见形状（Api/Hmi 侧的匿名对象；字段按 walk.sh 实测）——
/// <summary>POST /sessions 的 result。</summary>
public sealed record SessionBeginResult(string Id, string TestId, DateTime StartedAt, long StartEventSeq, string File);
/// <summary>POST /sessions/current/markers 的 result。</summary>
public sealed record MarkerResult(int Id, string Text, DateTime Ts, long Seq, string SessionId);
/// <summary>POST /channels/{id}/program/run 的 result。</summary>
public sealed record ProgramRunResult(int Rv, int Steps);
/// <summary>PUT /channels/{id}/temperature/setpoint 的 result。</summary>
public sealed record SetpointResult(int Channel, double TempSpC, bool Applied, bool Coalesced, bool ApproachReplanned);
/// <summary>POST /leak-tests 的 result。</summary>
public sealed record LeakTestStartResult(string RunId);
/// <summary>POST /control/release 的 result。</summary>
public sealed record ReleaseResult(string Mode);
