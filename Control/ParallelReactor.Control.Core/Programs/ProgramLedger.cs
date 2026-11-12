using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 台账的一条：本机下发到第 <see cref="N"/> 路的程序（PUT /channels/{n}/program 成功之后记下）。
/// <list type="bullet">
/// <item>程序：名称（HMI 不保存程序名）、程序库 id、下发的原文 <see cref="DocJson"/>（ProgramDoc.ToJson()，psi）、它的 SHA-256 <see cref="Hash"/>、步骤数。</item>
/// <item>认得出 HMI 上跑的是不是它：HMI 启动号 <see cref="BootId"/>、下发时刻（PC 时钟 <see cref="DownloadedAt"/>；按时钟差换到设备时钟 <see cref="DownloadedAtDevice"/>）、
///   下发那一刻 HMI 上这一路的上一次执行（<see cref="PriorKnown"/> / <see cref="PriorStartedAt"/>：比它新的执行才可能是这个程序）、
///   本机客户端名 <see cref="Client"/>（startedBy「远程（客户端名）」）、下发命令的 opId / 事件序号。</item>
/// <item><see cref="Superseded"/>：不再是 HMI 上的程序了（被别的客户端替换、HMI 重启、跑起来的不是它…）的原因；null = 仍有效。作废的条目留着备查，不再匹配。</item>
/// <item>事件核对到哪里（<see cref="EventEpoch"/> / <see cref="CheckedSeq"/>，复核 R5）：别的客户端替换程序只能从 HMI 的事件看出来，序号 ≤ <see cref="CheckedSeq"/> 的事件都已经对这一条核对过；
///   重新连上时从这里续取（GET /events），取不全就作废（见 <see cref="ProgramLedger.ObservePage"/>）。</item>
/// <item>下发之后触屏端接管过控制（<see cref="LocalControl"/>，复核）：触屏可能改过程序，照样匹配但带说明、不再从这里启动。</item>
/// </list>
/// 时刻一律 UTC。
/// </summary>
public sealed record LedgerEntry
{
    public int N { get; init; }
    public string Name { get; init; } = "";
    public string? LibraryId { get; init; }
    public string DocJson { get; init; } = "";
    public string Hash { get; init; } = "";
    public int StepCount { get; init; }
    public DateTime DownloadedAt { get; init; }
    public DateTime? DownloadedAtDevice { get; init; }
    public string BootId { get; init; } = "";
    public string Client { get; init; } = "";
    public string? OpId { get; init; }
    public long? OpEventSeq { get; init; }
    public bool PriorKnown { get; init; }
    public DateTime? PriorStartedAt { get; init; }
    public string? Superseded { get; init; }
    /// <summary>下发时 HMI 事件的序号纪元（GET /device / WS hello 的 seqEpoch）；不知道为 null（不按纪元比）。</summary>
    public int? EventEpoch { get; init; }
    /// <summary>
    /// 已核对到的事件序号：序号 ≤ 它的事件都已经对这一条核对过（下发时 = 下发命令自己的事件序号 <see cref="OpEventSeq"/>，更早的事件比下发早，不算；
    /// 之后连续收到的事件逐条往前推）。重新连上时从这里续取；null = 不知道从哪里核对（重新连上时作废）。
    /// </summary>
    public long? CheckedSeq { get; init; }
    /// <summary>
    /// 下发之后 HMI 回到过本地控制（事件 control.released / takenBack / expired / mode，本机自己释放也算）：触屏从这时起能改草稿，而改草稿不记事件 ——
    /// 设备上的程序可能已经不是下发的那份。之后照样匹配（带说明，<see cref="LedgerMatch.AfterLocalControl"/>），但不再从这里启动（<see cref="ProgramLedger.CanRun"/> 为假，要重新下发）。
    /// 只有重新下发（新的条目）才清掉。
    /// </summary>
    public bool LocalControl { get; init; }
    /// <summary>回到本地控制的那条事件的序号（最早的一条）；不知道为 null。</summary>
    public long? LocalSinceSeq { get; init; }
    /// <summary>回到本地控制的时刻（设备时钟，那条事件的时刻）；不知道为 null = 比下发新的执行都算在它之后。</summary>
    public DateTime? LocalSinceAt { get; init; }

    public bool Valid => Superseded is null;

    /// <summary>下发的程序（名称 = <see cref="Name"/>）。</summary>
    public ProgramDoc Doc() => ProgramDoc.FromJson(DocJson, Name);
}

/// <summary>
/// 台账与 HMI 上这一路的程序对得上吗：没有（本机没下发过 / 已作废）、已下发还没跑、正在 / 刚跑过的就是它、对不上（原因见 <see cref="LedgerMatch.Reason"/>）、
/// 还在核对（<see cref="Checking"/>：连上之后离线期间的事件还没核对完，先不认，见 <see cref="ProgramLedger.BeginCheck"/>）。
/// </summary>
public enum LedgerMatchKind { NoEntry, Loaded, Matched, Mismatch, Checking }

/// <summary>
/// 匹配结果：种类、条目、对不上的原因（给提示 / 日志）、是不是在触屏上启动的（本机下发之后由触屏启动，规则允许，但界面可以说明）、
/// 是不是在触屏端接管过控制之后（<see cref="LedgerEntry.LocalControl"/>：之后开始的执行、还没跑的已下发程序 —— 设备上的程序可能已修改，界面写明）。
/// </summary>
public sealed record LedgerMatch(LedgerMatchKind Kind, LedgerEntry? Entry = null, string? Reason = null, bool StartedByTouch = false, bool AfterLocalControl = false)
{
    public static readonly LedgerMatch None = new(LedgerMatchKind.NoEntry);
    /// <summary>可以按台账显示（已下发待运行，或正在 / 刚跑过的就是它）。</summary>
    public bool Usable => Kind is LedgerMatchKind.Loaded or LedgerMatchKind.Matched;
}

/// <summary>
/// 程序台账（PORT-PLAN §3.2 第 4 条、§5 M5「数据」、§6 R5）：HMI 没有程序读取接口（A1 / A7 之前），PC 记下本机下发过的程序，
/// 用来在反应釜页、卡片、时间轴上显示程序名、步骤、剩余时间 —— <b>只在严格对得上时</b>；对不上就降级成「RVn 程序 · 第 k/n 步 · HMI 的消息」
/// （<see cref="HmiProgramText"/>），绝不把过期的草稿当成正在运行的程序显示。
/// <para><b>匹配规则</b>（<see cref="Match"/>，全部满足才算是它）：</para>
/// <list type="number">
/// <item>条目有效（没被作废）。</item>
/// <item>HMI 启动号相同（HMI 重启后程序与执行记录都不算数）。</item>
/// <item>这次执行比下发新：下发时看到过这一路的上一次执行时，startedAt 必须比它新；知道下发时刻（设备时钟）时，startedAt 还必须 ≥ 下发时刻 − <see cref="SkewToleranceSeconds"/>
///   （复核：下发时的帧可能是旧的 —— 刚在触屏上开始又结束的一次执行不能当成这次下发的）；两样都不知道 → 对不上。
///   startedAt 不比下发新 = 还没跑过 → <see cref="LedgerMatchKind.Loaded"/>「已下发 · 待运行」。
///   下发时刻先按时钟差估（App 按最近的帧估），看到下发命令自己的事件（opId / 序号对得上）就换成它的时刻（设备时钟，复核：hello 量的时钟差可能是几天前的）。</item>
/// <item>步骤数相同。</item>
/// <item>由本机远程启动（startedBy =「远程（本机客户端名）」或「远程（本机客户端名 · 令牌 …）」），或在本机下发之后由触屏启动（"local"）；别的客户端启动的不算。</item>
/// </list>
/// <para><b>作废</b>（<see cref="LedgerEntry.Superseded"/>，之后再也不匹配，要重新下发）：比下发新的执行对不上（步骤数不同、别的客户端启动）；HMI 重启；
/// 别的客户端替换 / 复制了程序（事件 op.program.replace / op.program.copy，<see cref="ObserveEvent"/>）。</para>
/// <para><b>触屏端接管过控制</b>（复核）：触屏改草稿不记事件；但只有本地控制时能改（远程控制中触屏的编辑一律被拒），这一路在执行程序、在运行时草稿也锁定。
/// 下发要持有控制权，所以下发之后 HMI 回到本地的事件（control.released / takenBack / expired / mode —— 不管是谁的租约、什么级别）就是触屏能改程序的开始：
/// 记在条目上（<see cref="LedgerEntry.LocalControl"/>，只留最早的，重新下发才清掉）。之后开始的执行照样按上面的规则匹配、按台账显示，但带说明
/// （<see cref="LedgerMatch.AfterLocalControl"/>，状态行「触屏接管过控制」）；之前开始的不受影响（那时控制权还在本机，触屏改不了）。
/// 「启动」「全部运行」不再运行它（<see cref="CanRun"/> 为假、<see cref="LocalControlReason"/>）：program/run 跑的是设备上那份，要重新下发。</para>
/// <para><b>事件要核对全</b>（复核 R5）：替换只能从事件看出来，而本机断开期间（本机重启、整条连接重建）的事件不会经 WS 补发。每个条目记着核对到哪个序号
/// （<see cref="LedgerEntry.CheckedSeq"/>，下发时 = 下发命令自己的事件序号，随台账保存）；连上时从最早的那个续取 GET /events 直到序号头（<see cref="CatchUpAfter"/> →
/// <see cref="ObservePage"/>，一页一页），中间缺了（设备上已经没有那些事件）、序号纪元变了、取不下来、太多取不完 → 落在后面的作废（<see cref="FailCatchUp"/>），绝不带着没核对过的条目继续显示。
/// 连上到核对完之间（<see cref="BeginCheck"/> … <see cref="EndCheck"/>，局域网上通常不到 1 s，最多 50 页）条目先不认（<see cref="LedgerMatchKind.Checking"/>）：
/// 不按台账显示、「启动」不运行它（复核：以前这段时间里还是「已下发 · 待运行」、<see cref="CanRun"/> 为真）。连着时实时事件跳号，只有落在后面的条目先不认（<see cref="BeginCheckBehind"/>）。
/// 下发命令的 PUT 回来之前 WS 已经送到的事件（最近的几十条留着）在记下下发时补核对（<see cref="AddDownload"/>，复核：以前下一条事件看起来像跳号）。</para>
/// <para><b>剩余的风险</b>（HMI 补 A1 之前识别不了）：触屏端接管过控制之后，有人在触屏上改了程序（步骤数不变）再启动 —— 这次执行照样按台账显示，只能写明：
/// 触屏启动的写「触屏启动 · 按本机下发的程序显示」（<see cref="LedgerMatch.StartedByTouch"/> / <see cref="ProgramRecord.StartedByTouch"/>）；
/// 本机启动的（检查器的「运行」照 M4 的决定照样能运行设备上的程序）写「触屏接管过控制」（<see cref="LedgerMatch.AfterLocalControl"/> / <see cref="ProgramRecord.AfterLocalControl"/>）。
/// 回到本地的事件本身漏掉时（续取不全）条目整个作废，不会漏标。</para>
/// <para><b>持久化</b>：&lt;配置目录&gt;/programs/&lt;设备 id&gt;.json（原子写：同目录临时文件 + 改名），连同步骤计时（<see cref="StepTracker"/>，按启动号）。
/// 截图 / 演示模式不传路径（<see cref="Persistent"/> = false），只在内存里，绝不写盘。</para>
/// 不是线程安全的：在 UI 线程上用。时刻一律 UTC（设备时钟的取自 HMI 的帧和 startedAt）。
/// </summary>
public sealed class ProgramLedger
{
    public const int FileVersion = 1;
    /// <summary>按下发时刻（设备时钟）比较执行的开始时，允许的时钟误差（秒）。</summary>
    public const double SkewToleranceSeconds = 2;

    /// <summary>事件核对不全时作废的原因（续取失败、页数太多、设备上已经没有那段事件）。</summary>
    public const string CatchUpFailedReason = "离线期间的事件没有全部取回，无法确认程序未被替换 · 请重新下发";
    /// <summary>不知道从哪个事件开始核对（下发时没拿到事件序号）的条目，重新连上时作废的原因。</summary>
    public const string UncheckedReason = "无法核对离线期间的事件，不能确认程序未被替换 · 请重新下发";
    /// <summary>HMI 的事件序号纪元变了（事件记录重置）时作废的原因。</summary>
    public const string EpochReason = "设备的事件记录已重置，无法确认程序未被替换 · 请重新下发";
    /// <summary>连上之后还在核对离线期间的事件（<see cref="LedgerMatchKind.Checking"/>）：降级说明与「启动」的原因。</summary>
    public const string CheckingReason = "正在核对离线期间的事件 · 稍候";
    /// <summary>下发之后触屏端接管过控制（<see cref="LedgerEntry.LocalControl"/>）：「启动」「全部运行」不运行它的原因（复核）。</summary>
    public const string LocalControlReason = "触屏端接管过控制，设备上的程序可能已修改 · 请重新下发";
    /// <summary>留着的最近事件条数（<see cref="AddDownload"/> 补核对用：PUT 一来一回之间 WS 送来的通常只有一两条）。</summary>
    public const int RecentEvents = 64;

    private readonly Dictionary<int, LedgerEntry> _entries = new();
    private readonly string? _path;
    /// <summary>有条目的核对位置往前推了、还没写盘（不是每条事件都写盘：落后的位置只会让下次多核对几条，不会漏）。</summary>
    private bool _cursorDirty;
    /// <summary>这次连上之后还没核对完离线期间事件的路（<see cref="BeginCheck"/> / <see cref="EndCheck"/>；不写盘 —— 每次连上都要重新核对）。</summary>
    private readonly HashSet<int> _checking = new();
    /// <summary>实时事件跳号时落在后面的路 → 要核对到的序号（跳号的那条；核对位置追上之前算落在后面，见 <see cref="Behind"/>）。</summary>
    private readonly Dictionary<int, long> _behindTo = new();
    /// <summary>最近看到的事件（按序号，同一个纪元；纪元变了清空）：新下发的条目补核对 PUT 回来之前已经收到的（<see cref="AddDownload"/>）。</summary>
    private readonly SortedList<long, SeenEvent> _recent = new();
    private int? _recentEpoch;
    /// <summary>台账文件读不了（IO / 权限，不是内容坏了）：文件不动，这次运行不写盘（不然空台账会盖掉它），直到读成功（<see cref="RetryLoad"/>）。</summary>
    private bool _loadFailed;

    /// <summary>台账看到过的一条事件（实时或续取的，有序号的才留）。</summary>
    private readonly record struct SeenEvent(long? Seq, int? Epoch, string? Kind, string? Message, string? Client, string? OpId, string? Level, DateTime? At);

    /// <param name="deviceId">设备的本机 id（配置里的 Guid）。</param>
    /// <param name="client">本机客户端名（startedBy 里的「远程（客户端名）」）。</param>
    /// <param name="filePath">台账文件；null = 只在内存里（截图 / 演示 / 测试）。</param>
    public ProgramLedger(string deviceId, string client, string? filePath = null)
    {
        DeviceId = deviceId ?? "";
        Client = client ?? "";
        _path = filePath;
        Tracker = new StepTracker();
    }

    /// <summary>台账文件的位置：&lt;配置目录&gt;/programs/&lt;设备 id&gt;.json。</summary>
    public static string PathFor(string configDir, Guid deviceId) => Path.Combine(configDir, "programs", deviceId.ToString("D") + ".json");

    /// <summary>
    /// 打开（有文件就读）一台设备的台账。内容坏了时从空的开始，坏文件改名为 .bad 留着；读不了（IO / 权限）时文件不动、这次运行不写盘（见 <see cref="Load"/>）。
    /// </summary>
    public static ProgramLedger Open(string configDir, Guid deviceId, string client)
    {
        var ledger = new ProgramLedger(deviceId.ToString("D"), client, PathFor(configDir, deviceId));
        ledger.Load();
        return ledger;
    }

    public string DeviceId { get; }
    /// <summary>本机客户端名（配置里改了客户端名时跟着改；已有条目按下发时的名字匹配）。</summary>
    public string Client { get; set; }
    /// <summary>有台账文件（会写盘）。</summary>
    public bool Persistent => _path is not null;
    public string? FilePath => _path;
    /// <summary>最后一次看到的 HMI 启动号。</summary>
    public string? BootId { get; private set; }
    /// <summary>步骤计时（与台账一起保存）。</summary>
    public StepTracker Tracker { get; }
    /// <summary>内容每变一次加一（界面据此刷新程序显示）。</summary>
    public int Version { get; private set; }
    /// <summary>最近一次保存失败的原因；没有为 null。</summary>
    public string? SaveError { get; private set; }
    /// <summary>读台账文件的问题（读不了 / 内容坏了，见 <see cref="Load"/>）；没有为 null。App 提示一次。</summary>
    public string? LoadError { get; private set; }
    /// <summary>台账文件读不了（IO / 权限）：这次运行不写盘，直到读成功（<see cref="RetryLoad"/>）。</summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>全部条目（含已作废的），按反应釜号。</summary>
    public IReadOnlyList<LedgerEntry> Entries => _entries.Values.OrderBy(e => e.N).ToArray();

    /// <summary>第 n 路的条目（含已作废的）；没有为 null。</summary>
    public LedgerEntry? Entry(int n) => _entries.TryGetValue(n, out var e) ? e : null;

    // ———————————————————— 记录下发 ————————————————————

    /// <summary>
    /// 记下一次成功的下发（PUT /channels/{n}/program 返回成功之后）。<paramref name="prior"/> = 下发之前最新一帧里这一路的 program（没有帧时 <paramref name="priorKnown"/> = false）；
    /// <paramref name="serverOffset"/> = 设备时钟 − 本机时钟（App 按最近的帧估，没有帧时用 WS hello 的；不知道为 null，就只能靠 prior 判断新旧）：设备时钟的下发时刻先按它估，
    /// 看到下发命令自己的事件（这里补核对时、或之后实时 / 续取时）就换成那条事件的时刻。返回新条目（同一路之前的条目被替换）。
    /// 事件核对从 <paramref name="opEventSeq"/>（下发命令自己的事件序号）开始；没有时从 <paramref name="eventCursor"/>（已经连续收到的最后一条事件，比下发早，多核对几条无妨）；
    /// <paramref name="eventEpoch"/> = 序号纪元。两个序号都没有时，这一条在重新连上时作废（核对不了，<see cref="UncheckedReason"/>）。
    /// 台账已经看过、比起点新的最近事件（PUT 回来之前 WS 就送到了）马上对新条目补核对一遍（复核：以前下一条实时事件看起来像跳号）。
    /// </summary>
    public LedgerEntry AddDownload(int n, ProgramDoc doc, string bootId, DateTime downloadedAtUtc, TimeSpan? serverOffset, ChannelProgramSnap? prior, bool priorKnown,
        string? opId = null, long? opEventSeq = null, string? libraryId = null, int? eventEpoch = null, long? eventCursor = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!string.IsNullOrEmpty(bootId)) ObserveBoot(bootId);   // 先认启动号（其它启动号的条目作废）
        string json = doc.ToJson();
        var at = Utc(downloadedAtUtc);
        var e = new LedgerEntry
        {
            N = n, Name = doc.Name, LibraryId = libraryId, DocJson = json, Hash = HashOf(json), StepCount = doc.Steps.Count,
            DownloadedAt = at, DownloadedAtDevice = serverOffset is { } off ? at + off : null, BootId = bootId ?? "", Client = Client,
            OpId = opId, OpEventSeq = opEventSeq, PriorKnown = priorKnown, PriorStartedAt = prior?.StartedAt is { } ps ? Utc(ps) : null,
            EventEpoch = eventEpoch, CheckedSeq = opEventSeq ?? (eventCursor is >= 0 ? eventCursor : null),
        };
        _entries[n] = e;
        _checking.Remove(n);   // 这次连接上刚下发的：从下发命令自己的事件起核对，不用等续取
        _behindTo.Remove(n);
        // PUT 回来之前 WS 已经送到、台账已经看过的事件（下发命令自己的那条、紧跟着的别的事件）：对新条目补核对 ——
        // 自己的那条给出设备时钟的下发时刻，之后的接着往前推核对位置（复核）。纪元不同的（缓冲是旧纪元的）不算
        foreach (var ev in _recent.Values.ToArray())
        {
            if (!_entries.TryGetValue(n, out var cur) || !cur.Valid) break;
            if (ev.Epoch is int ep && cur.EventEpoch is int eep && ep != eep) continue;
            Apply(cur, ev);
        }
        Changed();
        return _entries[n];
    }

    /// <summary>作废第 n 路的条目（原因写进条目）。已作废 / 没有时什么都不做。</summary>
    public void Supersede(int n, string reason)
    {
        if (!_entries.TryGetValue(n, out var e) || !e.Valid) return;
        _entries[n] = e with { Superseded = string.IsNullOrEmpty(reason) ? "已作废" : reason };
        Changed();
    }

    /// <summary>删掉第 n 路的条目。</summary>
    public void Forget(int n)
    {
        _checking.Remove(n);
        _behindTo.Remove(n);
        if (_entries.Remove(n)) Changed();
    }

    // ———————————————————— 观察 HMI ————————————————————

    /// <summary>
    /// 连上 / 重连（WS hello、GET /device）：启动号变了 = HMI 重启过 —— 其它启动号的有效条目全部作废、步骤计时清空。
    /// </summary>
    public void ObserveBoot(string bootId)
    {
        if (string.IsNullOrEmpty(bootId)) return;
        bool changed = false;
        if (BootId != bootId)
        {
            BootId = bootId;
            changed = true;
        }
        foreach (var e in _entries.Values.ToArray())
            if (e.Valid && e.BootId != bootId) { _entries[e.N] = e with { Superseded = "设备重启过（启动号不同）" }; changed = true; }
        if (Tracker.BootId != bootId) changed |= Tracker.Reset(bootId);
        if (changed) Changed();
    }

    /// <summary>
    /// 每一帧：记步骤计时；比下发新、又对不上的执行把条目作废（之后不再匹配）。只有记下的内容变了才写盘（换步、新的执行、结束、作废），不是每帧。
    /// 还不知道启动号（WS hello / GET /device 之前）时什么都不做 —— 不能拿空的启动号去判断、把条目都作废了。
    /// </summary>
    public void ObserveFrame(TelemetrySnapshot frame, string? bootId)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrEmpty(bootId)) return;
        if (bootId != BootId) ObserveBoot(bootId);
        bool changed = Tracker.Observe(bootId, frame);
        if (frame.Channels is not null)
            foreach (var ch in frame.Channels)
            {
                if (ch is null || !_entries.TryGetValue(ch.Id, out var e) || !e.Valid) continue;
                var m = Match(ch.Id, ch.Program, bootId);
                if (m.Kind == LedgerMatchKind.Mismatch)
                {
                    _entries[ch.Id] = e with { Superseded = m.Reason ?? "对不上" };
                    changed = true;
                }
            }
        if (changed) Changed();
    }

    private static readonly Regex ReplaceRv = new(@"RV(\d+)\s*程序已更新", RegexOptions.CultureInvariant);
    private static readonly Regex CopyFrom = new(@"已将\s*RV(\d+)\s*的", RegexOptions.CultureInvariant);

    /// <summary>
    /// 一条事件（WS event / GET /events 的一项：kind / 说明 / 客户端，能拿到时加 opId、时刻、序号与纪元）：别的客户端替换（op.program.replace，说明「RV3 程序已更新（5 步）」）
    /// 或复制（op.program.copy，「已将 RV2 的 5 步程序应用到 3 个通道」—— 目标看不出来，除了来源以外的条目全部作废）了程序。
    /// 本机自己的（opId 或客户端名对得上）不算。失败的命令（级别不是 info）不算。
    /// 下发之后 HMI 回到本地控制（<see cref="IsLocalReturn"/>，不看级别和是谁的）→ 标上 <see cref="LedgerEntry.LocalControl"/>（复核）。
    /// 下发命令自己的事件（opId / 序号对得上）→ 它的时刻成为设备时钟的下发时刻（复核）。
    /// <para>新旧先按<b>序号</b>判断（<paramref name="seq"/> / <paramref name="epoch"/>，条目的 <see cref="LedgerEntry.CheckedSeq"/>）：序号不大于核对位置的事件已经核对过 / 比下发早，不算；
    /// 正好是下一条时核对位置往前推一条（不立即写盘，见 <see cref="FlushCursors"/>）；跳号的（中间的还没收到）照样核对，但核对位置不动 —— 这一条落在后面，中间的由续取补（<see cref="Behind"/>）。
    /// 纪元与条目的不同（HMI 的事件记录重置过）→ 作废。</para>
    /// <para>没有序号（或条目不知道核对位置）时按<b>时刻</b>（<paramref name="atUtc"/>，设备时钟）：比条目的下发早（容差 <see cref="SkewToleranceSeconds"/>）的事件不作废它、也不算回到本地；
    /// 不知道时刻、或条目没有设备时钟的下发时刻时照旧作废、照旧算（宁可降级，R5）。</para>
    /// 有序号的事件留在最近事件里（<see cref="AddDownload"/> 补核对用）。
    /// </summary>
    public void ObserveEvent(string? kind, string? message, string? client, string? opId = null, string? level = "info", DateTime? atUtc = null,
        long? seq = null, int? epoch = null)
    {
        var ev = new SeenEvent(seq, epoch, kind, message, client, opId, level, Known(atUtc));
        Remember(ev);
        foreach (var e in _entries.Values.ToArray())
            if (e.Valid) Apply(e, ev);
    }

    /// <summary>实时的一条事件对一个有效条目（规则见 <see cref="ObserveEvent"/>）。</summary>
    private void Apply(LedgerEntry e, SeenEvent ev)
    {
        e = OwnTime(e, ev);
        if (ev.Seq is long s && e.CheckedSeq is long c)
        {
            if (ev.Epoch is int ep && e.EventEpoch is int eep && ep != eep) { Supersede(e.N, EpochReason); return; }
            if (s <= c) return;                                         // 核对过的 / 比下发早
            if (ReasonFor(e, ev.Kind, ev.Message, ev.Client, ev.OpId, ev.Level, null) is { } why) { Supersede(e.N, why); return; }
            if (s == c + 1) e = Advance(e, s);
            else if (!_behindTo.TryGetValue(e.N, out long t) || t < s) _behindTo[e.N] = s;   // 跳号：中间的还没核对（续取补上）
            if (IsLocalReturn(ev.Kind)) MarkLocal(e, s, ev.At);          // 在推过核对位置之后标：写盘的是推过的位置
            return;
        }
        bool older = ev.At is { } at && e.DownloadedAtDevice is { } dl && at < dl - TimeSpan.FromSeconds(SkewToleranceSeconds);
        if (!older && IsLocalReturn(ev.Kind)) e = MarkLocal(e, ev.Seq, ev.At);
        if (ReasonFor(e, ev.Kind, ev.Message, ev.Client, ev.OpId, ev.Level, ev.At) is { } r) Supersede(e.N, r);
    }

    /// <summary>
    /// HMI 回到本地控制的事件（Api/ControlService.OnTransition 记的：远程释放、触屏切回、宽限期满，以及被收回 —— 急停、同一客户端在宽限期内重新申请；
    /// 不认识的迁移也记成 control.mode）。之后触屏就能改程序草稿了（触屏的编辑只在本地控制时放行，改了不记事件）。
    /// </summary>
    public static bool IsLocalReturn(string? kind) => kind is "control.released" or "control.takenBack" or "control.expired" or "control.mode";

    /// <summary>
    /// 标上「下发之后触屏端接管过控制」（<see cref="LedgerEntry.LocalControl"/>，写盘）。只留最早的：已经标过、而这一条不比它早（序号更大或不知道）时不动。
    /// </summary>
    private LedgerEntry MarkLocal(LedgerEntry e, long? seq, DateTime? at)
    {
        if (e.LocalControl && !(seq is long s && e.LocalSinceSeq is long s0 && s < s0)) return e;
        var x = e with { LocalControl = true, LocalSinceSeq = seq, LocalSinceAt = at };
        _entries[e.N] = x;
        Changed();
        return x;
    }

    /// <summary>
    /// 下发命令自己的事件（op.program.replace，opId 或序号对得上）：它的时刻就是设备时钟的下发时刻，换掉按时钟差估的（写盘）——
    /// 复核：WS hello 量的时钟差可能是几天前的，两边的钟漂了几秒，本机刚启动的执行就会被当成下发之前的、整段一直显示「待运行」。
    /// </summary>
    private LedgerEntry OwnTime(LedgerEntry e, SeenEvent ev)
    {
        if (ev.At is not { } at || e.DownloadedAtDevice == at || ev.Kind is not (null or "op.program.replace")) return e;
        bool own = ev.OpId is not null && ev.OpId == e.OpId
            || ev.Seq is long s && s == e.OpEventSeq && (ev.Epoch is not int ep || e.EventEpoch is not int eep || ep == eep);
        if (!own) return e;
        var x = e with { DownloadedAtDevice = at };
        _entries[e.N] = x;
        Changed();
        return x;
    }

    /// <summary>记下一条有序号的事件（最近 <see cref="RecentEvents"/> 条，按序号；纪元变了先清空）。</summary>
    private void Remember(SeenEvent ev)
    {
        if (ev.Seq is not long s) return;
        if (ev.Epoch is int ep)
        {
            if (_recentEpoch is int re && re != ep) _recent.Clear();
            _recentEpoch = ep;
        }
        _recent[s] = ev;
        while (_recent.Count > RecentEvents) _recent.RemoveAt(0);
    }

    /// <summary>事件的时刻（UTC）；没给（默认值）为 null。</summary>
    private static DateTime? Known(DateTime? t) => t is { } v && v != default ? Utc(v) : null;

    /// <summary>
    /// 这一条事件会不会作废条目 <paramref name="e"/>（null = 不会）：别的客户端成功替换了它这一路（或看不出哪一路）、复制覆盖了它（来源那一路除外）。
    /// 本机自己的命令（opId 是本机某次下发的、或没有 opId 而客户端名是本机）不算。<paramref name="at"/> 不为 null 时按时刻排除下发之前的事件。
    /// </summary>
    private string? ReasonFor(LedgerEntry e, string? kind, string? message, string? client, string? opId, string? level, DateTime? at)
    {
        if (kind is not ("op.program.replace" or "op.program.copy")) return null;
        if (level is not null && level != "info" && level != "ok") return null;
        if (opId is not null && _entries.Values.Any(x => x.OpId == opId)) return null;
        if (opId is null && client is not null && client == Client) return null;
        if (at is { } t && e.DownloadedAtDevice is { } dl && t < dl - TimeSpan.FromSeconds(SkewToleranceSeconds)) return null;
        string who = string.IsNullOrEmpty(client) ? "其他客户端" : client;
        if (kind == "op.program.replace")
        {
            var m = ReplaceRv.Match(message ?? "");
            if (!m.Success) return "程序可能已被 " + who + " 替换";
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == e.N ? "程序已被 " + who + " 替换" : null;
        }
        var cm = CopyFrom.Match(message ?? "");
        int? from = cm.Success ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return e.N != from ? "程序可能已被 " + who + " 复制覆盖" : null;
    }

    /// <summary>核对位置往前推到 <paramref name="seq"/>（只在内存里；写盘见 <see cref="FlushCursors"/>，或随下一次内容变化一起写）。返回推过的条目。</summary>
    private LedgerEntry Advance(LedgerEntry e, long seq)
    {
        var x = e with { CheckedSeq = seq };
        _entries[e.N] = x;
        _cursorDirty = true;
        return x;
    }

    // ———————————————————— 事件核对（重新连上时续取） ————————————————————

    /// <summary>
    /// 实时事件跳号了：某个有效条目的核对位置落在后面（中间的事件还没核对，追上跳号的那条之前都算）。数据源据此再续取一次（<see cref="CatchUpAfter"/>），
    /// 落在后面的条目先不认（<see cref="BeginCheckBehind"/>）。没有落在后面的有效条目时为 false。
    /// </summary>
    public bool Behind
    {
        get
        {
            foreach (var (n, t) in _behindTo)
                if (_entries.TryGetValue(n, out var e) && e.Valid && e.CheckedSeq is long c && c < t) return true;
            return false;
        }
    }

    /// <summary>核对位置已经追上的（或作废了的）落后记录去掉。</summary>
    private void PruneBehind()
    {
        foreach (var (n, t) in _behindTo.ToArray())
            if (!_entries.TryGetValue(n, out var e) || !e.Valid || e.CheckedSeq is not long c || c >= t) _behindTo.Remove(n);
    }

    /// <summary>核对位置有没往前推、还没写盘的（<see cref="FlushCursors"/>）。</summary>
    public bool CursorDirty => _cursorDirty;

    /// <summary>
    /// 重新连上时要从哪个序号之后续取事件（GET /events?afterSeq=…）：有效条目里最早的核对位置；没有要核对的有效条目为 null。
    /// 不知道核对位置的有效条目先用 <see cref="SupersedeUnchecked"/> 作废。
    /// </summary>
    public long? CatchUpAfter
    {
        get
        {
            long? min = null;
            foreach (var e in _entries.Values)
                if (e.Valid && e.CheckedSeq is long c && (min is null || c < min)) min = c;
            return min;
        }
    }

    /// <summary>有效条目里有不知道核对位置的（下发时没拿到事件序号）：重新连上之后核对不了，作废（<see cref="UncheckedReason"/>）。返回作废了几条。</summary>
    public int SupersedeUnchecked()
    {
        int k = 0;
        foreach (var e in _entries.Values.ToArray())
            if (e.Valid && e.CheckedSeq is null) { Supersede(e.N, UncheckedReason); k++; }
        return k;
    }

    /// <summary>
    /// 续取的一页（GET /events?afterSeq=<paramref name="after"/>，<paramref name="after"/> = 请求时的 <see cref="CatchUpAfter"/>）：按序号逐条核对每个有效条目。
    /// <list type="bullet">
    /// <item>纪元与条目的不同 → 作废（<see cref="EpochReason"/>）。</item>
    /// <item>正好是条目的下一条 → 核对（会作废就作废，否则核对位置往前推；回到本地控制的另外标上 <see cref="LedgerEntry.LocalControl"/>）；
    ///   比下一条大 → 设备上已经没有中间那几条（页是从 after 起按序号连续给的）→ 作废（<see cref="CatchUpFailedReason"/>）。</item>
    /// <item>这一页之后：还有（more）、或序号头在这一页之后又有了新事件 → 返回 true，调用方接着取下一页；这一页什么也没有而序号头还在前面（中间的事件已经不在设备上）→ 作废，返回 false。</item>
    /// </list>
    /// 调用方要限制页数（取不完时 <see cref="FailCatchUp"/>）。续取结束（返回 false）时追上了的落后记录清掉（<see cref="Behind"/>）。
    /// </summary>
    public bool ObservePage(EventsPageDto page, long after)
    {
        ArgumentNullException.ThrowIfNull(page);
        foreach (var e in _entries.Values.ToArray())
            if (e.Valid && e.CheckedSeq is not null && e.EventEpoch is int ep && ep != page.SeqEpoch) Supersede(e.N, EpochReason);
        var items = page.Items ?? Array.Empty<ParallelReactor.Services.EventRecord>();
        foreach (var r in items.OrderBy(x => x.Seq))
        {
            if (r.SeqEpoch != page.SeqEpoch || r.Seq <= after) continue;
            var ev = new SeenEvent(r.Seq, r.SeqEpoch, r.Kind, EventText(r.Title, r.Message), r.Client, r.OpId, r.Level, Known(r.Ts));
            Remember(ev);
            foreach (var e0 in _entries.Values.ToArray())
            {
                if (!e0.Valid || e0.CheckedSeq is not long c) continue;
                var e = OwnTime(e0, ev);
                if (r.Seq <= c) continue;
                if (r.Seq > c + 1) { Supersede(e.N, CatchUpFailedReason); continue; }   // 设备上缺了 c + 1：核对不全
                if (ReasonFor(e, r.Kind, ev.Message, r.Client, r.OpId, r.Level, null) is { } why) { Supersede(e.N, why); continue; }
                e = Advance(e, r.Seq);
                if (IsLocalReturn(r.Kind)) MarkLocal(e, r.Seq, ev.At);
            }
        }
        if (CatchUpAfter is not long min) { _behindTo.Clear(); return false; }
        if (page.More) return true;
        if (items.Count > 0 && min < page.EventSeqHead) return true;          // 头在这一页之后又有了新事件：再取一页
        if (min < page.EventSeqHead)
        {
            // 这一页什么也没有，序号头却在前面：中间的事件已经不在设备上了（环形缓冲与落盘文件里都没有）
            foreach (var e in _entries.Values.ToArray())
                if (e.Valid && e.CheckedSeq is long c && c < page.EventSeqHead) Supersede(e.N, CatchUpFailedReason);
        }
        PruneBehind();
        return false;
    }

    /// <summary>
    /// 续取失败（请求出错、页数太多取不完）：<b>还落在后面的</b>有效条目作废（原因写进条目）—— 在核对中（连上之后、跳号之后）而核对位置没到这次续取取到过的序号头
    /// （<paramref name="head"/>：最后一页的 EventSeqHead；一页也没取到为 null，在核对中的都算没到），或实时跳号之后还没追上的。
    /// 这次连接上新下发、一直连续核对着的条目、已经核对到头的条目照样有效（复核：以前全部作废，包括刚下发的）。
    /// </summary>
    public void FailCatchUp(string? reason = null, long? head = null)
    {
        foreach (var e in _entries.Values.ToArray())
        {
            if (!e.Valid || e.CheckedSeq is not long c) continue;
            bool behind = _checking.Contains(e.N) && !(head is long h && c >= h) || _behindTo.TryGetValue(e.N, out long t) && c < t;
            if (behind) Supersede(e.N, string.IsNullOrEmpty(reason) ? CatchUpFailedReason : reason);
        }
        _behindTo.Clear();
    }

    /// <summary>核对位置往前推过、还没写盘时写一次（续取结束、定时、释放时调；没有台账文件时只清标记）。</summary>
    public void FlushCursors()
    {
        if (!_cursorDirty) return;
        _cursorDirty = false;
        Save();
    }

    /// <summary>事件的说明（HMI 把命令的结果写在 message 里：「RV3 程序已更新（5 步）」；没有时用标题）。</summary>
    public static string EventText(string? title, string? message) => string.IsNullOrEmpty(message) ? title ?? "" : message;

    // ———————————————————— 连上之后的核对窗口 ————————————————————

    /// <summary>
    /// （重新）连上了：中间的事件还没核对，此刻有效的条目先不认（<see cref="Match"/> = <see cref="LedgerMatchKind.Checking"/>：不按台账显示、<see cref="CanRun"/> 为假），
    /// 直到这次连接的续取做完（<see cref="EndCheck"/>）。之后在这次连接上新下发的条目不用等（<see cref="AddDownload"/>）。返回有没有条目进入核对。
    /// </summary>
    public bool BeginCheck()
    {
        bool any = false;
        foreach (var e in _entries.Values)
            if (e.Valid && _checking.Add(e.N)) any = true;
        if (any) Version++;
        return any;
    }

    /// <summary>
    /// 连着时实时事件跳号了（<see cref="Behind"/>）：只有落在后面的条目（核对位置在跳号的那条之前）先不认，直到续取补上（<see cref="EndCheck"/>）——
    /// 复核：以前全部有效条目都进核对，刚下发、一直连续核对着的也降级显示、「启动」禁用。返回有没有条目进入核对。
    /// </summary>
    public bool BeginCheckBehind()
    {
        bool any = false;
        foreach (var (n, t) in _behindTo)
            if (_entries.TryGetValue(n, out var e) && e.Valid && e.CheckedSeq is long c && c < t && _checking.Add(n)) any = true;
        if (any) Version++;
        return any;
    }

    /// <summary>这次连接的续取做完了（核对到序号头，或核对不了的已经作废）：条目照常匹配。返回有没有条目结束核对。</summary>
    public bool EndCheck()
    {
        if (_checking.Count == 0) return false;
        bool any = _checking.Any(n => _entries.TryGetValue(n, out var e) && e.Valid);
        _checking.Clear();
        Version++;
        return any;
    }

    /// <summary>第 n 路的条目还在等核对（有效、连上之后还没核对完）。</summary>
    public bool IsChecking(int n) => _checking.Contains(n) && _entries.TryGetValue(n, out var e) && e.Valid;

    /// <summary>有条目还在等核对。</summary>
    public bool Checking => _checking.Any(IsChecking);

    /// <summary>
    /// HMI 重启了、新的启动号还不知道（WS 的 ServerRestarted 先到，GET /device 之后才有新启动号）：全部有效条目作废、步骤计时清空。
    /// 之后 <see cref="ObserveBoot"/> 记下新的启动号。
    /// </summary>
    public void ObserveRestart()
    {
        bool changed = false;
        foreach (var e in _entries.Values.ToArray())
            if (e.Valid) { _entries[e.N] = e with { Superseded = "设备重启过（启动号不同）" }; changed = true; }
        BootId = null;
        _recent.Clear();   // 旧启动的事件不再给新下发的条目补核对
        changed |= Tracker.Reset(null);
        if (changed) Changed();
    }

    // ———————————————————— 匹配 ————————————————————

    /// <summary>第 n 路：台账与 HMI 报的 program（<paramref name="hmi"/>，没跑过为 null）对得上吗（规则见类说明）。纯函数，不改台账。</summary>
    public LedgerMatch Match(int n, ChannelProgramSnap? hmi, string? bootId)
    {
        if (!_entries.TryGetValue(n, out var e) || !e.Valid) return LedgerMatch.None;
        if (string.IsNullOrEmpty(bootId) || bootId != e.BootId) return new LedgerMatch(LedgerMatchKind.Mismatch, e, "设备重启过（启动号不同）");
        if (_checking.Contains(n)) return new LedgerMatch(LedgerMatchKind.Checking, e, CheckingReason);
        // 还没跑：触屏端接管过控制的已下发程序带说明（设备上的可能已修改）
        if (hmi?.StartedAt is not { } s0) return new LedgerMatch(LedgerMatchKind.Loaded, e, AfterLocalControl: e.LocalControl);
        var startedAt = Utc(s0);
        if (!e.PriorKnown && e.DownloadedAtDevice is null) return new LedgerMatch(LedgerMatchKind.Mismatch, e, "无法确认这次执行是在下发之后开始的");
        // 不比下发时看到的上一次执行新 / 比下发时刻（设备时钟）早 = 还是下发之前的那次执行：本机的程序还没跑（两样知道几样查几样 ——
        // 下发时的帧可能是旧的，刚在触屏上开始又结束的一次执行不比「上一次」旧，但比下发早）
        if (e.PriorKnown && e.PriorStartedAt is { } prior && startedAt <= prior) return new LedgerMatch(LedgerMatchKind.Loaded, e, AfterLocalControl: e.LocalControl);
        if (e.DownloadedAtDevice is { } dl && startedAt < dl - TimeSpan.FromSeconds(SkewToleranceSeconds))
            return new LedgerMatch(LedgerMatchKind.Loaded, e, AfterLocalControl: e.LocalControl);
        if (hmi.StepCount != e.StepCount)
            return new LedgerMatch(LedgerMatchKind.Mismatch, e, "步骤数不同（设备 " + hmi.StepCount + " 步 · 本机下发 " + e.StepCount + " 步）");
        // 触屏端接管过控制之后开始的执行：设备上的程序可能已修改 —— 照样按台账显示，带说明；之前开始的不受影响（那时控制权还在本机，触屏改不了）
        bool afterLocal = e.LocalControl && (e.LocalSinceAt is not { } ls || startedAt >= ls - TimeSpan.FromSeconds(SkewToleranceSeconds));
        var by = hmi.StartedBy;
        if (by == "local") return new LedgerMatch(LedgerMatchKind.Matched, e, null, StartedByTouch: true, AfterLocalControl: afterLocal);
        if (by is null) return new LedgerMatch(LedgerMatchKind.Mismatch, e, "不知道由谁启动");
        if (IsMine(by, e.Client)) return new LedgerMatch(LedgerMatchKind.Matched, e, AfterLocalControl: afterLocal);
        return new LedgerMatch(LedgerMatchKind.Mismatch, e, "由其他客户端启动（" + by + "）");
    }

    /// <summary>startedBy 是本机：「远程（客户端名）」或「远程（客户端名 · 令牌 …）」（HMI CommandSource.Label）。</summary>
    public static bool IsMine(string? startedBy, string client)
    {
        if (string.IsNullOrEmpty(startedBy) || string.IsNullOrEmpty(client)) return false;
        return startedBy == "远程（" + client + "）" || startedBy.StartsWith("远程（" + client + " · 令牌 ", StringComparison.Ordinal);
    }

    /// <summary>
    /// 「启动」「全部运行」可以运行本机下发的程序（D6）：第 n 路的条目有效、连上之后核对过、HMI 上就是它（已下发待运行，或跑过的就是它），
    /// 而且下发之后触屏端没有接管过控制（复核：接管过就可能改过程序，program/run 跑的是设备上那份 —— 要重新下发，<see cref="IsLocalControl"/>）。
    /// </summary>
    public bool CanRun(int n, ChannelProgramSnap? hmi, string? bootId) => Match(n, hmi, bootId) is { Usable: true, Entry.LocalControl: false };

    /// <summary>
    /// 第 n 路是本机下发、HMI 上就是它的程序，但下发之后触屏端接管过控制（<see cref="LedgerEntry.LocalControl"/>）：「启动」禁用并写 <see cref="LocalControlReason"/>
    /// （不运行它、也不改成启用 + 加热），「全部运行」不算它。
    /// </summary>
    public bool IsLocalControl(int n, ChannelProgramSnap? hmi, string? bootId) => Match(n, hmi, bootId) is { Usable: true, Entry.LocalControl: true };

    // ———————————————————— 程序记录 ————————————————————

    /// <summary>
    /// 第 n 路给页面的程序记录（与演示数据同一种 <see cref="ProgramRecord"/>，Progress / StepText 照常用）：
    /// 对得上时 = 台账的程序 + HMI 的状态 / 当前步 + 步骤计时 + 剩余时间估算（<see cref="EtaEstimator"/>）；已下发待运行 = Loaded；
    /// 对不上 / 没有条目 / 程序里有显示不了的步骤类型 → null（界面用 <see cref="HmiProgramText"/> 降级显示）。
    /// <paramref name="frame"/> = 最新一帧（估算的时刻 = 它的 Ts；断线时就是断线前最后一帧，Progress 停在 DataAt）；
    /// <paramref name="history"/>：这一路的曲线（实测升温 / 充压速率；没有就按 <paramref name="rates"/>）。记录里的时刻是本地时间（设备时钟，同 IDeviceSource.DataAt）。
    /// </summary>
    public ProgramRecord? ProgramOf(int n, TelemetrySnapshot? frame, string? bootId, ModelProfile? model, ChannelHistory? history = null, EtaRates? rates = null)
    {
        ChannelSnap? ch = null;
        if (frame?.Channels is { } list) foreach (var c in list) if (c?.Id == n) { ch = c; break; }
        var hmi = ch?.Program;
        var m = Match(n, hmi, bootId);
        if (!m.Usable || m.Entry is not { } e) return null;
        ProgramDoc doc;
        try { doc = e.Doc(); } catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
        var steps = ProgramDocs.ToSteps(doc, model);
        if (steps.Count != e.StepCount || steps.Count == 0) return null;   // 有显示不了的步骤类型：步骤序号对不上，不按台账显示
        var rec = new ProgramRecord
        {
            Id = e.LibraryId, Name = e.Name, Steps = steps, Source = ProgramSource.Ledger, StartedByTouch = m.StartedByTouch, AfterLocalControl = m.AfterLocalControl,
        };
        // 已下发 · 待运行（同演示数据的 Loaded：第 0 步、没有开始时刻）；计划时长按室温往后推
        if (m.Kind == LedgerMatchKind.Loaded)
            return rec with { Status = ProgramStatus.Loaded, StepIndex = 0, Steps = EtaEstimator.Plan(steps, ch?.TempC, Format.Units.PsiToMpa(ch?.PressurePsi), rates) };

        var run = Tracker.TrackOf(n, hmi, bootId);
        int idx = Math.Clamp(hmi!.StepIndex, 0, steps.Count - 1);
        var status = StatusOf(hmi);
        var startedAt = Utc(hmi.StartedAt!.Value);
        var log = new List<StepLogEntry>();
        if (run is not null)
        {
            int upto = status == ProgramStatus.Done ? steps.Count : idx;
            for (int i = 0; i < upto; i++)
            {
                var t0 = run.StartOf(i);
                DateTime? t1 = i + 1 < steps.Count ? run.StartOf(i + 1) : run.EndedAt;
                if (i + 1 == upto && status == ProgramStatus.Done) t1 = run.EndedAt;
                if (t0 is { } a && t1 is { } b && b >= a) log.Add(new StepLogEntry(i, a.ToLocalTime(), b.ToLocalTime()));
            }
        }
        rec = rec with
        {
            Status = status,
            StepIndex = status == ProgramStatus.Done ? steps.Count - 1 : idx,
            StartedAt = startedAt.ToLocalTime(),
            EndedAt = run?.EndedAt?.ToLocalTime(),
            Log = log.AsReadOnly(),
        };
        var now = frame!.Ts;
        var reading = new EtaReading(ch!.TempC, Format.Units.PsiToMpa(ch.PressurePsi),
            EtaEstimator.ObservedRate(history, HistoryField.TempC, now), EtaEstimator.ObservedRate(history, HistoryField.PressureMPa, now), ch.Profile);
        if (rec.IsActive)
            return EtaEstimator.Apply(rec, now, run?.StartOf(idx), run?.FirstSeenOf(idx), hmi.Message, reading, rates);
        // 结束 / 中止：当前步（中止在这一步）的开始知道就带上（时间轴的中止段）
        var done = EtaEstimator.Apply(rec, now, null, null, null, null, rates);
        return done with { StepStartedAt = run?.StartOf(idx)?.ToLocalTime() };
    }

    /// <summary>HMI 的 program.state → 程序状态：准备 / 运行 / 等待气路 / 收尾 = 运行中；completed = 已结束；failed = 报警中止；aborted = 已中止。</summary>
    public static ProgramStatus StatusOf(ChannelProgramSnap p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.State switch
        {
            ChannelProgramStates.Completed => ProgramStatus.Done,
            ChannelProgramStates.Failed => ProgramStatus.Failed,
            ChannelProgramStates.Aborted => ProgramStatus.Aborted,
            ChannelProgramStates.Preparing or ChannelProgramStates.Running or ChannelProgramStates.WaitingGas or ChannelProgramStates.Finishing => ProgramStatus.Running,
            _ => p.Running ? ProgramStatus.Running : ProgramStatus.Done,
        };
    }

    // ———————————————————— 保存 / 读取 ————————————————————

    /// <summary>台账的 JSON（条目 + 步骤计时）。</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            w.WriteString("deviceId", DeviceId);
            w.WriteString("client", Client);
            if (BootId is not null) w.WriteString("bootId", BootId);
            w.WriteStartArray("entries");
            foreach (var e in _entries.Values.OrderBy(x => x.N))
            {
                w.WriteStartObject();
                w.WriteNumber("n", e.N);
                w.WriteString("name", e.Name);
                if (e.LibraryId is not null) w.WriteString("libraryId", e.LibraryId);
                w.WritePropertyName("doc");
                using (var d = JsonDocument.Parse(e.DocJson)) d.RootElement.WriteTo(w);
                w.WriteString("hash", e.Hash);
                w.WriteNumber("stepCount", e.StepCount);
                w.WriteString("downloadedAt", e.DownloadedAt);
                if (e.DownloadedAtDevice is { } dd) w.WriteString("downloadedAtDevice", dd);
                w.WriteString("bootId", e.BootId);
                w.WriteString("client", e.Client);
                if (e.OpId is not null) w.WriteString("opId", e.OpId);
                if (e.OpEventSeq is { } seq) w.WriteNumber("opEventSeq", seq);
                w.WriteBoolean("priorKnown", e.PriorKnown);
                if (e.PriorStartedAt is { } pa) w.WriteString("priorStartedAt", pa);
                if (e.EventEpoch is { } ep) w.WriteNumber("eventEpoch", ep);
                if (e.CheckedSeq is { } cs) w.WriteNumber("checkedSeq", cs);
                // 触屏端接管过控制（复核新加的字段；没有这几个字段的旧文件照样读）
                if (e.LocalControl) w.WriteBoolean("localControl", true);
                if (e.LocalSinceSeq is { } lq) w.WriteNumber("localSinceSeq", lq);
                if (e.LocalSinceAt is { } la) w.WriteString("localSinceAt", la);
                if (e.Superseded is not null) w.WriteString("superseded", e.Superseded);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("runs");
            foreach (var r in Tracker.Runs.OrderBy(x => x.N))
            {
                w.WriteStartObject();
                w.WriteNumber("n", r.N);
                w.WriteString("bootId", r.BootId);
                w.WriteString("startedAt", r.StartedAt);
                w.WriteNumber("stepCount", r.StepCount);
                if (r.StartedBy is not null) w.WriteString("startedBy", r.StartedBy);
                w.WriteNumber("index", r.Index);
                w.WriteString("state", r.State);
                WriteTimes(w, "starts", r.Starts);
                WriteTimes(w, "firstSeen", r.FirstSeen);
                if (r.EndedAt is { } en) w.WriteString("endedAt", en);
                w.WriteString("lastSeen", r.LastSeen);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteTimes(Utf8JsonWriter w, string name, IReadOnlyList<DateTime?> times)
    {
        w.WriteStartArray(name);
        foreach (var t in times) { if (t is { } v) w.WriteStringValue(v); else w.WriteNullValue(); }
        w.WriteEndArray();
    }

    /// <summary>读台账 JSON（替换现有内容）。格式不对时抛 FormatException / JsonException（比本机新的版本：<see cref="NewerVersionException"/>）。</summary>
    public void LoadJson(string json)
    {
        using var d = JsonDocument.Parse(json);
        var root = d.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("台账不是 JSON 对象");
        int ver = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int vi) ? vi : 0;
        if (ver > FileVersion) throw new NewerVersionException("程序台账是更新版本的软件写的（版本 " + ver + "）");
        if (ver != FileVersion) throw new FormatException("台账版本 " + ver + " 不认识");
        _entries.Clear();
        _checking.Clear();
        BootId = Str(root, "bootId");
        if (root.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array)
            foreach (var e in es.EnumerateArray())
            {
                if (!e.TryGetProperty("doc", out var doc) || doc.ValueKind != JsonValueKind.Object) continue;
                string docJson = doc.GetRawText();
                var entry = new LedgerEntry
                {
                    N = e.GetProperty("n").GetInt32(), Name = Str(e, "name") ?? "", LibraryId = Str(e, "libraryId"), DocJson = Canonical(docJson),
                    Hash = Str(e, "hash") ?? "", StepCount = e.GetProperty("stepCount").GetInt32(), DownloadedAt = Time(e, "downloadedAt") ?? DateTime.MinValue,
                    DownloadedAtDevice = Time(e, "downloadedAtDevice"), BootId = Str(e, "bootId") ?? "", Client = Str(e, "client") ?? "", OpId = Str(e, "opId"),
                    OpEventSeq = e.TryGetProperty("opEventSeq", out var os) && os.TryGetInt64(out long ol) ? ol : null,
                    PriorKnown = e.TryGetProperty("priorKnown", out var pk) && pk.ValueKind == JsonValueKind.True, PriorStartedAt = Time(e, "priorStartedAt"),
                    Superseded = Str(e, "superseded"),
                    EventEpoch = e.TryGetProperty("eventEpoch", out var ee) && ee.TryGetInt32(out int ei) ? ei : null,
                    CheckedSeq = e.TryGetProperty("checkedSeq", out var cq) && cq.TryGetInt64(out long cl) ? cl : null,
                    LocalSinceSeq = e.TryGetProperty("localSinceSeq", out var lq) && lq.TryGetInt64(out long ll) ? ll : null,
                    LocalSinceAt = Time(e, "localSinceAt"),
                };
                // 接管过控制：标记本身，或（手改过的文件）只剩起点
                if (e.TryGetProperty("localControl", out var lc) && lc.ValueKind == JsonValueKind.True || entry.LocalSinceSeq is not null || entry.LocalSinceAt is not null)
                    entry = entry with { LocalControl = true };
                // 没记核对位置的旧条目：从下发命令自己的事件起核对（多核对几条无妨）
                if (entry.CheckedSeq is null && entry.OpEventSeq is long os2) entry = entry with { CheckedSeq = os2 };
                // 文件被改过（原文与摘要对不上）：不信任这条
                if (entry.Hash != HashOf(entry.DocJson)) entry = entry with { Superseded = entry.Superseded ?? "台账内容与摘要不符" };
                _entries[entry.N] = entry;
            }
        var runs = new List<RunTrack>();
        if (root.TryGetProperty("runs", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray())
                runs.Add(new RunTrack
                {
                    N = r.GetProperty("n").GetInt32(), BootId = Str(r, "bootId") ?? "", StartedAt = Time(r, "startedAt") ?? DateTime.MinValue,
                    StepCount = r.GetProperty("stepCount").GetInt32(), StartedBy = Str(r, "startedBy"), Index = r.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1,
                    State = Str(r, "state") ?? "", Starts = Times(r, "starts"), FirstSeen = Times(r, "firstSeen"), EndedAt = Time(r, "endedAt"),
                    LastSeen = Time(r, "lastSeen") ?? DateTime.MinValue,
                });
        Tracker.Restore(runs, BootId);
        Version++;
    }

    /// <summary>
    /// 从文件读（没有文件 = 空台账）。问题写在 <see cref="LoadError"/>（App 提示一次）：
    /// <list type="bullet">
    /// <item>内容坏了（JSON / 字段不对）：坏文件改名 .bad 留着（File.Move），从空的开始，之后照常写新文件。</item>
    /// <item>读不了（IO、权限）、或是更新版本的软件写的：文件可能是好的 —— 不动它，这次运行不写盘（<see cref="LoadFailed"/>：不然内存里的空台账会盖掉它），
    ///   直到读成功（<see cref="RetryLoad"/>）。复核：以前读不了也当成坏了，之后第一次写盘就用空台账悄悄盖掉了好文件。</item>
    /// </list>
    /// </summary>
    public void Load()
    {
        if (_path is null) return;
        string json;
        try
        {
            if (!File.Exists(_path)) { _loadFailed = false; LoadError = null; return; }
            json = File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LeaveFileAlone("程序台账读取失败：" + ex.Message);
            return;
        }
        try
        {
            LoadJson(json);
            _loadFailed = false;
            LoadError = null;
        }
        catch (NewerVersionException ex)
        {
            ClearAll();
            LeaveFileAlone(ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            ClearAll();
            try { File.Move(_path, _path + ".bad", overwrite: true); }
            catch (Exception mx) when (mx is IOException or UnauthorizedAccessException)
            {
                LeaveFileAlone("程序台账内容有误，也没能改名留存：" + mx.Message);
                return;
            }
            _loadFailed = false;
            LoadError = "程序台账内容有误（" + ex.Message + "）· 已改名为 .bad 留存，从空台账开始";
        }
    }

    /// <summary>
    /// 读不了（<see cref="LoadFailed"/>）之后再读一次（App 每次连上时）：只在内存里还什么都没记下时（没有条目、没有步骤计时）—— 读回来的会替换内存里的。返回读成功了没有。
    /// </summary>
    public bool RetryLoad()
    {
        if (!_loadFailed || _entries.Count > 0 || Tracker.Runs.Count > 0) return false;
        Load();
        return !_loadFailed;
    }

    /// <summary>台账文件不动、这次运行不写盘（原因写进 <see cref="LoadError"/>）。</summary>
    private void LeaveFileAlone(string why)
    {
        _loadFailed = true;
        LoadError = why + " · 本次不写台账文件";
    }

    /// <summary>读到一半失败：从空的开始（条目、核对状态、步骤计时）。</summary>
    private void ClearAll()
    {
        _entries.Clear();
        _checking.Clear();
        _behindTo.Clear();
        Tracker.Reset(null);
        Version++;
    }

    /// <summary>台账文件是更新版本的软件写的：不认识，但也不是坏了（不改名、不盖掉）。</summary>
    public sealed class NewerVersionException : FormatException
    {
        public NewerVersionException(string message) : base(message) { }
    }

    /// <summary>写盘（没有文件路径、或台账文件读不了时什么都不做，见 <see cref="Load"/>）。失败不抛，原因在 <see cref="SaveError"/>。</summary>
    public bool Save()
    {
        if (_path is null || _loadFailed) return false;
        try
        {
            WriteAtomic(_path, ToJson());
            SaveError = null;
            _cursorDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = "程序台账保存失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>原子写：同目录的临时文件写完、刷盘，再改名替换（同 ControlConfig.WriteAtomic；macOS / Linux 上 0600）。</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var fs = new FileStream(tmp, opts))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>程序原文的 SHA-256（小写十六进制）。</summary>
    public static string HashOf(string docJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(docJson ?? ""))).ToLowerInvariant();

    private void Changed()
    {
        Version++;
        Save();
    }

    /// <summary>读回来的 doc 再按 ProgramDoc 的写法写一遍（与下发时的原文逐字相同，摘要才对得上）。</summary>
    private static string Canonical(string docJson)
    {
        try { return ProgramDoc.FromJson(docJson).ToJson(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return docJson; }
    }

    private static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Time(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var t) ? Utc(t) : null;

    private static IReadOnlyList<DateTime?> Times(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var a) || a.ValueKind != JsonValueKind.Array) return Array.Empty<DateTime?>();
        return a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && x.TryGetDateTime(out var t) ? Utc(t) : (DateTime?)null).ToArray();
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

/// <summary>
/// 对不上台账时的降级显示（PORT-PLAN §3.1「程序与曲线」/ §6 R5）：只用 HMI 报的 k/n、状态与消息，不显示步骤、不估剩余时间。
/// 例：「RV3 程序 · 第 2/5 步 · 02/05 升压 · 充压 1.2 / 3.5 MPa」「RV3 程序 · 已结束 · 共 5 步」。
/// </summary>
public static class HmiProgramText
{
    /// <summary>程序名的位置：'RV3 程序'。</summary>
    public static string Name(int n) => "RV" + n.ToString(CultureInfo.InvariantCulture) + " 程序";

    /// <summary>
    /// 步骤与状态：运行中（含准备 / 收尾）「第 k/n 步」、等待气路「第 k/n 步 · 等待气路」、completed「已结束 · 共 n 步」、aborted「已中止 · 第 k/n 步」、
    /// failed「报警中止 · 第 k/n 步」。准备中（k = 0）按第 1 步。
    /// </summary>
    public static string Steps(ChannelProgramSnap p)
    {
        ArgumentNullException.ThrowIfNull(p);
        int n = Math.Max(0, p.StepCount);
        int k = Math.Clamp(p.StepIndex + 1, n > 0 ? 1 : 0, n);
        string kn = k + "/" + n;
        return p.State switch
        {
            ChannelProgramStates.Completed => "已结束 · 共 " + n + " 步",
            ChannelProgramStates.Aborted => "已中止 · 第 " + kn + " 步",
            ChannelProgramStates.Failed => "报警中止 · 第 " + kn + " 步",
            ChannelProgramStates.WaitingGas => "第 " + kn + " 步 · " + State.StateMap.WaitGasWord,
            _ => "第 " + kn + " 步",
        };
    }

    /// <summary>整行：名称 · 步骤与状态 · HMI 的消息（运行中且有消息时）。</summary>
    public static string Full(int n, ChannelProgramSnap p)
    {
        ArgumentNullException.ThrowIfNull(p);
        string s = Name(n) + " · " + Steps(p);
        bool active = p.State is ChannelProgramStates.Preparing or ChannelProgramStates.Running or ChannelProgramStates.WaitingGas or ChannelProgramStates.Finishing;
        return active && !string.IsNullOrWhiteSpace(p.Message) ? s + " · " + p.Message.Trim() : s;
    }
}
