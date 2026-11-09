using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Services;

namespace ParallelReactor.Control.Core.Events;

/// <summary>
/// 一条事件，页面看到的形状（样稿 API.md §3.1 event：id、at、level、n、text；最新在前）。真机由 EventRecord / WS event 归一（<see cref="EventBook"/>）。
/// <see cref="Key"/>：身份 —— 真机「序号纪元:序号」（HMI 换了纪元时序号从头计，新旧事件不会撞号），演示 'e13'。
/// <see cref="Level"/>：Op = 操作（真机：kind 以 op. / control. / session. 开头）；Warn / Err = 「报警 / 提醒」筛选；其余 Info。
/// <see cref="Text"/>：样稿措辞（真机 = 标题 · 说明）；原文，断行保护在显示时做。
/// </summary>
public sealed record EventEntry(
    string Key,
    DateTime At,
    EventLevel Level,
    int? N,
    string Text,
    long Seq = 0,
    string? Kind = null,
    string? Title = null,
    string? Message = null,
    string? Origin = null,
    string? Client = null,
    string? Device = null)
{
    /// <summary>'10:11:30'。</summary>
    public string AtText => Fmt.Time(At);

    /// <summary>'RV3' / '整机'（设备页事件行的反应釜列）。</summary>
    public string RvText => N is int n ? "RV" + n.ToString(CultureInfo.InvariantCulture) : "整机";

    /// <summary>整机事件（反应釜列用 --t-3、400）。</summary>
    public bool IsDevice => N is null;

    /// <summary>
    /// 设备页事件行的文字（device.js evText）：反应釜列已经写了「RVn」，去掉文字开头的「RVn」和后面的空白；
    /// 与样稿的差异：后面紧跟数字时不去（「RV11 …」不会被 n = 1 截成「1 …」，样稿的正则会，但演示数据碰不到）。
    /// </summary>
    public string ShortText
    {
        get
        {
            if (N is not int n) return Text;
            string pre = "RV" + n.ToString(CultureInfo.InvariantCulture);
            if (!Text.StartsWith(pre, StringComparison.Ordinal)) return Text;
            if (Text.Length > pre.Length && char.IsAsciiDigit(Text[pre.Length])) return Text;
            int i = pre.Length;
            while (i < Text.Length && char.IsWhiteSpace(Text[i])) i++;
            return Text.Substring(i);
        }
    }

    /// <summary>「报警 / 提醒」筛选（提醒或错误）。</summary>
    public bool IsAlarm => Level is EventLevel.Warn or EventLevel.Err;

    /// <summary>「操作」筛选。</summary>
    public bool IsOp => Level == EventLevel.Op;

    /// <summary>图标名（device.js EV_IC）：错误 alert、提醒 warn、操作 user；信息没有图标（画一个灰点）为空串。</summary>
    public string IconName => Level switch { EventLevel.Err => "alert", EventLevel.Warn => "warn", EventLevel.Op => "user", _ => "" };
}

/// <summary>设备页「最近事件」的分段筛选（device.js evFilter）：全部 / 报警提醒 / 操作。</summary>
public enum EventFilter { All, Alarm, Op }

/// <summary>三个筛选各自的条数（device.js logBody cnt）。</summary>
public readonly record struct EventCounts(int All, int Alarm, int Op)
{
    public int Of(EventFilter f) => f switch { EventFilter.Alarm => Alarm, EventFilter.Op => Op, _ => All };
}

/// <summary>
/// 一台设备的事件簿（PORT-PLAN §3.2 第 8 条、§5 M4「数据」）：真机的 GET /events?afterSeq=…&amp;limit=…（分页、增量）+ WS event 消息，
/// 与演示数据的事件，归一成 <see cref="EventEntry"/>，最新在前、按身份去重、最多 <see cref="DefaultCapacity"/> 条。
/// <list type="bullet">
/// <item>初次加载：afterSeq = <see cref="InitialAfterSeq"/>(DeviceInfoDto.EventSeqHead)（最近 200 条）；之后跟随 WS；
///   一页有 more 时用 <see cref="NextAfterSeq"/> 接着取（WS 断线重同步时 Client 自己补发缺的事件，簿只去重）。</item>
/// <item>序号纪元变了（HMI 事件库重置）：旧事件留着（身份带纪元，不会撞号），续取的序号从 0 起。</item>
/// <item>级别：kind 以 op. / control. / session. 开头算「操作」；否则 warn → 提醒、err → 错误、其余信息。</item>
/// <item>反应釜号：HMI 给的 channel；没有时取标题开头的「RVn」，再没有取来源「RVn」。文字 = 标题 · 说明。</item>
/// </list>
/// 不是线程安全的：在 UI 线程上用。
/// </summary>
public sealed class EventBook
{
    /// <summary>最多保留的条数（样稿 sim.js log：超过 200 条截掉最旧的）。</summary>
    public const int DefaultCapacity = 200;

    /// <summary>初次加载取最近多少条（PORT-PLAN §3.2 第 8 条：afterSeq = max(0, EventSeqHead − 200)）。</summary>
    public const int InitialBack = 200;

    /// <summary>设备页事件列表最多显示的条数（device.js EV_MAX）。</summary>
    public const int ListMax = 60;

    private readonly int _capacity;
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly List<EventEntry> _items = new();
    private EventEntry[] _view = Array.Empty<EventEntry>();
    private bool _demo;

    public EventBook(string? device = null, int capacity = DefaultCapacity)
    {
        Device = device;
        _capacity = Math.Max(1, capacity);
    }

    public string? Device { get; }

    /// <summary>内容每变一次加一。</summary>
    public int Version { get; private set; }

    /// <summary>全部事件，最新在前。</summary>
    public IReadOnlyList<EventEntry> Items => _view;

    /// <summary>当前的序号纪元（真机；还没收到为 null）。</summary>
    public int? SeqEpoch { get; private set; }

    /// <summary>当前纪元里收到的最大序号（下一页 GET /events 的 afterSeq）。</summary>
    public long NextAfterSeq { get; private set; }

    /// <summary>最后一页说还有更多（调用方接着用 <see cref="NextAfterSeq"/> 取）。</summary>
    public bool More { get; private set; }

    /// <summary>初次加载的 afterSeq：max(0, head − back)。</summary>
    public static long InitialAfterSeq(long eventSeqHead, int back = InitialBack) => Math.Max(0, eventSeqHead - Math.Max(0, back));

    // ———————————————————— 真机 ————————————————————

    /// <summary>GET /events 的一页：纪元变了先重置续取序号；按身份去重并入。返回内容是否变了。</summary>
    public bool Load(EventsPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        LeaveDemo();
        SetEpoch(page.SeqEpoch);
        bool changed = false;
        foreach (var r in page.Items ?? Array.Empty<EventRecord>()) changed |= Insert(FromRecord(r, Device));
        // 同 Client 的 REST 重同步：这一页什么都没有、又说没有更多，而服务端的序号头在前面 —— 中间那些已不在环形缓冲里，直接跳到头
        if (!page.More && page.Count == 0 && page.EventSeqHead > NextAfterSeq && page.SeqEpoch == SeqEpoch) NextAfterSeq = page.EventSeqHead;
        More = page.More;
        return Commit(changed);
    }

    /// <summary>一条事件记录（REST 页里的一项）。</summary>
    public bool Add(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        LeaveDemo();
        SetEpoch(record.SeqEpoch);
        return Commit(Insert(FromRecord(record, Device)));
    }

    /// <summary>WS event 消息。</summary>
    public bool Add(EventMsg message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LeaveDemo();
        SetEpoch(message.SeqEpoch);
        return Commit(Insert(FromMsg(message, Device)));
    }

    /// <summary>清空（设备移除、换了地址）。</summary>
    public bool Clear()
    {
        bool had = _items.Count > 0;
        _items.Clear();
        _keys.Clear();
        SeqEpoch = null;
        NextAfterSeq = 0;
        More = false;
        return Commit(had);
    }

    // ———————————————————— 演示 ————————————————————

    /// <summary>演示数据（DemoDevice.Events，最新在前）：照给定的顺序整个换。返回内容是否变了。</summary>
    public bool Sync(IEnumerable<DemoEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Sync(events.Select(e => FromDemo(e, Device)));
    }

    /// <summary>任意来源：照给定的顺序整个换（身份重复的只留第一条；超过上限截掉后面的）。</summary>
    public bool Sync(IEnumerable<EventEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new List<EventEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (next.Count >= _capacity) break;
            if (keys.Add(e.Key)) next.Add(e);
        }
        bool changed = !_demo || !_items.SequenceEqual(next);
        _demo = true;
        SeqEpoch = null;
        NextAfterSeq = 0;
        More = false;
        if (!changed) return false;
        _items.Clear();
        _items.AddRange(next);
        _keys.Clear();
        _keys.UnionWith(keys);
        return Commit(true);
    }

    // ———————————————————— 读 ————————————————————

    /// <summary>三个筛选各自的条数（全部 / 报警提醒 / 操作）。</summary>
    public EventCounts Counts
    {
        get
        {
            int al = 0, op = 0;
            foreach (var e in _view) { if (e.IsAlarm) al++; else if (e.IsOp) op++; }
            return new EventCounts(_view.Length, al, op);
        }
    }

    /// <summary>筛选后的前 <paramref name="max"/> 条（device.js：.slice(0, EV_MAX)）。</summary>
    public IReadOnlyList<EventEntry> Filter(EventFilter filter, int max = ListMax)
    {
        var list = new List<EventEntry>();
        foreach (var e in _view)
        {
            if (list.Count >= max) break;
            if (filter == EventFilter.Alarm && !e.IsAlarm) continue;
            if (filter == EventFilter.Op && !e.IsOp) continue;
            list.Add(e);
        }
        return list;
    }

    // ———————————————————— 归一 ————————————————————

    /// <summary>EventRecord（GET /events 的一项）→ 条目。</summary>
    public static EventEntry FromRecord(EventRecord r, string? device = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        return Make(r.SeqEpoch, r.Seq, r.Ts, r.Level, r.Kind, r.Channel, r.Origin, r.Client, r.Title, r.Message, device);
    }

    /// <summary>WS event 消息 → 条目。</summary>
    public static EventEntry FromMsg(EventMsg m, string? device = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        return Make(m.SeqEpoch, m.Seq, m.Ts, m.Level, m.Kind, m.Channel, m.Origin, m.Client, m.Title, m.Message, device);
    }

    /// <summary>演示事件 → 条目（身份 = 'e13'）。</summary>
    public static EventEntry FromDemo(DemoEvent e, string? device = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new EventEntry(e.Id, e.At, e.Level, e.N, e.Text, Device: device);
    }

    /// <summary>事件级别：kind 以 op. / control. / session. 开头 → 操作；否则 warn → 提醒、err → 错误、其余信息。</summary>
    public static EventLevel LevelOf(string? level, string? kind)
    {
        string k = kind ?? "";
        if (k.StartsWith("op.", StringComparison.Ordinal) || k.StartsWith("control.", StringComparison.Ordinal) || k.StartsWith("session.", StringComparison.Ordinal))
            return EventLevel.Op;
        return (level ?? "").Trim().ToLowerInvariant() switch
        {
            "warn" or "warning" => EventLevel.Warn,
            "err" or "error" => EventLevel.Err,
            _ => EventLevel.Info,
        };
    }

    /// <summary>身份：'纪元:序号'。</summary>
    public static string KeyOf(int seqEpoch, long seq) =>
        seqEpoch.ToString(CultureInfo.InvariantCulture) + ":" + seq.ToString(CultureInfo.InvariantCulture);

    private static EventEntry Make(int epoch, long seq, DateTime ts, string? level, string? kind, int? channel, string? origin, string? client,
        string? title, string? message, string? device)
    {
        string t = title ?? "", msg = message ?? "";
        return new EventEntry(
            Key: KeyOf(epoch, seq),
            At: Local(ts),
            Level: LevelOf(level, kind),
            N: AlarmCodes.ChannelOf(channel, t, origin),
            Text: string.IsNullOrWhiteSpace(msg) ? t : t + " · " + msg,
            Seq: seq,
            Kind: kind,
            Title: t,
            Message: msg,
            Origin: origin,
            Client: client,
            Device: device);
    }

    // ———————————————————— 内部 ————————————————————

    private void LeaveDemo()
    {
        if (!_demo) return;
        _demo = false;
        _items.Clear();
        _keys.Clear();
    }

    private void SetEpoch(int epoch)
    {
        if (SeqEpoch == epoch) return;
        if (SeqEpoch != null) NextAfterSeq = 0;
        SeqEpoch = epoch;
    }

    /// <summary>按时刻插入（新的在前；同一时刻序号大的在前），按身份去重，超过上限丢最旧的。返回是否插入了。</summary>
    private bool Insert(EventEntry e)
    {
        if (SeqEpoch == ParseEpoch(e.Key) && e.Seq > NextAfterSeq) NextAfterSeq = e.Seq;
        if (!_keys.Add(e.Key)) return false;
        int at = _items.Count;
        for (int i = 0; i < _items.Count; i++)
        {
            var x = _items[i];
            if (e.At > x.At || (e.At == x.At && e.Seq > x.Seq)) { at = i; break; }
        }
        _items.Insert(at, e);
        while (_items.Count > _capacity)
        {
            _keys.Remove(_items[^1].Key);
            _items.RemoveAt(_items.Count - 1);
        }
        return _keys.Contains(e.Key);
    }

    private static int? ParseEpoch(string key)
    {
        int i = key.IndexOf(':');
        return i > 0 && int.TryParse(key.AsSpan(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ep) ? ep : null;
    }

    private bool Commit(bool changed)
    {
        if (!changed) return false;
        _view = _items.ToArray();
        Version++;
        return true;
    }

    private static DateTime Local(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t.ToLocalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(),
        _ => t,
    };
}

/// <summary>设备页「最近事件」的文字（device.js logBody），逐字照样稿。</summary>
public static class EventTexts
{
    /// <summary>小标题（设备页「最近事件」、检查器「最近事件」）。</summary>
    public const string Title = "最近事件";

    /// <summary>分段按钮的文字：'全部 13' / '报警/提醒 1' / '操作 6'。</summary>
    public static string SegLabel(EventFilter f, EventCounts c) => f switch
    {
        EventFilter.Alarm => "报警/提醒 " + c.Alarm.ToString(CultureInfo.InvariantCulture),
        EventFilter.Op => "操作 " + c.Op.ToString(CultureInfo.InvariantCulture),
        _ => "全部 " + c.All.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>分段按钮的提示：'全部事件' / '只看报警与提醒' / '只看操作记录'。</summary>
    public static string SegTip(EventFilter f) => f switch
    {
        EventFilter.Alarm => "只看报警与提醒",
        EventFilter.Op => "只看操作记录",
        _ => "全部事件",
    };

    /// <summary>筛选后为空：'没有报警或提醒事件' / '没有操作事件' / '没有事件'。</summary>
    public static string Empty(EventFilter f) => "没有" + (f == EventFilter.Alarm ? "报警或提醒" : f == EventFilter.Op ? "操作" : "") + "事件";

    /// <summary>检查器「最近事件」分区标题右边：'13 条'。</summary>
    public static string CountText(int n) => n.ToString(CultureInfo.InvariantCulture) + " 条";

    /// <summary>检查器「最近事件」最多列几条（shell.js：d.events.slice(0, 5)）。</summary>
    public const int InspectorMax = 5;

    /// <summary>检查器「最近事件」没有事件时。</summary>
    public const string InspectorNone = "暂无事件";
}
