using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Guard;

/// <summary>
/// 设备上的操作（样稿 sim.js ACTIONS 的键，API.md §3.4），顺序同样稿。代码（'heatStart' …）见 <see cref="DeviceActions.Code"/>。
/// </summary>
public enum DeviceAction
{
    Enable, Disable, Start, Stop, HeatStart, HeatStop, SetSp, ProgLoad, ProgRun, ProgAbort,
    StirOn, StirOff, StirSet, ChStirOn, ChStirOff, ChStirSet,
    RunAll, StopAll, Estop, SessionStart, SessionMark, SessionEnd, AlarmAck,
    LeaseRequest, LeaseCancel, LeaseRelease, Disconnect, Reconnect, Rename, Remove,
}

/// <summary>
/// 操作的守卫类别（样稿 guard kind）：
/// <list type="bullet">
/// <item><see cref="Control"/>：控制类 —— 要连接、不在急停、本机持有控制权（远程控制中）。</item>
/// <item><see cref="Safe"/>：停止类（样稿叫 safe）—— 只要连接正常；<b>永远不因为控制权、也不因为本机「以为」没在运行而禁用</b>（brief §6.4）。</item>
/// <item><see cref="Conn"/>：只要连接（申请控制权）。</item>
/// <item><see cref="None"/>：总是可用（断开、重连、取消申请、释放、重命名、移除），离线时也只剩这一类。</item>
/// </list>
/// </summary>
public enum GuardKind { None, Conn, Safe, Control }

/// <summary>操作表的一行：类别、按钮文字（样稿 label）、样稿代码、是否针对某个反应釜（payload 要带 n）。</summary>
public sealed record ActionSpec(DeviceAction Action, string Code, string Label, GuardKind Kind, bool PerChannel);

/// <summary>
/// 操作的参数（样稿 payload）：N = 反应釜号；Sp = 设定温度 ℃；Rpm = 转速；Id = 程序 id（下发）或报警 id（确认）；Alias = 新名称（重命名）；Text = 标记文字。
/// </summary>
public sealed record ActionPayload(int? N = null, double? Sp = null, double? Rpm = null, string? Id = null, string? Alias = null, string? Text = null)
{
    public static readonly ActionPayload Empty = new();

    /// <summary>只带反应釜号。</summary>
    public static ActionPayload Ch(int n) => new(N: n);
}

/// <summary>操作表（样稿 ACTIONS 的类别与文字，逐字相同）。</summary>
public static class DeviceActions
{
    private static readonly ActionSpec[] Table =
    {
        new(DeviceAction.Enable, "enable", "启用", GuardKind.Control, true),
        new(DeviceAction.Disable, "disable", "停用", GuardKind.Safe, true),
        new(DeviceAction.Start, "start", "启动", GuardKind.Control, true),
        new(DeviceAction.Stop, "stop", "停止", GuardKind.Safe, true),
        new(DeviceAction.HeatStart, "heatStart", "启动加热", GuardKind.Control, true),
        new(DeviceAction.HeatStop, "heatStop", "停止加热", GuardKind.Safe, true),
        new(DeviceAction.SetSp, "setSp", "设定温度", GuardKind.Control, true),
        new(DeviceAction.ProgLoad, "progLoad", "下发程序", GuardKind.Control, true),
        new(DeviceAction.ProgRun, "progRun", "运行程序", GuardKind.Control, true),
        new(DeviceAction.ProgAbort, "progAbort", "中止程序", GuardKind.Safe, true),
        new(DeviceAction.StirOn, "stirOn", "启动搅拌", GuardKind.Control, false),
        new(DeviceAction.StirOff, "stirOff", "停止搅拌", GuardKind.Safe, false),
        new(DeviceAction.StirSet, "stirSet", "设定转速", GuardKind.Control, false),
        new(DeviceAction.ChStirOn, "chStirOn", "启动搅拌", GuardKind.Control, true),
        new(DeviceAction.ChStirOff, "chStirOff", "停止搅拌", GuardKind.Safe, true),
        new(DeviceAction.ChStirSet, "chStirSet", "设定转速", GuardKind.Control, true),
        new(DeviceAction.RunAll, "runAll", "全部运行", GuardKind.Control, false),
        new(DeviceAction.StopAll, "stopAll", "全部停止", GuardKind.Safe, false),
        new(DeviceAction.Estop, "estop", "急停", GuardKind.Safe, false),
        new(DeviceAction.SessionStart, "sessionStart", "开始会话", GuardKind.Safe, false),
        new(DeviceAction.SessionMark, "sessionMark", "打标记", GuardKind.Safe, false),
        new(DeviceAction.SessionEnd, "sessionEnd", "结束会话", GuardKind.Safe, false),
        new(DeviceAction.AlarmAck, "alarmAck", "确认报警", GuardKind.Safe, false),
        new(DeviceAction.LeaseRequest, "leaseRequest", "申请控制权", GuardKind.Conn, false),
        new(DeviceAction.LeaseCancel, "leaseCancel", "取消申请", GuardKind.None, false),
        new(DeviceAction.LeaseRelease, "leaseRelease", "释放控制权", GuardKind.None, false),
        new(DeviceAction.Disconnect, "disconnect", "断开", GuardKind.None, false),
        new(DeviceAction.Reconnect, "reconnect", "重连", GuardKind.None, false),
        new(DeviceAction.Rename, "rename", "重命名", GuardKind.None, false),
        new(DeviceAction.Remove, "remove", "移除", GuardKind.None, false),
    };

    private static readonly Dictionary<string, ActionSpec> ByCode = BuildIndex();

    private static Dictionary<string, ActionSpec> BuildIndex()
    {
        var d = new Dictionary<string, ActionSpec>(StringComparer.Ordinal);
        foreach (var s in Table) d[s.Code] = s;
        return d;
    }

    /// <summary>全部操作（样稿 ACTIONS 的顺序）。</summary>
    public static IReadOnlyList<ActionSpec> All => Table;

    public static ActionSpec Spec(DeviceAction action) => Table[(int)action];

    /// <summary>样稿代码：'heatStart'。</summary>
    public static string Code(DeviceAction action) => Spec(action).Code;

    /// <summary>按钮文字：'启动加热'。</summary>
    public static string Label(DeviceAction action) => Spec(action).Label;

    /// <summary>守卫类别（样稿 ACTIONS[name].guard）。</summary>
    public static GuardKind KindOf(DeviceAction action) => Spec(action).Kind;

    /// <summary>需要控制权（样稿 needsLease：类别为 control）。</summary>
    public static bool NeedsLease(DeviceAction action) => KindOf(action) == GuardKind.Control;

    /// <summary>停止类（样稿 safe）：只要连接，永远不看控制权。</summary>
    public static bool IsStopClass(DeviceAction action) => KindOf(action) == GuardKind.Safe;

    /// <summary>样稿代码 → 操作；不认识返回 false。</summary>
    public static bool TryParse(string? code, out DeviceAction action)
    {
        if (code != null && ByCode.TryGetValue(code, out var s)) { action = s.Action; return true; }
        action = default;
        return false;
    }

    /// <summary>类别的样稿代码：'none' / 'conn' / 'safe' / 'control'。</summary>
    public static string KindCode(GuardKind kind) => kind switch
    {
        GuardKind.None => "none",
        GuardKind.Conn => "conn",
        GuardKind.Safe => "safe",
        _ => "control",
    };
}
