using System;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Commands;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>
/// 接口层与 HMI 之间的唯一接缝。Kestrel 线程只通过它拿数据：快照（只读、不可变）、事件存储（线程安全）、
/// 设备信息（静态）、程序 schema（静态 + 设置快照）。进 UI 线程的只有 <see cref="PreflightAsync"/>（§7.2）与 <see cref="Commands"/>（P3 写命令，§13.1），
/// 两者都经 Api/Hmi/UiCommandBridge 投递并带超时。控制权租约 <see cref="Lease"/> 是纯逻辑状态机（Commands/ControlLease），任意线程可调。
/// 主工程实现为 Api/Hmi/HmiApiSources；测试工程用假实现。
/// </summary>
public interface IApiSources
{
    SnapshotStore Snapshots { get; }
    EventStore Events { get; }
    string DataDir { get; }
    bool Simulated { get; }
    string BootId { get; }
    DateTime BootTime { get; }
    /// <summary>系统时钟是否已 NTP 同步（Linux timedatectl）；取不到为 null。</summary>
    bool? ClockSynced { get; }
    /// <summary>当前控制模式（ControlArbiter，线程安全）。</summary>
    ModeSnap Control { get; }
    /// <summary>远程控制租约状态机（持有 ControlArbiter；授权 / 拒绝 / 夺回由触屏经 OperatorCommands 调用，申请 / 心跳 / 释放由接口调用）。</summary>
    ControlLease Lease { get; }
    /// <summary>单调时间（与 <c>MainViewModel.MonoNow</c> 同源）：租约 / 幂等等计时用它，不受 NTP 校时影响。</summary>
    TimeSpan MonoNow { get; }
    /// <summary>远程写命令（每个端点一个方法；HMI 实现经 UI 线程执行 Try*）。</summary>
    IRemoteCommands Commands { get; }
    /// <summary>当前活动会话的 manifest（不可变快照，任意线程可读；无会话为 null）。</summary>
    SessionManifest? CurrentSession { get; }
    DeviceInfoDto Device(ApiConfig cfg);
    ProgramSchemaDto ProgramSchema();
    /// <summary>预运行检查（UI 线程，5 s 超时抛 <see cref="UiThreadTimeoutException"/>）。</summary>
    Task<PreflightDto> PreflightAsync(string clientName, CancellationToken ct);
    /// <summary>记一条接口事件（任意线程；来源「接口」，kind 按标题推导）。</summary>
    void Audit(string level, string title, string detail);
    /// <summary>
    /// 记一条带来源分类 / 客户端 / opId / 显式 kind 的审计事件（P3：<c>control.*</c>、<c>op.*</c>）。返回事件 seq（写命令响应的 opEventSeq；记不了为 0）。
    /// </summary>
    long Audit(string level, string title, string detail, string source, string? client, string? opId, string kind);
}

/// <summary>命令 / 读取在 5 s 内没能进入 UI 线程，已撤销，确定未执行（§11 UI_THREAD_TIMEOUT）。</summary>
public sealed class UiThreadTimeoutException : Exception
{
    public UiThreadTimeoutException() : base("UI 线程 5 s 内未响应，本次读取已撤销") { }
}
