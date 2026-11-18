using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Records;
using FleetActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 一次补拉（融合版 F8 无人值守）：断开那一段 [<see cref="FromUtc"/>, <see cref="ToUtc"/>]（设备时钟 UTC）、进行中、补回来的行数、失败原因。
/// <see cref="Seq"/> 每次补拉加一（断线横条按它对上「这一次重连」）。
/// </summary>
public sealed record BackfillStatus(int Seq, DateTime FromUtc, DateTime ToUtc, bool Pending, int? Rows = null, string? Error = null);

public sealed partial class LiveDeviceSource
{
    /// <summary>最多补拉多长一段（本机曲线缓存只留 240 min，更早的在记录页按会话下载）。</summary>
    public static readonly TimeSpan BackfillMax = TimeSpan.FromMinutes(Core.History.ChannelHistory.KeepMinutes);

    /// <summary>重连后补拉断开那一段（设置页「重连后补拉数据」，默认开；FleetVm 把设置传下来）。</summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>最近一次补拉（没有为 null）。</summary>
    public BackfillStatus? Backfill { get; private set; }

    /// <summary>断线时曲线最后一个读数的时刻（设备时钟 UTC）：重连后从它补拉。</summary>
    private DateTime? _gapFromUtc;
    private int _backfillSeq;

    /// <summary>断线（从在线变成不在线）：记下曲线最后一个读数的时刻。</summary>
    private void NoteGapStart() => _gapFromUtc ??= _history.Data.LastDataAt;

    /// <summary>
    /// 又连上了（SetConn(Online)）：设置开着、断线前有数据时，后台 GET /telemetry?from&amp;to 取回断开那一段（CSV），解析后在 UI 线程上并进曲线缓存
    /// （Core 的 DeviceHistory.Merge：已有的读数优先、这段里的断开标记去掉），断线横条报「已补拉这段的 N 条数据」。
    /// 失败只记原因（横条写「补拉失败（…）」），不重试 —— 记录页按会话下载的数据不受影响。
    /// </summary>
    private void MaybeStartBackfill()
    {
        var from = _gapFromUtc;
        _gapFromUtc = null;
        if (from is not { } f || !BackfillEnabled || Connection is not { } c) return;
        var to = _clock.UtcNow + HistoryClockOffset;
        if (to <= f) return;
        if (to - f > BackfillMax) f = to - BackfillMax;
        var st = new BackfillStatus(++_backfillSeq, f, to, Pending: true);
        Backfill = st;
        _ = RunBackfillAsync(c, st);
    }

    private async Task RunBackfillAsync(IReactorConnection c, BackfillStatus st)
    {
        try
        {
            var (samples, rows) = await Task.Run(async () =>
            {
                using var ms = new MemoryStream();
                await c.Rest.TelemetryRangeAsync(st.FromUtc, st.ToUtc, ms, CancellationToken.None).ConfigureAwait(false);
                ms.Position = 0;
                using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return TelemetryCsvReader.ReadSamples(reader, st.FromUtc, st.ToUtc);
            }).ConfigureAwait(false);
            _post(() =>
            {
                if (_disposed || Backfill?.Seq != st.Seq) return;
                _history.Data.Merge(samples, st.FromUtc, st.ToUtc);
                Backfill = st with { Pending = false, Rows = rows };
                MarkDirty(SourceChange.Frame | SourceChange.Conn);
            });
        }
        catch (Exception ex)
        {
            var why = FleetActions.Describe(ex);
            _post(() =>
            {
                if (_disposed || Backfill?.Seq != st.Seq) return;
                Backfill = st with { Pending = false, Error = why };
                MarkDirty(SourceChange.Conn);
            });
        }
    }
}
