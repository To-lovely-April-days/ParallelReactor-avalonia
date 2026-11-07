using System;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>
/// 接口层与 HMI 之间的唯一接缝。Kestrel 线程只通过它拿数据：快照（只读、不可变）、事件存储（线程安全）、
/// 设备信息（静态）、程序 schema（静态 + 设置快照）。唯一进 UI 线程的读取是 <see cref="PreflightAsync"/>（设计稿 §7.2 /preflight）。
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
    DeviceInfoDto Device(ApiConfig cfg);
    ProgramSchemaDto ProgramSchema();
    /// <summary>预运行检查（UI 线程，5 s 超时抛 <see cref="UiThreadTimeoutException"/>）。</summary>
    Task<PreflightDto> PreflightAsync(string clientName, CancellationToken ct);
    /// <summary>记一条接口事件（任意线程；来源「接口」）。</summary>
    void Audit(string level, string title, string detail);
}

/// <summary>命令 / 读取在 5 s 内没能进入 UI 线程，已撤销，确定未执行（§11 UI_THREAD_TIMEOUT）。</summary>
public sealed class UiThreadTimeoutException : Exception
{
    public UiThreadTimeoutException() : base("UI 线程 5 s 内未响应，本次读取已撤销") { }
}
