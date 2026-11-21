using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>预运行检查的一项：通过 / 提醒 / 不通过。</summary>
public enum CheckKind { Ok, Warn, No }

/// <summary>预运行检查的一行（样稿 .ckr：图标 · 项目 · 说明）。</summary>
public sealed partial class CheckRowVm : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarn), nameof(IsNo), nameof(Icon))] private CheckKind _kind;
    [ObservableProperty] private string _detail = "";
    public bool IsOk => Kind == CheckKind.Ok;
    public bool IsWarn => Kind == CheckKind.Warn;
    public bool IsNo => Kind == CheckKind.No;
    public string Icon => Kind switch { CheckKind.Ok => "ok-f", CheckKind.Warn => "warn-f", _ => "no-f" };
}

/// <summary>
/// 预运行检查（融合版 F6，样稿 pgCheckDlg / mxCheckDlg）：两种模式共用一个对话框（名字「pgcheck」），内容按模式。
/// <list type="bullet">
/// <item>本机检查：急停、控制模式、气路占用、搅拌、报警通道、程序 / 模板、目标 / 参加通道、溶液体积、（矩阵）每路参数、升压排队、会话。</item>
/// <item>程序步骤 v0.8 的下发前检查（STEPS-SPEC §4，<see cref="ProgramChecks"/>）：搅拌转速（同一批的几路必须一样，写明哪几路；和别的在跑的程序不同时提示）、
///   吸气计量（反应前没有充压）、升温后压力（先充压后升温：提示估算，超上限拒绝）、结束时压力（没有放空）—— 没问题的不列。</item>
/// <item>真机另取 GET /preflight（Client 的 PreflightAsync，HMI 的硬件自检：温控、压力 / 超压监视、阀门、搅拌器）—— 取到之前写「正在读取」；
///   演示数据按模块通讯写在线。HMI 的「运行通道」「程序通道」「控制模式（手动阀）」是给触屏「全部运行」用的，这里不用。</item>
/// </list>
/// 底栏：关闭 · 下发并运行（有不通过的项时禁用并写原因）。数据变了就地重算（每秒）。
/// </summary>
public sealed partial class PreflightDialogVm : DialogVm
{
    private readonly ProgramPageVm _page;
    private readonly bool _mx;
    private PreflightDto? _hmi;
    private bool _hmiLoading, _hmiFailed;

    public PreflightDialogVm(ProgramPageVm page, DeviceVm d, bool matrix)
    {
        _page = page;
        _mx = matrix;
        K = d.K;
        Name = "pgcheck";
        Title = matrix ? "预运行检查 · 条件矩阵" : "预运行检查";
        Sub = null;
        Icon = null;
        Width = 518;
        Body = new Views.Pages.Programs.PreflightView { DataContext = this };
        var close = new FootButton("关闭", new RelayCommand(() => page.Shell.Dialogs.Close(this)));
        GoButton = new FootButton("下发并运行", new AsyncRelayCommand(GoAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions), FootKind.Primary, "play");
        Footer = new FootBar(close, GoButton);
        if (d.Source is LiveDeviceSource { Connection: { } c } && d.Source.Conn.Online) _ = FetchAsync(c);
        Refresh();
    }

    public int K { get; }
    public FootButton GoButton { get; }
    public ObservableCollection<CheckRowVm> Rows { get; } = new();
    [ObservableProperty] private string _summary = "";

    private async Task FetchAsync(ParallelReactor.Client.IReactorConnection c)
    {
        _hmiLoading = true;
        try { _hmi = await c.Rest.PreflightAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception) { _hmiFailed = true; }
        finally { _hmiLoading = false; }
        Refresh();
    }

    public override void Refresh()
    {
        var d = _page.Shell.Fleet.Device(K);
        if (d is null) { _page.Shell.Dialogs.Close(this); return; }
        _page.Refresh(_page.Shell.Fleet);
        var rows = _mx ? MatrixRows(d) : OneRows(d);
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(new CheckRowVm());
            Rows[i].Title = rows[i].T;
            Rows[i].Kind = rows[i].K;
            Rows[i].Detail = rows[i].D;
        }
        bool bad = rows.Any(r => r.K == CheckKind.No);
        Summary = bad ? "有未通过的项，处理后再下发" : "全部通过 · 可以下发";
        GoButton.Reason = bad ? "有未通过的项，处理后再下发" : _mx ? _page.Mx.RunReason : _page.One.RunReason;
    }

    private async Task GoAsync()
    {
        _page.Shell.Dialogs.Close(this);
        if (_mx) await _page.Mx.SendAsync().ConfigureAwait(true);
        else await _page.One.SendAsync().ConfigureAwait(true);
    }

    // ———————————————————— 检查项 ————————————————————

    private (string T, CheckKind K, string D) Estop(DeviceVm d) => d.Source.Estop.Active ? ("急停", CheckKind.No, "急停中 · 需在触屏复位") : ("急停", CheckKind.Ok, "未触发");

    private static (string T, CheckKind K, string D) Lease(DeviceVm d, bool mx)
    {
        bool can = d.Actions.GuardText(GuardKind.Control).Length == 0;
        return ("控制模式", can ? CheckKind.Ok : CheckKind.No, can ? "远程控制中" : mx ? "本地只读 · 先申请控制权" : "本地只读");
    }

    /// <summary>HMI 的一项硬件自检（真机）；演示 / 取不到时按本机的数据写。</summary>
    private (string T, CheckKind K, string D) Hw(DeviceVm d, string title, Func<(CheckKind, string)> local)
    {
        if (d.Source is LiveDeviceSource)
        {
            if (_hmi?.Checks.FirstOrDefault(c => c.Title == title) is { } c)
                return (title, c.Kind switch { "ok" => CheckKind.Ok, "warn" => CheckKind.Warn, _ => CheckKind.No }, c.Detail);
            if (_hmiLoading) return (title, CheckKind.Warn, "正在读取设备的自检…");
            if (_hmiFailed) return (title, CheckKind.Warn, "读不到设备的自检（GET /preflight）");
        }
        var (k, text) = local();
        return (title, k, text);
    }

    private (string, CheckKind, string) Temp(DeviceVm d)
    {
        int n = d.Reactors.Count(r => r.Installed), on = d.Reactors.Count(r => r.Installed && r.Own != StateKey.Idle);
        return Hw(d, "温控", () => (CheckKind.Ok, on + "/" + n + " 在线"));
    }

    private (string, CheckKind, string) Valves(DeviceVm d) => Hw(d, "阀门", () => (CheckKind.Ok, "IO16R 在线 · 阀态已回读"));

    private (string, CheckKind, string) Pressure(DeviceVm d) => Hw(d, "压力 / 超压监视", () =>
        (CheckKind.Ok, d.Source.Plant.OverPressureMPa is double p ? "上限 " + PressureDisplay.FixedU(p, 2) + " · 监视中" : "监视中"));

    private static (string, CheckKind, string) Gas(DeviceVm d)
    {
        var holder = d.Source.Plant.GasHolder;
        var q = d.Source.Plant.GasQueue ?? Array.Empty<int>();
        var busy = (holder is int h ? new[] { h } : Array.Empty<int>()).Concat(q).Distinct().ToArray();
        return busy.Length > 0 ? ("气路", CheckKind.Warn, MatrixPlan.ChList(busy) + " 程序占用 · 升压步骤会排队") : ("气路", CheckKind.Ok, "空闲");
    }

    private static (string, CheckKind, string) Alarms(DeviceVm d)
    {
        var al = d.Reactors.Where(r => r.Installed && r.Own == StateKey.Alarm).Select(r => r.N).ToArray();
        return al.Length > 0 ? ("报警通道", CheckKind.Warn, MatrixPlan.ChList(al) + " 报警中 · 已排除在目标外") : ("报警通道", CheckKind.Ok, "无");
    }

    private List<(string T, CheckKind K, string D)> OneRows(DeviceVm d)
    {
        var o = _page.One;
        var p = _page.Sel;
        var schema = _page.SchemaOf(d);
        var iss = p is null ? Array.Empty<ProgramIssue>() : ProgramIssues.Of(p.Steps, schema, MatrixPlan.ModelLabelOf(p.Model));
        var sel = o.SelectedChannels;
        string vb = MatrixPlan.VolBad(o.Vol, ProgramPageVm.VolMaxOf(d));
        var stir = d.Source.Stir;
        var rows = new List<(string T, CheckKind K, string D)>
        {
            Estop(d),
            Lease(d, false),
            Temp(d),
            Valves(d),
            Gas(d),
            Hw(d, "搅拌器", () => (CheckKind.Ok, (stir is { On: true } s ? "运行中 " + JsNumber.ToString(s.Rpm) + " rpm" : "待机")
                + (p != null && ProgramChecks.FirstRpm(p.Steps) is double pr ? " · 程序设为 " + JsNumber.ToString(pr) + " rpm（整台）" : " · 程序不改转速"))),
            Pressure(d),
            Alarms(d),
            p is null ? ("程序", CheckKind.No, "没有选程序")
                : p.Model != d.Model.Key ? ("程序", CheckKind.No, "适用 " + MatrixPlan.ModelLabelOf(p.Model) + "，" + d.Alias + " 是 " + d.Model.Label)
                : iss.Count > 0 ? ("程序", CheckKind.No, iss.Count + " 处待改") : ("程序", CheckKind.Ok, "「" + p.Name + "」 " + p.Steps.Count + " 步 · 校验通过"),
            sel.Count > 0 ? ("目标通道", CheckKind.Ok, MatrixPlan.ChList(sel) + " 空闲") : ("目标通道", CheckKind.No, "未选择"),
            vb.Length > 0 ? ("溶液体积", CheckKind.No, vb) : ("溶液体积", CheckKind.Ok, "所选各路 " + JsNumber.ToFixed(o.Vol, 1) + " mL · 吸气量由设备按压降计算"),
        };
        if (p != null) rows.AddRange(StepRows(d, schema, sel.Select(n => (n, (IReadOnlyList<PcStep>)p.Steps)).ToList(), sel));
        return rows;
    }

    /// <summary>
    /// 程序步骤 v0.8 的下发前检查（STEPS-SPEC §4）：同一批的搅拌转速、与在跑的程序的搅拌转速、反应前没有充压、先充压后升温、没有放空。按项合并各路（同一条写哪几路），没问题的不列。
    /// </summary>
    private static List<(string T, CheckKind K, string D)> StepRows(DeviceVm d, ProgramSchema schema, IReadOnlyList<(int N, IReadOnlyList<PcStep> Steps)> batch, IReadOnlyCollection<int> sel)
    {
        var rows = new List<(string, CheckKind, string)>();
        if (batch.Count == 0) return rows;
        // 搅拌：一台电机带全部釜
        if (ProgramChecks.RpmMismatch(batch) is { } mis) rows.Add(("搅拌转速", CheckKind.No, mis));
        else if (batch.Select(b => ProgramChecks.FirstRpm(b.Steps)).FirstOrDefault(r => r != null) is double rpm)
        {
            var others = d.Reactors.Where(r => r.Installed && r.IsRunning && !sel.Contains(r.N)).Select(r => r.N).ToArray();
            if (others.Length > 0 && d.Source.Stir is { On: true } st && Math.Abs(st.Rpm - rpm) > 0.5)
                rows.Add(("搅拌转速", CheckKind.Warn, MatrixPlan.ChList(others) + " 的程序也在用搅拌（" + JsNumber.ToString(st.Rpm) + " rpm）· 下发后整台改为 " + JsNumber.ToString(rpm) + " rpm"));
        }
        // 结构检查（各路合并）
        var by = new List<(string Code, CheckLevel Level, string Msg, List<int> Ns)>();
        foreach (var (n, steps) in batch)
            foreach (var c in ProgramChecks.Of(steps, schema).Where(c => c.Code is "nogas" or "heatafter" or "novent"))
            {
                int at = by.FindIndex(x => x.Msg == c.Msg);
                if (at < 0) by.Add((c.Code, c.Level, c.Msg, new List<int> { n }));
                else by[at].Ns.Add(n);
            }
        foreach (var (code, level, msg, ns) in by)
        {
            string title = code switch { "nogas" => "吸气计量", "heatafter" => "升温后压力", _ => "结束时压力" };
            string who = batch.Count > 1 && ns.Count < batch.Count ? "（" + MatrixPlan.ChList(ns) + "）" : "";
            rows.Add((title, level == CheckLevel.Reject ? CheckKind.No : CheckKind.Warn, msg + who));
        }
        return rows;
    }

    private List<(string T, CheckKind K, string D)> MatrixRows(DeviceVm d)
    {
        var mx = _page.Mx;
        var m = mx.Plan;
        var rows = new List<(string, CheckKind, string)> { Estop(d), Lease(d, true) };
        if (m is null) { rows.Add(("模板", CheckKind.No, "没有选模板")); return rows; }
        var schema = _page.SchemaOf(d);
        var ctx = mx.Context(d, schema);
        var chs = ctx.Channels;
        var inc = chs.Where(c => m.Include.Contains(c.N)).ToList();
        var busy = inc.Where(c => c.Busy).Select(c => c.N).ToArray();
        var no = m.Issues(ctx).Where(x => x.Level == MxLevel.No).ToList();
        int param = no.Count(x => !x.Msg.StartsWith("溶液体积", StringComparison.Ordinal) && !x.Msg.StartsWith("模板", StringComparison.Ordinal) && !x.Msg.StartsWith("没有参加", StringComparison.Ordinal));
        var volBad = inc.Where(c => MatrixPlan.VolBad(m.Vol.TryGetValue(c.N, out var v) ? v : double.NaN, ctx.VolMax).Length > 0).Select(c => c.N).ToArray();
        var press = m.Steps.FirstOrDefault(s => s.Type == StepType.Press);
        var stirStep = m.Steps.FirstOrDefault(s => s.Type == StepType.Stir);
        string gasName = press is null ? "" : schema.Field(StepType.Press, "gas")?.LabelOf(press.S("gas")) ?? press.S("gas");
        double q = inc.Count > 1 && press != null ? (inc.Count - 1) * MatrixPlan.QueueMin : 0;
        rows.Add(m.Template.Model == d.Model.Key ? ("模板", CheckKind.Ok, "「" + m.Template.Name + "」 · " + m.Steps.Count + " 步") : ("模板", CheckKind.No, "适用 " + MatrixPlan.ModelLabelOf(m.Template.Model)));
        rows.Add(inc.Count == 0 ? ("参加通道", CheckKind.No, "没有勾选")
            : busy.Length > 0 ? ("参加通道", CheckKind.No, string.Join("、", busy.Select(n => "RV" + n)) + " 正在运行")
            : ("参加通道", CheckKind.Ok, string.Join("、", inc.Select(c => "RV" + c.N)) + " 空闲"));
        rows.Add(param > 0 ? ("每路参数", CheckKind.No, param + " 处超出范围") : ("每路参数", CheckKind.Ok, "全部在范围内"));
        rows.Add(volBad.Length > 0 ? ("溶液体积", CheckKind.No, MatrixPlan.ChList(volBad) + " 没填或超范围 · 算不出吸气量") : ("溶液体积", CheckKind.Ok, "每路已填 · 吸气量由设备按压降计算"));
        rows.Add(("气路", CheckKind.Ok, press is null ? "本程序不进气" : inc.Count > 1 ? gasName + " · " + inc.Count + " 路依次升压，最后一路晚约 " + JsNumber.ToString(q) + " min" : gasName + " · 只有 1 路，无需排队"));
        rows.Add(("搅拌", CheckKind.Ok, stirStep != null ? JsNumber.ToString(stirStep.N("rpm")) + " rpm · 一台电机带全部釜，各路相同" : "保持 " + (d.Source.Stir is { } st ? JsNumber.ToString(st.Rpm) : Fmt.Dash) + " rpm"));
        rows.Add(("会话", CheckKind.Ok, m.Record ? "下发后开始「" + m.Name + "」" : "不记录"));
        rows.AddRange(StepRows(d, schema, inc.Select(c => (c.N, m.StepsFor(c.N))).ToList(), inc.Select(c => c.N).ToArray()));
        // 真机：HMI 的硬件自检另列在后面（演示没有，同样稿）
        if (d.Source is LiveDeviceSource)
        {
            rows.Add(Temp(d));
            rows.Add(Pressure(d));
            rows.Add(Valves(d));
        }
        return rows;
    }
}
