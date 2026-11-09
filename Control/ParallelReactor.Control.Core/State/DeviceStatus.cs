using System;

namespace ParallelReactor.Control.Core.State;

/// <summary>
/// 设备连接状态（样稿 conn.state）：已连接、正在连接 / 重连、断线（倒计时重连）、用户断开。
/// 顺序与 App 的 Controls.Pr.ConnState 相同（转换请按名字，不要强转）。
/// </summary>
public enum ConnStatus { Online, Connecting, Offline, Disconnected }

/// <summary>
/// 控制权状态（样稿 lease.state 的 4 种 + 真机的第 5 种）：本地只读、申请中（等触屏确认）、本机远程控制中、心跳中断宽限中，
/// 以及 <see cref="Other"/> = 被其他客户端持有（PORT-PLAN §3.2 第 9 条 / §3.5 / R8；演示数据不会出现）。
/// 前 4 种的顺序与 App 的 Controls.Pr.LeaseState 相同；Other 在那边还没有徽标（M3）。
/// </summary>
public enum LeaseStatus { Local, Requested, Remote, Lost, Other }

/// <summary>语气（样稿 tone）：只决定图标 / 提示，不是颜色（RESTYLE-BRIEF：红色只给真报警、断线、急停）。</summary>
public enum Tone { Ok, Mute, Warn, Err, Info }

/// <summary>设备健康点（样稿 health）：外框只把 Err 画成红点。</summary>
public enum DeviceHealth { Ok, Warn, Err, Off, Mute }

/// <summary>报警级别（样稿 alarm.level）：错误报警（红，「报警」）、提醒（中性 + ⚠，「提醒」）、信息。</summary>
public enum AlarmLevel { Err, Warn, Info }

/// <summary>事件级别（样稿 event.level）：信息、操作、提醒、错误。</summary>
public enum EventLevel { Info, Op, Warn, Err }

/// <summary>控制权的显示文字（样稿 PR.sim.leaseInfo + ui.leaseBadge 的 short 写法）。</summary>
/// <param name="Text">完整文字：'远程控制中' / '申请中 · 45 s' / '控制权心跳中断 · 宽限 18 s' / '本地 · 只读'。</param>
/// <param name="Short">短文字（样稿 short）：'远程控制' / '申请中' / '控制权宽限 18 s' / '本地只读'。</param>
/// <param name="BadgeShort">树 / 列表徽标的短文字（ui.leaseBadge short）：'远程控制' / '申请中 45 s' / '宽限 18 s' / '本地只读'，完整文字放提示。</param>
/// <param name="Holder">持有者：本机客户端名或 '触屏'（第 5 种为其他客户端名）。</param>
public sealed record LeaseDisplay(LeaseStatus State, string Text, string Short, string BadgeShort, Tone Tone, string Holder)
{
    /// <summary>样稿代码：'local' / 'requested' / 'remote' / 'lost' / 'other'。</summary>
    public string Code => DeviceTexts.LeaseCode(State);
}

/// <summary>连接的显示文字（样稿 PR.sim.connInfo）。First = 新添加的设备还从未连上过。</summary>
public sealed record ConnDisplay(ConnStatus State, string Text, string Short, Tone Tone, bool First)
{
    /// <summary>样稿代码：'online' / 'offline' / 'connecting' / 'disconnected'。</summary>
    public string Code => DeviceTexts.ConnCode(State);
}

/// <summary>
/// 设备级的显示文字（样稿 sim.js leaseInfo / connInfo / dataText / estopText，逐字相同），演示数据与真机共用。
/// 第 5 种控制权「被其他客户端持有」的文字按 PORT-PLAN §3.5：「远程控制中（其他客户端 X）」。
/// </summary>
public static class DeviceTexts
{
    /// <summary>触屏（本地）一方的名字。</summary>
    public const string TouchScreen = "触屏";

    public static string LeaseCode(LeaseStatus s) => s switch
    {
        LeaseStatus.Local => "local",
        LeaseStatus.Requested => "requested",
        LeaseStatus.Remote => "remote",
        LeaseStatus.Lost => "lost",
        _ => "other",
    };

    public static string ConnCode(ConnStatus s) => s switch
    {
        ConnStatus.Online => "online",
        ConnStatus.Offline => "offline",
        ConnStatus.Connecting => "connecting",
        _ => "disconnected",
    };

    /// <summary>
    /// 控制权文字。<paramref name="remainS"/> = 申请剩余秒数，<paramref name="graceS"/> = 宽限剩余秒数，<paramref name="client"/> = 本机客户端名，
    /// <paramref name="otherHolder"/> = 第 5 种状态时持有控制权的客户端名。
    /// </summary>
    public static LeaseDisplay Lease(LeaseStatus state, int remainS, int graceS, string client, string? otherHolder = null) => state switch
    {
        LeaseStatus.Remote => new LeaseDisplay(state, "远程控制中", "远程控制", "远程控制", Tone.Ok, client),
        LeaseStatus.Requested => new LeaseDisplay(state, "申请中 · " + remainS + " s", "申请中", "申请中 " + remainS + " s", Tone.Warn, TouchScreen),
        LeaseStatus.Lost => new LeaseDisplay(state, "控制权心跳中断 · 宽限 " + graceS + " s", "控制权宽限 " + graceS + " s", "宽限 " + graceS + " s", Tone.Err, client),
        LeaseStatus.Other => new LeaseDisplay(state, "远程控制中（其他客户端 " + (otherHolder ?? "") + "）", "其他客户端", "其他客户端", Tone.Mute, otherHolder ?? ""),
        _ => new LeaseDisplay(LeaseStatus.Local, "本地 · 只读", "本地只读", "本地只读", Tone.Mute, TouchScreen),
    };

    /// <summary>连接文字：'已连接' / '正在重连…' / '正在连接…'（从未连上）/ '已断开' / '连接中断 · 重连中 12 s' / '连接失败 · 12 s 后重试'（从未连上）。</summary>
    public static ConnDisplay Conn(ConnStatus state, bool everOnline, int retryS) => state switch
    {
        ConnStatus.Online => new ConnDisplay(state, "已连接", "已连接", Tone.Ok, false),
        ConnStatus.Connecting => everOnline
            ? new ConnDisplay(state, "正在重连…", "正在重连", Tone.Warn, false)
            : new ConnDisplay(state, "正在连接…", "连接中", Tone.Warn, true),
        ConnStatus.Disconnected => new ConnDisplay(state, "已断开", "已断开", Tone.Mute, false),
        _ => everOnline
            ? new ConnDisplay(ConnStatus.Offline, "连接中断 · 重连中 " + retryS + " s", "连接中断", Tone.Err, false)
            : new ConnDisplay(ConnStatus.Offline, "连接失败 · " + retryS + " s 后重试", "连接失败", Tone.Err, true),
    };

    /// <summary>断线时的数据时刻文字：'数据停在 10:11:40'；从未连上或没有数据 '尚未收到数据'。</summary>
    public static string DataText(DateTime? dataAt, bool everOnline) =>
        dataAt is { } t && everOnline ? "数据停在 " + Format.Fmt.Time(t) : "尚未收到数据";

    /// <summary>急停文字：在线 '急停中'；断线 '断线前已急停'。</summary>
    public static string EstopText(bool online) => online ? "急停中" : "断线前已急停";
}
