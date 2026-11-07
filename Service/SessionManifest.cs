using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

/// <summary>
/// 当前会话的 manifest 维护（纯逻辑，单写者 = UI 线程；<see cref="Current"/> 是不可变快照，任意线程可读——接口的 /sessions/current 读它）。
/// 每次变化经 <c>write(relPath, json)</c> 落盘（主工程传 DataLogger.WriteFile，在后台任务写）。
/// </summary>
public sealed class SessionBook
{
    private readonly Action<string, string> _write;
    private volatile SessionManifest? _current;

    public SessionBook(Action<string, string> write) => _write = write;

    /// <summary>当前活动会话的 manifest（无会话为 null）。</summary>
    public SessionManifest? Current => _current;

    /// <summary>当前会话 id（无会话为 null）。</summary>
    public string? CurrentId => _current?.Id;

    public SessionManifest Begin(string id, string testId, string? note, DateTime nowUtc, string source, string? client, bool simulated, long startSeq, string file)
    {
        var m = new SessionManifest(id, testId, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), nowUtc, null, SessionManifest.Active,
            source, client, simulated, startSeq, null, file, Array.Empty<SessionMarker>(), Array.Empty<SessionProgramRun>(), Array.Empty<SessionLeakTest>());
        _current = m;
        Flush(m);
        return m;
    }

    /// <summary>打点：返回标记（含 id）；无会话返回 null。</summary>
    public SessionMarker? AddMarker(string text, DateTime nowUtc, long seq, string source, string? client)
    {
        var m = _current;
        if (m == null) return null;
        var mk = new SessionMarker(m.Markers.Length + 1, nowUtc, seq, text, source, client);
        m = m with { Markers = [.. m.Markers, mk] };
        _current = m;
        Flush(m);
        return mk;
    }

    public void TagProgramRun(int rv, DateTime nowUtc, string startedBy)
    {
        var m = _current;
        if (m == null) return;
        m = m with { ProgramRuns = [.. m.ProgramRuns, new SessionProgramRun(rv, nowUtc, startedBy)] };
        _current = m;
        Flush(m);
    }

    public void TagLeakTest(string runId, DateTime nowUtc)
    {
        var m = _current;
        if (m == null) return;
        m = m with { LeakTests = [.. m.LeakTests, new SessionLeakTest(runId, nowUtc)] };
        _current = m;
        Flush(m);
    }

    /// <summary>结束：state=ended，写最终版本；返回结束的 manifest（无会话为 null）。</summary>
    public SessionManifest? End(DateTime nowUtc, long endSeq)
    {
        var m = _current;
        if (m == null) return null;
        m = m with { EndedAt = nowUtc, EndEventSeq = endSeq, State = SessionManifest.Ended };
        _current = null;
        Flush(m);
        return m;
    }

    private void Flush(SessionManifest m)
    {
        try { _write(SessionManifest.RelPath(m.Id), m.ToJson()); } catch { /* 写盘失败由 DataLogger 上报 */ }
    }

    /// <summary>
    /// 开机恢复（K7）：数据目录下 state=active 的 manifest 改为 interrupted，endedAt 取该会话 CSV（<c>sessions/&lt;file&gt;</c>，每 Tick 追加一行）的最后修改时间
    /// ≈ 最后一行遥测的时刻；没有 CSV 时退回 manifest 自身的修改时间。
    /// 同步写（只在启动时调用一次）。返回处理的会话 id。
    /// </summary>
    public static IReadOnlyList<string> RecoverInterrupted(string dataDir)
    {
        var done = new List<string>();
        var dir = Path.Combine(dataDir, "sessions");
        if (!Directory.Exists(dir)) return done;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            var m = SessionManifest.Load(f);
            if (m == null || m.State != SessionManifest.Active) continue;
            try
            {
                var csv = string.IsNullOrEmpty(m.File) ? null : Path.Combine(dir, Path.GetFileName(m.File));
                var endedAt = csv != null && System.IO.File.Exists(csv) ? System.IO.File.GetLastWriteTimeUtc(csv) : System.IO.File.GetLastWriteTimeUtc(f);
                var ended = m with { State = SessionManifest.Interrupted, EndedAt = endedAt };
                System.IO.File.WriteAllText(f, ended.ToJson(), new UTF8Encoding(true));
                done.Add(m.Id);
            }
            catch { }
        }
        return done;
    }
}
