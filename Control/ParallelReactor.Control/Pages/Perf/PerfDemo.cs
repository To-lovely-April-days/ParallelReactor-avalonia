using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Pages.Perf;

/// <summary>
/// 性能测试页的演示状态（融合版 F8，样稿 PT_PLAN / PT / PT_RES / LEAK）：演示数据没有连接，引擎跑不起来，页面照样稿摆出「P1 第 4 / 11 点人工录入」的样子；
/// 转速按釜转速改过（上限 450：样稿结果表的 1000 / 1500 rpm 换成 450 rpm 和「上限内不失步」）。泄漏测试的阈值用设备设置的 2 psi/h（样稿写 0.5），
/// 所以 RV3 的泄漏率改成 2.6 psi/h（仍是「不通过」的例子）。
/// </summary>
internal sealed class PerfDemo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>样稿 PT_PLAN 的状态（没列的 = 未开始；二期 = off）。</summary>
    public static readonly IReadOnlyDictionary<string, string> States = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["R0"] = "done", ["F1"] = "done", ["F2"] = "done", ["F3"] = "done", ["F4"] = "done", ["F4H"] = "done", ["F5"] = "done", ["F6"] = "skip",
        ["S1"] = "done", ["S2"] = "done", ["S3"] = "done", ["P1"] = "cur",
    };

    private static readonly string[] Start = { "1.003", "0.998", "", "" };
    private static readonly double[] Local = { 0.997, 1.000, 1.003, 1.012 };
    private List<PtEntryVm>? _entries;

    public bool Running { get; private set; } = true;
    public bool Paused { get; private set; }
    public bool Aborted { get; set; }

    /// <summary>勾上方案里的正式项（快速自检不勾）、选 RV1–RV4（样稿 PT_SEL）。</summary>
    public void Init(PerfViewModel perf)
    {
        foreach (var g in perf.Groups)
            foreach (var i in g.Items)
                i.IsChecked = i.IsEnabled && !i.Item.Id.EndsWith("Q", StringComparison.Ordinal);
        perf.SelectChannels(1, 2, 3, 4);
    }

    public void Toggle()
    {
        if (Aborted) { Aborted = false; Running = true; Paused = false; return; }
        Running = !Running;
        Paused = !Running;
    }

    public void ClearEntries()
    {
        if (_entries == null) return;
        foreach (var e in _entries) e.Text = "";
    }

    public void Reset()
    {
        Running = true;
        Paused = false;
        Aborted = false;
        _entries = null;
    }

    /// <summary>进度、当前一步、实时、说明（样稿 PAGES.test.render 的 #ptRun）。</summary>
    public void Apply(PerfPageVm p, DeviceVm? d)
    {
        var perf = p.Perf;
        var all = perf.AllItems.Where(i => i.IsEnabled).ToList();
        var sel = all.Where(i => i.IsChecked).ToList();
        int done = all.Count(i => States.TryGetValue(i.Item.Id, out var s) && s is "done" or "skip");
        int total = Math.Max(1, sel.Count);
        double remain = sel.Where(i => !States.ContainsKey(i.Item.Id) || States[i.Item.Id] == "cur").Sum(i => i.Item.EstimatedMinutes) - 20;
        p.RunSub = "会话 PT-20261115-1305";
        (p.RunTag, p.RunTone, p.RunDot) = Aborted ? ("已中止", BadgeTone.None, false) : Running ? ("运行中", BadgeTone.Ok, true) : ("已暂停", BadgeTone.Warn, false);
        p.Progress = Math.Clamp(done / (double)total, 0, 1);
        p.DoneText = done.ToString(Inv) + " / " + total.ToString(Inv) + " 项";
        p.UsedText = "已用 3 h 12 min";
        p.RemainText = "预计还要 " + PerfTexts.Dur(TimeSpan.FromMinutes(Math.Max(0, remain)));
        p.IsIdle = false;
        p.IsFinished = false;
        p.HasCur = true;
        p.CurId = "P1";
        p.CurTitle = perf.Plan.FindItem("P1")?.Title ?? "压力示值误差";
        p.CurHint = "第 4 / 11 点 · 设定 1.00 MPa";
        p.CurKind = "人工录入";
        p.CurPrompt = "压力稳定后，把外部标准压力表的读数填到下面（单位 MPa）。外部表计：数字压力表 ±0.05 %FS。";
        p.IsEntry = true;
        p.IsConfirm = false;
        p.IsSafety = false;
        p.TwoSigners = false;
        p.CanSkipPrompt = true;
        p.ConfirmLabel = "确认并继续";
        p.Countdown = "已稳定 02:41 · 下一点 1.25 MPa";
        if (_entries == null)
        {
            _entries = Enumerable.Range(0, 4).Select(i => new PtEntryVm("RV" + (i + 1).ToString(Inv), "本机 " + Local[i].ToString("0.000", Inv), "MPa", null, Start[i])).ToList();
            foreach (var e in _entries) e.Changed += () => Apply(p, p.Dev);
        }
        if (!p.Entries.SequenceEqual(_entries)) { p.Entries.Clear(); foreach (var e in _entries) p.Entries.Add(e); }
        bool ctl = d != null && !d.IsOffline && d.Lease == LeaseState.Remote;
        p.ConfirmReason = !ctl ? (d?.Alias ?? "设备") + "为本地只读 · 先申请控制权" : _entries.Any(e => e.Text.Trim().Length == 0) ? "先填完 4 路读数" : null;
        var kpis = new List<PtKpiVm> { new("搅拌（整台）", d?.Stir is { On: true } ? "302" : "0", "rpm") };
        for (int i = 0; i < 4; i++) kpis.Add(new PtKpiVm("RV" + (i + 1).ToString(Inv), PressureDisplay.Fixed(1.0 + (i - 1) * .003 + (i == 3 ? .006 : 0), 3), PressureDisplay.Unit));
        if (!p.Kpis.SequenceEqual(kpis)) { p.Kpis.Clear(); foreach (var k in kpis) p.Kpis.Add(k); }
        p.Note = "R0 前置检查表已勾全（两人签字 · 张工 / 李工 08:55）· 环境 22.4 °C / 41 %RH / 101.2 kPa";
        p.HasNote = true;
    }

    /// <summary>结果表（样稿 PT_RES；搅拌两行按釜转速改成 300 / 450 rpm，S2 写「450 rpm 上限内」）。</summary>
    public void ApplyResults(PerfPageVm p)
    {
        if (p.Results.Count > 0) return;
        PtResVm R(string item, string ch, string set, string meas, string err, string v) =>
            new(item, ch, set, meas, err, v, v == "信息" ? BadgeTone.Info : BadgeTone.None);
        foreach (var r in new[]
        {
            R("S1", "RV1–4", "300 rpm", "301.2 rpm", "+0.4 %", "不判定"),
            R("S1", "RV1–4", "450 rpm", "449.1 rpm", "−0.2 %", "不判定"),
            R("S2", "全局", "450 rpm 上限内", "未失步", "—", "信息"),
            R("P1", "RV1", "0.50 MPa", "0.502 MPa", "+0.4 %", "不判定"),
            R("P1", "RV2", "0.50 MPa", "0.497 MPa", "−0.6 %", "不判定"),
            R("P1", "RV3", "0.50 MPa", "0.505 MPa", "+1.0 %", "不判定"),
            R("P1", "RV4", "0.50 MPa", "0.499 MPa", "−0.2 %", "不判定"),
        }) p.Results.Add(r);
        p.HasResults = true;
    }

    /// <summary>泄漏测试最近一次（样稿 LEAK；1号机 11-10 16:00，其余每路 145.1 → 145.0 psi、0.20 psi/h）。</summary>
    public static LeakSummary Leak(DeviceVm d)
    {
        if (d.K == 1)
        {
            (int N, double? A, double? B, double? R, string St)[] rows =
            {
                (1, 145.2, 145.0, .40, "ok"), (2, 145.1, 144.9, .40, "ok"), (3, 145.3, 144.0, 2.6, "bad"), (4, 145.0, 144.9, .20, "ok"),
                (5, 145.2, 145.1, .20, "ok"), (6, 145.1, 145.0, .20, "ok"), (7, null, null, null, "skip"), (8, 145.0, 144.8, .40, "ok"),
            };
            return new LeakSummary("leak-20261110-160000", new DateTime(2026, 11, 10, 16, 0, 0), rows.Select(r => new LeakChannel(r.N, r.St, r.St == "skip" ? "未测（停用）" : "", r.A, r.B, r.R)).ToList());
        }
        var when = d.K == 3 ? new DateTime(2026, 11, 15, 9, 12, 0) : new DateTime(2026, 11, 8, 10, 30, 0);
        return new LeakSummary("leak-" + when.ToString("yyyyMMdd-HHmmss", Inv), when, d.Reactors.Where(r => r.Installed).Select(r =>
            r.Own == Core.State.StateKey.Idle ? new LeakChannel(r.N, "skip", "未测（停用）", null, null, null) : new LeakChannel(r.N, "ok", "", 145.1, 145.0, .20)).ToList());
    }
}
