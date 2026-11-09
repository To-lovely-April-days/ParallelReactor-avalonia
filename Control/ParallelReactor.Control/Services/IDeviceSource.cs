using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 一台设备的数据源（PORT-PLAN §5 M2「App 服务」的 IDeviceSource）：演示 = <see cref="DemoDeviceSource"/>（Core 的 DemoFleet），
/// 实时 = <see cref="LiveDeviceSource"/>（一个 ReactorConnection）。视图模型（Fleet/DeviceVm、ReactorVm）只认这个接口，两种来源一套投影。
/// <para>线程：全部成员只在 UI 线程上读；<see cref="Changed"/> 是合并过的、在 UI 线程上触发（实时源把 WS 的事件攒起来，一次刷新一次）。</para>
/// <para>与 PORT-PLAN 草图的差异（按 Contracts / Client 实际有的类型改写）：</para>
/// <list type="bullet">
/// <item><c>ActionArgs</c> 用 Core 的 <see cref="ActionPayload"/>（N / Sp / Rpm / Id / Alias / Text，守卫与演示数据共用同一份参数）。</item>
/// <item><c>DeviceAction</c> 用 Core.Guard 的枚举（样稿 ACTIONS 的顺序与代码）。</item>
/// <item>多了几项草图里没有、两种来源都要给的：<see cref="Model"/>（型号表解析结果）、<see cref="Sn"/>、<see cref="Estop"/>、<see cref="Stir"/>、
///   <see cref="Read"/>（一个反应釜的显示数据，见 <see cref="ChannelReading"/> —— 演示数据没有线上的 TelemetrySnapshot，
///   两种来源都投影到这一个形状，ReactorVm 只有一条路径）、<see cref="Facts"/>（守卫的输入，Core.Guard.GuardFacts）、<see cref="IsDemo"/>。</item>
/// <item><see cref="Latest"/>：实时 = 最后一帧（性能测试、导出用）；演示 = null。<see cref="Info"/> / <see cref="Settings"/> / <see cref="Comm"/> 只有实时源有
///   （/device 在连上时取、/settings 连上后取一次，/device/comm 在 M4 才轮询 —— M2 恒为 null）。</item>
/// <item><see cref="Program"/>：演示 = 演示程序；实时 = null（程序台账 ProgramLedger 在 M5），HMI 的程序进度在 <see cref="ChannelReading.Hmi"/>。</item>
/// <item><see cref="Role"/>：本机令牌在这台设备上的权限（Core 的 TokenRole：第一个 403 FORBIDDEN_ROLE 之后学到并缓存，M3；演示数据恒为不知道）。
///   DeviceActions 遇到 403 时调 <see cref="LearnForbidden"/>。</item>
/// </list>
/// </summary>
public interface IDeviceSource : IAsyncDisposable
{
    /// <summary>本机给这台设备的身份（配置里的 Id；演示 = 按位置生成的固定 Guid）。</summary>
    Guid Id { get; }
    DeviceConfig Config { get; }
    ConnInfo Conn { get; }
    /// <summary>GET /device（实时，连上后才有）；演示 null。</summary>
    DeviceInfoDto? Info { get; }
    /// <summary>GET /settings（实时，连上后取一次）；演示 null。</summary>
    SettingsDto? Settings { get; }
    /// <summary>GET /device/comm（实时：连上时取一次，之后在线时每 5 s 一次，LiveDeviceSource.CommPollInterval）；演示 null。</summary>
    CommDto? Comm { get; }
    LeaseView Lease { get; }
    /// <summary>本机令牌的权限（第一个 403 之后学到；不知道 = 不限制）。守卫经 <see cref="Facts"/> 用它。</summary>
    TokenRole Role { get; }
    /// <summary>最后一帧遥测（实时）；演示 null。</summary>
    TelemetrySnapshot? Latest { get; }
    /// <summary>最后收到数据的时刻（本地时间；断线时停住；从未连上为 null）。</summary>
    DateTime? DataAt { get; }
    /// <summary>当前报警（未清除的，含已确认；info 级已过滤）= <see cref="AlarmBook"/>.Current 的投影（顺序相同）。</summary>
    IReadOnlyList<AlarmItem> Alarms { get; }
    /// <summary>事件，最新在前 = <see cref="EventBook"/>.Items 的投影。</summary>
    IReadOnlyList<EventItem> Events { get; }
    /// <summary>
    /// 报警簿（M4，Core 的 AlarmBook）：真机 = GET /alarms（连上 / HMI 重启时整个替换）+ WS alarm（增量），确认成功后先在本地标上；
    /// 演示 = DemoDevice.Alarms（同样稿 dev.alarms 的顺序）。页面的报警文字（Core.Alarms.AlarmTexts / AlarmStrip）都读它的 Current。
    /// </summary>
    AlarmBook AlarmBook { get; }
    /// <summary>事件簿（M4，Core 的 EventBook）：真机 = GET /events（最近 200 条，分页续取）+ WS event；演示 = DemoDevice.Events。</summary>
    EventBook EventBook { get; }
    /// <summary>
    /// 模块通讯（设备页「设备信息 › 模块通讯」，样稿 d.comm）：型号表的模块（名称、数量）+ 是否在线 + 最近一次通讯成功的时刻（本地时间）。
    /// 真机：在线取最新一帧的 CommSnap（没有帧时取 GET /device/comm），最近成功取 GET /device/comm 的 lastOkAt；演示：DemoDevice.Comm。
    /// </summary>
    IReadOnlyList<ModuleComm> Modules { get; }
    /// <summary>HMI 版本（'1.0.6'；真机 /device 的 hmiVersion 去掉「+提交号」）；还不知道时「—」。</summary>
    string HmiVersion { get; }
    /// <summary>接口版本（'v1.2' / '1.0'）；还不知道时「—」。</summary>
    string ApiVersion { get; }
    SessionInfo? Session { get; }
    /// <summary>HMI 不提供的反应釜数据（液位、每釜转速、冷却、mmol）：演示填写；实时 null（HMI 补接口后再填）。</summary>
    ChannelExtras? Extras(int n);
    /// <summary>反应釜上的程序（演示：演示程序；实时：M5 的程序台账，M2 为 null）。</summary>
    ProgramRecord? Program(int n);
    /// <summary>
    /// 检查器「程序」分区下拉里能下发到第 n 路的程序（样稿 d.programs：按型号的程序库）。演示 = 演示程序库；
    /// 实时 = 本机给这个反应釜的程序草稿（M2 的「下发」照旧下发它；按型号的程序库在 M7）。
    /// </summary>
    IReadOnlyList<ProgramChoice> ProgramChoices(int n);
    IChannelHistory History { get; }

    /// <summary>数据变了（合并后、在 UI 线程上触发）。</summary>
    event Action<SourceChange>? Changed;

    /// <summary>
    /// 执行一个操作。<b>不检查守卫</b>（守卫由 Fleet/DeviceActions 在执行前再查一次）；失败抛 ApiException 等（DeviceActions 翻译成文字）。
    /// 返回的结果带要提示的文字与语气、命令的警告（HW_UNCONFIRMED 等）。
    /// </summary>
    Task<ActionOutcome> ExecAsync(DeviceAction action, ActionPayload args, CancellationToken ct);

    /// <summary>
    /// 这个操作被 HMI 以 403 FORBIDDEN_ROLE 拒绝了：记下令牌的权限（<see cref="Role"/>，按设备缓存到换令牌为止），守卫随之禁用并写明原因。
    /// 演示数据：什么都不做。
    /// </summary>
    void LearnForbidden(DeviceAction action);

    Task RequestLeaseAsync();
    Task CancelLeaseRequestAsync();
    Task ReleaseLeaseAsync();
    Task ReconnectNowAsync();
    Task DisconnectAsync();

    // ———— 以下是草图之外、两种来源都给的（见类注释） ————

    bool IsDemo { get; }
    ModelProfile Model { get; }
    string Sn { get; }
    EstopInfo Estop { get; }
    /// <summary>全釜共用搅拌（null = 各釜独立搅拌的型号）。</summary>
    StirInfo? Stir { get; }
    /// <summary>第 n 路的显示数据（含未安装：Installed = false）。</summary>
    ChannelReading Read(int n);
    /// <summary>气路是否打开（型号表的进气 / 排空键，样稿 gas；只读，阀门在触屏操作）。</summary>
    bool GasOpen(string key);
    /// <summary>守卫的输入（一次刷新取一次，再问各个按钮）。</summary>
    GuardFacts Facts();
}

/// <summary>
/// 一台设备的本机配置（M3：devices.json 的一项，见 ControlConfig / DeviceEntry）：身份、别名、地址、端口、令牌、启动时自动连接。
/// 令牌只在内存里（是否落盘由 DeviceEntry.RememberToken 决定，D7）。
/// </summary>
public sealed record DeviceConfig(Guid Id, string Alias, string Host, int Port, string? Token = null, bool AutoConnect = false)
{
    /// <summary>接口根地址：http://host:port。</summary>
    public Uri BaseUrl => new("http://" + Host.Trim() + ":" + Port);
}

/// <summary>
/// 连接（样稿 conn）：状态、是否连上过、重连倒计时、上线 / 断线时刻；文字见 <see cref="Display"/>。
/// <see cref="StopReachable"/>：停止类命令现在能送达（真机：有连接对象且有证据表明 REST 通 —— 只是 WebSocket 在重连 / 被拒时也算，主机不通时不算；
/// 见 LiveDeviceSource.StopReachable）；演示数据不用（false，能不能停只看在线）。
/// <see cref="Attempts"/>：样稿 conn.attempts（总览的断线说明「12 s 后自动重连（已试 N 次）」，N = Attempts − 1，M4 用）：
/// 连上过的设备断线时从 1 起、每次重连失败加一；从未连上的设备 = 失败的连接次数；在线时 0。
/// </summary>
public sealed record ConnInfo(ConnStatus State, bool EverOnline, int RetryS = 0, DateTime? Since = null, DateTime? LostAt = null, bool StopReachable = false, int Attempts = 0)
{
    public bool Online => State == ConnStatus.Online;

    /// <summary>急停 / 全部停止 / 停止类能发：在线，或者真机只是 WebSocket 在重连（<see cref="StopReachable"/>）。</summary>
    public bool CanStop => Online || StopReachable;
    /// <summary>样稿 connInfo：'已连接' / '连接中断 · 重连中 12 s' / '正在连接…'。</summary>
    public ConnDisplay Display => DeviceTexts.Conn(State, EverOnline, RetryS);
}

/// <summary>
/// 控制权（样稿 lease + 真机第 5 种）：状态、申请 / 宽限倒计时、持有者；文字见 <see cref="Display"/>。
/// <see cref="HeartbeatDegraded"/>：本机远程控制中、但连续 ≥ 2 次心跳没送到（真机，Client 的 ControlLeaseClient.HeartbeatDegraded；HMI 还没判 remoteLost）——
/// 检查器 / 申请控制权对话框不再写「心跳正常」。演示数据恒为 false。
/// <see cref="WaitS"/>：申请等触屏确认的上限（秒）—— 申请控制权对话框倒计时环的满格：真机取 HMI 的 requestTimeoutSec（GET /control），演示 = 样稿 LEASE_WAIT_S 60。
/// </summary>
public sealed record LeaseView(LeaseStatus State, int RemainS, int GraceS, string Holder, LeaseDisplay Display, bool HeartbeatDegraded = false, int WaitS = 60);

/// <summary>急停：是否急停中、开始时刻、来源（真机没有来源字段，A4）。</summary>
public sealed record EstopInfo(bool Active, DateTime? At = null, string? By = null)
{
    public static readonly EstopInfo None = new(false);
}

/// <summary>全釜共用搅拌：开 / 关、转速（真机是设定值 RpmSp）。</summary>
public sealed record StirInfo(bool On, double Rpm);

/// <summary>一条当前报警（样稿 alarm；真机 AlarmDto 归一化：过滤 info，State ∈ {ack, done} 算已确认，代码按关键字推断）。</summary>
public sealed record AlarmItem(string Id, AlarmLevel Level, int? Ch, string Code, string Text, DateTime At, bool Acked, string? AckBy = null);

/// <summary>一条事件（样稿 event；最新在前）。</summary>
public sealed record EventItem(string Id, DateTime At, EventLevel Level, int? N, string Text);

/// <summary>
/// 一类通讯模块的现状（样稿 comm：key、name、count、online、lastOk）：<see cref="Online"/> = 这一类模块在线（设备断线时是断线前的），
/// <see cref="LastOk"/> = 最近一次通讯成功的时刻（本地时间；不知道为 null）。
/// </summary>
public sealed record ModuleComm(string Key, string Name, int Count, bool Online, DateTime? LastOk);

/// <summary>程序下拉的一项（样稿 select 的 option：value = 程序 id，label = 程序名）。</summary>
public sealed record ProgramChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>测试会话：记录中、会话号、开始时刻、标记个数。</summary>
public sealed record SessionInfo(bool Active, string? Id, DateTime? StartAt, int Markers);

/// <summary>HMI 没有、演示有的反应釜数据（样稿 Channel 的 volumeMl / rpm / stirOn / cooling / mmol）。</summary>
public sealed record ChannelExtras(double? VolumeMl, double? Rpm, bool StirOn, bool Cooling, double? Mmol);

/// <summary>曲线数据的量（样稿 series 的 key）。</summary>
public enum HistoryKey { TempC, PressureMPa, HeaterPct, Rpm, Mmol, SpC, PTargetMPa }

/// <summary>曲线数据（演示：60 min 预生成 + 每 10 s 追加；实时：TelemetryStore 的帧，M5 换成 10 s 一格的 ChannelHistory 环形缓冲）。</summary>
public interface IChannelHistory
{
    /// <summary>最近 <paramref name="minutes"/> 分钟的 (时刻（本地）, 值)，值缺失为 null，最旧在前。</summary>
    IReadOnlyList<(DateTime T, double? V)> Series(int n, HistoryKey key, double minutes = 60);
}

/// <summary>数据源这次变了什么（合并后的标志，订阅方一般全部刷新）。</summary>
[Flags]
public enum SourceChange
{
    None = 0,
    Frame = 1,
    Conn = 2,
    Lease = 4,
    Alarms = 8,
    Events = 16,
    Info = 32,
    Programs = 64,
    All = Frame | Conn | Lease | Alarms | Events | Info | Programs,
}

/// <summary>
/// 一次操作的结果：是否执行、要提示的文字（已带设备名）与语气、命令的警告（HMI 的 warnings，例如 HW_UNCONFIRMED）。
/// Info = 停止类在「本来就没在运行」时照样发送（样稿 info 语气「RV4 未在运行 · 已发送停止」）。
/// </summary>
public sealed record ActionOutcome(bool Ok, string Message, ToastKind Tone, IReadOnlyList<string> Warnings, bool Info = false)
{
    public static ActionOutcome Done(string message, ToastKind tone = ToastKind.Ok, IReadOnlyList<string>? warnings = null, bool info = false) =>
        new(true, message, tone, warnings ?? Array.Empty<string>(), info);

    public static ActionOutcome Failed(string message, ToastKind tone = ToastKind.Warn) => new(false, message, tone, Array.Empty<string>());
}

/// <summary>
/// 一个反应釜此刻的显示数据（两种来源的公共投影，ReactorVm 只读这个）。
/// <see cref="Key"/> / <see cref="Word"/> = 界面上的状态（未安装、设备断线「离线」/ 从未连上「未连接」、或反应釜自己的状态）；
/// <see cref="Own"/> / <see cref="OwnWord"/> = 反应釜自己的状态（断线时是断线前的，提示「离线 · 断线前 反应中」用）。
/// 读数：未安装 / 从未连上为 null；断线时是最后的值（界面另标「数据停在 …」）。压力一律 MPa（真机 psi ÷ 145.037738）。
/// </summary>
public sealed record ChannelReading(
    int N, bool Installed,
    StateKey Key, string Word,
    StateKey Own, string OwnWord, AlarmCause Cause,
    double? TempC, double? SpC, double? PressureMPa, double? PTargetMPa, double? HeaterPct, double? Rpm,
    bool Heating, bool Valve,
    HmiProgram? Hmi = null)
{
    /// <summary>未安装的位置。</summary>
    public static ChannelReading Uninstalled(int n) => new(n, false, StateKey.Uninstalled, StateKeys.Word(StateKey.Uninstalled),
        StateKey.Uninstalled, StateKeys.Word(StateKey.Uninstalled), AlarmCause.None, null, null, null, null, null, null, false, false);

    /// <summary>占用通道（自己的状态属于运行类，报警也算）。</summary>
    public bool IsRunning => Installed && StateKeys.IsRunning(Own);
}

/// <summary>HMI 的程序进度（ChannelProgramSnap 原样：没有程序名和步骤，程序台账在 M5）。</summary>
public sealed record HmiProgram(bool Running, int StepIndex, int StepCount, string State, string Message, DateTime? StartedAt, bool WaitingGas, string? StartedBy);
