using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>对话框按钮的样子（样稿 ui.btn 的 kind）：次要（默认）· 主要（紫色实心，每个对话框最多一个）· 主要 + 白色实心方块（全部停止的确认）· 红色实心（只给急停对话框的确认）。</summary>
public enum FootKind { Secondary, Primary, PrimaryStop, Danger }

/// <summary>
/// 对话框底栏的一个按钮（样稿 ui.btn(label, { kind, icon, size, reason })）：文字、图标、禁用原因可以随数据变（急停「急停所选 2 台」、原因「至少选择一台已连接的设备」），
/// 实例在对话框的生命周期内不变（每秒刷新不重建按钮，悬停 / 焦点不丢）。<see cref="Large"/> = 样稿 size: 'lg'（40 高）。
/// </summary>
public sealed partial class FootButton : ObservableObject
{
    public FootButton(string label, ICommand command, FootKind kind = FootKind.Secondary, string? icon = null, bool large = false)
    {
        _label = label;
        Command = command;
        Kind = kind;
        _icon = icon;
        Large = large;
    }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _icon;
    /// <summary>禁用原因（b:Pr.Reason：锁图标提示、点击 toast）；null = 可用。</summary>
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private bool _isVisible = true;
    /// <summary>悬停提示（样稿 ui.iconBtn 的 label / ui.btn 的 tip）。</summary>
    [ObservableProperty] private string? _tip;

    public FootKind Kind { get; }
    public bool Large { get; }
    public ICommand Command { get; }

    public bool IsSecondary => Kind == FootKind.Secondary;
    public bool IsPrimary => Kind is FootKind.Primary or FootKind.PrimaryStop;
    public bool IsPrimaryStop => Kind == FootKind.PrimaryStop;
    public bool IsDanger => Kind == FootKind.Danger;

    /// <summary>空串 → null（Pr.Reason 只认非空）。</summary>
    public static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>底栏右侧的一排按钮（样稿 .pr-modal-foot，间距 10，靠右）。</summary>
public sealed class FootBar
{
    public FootBar(params FootButton[] buttons) => Buttons = buttons;

    public IReadOnlyList<FootButton> Buttons { get; }

    /// <summary>同样的几个按钮（同一组实例、同样的顺序）。</summary>
    public bool SameAs(IReadOnlyList<FootButton> other)
    {
        if (other.Count != Buttons.Count) return false;
        for (int i = 0; i < other.Count; i++) if (!ReferenceEquals(other[i], Buttons[i])) return false;
        return true;
    }
}

/// <summary>底栏左侧：图标 + 文字（样稿 .pr-foot-left：申请控制权的「⏱ 倒计时结束自动取消」）。</summary>
public sealed record FootNote(string Icon, string Text);

/// <summary>
/// 设备在对话框里的样子（急停 / 全部停止的勾选行、申请控制权的设备卡片共用）：控制权徽标（在线）或连接徽标（断线），文字照样稿 ui.leaseBadge / ui.connBadge。
/// </summary>
public sealed partial class DeviceBadgeVm : ObservableObject
{
    /// <summary>在线：显示控制权徽标；断线：连接徽标。</summary>
    [ObservableProperty] private bool _showLease;
    [ObservableProperty] private LeaseState _lease;
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private string? _leaseTip;
    [ObservableProperty] private ConnState _conn;
    [ObservableProperty] private string _connText = "";
    [ObservableProperty] private string? _connTip;

    /// <param name="shortLease">树 / 列表的短写法（样稿 { short: true }：「远程控制」「申请中 45 s」，完整文字放提示）。</param>
    /// <param name="shortConn">连接徽标用短写法（设备管理表；完整文字放提示）。</param>
    public void Apply(DeviceVm d, bool shortLease, bool shortConn = false)
    {
        ShowLease = d.Conn == ConnState.Online;
        Lease = d.Lease;
        LeaseText = shortLease ? d.LeaseShort : d.LeaseText;
        LeaseTip = shortLease ? d.LeaseText : null;
        Conn = d.Conn;
        ConnText = shortConn ? d.ConnShort : d.ConnText;
        ConnTip = d.ConnText;
    }
}

internal static class DialogText
{
    /// <summary>样稿 PR.models.SAMPLE_NOTE。</summary>
    public static string SampleNote => Core.Models.ModelProfiles.SampleNote;

    public static string K(int k) => k.ToString(CultureInfo.InvariantCulture);

    /// <summary>对话框参数里的设备位置（样稿 dialog=name:k）。</summary>
    public static int? ParseK(string? arg) => int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : null;

    /// <summary>样稿 counts + ui.alarmText：「运行 4/4 · 提醒 1」。</summary>
    public static string RunText(DeviceVm d) => "运行 " + d.Running + "/" + d.Installed + (d.AlarmText.Length > 0 ? " · " + d.AlarmText : "");

    /// <summary>别名的字符宽度（样稿 aliasUnits；去掉首尾空白之后）。</summary>
    public static int AliasUnits(string? s) => Core.Guard.ActionGuard.AliasUnits(Core.Guard.ActionGuard.JsTrim(s ?? ""));

    /// <summary>设备不在了（被移除）：样稿 onDevices 关掉针对它的对话框。</summary>
    public static bool Gone(ShellVm shell, int k) => shell.Fleet.Device(k) == null;

    /// <summary>真机 / 演示的「在线」（样稿 PR.sim.online）。</summary>
    public static bool Online(DeviceVm d) => d.ConnStatus == ConnStatus.Online;
}
