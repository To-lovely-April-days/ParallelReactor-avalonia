using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParallelReactor.Services;

// SessionMarker / SessionProgramRun / SessionLeakTest / SessionManifest（含 SessionJsonContext）已移至
// Contracts/ParallelReactor.Contracts/Services/SessionManifest.cs（HMI 与 PC 共用，命名空间不变）。本文件只留 SessionBook。

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
