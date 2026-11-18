using System;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>溶液体积 / 气相体积的说明（融合版 F3，检查器「反应配置」）：纯函数，单元测试直接调。</summary>
public static class VolumeText
{
    /// <summary>
    /// 「气相 5.7 mL = 釜 10 − 溶液 + 管路 0.7」（未标定）/「气相 5.3 mL = 有效容积 10.3 − 溶液」（标定过）/「用于计算吸气量」（不知道溶液体积或容积）。
    /// </summary>
    public static string GasLine(VolumeInfo? v, int n, double? solutionMl)
    {
        if (v == null || solutionMl is null) return "用于计算吸气量";
        var g = v.GasVolume(n, solutionMl);
        if (g is not double gv) return "用于计算吸气量";
        return v.Calibrated(n)
            ? "气相 " + F(gv) + " mL = 有效容积 " + F(v.EffectiveMl[n - 1]!.Value) + " − 溶液"
            : "气相 " + F(gv) + " mL = 釜 " + F(v.VesselMl) + " − 溶液 + 管路 " + F(v.LineMl);
    }

    /// <summary>按 0.1 mL 步进（夹在 0 … 釜容积之间；不知道釜容积时只夹下限）。</summary>
    public static double Step(double? cur, int dir, double? vesselMl)
    {
        double v = Math.Round((cur ?? 0) + dir * 0.1, 1);
        if (v < 0) v = 0;
        if (vesselMl is double max && v > max) v = max;
        return v;
    }

    private static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// 主页检查器（融合版 F3，样稿 ctlHTML / #ctlPop.insp）：选中的一路 ——
/// 头（「RV1」24 细体 + 状态小块 + 上一路 / 下一路）· 说明（设备 · 型号 · 程序「…」/ 第 3/4 步 恒温反应 · 剩 27 min）· 只读 / 急停条 ·
/// 测量值（温度 / 压力 / 气体消耗）· 温度控制（升温方式、温度 SP 步进 ±1 ℃、启动 / 停止加热）· 反应配置（溶液体积 ±0.1 mL，写 PUT /channels/{id}/volume；
/// 气相体积说明；搅拌 = 全部釜共用）· 阀门（进气阀 SVn：只读，维护模式在触屏）· 操作（停止该通道 → 停止通道对话框 / 启动该通道；停用 / 启用通道）。
/// 每次刷新就地更新（同一个实例，换通道只换数据）。
/// </summary>
public sealed partial class ChannelCtlVm : ObservableObject
{
    private readonly ShellVm _shell;
    private int _k, _n;

    public ChannelCtlVm(ShellVm shell)
    {
        _shell = shell;
        PrevCommand = new RelayCommand(() => Step(-1));
        NextCommand = new RelayCommand(() => Step(+1));
        SpDownCommand = new RelayCommand(() => SetSp(-1));
        SpUpCommand = new RelayCommand(() => SetSp(+1));
        HeatStartCommand = new RelayCommand(() => Act(DeviceAction.HeatStart, new ActionPayload(N: _n, Sp: R?.SpC)));
        HeatStopCommand = new RelayCommand(() => Act(DeviceAction.HeatStop, ActionPayload.Ch(_n)));
        VolDownCommand = new RelayCommand(() => SetVol(-1));
        VolUpCommand = new RelayCommand(() => SetVol(+1));
        StopChCommand = new RelayCommand(() => shell.OpenDialog("stopch", _k.ToString(CultureInfo.InvariantCulture) + ":" + _n.ToString(CultureInfo.InvariantCulture)));
        StartChCommand = new RelayCommand(() => Act(DeviceAction.Start, new ActionPayload(N: _n, Sp: R?.SpC)));
        EnableCommand = new RelayCommand(() => Act(Disabled ? DeviceAction.Enable : DeviceAction.Disable, ActionPayload.Ch(_n)));
        CurveCommand = new RelayCommand(() => shell.Toast("曲线升温（最多 16 段）在程序页编辑（F6）", ToastKind.Info));
    }

    public ICommand PrevCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand SpDownCommand { get; }
    public ICommand SpUpCommand { get; }
    public ICommand HeatStartCommand { get; }
    public ICommand HeatStopCommand { get; }
    public ICommand VolDownCommand { get; }
    public ICommand VolUpCommand { get; }
    public ICommand StopChCommand { get; }
    public ICommand StartChCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand CurveCommand { get; }

    private DeviceVm? D => _k > 0 ? _shell.Fleet.Device(_k) : null;
    private ReactorVm? R => D?.Reactor(_n);

    [ObservableProperty] private bool _hasChannel;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private RunDot _dot;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private string _sub1 = "";
    [ObservableProperty] private string _sub2 = "";
    [ObservableProperty] private bool _hasSub2;
    /// <summary>只读 / 急停条（样稿 .cro）；null = 不显示。</summary>
    [ObservableProperty] private string? _bar;
    [ObservableProperty] private bool _barErr;

    [ObservableProperty] private string _tempText = Fmt.Dash;
    [ObservableProperty] private string _pressText = Fmt.Dash;
    [ObservableProperty] private bool _pressHot;
    [ObservableProperty] private string _gasText = Fmt.Dash;

    [ObservableProperty] private bool _progNote;
    [ObservableProperty] private string _spText = Fmt.Dash;
    [ObservableProperty] private string _spHint = "";
    [ObservableProperty] private string? _spReason;
    [ObservableProperty] private string? _heatStartReason;
    [ObservableProperty] private string? _heatStopReason;

    [ObservableProperty] private string _volText = Fmt.Dash;
    [ObservableProperty] private string _volHint = "";
    [ObservableProperty] private string? _volReason;
    [ObservableProperty] private string _stirText = "";

    [ObservableProperty] private string _svTitle = "";
    [ObservableProperty] private string _svHint = "";
    [ObservableProperty] private bool _svOpen;

    [ObservableProperty] private bool _running;
    [ObservableProperty] private string? _stopReason;
    [ObservableProperty] private string? _startReason;
    [ObservableProperty] private bool _disabled;
    [ObservableProperty] private string _enableLabel = "停用通道";
    [ObservableProperty] private string? _enableReason;

    /// <summary>按路由刷新（主页选中的设备 + 通道）。</summary>
    public void Refresh()
    {
        int k = _shell.Nav.DeviceK;
        var d = k > 0 ? _shell.Fleet.Device(k) : null;
        var r = d?.Reactor(_shell.Nav.ChannelOf(k));
        HasChannel = r is { Installed: true };
        if (d == null || r == null || !r.Installed) return;
        _k = d.K;
        _n = r.N;
        var own = r.Own;
        Title = r.Rv;
        Word = RunRowText.Word(own, r.Cause, r.OwnWord);
        IsErr = own == StateKey.Alarm;
        Dot = IsErr ? RunDot.Err : StateKeys.IsRunning(own) ? RunDot.Run : RunDot.Idle;

        var prog = r.Program;
        var pr = prog?.Progress;
        bool active = prog?.Record is { IsActive: true } && StateKeys.IsRunning(own);
        Sub1 = d.Alias + " · " + d.Model.Label.Split(' ')[0] + " · " + (prog?.Record is { } rec && (active || rec.Status == ProgramStatus.Done)
            ? "程序「" + rec.Name + "」" + (rec.Status == ProgramStatus.Done && !active ? "已结束" : "")
            : "未运行程序");
        var left = RunRowText.Left(own, pr);
        Sub2 = active && pr != null ? "第 " + pr.K + "/" + pr.N + " 步 " + pr.StepName + " · " + (left.Kind == LeftKind.Value ? "剩 " + left.Value + " min" : left.Value) : "";
        HasSub2 = Sub2.Length > 0;

        var why = d.Actions.Why(DeviceAction.SetSp, new ActionPayload(N: r.N, Sp: r.SpC ?? 25));
        Bar = d.EstopActive ? "急停中 · 复位只能在" + d.Alias + "触屏上进行"
            : d.IsOffline ? d.ConnText + (d.DataText.Length > 0 ? " · " + d.DataText : "")
            : d.Lease != Controls.Pr.LeaseState.Remote ? (d.Lease == Controls.Pr.LeaseState.Requested ? d.LeaseText : "本地只读 · 在工具条「申请控制」后才能操作")
            : null;
        BarErr = d.EstopActive;

        TempText = r.TempText;
        PressText = r.PressureMPa is double p ? p.ToString("0.00", CultureInfo.InvariantCulture) : Fmt.Dash;
        PressHot = IsErr;
        GasText = r.GasMmol is double g ? g.ToString("0.0", CultureInfo.InvariantCulture) : Fmt.Dash;

        ProgNote = active;
        SpText = r.SpC is double sp ? sp.ToString("0", CultureInfo.InvariantCulture) : Fmt.Dash;
        SpHint = "0 – " + d.Model.Limits.TempMaxC.ToString("0", CultureInfo.InvariantCulture) + " °C · 改动立即写入温控表";
        SpReason = Nz(why);
        HeatStartReason = Nz(d.Actions.Why(DeviceAction.HeatStart, new ActionPayload(N: r.N, Sp: r.SpC ?? 25)));
        HeatStopReason = Nz(d.Actions.Why(DeviceAction.HeatStop, ActionPayload.Ch(r.N)));

        var vol = d.Source.Plant.Volume;
        VolText = r.SolutionMl is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : Fmt.Dash;
        VolHint = VolumeText.GasLine(vol, r.N, r.SolutionMl);
        VolReason = Nz(d.Actions.Why(DeviceAction.SetVolume, new ActionPayload(N: r.N, Ml: r.SolutionMl ?? 0)));
        StirText = d.Stir is { } s ? s.On ? Math.Round(s.Rpm).ToString(CultureInfo.InvariantCulture) + " rpm" : "已停" : r.Rpm is double rpm ? Math.Round(rpm).ToString(CultureInfo.InvariantCulture) + " rpm" : Fmt.Dash;

        SvTitle = "进气阀 SV" + r.N.ToString(CultureInfo.InvariantCulture);
        SvOpen = r.Valve;
        SvHint = d.EstopActive ? "急停中 · 阀门已全关" : "只读 · 手动开关阀门要在设备触屏上进入「远程维护模式」";

        Running = StateKeys.IsRunning(own);
        StopReason = Nz(d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(r.N)));
        StartReason = Nz(d.Actions.Why(DeviceAction.Start, new ActionPayload(N: r.N, Sp: r.SpC ?? 25)));
        Disabled = own == StateKey.Idle;
        EnableLabel = Disabled ? "启用通道" : "停用通道";
        EnableReason = Running ? "先停止该通道" : Nz(d.Actions.Why(Disabled ? DeviceAction.Enable : DeviceAction.Disable, ActionPayload.Ch(r.N)));
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;

    private void Step(int dir)
    {
        if (D is not { } d) return;
        var ns = d.Reactors.Where(x => x.Installed).Select(x => x.N).ToList();
        int i = ns.IndexOf(_n);
        if (i < 0 || ns.Count == 0) return;
        _shell.SelectChannel(d.K, ns[(i + dir + ns.Count) % ns.Count]);
    }

    private void SetSp(int dir)
    {
        if (D is not { } d || R is not { } r) return;
        double sp = Math.Clamp(Math.Round((r.SpC ?? 25) + dir), 0, d.Model.Limits.TempMaxC);
        Act(DeviceAction.SetSp, new ActionPayload(N: _n, Sp: sp));
    }

    private void SetVol(int dir)
    {
        if (D is not { } d || R is not { } r) return;
        var ml = VolumeText.Step(r.SolutionMl, dir, d.Source.Plant.Volume?.VesselMl);
        Act(DeviceAction.SetVolume, new ActionPayload(N: _n, Ml: ml));
    }

    private void Act(DeviceAction a, ActionPayload p)
    {
        if (D is { } d) _ = d.Actions.RunAsync(a, p);
    }
}
