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
public sealed record ChannelFacts(int N, bool Installed, StateKey State = StateKey.Idle, bool Heating = false, bool StirOn = false,
    ProgramStatus? Program = null, double? Sp = null, bool? Downloaded = null)
{
    /// <summary>占用通道（样稿 isRunning：已安装且状态属于 RUNNING，报警也算）。</summary>
    public bool IsRunning => Installed && StateKeys.IsRunning(State);
}

/// <summary>守卫要看的一条报警：id + 是否已确认。</summary>
public sealed record AlarmFacts(string Id, bool Acked);

/// <summary>另一台设备的名字和地址（重命名时检查别名是否重复）。</summary>
public sealed record PeerDevice(string Alias, string Host);

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

    public bool EstopActive { get; init; }

    /// <summary>
    /// 连接没在「已连接」、但停止类命令仍可能送达（真机：只是 WebSocket 断了在自己重连，REST 客户端还在、设备连上过）。
    /// 这时停止类（急停、全部停止、停止、停止加热、中止程序、确认报警…）照样可用 —— 尽力发送，送不到就提示失败；控制类和申请控制权照样禁用。
    /// 旧界面也是这样：只要有连接对象就能急停（停止类「永远可用」，PORT-PLAN §3.5）。演示数据（样稿）不用它（默认 false）。
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
///   例外：真机只是 WebSocket 在重连、REST 还在（<see cref="GuardFacts.StopReachable"/>）时，停止类照样可用（尽力发送）。</item>
/// <item><b>停止类（safe）只要连接</b>：不看控制权，也不因为本机「以为」没在运行而禁用；只保留纯逻辑检查（未安装、型号没有该部件、已在急停、先开始会话、报警已确认）。</item>
/// <item>控制类另外要不在急停、本机远程控制中：「1号机急停中 · 需在触屏复位」「2号机为本地只读 · 先申请控制权」「2号机等待触屏确认 · 剩 45 s」
///   「1号机控制权心跳中断 · 宽限 18 s」；被其他客户端持有时「1号机远程控制中（其他客户端 X）」（PORT-PLAN §3.5 的徽标文字）。</item>
/// <item>然后是各操作自己的检查：「RV5 未安装」「RV15 已停用 · 先启用」「RV1 程序运行中 · 先中止」「超出量程 · HT-PR8 ≤ 200 ℃」…</item>
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
    public static string GuardText(GuardFacts? f, GuardKind kind)
    {
        if (f is null || !f.Exists) return "设备不存在";
        if (kind == GuardKind.None) return "";
        string a = f.Alias;
        if (f.Conn != ConnStatus.Online && !(kind == GuardKind.Safe && f.StopReachable))
        {
            if (f.Conn == ConnStatus.Disconnected) return a + "已断开 · 先连接";
            if (f.Conn == ConnStatus.Connecting) return a + (f.EverOnline ? "正在重连 · 稍候" : "正在连接 · 稍候");
            return a + (f.EverOnline ? "连接中断 · 重连中 " + f.RetryS + " s" : "连接失败 · " + f.RetryS + " s 后重试");
        }
        if (kind is GuardKind.Safe or GuardKind.Conn) return "";
        if (f.EstopActive) return a + "急停中 · 需在触屏复位";
        return f.Lease switch
        {
            LeaseStatus.Local => a + "为本地只读 · 先申请控制权",
            LeaseStatus.Requested => a + "等待触屏确认 · 剩 " + f.LeaseRemainS + " s",
            LeaseStatus.Lost => a + "控制权心跳中断 · 宽限 " + f.LeaseGraceS + " s",
            LeaseStatus.Other => a + "远程控制中（其他客户端 " + (f.LeaseHolder ?? "") + "）",
            _ => "",
        };
    }

    /// <summary>操作是否可用：'' = 可用，否则为原因（样稿 PR.sim.why）。</summary>
    public static string Why(GuardFacts? f, DeviceAction action, ActionPayload? payload = null)
    {
        string g = GuardText(f, KindOf(action));
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
                foreach (var c in f.Channels)
                    if (IsRunAllTarget(c))
                        return "";
                return "没有可运行的反应釜";
            case DeviceAction.Estop:
                return f.EstopActive ? f.Alias + "已在急停 · 需在触屏复位" : "";
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
                return f.Lease == LeaseStatus.Remote ? "已是远程控制" : f.Lease == LeaseStatus.Requested ? "正在等待触屏确认" : "";
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
    /// <see cref="ChannelFacts.Downloaded"/>）。守卫（「没有可运行的反应釜」）、确认清单和执行都用这一个判断，所以三处永远一致。
    /// </summary>
    public static bool IsRunAllTarget(ChannelFacts c) =>
        c.Installed && c.State != StateKey.Idle && !c.IsRunning && IsRerunnable(c.Program) && c.Downloaded != false;

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
