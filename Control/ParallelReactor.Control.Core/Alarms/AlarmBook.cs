using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.State;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Core.Alarms;

/// <summary>
/// 一台设备的报警簿（PORT-PLAN §3.2 第 7 条、§5 M4「数据」）：把真机的 GET /alarms、WS alarm 消息和演示数据的报警归一成一个
/// <see cref="AlarmEntry"/> 列表，页面（总览分区头、报警条、设备页「报警与事件」、检查器）只读这一份。
/// <list type="bullet">
/// <item><b>真机</b>：<see cref="Load(AlarmsDto?, string?)"/>（每次（重新）连上、HMI 重启后：整个换掉）+ <see cref="Apply(AlarmDto, string?)"/>（WS 增量，
///   按 <see cref="AlarmEntry.Key"/> = bootId/id 更新同一条）。level = info 的过滤掉（HMI 的报警表里混着事件）；state ack / done 算已确认；
///   cleared / done（或带 clearedAt）算条件已解除 —— 不在 <see cref="Current"/> 里，留在 <see cref="All"/>（有上限）。bootId 变了先清空（旧启动的报警作废）。
///   <b>确认之后</b>（M4 复核定的规则，见 <see cref="Current"/>）：条件型报警（<see cref="AlarmEntry.Condition"/>：超温、超压、模块离线）同样稿，
///   确认了照样列出、照样计数，直到 HMI 解除；其余报警 HMI 永远不会解除（Alarm.Log 记的），确认了就退出「当前报警」（留在 <see cref="All"/> 与事件里），
///   和触屏的「未处理」计数一致；急停那一条在设备仍急停时照样列出（<see cref="SetEstop"/>，同样稿：急停复位才清掉）。</item>
/// <item><b>演示</b>：<see cref="Sync(IEnumerable{DemoAlarm})"/> 照 DemoDevice.Alarms 的顺序整个换（就地更新：没变化时 <see cref="Version"/> 不变）。</item>
/// <item><b>确认</b>：<see cref="MarkAcked"/> 在确认命令成功后先在本地标上（WS 回来的 ack 状态到了再以它为准），不等下一次推送。</item>
/// <item><b>计数</b>（样稿 ui.countedAlarms / alarmCounts / alarmText）：「报警」只数错误报警、「提醒」数其余；急停中的设备不数急停那一条
///   （<paramref name="estopActive"/>，各处已有红色「急停中」）；all = true 时和列出的卡片一一对应（设备页「当前报警」、检查器「报警」分区的标题）。</item>
/// </list>
/// 顺序：<see cref="Current"/> 真机按产生时刻新的在前（同一时刻按报警号大的在前）；演示按 DemoDevice.Alarms 的顺序（同样稿 dev.alarms）。
/// 页面自己的排法（设备页未确认在前、报警条按时刻）见 <see cref="AlarmTexts"/> / <see cref="AlarmStrip"/>。
/// 不是线程安全的：在 UI 线程上用（同 IDeviceSource）。
/// </summary>
public sealed class AlarmBook
{
    /// <summary>簿里最多保留的条数（含已解除；超出时先丢最旧的已解除、再丢最旧的已确认、最后才丢最旧的未确认）。</summary>
    public const int DefaultCapacity = 200;

    private readonly Dictionary<string, AlarmEntry> _byKey = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private AlarmEntry[] _all = Array.Empty<AlarmEntry>();
    private AlarmEntry[] _current = Array.Empty<AlarmEntry>();
    // 演示数据：保持给定的顺序（样稿 dev.alarms 的顺序）；真机：按时刻排
    private bool _keepOrder;

    public AlarmBook(string? device = null, int capacity = DefaultCapacity)
    {
        Device = device;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>这本簿属于哪台设备（写进每条的 <see cref="AlarmEntry.Device"/>；可为 null）。</summary>
    public string? Device { get; }

    /// <summary>真机的启动号（HMI bootId）；演示 / 还不知道为 null。</summary>
    public string? BootId { get; private set; }

    /// <summary>内容每变一次加一（视图模型据此跳过没变化的刷新）。</summary>
    public int Version { get; private set; }

    /// <summary>
    /// 当前报警（不含 info）。演示 = 样稿 dev.alarms（条件仍在，含已确认）。真机 = HMI 还没解除的（<see cref="AlarmEntry.Active"/>），并且：未确认；
    /// 或条件型（<see cref="AlarmEntry.Condition"/>，HMI 在条件消失时自己解除）；或急停那一条、设备仍在急停（<see cref="EstopActive"/>）。
    /// 没有条件的报警（HMI 的 Alarm.Log：程序手动停止、命令失败、模拟模式…）HMI 永远不会解除：确认了就算处理完，不再列出、不再计数（留在 <see cref="All"/>）。
    /// </summary>
    public IReadOnlyList<AlarmEntry> Current => _current;

    /// <summary>
    /// 设备正在急停（真机：最新一帧遥测的 Estop.Active，由数据源每帧给 <see cref="SetEstop"/>）。急停中已确认的急停报警照样列在 <see cref="Current"/> 里
    /// （样稿：急停那条要到复位才清掉；计数照旧不数它）。演示不用（演示的报警簿照 dev.alarms）。
    /// </summary>
    public bool EstopActive { get; private set; }

    /// <summary>簿里的全部（含已解除的，最多 <see cref="DefaultCapacity"/> 条），新的在前（演示 = <see cref="Current"/>）。</summary>
    public IReadOnlyList<AlarmEntry> All => _all;

    /// <summary>当前报警条数（错误 + 提醒，样稿 counts().alarms）。</summary>
    public int Count => _current.Length;

    /// <summary>未确认的条数（样稿 counts().unacked）。</summary>
    public int Unacked
    {
        get
        {
            int n = 0;
            foreach (var a in _current) if (!a.Acked) n++;
            return n;
        }
    }

    /// <summary>有未确认的错误报警（急停那条也算；设备页窄工作区默认打开「报警与事件」页签、页签上的红点）。</summary>
    public bool HasUnackedErr
    {
        get
        {
            foreach (var a in _current) if (a.IsErr && !a.Acked) return true;
            return false;
        }
    }

    /// <summary>当前报警里按确认号找（先找条件仍在的）；没有返回 null。</summary>
    public AlarmEntry? Find(string? id)
    {
        if (id is null) return null;
        foreach (var a in _current) if (a.Id == id) return a;
        foreach (var a in _all) if (a.Id == id) return a;
        return null;
    }

    // ———————————————————— 真机 ————————————————————

    /// <summary>
    /// GET /alarms 的结果：整个换掉（每次（重新）连上、HMI 重启后都取一次 —— 断线期间的变化与旧启动的报警都不留下）。
    /// <paramref name="bootId"/> = 这次连接的 HMI 启动号（DeviceInfoDto.BootId / HelloMsg.BootId）。返回内容是否变了。
    /// </summary>
    public bool Load(AlarmsDto? dto, string? bootId) => Load(dto?.Items ?? Array.Empty<AlarmDto>(), bootId);

    /// <inheritdoc cref="Load(AlarmsDto?, string?)"/>
    public bool Load(IEnumerable<AlarmDto> items, string? bootId)
    {
        var before = Snapshot();
        _byKey.Clear();
        _keepOrder = false;
        BootId = bootId;
        foreach (var dto in items) Upsert(dto);
        return Commit(before);
    }

    /// <summary>
    /// WS alarm 消息（新报警 / 确认 / 解除，HMI 每次都推整条）：按 bootId/id 更新那一条。bootId 和簿里的不同（HMI 重启了）时先清空。
    /// info 级的忽略。返回内容是否变了。
    /// </summary>
    public bool Apply(AlarmDto alarm, string? bootId)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        var before = Snapshot();
        if (_keepOrder) { _byKey.Clear(); _keepOrder = false; }
        if (bootId != null && BootId != null && bootId != BootId) _byKey.Clear();
        else if (bootId != null && BootId == null && _byKey.Count > 0) Rekey(bootId);
        if (bootId != null) BootId = bootId;
        Upsert(alarm);
        return Commit(before);
    }

    /// <inheritdoc cref="Apply(AlarmDto, string?)"/>
    public bool Apply(AlarmMsg message, string? bootId)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Apply(message.Alarm, bootId);
    }

    /// <summary>
    /// 确认命令成功后在本地先标上已确认（<paramref name="by"/> = 本机客户端名；HMI 没有确认人字段，A6）。已确认 / 找不到时什么也不做。
    /// 之后 WS 推来的 ack 状态照常覆盖（确认人保留本地这一份）。
    /// </summary>
    public bool MarkAcked(string id, string? by = null)
    {
        foreach (var kv in _byKey)
        {
            var a = kv.Value;
            if (a.Id != id || !a.Active || a.Acked) continue;
            var before = Snapshot();
            _byKey[kv.Key] = a with { Acked = true, AckBy = by ?? a.AckBy };
            return Commit(before);
        }
        return false;
    }

    /// <summary>
    /// 真机：设备的急停状态变了（每帧遥测给一次，没变时什么也不做）。影响已确认的急停报警是否还在 <see cref="Current"/> 里。返回内容是否变了。
    /// </summary>
    public bool SetEstop(bool active)
    {
        if (EstopActive == active) return false;
        EstopActive = active;
        if (_keepOrder) return false;                  // 演示：报警簿照 dev.alarms，不看这个
        return Commit(Snapshot());
    }

    /// <summary>清空（设备移除、换了地址；HMI 重启时由 <see cref="Apply(AlarmDto, string?)"/> / <see cref="Load(AlarmsDto?, string?)"/> 自己清）。</summary>
    public bool Clear()
    {
        var before = Snapshot();
        _byKey.Clear();
        BootId = null;
        return Commit(before);
    }

    /// <summary>真机报警归一：info 返回 null；其余按类注释的规则（本地时间、文字 = 标题 · 说明、反应釜号、推断的代码、确认 / 解除状态）。</summary>
    public static AlarmEntry? FromDto(AlarmDto a, string? bootId = null, string? device = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var level = LevelOf(a.Level);
        if (level == AlarmLevel.Info) return null;
        string state = (a.State ?? "").Trim().ToLowerInvariant();
        bool acked = state is "ack" or "done";
        bool active = state is not ("cleared" or "done") && a.ClearedAt is null;
        string title = a.Title ?? "", message = a.Message ?? "";
        string text = string.IsNullOrWhiteSpace(message) ? title : title + " · " + message;
        return new AlarmEntry(
            Key: (bootId ?? "-") + "/" + a.Id,
            Id: a.Id,
            Level: level,
            Ch: AlarmCodes.ChannelOf(a.Channel, title, a.Rv),
            Code: AlarmCodes.Infer(title, a.Rv),
            Text: text,
            At: Local(a.RaisedAt),
            Acked: acked,
            AckBy: null,
            Active: active,
            ClearedAt: a.ClearedAt is { } c ? Local(c) : null,
            Device: device,
            Condition: AlarmCodes.IsCondition(title, a.Rv));
    }

    /// <summary>'err' / 'error' → Err；'warn' / 'warning' → Warn；其余 Info。</summary>
    public static AlarmLevel LevelOf(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        "err" or "error" => AlarmLevel.Err,
        "warn" or "warning" => AlarmLevel.Warn,
        _ => AlarmLevel.Info,
    };

    /// <summary>先前不知道启动号时载入的报警（身份「-/id」）：知道启动号后改成「bootId/id」，WS 推来的同一条不会变成两条。</summary>
    private void Rekey(string bootId)
    {
        var old = _byKey.Values.ToArray();
        _byKey.Clear();
        foreach (var e in old)
        {
            var k = e.Key.StartsWith("-/", StringComparison.Ordinal) ? bootId + "/" + e.Id : e.Key;
            _byKey[k] = e with { Key = k };
        }
    }

    private void Upsert(AlarmDto dto)
    {
        var e = FromDto(dto, BootId, Device);
        if (e is null) return;
        // 本地先标上的确认人：HMI 的 ack 状态回来时不丢
        if (_byKey.TryGetValue(e.Key, out var old) && e.Acked && e.AckBy is null && old.AckBy != null) e = e with { AckBy = old.AckBy };
        _byKey[e.Key] = e;
    }

    // ———————————————————— 演示 ————————————————————

    /// <summary>演示数据（DemoDevice.Alarms，样稿 dev.alarms）：照给定的顺序整个换。返回内容是否变了。</summary>
    public bool Sync(IEnumerable<DemoAlarm> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return Sync(alarms.Select(a => FromDemo(a, Device)));
    }

    /// <summary>任意来源：照给定的顺序整个换成这些条目（Key 重复时后面的覆盖前面的）。返回内容是否变了。</summary>
    public bool Sync(IEnumerable<AlarmEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<AlarmEntry>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Level == AlarmLevel.Info) continue;
            if (seen.TryGetValue(e.Key, out int at)) list[at] = e;
            else { seen[e.Key] = list.Count; list.Add(e); }
        }
        var next = list.ToArray();
        var cur = next.Where(a => a.Active).ToArray();
        bool changed = !_keepOrder || !_all.SequenceEqual(next) || !_current.SequenceEqual(cur);
        _keepOrder = true;
        _byKey.Clear();
        foreach (var e in next) _byKey[e.Key] = e;
        BootId = null;
        if (!changed) return false;
        _all = next;
        _current = cur;
        Version++;
        return true;
    }

    /// <summary>演示报警 → 条目（Key = Id；演示的报警都是条件型、条件仍在的：样稿只在条件消失时把它清掉）。</summary>
    public static AlarmEntry FromDemo(DemoAlarm a, string? device = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new AlarmEntry(a.Id, a.Id, a.Level, a.Ch, a.Code, a.Text, a.At, a.Acked, a.AckBy, Active: true, ClearedAt: null, Device: device, Condition: true);
    }

    // ———————————————————— 计数（样稿 ui.countedAlarms / alarmCounts / alarmText） ————————————————————

    /// <summary>计入「报警 / 提醒」计数的报警：急停中的设备不数急停那一条；<paramref name="all"/> = true 全数（与列出的卡片一一对应）。</summary>
    public IEnumerable<AlarmEntry> Counted(bool estopActive, bool all = false) => AlarmTexts.Counted(_current, estopActive, all);

    /// <summary>错误报警（「报警」）/ 提醒 / 合计。</summary>
    public AlarmTally Counts(bool estopActive, bool all = false) => AlarmTexts.Counts(_current, estopActive, all);

    /// <summary>'报警 1 · 提醒 1' / '提醒 1' / <paramref name="none"/>。</summary>
    public string CountText(bool estopActive, bool all = false, string none = "") => AlarmTexts.CountText(_current, estopActive, all, none);

    /// <summary>未确认的错误报警的确认号（设备页窄工作区页签的自动切换看它）。</summary>
    public IReadOnlyList<string> UnackedErrIds() => _current.Where(a => a.IsErr && !a.Acked).Select(a => a.Id).ToArray();

    // ———————————————————— 内部 ————————————————————

    private (AlarmEntry[] All, AlarmEntry[] Current) Snapshot() => (_all, _current);

    private bool Commit((AlarmEntry[] All, AlarmEntry[] Current) before)
    {
        Trim();
        var all = _byKey.Values.ToList();
        all.Sort(NewestFirst);
        _all = all.ToArray();
        _current = _all.Where(IsCurrent).ToArray();
        bool changed = !before.All.SequenceEqual(_all) || !before.Current.SequenceEqual(_current);
        if (changed) Version++;
        return changed;
    }

    /// <summary>真机的当前报警（见 <see cref="Current"/>）：HMI 没解除，并且未确认 / 条件型 / 急停中的急停报警。</summary>
    private bool IsCurrent(AlarmEntry a) => a.Active && (!a.Acked || a.Condition || (a.IsEstop && EstopActive));

    /// <summary>超过上限时：先丢最旧的已解除，再丢最旧的已确认，最后丢最旧的未确认。</summary>
    private void Trim()
    {
        int over = _byKey.Count - _capacity;
        if (over <= 0) return;
        var victims = _byKey.Values
            .OrderBy(a => a.Active ? (a.Acked ? 1 : 2) : 0)
            .ThenBy(a => a.At)
            .ThenBy(a => NumericId(a.Id))
            .Take(over)
            .Select(a => a.Key)
            .ToArray();
        foreach (var k in victims) _byKey.Remove(k);
    }

    /// <summary>新的在前：时刻降序，同一时刻按报警号（数字）降序，再按 Key。</summary>
    private static int NewestFirst(AlarmEntry x, AlarmEntry y)
    {
        int c = y.At.CompareTo(x.At);
        if (c != 0) return c;
        c = NumericId(y.Id).CompareTo(NumericId(x.Id));
        return c != 0 ? c : string.CompareOrdinal(y.Key, x.Key);
    }

    private static long NumericId(string id) => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : -1;

    private static DateTime Local(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t.ToLocalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(),   // 线上时间都是 UTC（DTO 注释：时间为 UTC）
        _ => t,
    };
}
