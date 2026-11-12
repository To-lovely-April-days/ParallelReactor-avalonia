using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 本机客户端名（真机；M3 复核新加，样稿没有这个对话框 —— 样稿把它放在第二阶段的「设置」页，之前没有地方改，而每台电脑都叫「PC-测试工位1」时
/// 触屏弹窗、审计记录分不清是谁，R8 的持有者比较也会把别的 PC 当成本机）。从「设备 › 本机客户端名…」打开（演示数据不注册入口）。
/// 外观照样稿的重命名：一栏输入（就地校验，错误红字）+ 中性说明；「保存」= FleetVm.SetClientName（写进 devices.json；菜单栏右端跟着变；
/// 每台设备下一次新建连接时用新名字）。新加的文字：标题 / 副标题 / 栏名 / 说明 / 两句校验（ControlConfig.ClientNameError）/ 提示「本机客户端名已改为 X」。
/// </summary>
public sealed partial class ClientNameDialogVm : DialogVm
{
    /// <summary>设备菜单里的项（添加设备的说明里也引用它）。</summary>
    public const string MenuLabel = "本机客户端名…";

    /// <summary>输入框下面的说明（没有错误时）。</summary>
    public const string HintText = "最多 64 个字符 · 申请控制权时显示在设备触屏上";

    private readonly ShellVm _shell;

    private ClientNameDialogVm(ShellVm shell)
    {
        _shell = shell;
        Name = "client";
        Title = "本机客户端名";
        Sub = "触屏弹窗与审计记录靠它区分电脑：每台电脑用不同的名字";
        Icon = "monitor";
        Width = 520;
        _value = shell.Fleet.ClientName;
        SaveButton = new FootButton("保存", new RelayCommand(Save), FootKind.Primary, "check");
        Footer = new FootBar(new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this))), SaveButton);
        Body = this;
        Validate();
    }

    /// <summary>演示数据没有入口（客户端名是样稿的固定值）：返回 null（不打开）。</summary>
    public static ClientNameDialogVm? Create(ShellVm shell) => shell.Fleet.IsDemo ? null : new ClientNameDialogVm(shell);

    public FootButton SaveButton { get; }

    [ObservableProperty] private string _value;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _hint = HintText;

    /// <summary>中性说明：改了之后什么时候生效。</summary>
    public string Note => "已经连着的设备在下次连接时改用新名字；本机正持有的控制权不受影响。";

    /// <summary>界面上的说明（同 <see cref="Note"/>，两句各自不折断：放不下时在分号后换行）。</summary>
    public IReadOnlyList<TextSeg> NoteSegs { get; } = new[] { new TextSeg("已经连着的设备在下次连接时改用新名字；", NoWrap: 1), new TextSeg("本机正持有的控制权不受影响。", NoWrap: 2) };

    partial void OnValueChanged(string value) => Validate();

    private void Validate()
    {
        Error = _shell.Fleet.ClientNameError(Value);
        HasError = Error.Length > 0;
        Hint = HasError ? Error : HintText;
        if (SaveButton != null) SaveButton.Reason = FootButton.Nz(Error);
    }

    private void Save()
    {
        var err = _shell.Fleet.SetClientName(Value);
        if (err.Length > 0) { Error = err; HasError = true; Hint = err; return; }
        _shell.Dialogs.Close(this);
        _shell.Toast("本机客户端名已改为 " + _shell.Fleet.ClientName, ToastKind.Ok);
    }
}
