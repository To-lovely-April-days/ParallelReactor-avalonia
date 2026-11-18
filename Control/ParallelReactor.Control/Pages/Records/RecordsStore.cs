using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Records;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Services;

namespace ParallelReactor.Control.Pages.Records;

/// <summary>会话的一条标记或事件（下方「标记与事件」页签）：时刻、种类（图标）、来源（「RV3」「全部」「会话」）、文字。</summary>
public sealed record RecEvent(DateTime At, RecEventKind Kind, string Src, string Text);

/// <summary>标记 / 事件的图标：标记（灰旗）、报警时刻的标记（红旗）、信息（灰圈 i）、提醒（琥珀三角）、报警（红方块）。</summary>
public enum RecEventKind { Mark, MarkErr, Info, Warn, Err }

/// <summary>
/// 会话的一个数据文件（下方「数据文件」页签）：文件名、内容、大小；<see cref="Type"/> / <see cref="Name"/> = HMI 的 GET /files/{type}/{name}
/// （实时设备才有；演示数据为 null，下载按钮带原因）。
/// </summary>
public sealed record RecFile(string File, string What, string Size, string? Type = null, string? Name = null);

/// <summary>
/// 记录页的数据（融合版 F7）：各台设备的会话汇总到 PC 上，按开始时刻从新到旧；每条会话的每路数据。
/// <list type="bullet">
/// <item><b>会话列表</b>：演示数据 = Core 的 DemoRecords（样稿 SESS）+ 设备上此刻在记录、又不在表里的会话（演示里新建的）；
///   实时设备 = GET /sessions（列表）+ GET /sessions/{id}（manifest：名字、状态、开始 / 结束、创建者、标记、泄漏测试），
///   打开页面时取、之后最多 30 s 取一次（「刷新」立即取）；已结束的会话的 manifest 只取一次。</item>
/// <item><b>每路数据</b>：进行中的会话 = 本机内存里的历史缓存（与主页曲线同一份，最多 240 min）+ 每路此刻的状态；
///   已结束的会话：演示 = DemoRecords 的确定性曲线；实时 = GET /telemetry?from&amp;to（会话窗口）下载到
///   &lt;数据目录&gt;/records/&lt;设备&gt;/&lt;会话号&gt;.csv（数据目录 = PR_CONTROL_DATA，默认 文稿/ParallelReactor），下次直接读缓存。</item>
/// </list>
/// 线程：全部在 UI 线程上调；下载与解析在后台，完成后经构造时的同步上下文（UI 线程）发 <see cref="Changed"/>。
/// </summary>
public sealed class RecordsStore
{
    private readonly FleetVm _fleet;
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<int, LiveList> _live = new();
    private readonly Dictionary<string, RecSeriesSet> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _seriesState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Version, DateTime Now, RecSeriesSet Set)> _liveSeries = new(StringComparer.Ordinal);

    /// <summary>实时设备的会话列表最多多久取一次。</summary>
    public static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(30);
    /// <summary>每台设备最多取多少条会话的 manifest（按开始时刻从新到旧）。</summary>
    public const int MaxPerDevice = 40;

    private sealed class LiveList
    {
        public DateTime LoadedAt = DateTime.MinValue;
        public bool Loading;
        public string? Error;
        public List<RecSession> Sessions = new();
        public Dictionary<string, SessionDetailDto> Details = new(StringComparer.Ordinal);
    }

    public RecordsStore(FleetVm fleet)
    {
        _fleet = fleet;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>后台加载完成（会话列表、下载 / 解析好的数据）。</summary>
    public event Action? Changed;

    /// <summary>数据目录（<see cref="Services.DataDirs.Root"/>：PR_CONTROL_DATA &gt; 设置页 &gt; 文稿/ParallelReactor）下的 records；设置页改了目录马上跟着变。测试可换。</summary>
    public string CacheRoot { get => _cacheRoot ?? DefaultCacheRoot(); set => _cacheRoot = value; }

    private string? _cacheRoot;

    public static string DefaultCacheRoot() => Path.Combine(Services.DataDirs.Root, "records");

    private void Raise()
    {
        if (_ui != null && SynchronizationContext.Current != _ui) _ui.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }

    // ———————————————————— 会话列表 ————————————————————

    /// <summary>全部设备的会话（按开始时刻从新到旧）。实时设备按需在后台刷新（<paramref name="force"/> = 立即）。</summary>
    public IReadOnlyList<RecSession> Sessions(bool force = false)
    {
        var list = new List<RecSession>();
        foreach (var d in _fleet.Devices)
        {
            if (d.Source.IsDemo) list.AddRange(DemoSessions(d));
            else list.AddRange(LiveSessions(d, force));
        }
        return list.OrderByDescending(s => s.Start).ThenBy(s => s.K).ToList();
    }

    /// <summary>某台设备的会话列表在加载中 / 出错（实时设备；「1号机 正在读取会话…」）。</summary>
    public string? DeviceStatus(int k) =>
        _live.TryGetValue(k, out var l) ? l.Loading && l.Sessions.Count == 0 ? "正在读取会话…" : l.Error : null;

    private IEnumerable<RecSession> DemoSessions(DeviceVm d)
    {
        var now = _fleet.Now;
        var cur = d.Source.Session;
        var output = new List<RecSession>();
        if (_fleet.DemoData?.Fusion == true)
        {
            foreach (var s in DemoRecords.Sessions(k => k == d.K))
            {
                // 进行中的演示会话：设备上已经结束（演示里点了「结束会话」）就当作已结束
                if (s.Active && (cur == null || !cur.Active || cur.Id != s.Id))
                    output.Add(s with { State = RecState.Ended, End = EndOf(d) ?? now });
                else output.Add(s with { Marks = s.Active && cur != null ? cur.Markers : s.Marks });
            }
        }
        if (cur is { Active: true, Id: { } id } && output.All(s => s.Id != id))
            output.Add(new RecSession(id, d.K, "新会话", CurrentProgram(d), "—", _fleet.ClientName, LiveChannels(d), cur.StartAt ?? now, null,
                RecState.Active, cur.Markers, 0));
        return output;
    }

    private static DateTime? EndOf(DeviceVm d) => d.Source is DemoDeviceSource ds ? ds.Device.Session?.EndAt : null;

    /// <summary>进行中的会话的通道：装了、有读数的（停用的不算，样稿 1号机会话没有 RV7）。</summary>
    private static IReadOnlyList<int> LiveChannels(DeviceVm d) => d.Reactors.Where(r => r.Installed && !r.TempNa).Select(r => r.N).ToList();

    /// <summary>设备此刻的程序名（各路程序里最多的那个；没有 = 空）。</summary>
    private static string CurrentProgram(DeviceVm d) =>
        d.Reactors.Select(r => d.Source.Program(r.N)?.Name).Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";

    private IEnumerable<RecSession> LiveSessions(DeviceVm d, bool force)
    {
        if (!_live.TryGetValue(d.K, out var l)) _live[d.K] = l = new LiveList();
        bool due = force || DateTime.UtcNow - l.LoadedAt > ListInterval;
        if (due && !l.Loading && d.Source is LiveDeviceSource src && src.Connection is { } c && d.Source.Conn.Online)
        {
            l.Loading = true;
            _ = LoadListAsync(d, c.Rest, l);
        }
        // 进行中的会话：状态 / 标记跟着设备此刻（列表 30 s 才取一次）
        var cur = d.Source.Session;
        return l.Sessions.Select(s => s.Active && (cur == null || !cur.Active || cur.Id != s.Id) ? s with { State = RecState.Ended, End = s.End ?? _fleet.Now } : s).ToList();
    }

    private async Task LoadListAsync(DeviceVm d, ReactorApiClient api, LiveList l)
    {
        try
        {
            var dto = await api.SessionsAsync(CancellationToken.None).ConfigureAwait(false);
            var items = dto.Items.OrderByDescending(i => i.StartedAt ?? i.From ?? DateTime.MinValue).Take(MaxPerDevice).ToList();
            var output = new List<RecSession>(items.Count);
            foreach (var it in items)
            {
                string id = it.Id ?? Path.GetFileNameWithoutExtension(it.File);
                bool active = dto.Current is { Active: true } cur && (cur.Id == id || cur.TestId == it.TestId && it.Id == null);
                if (active || !l.Details.ContainsKey(id))
                {
                    try { l.Details[id] = await api.SessionAsync(id, CancellationToken.None).ConfigureAwait(false); }
                    catch (ApiException) { /* 没有 manifest 的旧会话：只用列表里的信息 */ }
                }
                l.Details.TryGetValue(id, out var det);
                output.Add(FromDto(d, it, det, active));
            }
            l.Sessions = output;
            l.Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            l.Error = "读取会话失败：" + DeviceActions.Describe(ex);
        }
        finally
        {
            l.Loading = false;
            l.LoadedAt = DateTime.UtcNow;
            Raise();
        }
    }

    /// <summary>
    /// GET /sessions 的一项 + manifest → 一条记录：名字 = 备注（没有 = 测试编号）；创建者 = 远程客户端名 / 「触屏」；
    /// 通道 = 装了的各路；泄漏测试 = 会话里开始过泄漏测试；程序名 = 进行中时设备此刻的程序（HMI 不记程序名）。
    /// </summary>
    public RecSession FromDto(DeviceVm d, SessionInfoDto it, SessionDetailDto? det, bool active)
    {
        var m = det?.Manifest;
        string id = det?.Id ?? it.Id ?? Path.GetFileNameWithoutExtension(it.File);
        var start = Local(m?.StartedAt ?? it.StartedAt ?? it.From) ?? _fleet.Now;
        var state = m?.State switch
        {
            SessionManifest.Active => RecState.Active,
            SessionManifest.Interrupted => RecState.Interrupted,
            SessionManifest.Ended => RecState.Ended,
            _ => active ? RecState.Active : RecState.Ended,
        };
        if (state == RecState.Active && !active) state = RecState.Interrupted;
        DateTime? end = state == RecState.Active ? null : Local(m?.EndedAt ?? it.To) ?? start;
        string by = m == null ? "—" : m.Source == "remote" ? m.Client ?? "PC" : "触屏";
        var hold = m is { LeakTests.Length: > 0 } || id.StartsWith("LK-", StringComparison.Ordinal) ? RecHold.Leak : RecHold.None;
        string prog = state == RecState.Active ? CurrentProgram(d) : "";
        if (hold == RecHold.None && prog == "气密预检") hold = RecHold.Tight;
        var chs = d.Reactors.Where(r => r.Installed && (state != RecState.Active || !r.TempNa)).Select(r => r.N).ToList();
        string? note = state == RecState.Interrupted ? "会话中断 · 数据保留到 " + RecTexts.Hm(end ?? start) : null;
        return new RecSession(id, d.K, m?.Note ?? m?.TestId ?? it.TestId, prog, "—", by, chs, start, end, state, m?.Markers.Length ?? 0,
            AlarmsIn(d, start, end), note, null, hold);
    }

    private static DateTime? Local(DateTime? t) => t is DateTime x ? x.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(x, DateTimeKind.Utc).ToLocalTime() : x.ToLocalTime() : null;

    /// <summary>会话窗口里的报警 / 提醒事件数（本机收到的事件；更早的事件不在本机时少算）。</summary>
    private int AlarmsIn(DeviceVm d, DateTime start, DateTime? end)
    {
        var e = end ?? _fleet.Now;
        return d.Events.Count(x => (x.Level == EventLevel.Err || x.Level == EventLevel.Warn) && x.At >= start && x.At <= e);
    }

    // ———————————————————— 每路数据 ————————————————————

    private static string Key(RecSession s) => s.K.ToString(CultureInfo.InvariantCulture) + ":" + s.Id;

    /// <summary>
    /// 这条会话的各路数据；还没有（正在下载 / 解析，或失败）时为 null，<paramref name="status"/> 写明（「正在下载数据…」「下载失败：…」）。
    /// </summary>
    public RecSeriesSet? Series(RecSession s, out string? status)
    {
        status = null;
        var d = _fleet.Device(s.K);
        if (s.Active)
        {
            if (d == null) { status = "设备不在了"; return null; }
            return LiveSeries(s, d);
        }
        var key = Key(s);
        if (_series.TryGetValue(key, out var set)) return set;
        if (d != null && d.Source.IsDemo)
        {
            set = DemoRecords.Series(s) ?? RecSeriesSet.Empty;
            _series[key] = set;
            return set;
        }
        if (_seriesState.TryGetValue(key, out var st)) { status = st; return null; }
        if (d?.Source is not LiveDeviceSource src) { status = "设备不在了"; return null; }
        var cache = CachePath(d, s);
        if (File.Exists(cache)) { _seriesState[key] = status = "正在读取数据…"; _ = ParseAsync(key, s, cache); return null; }
        if (src.Connection is not { } c || !d.Source.Conn.Online) { status = "设备未连接 · 连上后下载这次会话的数据"; return null; }
        _seriesState[key] = status = "正在下载数据…";
        _ = DownloadAsync(key, s, c.Rest, cache);
        return null;
    }

    /// <summary>缓存文件：&lt;数据目录&gt;/records/&lt;设备 SN（没有用本机给的身份）&gt;/&lt;会话号&gt;.csv。</summary>
    public string CachePath(DeviceVm d, RecSession s)
    {
        string dev = Safe(d.Source.Sn is { Length: > 0 } sn && sn != "—" ? sn : d.Source.Id.ToString("N"));
        return Path.Combine(CacheRoot, dev, Safe(s.Id) + ".csv");
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    private async Task DownloadAsync(string key, RecSession s, ReactorApiClient api, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            var end = s.End ?? _fleet.Now;
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                await api.TelemetryRangeAsync(s.Start.ToUniversalTime(), end.ToUniversalTime(), fs, CancellationToken.None).ConfigureAwait(false);
            File.Move(part, path, overwrite: true);
            await ParseAsync(key, s, path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _seriesState[key] = "下载失败：" + DeviceActions.Describe(ex) + " · 点「刷新」重试";
            Raise();
        }
    }

    private async Task ParseAsync(string key, RecSession s, string path)
    {
        try
        {
            var set = await Task.Run(() => ParseFile(s, path)).ConfigureAwait(false);
            _series[key] = set;
            _seriesState.Remove(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _seriesState[key] = "数据文件读不了：" + ex.Message;
        }
        Raise();
    }

    /// <summary>读缓存的 CSV → 各路数据（只要会话的通道；横轴 = 会话时长）。</summary>
    public static RecSeriesSet ParseFile(RecSession s, string path)
    {
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var byN = TelemetryCsvReader.Read(r, s.Start, s.End);
        var chs = s.Channels.Select(n => new RecChannelSeries(n, byN.TryGetValue(n, out var p) ? p : new List<RecPoint>())).ToList();
        double span = s.End is DateTime e ? (e - s.Start).TotalMinutes : chs.SelectMany(c => c.Points).Select(p => p.M).DefaultIfEmpty(60).Max();
        return new RecSeriesSet(chs, Math.Max(1, span), false);
    }

    /// <summary>清掉失败 / 缓存的状态（「刷新」）：失败的重新下载。</summary>
    public void Retry(RecSession s)
    {
        var key = Key(s);
        if (_seriesState.TryGetValue(key, out var st) && st.StartsWith("下载失败", StringComparison.Ordinal)) _seriesState.Remove(key);
    }

    /// <summary>
    /// 进行中的会话：本机历史缓存里从会话开始到现在的采样（10 s 一格），加上每路此刻的状态。历史缓存没变（版本、时刻）时用上一次的结果。
    /// </summary>
    private RecSeriesSet LiveSeries(RecSession s, DeviceVm d)
    {
        var src = d.Source;
        var now = src.History.Now;
        int version = 0;
        foreach (int n in s.Channels) version = unchecked(version * 31 + (src.History.Channel(n)?.Version ?? -1));
        var key = Key(s);
        var states = s.Channels.Select(n => LiveState(d, n)).ToList();
        if (_liveSeries.TryGetValue(key, out var hit) && hit.Version == version && hit.Now == now)
            return hit.Set with { Channels = hit.Set.Channels.Select((c, i) => c with { Now = states[i] }).ToList() };
        var chs = new List<RecChannelSeries>(s.Channels.Count);
        for (int i = 0; i < s.Channels.Count; i++)
        {
            int n = s.Channels[i];
            var h = src.History.Channel(n);
            var pts = new List<RecPoint>(h?.Count ?? 0);
            if (h != null)
                for (int j = 0; j < h.Count; j++)
                {
                    var x = h[j];
                    var t = x.T.ToLocalTime();
                    if (t < s.Start || x.Gap) continue;
                    pts.Add(new RecPoint((t - s.Start).TotalMinutes, x.TempC ?? double.NaN, x.PressureMPa ?? double.NaN, x.Mmol ?? double.NaN));
                }
            chs.Add(new RecChannelSeries(n, pts, states[i]));
        }
        var set = new RecSeriesSet(chs, Math.Max(1, (now - s.Start).TotalMinutes), true);
        _liveSeries[key] = (version, now, set);
        return set;
    }

    /// <summary>一路此刻的状态（样稿 rcData 实时分支：c.t、RUN、c.st、c.gas）。</summary>
    public static RecLiveState LiveState(DeviceVm d, int n)
    {
        var r = d.Reactor(n);
        if (r == null || !r.Installed) return new RecLiveState(false, false, false, false, "未安装", null);
        var own = r.Own;
        bool alarm = own == StateKey.Alarm;
        bool running = own is StateKey.Heat or StateKey.Press or StateKey.React or StateKey.Cool or StateKey.Wait or StateKey.Stir;
        return new RecLiveState(!r.TempNa, running, own == StateKey.Cool, alarm, RunRowText.Word(own, r.Cause, r.OwnWord), r.GasMmol);
    }

    // ———————————————————— 标记与事件、数据文件 ————————————————————

    /// <summary>
    /// 会话的标记与事件（按时刻）：标记 = 进行中的会话用设备此刻的会话标记、实时设备的历史会话用 manifest、演示的历史会话用 DemoRecords；
    /// 事件 = 本机收到的这台设备的事件里落在会话窗口里的（进行中的会话才有，同样稿）。报警时刻（同一路 ±60 s 内有报警事件）的标记是红旗。
    /// </summary>
    public IReadOnlyList<RecEvent> Events(RecSession s)
    {
        var d = _fleet.Device(s.K);
        var output = new List<RecEvent>();
        var evs = d == null || !s.Active ? new List<EventVm>() : d.Events.Where(e => e.At >= s.Start && e.At <= _fleet.Now).ToList();
        IEnumerable<(DateTime At, string Text, int? N)> marks;
        if (s.Active && d != null) marks = d.Source.History.Marks.Select(m => (m.At.ToLocalTime(), m.Text, m.N));
        else if (d != null && d.Source.IsDemo) marks = DemoRecords.Marks(s).Select(m => (m.At, m.Text, m.Src.StartsWith("RV", StringComparison.Ordinal) ? (int?)int.Parse(m.Src[2..], CultureInfo.InvariantCulture) : null));
        else marks = _live.TryGetValue(s.K, out var l) && l.Details.TryGetValue(s.Id, out var det) && det.Manifest is { } m
            ? m.Markers.Select(x => (Local(x.Ts) ?? s.Start, x.Text, MarkRv(x.Text)))
            : Enumerable.Empty<(DateTime, string, int?)>();
        foreach (var (at, text, n) in marks)
        {
            bool err = n is int nn && evs.Any(e => e.N == nn && e.Level == EventLevel.Err && Math.Abs((e.At - at).TotalSeconds) <= 60);
            output.Add(new RecEvent(at, err ? RecEventKind.MarkErr : RecEventKind.Mark, n is int k ? "RV" + k.ToString(CultureInfo.InvariantCulture) : "全部", "标记 · " + text));
        }
        foreach (var e in evs)
        {
            var re = ResPaneVm.Event(e);
            var kind = e.Level switch { EventLevel.Err => RecEventKind.Err, EventLevel.Warn => RecEventKind.Warn, _ => RecEventKind.Info };
            output.Add(new RecEvent(e.At, kind, re.Src, re.Title));
        }
        return output.OrderBy(x => x.At).ToList();
    }

    /// <summary>标记文字开头的「RVn」→ n（同 LiveDeviceSource 的 MarkRv）。</summary>
    private static int? MarkRv(string? text)
    {
        if (text is not { Length: > 2 } t || !t.StartsWith("RV", StringComparison.Ordinal)) return null;
        int i = 2; while (i < t.Length && char.IsDigit(t[i])) i++;
        return i > 2 && int.TryParse(t.AsSpan(2, i - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>会话的数据文件：演示 = 样稿的四类文件；实时 = GET /sessions/{id} 的文件（sessions/&lt;id&gt;.csv、.json …）。</summary>
    public IReadOnlyList<RecFile> Files(RecSession s)
    {
        var d = _fleet.Device(s.K);
        if (d == null) return Array.Empty<RecFile>();
        if (d.Source.IsDemo) return DemoRecords.Files(s).Select(f => new RecFile(f.Name, f.What, f.Size)).ToList();
        if (!_live.TryGetValue(s.K, out var l) || !l.Details.TryGetValue(s.Id, out var det)) return Array.Empty<RecFile>();
        return det.Files.Select(f =>
        {
            int i = f.IndexOf('/');
            string type = i > 0 ? f[..i] : "sessions", name = i > 0 ? f[(i + 1)..] : f;
            string what = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "会话清单：标记、下发的程序、泄漏测试" : "会话期间的运行记录（温度 / 压力 / 阀态 / 搅拌 / 吸气量）";
            return new RecFile(name, what, "—", type, name);
        }).ToList();
    }

    /// <summary>下载一个数据文件到 &lt;数据目录&gt;/records/&lt;设备&gt;/files/，返回本机路径。</summary>
    public async Task<string> DownloadFileAsync(RecSession s, RecFile f)
    {
        var d = _fleet.Device(s.K) ?? throw new InvalidOperationException("设备不在了");
        if (f.Type == null || f.Name == null) throw new InvalidOperationException("演示数据没有文件可下载");
        if (d.Source is not LiveDeviceSource { Connection: { } c }) throw new InvalidOperationException(d.Alias + "未连接");
        var dir = Path.Combine(Path.GetDirectoryName(CachePath(d, s))!, "files");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Safe(f.Name));
        await using (var fs = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None))
            await c.Rest.DownloadFileAsync(f.Type, f.Name, fs, CancellationToken.None).ConfigureAwait(true);
        File.Move(path + ".part", path, overwrite: true);
        return path;
    }

    /// <summary>
    /// 导出这次会话的数据（「下载数据」）：实时 = 会话窗口的运行记录 CSV（已缓存的直接复制）；演示 = 按演示曲线写一份同格式的简表。返回本机路径。
    /// </summary>
    public async Task<string> ExportDataAsync(RecSession s)
    {
        var d = _fleet.Device(s.K) ?? throw new InvalidOperationException("设备不在了");
        var dir = Path.Combine(CacheRoot, "export");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Safe(s.Id) + "-telemetry.csv");
        if (!d.Source.IsDemo && !s.Active)
        {
            var cache = CachePath(d, s);
            if (!File.Exists(cache))
            {
                if (d.Source is not LiveDeviceSource { Connection: { } c }) throw new InvalidOperationException(d.Alias + "未连接");
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await using (var fs = new FileStream(cache + ".part", FileMode.Create, FileAccess.Write, FileShare.None))
                    await c.Rest.TelemetryRangeAsync(s.Start.ToUniversalTime(), (s.End ?? _fleet.Now).ToUniversalTime(), fs, CancellationToken.None).ConfigureAwait(true);
                File.Move(cache + ".part", cache, overwrite: true);
            }
            File.Copy(cache, path, overwrite: true);
            return path;
        }
        var set = Series(s, out var st) ?? throw new InvalidOperationException(st ?? "数据还没准备好");
        await File.WriteAllTextAsync(path, RecExport.SeriesCsv(s, set), new UTF8Encoding(true)).ConfigureAwait(true);
        return path;
    }
}
