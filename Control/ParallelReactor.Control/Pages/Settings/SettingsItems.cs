using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;

namespace ParallelReactor.Control.Pages.Settings;

/// <summary>
/// 设置页左侧导航的一行（样稿 #stNav）：组标题（.stg，13/600 t-3）/ 分隔（.stg.sep）/ 一节（.stn：14 px，选中 a-tint + 3 px 品牌蓝 + 600，「只读」标签）。
/// </summary>
public sealed partial class SetNavVm : ObservableObject
{
    public SetNavVm(string key, string label, bool readOnly, Action<string>? pick)
    {
        Key = key;
        _label = label;
        ReadOnly = readOnly;
        if (pick != null) PickCommand = new RelayCommand(() => pick(key));
    }

    /// <summary>组标题：键为空、文字为组名。</summary>
    public static SetNavVm Group(string text) => new("", text, false, null) { IsGroup = true };

    /// <summary>分隔线（「关于」上面）。</summary>
    public static SetNavVm Sep() => new("", "", false, null) { IsSep = true };

    public string Key { get; }
    public bool ReadOnly { get; }
    public bool IsGroup { get; private init; }
    public bool IsSep { get; private init; }
    public bool IsItem => !IsGroup && !IsSep;
    public ICommand? PickCommand { get; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isOn;
}

/// <summary>只读的一项（样稿 .frm 的「标签 | 值 单位 说明」，ro()：值 14/600 t-1、单位 14 t-2、说明 t-3）。</summary>
public sealed record SetRoRow(string Label, string Value, string Unit = "", string Note = "")
{
    public bool HasUnit => Unit.Length > 0;
    public bool HasNote => Note.Length > 0;
}

/// <summary>一组只读项（样稿 .grp 小标题 + .frm）。</summary>
public sealed record SetRoGroup(string Title, System.Collections.Generic.IReadOnlyList<SetRoRow> Rows)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>设备连接表的一行（样稿 conn：别名 · 型号 · 地址 · 端口 · 访问令牌（遮住 / 显示）· 自动连接 · 状态 · 编辑）。</summary>
public sealed partial class SetDevRowVm : ObservableObject
{
    public SetDevRowVm(int k, Action<SetDevRowVm> eye, Action<int> edit, Action<int, bool> auto)
    {
        K = k;
        EyeCommand = new RelayCommand(() => eye(this));
        EditCommand = new RelayCommand(() => edit(K));
        AutoCommand = new RelayCommand(() => auto(K, !AutoOn));
    }

    public int K { get; }
    public ICommand EyeCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand AutoCommand { get; }
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _token = SettingsTexts.TokenMask;
    [ObservableProperty] private bool _revealed;
    [ObservableProperty] private string _eyeTip = "显示令牌";
    [ObservableProperty] private bool _autoOn;
    [ObservableProperty] private string? _autoReason;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private BadgeTone _stateTone;
    [ObservableProperty] private bool _stateDot;
}

/// <summary>「PC 断开时设备怎么做」的一行（样稿 night 表：设备 · 断开时（保持运行带「推荐」）· 说明）。</summary>
public sealed record SetLostRow(string Alias, string Mode, bool Recommended, string Note);

/// <summary>模块表的一行（样稿 comm：模块 · 数量 · 状态 · 事务数 · 失败 · 最近响应）。</summary>
public sealed record SetModuleRow(string Name, string Count, bool Online, string State, string Polls, string Fails, string LastOk);

/// <summary>吸气量计算表的一行（显示用文字）。</summary>
public sealed record SetUptRowVm(string Rv, string Vessel, string Line, string Eff, string Sol, string Gas, string Res, bool GasBold,
    string Cal, BadgeTone CalTone, bool CalIsTag, bool Off, string Tip)
{
    public bool CalIsText => !CalIsTag;
    public bool ShowTouch => !Off;
}

/// <summary>「关于 / 帮助」的一条说明。</summary>
public sealed record SetHelpRow(string Title, string Text);

/// <summary>只读项的集合（就地替换，视图绑它）。</summary>
public sealed class SetRoGroups : ObservableCollection<SetRoGroup> { }
