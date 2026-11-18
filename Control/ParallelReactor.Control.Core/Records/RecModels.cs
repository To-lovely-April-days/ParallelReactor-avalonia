using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Records;

/// <summary>会话的状态（HMI SessionManifest.State：active / ended / interrupted）。</summary>
public enum RecState { Active, Ended, Interrupted }

/// <summary>
/// 保压类会话（样稿 rcHold）：泄漏测试（会话里开始过泄漏测试 / 会话号 LK-…）、气密预检（程序名「气密预检」）。
/// 保压类比较的是压降（psi/h），不比吸气量。
/// </summary>
public enum RecHold { None, Leak, Tight }

/// <summary>
/// 一条运行记录（融合版 F7，样稿 SESS 的一项）：会话号、哪台设备（位置 K）、名字、程序、操作员、由谁创建（本机客户端名 / 「触屏」）、
/// 通道（参与这次实验的 RV 号）、开始 / 结束（本地时间；进行中的会话 End = null）、状态、标记数、报警数、说明（中断原因）、提醒、保压类型。
/// 实时设备：GET /sessions + GET /sessions/{id} 的 manifest 拼出来（见 App 的 RecordsStore）；演示：Core 的 DemoRecords（同样稿 SESS）。
/// </summary>
public sealed record RecSession(
    string Id,
    int K,
    string Name,
    string Program,
    string Operator,
    string By,
    IReadOnlyList<int> Channels,
    DateTime Start,
    DateTime? End,
    RecState State,
    int Marks,
    int Alarms,
    string? Note = null,
    string? Warn = null,
    RecHold Hold = RecHold.None)
{
    /// <summary>时长（进行中的会话算到 <paramref name="now"/>）。</summary>
    public TimeSpan Duration(DateTime now) => (End ?? now) - Start;

    /// <summary>进行中。</summary>
    public bool Active => State == RecState.Active;

    /// <summary>程序名「—」或空：没有程序（泄漏测试）。</summary>
    public bool HasProgram => Program.Length > 0 && Program != "—";
}

/// <summary>
/// 会话里一路的一个采样：<see cref="M"/> = 距会话开始的分钟，温度 ℃、压力 MPa（表压）、累计吸气量 mmol；缺失为 NaN。
/// </summary>
public readonly record struct RecPoint(double M, double T, double P, double G);

/// <summary>
/// 一路的数据（给 <see cref="RecCompute"/>）：RV 号、采样（按时间排好）。
/// 进行中的会话另带这一路此刻的状态（<see cref="Now"/>）：有没有读数、在不在跑、是不是报警暂停、状态字 —— 决定「不计入统计」「未到」和状态列。
/// </summary>
public sealed record RecChannelSeries(int N, IReadOnlyList<RecPoint> Points, RecLiveState? Now = null);

/// <summary>
/// 进行中的会话里一路此刻的状态（样稿 rcData 实时分支的 c.t / c.st / c.gas）：<see cref="HasReading"/> 有温度读数（停用 / 未安装为 false）、
/// <see cref="Running"/> 程序在跑（升温 / 升压 / 反应 / 降温…，样稿 RUN）、<see cref="Cooling"/> 已到降温段（终值已定，算「到 90 %」）、
/// <see cref="Alarm"/> 报警暂停、<see cref="Word"/> 状态字（「反应中」「已就绪」…）、<see cref="Gas"/> 当前吸气量（设备算的，HMI gasConsumed）。
/// </summary>
public sealed record RecLiveState(bool HasReading, bool Running, bool Cooling, bool Alarm, string Word, double? Gas);

/// <summary>一次会话全部通道的数据：<see cref="Span"/> = 横轴长度（分钟，进行中 = 到现在）、<see cref="Live"/> = 进行中（数据来自本机内存里的历史缓存）。</summary>
public sealed record RecSeriesSet(IReadOnlyList<RecChannelSeries> Channels, double Span, bool Live)
{
    public static readonly RecSeriesSet Empty = new(Array.Empty<RecChannelSeries>(), 60, false);
}

/// <summary>结果表状态列的语气：普通 t-2、提醒（中断 / 有提醒）琥珀 600、报警（已暂停）红 600。</summary>
public enum RecTone { Normal, Warn, Err }

/// <summary>
/// 结果表的一行（样稿 rcData 的 rows 一项）：
/// <list type="bullet">
/// <item>吸气类：<see cref="G"/> 吸气终值（进行中 = 当前值）mmol、<see cref="Rate"/> 最大吸气速率 mmol/min（2 min 窗口）、<see cref="T90"/> 到 90 % 用时 min
///   （还在反应的为 null，显示「未到」）、<see cref="Used"/> 计入统计（没运行的「已就绪 · 不计入统计」）、<see cref="Live"/> 还在反应。</item>
/// <item>保压类：<see cref="Drop"/> 压降 psi/h（平衡后的判读窗口里按最小二乘斜率），<see cref="Ok"/> ≤ 判据。</item>
/// </list>
/// <see cref="Result"/> = 状态列的字（「反应中」「完成」「中断」「有提醒」「已暂停」「通过」「不通过」「已就绪」）。
/// </summary>
public sealed record RecRow(int N, IReadOnlyList<RecPoint> Points, bool Used, double G, double Rate, double? T90, bool Live,
    string Result, RecTone Tone, double Drop = 0, bool Ok = true);

/// <summary>统计（样稿 rcStats）：计入的路数、平均、样本标准差（n − 1）、RSD %。</summary>
public sealed record RecStats(int N, double Mean, double Sd, double Rsd)
{
    public static readonly RecStats Empty = new(0, 0, 0, 0);
}

/// <summary>
/// 一次会话的对比结果：保压类与否、每路一行、横轴长度、进行中、统计、没运行的通道（「RV5 未运行，不计入」）、
/// 保压类的测试压力（MPa，判读窗口开始时各路的平均）。
/// </summary>
public sealed record RecCompare(RecHold Hold, IReadOnlyList<RecRow> Rows, double Span, bool Live, RecStats Stats, IReadOnlyList<int> Skipped, double? TestMPa)
{
    public bool IsHold => Hold != RecHold.None;
    /// <summary>保压类的判据 psi/h（融合版 F8：设备设置 leakTest.rateLimitPsiPerHr，同性能测试页的泄漏测试；不知道时 HMI 默认 2）。</summary>
    public double HoldLimit { get; init; } = RecCompute.HoldLimitPsiH;
}
