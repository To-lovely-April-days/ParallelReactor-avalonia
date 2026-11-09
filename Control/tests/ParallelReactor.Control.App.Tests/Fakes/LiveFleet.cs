using System;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 实时的 FleetVm + 假连接（M3：设备经 DeviceRegistry 添加，内存配置、不写任何文件）。每台设备一个 <see cref="FakeReactorConnection"/>，
/// 设备信息与第一帧取自实测样本（live-device.json / live-snapshot.json）。没有 UiContext、post 立即执行：事件和刷新都在调用线程上。
/// </summary>
internal static class LiveFleet
{
    /// <summary>空的实时 FleetVm；<paramref name="made"/> 收到每一个新建的假连接（按创建顺序）。</summary>
    public static FleetVm Create(Action<FakeReactorConnection>? setup = null, Action<FakeReactorConnection>? made = null, ControlConfig? config = null,
        ControlStateDto? control = null, Func<System.Net.Http.HttpMessageHandler>? http = null)
    {
        return FleetVm.CreateLive(config ?? ControlConfig.InMemory("PC-测试"), null, null, o =>
        {
            var fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
                ControlState = control,
            };
            setup?.Invoke(fake);
            made?.Invoke(fake);
            return fake;
        }, http);
    }

    /// <summary>一台连上的设备（「1号机」，127.0.0.1:8080，令牌 tok，不记住）。<paramref name="before"/> 在添加设备之前对 FleetVm 做的事。</summary>
    public static async Task<(FleetVm Fleet, DeviceVm Device, FakeReactorConnection Fake)> OneAsync(Action<ScriptedHttpHandler>? script = null,
        ControlStateDto? control = null, Action<FleetVm>? before = null)
    {
        FakeReactorConnection? fake = null;
        var f = Create(x => script?.Invoke(x.Http), x => fake = x, control: control);
        before?.Invoke(f);
        var d = f.AddDevice(new NewDevice("127.0.0.1", 8080, "tok", "1号机", AutoConnect: false, RememberToken: false)) ?? throw new InvalidOperationException("添加设备失败");
        await WaitOnline(d);
        f.RefreshNow();
        return (f, d, fake!);
    }

    /// <summary>等这台设备连上（假连接一般同步就连上了）。</summary>
    public static async Task WaitOnline(DeviceVm d)
    {
        for (int i = 0; i < 200 && d.Source.Conn.State != ConnStatus.Online; i++) await Task.Delay(10);
        if (d.Source.Conn.State != ConnStatus.Online) throw new TimeoutException(d.Alias + " 没有连上：" + (d.Source as LiveDeviceSource)?.LastError);
    }
}
