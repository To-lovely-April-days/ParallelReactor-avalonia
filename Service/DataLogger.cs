using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ParallelReactor.Services;

/// <summary>
/// 统一数据记录（运行记录 + 事件日志 + 测试会话 + 任意结果文件），全部落到数据目录：
/// <list type="bullet">
/// <item><c>telemetry-YYYYMMDD.csv</c>：每个实时 Tick 一行（列定义见 <see cref="TelemetryCsv"/>），按行时间戳的本地日期滚动；</item>
/// <item><c>events-YYYYMMDD.csv</c>：报警与事件（时间、级别、来源、标题、详情）；</item>
/// <item><c>sessions/&lt;testId&gt;-&lt;yyyyMMdd-HHmmss&gt;.csv</c>：<see cref="BeginSession"/> 期间额外写一份同列的运行记录；</item>
/// <item><c>leak/…</c> 等：<see cref="WriteFile"/> 整文件写入（泄漏测试逐点数据 / 汇总）。</item>
/// </list>
/// 文件均为 UTF-8 带 BOM（Excel 直接打开中文表头不乱码），表头每个文件只写一次；
/// 同名文件已存在但表头不同（升级后列变化）时另起 <c>-1</c>、<c>-2</c>… 新文件，不把两种列混在一个文件里。
/// <para>
/// 线程：调用方（UI 线程）只负责把行格式化成字符串并入队；单个后台任务写盘，至少每 10 秒 flush 一次，
/// 停止时（<see cref="StopAsync"/> / <see cref="Stop"/>）写完队列再 flush 关闭。
/// 写盘失败（磁盘满、无权限、目录不存在且建不了）不抛给调用方：丢弃该行、30 秒后重试打开，
/// 并通过 <c>onError</c> 回调按文件上报一次（同一文件恢复写入后再失败才会再次上报）。
/// 队列有上限（<see cref="QueueCapacity"/>）：磁盘卡死时新内容直接丢弃并计数（<see cref="DroppedLines"/>），内存不会无限增长；
/// 后台任务意外退出时停止接收并上报。纯逻辑（无 UI 依赖）。
/// </para>
/// </summary>
public sealed class DataLogger
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryOpenAfter = TimeSpan.FromSeconds(30);

    /// <summary>数据目录：环境变量 PR_DATA_DIR，默认 ~/ParallelReactor-data。</summary>
    public static string DefaultDir => DefaultDirFor(sim: false);

    /// <summary>数据目录：PR_DATA_DIR 优先；未设置时真机为 ~/ParallelReactor-data，模拟模式为 ~/ParallelReactor-data-sim（模拟数据不混入真实记录）。</summary>
    public static string DefaultDirFor(bool sim)
    {
        var v = Environment.GetEnvironmentVariable("PR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            sim ? "ParallelReactor-data-sim" : "ParallelReactor-data");
    }

    public string Dir { get; }

    /// <summary>队列上限（约 1.2 s 一行运行记录 + 事件，够积压数小时）。满了丢弃新内容，不阻塞调用方。</summary>
    public const int QueueCapacity = 20000;

    private readonly Channel<Job> _q = Channel.CreateBounded<Job>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,   // 只用 TryWrite：满时返回 false（计入丢弃），从不等待
        SingleReader = true,
    });
    private readonly Task _loop;
    private readonly Action<string>? _onError;

    // —— 调用方线程状态（会话 / 标记）——
    private readonly object _lock = new();
    private string? _marker;
    private string? _session;
    private string? _sessionPath;

    /// <summary>当前测试会话 id（未开始为 null）。</summary>
    public string? SessionId { get { lock (_lock) return _session; } }

    /// <summary>当前会话文件的完整路径（未开始为 null）。</summary>
    public string? SessionPath { get { lock (_lock) return _sessionPath; } }

    /// <summary>因写盘失败被丢弃的行数（累计）。</summary>
    public long DroppedLines => System.Threading.Interlocked.Read(ref _dropped);
    private long _dropped;

    /// <summary>事件存储（带 seq 的 JSONL + 内存环，供对外接口；null = 只写 CSV）。</summary>
    public EventStore? Events { get; }

    /// <param name="dir">数据目录（不存在时自动创建）。</param>
    /// <param name="onError">写盘失败时回调（在后台线程调用；调用方自行切回 UI 线程）。</param>
    /// <param name="events">事件存储：每条 <see cref="Event"/> 同时写入它（分配 seq、JSONL、环形缓冲）。</param>
    public DataLogger(string dir, Action<string>? onError = null, EventStore? events = null)
    {
        Dir = dir;
        _onError = onError;
        Events = events;
        _loop = Task.Run(LoopAsync);
    }

    // ===================== 调用方 API =====================

    /// <summary>入队一行运行记录：自动填入待写标记（消费掉）与当前会话 id。</summary>
    public void Telemetry(TelemetryRow row)
    {
        string? sessionPath;
        lock (_lock)
        {
            if (_marker != null)
            {
                row.Marker = string.IsNullOrEmpty(row.Marker) ? _marker : row.Marker + " | " + _marker;
                _marker = null;
            }
            row.Session ??= _session;
            sessionPath = _sessionPath;
        }
        Enqueue(new RowJob(row.Time, TelemetryCsv.Format(row), sessionPath));
    }

    /// <summary>入队一条事件（CSV）；配置了 <see cref="Events"/> 时同时写入事件存储（seq / JSONL / 环）。任意线程可调。</summary>
    public void Event(DateTimeOffset t, string level, string source, string title, string detail)
    {
        try { Events?.Append(t, level, source, title, detail); }
        catch { /* 事件存储异常不影响 CSV 与调用方 */ }
        Enqueue(new EventJob(t, TelemetryCsv.FormatEvent(t, level, source, title, detail)));
    }

    /// <summary>
    /// 开始测试会话：之后的运行记录同时写入 <c>sessions/&lt;testId&gt;-&lt;yyyyMMdd-HHmmss&gt;.csv</c>。
    /// 已有会话时先结束它。返回会话文件完整路径。
    /// </summary>
    public string BeginSession(string testId, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        string id = string.IsNullOrWhiteSpace(testId) ? "test" : testId.Trim();
        string path = Path.Combine(Dir, "sessions", $"{SafeName(id)}-{t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");
        lock (_lock)
        {
            _session = id;
            _sessionPath = path;
        }
        return path;
    }

    /// <summary>
    /// 结束测试会话（会话文件 flush 并关闭）。无会话时不做任何事。
    /// finalRow 不为 null 时作为会话文件的最后一行写入（只写会话文件，不写日运行记录），
    /// 其标记列为 endMarker——否则结束标记只能落到下一行日运行记录里，会话文件里没有。
    /// </summary>
    public void EndSession(TelemetryRow? finalRow = null, string? endMarker = null)
    {
        string? id, path;
        lock (_lock)
        {
            if (_sessionPath == null) return;
            id = _session;
            path = _sessionPath;
            _session = null;
            _sessionPath = null;
        }
        if (finalRow != null)
        {
            finalRow.Session = id;
            if (!string.IsNullOrEmpty(endMarker))
                finalRow.Marker = string.IsNullOrEmpty(finalRow.Marker) ? endMarker : finalRow.Marker + " | " + endMarker;
            Enqueue(new SessionRowJob(TelemetryCsv.Format(finalRow), path!));
        }
        Enqueue(new SessionCloseJob());
    }

    /// <summary>添加标记：出现在下一行运行记录的「标记」列（多个标记以 | 连接）。</summary>
    public void AddMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock) _marker = _marker == null ? text : _marker + " | " + text;
    }

    /// <summary>整文件写入（UTF-8 带 BOM，覆盖同名文件）。relPath 相对数据目录。</summary>
    public void WriteFile(string relPath, string content)
        => Enqueue(new FileJob(Path.Combine(Dir, relPath), content));

    /// <summary>等待此前入队的内容全部写盘并 flush。</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_q.Writer.TryWrite(new FlushJob(tcs))) tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>停止：不再接收新内容，写完队列、flush 并关闭全部文件（最多等 timeout）。</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _q.Writer.TryComplete();
        var t = timeout ?? TimeSpan.FromSeconds(3);
        var ev = Events?.StopAsync(t) ?? Task.CompletedTask;
        await Task.WhenAny(Task.WhenAll(_loop, ev), Task.Delay(t)).ConfigureAwait(false);
    }

    /// <summary>同步停止（进程退出事件里用，最多阻塞 timeout）。</summary>
    public void Stop(TimeSpan? timeout = null)
    {
        _q.Writer.TryComplete();
        try { _loop.Wait(timeout ?? TimeSpan.FromSeconds(2)); } catch { }
        try { Events?.Stop(timeout ?? TimeSpan.FromSeconds(2)); } catch { }
    }

    private void Enqueue(Job j)
    {
        if (!_q.Writer.TryWrite(j)) System.Threading.Interlocked.Increment(ref _dropped);   // 已停止
    }

    /// <summary>文件名安全化：非法字符与空白替换为下划线。</summary>
    public static string SafeName(string s)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(bad.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch);
        return sb.ToString();
    }

    // ===================== 后台写盘 =====================

    private abstract record Job;
    private sealed record RowJob(DateTimeOffset Time, string Line, string? SessionPath) : Job;
    private sealed record SessionRowJob(string Line, string SessionPath) : Job;
    private sealed record EventJob(DateTimeOffset Time, string Line) : Job;
    private sealed record FileJob(string Path, string Content) : Job;
    private sealed record SessionCloseJob : Job;
    private sealed record FlushJob(TaskCompletionSource Done) : Job;

    /// <summary>一个持续追加的 CSV 文件。</summary>
    private sealed class Sink
    {
        public readonly string Name;   // 上报去重用：tel / evt / ses
        public readonly string Header;
        public string? Key;            // 日期（yyyyMMdd）或会话路径
        public string? Path;
        public StreamWriter? W;
        public DateTime RetryAt;       // 打开失败后到此时刻前直接丢弃
        public Sink(string name, string header) { Name = name; Header = header; }
    }

    private readonly Sink _tel = new("tel", TelemetryCsv.Header);
    private readonly Sink _evt = new("evt", TelemetryCsv.EventHeader);
    private readonly Sink _ses = new("ses", TelemetryCsv.Header);
    private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
    private bool _dirty;
    private readonly HashSet<string> _reported = new();   // 已上报过写盘失败、尚未恢复的文件（按 Sink / 整文件区分）

    private async Task LoopAsync()
    {
        try { await RunAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // 不应发生（单条内容的异常已在 Process 里吃掉）：停止接收，避免调用方继续往一个没人读的队列里塞
            _q.Writer.TryComplete();
            Report("loop", $"运行记录后台任务异常退出：{ex.Message}");
        }
        finally
        {
            try { FlushAll(); } catch { }
            Close(_tel); Close(_evt); Close(_ses);
            while (_q.Reader.TryRead(out var j))   // 后台任务已退出：等待中的 FlushAsync 不能永远挂着
            {
                if (j is FlushJob fl) fl.Done.TrySetResult();
                else System.Threading.Interlocked.Increment(ref _dropped);
            }
        }
    }

    private async Task RunAsync()
    {
        Task<bool>? wait = null;
        while (true)
        {
            while (_q.Reader.TryRead(out var job))
            {
                try { Process(job); }
                catch (Exception ex)
                {
                    if (job is FlushJob fj) fj.Done.TrySetResult();
                    Report("loop", $"运行记录写盘异常：{ex.Message}");
                }
                if (_dirty && _sinceFlush.Elapsed >= FlushInterval) FlushAll();
            }

            wait ??= _q.Reader.WaitToReadAsync().AsTask();
            if (_dirty)
            {
                var due = FlushInterval - _sinceFlush.Elapsed;
                if (due <= TimeSpan.Zero) { FlushAll(); continue; }
                if (await Task.WhenAny(wait, Task.Delay(due)).ConfigureAwait(false) != wait)
                {
                    FlushAll();
                    continue;   // wait 仍在等待，下一轮复用
                }
            }
            bool more = await wait.ConfigureAwait(false);
            wait = null;
            if (!more) break;
        }
    }

    private void Process(Job job)
    {
        switch (job)
        {
            case RowJob r:
                Append(_tel, Path.Combine(Dir, $"telemetry-{Day(r.Time)}.csv"), Day(r.Time), r.Line);
                if (r.SessionPath != null) Append(_ses, r.SessionPath, r.SessionPath, r.Line);
                break;
            case SessionRowJob sr:
                Append(_ses, sr.SessionPath, sr.SessionPath, sr.Line);
                break;
            case EventJob e:
                Append(_evt, Path.Combine(Dir, $"events-{Day(e.Time)}.csv"), Day(e.Time), e.Line);
                break;
            case FileJob f:
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Path)!);
                    File.WriteAllText(f.Path, f.Content, new UTF8Encoding(true));
                    _reported.Remove("file");
                }
                catch (Exception ex) { Report("file", $"写入 {f.Path} 失败：{ex.Message}"); }
                break;
            case SessionCloseJob:
                Close(_ses);
                break;
            case FlushJob fl:
                FlushAll();
                fl.Done.TrySetResult();
                break;
        }
    }

    private void Append(Sink s, string path, string key, string line)
    {
        try
        {
            if (s.W == null || s.Key != key)
            {
                Close(s);
                if (DateTime.UtcNow < s.RetryAt) { System.Threading.Interlocked.Increment(ref _dropped); return; }
                s.Key = key;
                s.Path = OpenPath(path, s.Header, out bool fresh);
                bool partial = !fresh && !EndsWithNewline(s.Path);   // 上次异常退出（断电 / kill）留下半行
                var fs = new FileStream(s.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                s.W = new StreamWriter(fs, new UTF8Encoding(true));   // 文件为空时写 BOM，追加时不再写
                if (fresh) s.W.WriteLine(s.Header);
                else if (partial) s.W.WriteLine();   // 先把半行收尾，新行不拼进去（该半行本身仍是坏行）
            }
            s.W.WriteLine(line);
            _dirty = true;
            _reported.Remove(s.Name);
        }
        catch (Exception ex)
        {
            Close(s);
            s.RetryAt = DateTime.UtcNow + RetryOpenAfter;
            System.Threading.Interlocked.Increment(ref _dropped);
            Report(s.Name, $"写入 {path} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 选定实际写入的文件：不存在或为空 → 新文件（fresh，需写表头）；首行与表头一致 → 追加；
    /// 否则依次尝试 name-1.csv、name-2.csv…
    /// </summary>
    private static string OpenPath(string path, string header, out bool fresh)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path));
        string ext = System.IO.Path.GetExtension(path);
        for (int k = 0; ; k++)
        {
            string p = k == 0 ? path : $"{stem}-{k}{ext}";
            var fi = new FileInfo(p);
            if (!fi.Exists || fi.Length == 0) { fresh = true; return p; }
            if (FirstLine(p) == header) { fresh = false; return p; }
        }
    }

    /// <summary>非空文件的最后一个字节是否为换行。</summary>
    private static bool EndsWithNewline(string p)
    {
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }

    private static string? FirstLine(string p)
    {
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadLine();
    }

    private void FlushAll()
    {
        foreach (var s in new[] { _tel, _evt, _ses })
        {
            if (s.W == null) continue;
            try { s.W.Flush(); }
            catch (Exception ex)
            {
                Close(s);
                s.RetryAt = DateTime.UtcNow + RetryOpenAfter;
                Report(s.Name, $"写入 {s.Path} 失败：{ex.Message}");
            }
        }
        _dirty = false;
        _sinceFlush.Restart();
    }

    private static void Close(Sink s)
    {
        if (s.W != null)
        {
            try { s.W.Dispose(); } catch { /* 磁盘满时 Dispose 也会因 flush 失败抛出 */ }
        }
        s.W = null;
        s.Key = null;
    }

    private static string Day(DateTimeOffset t) => t.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private DateTime _lastReport = DateTime.MinValue;

    /// <summary>上报写盘失败：同一文件（key）的同一次故障只报一次；该文件恢复写入后再失败才再报，
    /// 且两次上报至少间隔 10 分钟（防刷屏）。别的文件写成功不会重置本文件的已上报状态。</summary>
    private void Report(string key, string msg)
    {
        if (!_reported.Add(key)) return;
        if (DateTime.UtcNow - _lastReport < TimeSpan.FromMinutes(10)) return;
        _lastReport = DateTime.UtcNow;
        try { _onError?.Invoke(msg); } catch { }
    }
}
