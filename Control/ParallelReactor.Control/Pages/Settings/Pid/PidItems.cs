using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Pid;

namespace ParallelReactor.Control.Pages.Settings.Pid;

/// <summary>
/// 档位卡片（样稿 .pidtab）：名字 14/600、温区 13 t-2、「n/m 路已整定」12.5 t-3；选中 = 品牌蓝边 + 底边 3 px + a-tint 底；
/// 超过本机上限的档 55 % 不透明、点不了（「超过本机上限 200 °C」）。
/// </summary>
public sealed partial class PidBandCardVm : ObservableObject
{
    public PidBandCardVm(int index, Action<int> pick)
    {
        Index = index;
        PickCommand = new RelayCommand(() => pick(Index));
    }

    public int Index { get; }
    public ICommand PickCommand { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _usable = true;
    /// <summary>不能选时的原因（b:Pr.Reason）。</summary>
    [ObservableProperty] private string? _reason;
}

/// <summary>状态列的样式：灰字 / 标签（品牌蓝「整定中」、琥珀「未整定」、绿「整定完成」）。</summary>
public enum PidTone { Mute, Brand, Warn, Run }

/// <summary>
/// PID 表的一行（样稿 pidt）：勾选 + 识别色竖条 + RV 号；P / I (s) / D (s) 输入（失焦 / 回车写入这一档，SetPidAsync）；整定温度、整定日期（窄屏不显示）、状态。
/// 选中或在整定的行 a-tint 底 + 左边 3 px 品牌蓝。
/// </summary>
public sealed partial class PidRowVm : ObservableObject
{
    private readonly Action<PidRowVm> _commit, _check;
    internal bool Syncing;
    /// <summary>正在写入（写完重读之前不拿设备的值覆盖输入框）。</summary>
    internal bool Pending;

    public PidRowVm(int n, IBrush color, Action<PidRowVm> commit, Action<PidRowVm> check)
    {
        N = n;
        Rv = "RV" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Color = color;
        _commit = commit;
        _check = check;
    }

    public int N { get; }
    public string Rv { get; }
    public IBrush Color { get; }

    [ObservableProperty] private bool _checked;
    [ObservableProperty] private bool _canCheck;
    [ObservableProperty] private string? _checkReason;
    [ObservableProperty] private string _p = "";
    [ObservableProperty] private string _i = "";
    [ObservableProperty] private string _d = "";
    /// <summary>有 P / I / D（停用的通道、还没读到时写「—」）。</summary>
    [ObservableProperty] private bool _hasValues;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private string? _editReason;
    [ObservableProperty] private string _tunedSp = "—";
    [ObservableProperty] private string _tunedAt = "—";
    [ObservableProperty] private string? _tunedTip;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTag), nameof(IsBrand), nameof(IsWarn), nameof(IsRun))] private PidTone _tone;
    /// <summary>行高亮（勾选了 / 在整定）。</summary>
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _off;

    public bool IsTag => Tone != PidTone.Mute;
    public bool IsBrand => Tone == PidTone.Brand;
    public bool IsWarn => Tone == PidTone.Warn;
    public bool IsRun => Tone == PidTone.Run;

    partial void OnCheckedChanged(bool value) { if (!Syncing) _check(this); }
    partial void OnPChanged(string value) { if (!Syncing) _commit(this); }
    partial void OnIChanged(string value) { if (!Syncing) _commit(this); }
    partial void OnDChanged(string value) { if (!Syncing) _commit(this); }
}

/// <summary>「整定通道」里的一个小块（样稿 .pidform .chip：24 高圆角 12，8 px 识别色圆点 + RV 号）。</summary>
public sealed record PidChipVm(string Rv, IBrush Color);

/// <summary>振荡步骤（样稿 .pidstep）：完成 = 品牌蓝实心；当前 = 品牌蓝圈 + 光晕、600 字；未到 = 灰圈。</summary>
public sealed record PidStepVm(string Text, bool Done, bool Cur)
{
    public bool Todo => !Done && !Cur;
}

/// <summary>
/// 整定结果的一行（样稿 .pidres .pr）：识别色竖条 · RV5 · P <s>15.1</s> → <b>14.0</b> · I … s · D … s · 右边「已存入中温档」（绿）；
/// 没存入新 PID 时一句琥珀说明（<see cref="Fail"/>）。<see cref="Text"/> = 整句（PidTexts.Diff，测试 / 无障碍用）。
/// </summary>
public sealed record PidResultVm(int N, string Rv, IBrush Color, IReadOnlyList<PidDiffPart> Parts, string Saved, string? Fail, string Text)
{
    public bool Ok => Fail == null;
    public bool Failed => Fail != null;
}

/// <summary>整定曲线的一路：距开始的分钟、PV（°C）、加热输出 %（NaN = 没有）；识别色。</summary>
public sealed record PidChartLine(int N, Color Color, double[] M, double[] T, double[] O);

/// <summary>
/// 整定监视的两张图（温度 PV + 整定温度虚线；加热输出 %）的输入：整定温度、横轴总长（min）、「现在」（min；结束了为 null，不画竖线）、各路。
/// </summary>
public sealed record PidChartData(double Sp, double Total, double? Now, IReadOnlyList<PidChartLine> Lines);
