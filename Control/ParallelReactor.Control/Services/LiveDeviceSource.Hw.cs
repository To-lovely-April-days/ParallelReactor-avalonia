using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Guard;
using AlarmDto = ParallelReactor.Services.AlarmDto;
using FleetActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Services;

/// <summary>
/// LiveDeviceSource 的两种提示（M6；样稿都没有，是真机才有的）：
/// <list type="bullet">
/// <item><b>硬件未确认之后的最终结果</b>：HMI 对 2 s 内硬件没确认的命令先回 202 + warnings [HW_UNCONFIRMED]（界面提示一条「1号机 · RV1 停止 · 硬件未确认：…」，
///   见 Fleet/DeviceActions），之后硬件做完再经 WS 推一条同 opId 的 opEvent「&lt;action&gt;.result」（REPO Api/Endpoints.Write.cs：最终的状态码、代码、消息）。
///   这里记下这些 opId，结果来了再提示一条：成功「1号机 · RV1 停止 · 硬件已执行完」（info），失败「1号机 · RV1 停止 · 硬件执行失败：…」（err）。
///   成功的说法是中性的：这条事件不带最终结果的 warnings（例如停止时关阀失败的后续提示只在 HTTP 正文里）—— 不能说「已确认」、也不用 ✓；
///   硬件出的问题 HMI 另记报警（「SV1 关闭失败」），照常提示出现的报警（见下）。要拿到 warnings 得用同一个 Idempotency-Key 再发一次请求（HMI 重放存着的正文），
///   幂等表的条目被挤掉（10 min / 1000 条）时那次请求会真的再执行一遍 —— 不做。
///   一次操作发了几条命令（真机的「全部运行」逐个反应釜）等全部到齐合成一条；同一台设备上一起等着的几次操作（例如逐个停止几个反应釜）也合成一条
///   「1号机 · RV1、RV3 停止 · 硬件已执行完」—— 先到的最多再等 <see cref="HwHold"/>，在每秒的 Tick 里放出去（所以实际最多约 2 s）。
///   WS 的结果可能比 HTTP 的 202 先处理完：先到的结果留 <see cref="HwEarlyKeep"/>，登记时直接算（全部已经到了就不再说「未确认」，见 <see cref="AwaitHardware"/>）。
///   等了 <see cref="HwWaitMax"/> 还没结果（WS 断过：HMI 不补发 opEvent）、HMI 重启、断开时不再等，也不提示（「未确认」那一条已经叫用户去核对）。</item>
/// <item><b>报警出现</b>（<see cref="OnAlarm"/>）：只在一条报警出现（active、本机还没见过）时提示一次，带设备名 ——「3号机 · 报警 RV11：超压」（err）/
///   「3号机 · 提醒 RV3：…」（warn）；同一条的确认、解除不再提示（M6：以前每条 WS 报警消息都提示，确认 / 解除错误报警时又红一次，也不写是哪台设备）。</item>
/// </list>
/// </summary>
public sealed partial class LiveDeviceSource
{
    /// <summary>最终结果最多等这么久（硬件的尾部一般几秒；WS 断过、HMI 重启时结果不会再来）。</summary>
    public static readonly TimeSpan HwWaitMax = TimeSpan.FromMinutes(2);

    /// <summary>先到的最终结果（还没登记的 opId，可能是别的客户端的命令）留这么久、最多 <see cref="HwEarlyMax"/> 条。</summary>
    public static readonly TimeSpan HwEarlyKeep = TimeSpan.FromSeconds(30);
    public const int HwEarlyMax = 32;

    /// <summary>已经有结果的操作等同一台设备上其它还在等的操作至少这么久，再单独提示（合成一条，见类注释；按每秒的 Tick 放，实际 1–2 s）。</summary>
    public static readonly TimeSpan HwHold = TimeSpan.FromSeconds(1);

    /// <summary>硬件执行完 / 执行失败的说法（提示的后半句；成功为什么不说「已确认」见类注释）。</summary>
    public const string HwDoneText = "硬件已执行完", HwFailedText = "硬件执行失败";

    /// <summary>一次操作里硬件未确认的命令（一个或几个 opId），等最终结果。</summary>
    private sealed class HwWait
    {
        public HwWait(DeviceAction action, int? n, DateTime since)
        {
            Action = action;
            N = n;
            Since = since;
        }

        public DeviceAction Action { get; }
        /// <summary>针对的反应釜（提示里写「RVn」）；整台设备的操作为 null。</summary>
        public int? N { get; }
        public DateTime Since { get; }
        /// <summary>还没有结果的 opId。</summary>
        public HashSet<string> Left { get; } = new(StringComparer.Ordinal);
        /// <summary>失败的结果（说法：消息（代码））。</summary>
        public List<string> Failures { get; } = new();
        /// <summary>全部到齐的时刻（之后最多等 <see cref="HwHold"/> 合成提示）。</summary>
        public DateTime? DoneAt { get; set; }

        public void Settle(OpEventMsg m)
        {
            Left.Remove(m.OpId);
            if (m.HttpStatus is < 200 or >= 300) Failures.Add(FailText(m));
        }
    }

    private readonly Dictionary<string, HwWait> _hwWaiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (OpEventMsg Msg, DateTime At)> _hwEarly = new(StringComparer.Ordinal);
    private readonly List<HwWait> _hwDone = new();

    /// <summary>还在等最终结果的命令条数（测试用）。</summary>
    public int HwPending => _hwWaiting.Count;

    /// <summary>
    /// 命令的结果带 HW_UNCONFIRMED：记下它的 opId（之后跟着 HMI 的 op.*.result）。返回结果的警告（原样，ExecCoreAsync 拿去组 ActionOutcome）。
    /// </summary>
    private static IReadOnlyList<string> Hw<T>(CommandResult<T> r, List<string>? opIds)
    {
        if (opIds != null && r.OpId is { Length: > 0 } id && FleetActions.HasHwUnconfirmed(r.Warnings)) opIds.Add(id);
        return r.Warnings;
    }

    /// <summary>
    /// 操作执行完、有硬件未确认的命令：登记等最终结果。最终结果已经全部先到了（WS 比 HTTP 快）就直接按它说 ——
    /// 成功：去掉 HW_UNCONFIRMED（界面照常 ✓）；失败：这次操作算失败（err）。还有没到的照常返回（界面提示「硬件未确认」）。
    /// </summary>
    private ActionOutcome AwaitHardware(DeviceAction action, ActionPayload a, IReadOnlyList<string> opIds, ActionOutcome o)
    {
        var w = new HwWait(action, CoreActionsPerChannel(action) ? a.N : null, _clock.UtcNow);
        foreach (var id in opIds.Distinct(StringComparer.Ordinal))
        {
            if (_hwEarly.Remove(id, out var early)) { w.Left.Add(id); w.Settle(early.Msg); }
            else { w.Left.Add(id); _hwWaiting[id] = w; }
        }
        if (w.Left.Count > 0) return o;
        if (w.Failures.Count == 0) return o with { Warnings = o.Warnings.Where(x => x != OpCodes.HwUnconfirmed).ToArray() };
        return ActionOutcome.Failed(FailLine(w), ToastKind.Err);
    }

    private static bool CoreActionsPerChannel(DeviceAction action) => Core.Guard.DeviceActions.Spec(action).PerChannel;

    /// <summary>WS 的 opEvent：最终结果（action 以「.result」结尾）对上了在等的 opId 就算上；还没登记的先留着（见类注释）。</summary>
    private void OnOpEvent(OpEventMsg m)
    {
        if (_disposed || string.IsNullOrEmpty(m.OpId) || m.Action is null || !m.Action.EndsWith(".result", StringComparison.Ordinal)) return;
        var now = _clock.UtcNow;
        if (!_hwWaiting.Remove(m.OpId, out var w))
        {
            PruneEarly(now);
            if (_hwEarly.Count >= HwEarlyMax) _hwEarly.Remove(_hwEarly.MinBy(kv => kv.Value.At).Key);
            _hwEarly[m.OpId] = (m, now);
            return;
        }
        w.Settle(m);
        if (w.Left.Count > 0) return;
        w.DoneAt = now;
        _hwDone.Add(w);
        // 同一台设备上没有别的在等了：马上提示；还有在等的：等它们一会儿合成一条（Tick 里放出去）
        if (_hwWaiting.Count == 0) FlushHw();
    }

    /// <summary>每秒（Tick）：等太久的不再等；先到的结果过期；已经到齐的等够了就提示。</summary>
    private void TickHw()
    {
        if (_hwWaiting.Count == 0 && _hwDone.Count == 0 && _hwEarly.Count == 0) return;
        var now = _clock.UtcNow;
        foreach (var kv in _hwWaiting.Where(kv => now - kv.Value.Since >= HwWaitMax).ToList()) _hwWaiting.Remove(kv.Key);
        PruneEarly(now);
        if (_hwDone.Count > 0 && (_hwWaiting.Count == 0 || _hwDone.Any(w => now - w.DoneAt >= HwHold))) FlushHw();
    }

    private void PruneEarly(DateTime now)
    {
        foreach (var k in _hwEarly.Where(kv => now - kv.Value.At >= HwEarlyKeep).Select(kv => kv.Key).ToList()) _hwEarly.Remove(k);
    }

    /// <summary>
    /// 提示到齐的结果：成功的同一个操作合成一条「1号机 · RV1、RV3 停止 · 硬件已执行完」（info）；失败的各一条「1号机 · RV1 停止 · 硬件执行失败：…」（err）。
    /// </summary>
    private void FlushHw()
    {
        var done = _hwDone.ToList();
        _hwDone.Clear();
        foreach (var g in done.Where(w => w.Failures.Count == 0).GroupBy(w => w.Action))
        {
            var rvs = g.Where(w => w.N != null).Select(w => w.N!.Value).Distinct().OrderBy(n => n).ToList();
            string what = (rvs.Count > 0 ? "RV" + string.Join("、RV", rvs) + " " : "") + FleetActions.Label(g.Key);
            Notice?.Invoke(Config.Alias + " · " + what + " · " + HwDoneText, ToastKind.Info);
        }
        foreach (var w in done.Where(w => w.Failures.Count > 0)) Notice?.Invoke(FailLine(w), ToastKind.Err);
    }

    /// <summary>「1号机 · RV1 停止 · 硬件执行失败：加热模块无响应（HW_FAULT）」（几条失败时用「；」隔开）。</summary>
    private string FailLine(HwWait w) =>
        Config.Alias + " · " + FleetActions.What(w.Action, new ActionPayload(N: w.N)) + " · " + HwFailedText + "：" + string.Join("；", w.Failures.Distinct());

    /// <summary>失败的最终结果的说法：消息（代码）；没有消息时「状态码 代码」。</summary>
    private static string FailText(OpEventMsg m)
    {
        string code = string.IsNullOrEmpty(m.Code) || m.Code == OpCodes.Ok ? "" : m.Code;
        if (!string.IsNullOrWhiteSpace(m.Message)) return m.Message.Trim() + (code.Length > 0 ? "（" + code + "）" : "");
        return (m.HttpStatus + " " + code).Trim();
    }

    /// <summary>不再等（断开、换地址、HMI 重启：最终结果不会来了）。</summary>
    private void ForgetHw()
    {
        _hwWaiting.Clear();
        _hwEarly.Clear();
        _hwDone.Clear();
    }

    // ———————————————————— 报警出现 ————————————————————

    /// <summary>这条 WS 报警消息是一条报警的出现：错误 / 提醒（info 不算）、状态 active、本机还没见过这一条（同一次启动里）。在 Apply 之前问。</summary>
    private bool IsRaise(AlarmDto a)
    {
        if (AlarmBook.FromDto(a) is not { Active: true, Acked: false }) return false;
        bool sameBoot = _bootId == null || _alarmBook.BootId == null || _bootId == _alarmBook.BootId;
        return !sameBoot || _alarmBook.Find(a.Id) is not { Active: true };
    }

    /// <summary>报警出现的提示：「3号机 · 报警 RV11：超压」/「3号机 · 提醒 RV3：…」（标题自己以 RV 号开头时不重复写）。</summary>
    public static string AlarmNotice(string alias, AlarmDto a)
    {
        string word = IsErrAlarm(a) ? "报警" : "提醒";
        string title = (a.Title ?? "").Trim(), rv = (a.Rv ?? "").Trim();
        string head = rv.Length > 0 && !title.StartsWith(rv, StringComparison.Ordinal) ? " " + rv : "";
        return alias + " · " + word + head + "：" + title;
    }

    /// <summary>报警出现的提示的语气：错误报警 err，提醒 warn。</summary>
    public static ToastKind AlarmNoticeTone(AlarmDto a) => IsErrAlarm(a) ? ToastKind.Err : ToastKind.Warn;

    private static bool IsErrAlarm(AlarmDto a) => AlarmBook.LevelOf(a.Level) == Core.State.AlarmLevel.Err;
}
