using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 一个对话框（样稿 PR.dialogs.register 的 render 结果：{ title, sub, icon, tone, w, body, foot }）。M3 的各个对话框继承它，
/// 把内容（Body）和底栏按钮（Footer）做成自己的视图模型 + DataTemplate（或直接给控件）。
/// </summary>
public partial class DialogVm : ObservableObject
{
    /// <summary>注册名（add / devices / lease / estop / stopall / edit / rename / remove / disconnect）。</summary>
    public string Name { get; init; } = "";
    /// <summary>参数（设备位置等；样稿 dialog=name:arg）。</summary>
    public string? Arg { get; init; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _sub;
    [ObservableProperty] private string? _icon = "info";
    /// <summary>急停类（图标块红底，样稿 tone: 'red'）。</summary>
    [ObservableProperty] private bool _isDanger;
    /// <summary>宽度（样稿 r.w，默认 560）。</summary>
    [ObservableProperty] private double _width = 560;
    [ObservableProperty] private object? _body;
    [ObservableProperty] private object? _footer;
    [ObservableProperty] private object? _footerLeft;

    /// <summary>对话框关闭之后（任何方式：×、遮罩、Esc、另开一个对话框）。</summary>
    public virtual void OnClosed() { }

    /// <summary>
    /// 数据变了 / 每秒（外壳在 FleetVm.Changed 时调用，样稿 refresh() 里的 renderDialog）：计数、说明、倒计时跟着变；对象设备没了时关闭自己
    /// （样稿 onDevices）。表单类对话框（样稿 live: false）不改用户正在填的内容。
    /// </summary>
    public virtual void Refresh() { }
}

/// <summary>
/// 对话框宿主（PORT-PLAN §2.1「对话框宿主 + 9 个对话框」：宿主 M2，对话框 M3（Shell/Dialogs）；样稿 shell.js openDialog / closeDialog）：
/// 遮罩（OverlayBrush，ZIndex 200）+ 居中的 DialogFrame；全局停止格在遮罩之上（ZIndex 201），对话框开着也能直接点急停。
/// 同一时间只有一个对话框：打开新的之前正常关闭旧的（OnClosed 照常执行）。点遮罩、×、Esc 关闭。
/// 对话框按名字注册（<see cref="Register"/>）；没注册的名字给一条提示（同样稿「对话框 … 未定义」）。
/// </summary>
public sealed partial class DialogHostVm : ObservableObject
{
    private readonly Dictionary<string, Func<string?, DialogVm?>> _factories = new(StringComparer.Ordinal);
    private readonly Action<string, ToastKind> _toast;

    public DialogHostVm(Action<string, ToastKind> toast) => _toast = toast;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen))] private DialogVm? _current;

    public bool IsOpen => Current != null;

    /// <summary>注册一个对话框：factory(arg) 返回对话框，返回 null 表示不能打开（例如设备已经没了）。</summary>
    public void Register(string name, Func<string?, DialogVm?> factory) => _factories[name] = factory;

    public bool IsRegistered(string name) => _factories.ContainsKey(name);

    /// <summary>按名字打开（样稿 shell.openDialog(name, arg)）。</summary>
    public void Open(string name, string? arg = null)
    {
        if (!_factories.TryGetValue(name, out var f)) { _toast("对话框 " + name + " 未定义", ToastKind.Warn); return; }
        var vm = f(arg);
        if (vm == null) return;
        Show(vm);
    }

    /// <summary>直接打开一个对话框实例。</summary>
    public void Show(DialogVm vm)
    {
        if (Current != null) Close();
        Current = vm;
    }

    public void Close()
    {
        var c = Current;
        if (c == null) return;
        Current = null;
        c.OnClosed();
    }

    /// <summary>关闭指定的对话框（它还开着时）：对话框自己的操作用它，不会误关掉已经换上来的另一个。</summary>
    public void Close(DialogVm vm)
    {
        if (ReferenceEquals(Current, vm)) Close();
    }

    /// <summary>当前对话框跟着数据刷新一次（见 <see cref="DialogVm.Refresh"/>）。</summary>
    public void RefreshCurrent() => Current?.Refresh();
}
