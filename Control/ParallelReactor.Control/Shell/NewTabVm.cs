using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Shell;

/// <summary>新页签弹出框里的一个反应釜芯片（「● RV1」；未安装 = 虚线、t-dis、原因「RV5 未安装」）。</summary>
public sealed class NewTabRvVm
{
    public NewTabRvVm(int n, bool installed, StateKey key, ICommand go)
    {
        Text = "RV" + n;
        Installed = installed;
        Key = key;
        IsDone = key == StateKey.Done;
        Reason = installed ? null : Text + " 未安装";
        GoCommand = go;
    }

    public string Text { get; }
    public bool Installed { get; }
    public StateKey Key { get; }
    public bool IsDone { get; }
    public string? Reason { get; }
    public ICommand GoCommand { get; }
}

/// <summary>新页签弹出框里的一台设备：设备按钮（别名 + 型号 + 打开）+ 一排反应釜芯片（每行 8 个）。</summary>
public sealed class NewTabDeviceVm
{
    public NewTabDeviceVm(string alias, string model, ICommand go, IReadOnlyList<NewTabRvVm> rvs)
    {
        Alias = alias; Model = model; GoCommand = go; Rvs = rvs;
    }

    public string Alias { get; }
    public string Model { get; }
    public ICommand GoCommand { get; }
    public IReadOnlyList<NewTabRvVm> Rvs { get; }
}

/// <summary>新页签弹出框里的一个视图（第二阶段的占位页）。</summary>
public sealed record NewTabViewVm(string Title, string Icon, ICommand GoCommand);

/// <summary>
/// 「+」新页签弹出框（样稿 shell.js newTabHTML / .pr-nt）：「设备与反应釜」每台设备一张卡片（设备页 + 各反应釜页），「视图」三列（第二阶段的占位页）。
/// 打开时按当前数据生成；点了任何一项就导航并关闭（<see cref="Navigated"/>）。
/// </summary>
public sealed class NewTabVm
{
    public NewTabVm(ShellVm shell)
    {
        ICommand Go(Route r) => new RelayCommand(() => { shell.Navigate(r); Navigated?.Invoke(this, EventArgs.Empty); });
        Devices = shell.Fleet.Devices.Select(d => new NewTabDeviceVm(d.Alias, d.ModelLabel, Go(Route.Device(d.K)),
            d.Reactors.Select(c => new NewTabRvVm(c.N, c.Installed,
                !c.Installed ? StateKey.Uninstalled : d.Conn != Controls.Pr.ConnState.Online ? StateKey.Offline : c.Key,
                Go(Route.Reactor(d.K, c.N)))).ToList())).ToList();
        Views = Placeholders.All.Select(p => new NewTabViewVm(p.Title, p.Icon, Go(Route.Placeholder(p.Key)))).ToList();
    }

    public IReadOnlyList<NewTabDeviceVm> Devices { get; }
    public IReadOnlyList<NewTabViewVm> Views { get; }

    public event EventHandler? Navigated;
}
