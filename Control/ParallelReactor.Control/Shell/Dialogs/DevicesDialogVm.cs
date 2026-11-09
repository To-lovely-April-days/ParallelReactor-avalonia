using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 设备管理表的一行（样稿 dialogs.js 'devices' 的 cols）：别名 · 型号（「 · 」后的变体可折行）· SN · 地址 · 状态（连接短徽标，完整文字在提示）·
/// 控制权（在线：短徽标；断线：「—」）· 自动连接（复选框）· 操作（断开 / 连接、编辑、移除）。
/// </summary>
public sealed partial class DeviceRowVm : ObservableObject
{
    private readonly DevicesDialogVm _owner;

    public DeviceRowVm(DevicesDialogVm owner, int k)
    {
        _owner = owner;
        K = k;
        ConnectCommand = new RelayCommand(() => owner.Connect(K));
        EditCommand = new RelayCommand(() => owner.Edit(K));
        RemoveCommand = new RelayCommand(() => owner.Remove(K));
    }

    public int K { get; }
    public DeviceBadgeVm Badge { get; } = new();
    /// <summary>这张表共用的列宽（表头与各行）。</summary>
    public TableColumns Columns => _owner.Columns;
    public ICommand ConnectCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _aliasTip = "";
    [ObservableProperty] private string _modelName = "";
    /// <summary>型号的变体（「 · 现场」）；没有为空串。</summary>
    [ObservableProperty] private string _modelVariant = "";
    [ObservableProperty] private string _sn = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private bool _online;
    /// <summary>操作列第一个按钮：在线「断开」（unplug）/ 断线「连接」（link，带原因）。</summary>
    [ObservableProperty] private string _connLabel = "";
    [ObservableProperty] private string _connIcon = "link";
    [ObservableProperty] private string? _connReason;

    /// <summary>「自动连接」复选框（双向；变了交给对话框，样稿 'auto' 动作）。</summary>
    [ObservableProperty] private bool _autoConnect;
    /// <summary>勾不了的原因（实时设备没记住令牌，D7）；null = 能勾。</summary>
    [ObservableProperty] private string? _autoReason;
    public string AutoName => Alias + " 启动时自动连接";

    internal bool Syncing;

    partial void OnAutoConnectChanged(bool value)
    {
        if (!Syncing) _owner.SetAuto(K, value);
    }

    partial void OnAliasChanged(string value) => OnPropertyChanged(nameof(AutoName));
}

/// <summary>
/// 设备管理（样稿 dialogs.js 'devices'）：每台设备一行（表头 40、行 44），下面一条中性说明；底栏左「添加设备…」、右「关闭」（这个对话框没有主要按钮）。
/// 宽 1060；有长别名（超过 10 个字符宽度）时 1120（样稿给 Windows 字体留余量）。每秒刷新（连接 / 控制权徽标、重连倒计时）。
/// 「断开」：本机持有或正在申请控制权时先打开断开确认（样稿 requestDisconnect），否则直接断开；「连接」= 样稿 reconnect；编辑 / 移除打开各自的对话框
/// （同样稿：新对话框替换这个）。「自动连接」：演示同样稿（直接切换 + 提示）；真机要求记住了令牌（D7，没记住时复选框带原因）。
/// </summary>
public sealed partial class DevicesDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private bool _closed;

    public DevicesDialogVm(ShellVm shell)
    {
        _shell = shell;
        Name = "devices";
        Title = "设备管理";
        Icon = "list";
        Width = 1060;
        AddButton = new FootButton("添加设备…", new RelayCommand(() => shell.OpenDialog("add")), icon: "plus");
        FooterLeft = AddButton;
        Footer = new FootBar(new FootButton("关闭", new RelayCommand(() => shell.Dialogs.Close(this))));
        Note = "每台设备一个接口地址、一个访问令牌、一个控制权。断开 / 移除前，本机持有或正在申请的控制权会先释放。移除设备不影响设备上的程序与数据。" + DialogText.SampleNote + "。";
        Body = this;
        Refresh();
    }

    public FootButton AddButton { get; }
    public ObservableCollection<DeviceRowVm> Rows { get; } = new();
    /// <summary>
    /// 表格的列宽（8 列；按内容宽、多出来的按比例分，同样稿的 HTML 表格）。控制权一列放不下时可以缩窄（真机第 5 种「其他客户端 PC-工位2」这类长字：
    /// 徽标的字省略、完整文字在悬停提示里；样稿的字都放得下，不会缩）。
    /// </summary>
    public TableColumns Columns { get; } = new(8, 5);
    public string Note { get; }
    [ObservableProperty] private bool _isEmpty;

    public override void Refresh()
    {
        if (_closed) return;
        var f = _shell.Fleet;
        var ds = f.Devices;
        Sub = "已添加 " + ds.Count + " 台 · 已连接 " + ds.Count(DialogText.Online) + " 台";
        // 长别名（最长 8 个汉字 / 16 个字母）：对话框加宽一点（样稿 long ? 1120 : 1060）
        Width = ds.Any(d => DialogText.AliasUnits(d.Alias) > 10) ? 1120 : 1060;
        IsEmpty = ds.Count == 0;
        for (int i = Rows.Count - 1; i >= 0; i--) if (ds.All(d => d.K != Rows[i].K)) Rows.RemoveAt(i);
        for (int i = 0; i < ds.Count; i++)
        {
            var d = ds[i];
            var row = Rows.FirstOrDefault(r => r.K == d.K);
            if (row == null) { row = new DeviceRowVm(this, d.K); Rows.Insert(Math.Min(i, Rows.Count), row); }
            else if (Rows.IndexOf(row) != i) Rows.Move(Rows.IndexOf(row), Math.Min(i, Rows.Count - 1));
            row.Alias = d.Alias;
            row.AliasTip = d.Alias + " · " + d.ModelLabel;
            row.ModelName = d.Model.Name;
            row.ModelVariant = string.IsNullOrEmpty(d.Model.Variant) ? "" : " · " + d.Model.Variant;
            row.Sn = d.Sn.Length > 0 ? d.Sn : Core.Format.Fmt.Dash;
            row.Address = d.Host + ":" + d.Port;
            row.Online = DialogText.Online(d);
            row.ConnLabel = row.Online ? "断开" : "连接";
            row.ConnIcon = row.Online ? "unplug" : "link";
            row.ConnReason = row.Online ? null : FootButton.Nz(d.Why("reconnect"));
            row.Badge.Apply(d, shortLease: true, shortConn: true);
            row.Syncing = true;
            row.AutoConnect = f.AutoConnectOf(d.K);
            row.AutoReason = FootButton.Nz(f.AutoConnectReason(d.K));
            row.Syncing = false;
        }
    }

    internal void Connect(int k)
    {
        var d = _shell.Fleet.Device(k);
        if (d == null) return;
        if (DialogText.Online(d)) _shell.RequestDisconnect(d);
        else _ = d.Actions.RunAsync(DeviceAction.Reconnect);
        Refresh();
    }

    internal void Edit(int k) => _shell.OpenDialog("edit", DialogText.K(k));

    internal void Remove(int k) => _shell.OpenDialog("remove", DialogText.K(k));

    internal void SetAuto(int k, bool on)
    {
        var d = _shell.Fleet.Device(k);
        if (d == null) return;
        var r = _shell.Fleet.SetAutoConnect(k, on);
        if (r.Length > 0) _shell.Toast(r, ToastKind.Warn);
        else _shell.Toast(d.Alias + (on ? " 启动时自动连接" : " 启动时不自动连接"), ToastKind.Info);
        Refresh();
    }

    public override void OnClosed() => _closed = true;
}
