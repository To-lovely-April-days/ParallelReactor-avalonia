using System.Threading.Tasks;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6（states-readonly-token 的检查器部分）：真机学到令牌只读之后，检查器控制权卡的说明写原因（不再写「需要控制权」——申请控制权也会被拒），
/// 只读又没有急停权限位时第二行写停止要什么；「申请控制权」带同一个原因。两台进程内的假 HMI 经真 ReactorConnection（同 LiveDeviceSourceTests.Live.cs）。
/// </summary>
public partial class LiveDeviceSourceTests
{
    [Fact]
    public async Task M6_LeaseCard_AfterA403_SaysWhatTheTokenCanDo()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var shell = await r.On(() => { var s = new ShellVm(r.Fleet); s.Toasts.Frozen = true; s.Select(1); return s; });
        var lease = await r.On(() => ((DeviceInspectorVm)shell.Inspector.Body!).Lease);
        Assert.Equal("本机只读 · 启动、设定等需要控制权\n停止类操作随时可用", await r.On(() => lease.Note));

        // 申请控制权被拒（403 FORBIDDEN_ROLE）→ 只读令牌；急停权限位还不知道：停止类照样可用
        a.Forbid.Add("POST /control/request");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.LeaseRequest, quiet: true));
        await r.Until(() => r.S(1).Role.ReadOnly, "学到只读");
        Assert.False(await r.On(() => r.S(1).Role.NoEstop));
        await r.On(() => { r.Fleet.RefreshNow(); shell.RefreshChrome(); });
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌\n停止类操作随时可用", await r.On(() => lease.Note));
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌", await r.On(() => lease.CtaReason));

        // 停止类也被拒 → 只读且没有急停权限位：第二行写停止要什么
        a.Forbid.Add("POST /stop-all");
        await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.StopAll, quiet: true));
        Assert.True(await r.On(() => r.S(1).Role is { ReadOnly: true, NoEstop: true }));
        await r.On(() => { r.Fleet.RefreshNow(); shell.RefreshChrome(); });
        Assert.Equal("1号机的令牌只能读取 · 需要 operator 令牌\n停止需要 operator 令牌或急停权限", await r.On(() => lease.Note));
        Assert.Empty(r.Ui.Errors);
    }
}
