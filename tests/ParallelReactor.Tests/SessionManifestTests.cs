using System;
using System.Collections.Generic;
using System.IO;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>§6.11 / §9.1 K6–K7：会话 manifest 的开始 / 打点 / 标签 / 结束、JSON 往返、开机 interrupted。</summary>
public class SessionManifestTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Begin_Marker_Tags_End_WriteEachVersion()
    {
        var writes = new List<(string Path, string Json)>();
        var book = new SessionBook((p, j) => writes.Add((p, j)));
        Assert.Null(book.Current);
        var m = book.Begin("TP-017-20261107-080000", "TP-017", " 性能测试 ", T0, "remote", "PC-1", true, 100, "TP-017-20261107-080000.csv");
        Assert.Equal("TP-017-20261107-080000", book.CurrentId);
        Assert.Equal("性能测试", m.Note);
        Assert.Equal(SessionManifest.Active, m.State);
        Assert.Equal("sessions/TP-017-20261107-080000.json", writes[0].Path);

        var mk = book.AddMarker("开始升温", T0.AddMinutes(1), 105, "remote", "PC-1")!;
        Assert.Equal(1, mk.Id);
        Assert.Equal(2, book.AddMarker("保温", T0.AddMinutes(2), 106, "local", null)!.Id);
        book.TagProgramRun(1, T0.AddMinutes(3), "远程（PC-1）");
        book.TagLeakTest("leak-20261107-080500", T0.AddMinutes(5));
        var cur = book.Current!;
        Assert.Equal(2, cur.Markers.Length);
        Assert.Single(cur.ProgramRuns);
        Assert.Equal("远程（PC-1）", cur.ProgramRuns[0].StartedBy);
        Assert.Single(cur.LeakTests);

        var ended = book.End(T0.AddMinutes(10), 120)!;
        Assert.Null(book.Current);
        Assert.Equal(SessionManifest.Ended, ended.State);
        Assert.Equal(120, ended.EndEventSeq);
        Assert.Equal(6, writes.Count);   // begin + 2 markers + 2 tags + end

        // JSON 往返（camelCase、嵌套数组）
        var back = SessionManifest.FromJson(writes[^1].Json)!;
        Assert.Equal(ended, back with { Markers = ended.Markers, ProgramRuns = ended.ProgramRuns, LeakTests = ended.LeakTests });
        Assert.Equal("开始升温", back.Markers[0].Text);
        Assert.Contains("\"testId\": \"TP-017\"", writes[^1].Json);
    }

    [Fact]
    public void NoSession_MarkerAndTags_AreNoOps()
    {
        var writes = 0;
        var book = new SessionBook((_, _) => writes++);
        Assert.Null(book.AddMarker("x", T0, 1, "local", null));
        book.TagProgramRun(1, T0, "local");
        book.TagLeakTest("leak-20261107-080500", T0);
        Assert.Null(book.End(T0, 1));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void RecoverInterrupted_MarksActiveManifests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        try
        {
            var active = new SessionManifest("A-20261107-080000", "A", null, T0, null, SessionManifest.Active, "local", null, false, 1, null, "A-20261107-080000.csv",
                Array.Empty<SessionMarker>(), Array.Empty<SessionProgramRun>(), Array.Empty<SessionLeakTest>());
            var ended = active with { Id = "B-20261107-080000", State = SessionManifest.Ended, EndedAt = T0.AddHours(1) };
            File.WriteAllText(Path.Combine(dir, "sessions", "A-20261107-080000.json"), active.ToJson());
            File.WriteAllText(Path.Combine(dir, "sessions", "B-20261107-080000.json"), ended.ToJson());
            File.WriteAllText(Path.Combine(dir, "sessions", "bad.json"), "{not json");
            var fixedUp = SessionBook.RecoverInterrupted(dir);
            Assert.Equal(new[] { "A-20261107-080000" }, fixedUp);
            var a = SessionManifest.Load(Path.Combine(dir, "sessions", "A-20261107-080000.json"))!;
            Assert.Equal(SessionManifest.Interrupted, a.State);
            Assert.NotNull(a.EndedAt);
            Assert.Equal(SessionManifest.Ended, SessionManifest.Load(Path.Combine(dir, "sessions", "B-20261107-080000.json"))!.State);
            Assert.Empty(SessionBook.RecoverInterrupted(dir));   // 幂等
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DataLogger_SessionKey_IsFileStem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-dl-" + Guid.NewGuid().ToString("N"));
        var log = new DataLogger(dir);
        try
        {
            Assert.Null(log.SessionKey);
            var path = log.BeginSession("TP 017", new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero));
            Assert.Equal("TP 017", log.SessionId);                      // testId 原样（CSV「会话」列）
            Assert.Equal("TP_017-20261107-080000", log.SessionKey);   // 文件主名：空白 / 非法字符换成下划线
            Assert.Equal(log.SessionKey + ".csv", Path.GetFileName(path));
            log.EndSession();
            Assert.Null(log.SessionKey);
        }
        finally { log.Stop(); try { Directory.Delete(dir, true); } catch { } }
    }
}
