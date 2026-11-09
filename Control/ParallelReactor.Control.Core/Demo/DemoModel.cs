using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>连接（样稿 conn）：状态、上线 / 断线时刻、重连倒计时、连接倒计时、演示强制断线、重试次数、是否连上过。</summary>
public sealed class DemoConn
{
    public ConnStatus State { get; internal set; }
    public DateTime? Since { get; internal set; }
    public DateTime? LostAt { get; internal set; }
    /// <summary>断线时距下次重连的秒数。</summary>
    public int RetryS { get; internal set; }
    /// <summary>正在连接时剩余的秒数。</summary>
    public int ConnectS { get; internal set; }
    /// <summary>演示强制断线（重连会一直失败，直到参数 / 演示条恢复）。</summary>
    public bool Forced { get; internal set; }
    public int Attempts { get; internal set; }
    /// <summary>连上过（false = 新添加、从未连上）。</summary>
    public bool EverOnline { get; internal set; }
}

/// <summary>控制权（样稿 lease）：状态、持有者、申请倒计时、宽限倒计时、演示自动批准时刻、开始时刻。</summary>
public sealed class DemoLease
{
    public LeaseStatus State { get; internal set; }
    public string Holder { get; internal set; } = DeviceTexts.TouchScreen;
    /// <summary>申请中：剩余秒数。</summary>
    public int RemainS { get; internal set; }
    /// <summary>宽限中：剩余秒数。</summary>
    public int GraceS { get; internal set; }
    /// <summary>演示：申请后 5 s 当作触屏已确认的时刻；没有为 null。</summary>
    public DateTime? AutoAt { get; internal set; }
    public DateTime Since { get; internal set; }
}

/// <summary>急停（样稿 estop）：是否急停中、时刻、来源（'触屏 / 急停按钮' / 本机客户端名）。</summary>
public sealed class DemoEstop
{
    public bool Active { get; internal set; }
    public DateTime? At { get; internal set; }
    public string? By { get; internal set; }
}

/// <summary>全釜共用搅拌（样稿 stir）：是否在转、转速。独立搅拌的型号没有（DemoDevice.Stir = null）。</summary>
public sealed class DemoStir
{
    public bool On { get; internal set; }
    public double Rpm { get; internal set; }
}

/// <summary>会话标记：时刻、文字、反应釜号（没有为 null）。</summary>
public sealed record DemoMarker(DateTime At, string Text, int? N);

/// <summary>测试会话（样稿 session）：记录中、会话号、开始 / 结束时刻、标记。</summary>
public sealed class DemoSession
{
    internal readonly List<DemoMarker> MarkerList = new();

    internal DemoSession(string id, DateTime startAt) { Id = id; StartAt = startAt; Markers = MarkerList.AsReadOnly(); }

    public bool Active { get; internal set; } = true;
    public string Id { get; }
    public DateTime StartAt { get; }
    public DateTime? EndAt { get; internal set; }
    public IReadOnlyList<DemoMarker> Markers { get; }
}

/// <summary>
/// 一条报警（样稿 alarm）：id（'a1' …）、级别（错误报警 / 提醒）、反应釜号（设备级为 null）、代码（P-HIGH / HEAT-SLOW / ESTOP）、
/// 文字、时刻、是否已确认、确认人、是否演示参数加的（alarm=k:1）。
/// </summary>
public sealed class DemoAlarm
{
    internal DemoAlarm(string id, AlarmLevel level, int? ch, string code, string text, DateTime at, bool demo)
    { Id = id; Level = level; Ch = ch; Code = code; Text = text; At = at; Demo = demo; }

    public string Id { get; }
    public AlarmLevel Level { get; }
    public int? Ch { get; }
    public string Code { get; }
    public string Text { get; }
    public DateTime At { get; }
    public bool Acked { get; internal set; }
    public string? AckBy { get; internal set; }
    public bool Demo { get; }
}

/// <summary>一条事件（样稿 event，最新在前）：id（'e1' …）、时刻、级别、反应釜号、文字。</summary>
public sealed record DemoEvent(string Id, DateTime At, EventLevel Level, int? N, string Text);

/// <summary>模块通讯（样稿 comm）：类别（temp / press / stir / valve）、名称、数量、是否在线、最后一次通讯成功的时刻。</summary>
public sealed class DemoComm
{
    internal DemoComm(string key, string name, int count) { Key = key; Name = name; Count = count; }

    public string Key { get; }
    public string Name { get; }
    public int Count { get; }
    public bool Online { get; internal set; }
    public DateTime? LastOk { get; internal set; }
}

/// <summary>曲线采样（样稿 Sample，每 10 s 一个）：时刻、温度、压力（MPa）、加热输出 %、转速、耗气 mmol、温度设定值（加热 / 冷却时）、目标压力。</summary>
public readonly record struct DemoSample(DateTime T, double TempC, double PressureMPa, double HeaterPct, double Rpm, double? Mmol, double? SpC, double? PTargetMPa);

/// <summary>可读的量（样稿 reading(dev, ch, key) / series(dev, n, key) 的 key）。</summary>
public enum DemoReading { TempC, PressureMPa, HeaterPct, Rpm, Mmol, VolumeMl, SpC, PTargetMPa }

/// <summary>
/// 一个反应釜（样稿 Channel）。状态 <see cref="State"/> 是反应釜自己的状态（不按设备断线改写；显示用 DemoDevice.StateKeyOf）。
/// 压力一律 MPa；独立搅拌的型号才看 <see cref="StirOn"/> / <see cref="Rpm"/>（共用搅拌看 DemoDevice.Stir，有效转速用 DemoDevice.RpmOf）。
/// </summary>
public sealed class DemoChannel
{
    internal readonly List<DemoSample> HistoryList = new();

    internal DemoChannel(int n, bool installed) { N = n; Installed = installed; History = HistoryList.AsReadOnly(); }

    public int N { get; }
    public bool Installed { get; }
    public StateKey State { get; internal set; }
    public double TempC { get; internal set; }
    public double? SpC { get; internal set; }
    public double PressureMPa { get; internal set; }
    public double? PTargetMPa { get; internal set; }
    public double HeaterPct { get; internal set; }
    public bool Heating { get; internal set; }
    /// <summary>冷却水降温中（只有有冷却水的型号）。</summary>
    public bool Cooling { get; internal set; }
    public double? Mmol { get; internal set; }
    public double VolumeMl { get; internal set; }
    /// <summary>进气阀 SVn 开。</summary>
    public bool Sv { get; internal set; }
    public bool StirOn { get; internal set; }
    public double Rpm { get; internal set; }
    /// <summary>升温速率 ℃/s（演示物理用）。</summary>
    public double HeatRate { get; internal set; }
    /// <summary>升压速率 MPa/s（演示物理用）。</summary>
    public double PRate { get; internal set; }
    public ProgramRecord? Program { get; internal set; }
    public bool Alarm { get; internal set; }

    /// <summary>60 min 预生成 + 每 10 s 追加的历史（保留 4 h），最旧在前。未安装为空。</summary>
    public IReadOnlyList<DemoSample> History { get; }

    /// <summary>占用通道（样稿 isRunning：已安装且状态属于 RUNNING，报警也算）。</summary>
    public bool IsRunning => Installed && StateKeys.IsRunning(State);
}

/// <summary>设备的统计（样稿 counts）：已安装、运行中、已启用（非停用）、报警条数（错误 + 提醒）、未确认、错误报警。</summary>
public readonly record struct DemoCounts(int Installed, int Running, int Enabled, int Alarms, int Unacked, int ErrAlarms);

/// <summary>界面上的报警计数（样稿 ui.alarmCounts）：错误报警（「报警」，红）、提醒（中性）、合计。</summary>
public readonly record struct AlarmTally(int Err, int Warn, int Total);

/// <summary>全部设备汇总（样稿 totals，状态栏用）：设备数、已连接、远程控制（只算在线）、宽限中、报警条数、记录中的会话。</summary>
public sealed record DemoTotals(int Devices, int Connected, int Remote, int Grace, int Alarms, IReadOnlyList<(DemoDevice Device, string Id)> Sessions);

/// <summary>演示数据变化的种类：每秒推进、操作改了状态、设备增删 / 重建、参数覆盖。</summary>
public enum DemoChangeKind { Tick, State, Devices, Params }

/// <summary>演示数据变化通知：种类 + 相关设备（全体变化为 null）+ 操作（State 时）。</summary>
public sealed class DemoChangedEventArgs : EventArgs
{
    public DemoChangedEventArgs(DemoChangeKind kind, DemoDevice? device = null, Guard.DeviceAction? action = null) { Kind = kind; Device = device; Action = action; }

    public DemoChangeKind Kind { get; }
    public DemoDevice? Device { get; }
    public Guard.DeviceAction? Action { get; }
}

/// <summary>提示的语气（样稿 toast kind）：ok 白色 ✓、info 强调色点、warn 灰点、err 红点。</summary>
public enum DemoToastKind { Ok, Info, Warn, Err }

/// <summary>演示数据要给用户的一条提示（样稿 toast：控制权申请超时、宽限期结束、操作结果……）。</summary>
public sealed class DemoToastEventArgs : EventArgs
{
    public DemoToastEventArgs(string text, DemoToastKind kind) { Text = text; Kind = kind; }

    public string Text { get; }
    public DemoToastKind Kind { get; }
}

/// <summary>操作的结果（样稿 act）：是否执行、提示文字（被拦下时是原因）、是否只是说明（停止类在「本来就没在运行」时照样发送）。</summary>
public readonly record struct DemoActResult(bool Ok, string Message, bool Info = false);

/// <summary>「添加设备」的输入（样稿 addDevice）。</summary>
public sealed record DemoNewDevice(string? Host = null, int? Port = null, string? Token = null, string? Alias = null, bool? AutoConnect = null, string? ModelKey = null);

/// <summary>「编辑设备」的输入（样稿 editDevice）：null = 不改。</summary>
public sealed record DemoDeviceEdit(string? Alias, string? Host = null, int? Port = null, string? Token = null, bool? AutoConnect = null);
