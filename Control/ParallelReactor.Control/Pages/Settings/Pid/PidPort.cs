using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages.Settings.Pid;

/// <summary>
/// 温控 / PID 页对一台设备的读写（两种来源一个形状）：
/// <list type="bullet">
/// <item>真机：读 = <c>GET /pid</c>（<see cref="ReactorApiClient.PidAsync"/>，任何角色可读）；自整定 = <c>ControlledWrites.StartAutotuneAsync(n, tempC, band)</c>（要控制权）；
///   取消 = <c>DELETE /channels/{n}/autotune</c>（停止类，不要控制权）；手改 = <c>ControlledWrites.SetPidAsync</c>。「在整定」看最后一帧遥测的 channels[i].autotuning。</item>
/// <item>演示：Core 的 DemoFleet（DemoFleet.Pid.cs：样稿 PIDS、整定曲线）。</item>
/// </list>
/// 失败一律抛异常（真机 ApiException 等；演示 <see cref="InvalidOperationException"/>，消息 = 原因），调用方翻译成文字（DeviceActions.Describe）。
/// </summary>
public interface IPidPort
{
    /// <summary>现在能读（在线；演示：在线）。</summary>
    bool CanRead { get; }

    Task<PidDto> ReadAsync(CancellationToken ct);

    Task StartAsync(int n, double tempC, int band, CancellationToken ct);

    Task CancelAsync(int n, CancellationToken ct);

    Task SetPidAsync(int n, int band, double p, double i, double d, CancellationToken ct);

    /// <summary>第 n 路此刻在整定（遥测 / 演示状态）；不知道（没有帧）为 null。</summary>
    bool? Autotuning(int n);

    /// <summary>第 n 路这次整定开始的时刻（本地时间）：演示知道；真机不知道（null，页面按本机发起 / 第一次看到的时刻）。</summary>
    DateTime? StartedAt(int n);
}

/// <summary>按数据源选实现（实时 / 演示）；别的来源（测试的假数据源）为 null。</summary>
public static class PidPorts
{
    public static IPidPort? For(DeviceVm d) => d.Source switch
    {
        LiveDeviceSource live => new LivePidPort(live),
        DemoDeviceSource demo => new DemoPidPort(demo),
        _ => null,
    };
}

/// <summary>真机（LiveDeviceSource 的 ReactorConnection）。</summary>
public sealed class LivePidPort : IPidPort
{
    private readonly LiveDeviceSource _s;

    public LivePidPort(LiveDeviceSource s) => _s = s;

    private ParallelReactor.Client.IReactorConnection C => _s.Connection ?? throw new InvalidOperationException("未连接");

    public bool CanRead => _s.Connection != null && _s.Conn.Online;

    public Task<PidDto> ReadAsync(CancellationToken ct) => C.Rest.PidAsync(ct);

    public async Task StartAsync(int n, double tempC, int band, CancellationToken ct) =>
        await C.Control.StartAutotuneAsync(n, tempC, band, ct).ConfigureAwait(false);

    public async Task CancelAsync(int n, CancellationToken ct) => await C.Rest.CancelAutotuneAsync(n, ct).ConfigureAwait(false);

    public async Task SetPidAsync(int n, int band, double p, double i, double d, CancellationToken ct) =>
        await C.Control.SetPidAsync(n, band, p, i, d, ct).ConfigureAwait(false);

    public bool? Autotuning(int n) => _s.Latest?.Channels?.FirstOrDefault(c => c != null && c.Id == n)?.Autotuning;

    public DateTime? StartedAt(int n) => null;
}

/// <summary>演示（Core 的 DemoFleet）。</summary>
public sealed class DemoPidPort : IPidPort
{
    private readonly DemoDeviceSource _s;

    public DemoPidPort(DemoDeviceSource s) => _s = s;

    private DemoFleet F => _s.Fleet;
    private DemoDevice D => _s.Device;

    public bool CanRead => D.Online;

    public Task<PidDto> ReadAsync(CancellationToken ct) =>
        D.Online ? Task.FromResult(F.PidOf(D)) : Task.FromException<PidDto>(new InvalidOperationException(D.Alias + "未连接"));

    public Task StartAsync(int n, double tempC, int band, CancellationToken ct) => Run(F.StartAutotune(D, n, tempC, band));

    public Task CancelAsync(int n, CancellationToken ct) => Run(F.CancelAutotune(D, n));

    public Task SetPidAsync(int n, int band, double p, double i, double d, CancellationToken ct) => Run(F.SetPid(D, n, band, p, i, d));

    public bool? Autotuning(int n) => F.Autotuning(D, n);

    public DateTime? StartedAt(int n) => F.AutotuneStartedAt(D, n);

    private static Task Run(string why) => why.Length == 0 ? Task.CompletedTask : Task.FromException(new InvalidOperationException(why));
}
