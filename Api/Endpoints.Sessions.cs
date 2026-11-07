using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ParallelReactor.Commands;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>
/// 测试会话端点（设计稿 §10；P4）。会话只是记录层：开始 / 结束不启停任何设备，所以在本地模式下远程也可以（Record 类，免租约，需要 operator）。
/// 会话 id = 会话文件主名 <c>&lt;testId&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>（§14.1 偏差）；manifest 在 <c>sessions/&lt;id&gt;.json</c>。
/// 归属（§10.1）由 OperatorCommands.TryEndSession 判：远程只能结束自己创建的（同一令牌），否则 403 SESSION_NOT_OWNER；已有会话时开新会话 409 SESSION_ACTIVE。
/// </summary>
public static partial class Endpoints
{
    private static readonly Regex SessionId = new(@"^(?!\.)[^/\\\0:*?""<>|\s]{1,200}\z", RegexOptions.CultureInvariant);

    private static void MapSessions(RouteGroupBuilder g, ApiHost host)
    {
        var src = host.Sources;
        var cmd = src.Commands;

        g.MapGet("/sessions", () =>
        {
            var items = HistoryFiles.List(src.DataDir, SafePath.Sessions)
                .Select(f =>
                {
                    var p = HistoryFiles.ParseSessionName(f.Name);
                    return new SessionInfoDto(p?.TestId ?? f.Name, p?.StartedLocal?.ToUniversalTime(), f.Name, f.Bytes, f.From, f.To, Path.GetFileNameWithoutExtension(f.Name));
                })
                .OrderByDescending(s => s.File, StringComparer.Ordinal).ToArray();
            // current 与 /sessions/current 同源（manifest），不取遥测快照（晚一个 Tick）
            var cur = src.CurrentSession;
            return ApiErrors.Ok(new SessionsDto(items.Length, items, new SessionSnap(cur != null, cur?.Id, cur?.TestId, cur?.File)));
        });

        g.MapGet("/sessions/current", () =>
        {
            var m = src.CurrentSession;
            if (m == null) return ApiErrors.Result(OpCodes.NotFound, "当前没有活动会话");
            return ApiErrors.Ok(new SessionDetailDto(true, m.Id, m.TestId, m.File, m, SessionFiles(src.DataDir, m.Id)));
        });

        g.MapGet("/sessions/{id}", (string id) =>
        {
            if (!SessionId.IsMatch(id) || id.Contains("..")) return ApiErrors.Result(OpCodes.NotFound, "会话 id 不合法");
            var cur = src.CurrentSession;
            if (cur != null && cur.Id == id) return ApiErrors.Ok(new SessionDetailDto(true, cur.Id, cur.TestId, cur.File, cur, SessionFiles(src.DataDir, id)));
            var m = SessionManifest.Load(Path.Combine(src.DataDir, "sessions", id + ".json"));
            var files = SessionFiles(src.DataDir, id);
            if (m == null && files.Length == 0) return ApiErrors.Result(OpCodes.NotFound, $"会话 {id} 不存在");
            var p = HistoryFiles.ParseSessionName(id + ".csv");
            return ApiErrors.Ok(new SessionDetailDto(false, id, m?.TestId ?? p?.TestId, m?.File ?? (files.FirstOrDefault(f => f.EndsWith(".csv", StringComparison.Ordinal)) is { } csv ? Path.GetFileName(csv) : null), m, files));
        });

        var w = g.MapGroup("").RequireRateLimiting(ApiHost.WritesPolicy);
        w.MapPost("/sessions", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("session.begin", ActionClass.Record, IdemRequired: true),
            (b, c) => cmd.BeginSessionAsync(c, Str(b, "testId") ?? throw new ApiBadRequestException("缺少 testId"), Note(b, "note") ?? Note(b, "name"), ctx.RequestAborted))));
        w.MapDelete("/sessions/current", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("session.end", ActionClass.Record),
            (b, c) => cmd.EndSessionAsync(c, ctx.RequestAborted))));
        w.MapPost("/sessions/current/markers", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("session.marker", ActionClass.Record, IdemRequired: true),
            (b, c) => cmd.AddMarkerAsync(c, Str(b, "text") ?? Str(b, "label") ?? throw new ApiBadRequestException("缺少 text"), ctx.RequestAborted))));
    }

    /// <summary>该会话的文件（相对数据目录）：sessions/&lt;id&gt;.csv、sessions/&lt;id&gt;-1.csv…、sessions/&lt;id&gt;.json。</summary>
    private static string[] SessionFiles(string dataDir, string id)
    {
        var dir = Path.Combine(dataDir, "sessions");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir).Select(Path.GetFileName)
            .Where(n => n != null && (n == id + ".json" || n == id + ".csv" || Regex.IsMatch(n, "^" + Regex.Escape(id) + @"-\d{1,3}\.csv\z")))
            .Select(n => "sessions/" + n).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}
