using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 路由（融合版 F2，取代 pc-v3 的 Route + 页签）：<b>页面 + 选中的设备 + 选中的通道</b>，同样稿的 PAGE / VIEW / SEL。
/// <list type="bullet">
/// <item><see cref="Page"/>：当前页面（菜单栏 / 深蓝条的「设置」/ Ctrl+1…6）。</item>
/// <item><see cref="DeviceK"/>：选中的设备（设备面板、工具条的设备下拉）；0 = 不选设备（主页 = 总览，报警 / 记录 = 全部设备）。
///   一次看一台的页面（<see cref="PageScope.One"/>）不允许 0：进来时选第一台（样稿 goPage）。换页面时设备不变。</item>
/// <item>通道：每台设备记一个（样稿 SEL[k]，默认 RV1）；换设备再回来还是原来那一路。</item>
/// </list>
/// 设备没了（移除、换演示组合）时 <see cref="Prune"/>：选中的设备不在 → 一台看一台的页面选第一台，其它回到 0；通道不在 → RV1。
/// 只读设备数据（<see cref="IFleetView"/>），不碰界面；变化合并成一次 <see cref="Changed"/>。
/// </summary>
public sealed partial class NavVm : ObservableObject
{
    private readonly IFleetView _fleet;
    private readonly Dictionary<int, int> _ch = new();

    public NavVm(IFleetView fleet) => _fleet = fleet;

    [ObservableProperty] private AppPage _page = AppPage.Home;
    /// <summary>选中的设备位置（1号机 = 1）；0 = 总览 / 全部设备。</summary>
    [ObservableProperty] private int _deviceK;

    /// <summary>页面、设备或通道变了（一次操作只发一次）。</summary>
    public event EventHandler? Changed;

    public AppPageInfo Info => AppPages.Info(Page);
    public PageScope Scope => Info.Scope;

    /// <summary>不选设备（主页的总览 / 报警、记录的全部设备）。</summary>
    public bool IsAll => DeviceK == 0;

    public IFleetDevice? Device => DeviceK > 0 ? _fleet.Device(DeviceK) : null;

    /// <summary>选中设备的选中通道（RV 号）；不选设备时 0。</summary>
    public int Channel => DeviceK > 0 ? ChannelOf(DeviceK) : 0;

    /// <summary>这台设备选中的通道（没选过 = RV1）。</summary>
    public int ChannelOf(int k) => _ch.TryGetValue(k, out var n) ? n : 1;

    /// <summary>换页面（样稿 goPage）：一次看一台的页面没选设备时选第一台。返回是否变了。</summary>
    public bool Go(AppPage page)
    {
        int k = DeviceK;
        if (AppPages.Info(page).Scope == PageScope.One && k == 0) k = FirstK();
        if (page == Page && k == DeviceK) return false;
        Page = page;
        DeviceK = k;
        Raise();
        return true;
    }

    /// <summary>
    /// 选设备（设备面板、设备下拉、竖条的设备号小方块）：0 = 总览 / 全部设备（一次看一台的页面不接受 0）；设备不存在时不变。返回是否变了。
    /// </summary>
    public bool SelectDevice(int k)
    {
        if (k < 0) return false;
        if (k == 0 && Scope == PageScope.One) return false;
        if (k > 0 && _fleet.Device(k) == null) return false;
        if (k == DeviceK) return false;
        DeviceK = k;
        Raise();
        return true;
    }

    /// <summary>选通道（同时选中它的设备）：通道不在这台设备上时不变。返回是否变了。</summary>
    public bool SelectChannel(int k, int n)
    {
        var d = _fleet.Device(k);
        if (d == null || !d.Channels.Any(c => c.N == n)) return false;
        bool changed = DeviceK != k || ChannelOf(k) != n;
        _ch[k] = n;
        DeviceK = k;
        if (changed) Raise();
        return changed;
    }

    /// <summary>一次设好页面、设备、通道（截图 / 演示参数）；不合规的部分照上面的规则修正。</summary>
    public void Set(AppPage page, int k, int? n = null)
    {
        Page = page;
        DeviceK = k > 0 && _fleet.Device(k) != null ? k : 0;
        if (DeviceK > 0 && n is int nn && _fleet.Device(DeviceK)!.Channels.Any(c => c.N == nn)) _ch[DeviceK] = nn;
        Prune(force: true);
    }

    /// <summary>回到刚打开时的样子：主页 · 1号机 · 每台都选 RV1（样稿新开的页面 VIEW = 1、SEL 全是 1）。</summary>
    public void Reset()
    {
        _ch.Clear();
        Page = AppPage.Home;
        DeviceK = FirstK();
        Raise();
    }

    /// <summary>设备数据变了：选中的设备 / 通道不在了就修正（见类说明）。返回是否变了。</summary>
    public bool Prune() => Prune(force: false);

    private bool Prune(bool force)
    {
        bool changed = false;
        if (DeviceK > 0 && _fleet.Device(DeviceK) == null) { DeviceK = 0; changed = true; }
        if (DeviceK == 0 && Scope == PageScope.One)
        {
            int k = FirstK();
            if (k != DeviceK) { DeviceK = k; changed = true; }
        }
        foreach (var k in _ch.Keys.ToList())
        {
            var d = _fleet.Device(k);
            if (d == null || !d.Channels.Any(c => c.N == _ch[k])) { _ch.Remove(k); changed = true; }
        }
        if (changed || force) Raise();
        return changed;
    }

    private int FirstK() => _fleet.Devices.FirstOrDefault()?.K ?? 0;

    private void Raise()
    {
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(Channel));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>地址写法（截图日志）：「main?dev=1&amp;ch=1」。</summary>
    public override string ToString() =>
        Info.Key + "?dev=" + DeviceK.ToString(CultureInfo.InvariantCulture) + (DeviceK > 0 ? "&ch=" + Channel.ToString(CultureInfo.InvariantCulture) : "");
}
