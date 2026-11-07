using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>
/// 接口 JSON 的源生成序列化上下文（设计稿 §3.2：不开 Trimming，但用源生成降低 ARM 首次 JIT 开销）。
/// camelCase；null 照常输出（无效读数 = null 是协议的一部分，不省略）。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(DeviceInfoDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(TelemetrySnapshot))]
[JsonSerializable(typeof(SnapshotBundle))]
[JsonSerializable(typeof(ChannelsDto))]
[JsonSerializable(typeof(ChannelDetailDto))]
[JsonSerializable(typeof(ChannelSnap))]
[JsonSerializable(typeof(ChannelSnap[]))]
[JsonSerializable(typeof(EventsPageDto))]
[JsonSerializable(typeof(EventRecord))]
[JsonSerializable(typeof(List<EventRecord>))]
[JsonSerializable(typeof(AlarmsDto))]
[JsonSerializable(typeof(AlarmDto))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(PreflightDto))]
[JsonSerializable(typeof(ProgramSchemaDto))]
[JsonSerializable(typeof(CommDto))]
[JsonSerializable(typeof(FilesDto))]
[JsonSerializable(typeof(LeakTestsDto))]
[JsonSerializable(typeof(LeakTestInfoDto))]
[JsonSerializable(typeof(SessionsDto))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(SessionManifest))]
[JsonSerializable(typeof(TelemetryRangeInfoDto))]
[JsonSerializable(typeof(HelloMsg))]
[JsonSerializable(typeof(PongMsg))]
[JsonSerializable(typeof(GapMsg))]
[JsonSerializable(typeof(WsErrorMsg))]
[JsonSerializable(typeof(SubscribedMsg))]
[JsonSerializable(typeof(AlarmMsg))]
[JsonSerializable(typeof(EventMsg))]
// —— P3 控制权 / 写命令 ——
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(CommandResponseDto))]
[JsonSerializable(typeof(CommandErrorDto))]
[JsonSerializable(typeof(ControlStateDto))]
[JsonSerializable(typeof(ControlRequestResultDto))]
[JsonSerializable(typeof(ControlRequestStatusDto))]
[JsonSerializable(typeof(HeartbeatResultDto))]
[JsonSerializable(typeof(ControlMsg))]
[JsonSerializable(typeof(OpEventMsg))]
public partial class ApiJsonContext : JsonSerializerContext
{
}

/// <summary>统一的序列化入口（REST 响应与 WS 消息都用它，保证字段名一致）。中文不转义成 \uXXXX（UTF-8 正文，curl 直接可读）。</summary>
public static class ApiJson
{
    /// <summary>源生成解析器 + 宽松转义（只在 ASP.NET Core 响应 / WS 文本帧里使用，正文始终 UTF-8）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = ApiJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 反射解析器（camelCase）：只用于把 <c>OpResult.Data</c> 这类匿名对象转成 <see cref="JsonElement"/>（源生成不认识匿名类型；
    /// 项目不 Trim，§3.2 允许）。在 Kestrel 线程做，不进 UI 线程。
    /// </summary>
    public static readonly JsonSerializerOptions Reflection = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Options);

    public static byte[] SerializeUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), Options);

    public static T? Deserialize<T>(string json) => (T?)JsonSerializer.Deserialize(json, typeof(T), Options);

    /// <summary>任意对象（含匿名类型）→ JsonElement；null → null。失败时返回带 error 字段的对象而不是抛出（响应形状不能因 Data 而坏）。</summary>
    public static JsonElement? ToElement(object? data)
    {
        if (data == null) return null;
        if (data is JsonElement je) return je;
        try { return JsonSerializer.SerializeToElement(data, data.GetType(), Reflection); }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["error"] = "result 序列化失败：" + ex.GetBaseException().Message }, Reflection);
        }
    }
}
