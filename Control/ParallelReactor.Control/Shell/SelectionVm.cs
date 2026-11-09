using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 当前选中的对象（样稿 shell.sel：<b>单选</b>，一台设备或一个反应釜）：检查器显示它，资源管理器 / 时间轴高亮它。
/// 单击 = 选中（<see cref="Select"/>），双击 = 打开页签（ShellVm.Navigate，设备页 / 反应釜页导航时同时选中它）。
/// 选中的对象不在了（设备移除、反应釜未安装）时回到第一台设备（样稿 pruneSel）。
/// </summary>
public sealed partial class SelectionVm : ObservableObject
{
    private readonly FleetVm _fleet;

    public SelectionVm(FleetVm fleet) => _fleet = fleet;

    /// <summary>选中的设备位置（没有设备时 0）。</summary>
    [ObservableProperty] private int _k;
    /// <summary>选中的反应釜 RV 号（选中的是设备时 null）。</summary>
    [ObservableProperty] private int? _n;

    /// <summary>选中对象变了（设备或反应釜）。</summary>
    public event EventHandler? Changed;

    public DeviceVm? Device => _fleet.Device(K);

    /// <summary>选中的反应釜（选中的是设备时 null）。</summary>
    public ReactorVm? Reactor => N is int n ? Device?.Reactor(n) : null;

    /// <summary>选中设备（n = null）或它的一个反应釜；设备不存在时不变，反应釜不存在时选中设备。返回是否真的变了。</summary>
    public bool Select(int k, int? n = null)
    {
        var d = _fleet.Device(k);
        if (d == null) return false;
        int? nn = n is int v && d.Reactors.Any(r => r.N == v) ? v : null;
        if (K == k && N == nn) return false;
        K = k;
        N = nn;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>选中的对象不在了：选第一台（样稿 pruneSel）。返回是否变了。</summary>
    public bool Prune()
    {
        var d = _fleet.Device(K);
        if (d != null && (N == null || d.Reactors.Any(r => r.N == N))) return false;
        var first = _fleet.Devices.FirstOrDefault();
        int k = first?.K ?? 0;
        if (K == k && N == null) return false;
        K = k;
        N = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>是不是这台设备（本身被选中，不是它的反应釜）。</summary>
    public bool IsDevice(int k) => K == k && N == null;

    /// <summary>是不是这个反应釜。</summary>
    public bool IsReactor(int k, int n) => K == k && N == n;

    /// <summary>地址参数写法（样稿 sel=2 / sel=3/11）。</summary>
    public override string ToString() => K == 0 ? "" : N is int n ? K + "/" + n : K.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
