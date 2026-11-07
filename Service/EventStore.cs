using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ParallelReactor.Services;

/// <summary>
/// 一条带全局序号的事件（设计稿 §6.4）。时间为 UTC；<c>Origin</c> 是 P0 事件原有的中文「来源」列（RV3 / 系统 / 通讯 / 阀门 / 设置…），
/// <c>Source</c> 是 §6.4 的来源分类（P2 全部为 system，P3 写命令接入后才有 local / remote）。
/// <c>Kind</c> 由 <see cref="EventKinds.Derive"/> 按来源与标题推导（P2 过渡方案，见设计稿 §6.4 说明）。
/// </summary>
public sealed record EventRecord(
    long Seq,
    int SeqEpoch,
    DateTime Ts,
    string TsLocal,
    string Level,
    string Kind,
    int? Channel,
    string Source,
    string Origin,
    string? Client,
    string? OpId,
    string Title,
    string Message);

/// <summary>GET /alarms 与 WS alarm 消息的一条（由界面报警列表派生，见 ViewModels/AlarmViewModel）。</summary>
public sealed record AlarmDto(string Id, string Level, string Rv, int? Channel, string Title, string Message, DateTime RaisedAt, string State, DateTime? ClearedAt);

/// <summary>重同步结果：命中环形缓冲时 Items 为缺的事件；Gap 为 true 表示 afterSeq 已不在缓冲区内（需走 GET /events）。</summary>
public sealed record ResyncResult(IReadOnlyList<EventRecord> Items, bool Gap, long OldestBuffered, long Head);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EventRecord))]
internal partial class EventJsonContext : JsonSerializerContext
{
}

/// <summary>§6.4 kind 的推导表（P2：现有事件产生点尚未显式带 kind，按来源与标题归类；P3 改为生产者显式给出）。纯逻辑。</summary>
public static class EventKinds
{
    private static readonly Regex RvSource = new(@"^RV([1-8])$", RegexOptions.CultureInvariant);

    /// <summary>来源「RVn」→ 通道号；其他为 null。</summary>
    public static int? ChannelOf(string? origin)
        => origin != null && RvSource.Match(origin) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    public static string Derive(string? origin, string? title)
    {
        origin ??= "";
        title ??= "";
        bool has(string s) => title.Contains(s, StringComparison.Ordinal);

        if (origin == "接口")
        {
            if (has("鉴权")) return "api.authFailed";
            if (has("绑定失败")) return "system.apiBindFailed";
            if (has("已启动")) return "system.apiStarted";
            if (has("已停止")) return "system.apiStopped";
            if (has("已连接")) return "system.clientConnected";
            if (has("已断开")) return "system.clientDisconnected";
            return "api.generic";
        }
        if (origin == "阀门") return "valve.frame";
        if (origin == "设置") return "system.settingsChanged";
        if (origin == "通讯") return has("恢复") ? "alarm.commRestored" : "alarm.commLost";

        if (has("超温"))
        {
            if (has("解除")) return "alarm.overTempCleared";
            if (has("已确认")) return "alarm.overTempStopConfirmed";
            if (has("重新打开")) return "alarm.overTempReopened";
            if (has("未确认")) return "alarm.overTempStopUnconfirmed";
            return "alarm.overTemp";
        }
        if (has("超压"))
        {
            if (has("解除")) return "alarm.overPressureCleared";
            return "alarm.overPressure";
        }
        if (has("急停"))
        {
            if (has("开机")) return "estop.restoredOnBoot";
            if (has("复位")) return "estop.reset";
            if (has("锁存")) return "estop.latch";
            if (has("已确认") || has("未确认")) return "estop.part";
            return "estop.triggered";
        }
        if (has("测试会话开始")) return "session.begin";
        if (has("测试会话结束")) return "session.end";
        if (title == "标记") return "session.marker";
        if (has("泄漏测试")) return has("开始") ? "leak.started" : "leak.finished";
        if (has("程序启动")) return "system.boot";
        if (has("程序退出")) return "system.shutdown";
        if (has("模拟模式")) return "system.simulated";
        if (has("程序"))
        {
            if (has("中止")) return "program.aborted";
            if (has("完成")) return "program.finished";
            if (has("失败") || has("拒绝")) return "program.fault";
            if (has("开始") || has("启动")) return "program.started";
            return "program.generic";
        }
        if (has("自整定")) return has("取消") ? "autotune.cancelled" : has("完成") ? "autotune.finished" : "autotune.started";
        if (has("启动加热失败")) return "alarm.heatStartFailed";
        if (has("停止加热未确认")) return "alarm.heatStopFailed";
        if (has("写盘失败")) return "alarm.loggerWriteFailed";
        if (has("关闭失败") || has("写入失败")) return "alarm.valveWriteFailed";
        if (has("控制循环超时")) return "system.tickOverrun";
        return "event.generic";
    }
}

/// <summary>
/// 事件存储（设计稿 §2.2 第 5 条 / §9.1 K1、K2）：所有经 <see cref="DataLogger.Event"/> 的事件在这里分配全局单调序号 <c>seq</c>，
/// 追加写到 <c>events/events-YYYYMMDD.jsonl</c>（现有 CSV 继续由 DataLogger 写），并保留最近 <see cref="RingSize"/> 条在内存环形缓冲，
/// 供 WebSocket 断线重同步。<c>seq</c> 跨重启连续：每次 flush 写 <c>events/.seq</c>，开机取 <c>.seq</c> 与最新 JSONL 尾部能解析出的最大 seq 的较大者 + 1
/// （文件损坏时按能解析的行里的最大值接续）。<c>seqEpoch</c> 在 P2 固定为 1。
/// <para>线程：<see cref="Append"/> 可在任意线程调用（UI 线程与 Kestrel 线程并发）。分配 seq、入环、入写盘队列、触发 <see cref="Appended"/>
/// 四步在同一把锁内完成，所以 JSONL 行序、环内容与推送顺序都严格按 seq 递增（订阅者只能做非阻塞入队，锁内回调不等待任何 I/O）；
/// 写盘由单个后台任务完成，失败丢弃并计数，不抛给调用方。</para>
/// 纯逻辑（无 UI 依赖），测试工程直接链接。
/// </summary>
public sealed class EventStore : IDisposable
{
    public const int RingSize = 5000;
    public const int SeqEpochValue = 1;
    public const int QueueCapacity = 50000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>JSONL 与 .seq 所在目录（数据目录/events）。</summary>
    public string Dir { get; }
    public string SeqPath => Path.Combine(Dir, ".seq");

    private readonly object _lock = new();
    private readonly EventRecord?[] _ring = new EventRecord?[RingSize];
    private long _head;        // 已分配的最大 seq（0 = 尚无）
    private long _oldest;      // 环里最旧一条的 seq（0 = 空）
    private long _dropped;

    private readonly Channel<(EventRecord Rec, string Day)> _q = Channel.CreateBounded<(EventRecord, string)>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _loop;
    private readonly Action<string>? _onError;

    private AlarmDto[] _alarms = Array.Empty<AlarmDto>();

    public EventStore(string dataDir, Action<string>? onError = null)
    {
        Dir = Path.Combine(dataDir, "events");
        _onError = onError;
        _head = RecoverLastSeq(Dir);
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>已分配的最大 seq。</summary>
    public long Head => Volatile.Read(ref _head);

    public int SeqEpoch => SeqEpochValue;

    /// <summary>环形缓冲里最旧一条的 seq（空为 0）。</summary>
    public long OldestBuffered { get { lock (_lock) return _oldest; } }

    /// <summary>因队列满 / 写盘失败丢弃的 JSONL 行数。</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>每追加一条触发（在调用方线程上、<b>存储锁内</b>，严格按 seq 递增）。订阅者只能做非阻塞入队，不得阻塞、不得回调本存储。</summary>
    public event Action<EventRecord>? Appended;

    /// <summary>追加一条事件：锁内分配 seq → 入环 → 入写盘队列 → 触发 <see cref="Appended"/>（kind / channel 推导在锁外）。</summary>
    public EventRecord Append(DateTimeOffset ts, string level, string origin, string title, string message,
        string source = "system", string? client = null, string? opId = null)
    {
        string kind = EventKinds.Derive(origin, title);
        int? channel = EventKinds.ChannelOf(origin);
        string tsLocal = ts.ToString(Csv.TimeFormat, CultureInfo.InvariantCulture);
        string day = ts.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        EventRecord rec;
        lock (_lock)
        {
            long seq = ++_head;
            rec = new EventRecord(seq, SeqEpochValue, ts.UtcDateTime, tsLocal, level ?? "info", kind, channel, source, origin ?? "", client, opId, title ?? "", message ?? "");
            _ring[seq % RingSize] = rec;
            if (_oldest == 0) _oldest = seq;                               // 本次运行写入的第一条（重启后环是空的）
            else if (seq - _oldest >= RingSize) _oldest = seq - RingSize + 1;
            if (!_q.Writer.TryWrite((rec, day))) Interlocked.Increment(ref _dropped);
            try { Appended?.Invoke(rec); } catch { /* 订阅者异常不影响记录 */ }
        }
        return rec;
    }

    /// <summary>
    /// 重同步：返回 seq &gt; afterSeq 的最多 limit 条（按序）。afterSeq 早于环里最旧一条（且环已满或 afterSeq+1 不在环里）时 Gap = true。
    /// afterSeq ≥ Head 时返回空列表（无缺口）。
    /// </summary>
    public ResyncResult Resync(long afterSeq, int limit = 1000)
    {
        lock (_lock)
        {
            long head = _head;
            if (afterSeq >= head || head == 0) return new ResyncResult(Array.Empty<EventRecord>(), false, _oldest, head);
            if (_oldest == 0 || afterSeq + 1 < _oldest) return new ResyncResult(Array.Empty<EventRecord>(), true, _oldest, head);
            var list = new List<EventRecord>();
            for (long s = afterSeq + 1; s <= head && list.Count < limit; s++)
            {
                var r = _ring[s % RingSize];
                if (r == null || r.Seq != s) return new ResyncResult(list, true, _oldest, head);
                list.Add(r);
            }
            return new ResyncResult(list, false, _oldest, head);
        }
    }

    /// <summary>GET /events：先查环，环里没有（太旧）再扫落盘的 JSONL。</summary>
    public IReadOnlyList<EventRecord> ReadAfter(long afterSeq, int limit = 500)
    {
        var r = Resync(afterSeq, limit);
        if (!r.Gap) return r.Items;
        return ReadFiles(Dir, afterSeq, limit);
    }

    // ===================== 报警视图（由 AlarmViewModel 维护，接口只读）=====================

    /// <summary>当前界面报警列表的只读副本（本次运行内）。</summary>
    public AlarmDto[] Alarms => Volatile.Read(ref _alarms);

    /// <summary>每条报警产生 / 解除 / 确认时触发（在调用方线程上）。</summary>
    public event Action<AlarmDto>? AlarmChanged;

    /// <summary>替换报警列表副本，并通知变化的那条。</summary>
    public void PublishAlarms(AlarmDto[] all, AlarmDto? changed)
    {
        Volatile.Write(ref _alarms, all);
        if (changed != null) AlarmChanged?.Invoke(changed);
    }

    // ===================== 落盘 / 恢复 =====================

    /// <summary>开机恢复：max(.seq, 最新 JSONL 里能解析出的最大 seq)；都没有为 0。</summary>
    public static long RecoverLastSeq(string eventsDir)
    {
        long fromSeqFile = 0, fromTail = 0;
        try
        {
            var p = Path.Combine(eventsDir, ".seq");
            if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
                fromSeqFile = v;
        }
        catch { }
        try
        {
            var newest = NewestJsonl(eventsDir);
            if (newest != null) fromTail = MaxSeqIn(newest);
        }
        catch { }
        return Math.Max(fromSeqFile, fromTail);
    }

    private static string? NewestJsonl(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string? best = null;
        foreach (var f in Directory.EnumerateFiles(dir, "events-*.jsonl"))
            if (best == null || string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(best)) > 0) best = f;
        return best;
    }

    /// <summary>扫一个 JSONL 文件里能解析出来的最大 seq（损坏行 / 半行跳过）。</summary>
    public static long MaxSeqIn(string path)
    {
        long max = 0;
        foreach (var line in ReadLinesShared(path))
        {
            var rec = TryParse(line);
            if (rec != null && rec.Seq > max) max = rec.Seq;
        }
        return max;
    }

    /// <summary>
    /// 从落盘 JSONL 读 seq &gt; afterSeq 的最多 limit 条（按文件名升序、行序）。
    /// seq 随时间单调，文件按日滚动，所以文件顺序即 seq 顺序：先看每个文件首行的 seq，整体不超过 afterSeq 的旧文件直接跳过
    /// （只读首行，不逐行扫），从最后一个「首行 seq ≤ afterSeq + 1」的文件开始读。
    /// </summary>
    public static List<EventRecord> ReadFiles(string eventsDir, long afterSeq, int limit)
    {
        var list = new List<EventRecord>();
        if (!Directory.Exists(eventsDir)) return list;
        var files = new List<string>(Directory.EnumerateFiles(eventsDir, "events-*.jsonl"));
        files.Sort(string.CompareOrdinal);
        int start = 0;
        for (int i = files.Count - 1; i > 0; i--)
        {
            long first = FirstSeqIn(files[i]);
            if (first > 0 && first <= afterSeq + 1) { start = i; break; }
        }
        foreach (var f in files.Skip(start))
        {
            foreach (var line in ReadLinesShared(f))
            {
                var rec = TryParse(line);
                if (rec == null || rec.Seq <= afterSeq) continue;
                list.Add(rec);
                if (list.Count >= limit) { list.Sort((a, b) => a.Seq.CompareTo(b.Seq)); return list; }
            }
        }
        list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return list;
    }

    /// <summary>文件里第一条能解析出来的 seq（没有为 0）。只读开头几行。</summary>
    public static long FirstSeqIn(string path)
    {
        try
        {
            int n = 0;
            foreach (var line in ReadLinesShared(path))
            {
                var rec = TryParse(line);
                if (rec != null) return rec.Seq;
                if (++n >= 20) break;   // 开头全是坏行：当作未知，不跳过该文件
            }
        }
        catch { }
        return 0;
    }

    public static EventRecord? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize(line, EventJsonContext.Default.EventRecord); }
        catch { return null; }
    }

    /// <summary>JSONL 行的序列化选项：源生成 + 中文不转义（文件直接可读）。</summary>
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        TypeInfoResolver = EventJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(EventRecord rec) => JsonSerializer.Serialize(rec, typeof(EventRecord), JsonlOptions);

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = r.ReadLine()) != null) yield return line;
    }

    // ===================== 后台写盘 =====================

    private StreamWriter? _w;
    private string? _wDay;
    private DateTime _retryAt;
    private bool _dirty;
    private bool _reported;
    private long _flushedHead;

    private async Task LoopAsync()
    {
        try
        {
            Task<bool>? wait = null;   // 同一时刻只保留一个等待（复用到下一轮）
            while (true)
            {
                while (_q.Reader.TryRead(out var job)) Write(job.Rec, job.Day);
                wait ??= _q.Reader.WaitToReadAsync().AsTask();
                if (_dirty)
                {
                    // 空闲 FlushInterval 后 flush；期间来了新内容则先写
                    if (await Task.WhenAny(wait, Task.Delay(FlushInterval)).ConfigureAwait(false) != wait) { Flush(); continue; }
                }
                bool more = await wait.ConfigureAwait(false);
                wait = null;
                if (!more) break;
            }
        }
        catch (Exception ex) { Report($"事件存储后台任务异常退出：{ex.Message}"); }
        finally
        {
            while (_q.Reader.TryRead(out var job)) Write(job.Rec, job.Day);
            Flush();
            try { _w?.Dispose(); } catch { }
            _w = null;
        }
    }

    private void Write(EventRecord rec, string day)
    {
        try
        {
            if (_w == null || _wDay != day)
            {
                try { _w?.Dispose(); } catch { }
                _w = null;
                if (DateTime.UtcNow < _retryAt) { Interlocked.Increment(ref _dropped); return; }
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, $"events-{day}.jsonl");
                bool partial = File.Exists(path) && new FileInfo(path).Length > 0 && !EndsWithNewline(path);
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
                if (partial) _w.WriteLine();   // 上次异常退出留下的半行先收尾（该行本身仍是坏行，读取时跳过）
                _wDay = day;
            }
            _w.WriteLine(ToJson(rec));
            _dirty = true;
            _reported = false;
        }
        catch (Exception ex)
        {
            try { _w?.Dispose(); } catch { }
            _w = null;
            _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            Interlocked.Increment(ref _dropped);
            Report($"写入事件 JSONL 失败：{ex.Message}");
        }
    }

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        try { _w?.Flush(); }
        catch (Exception ex)
        {
            try { _w?.Dispose(); } catch { }
            _w = null;
            _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            Report($"事件 JSONL flush 失败：{ex.Message}");
            return;
        }
        long head = Head;
        if (head == _flushedHead) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SeqPath, head.ToString(CultureInfo.InvariantCulture));
            _flushedHead = head;
        }
        catch (Exception ex) { Report($"写入 events/.seq 失败：{ex.Message}"); }
    }

    private static bool EndsWithNewline(string p)
    {
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }

    private void Report(string msg)
    {
        if (_reported) return;
        _reported = true;
        try { _onError?.Invoke(msg); } catch { }
    }

    /// <summary>停止：写完队列并 flush（最多等 timeout）。</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _q.Writer.TryComplete();
        await Task.WhenAny(_loop, Task.Delay(timeout ?? TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    /// <summary>同步停止（进程退出路径）。</summary>
    public void Stop(TimeSpan? timeout = null)
    {
        _q.Writer.TryComplete();
        try { _loop.Wait(timeout ?? TimeSpan.FromSeconds(2)); } catch { }
    }

    public void Dispose() => Stop();
}
