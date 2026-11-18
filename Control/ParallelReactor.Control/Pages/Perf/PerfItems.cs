using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Pages.Perf;

/// <summary>测试项列表的一组（样稿 .ptg「0 准备」：13/600 t-3）。</summary>
public sealed record PtGroupVm(string Title, IReadOnlyList<PtItemVm> Items);

/// <summary>
/// 测试项的一行（样稿 .pti-r：状态图标 18（完成 = 绿勾、跳过 = 跳过箭头、当前 = 品牌蓝 ▶）· 复选框 · 编号 600 · 名字 t-2 · 分钟 13 t-3；
/// 当前一行 a-tint 底 + 3 px 品牌蓝；二期的灰掉）。勾选直接改性能测试视图模型里的那一项（<see cref="PerfItemVm.IsChecked"/>）。
/// </summary>
public sealed partial class PtItemVm : ObservableObject
{
    private readonly PerfItemVm _src;

    public PtItemVm(PerfItemVm src)
    {
        _src = src;
        src.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerfItemVm.IsChecked)) OnPropertyChanged(nameof(IsChecked)); };
        Minutes = PerfTexts.Hm(src.Item.EstimatedMinutes);
        Tip = src.Tooltip;
    }

    public string Id => _src.Item.Id;
    public string Name => _src.Item.Title;
    public string Minutes { get; }
    public string Tip { get; }
    public bool Enabled => _src.IsEnabled;
    public PerfItemVm Source => _src;

    /// <summary>勾选（二期 / 已完成 / 正在测的不能改，<see cref="CanCheck"/>）。</summary>
    public bool IsChecked
    {
        get => _src.IsChecked;
        set { if (CanCheck && _src.IsChecked != value) _src.IsChecked = value; }
    }

    /// <summary>状态：todo / done / skip / cur / off（二期）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsSkip), nameof(IsCur), nameof(IsOff), nameof(CanCheck))]
    private string _state = "todo";

    public bool IsDone => State == "done";
    public bool IsSkip => State == "skip";
    public bool IsCur => State == "cur";
    public bool IsOff => State == "off" || !Enabled;
    public bool CanCheck => !IsOff && !IsDone && !IsCur && !Locked;

    /// <summary>测试在跑：勾选锁住。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanCheck))] private bool _locked;
}

/// <summary>测试通道的小块（样稿 .ptch .tag.brand：选中的品牌蓝；点一下切换，测试在跑时锁住）。</summary>
public sealed partial class PtChanVm : ObservableObject
{
    public PtChanVm(int n, Action<PtChanVm> toggle)
    {
        N = n;
        Name = "RV" + n.ToString(CultureInfo.InvariantCulture);
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public int N { get; }
    public string Name { get; }
    public ICommand ToggleCommand { get; }
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _tip = "";
}

/// <summary>人工录入的一格（样稿 .pin：「RV1 本机 0.997」14/600 + 30 高的带单位输入框）。文字直接改性能测试视图模型里的录入项。</summary>
public sealed partial class PtEntryVm : ObservableObject
{
    private readonly PerfEntryFieldVm? _src;
    private string _demoText;

    public PtEntryVm(string label, string sub, string unit, PerfEntryFieldVm? src, string demoText = "")
    {
        Label = label;
        Sub = sub;
        Unit = unit;
        _src = src;
        _demoText = demoText;
        if (src != null) src.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerfEntryFieldVm.Text)) OnPropertyChanged(nameof(Text)); if (e.PropertyName == nameof(PerfEntryFieldVm.HasError)) OnPropertyChanged(nameof(HasError)); };
    }

    public string Label { get; }
    public string Sub { get; }
    public string Unit { get; }
    public bool HasSub => Sub.Length > 0;
    public bool IsText => _src?.IsText ?? false;
    public bool HasError => _src?.HasError ?? false;

    public string Text
    {
        get => _src?.Text ?? _demoText;
        set
        {
            if (_src != null) { if (_src.Text != value) _src.Text = value; }
            else if (_demoText != value) { _demoText = value; OnPropertyChanged(); Changed?.Invoke(); }
        }
    }

    /// <summary>演示的录入改了（「确认并继续」要四格都填了）。</summary>
    public event Action? Changed;
}

/// <summary>实时的一块（样稿 .kpi：k 13 t-3、v 22/600 + 单位 13 t-4）。</summary>
public sealed record PtKpiVm(string K, string V, string U);

/// <summary>结果表的一行（样稿 #ptRes：测试项 600 · 通道 · 设定 · 标准 t-3 · 实测 / 误差右对齐 · 判定标签）。</summary>
public sealed record PtResVm(string Item, string Ch, string Setting, string Measured, string Error, string Verdict, BadgeTone Tone, bool IsItem = true);

/// <summary>泄漏测试结果的一行（样稿 #lkRes：通道 · 起始 / 结束 psi · 泄漏率 psi/h（不通过红）· 阈值 · 结论 ✓通过 / ✗不通过 / 未测）。</summary>
public sealed record LeakRowVm(string Rv, string Start, string End, string Rate, string Limit, bool? Ok, string Detail)
{
    public bool IsOk => Ok == true;
    public bool IsBad => Ok == false;
    public bool IsNa => Ok == null;
    public string Verdict => Ok == true ? "通过" : Ok == false ? "不通过" : Detail.Length > 0 ? Detail : "未测";
}

/// <summary>泄漏测试的一个阶段（样稿 .lkph：完成 = 浅绿圆 + 勾；进行中 = 品牌蓝；没到 = 灰）。</summary>
public sealed record LeakPhaseVm(string Text, bool Done, bool Cur)
{
    public bool Todo => !Done && !Cur;
}

/// <summary>泄漏测试的一路结果（HMI 的 leak/&lt;runId&gt;-summary.json 的 channels[]）。</summary>
public sealed record LeakChannel(int Rv, string Status, string Detail, double? StartPsi, double? EndPsi, double? RatePsiH);

/// <summary>一次泄漏测试的汇总（runId、开始时刻（本地）、各路）。</summary>
public sealed record LeakSummary(string RunId, DateTime? Start, IReadOnlyList<LeakChannel> Channels);

/// <summary>性能测试页的文字（纯函数）。</summary>
public static class PerfTexts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>「45 min」/「1 h 30 min」/「2 h」（样稿 hm）。</summary>
    public static string Hm(double min)
    {
        int m = (int)Math.Round(Math.Max(0, min));
        if (m < 60) return m.ToString(Inv) + " min";
        int h = m / 60, r = m % 60;
        return h.ToString(Inv) + " h" + (r > 0 ? " " + r.ToString("00", Inv) + " min" : "");
    }

    /// <summary>「3 h 12 min」（已用 / 预计还要，样稿写法：分钟不补零）。</summary>
    public static string Dur(TimeSpan t)
    {
        int m = (int)Math.Round(Math.Max(0, t.TotalMinutes));
        return m < 60 ? m.ToString(Inv) + " min" : (m / 60).ToString(Inv) + " h " + (m % 60).ToString(Inv) + " min";
    }

    /// <summary>
    /// 读 HMI 的泄漏测试汇总（GET /leak-tests/{runId}：<c>{ runId, start, parameters, channels: [{ Rv, Status: ok|bad|skip|"", Detail, JudgedDropPsiPerH, StartPsig, EndPsig, … }] }</c>；
    /// 字段名不分大小写）。读不了返回 null。
    /// </summary>
    public static LeakSummary? ParseLeak(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string run = Str(root, "runId") ?? "";
        DateTime? start = DateTime.TryParse(Str(root, "start"), Inv, DateTimeStyles.AssumeLocal, out var st) ? st : null;
        var list = new List<LeakChannel>();
        if (Prop(root, "channels") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || Num(c, "rv") is not double rv) continue;
                list.Add(new LeakChannel((int)rv, Str(c, "status") ?? "", Str(c, "detail") ?? "", Num(c, "startPsig"), Num(c, "endPsig"), Num(c, "judgedDropPsiPerH")));
            }
        return new LeakSummary(run, start, list.OrderBy(c => c.Rv).ToList());
    }

    /// <summary>一路的判定：HMI 给了 ok / bad 就按它；没给时按 <paramref name="limit"/>（psi/h，压降 ≤ 阈值通过）；skip / 没数据 = 未测。</summary>
    public static bool? Verdict(LeakChannel c, double limit) =>
        c.Status == "ok" ? true : c.Status == "bad" ? false : c.Status == "skip" ? null : c.RatePsiH is double r ? r <= limit + 1e-9 : null;

    private static JsonElement? Prop(JsonElement o, string name)
    {
        foreach (var p in o.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement o, string name) => Prop(o, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) => Prop(o, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;
}
