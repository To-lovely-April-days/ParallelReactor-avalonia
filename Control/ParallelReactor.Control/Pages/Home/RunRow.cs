using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>运行表状态点的样子（样稿 .sts.s-run / s-err / s-idle）。</summary>
public enum RunDot { Idle, Run, Err }

/// <summary>「本步剩余」格的写法（样稿 runTable left）：数值 + min / 已暂停（红 600）/ 已结束（灰）/ —。</summary>
public enum LeftKind { None, Value, Paused, Ended }

/// <summary>步骤分段条的一段（样稿 .pgb i：flex = 步骤分钟数（至少 1.2）；done / cur（按进度填充，报警时红）/ next）。</summary>
public readonly record struct StepSeg(double Weight, SegmentState State, double Fill, bool Err, string Tip);

/// <summary>
/// 运行表的文字规则（样稿 runTable 的 pgCell / left / end / tCell / pCell，融合版 F3）：纯函数，单元测试直接调。
/// </summary>
public static class RunRowText
{
    /// <summary>温度偏差标签的阈值 ℃（样稿 |Δ| &gt; 2，只在反应中 / 报警时出）。</summary>
    public const double DevThreshold = 2;

    /// <summary>
    /// 温度偏差标签（样稿 dvn）：反应中或报警（自己的状态）、有实测和设定、|实测 − 设定| &gt; 2 ℃ 时「+3.1」/「−4.8」（减号是 U+2212）；否则 null。
    /// </summary>
    public static string? DevChip(StateKey own, double? t, double? sp)
    {
        if (own is not (StateKey.React or StateKey.Alarm) || t is not double tv || sp is not double sv) return null;
        double dv = tv - sv;
        if (Math.Abs(dv) <= DevThreshold) return null;
        return (dv > 0 ? "+" : "−") + Math.Abs(dv).ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>偏差标签的提示：「比设定值低 4.8 °C」。</summary>
    public static string DevTip(double t, double sp) =>
        "比设定值" + (t - sp > 0 ? "高 " : "低 ") + Math.Abs(t - sp).ToString("0.0", CultureInfo.InvariantCulture) + " °C";

    /// <summary>运行类（不含报警暂停）：升温、升压、反应、降温、等待、搅拌（样稿 RUN）。</summary>
    public static bool Running(StateKey own) => own is StateKey.Heat or StateKey.Press or StateKey.React or StateKey.Cool or StateKey.Wait or StateKey.Stir;

    /// <summary>「本步剩余」：报警 = 已暂停；已结束 = 已结束；不在运行 = —；运行中 = 本步剩余分钟（没有程序进度时 —）。</summary>
    public static (LeftKind Kind, string Value) Left(StateKey own, ProgressInfo? pr)
    {
        if (own == StateKey.Alarm) return (LeftKind.Paused, "已暂停");
        if (own == StateKey.Done) return (LeftKind.Ended, "已结束");
        if (!Running(own) || pr is null) return (LeftKind.None, Fmt.Dash);
        return (LeftKind.Value, pr.StepRemainMin.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 预计结束（样稿 etaOf：只算运行中的 —— 本步剩余 + 后面各步的估计时长）：现在 + 全程剩余 →「15:19」；不在运行 / 没有程序为 null。
    /// </summary>
    public static string? End(DateTime now, StateKey own, ProgressInfo? pr) =>
        Running(own) && pr is { TotalRemainMin: >= 0 } p ? ProgramPanelText.EtaAt(now, p.TotalRemainMin).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// 步骤分段（样稿 pgCell）：每步一段，宽度 = 计划分钟数（至少 1.2）；已完成 / 当前（按本步进度填充，报警暂停时红）/ 未到；程序已结束全部算完成。
    /// </summary>
    public static IReadOnlyList<StepSeg> Segs(ProgramRecord p, ProgressInfo? pr, bool alarm)
    {
        var list = new List<StepSeg>(p.Steps.Count);
        bool done = p.Status == ProgramStatus.Done;
        for (int i = 0; i < p.Steps.Count; i++)
        {
            var st = p.Steps[i];
            double w = Math.Max(1.2, Progress.StepPlanS(st) / 60);
            var s = done || i < p.StepIndex ? SegmentState.Done : i == p.StepIndex ? SegmentState.Cur : SegmentState.Next;
            double f = s == SegmentState.Cur ? Math.Clamp(pr?.Within ?? 0, 0, 1) : 0;
            list.Add(new StepSeg(w, s, f, s == SegmentState.Cur && alarm,
                "第 " + (i + 1) + " 步 " + StepText.Name(st) + " · 约 " + Math.Round(w).ToString(CultureInfo.InvariantCulture) + " min"));
        }
        return list;
    }

    /// <summary>界面上的状态字（融合版样稿 ST：已就绪、压力超限；其余同设备给的）。</summary>
    public static string Word(StateKey own, AlarmCause cause, string ownWord) => own switch
    {
        StateKey.Ready => "已就绪",
        StateKey.Alarm when cause == AlarmCause.OverPressure => "压力超限",
        _ => ownWord,
    };

    /// <summary>通道识别色（样稿 CH_COL：ch-1…ch-8，16 路的 RV9–16 同色、曲线用虚线）。</summary>
    public static IBrush ChBrush(int n) => ((n - 1) % 8) switch
    {
        0 => Palette.Ch1Brush, 1 => Palette.Ch2Brush, 2 => Palette.Ch3Brush, 3 => Palette.Ch4Brush,
        4 => Palette.Ch5Brush, 5 => Palette.Ch6Brush, 6 => Palette.Ch7Brush, _ => Palette.Ch8Brush,
    };

    public static Color ChColor(int n) => ((n - 1) % 8) switch
    {
        0 => Palette.Ch1Color, 1 => Palette.Ch2Color, 2 => Palette.Ch3Color, 3 => Palette.Ch4Color,
        4 => Palette.Ch5Color, 5 => Palette.Ch6Color, 6 => Palette.Ch7Color, _ => Palette.Ch8Color,
    };
}

/// <summary>
/// 运行表的一行（样稿 runTable 的 tr.rw：一路一行）。身份 = (设备, RV 号)；每次刷新 <see cref="Apply"/> 就地更新。
/// 选中（左侧品牌蓝竖条 + a-tint）、报警（左侧红竖条 + r-bg）、停用（t-dis）、断线（<see cref="Stale"/>：读数变灰，数据停在断线时刻）。
/// </summary>
public sealed partial class RunRowVm : ObservableObject
{
    public RunRowVm(int n)
    {
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        ChBrush = RunRowText.ChBrush(n);
    }

    public int N { get; }
    public string Rv { get; }
    public IBrush ChBrush { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private bool _stale;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private RunDot _dot;
    [ObservableProperty] private string _tip = "";

    // 程序 · 当前步骤
    [ObservableProperty] private bool _hasProg;
    [ObservableProperty] private string _stepName = "";
    [ObservableProperty] private string _stepFrac = "";
    [ObservableProperty] private IReadOnlyList<StepSeg> _segs = Array.Empty<StepSeg>();
    [ObservableProperty] private string _noProg = "";

    // 本步剩余 / 预计结束
    [ObservableProperty] private LeftKind _left;
    [ObservableProperty] private string _leftText = Fmt.Dash;
    [ObservableProperty] private string? _endText;

    // 温度 / 压力
    [ObservableProperty] private string _tempText = Fmt.Dash;
    [ObservableProperty] private bool _tempNa = true;
    [ObservableProperty] private string _spText = Fmt.Dash;
    [ObservableProperty] private string? _devChip;
    [ObservableProperty] private string? _devTip;
    [ObservableProperty] private string _pressText = Fmt.Dash;
    [ObservableProperty] private bool _pressNa = true;
    [ObservableProperty] private string _pSpText = Fmt.Dash;
    [ObservableProperty] private bool _pressHot;

    // 吸气 / 速率
    [ObservableProperty] private string _gasText = Fmt.Dash;
    /// <summary>吸气量格：0 = —，1 = 数值，2 = 「未算」（没填溶液体积）。</summary>
    [ObservableProperty] private int _gasKind;
    [ObservableProperty] private string _rateText = Fmt.Dash;
    [ObservableProperty] private bool _rateNa = true;

    /// <summary>按反应釜视图模型更新。<paramref name="now"/> = 预计结束的「现在」；<paramref name="plimMPa"/> = 压力报警上限（超过时压力红字）。</summary>
    public void Apply(ReactorVm r, DateTime now, double? plimMPa, bool selected, bool stale)
    {
        var own = r.Own;
        IsSelected = selected;
        Stale = stale;
        IsErr = own == StateKey.Alarm;
        IsOff = own is StateKey.Idle or StateKey.Uninstalled;
        Word = RunRowText.Word(own, r.Cause, r.OwnWord);
        Dot = IsErr ? RunDot.Err : StateKeys.IsRunning(own) ? RunDot.Run : RunDot.Idle;
        Tip = Rv + " · 点击在检查器里查看和操作";

        var prog = r.Program;
        var pr = prog?.Progress;
        bool showProg = prog?.Record is { } rec && rec.Steps.Count > 0 && own is not (StateKey.Idle or StateKey.Ready or StateKey.Uninstalled);
        HasProg = showProg;
        if (showProg)
        {
            var rec2 = prog!.Record!;
            bool done = rec2.Status == ProgramStatus.Done;
            int k = Math.Clamp(rec2.StepIndex, 0, rec2.Steps.Count - 1);
            StepName = done ? "全部完成" : StepText.Name(rec2.Steps[k]);
            StepFrac = (done ? rec2.Steps.Count : k + 1).ToString(CultureInfo.InvariantCulture) + "/" + rec2.Steps.Count.ToString(CultureInfo.InvariantCulture);
            Segs = RunRowText.Segs(rec2, pr, IsErr);
        }
        else
        {
            NoProg = own == StateKey.Idle ? "已停用" : r.Hmi is { Running: true } h
                ? "设备程序 · 第 " + (h.StepIndex + 1).ToString(CultureInfo.InvariantCulture) + "/" + h.StepCount.ToString(CultureInfo.InvariantCulture) + " 步"
                : "未运行 · 待启动";
            if (Segs.Count > 0) Segs = Array.Empty<StepSeg>();
        }
        var (lk, lt) = RunRowText.Left(own, showProg ? pr : null);
        Left = lk;
        LeftText = lt;
        EndText = showProg ? RunRowText.End(now, own, pr) : null;

        TempText = r.TempText;
        TempNa = r.TempNa;
        SpText = r.SpC is double sp ? sp.ToString("0.0", CultureInfo.InvariantCulture) : Fmt.Dash;
        DevChip = RunRowText.DevChip(own, r.TempC, r.SpC);
        DevTip = DevChip != null ? RunRowText.DevTip(r.TempC!.Value, r.SpC!.Value) : null;
        // 运行表的压力一律 2 位小数（样稿 fp：toFixed(2)；检查器 / 曲线页仍按型号的小数位）
        PressText = r.PressureMPa is double pv ? pv.ToString("0.00", CultureInfo.InvariantCulture) : r.PressText;
        PressNa = r.PressNa;
        PSpText = r.PTargetMPa is double ps && ps > 0 ? ps.ToString("0.00", CultureInfo.InvariantCulture) : Fmt.Dash;
        PressHot = r.PressureMPa is double p && plimMPa is double lim && p > lim;

        if (r.TempNa) { GasKind = 0; GasText = Fmt.Dash; }
        else if (r.GasMmol is double g) { GasKind = 1; GasText = g.ToString("0.0", CultureInfo.InvariantCulture); }
        else if (r.SolutionMl is null && StateKeys.IsRunning(own)) { GasKind = 2; GasText = "未算"; }
        else { GasKind = 0; GasText = Fmt.Dash; }
        bool rate = r.GasRate is double gr && gr > 0 && RunRowText.Running(own);
        RateNa = !rate;
        RateText = rate ? r.GasRate!.Value.ToString("0.00", CultureInfo.InvariantCulture) : Fmt.Dash;
    }
}
