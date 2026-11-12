using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Services;
using CoreActions = ParallelReactor.Control.Core.Guard.DeviceActions;

namespace ParallelReactor.Control.Fleet;

/// <summary>
/// 一台设备的操作（PORT-PLAN §5 M2「DeviceActions：命令 + 守卫 + toast + Describe(ex) + CommandResult.Warnings」；旧 MainViewModel 的
/// Controlled() / StopClass() / Describe(ex) 的去处）：
/// <list type="bullet">
/// <item><see cref="Why"/>：Core 守卫（ActionGuard.Why）给出的原因，按钮用 b:Pr.Reason 显示（锁图标提示、点击 toast 原因）。</item>
/// <item><see cref="RunAsync"/>：<b>执行前再查一次守卫</b>（界面上的状态可能已经过期）；控制类要连接 + 不在急停 + 本机远程控制中，
///   <b>停止类只要连接</b>（不看控制权，也不因为本机「以为」没在运行而拦下）；然后交给数据源执行，结果 / 原因 / 异常都 toast 出来；
///   命令带警告时另外提示一条 —— HW_UNCONFIRMED（硬件未在时限内确认）例外：只提示一条 warn「1号机 · RV1 停止 · 硬件未确认：…」，
///   代替 ✓ 结果 + 警告两条（M6：结果的「已停止」还没得到硬件确认）；最终结果由数据源跟着 HMI 的 op.*.result 事件再提示（LiveDeviceSource.Hw.cs）。</item>
/// <item><see cref="Command"/>：按钮用的 ICommand（同一个操作同一个实例）；不带 CanExecute（禁用由 Pr.Reason 表达），允许并发执行（M6：AsyncRelayCommand
///   默认在命令执行期间把按钮变成没有原因的禁用），执行时照样走守卫。</item>
/// </list>
/// 改名 / 移除是本机的事（实时设备不发给 HMI），由 FleetVm 处理。
/// </summary>
public sealed class DeviceActions
{
    private readonly DeviceVm _d;
    private readonly Dictionary<DeviceAction, ICommand> _commands = new();

    internal DeviceActions(DeviceVm device) => _d = device;

    /// <summary>
    /// 这个操作现在为什么不能做；能做返回空串（同样稿 PR.sim.why）。显示用：按这台设备这一刻的事实（DeviceVm.Facts，数据源一变就重取，
    /// 一次刷新里各个按钮、菜单项、时间轴行共用一份 —— R10）。执行时 <see cref="RunAsync"/> 另取最新的事实再查一次。
    /// </summary>
    public string Why(DeviceAction action, ActionPayload? payload = null) => ActionGuard.Why(_d.Facts, action, payload);

    /// <summary>按样稿代码问（'leaseRequest' …）；不认识返回「未知操作 …」。</summary>
    public string Why(string code, ActionPayload? payload = null) => ActionGuard.Why(_d.Facts, code, payload);

    /// <summary>只看连接 / 急停 / 控制权的原因（每个区域写一次，同样稿 PR.sim.guard）。</summary>
    public string GuardText(GuardKind kind) => ActionGuard.GuardText(_d.Facts, kind);

    /// <summary>按钮用的命令（无参数的操作；同一个操作同一个实例；执行期间不灰，见类注释）。</summary>
    public ICommand Command(DeviceAction action)
    {
        if (!_commands.TryGetValue(action, out var c))
            _commands[action] = c = new AsyncRelayCommand(() => RunAsync(action), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        return c;
    }

    /// <summary>带参数的命令（参数在执行时取，例如输入框里的设定值；执行期间不灰）。</summary>
    public ICommand Command(DeviceAction action, Func<ActionPayload?> payload) =>
        new AsyncRelayCommand(() => RunAsync(action, payload()), AsyncRelayCommandOptions.AllowConcurrentExecutions);

    /// <summary>
    /// 执行：先再查一次守卫（被拦下 → warn toast 原因），再交给数据源；结果 toast（停止类没在运行时 info 语气），警告另提示（HW_UNCONFIRMED 合成一条，见类注释），
    /// 异常翻译成文字（err）。<paramref name="quiet"/> = 不 toast，警告也不（多台 / 多个反应釜一起执行的对话框自己汇总：同一句警告合成一条，M6）。
    /// </summary>
    public async Task<ActionOutcome> RunAsync(DeviceAction action, ActionPayload? payload = null, bool quiet = false, CancellationToken ct = default)
    {
        var p = payload ?? ActionPayload.Empty;
        // 执行前用数据源此刻的事实再查一次（不用显示用的那份：界面上的状态可能已经过期）
        string why = ActionGuard.Why(_d.Source.Facts(), action, p);
        if (why.Length > 0)
        {
            if (!quiet) _d.Fleet.RaiseToast(why, ToastKind.Warn);
            return ActionOutcome.Failed(why);
        }
        try
        {
            ActionOutcome o;
            if (_d.Source is LiveDeviceSource && action is DeviceAction.Rename or DeviceAction.Remove)
                o = await _d.Fleet.LocalActionAsync(_d, action, p).ConfigureAwait(true);
            else
                o = await _d.Source.ExecAsync(action, p, ct).ConfigureAwait(true);
            if (!quiet)
            {
                if (o.Ok && HasHwUnconfirmed(o.Warnings))
                    // 硬件未确认：一条 warn 代替 ✓ 结果 + 警告（急停的结果本来是 err：不降成 warn）
                    _d.Fleet.RaiseToast(WarnLine(_d.Alias, What(action, p), o.Warnings), o.Tone == ToastKind.Err ? ToastKind.Err : ToastKind.Warn);
                else
                {
                    // 失败一般是 warn（原因）；数据源给了 err 的照旧 err（例如硬件的最终结果先到了、是失败）
                    if (o.Message.Length > 0) _d.Fleet.RaiseToast(o.Message, o.Ok || o.Tone == ToastKind.Err ? o.Tone : ToastKind.Warn);
                    if (WarningText(o.Warnings) is { } w) _d.Fleet.RaiseToast(_d.Alias + " · " + w, ToastKind.Warn);
                }
            }
            _d.Fleet.RequestRefresh();
            return o;
        }
        catch (Exception ex)
        {
            // 403 FORBIDDEN_ROLE：令牌没有这个权限 —— 记下（按设备缓存，PORT-PLAN §3.2 第 10 条 / R9），之后守卫直接禁用并写明原因。
            // 「该操作需要 admin 角色」不学：说明不了 operator 的权限（M3 复核）
            if (ex is ApiException { Code: OpCodes.ForbiddenRole } fe && !LiveDeviceSource.AdminOnly(fe.Message)) _d.Source.LearnForbidden(action);
            var msg = _d.Alias + " · " + Describe(ex);
            if (!quiet) _d.Fleet.RaiseToast(msg, ToastKind.Err);
            _d.Fleet.RequestRefresh();
            return ActionOutcome.Failed(msg, ToastKind.Err);
        }
    }

    /// <summary>
    /// 异常 → 给操作员看的一句话（旧 MainViewModel.Describe）：租约类 409 说清楚要先申请控制权；HW_UNCONFIRMED 不要直接重发；网络错误 / 超时；
    /// 其它 HMI 错误「状态码 代码：消息」。
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        ApiException a => a.Code switch
        {
            OpCodes.ModeLocal => "409 本地模式：触屏未开放远程控制，请先申请控制权",
            OpCodes.NotControlHolder => "409 非控制权持有者：请先申请远程控制",
            OpCodes.ControlLeaseExpired => "409 控制权已过期：请重新申请",
            OpCodes.HwUnconfirmed => HwUnconfirmedText,
            _ => a.Status + " " + a.Code + "：" + a.Message,
        },
        HttpRequestException h => "网络错误：" + h.Message,
        TaskCanceledException => "请求超时",
        _ => ex.Message,
    };

    /// <summary>HW_UNCONFIRMED 的说明（命令已下发、硬件没在时限内确认）。</summary>
    public const string HwUnconfirmedText = "硬件未确认：命令已下发但设备未在时限内确认，请核对属性卡 / 快照后再操作，勿直接重发";

    /// <summary>
    /// 命令结果的警告 → 提示文字（HW_UNCONFIRMED 用上面的说明，其它原样；重复的只写一次 ——「全部运行」逐个反应釜下发，每个都可能带 HW_UNCONFIRMED）；
    /// 没有警告返回 null。
    /// </summary>
    public static string? WarningText(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0) return null;
        return string.Join("；", warnings.Distinct().Select(w => w == OpCodes.HwUnconfirmed ? HwUnconfirmedText : w));
    }

    /// <summary>警告里有 HW_UNCONFIRMED（命令已下发，硬件没在时限内确认）。</summary>
    public static bool HasHwUnconfirmed(IReadOnlyList<string>? warnings) => warnings?.Contains(OpCodes.HwUnconfirmed) == true;

    /// <summary>提示里操作的说法：针对某个反应釜的带「RVn 」（「RV1 停止」），其它是按钮文字（「急停」「全部停止」）。</summary>
    public static string What(DeviceAction action, ActionPayload? payload) =>
        (CoreActions.Spec(action).PerChannel && payload?.N is int n ? "RV" + n + " " : "") + Label(action);

    /// <summary>
    /// 带警告的结果的一条提示（M6）：「1号机 · RV1 停止 · 硬件未确认：…」。<paramref name="who"/> = 别名（几台一起时「1号机、3号机」），
    /// <paramref name="what"/> = <see cref="What"/>（几个反应釜一起时「RV1、RV3 运行程序」）。
    /// </summary>
    public static string WarnLine(string who, string what, IReadOnlyList<string> warnings) => who + " · " + what + " · " + WarningText(warnings);

    /// <summary>操作的按钮文字（样稿 ACTIONS 的 label）。</summary>
    public static string Label(DeviceAction action) => CoreActions.Label(action);
}
