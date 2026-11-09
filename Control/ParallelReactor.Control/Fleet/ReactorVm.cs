using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Fleet;

/// <summary>
/// 一个反应釜（PORT-PLAN §5 M2「ReactorVm：吸收 ChannelVm.Apply」）：身份 = (设备, RV 号)，生命周期内不变；每次刷新由 <see cref="Apply"/> 就地更新，
/// 只在值真的变了时发通知（R10：不重建集合、不每帧重设 ItemsSource）。
/// <para>数据来自 <see cref="ChannelReading"/>（实时 = ChannelSnap 经 StateMap / Units；演示 = DemoDevice），文字一律在这里格式化：
/// 温度 1 位小数、压力按型号小数位与<b>唯一的</b>压力单位设置（Core.Format.PressureDisplay，D8）、空值「—」。</para>
/// 资源管理器 / 时间轴行要的东西（读数格的取舍、悬停提示）也在这里，与样稿 shell.js keyReading / rvTip 一致。
/// </summary>
public sealed partial class ReactorVm : ObservableObject, IFleetChannel
{
    /// <param name="device">所属设备（单独投影一个反应釜时可以为 null，例如测试）。</param>
    public ReactorVm(DeviceVm? device, int n)
    {
        Device = device;
        N = n;
        Rv = "RV" + n;
    }

    public DeviceVm? Device { get; }
    public int N { get; }
    /// <summary>'RV1'。</summary>
    public string Rv { get; }
    /// <summary>压力单位（PressureDisplay.Unit：'MPa'，D8 的唯一设置）。</summary>
    public string PressUnit => PressureDisplay.Unit;

    [ObservableProperty] private bool _installed = true;
    /// <summary>界面上的状态（未安装 / 离线 / 自己的状态）。</summary>
    [ObservableProperty] private StateKey _key = StateKey.Offline;
    /// <summary>界面上的状态字（「离线」「未连接」「超压报警」…；绝不省略）。</summary>
    [ObservableProperty] private string _word = StateKeys.NeverConnectedWord;
    /// <summary>反应釜自己的状态（断线时是断线前的）。</summary>
    [ObservableProperty] private StateKey _own = StateKey.Idle;
    [ObservableProperty] private string _ownWord = "";
    [ObservableProperty] private AlarmCause _cause;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isAlarm;

    [ObservableProperty] private double? _tempC;
    [ObservableProperty] private double? _spC;
    [ObservableProperty] private double? _pressureMPa;
    [ObservableProperty] private double? _pTargetMPa;
    [ObservableProperty] private double? _heaterPct;
    [ObservableProperty] private double? _rpm;
    [ObservableProperty] private bool _heating;
    [ObservableProperty] private bool _valve;

    /// <summary>温度数值文字（'119.8' / '—'）。</summary>
    [ObservableProperty] private string _tempText = Fmt.Dash;
    /// <summary>温度没有读数（画 t-dis 的「—」、不带单位）。</summary>
    [ObservableProperty] private bool _tempNa = true;
    /// <summary>压力数值文字（按型号小数位：'0.349' / '5.20' / '—'）。</summary>
    [ObservableProperty] private string _pressText = Fmt.Dash;
    [ObservableProperty] private bool _pressNa = true;

    /// <summary>行里显示温度格（样稿 keyReading：升压 / 等待只写压力；超压报警温度 ≥ 100 ℃ 放不下时只写压力）。</summary>
    [ObservableProperty] private bool _showTemp;
    /// <summary>超压报警行的窄温度格（is-tn：62 px，可以缩到数字本身）。</summary>
    [ObservableProperty] private bool _narrowTemp;
    /// <summary>行里显示压力格（升压 / 等待 / 反应 / 报警）；不显示时留空格（温度列照样对齐）。</summary>
    [ObservableProperty] private bool _showPress;

    /// <summary>资源管理器的状态字：从未连上的设备写「—」（样稿 rvWord），否则同 <see cref="Word"/>。</summary>
    [ObservableProperty] private string _treeWord = "—";
    /// <summary>悬停提示（样稿 rvTip）：「1号机 · RV1 · 反应中 · 119.8 ℃ · 0.349 MPa · 加氢-标准 第 3/4 步 · 剩 42 min」。</summary>
    [ObservableProperty] private string _tip = "";

    /// <summary>程序（演示：演示程序；实时：M5 的台账，之前为 null）。</summary>
    [ObservableProperty] private ProgramVm? _program;
    /// <summary>HMI 的程序进度（实时；没有程序名和步骤）。</summary>
    [ObservableProperty] private HmiProgram? _hmi;
    /// <summary>演示才有的数据（液位、每釜转速、冷却、mmol）。</summary>
    [ObservableProperty] private ChannelExtras? _extras;

    partial void OnKeyChanged(StateKey value) => IsAlarm = value == StateKey.Alarm;

    /// <summary>
    /// 就地更新。<paramref name="model"/> 决定压力小数位；<paramref name="online"/> / <paramref name="everOnline"/> 是设备的连接；
    /// <paramref name="dataText"/> 断线时写进提示；<paramref name="program"/> / <paramref name="progress"/> 是这一路的程序与进度（可为 null）。
    /// </summary>
    public void Apply(ChannelReading r, ModelProfile model, bool online, bool everOnline, string alias, string dataText,
        ProgramRecord? program = null, ProgressInfo? progress = null, ChannelExtras? extras = null)
    {
        Installed = r.Installed;
        Key = r.Key;
        Word = r.Word;
        Own = r.Own;
        OwnWord = r.OwnWord;
        Cause = r.Cause;
        IsRunning = r.IsRunning;
        TempC = r.TempC;
        SpC = r.SpC;
        PressureMPa = r.PressureMPa;
        PTargetMPa = r.PTargetMPa;
        HeaterPct = r.HeaterPct;
        Rpm = r.Rpm;
        Heating = r.Heating;
        Valve = r.Valve;
        Hmi = r.Hmi;
        Extras = extras;

        var t = Fmt.Temp(r.TempC);
        var p = PressureDisplay.Value(model, r.PressureMPa);
        TempText = t;
        TempNa = t == Fmt.Dash;
        PressText = p;
        PressNa = p == Fmt.Dash;
        var (showT, narrowT, showP) = r.Installed ? ChannelProjection.KeyColumns(r.Own, t) : (false, false, false);
        ShowTemp = showT;
        NarrowTemp = narrowT;
        ShowPress = showP;
        TreeWord = everOnline ? r.Word : "—";

        if (program is null) Program = null;
        else
        {
            Program ??= new ProgramVm();
            Program.Apply(program, progress);
        }
        Tip = TipFor(r, alias, model, online, everOnline, dataText, progress);
    }

    /// <summary>悬停提示（样稿 shell.js rvTip）：断线时写「离线 · 断线前 反应中 · 数据停在 …」。</summary>
    private string TipFor(ChannelReading r, string alias, ModelProfile model, bool online, bool everOnline, string dataText, ProgressInfo? pr)
    {
        if (!r.Installed) return alias + " · " + Rv + " · " + r.Word;
        bool off = !online && everOnline;
        var rd = r.TempC is null ? "" : " · " + Fmt.Temp(r.TempC) + " ℃ · " + PressureDisplay.WithUnit(model, r.PressureMPa);
        var prog = pr != null ? " · " + pr.Name + " " + Progress.Text(pr)
            : r.Hmi is { Running: true } h ? " · " + Rv + " 程序 · 第 " + (h.StepIndex + 1) + "/" + h.StepCount + " 步" : "";
        return alias + " · " + Rv + " · " + (off ? "离线 · 断线前 " : "") + r.OwnWord + rd + prog + (online ? "" : " · " + dataText);
    }
}
