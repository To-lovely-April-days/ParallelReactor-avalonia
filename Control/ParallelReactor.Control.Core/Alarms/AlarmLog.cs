using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Alarms;

/// <summary>报警页一行的级别（样稿 ALOG.level：hi = 报警（红色实心圆）、lo = 提醒（琥珀三角）、info = 事件（灰色信息））。</summary>
public enum AlarmLogLevel { Hi, Lo, Info }

/// <summary>
/// 报警页一行的状态（样稿 ALOG.state）：Active 活动 / Cleared 已解除（报警）；Info 事件；
/// Logged = 事件簿里记着的报警 / 提醒，但报警簿里没有这一条（比本机连上更早、或 HMI 重启前的）—— 不知道解除与确认，只写「已记录」。
/// </summary>
public enum AlarmLogState { Active, Cleared, Info, Logged }

/// <summary>时间段（样稿 ALP.rng：今天 / 近 7 天；自定义在日期选择做好之前只提示）。</summary>
public enum AlarmLogRange { Day, Week }

/// <summary>级别筛选（样稿 ALP.lv：全部 / 报警 / 提醒 / 事件）。</summary>
public enum AlarmLogLevelFilter { All, Hi, Lo, Ev }

/// <summary>状态筛选（样稿 ALP.st：全部 / 未确认 / 活动 / 已解除）。</summary>
public enum AlarmLogStateFilter { All, Unacked, Active, Cleared }

/// <summary>报警页的筛选（样稿 ALP 去掉 sel）。</summary>
public sealed record AlarmLogFilter(
    AlarmLogRange Range = AlarmLogRange.Day,
    AlarmLogLevelFilter Level = AlarmLogLevelFilter.All,
    AlarmLogStateFilter State = AlarmLogStateFilter.All,
    string Query = "");

/// <summary>报警的读数（「值 / 限值」两行：「3.55 MPa」/「上限 3.50 MPa」）。</summary>
public readonly record struct AlarmReading(string? Val, string? Lim);

/// <summary>
/// 一台设备给报警页的数据：设备号、别名、型号、报警簿的全部条目（<see cref="AlarmBook.All"/>：活动 + 已解除）、事件簿（<see cref="EventBook.Items"/>）、
/// 报警文字里读不出数值时的读数（当前读数 + 设备的报警上限，由 App 按反应釜给）。
/// </summary>
public sealed record AlarmLogSource(int K, string Alias, string Model, IReadOnlyList<AlarmEntry> Alarms, IReadOnlyList<EventEntry> Events,
    Func<AlarmEntry, AlarmReading?>? Reading = null);

/// <summary>
/// 报警页的一行（样稿 ALOG 的一项：报警 + 事件合在一张表里）。<see cref="Key"/> = 设备号 + 报警簿 / 事件簿的身份（选中、就地更新用）。
/// <see cref="Title"/> = 报警名 / 事件标题（粗体）；<see cref="Act"/> = 第二行（设备已自动处理的 / 事件说明）；<see cref="Val"/> / <see cref="Lim"/> = 「值 / 限值」两行。
/// </summary>
public sealed record AlarmLogRow(
    string Key,
    int K,
    string Device,
    string Model,
    DateTime At,
    AlarmLogLevel Level,
    string Src,
    int? N,
    string Title,
    string Act,
    string? Val,
    string? Lim,
    AlarmLogState State,
    DateTime? ClearedAt,
    bool Acked,
    string? AckBy,
    string? AlarmId,
    string Code)
{
    /// <summary>报警或提醒（不是事件）。</summary>
    public bool IsAlarm => Level != AlarmLogLevel.Info;

    /// <summary>未确认（计入「未确认」、行首红竖条、行尾「确认」按钮）：报警簿里有、还没确认的报警 / 提醒。</summary>
    public bool Unacked => IsAlarm && (State is AlarmLogState.Active or AlarmLogState.Cleared) && !Acked;

    /// <summary>压力类（报警前后的小趋势画压力，否则画温度）。</summary>
    public bool IsPressure => Code == AlarmCodes.PressureHigh || Title.Contains("压", StringComparison.Ordinal);

    /// <summary>「报警」/「提醒」/「事件」。</summary>
    public string LevelWord => Level switch { AlarmLogLevel.Hi => "报警", AlarmLogLevel.Lo => "提醒", _ => "事件" };
}

/// <summary>四个指标（样稿 .alk：活动报警 / 未确认 / 提醒 / 今天的事件）。</summary>
public readonly record struct AlarmLogKpis(int ActiveHi, int Unacked, int ActiveLo, int Today);

/// <summary>
/// 报警页（融合版 F5，样稿 PAGES.alarm / ALOG / alRows）的数据：把各台设备的报警簿（活动 + 已解除）和事件簿合成一张按时间倒序的表，
/// 再按时间段 / 级别 / 状态 / 搜索筛选。纯函数，单元测试直接调。
/// <list type="bullet">
/// <item><b>报警</b>：报警簿的全部条目（<see cref="AlarmBook.All"/>）。文字 = 「报警名 · 说明」：报警名先认已知的名字（含「 · 」的「超温 · 切断加热」），否则取第一个「 · 」之前；
///   说明里「PV 205.3℃ ≥ 超温限值 200℃」「压力 3.55 MPa ≥ 超压报警值 3.50 MPa」这一段拆成「值 / 限值」（HMI 的写法），其余是第二行。读不出数值时用 <see cref="AlarmLogSource.Reading"/>。</item>
/// <item><b>事件</b>：事件簿里的信息 / 操作事件。来源 = HMI 的 origin，演示 = 文字开头的「控制权 · 」；反应釜事件 = RVn。
///   报警 / 提醒级的事件和报警簿里的同一条（同一路、相差 5 s 以内）重复，不再列；对不上的（比本机连上更早的报警）列成「已记录」。</item>
/// </list>
/// </summary>
public static class AlarmLog
{
    /// <summary>HMI 报警里自带「 · 」的报警名（ViewModels/MainViewModel.Logging.cs 的 Alarm.Raise 标题）：拆报警名时整段认。</summary>
    public static readonly IReadOnlyList<string> KnownTitles = new[]
    {
        "超温 · 切断加热", "超温切断后输出被重新打开 · 重新切断", "仍超温 · 重新置报警", "超温切断多次未确认 · 请使用急停",
        "超压 · 联锁动作", "超压联锁后阀门被重新打开 · 重新关闭", "超压联锁后加热输出被重新打开 · 重新切断", "仍超压 · 重新置报警",
        "超压联锁 · 关阀未确认", "超压联锁多次未确认 · 请使用急停", "超压联锁 · 停止加热未确认",
    };

    /// <summary>同一条报警在事件簿里的记录与报警簿相差多少秒以内算重复。</summary>
    public const double SameAlarmSeconds = 5;

    // 「PV 205.3℃ ≥ 超温限值 200℃」「压力 3.55 MPa ≥ 超压报警值 3.50 MPa」「205.3 °C ≥ 200 °C」
    private static readonly Regex ValueRe = new(
        @"^(?:[^\d−\-]*?)\s*(?<v>[−\-]?\d+(?:\.\d+)?)\s*(?<u>℃|°C|MPa|kPa|bar|psi|rpm|mmol|min|%)\s*(?:≥|>=|>|≤|<=|<)\s*[^\d−\-]*?(?<l>[−\-]?\d+(?:\.\d+)?)\s*(?<lu>℃|°C|MPa|kPa|bar|psi|rpm|mmol|min|%)?\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 报警文字 → (报警名, 说明)：先认 <paramref name="titles"/>（处理建议的键）与 <see cref="KnownTitles"/> 里最长的、文字以它开头（后面是结尾或「 · 」）的名字；
    /// 否则在第一个「 · 」处分开。
    /// </summary>
    public static (string Title, string More) SplitTitle(string text, IEnumerable<string>? titles = null)
    {
        text ??= "";
        string? best = null;
        foreach (var t in KnownTitles.Concat(titles ?? Array.Empty<string>()))
        {
            if (t.Length == 0 || !text.StartsWith(t, StringComparison.Ordinal)) continue;
            if (text.Length != t.Length && !text.AsSpan(t.Length).StartsWith(" · ", StringComparison.Ordinal)) continue;
            if (best == null || t.Length > best.Length) best = t;
        }
        if (best != null) return (best, text.Length > best.Length ? text[(best.Length + 3)..] : "");
        int i = text.IndexOf(" · ", StringComparison.Ordinal);
        return i > 0 ? (text[..i], text[(i + 3)..]) : (text, "");
    }

    /// <summary>
    /// 说明 → (值, 限值, 其余)：找到「读数 单位 ≥ 限值 单位」这一段时拆出来（「205.3 °C」/「上限 200 °C」，℃ 写成 °C），其余各段照原样用「 · 」连起来。
    /// 没有这一段时值 / 限值为 null、说明原样。
    /// </summary>
    public static (string? Val, string? Lim, string Other) SplitValue(string rest)
    {
        if (string.IsNullOrEmpty(rest)) return (null, null, "");
        var parts = rest.Split(" · ");
        for (int i = 0; i < parts.Length; i++)
        {
            var m = ValueRe.Match(parts[i].Trim());
            if (!m.Success) continue;
            string u = Unit(m.Groups["u"].Value), lu = m.Groups["lu"].Success && m.Groups["lu"].Length > 0 ? Unit(m.Groups["lu"].Value) : u;
            string val = Shown(m.Groups["v"].Value, u);
            string lim = "上限 " + Shown(m.Groups["l"].Value, lu);
            var others = parts.Where((_, j) => j != i).ToArray();
            return (val, lim, string.Join(" · ", others));
        }
        return (null, null, rest);
    }

    private static string Unit(string u) => u == "℃" ? "°C" : u;

    /// <summary>
    /// 「数 单位」的写法：压力（MPa / kPa / bar / psi）按设置的压力单位换算（<see cref="Format.PressureDisplay.FixedU"/>：MPa 2 位、bar 2 位、psi 1 位）；
    /// 设置是 MPa、或者原文已经是设置的单位时原样（与改单位之前逐字节相同）。其余单位原样。负号写成「−」。
    /// </summary>
    private static string Shown(string num, string unit)
    {
        var pu = Format.PressureDisplay.Primary;
        double? perMpa = unit switch { "MPa" => 1, "kPa" => 1000, "bar" => Format.PressureUnits.BarPerMpa, "psi" => Format.Units.PsiPerMpa, _ => null };
        if (perMpa is double k && pu != Format.PressureUnit.MPa && unit != Format.PressureDisplay.Unit
            && double.TryParse(num.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return Format.PressureDisplay.FixedU(v / k, 2).Replace('-', '−');
        return num.Replace('-', '−') + " " + unit;
    }

    /// <summary>报警来源（样稿 ALOG.src）：反应釜号 → RVn；通讯 → 通讯；急停 → 急停；名字里有搅拌 → 搅拌；泄漏 → 泄漏测试；其余整机。</summary>
    public static string SrcOf(AlarmEntry a)
    {
        if (a.Ch is int n) return "RV" + n.ToString(CultureInfo.InvariantCulture);
        if (a.Code == AlarmCodes.Comm) return "通讯";
        if (a.Code is AlarmCodes.Estop or AlarmCodes.EstopReset) return "急停";
        if (a.Text.Contains("搅拌", StringComparison.Ordinal)) return "搅拌";
        if (a.Code == AlarmCodes.Leak) return "泄漏测试";
        return "整机";
    }

    /// <summary>一条报警 → 行。</summary>
    public static AlarmLogRow FromAlarm(AlarmLogSource s, AlarmEntry a, IEnumerable<string>? titles = null)
    {
        var (title, rest) = SplitTitle(a.Text, titles);
        var (val, lim, act) = SplitValue(rest);
        if (val == null && s.Reading?.Invoke(a) is { } r) { val = r.Val; lim = r.Lim; }
        var state = a.Active ? AlarmLogState.Active : AlarmLogState.Cleared;
        return new AlarmLogRow("A" + s.K.ToString(CultureInfo.InvariantCulture) + ":" + a.Key, s.K, s.Alias, s.Model, a.At,
            a.IsErr ? AlarmLogLevel.Hi : AlarmLogLevel.Lo, SrcOf(a), a.Ch, title, act, val, lim, state, a.ClearedAt, a.Acked, a.AckBy, a.Id, a.Code);
    }

    /// <summary>
    /// 一条事件 → 行：真机用 HMI 的标题 / 说明 / 来源；演示（只有文字）—— 反应釜事件去掉开头的「RVn」，整机事件「来源 · 标题」拆开。
    /// </summary>
    public static AlarmLogRow FromEvent(AlarmLogSource s, EventEntry e)
    {
        string src, title, act;
        if (e.Title is { Length: > 0 } t)
        {
            src = e.Origin is { Length: > 0 } o ? o : e.RvText;
            title = t;
            act = e.Message ?? "";
            if (e.N is int n && (src == "系统" || src.Length == 0)) src = "RV" + n.ToString(CultureInfo.InvariantCulture);
        }
        else if (e.N is int)
        {
            src = e.RvText;
            title = e.ShortText;
            act = "";
        }
        else
        {
            int i = e.Text.IndexOf(" · ", StringComparison.Ordinal);
            (src, title) = i > 0 ? (e.Text[..i], e.Text[(i + 3)..]) : ("整机", e.Text);
            act = "";
        }
        var level = e.Level switch { EventLevel.Err => AlarmLogLevel.Hi, EventLevel.Warn => AlarmLogLevel.Lo, _ => AlarmLogLevel.Info };
        return new AlarmLogRow("E" + s.K.ToString(CultureInfo.InvariantCulture) + ":" + e.Key, s.K, s.Alias, s.Model, e.At, level, src, e.N, title, act,
            null, null, level == AlarmLogLevel.Info ? AlarmLogState.Info : AlarmLogState.Logged, null, false, null, null, "");
    }

    /// <summary>全部设备的行（时间倒序；同一时刻按设备号、再按报警在前）。</summary>
    public static IReadOnlyList<AlarmLogRow> Build(IEnumerable<AlarmLogSource> sources, IEnumerable<string>? titles = null)
    {
        var tl = titles?.ToArray();
        var rows = new List<AlarmLogRow>();
        foreach (var s in sources)
        {
            foreach (var a in s.Alarms) rows.Add(FromAlarm(s, a, tl));
            foreach (var e in s.Events)
            {
                if ((e.Level is EventLevel.Err or EventLevel.Warn) && s.Alarms.Any(a => a.Ch == e.N && Math.Abs((a.At - e.At).TotalSeconds) <= SameAlarmSeconds)) continue;
                rows.Add(FromEvent(s, e));
            }
        }
        return rows.OrderByDescending(r => r.At).ThenBy(r => r.K).ThenBy(r => r.IsAlarm ? 0 : 1).ToList();
    }

    /// <summary>同一天（本地日期）。</summary>
    public static bool IsToday(DateTime at, DateTime now) => at.Date == now.Date;

    /// <summary>筛选（样稿 alRows）：设备（<paramref name="k"/> = 0 全部）· 时间段 · 级别 · 状态 · 搜索（报警名 + 来源 + 设备名，不分大小写）。</summary>
    public static IReadOnlyList<AlarmLogRow> Filter(IEnumerable<AlarmLogRow> rows, int k, AlarmLogFilter f, DateTime now)
    {
        string q = (f.Query ?? "").Trim();
        return rows.Where(r =>
                (k == 0 || r.K == k)
                && (f.Range == AlarmLogRange.Day ? IsToday(r.At, now) : r.At.Date > now.Date.AddDays(-7))
                && f.Level switch
                {
                    AlarmLogLevelFilter.Hi => r.Level == AlarmLogLevel.Hi,
                    AlarmLogLevelFilter.Lo => r.Level == AlarmLogLevel.Lo,
                    AlarmLogLevelFilter.Ev => r.Level == AlarmLogLevel.Info,
                    _ => true,
                }
                && f.State switch
                {
                    AlarmLogStateFilter.Unacked => r.Unacked,
                    AlarmLogStateFilter.Active => r.State == AlarmLogState.Active,
                    AlarmLogStateFilter.Cleared => r.State == AlarmLogState.Cleared,
                    _ => true,
                }
                && (q.Length == 0 || (r.Title + r.Src + r.Device).Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>四个指标（样稿 render 的 act / ua / 今天）：只按设备筛选，不看其余筛选。</summary>
    public static AlarmLogKpis Kpis(IEnumerable<AlarmLogRow> rows, int k, DateTime now)
    {
        int hi = 0, ua = 0, lo = 0, today = 0;
        foreach (var r in rows)
        {
            if (k != 0 && r.K != k) continue;
            if (r.State == AlarmLogState.Active && r.Level == AlarmLogLevel.Hi) hi++;
            if (r.State == AlarmLogState.Active && r.Level == AlarmLogLevel.Lo) lo++;
            if (r.Unacked) ua++;
            if (IsToday(r.At, now)) today++;
        }
        return new AlarmLogKpis(hi, ua, lo, today);
    }

    /// <summary>「14:03:18」（今天）/「昨天 16:47:20」/「11-12 09:30:00」（样稿时间列：今天不写日期）。</summary>
    public static string TimeText(DateTime at, DateTime now)
    {
        string t = at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (IsToday(at, now)) return t;
        if (at.Date == now.Date.AddDays(-1)) return "昨天 " + t;
        return at.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + t;
    }

    /// <summary>详情「发生」：「今天 14:03:18」/「昨天 16:47:20」/「2026-11-12 09:30:00」。</summary>
    public static string WhenText(DateTime at, DateTime now)
    {
        string t = at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (IsToday(at, now)) return "今天 " + t;
        if (at.Date == now.Date.AddDays(-1)) return "昨天 " + t;
        return at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + t;
    }

    /// <summary>状态列第一行：「活动」/「已解除 11:24:52」/「事件」/「已记录」。</summary>
    public static string StateText(AlarmLogRow r) => r.State switch
    {
        AlarmLogState.Active => "活动",
        AlarmLogState.Cleared => "已解除" + (r.ClearedAt is { } c ? " " + c.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : ""),
        AlarmLogState.Logged => "已记录",
        _ => "事件",
    };

    /// <summary>状态列第二行：「未确认」/「已确认 · PC-测试工位1」/（已记录）「设备事件记录」/ 事件为空。</summary>
    public static string AckText(AlarmLogRow r) => r.State switch
    {
        AlarmLogState.Info => "",
        AlarmLogState.Logged => "设备事件记录",
        _ => r.Acked ? "已确认" + (string.IsNullOrEmpty(r.AckBy) ? "" : " · " + r.AckBy) : "未确认",
    };

    /// <summary>
    /// 导出 CSV（当前筛选的行，UTF-8 带 BOM 由调用方写）：日期, 时间, 设备, 型号, 级别, 来源, 内容, 说明, 值, 限值, 状态, 解除时刻, 确认。
    /// 含逗号 / 引号 / 换行的字段加引号。
    /// </summary>
    public static string Csv(IEnumerable<AlarmLogRow> rows)
    {
        var sb = new StringBuilder("日期,时间,设备,型号,级别,来源,内容,说明,值,限值,状态,解除时刻,确认\n");
        foreach (var r in rows)
        {
            string[] f =
            {
                r.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), r.Device, r.Model, r.LevelWord,
                r.Src, r.Title, r.Act, r.Val ?? "", r.Lim ?? "",
                r.State switch { AlarmLogState.Active => "活动", AlarmLogState.Cleared => "已解除", AlarmLogState.Logged => "已记录", _ => "事件" },
                r.ClearedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                r.IsAlarm && r.State != AlarmLogState.Logged ? (r.Acked ? "已确认" + (string.IsNullOrEmpty(r.AckBy) ? "" : "（" + r.AckBy + "）") : "未确认") : "",
            };
            sb.AppendJoin(',', f.Select(Esc)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>CSV 字段转义。</summary>
    public static string Esc(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
