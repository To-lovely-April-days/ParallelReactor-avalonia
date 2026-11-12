using System;
using System.Globalization;
using System.Text.RegularExpressions;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Alarms;

/// <summary>
/// 一条报警，页面看到的形状（样稿 API.md §3.1 alarm：id、level、ch、code、text、at、acked、ackBy），真机与演示数据共用（PORT-PLAN §3.2 第 7 条、§5 M4「数据」）。
/// <list type="bullet">
/// <item><see cref="Key"/>：身份（集合就地更新、去重用）。真机 = 「bootId/id」（HMI 的报警号每次启动从 1 计，不同启动的同号报警不会撞）；演示 = 'a1' …。</item>
/// <item><see cref="Id"/>：确认用的号（POST /alarms/{id}/ack；演示 alarmAck { id }）。</item>
/// <item><see cref="Level"/>：Err = 「报警」（红点）、Warn = 「提醒」（中性 + 描边 ⚠）；Info 不会出现在报警表里（AlarmBook 过滤掉）。</item>
/// <item><see cref="Ch"/>：反应釜号；设备级（急停、通讯、模拟模式…）为 null，页面写「整机」。</item>
/// <item><see cref="Code"/>：P-HIGH / T-HIGH / HEAT-SLOW / ESTOP / COMM …（真机按关键字推断，见 <see cref="AlarmCodes"/>）。</item>
/// <item><see cref="Text"/>：样稿措辞「RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11」（真机 = 标题 · 说明）。原文，断行保护（Keep）在显示时做。</item>
/// <item><see cref="At"/>：产生时刻（本地时间）。<see cref="Active"/>：HMI 还没解除（真机 state ∈ active / ack；cleared / done 为 false，带 <see cref="ClearedAt"/>）。</item>
/// <item><see cref="Condition"/>：条件型 —— 条件消失时由 HMI 自己解除（HMI 的 AlarmViewModel.Raise：超温、超压、模块离线；演示的报警都是这种）。
///   其余真机报警（Alarm.Log：程序手动停止、急停触发、命令失败、模拟模式…）HMI 永远不解除，确认了就算处理完（<see cref="AlarmBook.Current"/>）。</item>
/// </list>
/// 不可变：状态变了（确认、解除）就整条换掉（同一个 <see cref="Key"/>）。
/// </summary>
public sealed record AlarmEntry(
    string Key,
    string Id,
    AlarmLevel Level,
    int? Ch,
    string Code,
    string Text,
    DateTime At,
    bool Acked,
    string? AckBy = null,
    bool Active = true,
    DateTime? ClearedAt = null,
    string? Device = null,
    bool Condition = false)
{
    /// <summary>错误报警（「报警」）；否则是提醒。</summary>
    public bool IsErr => Level == AlarmLevel.Err;

    /// <summary>急停那一条（急停中的设备不计入「报警 / 提醒」计数，样稿 ui.countedAlarms）。</summary>
    public bool IsEstop => Code == AlarmCodes.Estop;

    /// <summary>'报警'（错误报警）/ '提醒'（其余，样稿 a.level === 'err' ? '报警' : '提醒'）。</summary>
    public string LevelWord => IsErr ? "报警" : "提醒";

    /// <summary>'RV11' / '整机'。</summary>
    public string Where => Ch is int n ? "RV" + n.ToString(CultureInfo.InvariantCulture) : "整机";

    /// <summary>'10:09:42'。</summary>
    public string AtText => Fmt.Time(At);

    /// <summary>设备页报警卡第二行（device.js alarmHTML meta）：'10:09:42 · 报警 · 未确认' / '… · 已确认（PC-测试工位1）'。</summary>
    public string Meta => AtText + " · " + LevelWord + " · " + (Acked ? "已确认" + (string.IsNullOrEmpty(AckBy) ? "" : "（" + AckBy + "）") : "未确认");

    /// <summary>那一行的悬停全文（device.js alarmHTML full）：meta + ' · RV11 · 代码 P-HIGH'。</summary>
    public string MetaFull => Meta + " · " + Where + " · 代码 " + Code;

    /// <summary>检查器报警卡底行（shell.js alarmCard）：'报警' / '提醒 · 已确认'。</summary>
    public string InspectorMeta => LevelWord + (Acked ? " · 已确认" : "");

    /// <summary>
    /// 去掉开头的「RVn 」（总览报警条：前面已经写了「3号机 · RV11」，overview.js alarmBarHTML）。没有反应釜号或不是这样开头时原样。
    /// </summary>
    public string TextAfterRv
    {
        get
        {
            if (Ch is not int n) return Text;
            string pre = "RV" + n.ToString(CultureInfo.InvariantCulture) + " ";
            return Text.StartsWith(pre, StringComparison.Ordinal) ? Text.Substring(pre.Length) : Text;
        }
    }

    /// <summary>
    /// 总览分区头报警小块悬停提示里的一段（overview.js alarmTip）：'报警：RV11 超压 …' / '提醒：RV3 升温较慢 …（已确认）'；
    /// 文字不是以「RVn」开头时在前面补上。
    /// </summary>
    public string TipPart => LevelWord + "：" + (Ch is int n && !Text.StartsWith("RV" + n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        ? "RV" + n.ToString(CultureInfo.InvariantCulture) + " " : "") + Text + (Acked ? "（已确认）" : "");
}

/// <summary>
/// 报警代码。样稿：P-HIGH（超压）、HEAT-SLOW（升温较慢）、ESTOP（急停已触发）。真机的 AlarmDto 没有代码（PORT-PLAN §3.3 A6），
/// 按标题 / 来源的关键字推断（参照 HMI 的 EventKinds.Derive 与 ViewModels 里报警的标题）。代码只用于：急停那条不计数、报警条不列急停、
/// 分区头「RV11 超压报警」、悬停提示「代码 …」。
/// </summary>
public static class AlarmCodes
{
    public const string PressureHigh = "P-HIGH";
    public const string TempHigh = "T-HIGH";
    public const string HeatSlow = "HEAT-SLOW";
    public const string Estop = "ESTOP";
    /// <summary>「急停已复位」（HMI 的提醒；不是急停中的那条，所以单独一个代码，不被排除在计数外）。</summary>
    public const string EstopReset = "ESTOP-RESET";
    public const string Comm = "COMM";
    public const string Program = "PROG";
    public const string Leak = "LEAK";
    public const string Other = "ALARM";

    /// <summary>
    /// 推断（顺序有意义）：急停已复位 → ESTOP-RESET；急停 → ESTOP；来源「通讯」或标题含 离线 / 掉线 / 通讯 → COMM；超压 → P-HIGH；
    /// 超温 → T-HIGH；升温较慢 / 升温慢 → HEAT-SLOW；程序 / 稳压 → PROG；泄漏 → LEAK；其余 ALARM。
    /// </summary>
    public static string Infer(string? title, string? origin = null)
    {
        string t = title ?? "";
        bool has(string s) => t.Contains(s, StringComparison.Ordinal);
        if (has("急停已复位")) return EstopReset;
        if (has("急停")) return Estop;
        if (origin == "通讯" || has("离线") || has("掉线") || has("通讯")) return Comm;
        if (has("超压")) return PressureHigh;
        if (has("超温")) return TempHigh;
        if (has("升温较慢") || has("升温慢")) return HeatSlow;
        if (has("程序") || has("稳压")) return Program;
        if (has("泄漏")) return Leak;
        return Other;
    }

    /// <summary>
    /// 真机报警是不是条件型（条件消失时 HMI 自己把它置为 cleared / done）。HMI 只对三类报警登记条件键（ViewModels/AlarmViewModel.Raise，
    /// 调用处都在 MainViewModel.Logging.cs）：超温 ot:N、超压 op / opv / oph:N、模块离线 comm:K；其余都是 Alarm.Log 记的、永远不会被解除。
    /// AlarmDto 没有种类字段（PORT-PLAN §3.3 A6），按来源与标题判断，规则同 HMI 的 EventKinds.Derive（Service/EventStore.cs）：
    /// 来源「通讯」且标题不含「恢复」（alarm.commLost）；标题含「超温」且不含「解除」「已确认」（alarm.overTemp…）；标题含「超压」且不含「解除」（alarm.overPressure）。
    /// 「超压已解除 · 联锁动作仍未确认」这类 Alarm.Log 的提醒因此不算条件型。
    /// </summary>
    public static bool IsCondition(string? title, string? origin = null)
    {
        string t = title ?? "";
        bool has(string s) => t.Contains(s, StringComparison.Ordinal);
        if (origin == "通讯") return !has("恢复");
        if (has("超温")) return !has("解除") && !has("已确认");
        if (has("超压")) return !has("解除");
        return false;
    }

    private static readonly Regex RvLead = new(@"^RV(\d+)(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex RvExact = new(@"^RV(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 反应釜号：HMI 给的 channel；没有时取标题开头的「RVn」（HMI 有的报警来源写「系统」、标题写「RV2 程序手动停止」），再没有取来源「RVn」。都没有为 null。
    /// </summary>
    public static int? ChannelOf(int? channel, string? title, string? origin = null)
    {
        if (channel is int c && c > 0) return c;
        if (title != null && RvLead.Match(title) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int a) && a > 0) return a;
        if (origin != null && RvExact.Match(origin) is { Success: true } o && int.TryParse(o.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int b) && b > 0) return b;
        return null;
    }
}
