using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Guard;

/// <summary>
/// 守卫要看的一个反应釜：号、是否安装、自己的状态（样稿 ch.state，未按断线改写；真机用 StateMap.Map(…, online: true)）、
/// 是否在加热、独立搅拌是否在转、程序状态（没有程序为 null）、当前温度设定值（「启动加热」没给设定值时用它查量程）、
/// 程序是不是本机下发的（<see cref="Downloaded"/>）。
/// </summary>
/// <param name="Downloaded">
/// 程序是本机这次下发的（真机，D6：「全部运行」只运行本机下发过、没在运行的反应釜，不调 HMI 的 POST /run）；false = 触屏或别的客户端放上去的程序，
/// 「全部运行」不碰它。null = 不区分（演示数据 / 样稿：有能再跑的程序就算）。
/// </param>
/// <param name="Checking">
/// 真机：本机下发过的程序，连上之后离线期间的事件还没核对完（程序台账 <see cref="LedgerMatchKind.Checking"/>，复核 R5）——「启动」先禁用并写明，
/// 不运行它、也不改成启用 + 加热（核对完再按台账决定）。演示数据恒为 false。
/// </param>
/// <param name="LocalControl">
/// 真机：本机下发、HMI 上就是它的程序，但下发之后触屏端接管过控制（程序台账 <see cref="ProgramLedger.IsLocalControl"/>，复核）—— 触屏可能改过程序：
/// 「启动」禁用并写 <see cref="ProgramLedger.LocalControlReason"/>（不运行它、也不改成启用 + 加热），「全部运行」不算它；重新下发之后照常。演示数据恒为 false。
/// </param>
public sealed record ChannelFacts(int N, bool Installed, StateKey State = StateKey.Idle, bool Heating = false, bool StirOn = false,
    ProgramStatus? Program = null, double? Sp = null, bool? Downloaded = null, bool Checking = false, bool LocalControl = false)
{
    /// <summary>占用通道（样稿 isRunning：已安装且状态属于 RUNNING，报警也算）。</summary>
    public bool IsRunning => Installed && StateKeys.IsRunning(State);
}

/// <summary>守卫要看的一条报警：id + 是否已确认。</summary>
public sealed record AlarmFacts(string Id, bool Acked);

/// <summary>另一台设备的名字和地址（重命名时检查别名是否重复）。</summary>
public sealed record PeerDevice(string Alias, string Host);

/// <summary>
/// 本机旧租约（<see cref="GuardFacts.Orphan"/>）：没有 / HMI 在 remote（拒绝新申请）/ HMI 已进入 remoteLost（同一令牌 + 客户端名可以重新申请，旧租约作废）。
/// </summary>
public enum OrphanLease { None, Remote, Lost }

/// <summary>
/// 守卫的输入（一台设备此刻的事实）：App 从演示数据（DemoDevice.Facts()）或真机（连接、租约、遥测、型号表、程序台账）填写。
/// 只读、无副作用；同一份事实可以问多个操作。
/// </summary>
public sealed record GuardFacts
{
    private readonly bool? _sharedStir;

    /// <summary>设备还在（false →「设备不存在」）。</summary>
    public bool Exists { get; init; } = true;
    /// <summary>别名（原因文字以它开头：「2号机为本地只读 · 先申请控制权」）。</summary>
    public string Alias { get; init; } = "";

    public ConnStatus Conn { get; init; } = ConnStatus.Online;
    /// <summary>连上过（false = 新添加、从未连上：「正在连接 · 稍候」「连接失败 · 12 s 后重试」）。</summary>
    public bool EverOnline { get; init; } = true;
    /// <summary>断线时距下次重连的秒数。</summary>
    public int RetryS { get; init; }

    public LeaseStatus Lease { get; init; } = LeaseStatus.Local;
    /// <summary>申请中：等触屏确认的剩余秒数。</summary>
    public int LeaseRemainS { get; init; }
    /// <summary>宽限中：剩余秒数。</summary>
    public int LeaseGraceS { get; init; }
    /// <summary>第 5 种状态（被其他客户端持有）时的持有者名称。</summary>
    public string? LeaseHolder { get; init; }
    /// <summary>
    /// 另一个客户端的控制权申请正在等触屏处理（真机 GET /control 的 pending，申请人不是本机）：这时 HMI 会拒绝本机的申请
    /// （409 CONTROL_REQUEST_PENDING「已有待处理的远程申请（X）」），「申请控制权」先禁用并写明。值 = 那个客户端的名字；没有为 null。
    /// </summary>
    public string? OtherRequest { get; init; }

    /// <summary>
    /// 触屏刚拒绝了本机的申请：HMI 对同一令牌冷却 30 s（Client 的 ControlLeaseClient.DenyUntil；429 同样），这期间 Client 不再发申请。
    /// HMI 在申请<b>超时</b>时也冷却 30 s（ControlLease 的 RequestExpired → CooldownLocked）：App 在本机的申请超时后自己记一份（M3 复核）。
    /// 值 = 还要等的秒数（0 = 不在冷却）。「申请控制权」先禁用并写明（<see cref="ActionGuard.DenyText"/>；原因不确定时 <see cref="ActionGuard.CooldownText"/>）。
    /// 演示数据恒为 0（样稿没有拒绝）。
    /// </summary>
    public int LeaseDenyS { get; init; }

    /// <summary>
    /// 冷却的原因不是（确知的）触屏拒绝：本机的申请超时了、或 HMI 回了 429（「触屏刚拒绝了该令牌的申请（或申请超时）」）——
    /// 「申请控制权」的原因写中性的 <see cref="ActionGuard.CooldownText"/>，不写「触屏已拒绝申请」。
    /// </summary>
    public bool LeaseDenyNeutral { get; init; }

    /// <summary>
    /// 本机旧租约（R8 的边角，真机）：HMI 报告控制权在<b>本机客户端名</b>手里，而本机这条连接没有持有 —— 上次运行崩溃 / 强退后留下、心跳还没超时
    /// （或另一台 PC 用了同一个客户端名）。这时 <see cref="Lease"/> = Other、<see cref="LeaseHolder"/> = 本机客户端名；HMI 在 remote 时拒绝新申请
    /// （409 NOT_CONTROL_HOLDER），心跳超时进入 remoteLost 之后同一令牌 + 客户端名的申请会作废旧租约、按新申请处理 —— 所以只有 <see cref="OrphanLease.Remote"/> 时禁用「申请控制权」。
    /// </summary>
    public OrphanLease Orphan { get; init; }

    /// <summary>本机令牌在这台设备上的权限（第一个 403 之后学到，见 <see cref="TokenRole"/>）；默认不知道 = 不限制。</summary>
    public TokenRole Role { get; init; } = TokenRole.Unknown;

    public bool EstopActive { get; init; }

    /// <summary>
    /// 连接没在「已连接」、但停止类命令仍能送达（真机：WebSocket 断了 / 握手被拒、在自己重连，而 REST <b>确实还通</b> —— 要有正面的证据：
    /// REST 起步刚完成、WS 握手被 HTTP 拒绝（503 / 401…：主机答话了）、探测 GET /health 有响应、命令送达；见 App 的 LiveDeviceSource.StopReachable）。
    /// 这时真正的停止类（急停、全部停止、停止、停用、停止加热、中止程序、停止搅拌 —— TokenRole 的 Stop / Estop 类）照样可用：尽力发送，送不到就提示失败；
    /// 会话、打标记、确认报警（样稿也算 safe）和控制类、申请控制权照样禁用（M3 复核）。主机真的不通（拒绝连接 / 找不到主机 / 超时）时为 false：只剩 none 类（样稿）。
    /// 演示数据（样稿）不用它（默认 false）。
    /// </summary>
    public bool StopReachable { get; init; }

    /// <summary>型号：温度上限、转速范围、型号名（量程原因「超出量程 · HT-PR8 ≤ 200 ℃」）。null 时不做量程检查以外的型号判断。</summary>
    public ModelProfile? Model { get; init; }

    /// <summary>全釜共用搅拌（样稿 dev.stir ≠ null）。不填时按型号：独立搅拌的型号为 false，其余 true。</summary>
    public bool SharedStir { get => _sharedStir ?? Model?.StirMode != StirMode.Independent; init => _sharedStir = value; }
    /// <summary>共用搅拌正在转。</summary>
    public bool StirOn { get; init; }

    /// <summary>全部通道（含未安装）。</summary>
    public IReadOnlyList<ChannelFacts> Channels { get; init; } = Array.Empty<ChannelFacts>();
    /// <summary>程序库里的 id（下发时检查「程序不存在」）；null = 不检查。</summary>
    public IReadOnlyCollection<string>? ProgramIds { get; init; }

    public bool SessionActive { get; init; }
    public string? SessionId { get; init; }

    /// <summary>当前报警（确认报警时检查）。</summary>
    public IReadOnlyList<AlarmFacts> Alarms { get; init; } = Array.Empty<AlarmFacts>();
    /// <summary>其它设备（重命名查重）。</summary>
    public IReadOnlyList<PeerDevice> Peers { get; init; } = Array.Empty<PeerDevice>();

    public bool Online => Conn == ConnStatus.Online;

    /// <summary>第 n 路；没有返回 null。</summary>
    public ChannelFacts? Channel(int? n)
    {
        if (n is not int k) return null;
        foreach (var c in Channels) if (c.N == k) return c;
        return null;
    }
}

/// <summary>
/// 操作守卫（样稿 sim.js guard / why / ACTIONS 的移植，原因文字逐字相同，API.md §3.3）：返回 '' = 可用，否则是一句已经带设备名的原因，
/// 可以直接写在按钮下 / 悬停提示 / toast 里。规则：
/// <list type="number">
/// <item>离线（断线、正在连接、已断开）只剩 none 类操作：「4号机连接中断 · 重连中 12 s」「已断开 · 先连接」「正在重连 · 稍候」「正在连接 · 稍候」。
///   例外：真机只是 WebSocket 在重连、REST 确实还通（<see cref="GuardFacts.StopReachable"/>）时，真正的停止类（TokenRole 的 Stop / Estop 类）照样可用（尽力发送）；
///   会话 / 标记 / 确认报警不在例外里。</item>
/// <item><b>停止类（safe）只要连接</b>：不看控制权，也不因为本机「以为」没在运行而禁用；只保留纯逻辑检查（未安装、型号没有该部件、已在急停、先开始会话、报警已确认）。</item>
/// <item>控制类另外要不在急停、本机远程控制中：「1号机急停中 · 需在触屏复位」「2号机为本地只读 · 先申请控制权」「2号机等待触屏确认 · 剩 45 s」
///   「1号机控制权心跳中断 · 宽限 18 s」；被其他客户端持有时「1号机远程控制中（其他客户端 X）」（PORT-PLAN §3.5 的徽标文字）。</item>
/// <item>令牌权限（真机，第一个 403 之后学到，<see cref="TokenRole"/>）：只读令牌时控制类、申请控制权、确认报警、会话禁用；
///   只读又没有急停权限位时停止类也禁用；没有急停权限位时急停禁用。没学到的一律不禁用。</item>
/// <item>然后是各操作自己的检查：「RV5 未安装」「RV15 已停用 · 先启用」「RV1 程序运行中 · 先中止」「超出量程 · HT-PR8 ≤ 200 ℃」…；
///   申请控制权另外看第 5 种状态（「1号机远程控制中（其他客户端 X）」）和别人的待批申请（「1号机已有待处理的远程申请（X）」）。</item>
/// </list>
/// 带守卫的命令在执行时也要再调一次（界面状态可能已过期）。
/// </summary>
public static class ActionGuard
{
    /// <summary>别名最多 16 个字符宽度（汉字算 2，即 8 个汉字或 16 个字母）。</summary>
    public const int AliasMax = 16;

    /// <summary>守卫类别（样稿 ACTIONS[name].guard）。</summary>
    public static GuardKind KindOf(DeviceAction action) => DeviceActions.KindOf(action);

    /// <summary>需要控制权（样稿 needsLease）。</summary>
    public static bool NeedsLease(DeviceAction action) => DeviceActions.NeedsLease(action);

    /// <summary>
    /// 只看连接 / 急停 / 控制权的原因（样稿 PR.sim.guard(dev, kind)）：每个区域只写一次（例如检查器控制权卡片），不必写在每个按钮下。
    /// </summary>
    public static string GuardText(GuardFacts? f, GuardKind kind) => GuardText(f, kind, stopException: true);

    /// <summary>
    /// <paramref name="stopException"/>：这个操作可以用 <see cref="GuardFacts.StopReachable"/> 的例外（真正的停止类）。区域级的问法（上面的重载）按停止类算。
    /// </summary>
    private static string GuardText(GuardFacts? f, GuardKind kind, bool stopException)
    {
        if (f is null || !f.Exists) return "设备不存在";
        if (kind == GuardKind.None) return "";
        string a = f.Alias;
        if (f.Conn != ConnStatus.Online && !(kind == GuardKind.Safe && stopException && f.StopReachable))
        {
            if (f.Conn == ConnStatus.Disconnected) return a + "已断开 · 先连接";
            if (f.Conn == ConnStatus.Connecting) return a + (f.EverOnline ? "正在重连 · 稍候" : "正在连接 · 稍候");
            return a + (f.EverOnline ? "连接中断 · 重连中 " + f.RetryS + " s" : "连接失败 · " + f.RetryS + " s 后重试");
        }
        if (kind == GuardKind.Safe) return "";
        // 只读令牌（第一个 403 之后学到）：控制类和申请控制权都会被 HMI 拒绝
        if (f.Role.ReadOnly) return TokenRole.ReadOnlyText(a);
        if (kind == GuardKind.Conn) return "";
        if (f.EstopActive) return a + "急停中 · 需在触屏复位";
        return f.Lease switch
        {
            LeaseStatus.Local => a + "为本地只读 · 先申请控制权",
            LeaseStatus.Requested => a + "等待触屏确认 · 剩 " + f.LeaseRemainS + " s",
            LeaseStatus.Lost => a + "控制权心跳中断 · 宽限 " + f.LeaseGraceS + " s",
            LeaseStatus.Other => OtherText(f),
            _ => "",
        };
    }

    /// <summary>第 5 种状态的原因：「1号机远程控制中（其他客户端 X）」；本机旧租约时「1号机远程控制中（本机旧租约）」。</summary>
    private static string OtherText(GuardFacts f) =>
        f.Orphan != OrphanLease.None ? OrphanText(f.Alias) : f.Alias + "远程控制中（其他客户端 " + (f.LeaseHolder ?? "") + "）";

    /// <summary>
    /// 操作是否可用：'' = 可用，否则为原因（样稿 PR.sim.why）。断线时 <see cref="GuardFacts.StopReachable"/> 的例外只给真正的停止类
    /// （<see cref="TokenRole.PermissionOf"/> 为 Stop / Estop：停止、停用、停止加热、中止程序、停止搅拌、全部停止、急停）。
    /// </summary>
    public static string Why(GuardFacts? f, DeviceAction action, ActionPayload? payload = null)
    {
        string g = GuardText(f, KindOf(action), TokenRole.PermissionOf(action) is TokenPermission.Stop or TokenPermission.Estop);
        if (g.Length > 0) return g;
        return Check(f!, action, payload ?? ActionPayload.Empty);
    }

    /// <summary>按样稿代码问（'heatStart'）；不认识的代码返回「未知操作 xxx」。</summary>
    public static string Why(GuardFacts? f, string code, ActionPayload? payload = null) =>
        DeviceActions.TryParse(code, out var a) ? Why(f, a, payload) : "未知操作 " + code;

    /// <summary>设定温度检查（样稿 spCheck）：'请输入设定温度' / '超出量程 · HT-PR8 ≤ 200 ℃'。没有型号时只查有没有数。</summary>
    public static string SpCheck(ModelProfile? model, double? sp)
    {
        if (sp is not double v || !double.IsFinite(v)) return "请输入设定温度";
        if (model is null) return "";
        double max = model.Limits.TempMaxC;
        return v < 0 || v > max ? "超出量程 · " + model.Name + " ≤ " + JsNumber.ToString(max) + " ℃" : "";
    }

    /// <summary>转速检查（样稿 rpmCheck）：'请输入转速' / '超出量程 · 0–1500 rpm'。</summary>
    public static string RpmCheck(ModelProfile? model, double? rpm)
    {
        if (rpm is not double v || !double.IsFinite(v)) return "请输入转速";
        if (model is null) return "";
        var l = model.Limits;
        return v < l.RpmMin || v > l.RpmMax ? "超出量程 · " + JsNumber.ToString(l.RpmMin) + "–" + JsNumber.ToString(l.RpmMax) + " rpm" : "";
    }

    /// <summary>
    /// 别名检查（样稿 aliasError，添加 / 重命名 / 编辑共用）：'名称不能为空' / '名称太长 · 最多 8 个汉字或 16 个字母' /
    /// '名称已被其他设备使用（192.168.1.20）'。<paramref name="others"/> = 除这台以外的设备。
    /// </summary>
    public static string AliasError(string? alias, IEnumerable<PeerDevice>? others)
    {
        string a = JsTrim(alias ?? "");
        if (a.Length == 0) return "名称不能为空";
        if (AliasUnits(a) > AliasMax) return "名称太长 · 最多 8 个汉字或 16 个字母";
        if (others != null)
            foreach (var o in others)
                if (o.Alias == a) return "名称已被其他设备使用（" + o.Host + "）";
        return "";
    }

    /// <summary>第一个没被占用的「k号机」（样稿 freeAlias：添加设备时的默认别名）。</summary>
    public static string FreeAlias(IEnumerable<string> used)
    {
        var set = new HashSet<string>(used, StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            string a = i + "号机";
            if (!set.Contains(a)) return a;
        }
    }

    /// <summary>
    /// 另一个客户端的申请正在等触屏处理（真机）：「1号机已有待处理的远程申请（PC-2）」。样稿没有这一种（演示数据里只有本机申请）；
    /// 写法用 HMI 拒绝时自己的消息（ControlLease.MsgRequestPending「已有待处理的远程申请」+「（客户端名）」），前面加别名。
    /// </summary>
    public static string OtherRequestText(string alias, string client) => alias + "已有待处理的远程申请（" + client + "）";

    /// <summary>
    /// 触屏刚拒绝了本机的申请、HMI 的冷却还没过（真机，<see cref="GuardFacts.LeaseDenyS"/>）：「1号机触屏已拒绝申请 · 28 s 后可再申请」。
    /// 样稿没有这一种（演示数据的申请只会被确认或超时）；写法沿用样稿的「… · N s 后重试」倒计时句式，前面加别名。
    /// </summary>
    public static string DenyText(string alias, int seconds) => alias + "触屏已拒绝申请 · " + seconds + " s 后可再申请";

    /// <summary>
    /// 冷却的原因不确定（<see cref="GuardFacts.LeaseDenyNeutral"/>：本机的申请超时了 —— HMI 超时同样冷却 30 s —— 或 HMI 回了 429）：
    /// 「1号机申请刚被拒绝或超时 · 28 s 后可再申请」。样稿没有这一种；写法照 HMI 429 的消息「触屏刚拒绝了该令牌的申请（或申请超时）」，句式同 <see cref="DenyText"/>。
    /// </summary>
    public static string CooldownText(string alias, int seconds) => alias + "申请刚被拒绝或超时 · " + seconds + " s 后可再申请";

    /// <summary>
    /// 本机旧租约（<see cref="GuardFacts.Orphan"/>）：「1号机远程控制中（本机旧租约）」。样稿没有这一种；写法同第 5 种状态「远程控制中（其他客户端 X）」。
    /// </summary>
    public static string OrphanText(string alias) => alias + "远程控制中（本机旧租约）";

    /// <summary>本机旧租约、HMI 还在 remote 时「申请控制权」的原因：「1号机远程控制中（本机旧租约） · 心跳超时后可重新申请」（HMI 此时回 409；超时进入 remoteLost 后可以）。</summary>
    public static string OrphanRequestText(string alias) => OrphanText(alias) + " · 心跳超时后可重新申请";

    /// <summary>别名的字符宽度（样稿 aliasUnits）：每个码位 U+2E80 起（含全部辅助平面字符）算 2，其余算 1。</summary>
    public static int AliasUnits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { n += 2; i++; continue; }
            n += c >= 0x2E80 ? 2 : 1;
        }
        return n;
    }

    /// <summary>JS String.prototype.trim：去掉首尾的 JS 空白（含 U+FEFF、全部 Zs、行终止符；不含 U+0085）。</summary>
    public static string JsTrim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsJsSpace(s[a])) a++;
        while (b > a && IsJsSpace(s[b - 1])) b--;
        return a == 0 && b == s.Length ? s : s[a..b];
    }

    private static bool IsJsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or (char)0xA0 or (char)0x1680 or (char)0x2028 or (char)0x2029
        or (char)0x202F or (char)0x205F or (char)0x3000 or (char)0xFEFF || c >= (char)0x2000 && c <= (char)0x200A;

    // ———————————————————— 各操作自己的检查（样稿 ACTIONS[name].check） ————————————————————

    private static string Check(GuardFacts f, DeviceAction action, ActionPayload p)
    {
        // 令牌权限（真机学到的；演示数据恒为不知道）：停止类只在「只读且没有急停权限位」时禁用；确认报警 / 会话要 operator
        switch (TokenRole.PermissionOf(action))
        {
            case TokenPermission.Stop when !f.Role.CanStop:
                return TokenRole.NoStopText(f.Alias);
            case TokenPermission.Record when f.Role.ReadOnly && action is not (DeviceAction.LeaseRequest or DeviceAction.LeaseRelease):
                return TokenRole.ReadOnlyText(f.Alias);
        }
        switch (action)
        {
            case DeviceAction.Enable:
            {
                string r = NeedCh(f, p);
                return r.Length > 0 ? r : f.Channel(p.N)!.State != StateKey.Idle ? Rv(p) + " 已启用" : "";
            }
            case DeviceAction.Disable:
            case DeviceAction.Stop:
            case DeviceAction.HeatStop:
            case DeviceAction.ProgAbort:
                return NeedCh(f, p);
            case DeviceAction.Start:
            {
                string r = NeedCh(f, p);
                if (r.Length > 0) return r;
                var ch = f.Channel(p.N)!;
                if (ch.State == StateKey.Idle) return Rv(p) + " 已停用 · 先启用";
                if (ch.IsRunning) return Rv(p) + " 已在运行";
                if (ch.Checking) return Rv(p) + " " + ProgramLedger.CheckingReason;
                if (ch.LocalControl) return Rv(p) + " " + ProgramLedger.LocalControlReason;
                return "";
            }
            case DeviceAction.HeatStart:
            {
                string r = NeedCh(f, p);
                if (r.Length > 0) return r;
                var ch = f.Channel(p.N)!;
                if (ch.State == StateKey.Idle) return Rv(p) + " 已停用 · 先启用";
                if (ch.Heating) return Rv(p) + " 已在加热";
                return SpCheck(f.Model, p.Sp ?? ch.Sp);
            }
            case DeviceAction.SetSp:
            {
                string r = NeedCh(f, p);
                return r.Length > 0 ? r : SpCheck(f.Model, p.Sp);
            }
            case DeviceAction.ProgLoad:
            {
                string r = NeedCh(f, p);
                if (r.Length > 0) return r;
                var pr = f.Channel(p.N)!.Program;
                if (pr is ProgramStatus.Running or ProgramStatus.Held) return Rv(p) + " 程序运行中 · 先中止";
                if (!string.IsNullOrEmpty(p.Id) && f.ProgramIds != null && !Contains(f.ProgramIds, p.Id)) return "程序不存在";
                return "";
            }
            case DeviceAction.ProgRun:
            {
                string r = NeedCh(f, p);
                if (r.Length > 0) return r;
                var ch = f.Channel(p.N)!;
                if (ch.State == StateKey.Idle) return Rv(p) + " 已停用 · 先启用";
                if (ch.Program is null) return Rv(p) + " 没有程序 · 先下发";
                if (ch.Program is ProgramStatus.Running or ProgramStatus.Held) return Rv(p) + " 程序运行中";
                return "";
            }
            case DeviceAction.StirOn:
                return !f.SharedStir ? IndependentStir : f.StirOn ? "搅拌已在运行" : "";
            case DeviceAction.StirOff:
                return !f.SharedStir ? IndependentStir : "";
            case DeviceAction.StirSet:
                return !f.SharedStir ? IndependentStir : RpmCheck(f.Model, p.Rpm);
            case DeviceAction.ChStirOn:
            {
                if (f.SharedStir) return SharedStir;
                string r = NeedCh(f, p);
                return r.Length > 0 ? r : f.Channel(p.N)!.StirOn ? Rv(p) + " 搅拌已在运行" : "";
            }
            case DeviceAction.ChStirOff:
                return f.SharedStir ? SharedStir : NeedCh(f, p);
            case DeviceAction.ChStirSet:
            {
                if (f.SharedStir) return SharedStir;
                string r = NeedCh(f, p);
                return r.Length > 0 ? r : RpmCheck(f.Model, p.Rpm);
            }
            case DeviceAction.RunAll:
            {
                bool checking = false, local = false;
                foreach (var c in f.Channels)
                {
                    if (IsRunAllTarget(c)) return "";
                    checking |= c.Checking && c.Installed;
                    local |= c.LocalControl && IsRunAllTarget(c with { LocalControl = false });
                }
                // 本机下发过的程序还在核对（连上之后）：先写明，核对完就能运行；触屏端接管过控制的（本来能运行）：写明要重新下发
                return checking ? ProgramLedger.CheckingReason : local ? ProgramLedger.LocalControlReason : "没有可运行的反应釜";
            }
            case DeviceAction.Estop:
                return f.EstopActive ? f.Alias + "已在急停 · 需在触屏复位" : f.Role.NoEstop ? TokenRole.NoEstopText(f.Alias) : "";
            case DeviceAction.SessionStart:
                return f.SessionActive ? "会话 " + f.SessionId + " 记录中" : "";
            case DeviceAction.SessionMark:
                return f.SessionActive ? "" : f.Alias + "没有记录中的会话 · 先开始会话";
            case DeviceAction.SessionEnd:
                return f.SessionActive ? "" : "没有记录中的会话";
            case DeviceAction.AlarmAck:
            {
                AlarmFacts? a = null;
                if (p.Id != null) foreach (var x in f.Alarms) if (x.Id == p.Id) { a = x; break; }
                return a is null ? "报警不存在" : a.Acked ? "已确认" : "";
            }
            case DeviceAction.LeaseRequest:
                // 第 5 种状态 / 别人的待批申请（真机）：HMI 会拒绝（409 NOT_CONTROL_HOLDER / CONTROL_REQUEST_PENDING），先禁用并写明是谁。
                // 本机旧租约：HMI 在 remote 时同样拒绝；进入 remoteLost 之后同一令牌 + 客户端名的申请会作废旧租约 —— 可以申请
                return f.Lease == LeaseStatus.Remote ? "已是远程控制"
                    : f.Lease == LeaseStatus.Requested ? "正在等待触屏确认"
                    : f.Lease == LeaseStatus.Other && f.Orphan == OrphanLease.Remote ? OrphanRequestText(f.Alias)
                    : f.Lease == LeaseStatus.Other && f.Orphan == OrphanLease.None ? f.Alias + "远程控制中（其他客户端 " + (f.LeaseHolder ?? "") + "）"
                    : f.OtherRequest != null ? OtherRequestText(f.Alias, f.OtherRequest)
                    : f.LeaseDenyS > 0 ? (f.LeaseDenyNeutral ? CooldownText(f.Alias, f.LeaseDenyS) : DenyText(f.Alias, f.LeaseDenyS)) : "";
            case DeviceAction.LeaseCancel:
                return f.Lease == LeaseStatus.Requested ? "" : "没有进行中的申请";
            case DeviceAction.LeaseRelease:
                return f.Lease is LeaseStatus.Remote or LeaseStatus.Lost ? "" : "当前没有控制权";
            case DeviceAction.Disconnect:
                return f.Conn == ConnStatus.Disconnected ? "已断开" : "";
            case DeviceAction.Reconnect:
                return f.Conn == ConnStatus.Online ? "已连接" : f.Conn == ConnStatus.Connecting ? "正在重连" : "";
            case DeviceAction.Rename:
                return AliasError(p.Alias, f.Peers);
            default:   // StopAll、Remove：没有自己的检查
                return "";
        }
    }

    private const string IndependentStir = "各釜独立搅拌 · 在反应釜上设置";
    private const string SharedStir = "全釜共用搅拌 · 在设备上设置";

    /// <summary>样稿 needCh：'没有这个反应釜' / 'RV5 未安装'。</summary>
    private static string NeedCh(GuardFacts f, ActionPayload p)
    {
        var ch = f.Channel(p.N);
        if (ch is null) return "没有这个反应釜";
        if (!ch.Installed) return "RV" + ch.N + " 未安装";
        return "";
    }

    private static string Rv(ActionPayload p) => "RV" + p.N;

    /// <summary>
    /// 「全部运行」/「启动」能再跑的程序（样稿：loaded / aborted / done；真机的报警中止 Failed 同 aborted）。
    /// </summary>
    public static bool IsRerunnable(ProgramStatus? status) =>
        status is ProgramStatus.Loaded or ProgramStatus.Aborted or ProgramStatus.Done or ProgramStatus.Failed;

    /// <summary>
    /// 「全部运行」会运行这个反应釜吗（样稿 runAll 的条件：已安装、已启用、没在运行、有能再跑的程序；真机另外要求程序是本机下发的，
    /// <see cref="ChannelFacts.Downloaded"/>，而且下发之后触屏端没有接管过控制，<see cref="ChannelFacts.LocalControl"/>）。
    /// 守卫（「没有可运行的反应釜」）、确认清单和执行都用这一个判断，所以三处永远一致。
    /// </summary>
    public static bool IsRunAllTarget(ChannelFacts c) =>
        c.Installed && c.State != StateKey.Idle && !c.IsRunning && IsRerunnable(c.Program) && c.Downloaded != false && !c.LocalControl;

    /// <summary>「全部运行」要运行的反应釜号（按 RV 号）。</summary>
    public static IReadOnlyList<int> RunAllTargets(GuardFacts f)
    {
        var list = new List<int>();
        foreach (var c in f.Channels) if (IsRunAllTarget(c)) list.Add(c.N);
        list.Sort();
        return list;
    }

    private static bool Contains(IReadOnlyCollection<string> ids, string id)
    {
        foreach (var x in ids) if (x == id) return true;
        return false;
    }
}
