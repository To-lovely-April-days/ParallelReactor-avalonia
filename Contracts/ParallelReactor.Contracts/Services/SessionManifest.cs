using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.Services;

/// <summary>会话里的一次打点（§6.11）：服务端时间、当时的事件 seq、文字、来源 / 客户端。</summary>
public sealed record SessionMarker(int Id, DateTime Ts, long Seq, string Text, string Source, string? Client);
/// <summary>会话期间下发的程序。</summary>
public sealed record SessionProgramRun(int Rv, DateTime StartedAt, string StartedBy);
/// <summary>会话期间开始的泄漏测试。</summary>
public sealed record SessionLeakTest(string RunId, DateTime StartedAt);

/// <summary>
/// 会话元数据（设计稿 §6.11 / §9.1 K6 的最小实现）：落盘为 <c>sessions/&lt;id&gt;.json</c>，开始 / 打点 / 标签 / 结束时整文件重写。
/// <c>id</c> = 会话文件主名 <c>&lt;testId&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>（本机唯一）；<c>testId</c> 为调用方给的测试编号（可重复）。
/// state：active / ended / interrupted（开机发现 active → interrupted，K7）。不可变 record，<see cref="SessionBook"/> 用 with 生成新版本。
/// </summary>
public sealed record SessionManifest(
    string Id,
    string TestId,
    string? Note,
    DateTime StartedAt,
    DateTime? EndedAt,
    string State,
    string Source,
    string? Client,
    bool Simulated,
    long StartEventSeq,
    long? EndEventSeq,
    string File,
    SessionMarker[] Markers,
    SessionProgramRun[] ProgramRuns,
    SessionLeakTest[] LeakTests)
{
    public const string Active = "active", Ended = "ended", Interrupted = "interrupted";

    public static string RelPath(string id) => $"sessions/{id}.json";

    public string ToJson() => JsonSerializer.Serialize(this, SessionJsonContext.Default.SessionManifest);

    public static SessionManifest? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionManifest); }
        catch { return null; }
    }

    public static SessionManifest? Load(string path)
    {
        try { return System.IO.File.Exists(path) ? FromJson(System.IO.File.ReadAllText(path)) : null; }
        catch { return null; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SessionManifest))]
internal partial class SessionJsonContext : JsonSerializerContext
{
}

