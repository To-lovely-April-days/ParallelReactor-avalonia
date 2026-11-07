using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>
/// /api/v1 下的端点。本文件：只读端点（设计稿 §7 GET 部分、§9 历史），每个处理器只读快照 / 事件存储 / 文件，不进 UI 线程
/// （例外 /preflight，经 <see cref="IApiSources.PreflightAsync"/> 投递到 UI 线程并带 5 s 超时）。
/// P3 / P4：控制权（Endpoints.Control.cs）、写命令（Endpoints.Write.cs）、会话（Endpoints.Sessions.cs），都在 <see cref="MapFallback"/> 之前登记。
/// </summary>
public static partial class Endpoints
{
    public const int EventsDefaultLimit = 500, EventsMaxLimit = 1000;

    private static SnapshotBundle? Snap(IApiSources src) => src.Snapshots.Current;
    private static IResult Starting() => ApiErrors.Result(OpCodes.Starting, "尚未产生第一个控制周期的快照，请稍后重试");

    public static void Map(RouteGroupBuilder g, ApiHost host)
    {
        var src = host.Sources;
        var cfg = host.Config;

        SnapshotBundle? Snap() => Endpoints.Snap(src);

        // —— §7.2 系统 ——
        g.MapGet("/health", () => ApiErrors.Ok(new HealthDto(true, ApiVersionInfo.ApiVersion, src.Simulated, src.BootId, DateTime.UtcNow)))
            .DisableRateLimiting();

        g.MapGet("/device", () => ApiErrors.Ok(src.Device(cfg)));

        g.MapGet("/snapshot", () => Snap() is { } b ? ApiErrors.Ok(b.Frame) : Starting());

        g.MapGet("/preflight", async (HttpContext ctx) =>
        {
            var auth = ApiHost.AuthOf(ctx);
            try { return ApiErrors.Ok(await src.PreflightAsync(auth.Name, ctx.RequestAborted).ConfigureAwait(false)); }
            catch (UiThreadTimeoutException ex) { return ApiErrors.Result(OpCodes.UiThreadTimeout, ex.Message); }
            catch (OperationCanceledException) { return ApiErrors.Result(OpCodes.UiThreadTimeout, "请求已取消"); }
            catch (Exception ex) { return ApiErrors.Result(OpCodes.Internal, "预运行检查失败：" + ex.GetBaseException().Message); }   // 不让框架回空 500
        }).RequireRateLimiting(ApiHost.PreflightPolicy);

        g.MapGet("/device/comm", () =>
        {
            var b = Snap();
            var ev = src.Events;
            return ApiErrors.Ok(new CommDto(DateTime.UtcNow, b?.Frame.Seq ?? 0, b?.TempComm, b?.PressureComm, b?.StirComm, b?.IoComm, b?.Tick,
                host.Stats.ToDto(cfg.MaxClients),
                new LoggerStatsDto(b?.LoggerDroppedLines ?? 0, ev.DroppedLines, ev.Head, ev.OldestBuffered)));
        });

        // —— §7.4 通道 ——
        g.MapGet("/channels", () => Snap() is { } b
            ? ApiErrors.Ok(new ChannelsDto(b.Frame.Seq, b.Frame.Ts, b.FullChannels.Where(c => c.Installed).Select(c => c.Id).ToArray(), b.Frame.Channels))
            : Starting());

        g.MapGet("/channels/{id}", (string id) =>
        {
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > ChannelRange.ChannelCount)
                return ApiErrors.Result(OpCodes.ChannelNotFound, $"通道 {id} 不存在（有效范围 1–{ChannelRange.ChannelCount}）");
            var b = Snap();
            if (b == null) return Starting();
            var c = b.Channel(n);
            return c == null ? ApiErrors.Result(OpCodes.ChannelNotFound, $"通道 {id} 不存在") : ApiErrors.Ok(new ChannelDetailDto(b.Frame.Seq, b.Frame.Ts, c));
        });

        // —— §7.10 事件 / 报警 ——
        g.MapGet("/events", (long? afterSeq, int? limit) =>
        {
            long after = Math.Max(0, afterSeq ?? 0);
            int lim = Math.Clamp(limit ?? EventsDefaultLimit, 1, EventsMaxLimit);
            var ev = src.Events;
            var items = ev.ReadAfter(after, lim + 1);
            bool more = items.Count > lim;
            if (more) items = items.Take(lim).ToList();
            return ApiErrors.Ok(new EventsPageDto(ev.SeqEpoch, ev.Head, after, items.Count, more, items));
        });

        g.MapGet("/alarms", (string? state) =>
        {
            var all = src.Events.Alarms;
            var items = string.IsNullOrEmpty(state) || state == "all" ? all : all.Where(a => a.State == state).ToArray();
            return ApiErrors.Ok(new AlarmsDto(items.Length, items));
        });

        // —— §7.11 设置（只读）/ §6.9 程序 schema ——
        g.MapGet("/settings", () => Snap() is { } b ? ApiErrors.Ok(SettingsDto.From(b.Settings)) : Starting());
        g.MapGet("/program-schema", () => ApiErrors.Ok(src.ProgramSchema()));

        // —— §9 历史与文件 ——
        g.MapGet("/files", (string? type) =>
        {
            if (string.IsNullOrEmpty(type) || !SafePath.Types.Contains(type))
                return ApiErrors.Result(OpCodes.BadRequest, "type 必须是 telemetry / events / sessions / leak 之一",
                    new() { ["types"] = string.Join(",", SafePath.Types) });
            var items = HistoryFiles.List(src.DataDir, type);
            return ApiErrors.Ok(new FilesDto(type, items.Count, items));
        });

        g.MapGet("/files/{type}/{name}", (string type, string name) => FileResult(src.DataDir, type, name))
            .RequireRateLimiting(ApiHost.DownloadsPolicy);

        g.MapGet("/telemetry", (HttpContext ctx, string? from, string? to) =>
        {
            if (!HistoryFiles.TryParseQueryTime(from, out var f) || !HistoryFiles.TryParseQueryTime(to, out var t))
                return ApiErrors.Result(OpCodes.BadRequest, "from / to 必须是带时区偏移或 Z 的 ISO-8601 时间，例如 2026-11-06T08:00:00Z");
            if (t <= f) return ApiErrors.Result(OpCodes.BadRequest, "to 必须晚于 from（半开区间 [from, to)）");
            var files = HistoryFiles.SelectDailyFiles(src.DataDir, f, t);
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Stream(stream => HistoryFiles.WriteRangeAsync(files, f, t, stream, ctx.RequestAborted),
                "text/csv; charset=utf-8", $"telemetry-{f.UtcDateTime:yyyyMMddTHHmmss}Z-{t.UtcDateTime:yyyyMMddTHHmmss}Z.csv");
        }).RequireRateLimiting(ApiHost.DownloadsPolicy);

        g.MapGet("/leak-tests", () =>
        {
            var dir = Path.Combine(src.DataDir, "leak");
            var items = new List<LeakTestInfoDto>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "leak-*-summary.json"))
                {
                    var name = Path.GetFileName(f);
                    var runId = name[..^"-summary.json".Length];
                    if (!SafePath.LeakRunId.IsMatch(runId)) continue;
                    DateTime? start = DateTime.TryParseExact(runId[5..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d.ToUniversalTime() : null;
                    long bytes = 0;
                    try { bytes = new FileInfo(f).Length; } catch { }
                    items.Add(new LeakTestInfoDto(runId, start, bytes, LeakFiles(dir, runId)));
                }
            }
            items.Sort((a, b) => string.CompareOrdinal(b.RunId, a.RunId));
            return ApiErrors.Ok(new LeakTestsDto(items.Count, items.ToArray()));
        });

        g.MapGet("/leak-tests/{runId}", (string runId) => SafePath.LeakRunId.IsMatch(runId)
            ? FileResult(src.DataDir, SafePath.Leak, runId + "-summary.json")
            : ApiErrors.Result(OpCodes.NotFound, "runId 格式应为 leak-yyyyMMdd-HHmmss"));

        g.MapGet("/leak-tests/{runId}/data.csv", (string runId) => SafePath.LeakRunId.IsMatch(runId)
            ? FileResult(src.DataDir, SafePath.Leak, runId + ".csv")
            : ApiErrors.Result(OpCodes.NotFound, "runId 格式应为 leak-yyyyMMdd-HHmmss")).RequireRateLimiting(ApiHost.DownloadsPolicy);

        g.MapGet("/leak-tests/{runId}/summary.csv", (string runId) => SafePath.LeakRunId.IsMatch(runId)
            ? FileResult(src.DataDir, SafePath.Leak, runId + "-summary.csv")
            : ApiErrors.Result(OpCodes.NotFound, "runId 格式应为 leak-yyyyMMdd-HHmmss")).RequireRateLimiting(ApiHost.DownloadsPolicy);

        // —— §10 会话（GET 列表 / 当前 / 详情 + POST / DELETE，Endpoints.Sessions.cs）——
        MapSessions(g, host);

        // —— §7.2 控制权（P3，Endpoints.Control.cs）——
        MapControl(g, host);

        // —— §7 写命令（P3，Endpoints.Write.cs）——
        MapWrites(g, host);

        // —— §8 WebSocket ——
        g.Map("/ws", host.Hub.HandleAsync);

        // 组内其余路径：统一 404 JSON（而不是空 404）
        g.MapFallback(() => ApiErrors.Result(OpCodes.NotFound, "没有这个端点"));
    }

    private static LeakFilesDto LeakFiles(string dir, string runId) => new(
        File.Exists(Path.Combine(dir, runId + ".csv")) ? $"leak/{runId}.csv" : null,
        File.Exists(Path.Combine(dir, runId + "-summary.csv")) ? $"leak/{runId}-summary.csv" : null,
        File.Exists(Path.Combine(dir, runId + "-summary.json")) ? $"leak/{runId}-summary.json" : null);

    /// <summary>按类型 + 文件名下载（路径安全见 SafePath）；支持 Range；ETag = 长度 + 修改时间。</summary>
    public static IResult FileResult(string dataDir, string type, string name)
    {
        var path = SafePath.TryResolve(dataDir, type, name);
        if (path == null || !File.Exists(path)) return ApiErrors.Result(OpCodes.NotFound, "文件不存在或名称不合法");
        var fi = new FileInfo(path);
        string ct = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json; charset=utf-8"
            : name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? "application/x-ndjson; charset=utf-8"
            : "text/csv; charset=utf-8";
        var etag = new EntityTagHeaderValue($"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\"", isWeak: true);
        return Results.File(path, ct, fileDownloadName: name, enableRangeProcessing: true, lastModified: fi.LastWriteTimeUtc, entityTag: etag);
    }
}
