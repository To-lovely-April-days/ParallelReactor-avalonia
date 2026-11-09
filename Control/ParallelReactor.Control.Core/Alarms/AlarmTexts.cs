using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Core.Alarms;

/// <summary>
/// 页面上的报警文字（样稿 ui.js countedAlarms / alarmCounts / alarmText，overview.js alarmChip / alarmTip，device.js logBody / unackSub / tabsPanel），
/// 逐字照样稿。输入是一台设备的当前报警（<see cref="AlarmBook.Current"/>，顺序 = 样稿 dev.alarms）。
/// </summary>
public static class AlarmTexts
{
    /// <summary>设备页「报警与事件」没有报警时（device.js logBody）。</summary>
    public const string NoAlarm = "当前没有报警";

    /// <summary>检查器「报警」分区没有报警时（shell.js inspDevice）。</summary>
    public const string InspectorNone = "没有报警";

    /// <summary>总览分区头报警小块悬停提示的结尾（overview.js alarmTip）。</summary>
    public const string ChipTipTail = " · 单击在检查器中查看";

    /// <summary>确认按钮的提示（可用时；device.js alarmHTML）。</summary>
    public const string AckTip = "确认报警不需要控制权，连接正常就可用";

    /// <summary>
    /// 设备页「报警与事件」可见时，它列出的未确认报警的「确认」在页面上（device.js ownsAlarms 的值）：检查器不再放第二个确认按钮，
    /// 卡片底行写「确认在「报警与事件」」。键 = 'k:id'（同 <see cref="AlarmStrip.OwnedKey"/>）。
    /// </summary>
    public const string LogOwnerName = "「报警与事件」";

    /// <summary>检查器「报警」分区最多列几条（shell.js inspDevice：d.alarms.slice(0, 4)）。</summary>
    public const int InspectorMax = 4;

    /// <summary>计入「报警 / 提醒」计数的报警（ui.countedAlarms）：急停中的设备不数急停那一条；all = true 全数。</summary>
    public static IEnumerable<AlarmEntry> Counted(IEnumerable<AlarmEntry> alarms, bool estopActive, bool all = false)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return alarms.Where(a => all || !(estopActive && a.IsEstop));
    }

    /// <summary>错误报警（「报警」，红）/ 提醒（中性）/ 合计（ui.alarmCounts）。</summary>
    public static AlarmTally Counts(IEnumerable<AlarmEntry> alarms, bool estopActive, bool all = false)
    {
        int err = 0, total = 0;
        foreach (var a in Counted(alarms, estopActive, all)) { total++; if (a.IsErr) err++; }
        return new AlarmTally(err, total - err, total);
    }

    /// <summary>'报警 1 · 提醒 1' / '提醒 1' / <paramref name="none"/>（ui.alarmText）。</summary>
    public static string CountText(IEnumerable<AlarmEntry> alarms, bool estopActive, bool all = false, string none = "") => CountText(Counts(alarms, estopActive, all), none);

    /// <summary>'报警 1 · 提醒 1' / '提醒 1' / <paramref name="none"/>。</summary>
    public static string CountText(AlarmTally c, string none = "")
    {
        string t = string.Join(" · ", new[] { c.Err > 0 ? "报警 " + N(c.Err) : "", c.Warn > 0 ? "提醒 " + N(c.Warn) : "" }.Where(x => x.Length > 0));
        return t.Length > 0 ? t : none;
    }

    // ———————————————————— 总览分区头（overview.js alarmChip / alarmTip） ————————————————————

    /// <summary>
    /// 分区头的报警 / 提醒小块；没有（计入计数的）报警时为 null。只有一条错误报警且在某个反应釜上时直接写是哪一个：
    /// 「RV11 超压报警」（P-HIGH）或「RVn + 该反应釜的状态字」（<paramref name="stateText"/>，例如「超温报警」；不给时写「报警」）；
    /// 否则「报警 n」。提醒写「提醒 n」。急停中的设备不数急停那一条（红色「急停中」另有徽标）。
    /// </summary>
    public static AlarmChip? Chip(IReadOnlyList<AlarmEntry> alarms, bool estopActive, Func<int, string>? stateText = null)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        var list = alarms.Where(a => !(estopActive && a.IsEstop)).ToList();
        if (list.Count == 0) return null;
        var errs = list.Where(a => a.IsErr).ToList();
        int nWarn = list.Count - errs.Count;
        var first = errs.Count > 0 ? errs[0] : list[0];
        string one = "";
        if (errs.Count == 1 && errs[0].Ch is int ch)
            one = "RV" + N(ch) + " " + (errs[0].Code == AlarmCodes.PressureHigh ? "超压报警" : stateText?.Invoke(ch) ?? "报警");
        string err = errs.Count > 0 ? (one.Length > 0 ? one : "报警 " + N(errs.Count)) : "";
        string warn = nWarn > 0 ? "提醒 " + N(nWarn) : "";
        return new AlarmChip(err, warn, errs.Count > 0, first.Ch, ChipTip(alarms));
    }

    /// <summary>
    /// 小块的悬停提示（overview.js alarmTip）：全部报警（急停那条也列）'报警：RV11 超压 …；提醒：RV3 升温较慢 …（已确认） · 单击在检查器中查看'。
    /// </summary>
    public static string ChipTip(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return string.Join("；", alarms.Select(a => a.TipPart)) + ChipTipTail;
    }

    // ———————————————————— 设备页「报警与事件」（device.js logBody / unackSub / tabsPanel） ————————————————————

    /// <summary>当前报警卡片的顺序：未确认在前 → 错误报警在前 → 新的在前（稳定排序）。</summary>
    public static IReadOnlyList<AlarmEntry> LogOrder(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return alarms.Select((a, i) => (a, i))
            .OrderBy(x => x.a.Acked ? 1 : 0)
            .ThenBy(x => x.a.IsErr ? 0 : 1)
            .ThenByDescending(x => x.a.At)
            .ThenBy(x => x.i)
            .Select(x => x.a)
            .ToArray();
    }

    /// <summary>小标题：'当前报警 / 提醒'（两种都有）/ '当前提醒'（只有提醒）/ '当前报警'（其余，含没有报警）。</summary>
    public static string LogHead(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        int nErr = alarms.Count(a => a.IsErr), nWarn = alarms.Count - nErr;
        return nErr > 0 && nWarn > 0 ? "当前报警 / 提醒" : nWarn > 0 ? "当前提醒" : "当前报警";
    }

    /// <summary>小标题右边的计数：'报警 1 · 提醒 1 · 未确认 2'（急停那条也算，和卡片一一对应）；没有报警为空串。</summary>
    public static string LogSub(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        if (alarms.Count == 0) return "";
        int un = alarms.Count(a => !a.Acked);
        return CountText(alarms, estopActive: false, all: true) + (un > 0 ? " · 未确认 " + N(un) : "");
    }

    /// <summary>面板标题栏的副标题：'2 条未确认'；没有未确认的为空串（device.js unackSub）。</summary>
    public static string UnackedText(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        int un = alarms.Count(a => !a.Acked);
        return un > 0 ? N(un) + " 条未确认" : "";
    }

    /// <summary>副标题前的红点：有未确认的错误报警（页签面板里不画：页签上已经有红点）。</summary>
    public static bool UnackedDot(IReadOnlyList<AlarmEntry> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        return alarms.Any(a => a.IsErr && !a.Acked);
    }

    /// <summary>
    /// 窄工作区「报警与事件」页签上的标记（device.js tabsPanel）：有未确认的错误报警 → 红点；否则有未确认的提醒、且这个页签不是当前页签 → 中性数字；
    /// 提示 'n 条未确认'。<paramref name="logTabOn"/> = 「报警与事件」是当前页签。
    /// </summary>
    public static LogTabBadge LogTab(IReadOnlyList<AlarmEntry> alarms, bool logTabOn)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        bool err = UnackedDot(alarms);
        int un = alarms.Count(a => !a.Acked);
        return new LogTabBadge(err, !err && un > 0 && !logTabOn ? un : 0, un > 0 ? N(un) + " 条未确认" : "");
    }

    /// <summary>检查器「报警」分区标题右边：'报警 1 · 提醒 1' / '无'（ui.alarmText(d, { all: true, none: '无' })）。</summary>
    public static string InspectorSub(IReadOnlyList<AlarmEntry> alarms) => CountText(alarms, estopActive: false, all: true, none: "无");

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 总览分区头的报警 / 提醒小块（overview.js alarmChip）：错误部分（'RV11 超压报警' / '报警 2'，前面一个红点）、提醒部分（'提醒 1'，前面一个描边 ⚠），
/// 两部分都有时中间一个「·」；<see cref="TargetCh"/> = 单击选中的反应釜（第一条错误报警所在，没有错误报警时第一条提醒；设备级为 null = 选中设备）。
/// </summary>
public sealed record AlarmChip(string ErrText, string WarnText, bool HasErr, int? TargetCh, string Tip)
{
    public bool HasWarn => WarnText.Length > 0;

    /// <summary>连起来的文字（同样稿 textContent：'RV4 超压报警·提醒 1'）。</summary>
    public string Text => ErrText + (ErrText.Length > 0 && WarnText.Length > 0 ? "·" : "") + WarnText;
}

/// <summary>「报警与事件」页签上的标记：红点（未确认的错误报警）/ 中性数字（只有未确认的提醒、页签不在前台）/ 提示。</summary>
public readonly record struct LogTabBadge(bool Dot, int Count, string Tip);

/// <summary>报警条的输入：一台设备（位置 k、别名）和它的当前报警。</summary>
public sealed record AlarmStripSource(int K, string Alias, IReadOnlyList<AlarmEntry> Alarms);

/// <summary>报警条上的一条：哪台设备 + 哪条报警。</summary>
public sealed record AlarmStripItem(int K, string Alias, AlarmEntry Alarm);

/// <summary>
/// 总览顶部的报警条（overview.js alarmItems / alarmBarHTML）：全部设备里未确认的错误报警（急停那条不列：各处已有红色「急停中」），
/// 按产生时刻新的在前（同一时刻保持设备顺序）；条上只显示第一条，其余写「另有 n 条」。
/// 什么时候显示（内容超出一屏）见 Layout.OverviewLayout.ShowAlarmStrip。
/// </summary>
public sealed class AlarmStrip
{
    private AlarmStrip(IReadOnlyList<AlarmStripItem> items) { Items = items; }

    public IReadOnlyList<AlarmStripItem> Items { get; }

    public static readonly AlarmStrip Empty = new(Array.Empty<AlarmStripItem>());

    /// <summary>按设备顺序收集、再按时刻排（稳定）。</summary>
    public static AlarmStrip Build(IEnumerable<AlarmStripSource> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var list = new List<AlarmStripItem>();
        foreach (var d in devices)
            foreach (var a in d.Alarms)
                if (a.IsErr && !a.Acked && !a.IsEstop) list.Add(new AlarmStripItem(d.K, d.Alias, a));
        var sorted = list.Select((x, i) => (x, i)).OrderByDescending(p => p.x.Alarm.At).ThenBy(p => p.i).Select(p => p.x).ToArray();
        return sorted.Length == 0 ? Empty : new AlarmStrip(sorted);
    }

    public bool Any => Items.Count > 0;

    /// <summary>条上显示的那一条（最新的）。</summary>
    public AlarmStripItem? First => Items.Count > 0 ? Items[0] : null;

    /// <summary>'3号机 · RV11' / '3号机'。</summary>
    public string Who => First is { } f ? f.Alias + (f.Alarm.Ch is int n ? " · RV" + n.ToString(CultureInfo.InvariantCulture) : "") : "";

    /// <summary>报警文字，去掉开头已经写在 <see cref="Who"/> 里的「RVn 」。</summary>
    public string Text => First?.Alarm.TextAfterRv ?? "";

    /// <summary>悬停全文。</summary>
    public string Title => First?.Alarm.Text ?? "";

    /// <summary>'10:09:42'。</summary>
    public string Time => First is { } f ? Fmt.Time(f.Alarm.At) : "";

    /// <summary>'另有 2 条'；只有一条时为空串。</summary>
    public string More => Items.Count > 1 ? "另有 " + (Items.Count - 1).ToString(CultureInfo.InvariantCulture) + " 条" : "";

    /// <summary>「定位」的目标（设备位置 + 反应釜号，设备级为 null）。</summary>
    public (int K, int? Ch)? Locate => First is { } f ? (f.K, f.Alarm.Ch) : null;

    /// <summary>
    /// 报警条上有「确认」的那条报警的键（样稿 ownsAlarms：'k:id' → '顶部报警条'；检查器据此不再放第二个确认按钮）。没有报警条时 null。
    /// </summary>
    public string? OwnedKey => First is { } f ? f.K.ToString(CultureInfo.InvariantCulture) + ":" + f.Alarm.Id : null;

    /// <summary>检查器里「确认在…」的位置名（样稿 ownsAlarms 的值）。</summary>
    public const string OwnerName = "顶部报警条";
}
