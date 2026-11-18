using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Pages.Settings;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages.Perf;

/// <summary>
/// 泄漏测试（融合版 F8，样稿 PAGES.test.leak）：左「泄漏测试 · 1号机 · HT-PR8」—— 参数（来自设备设置 · 只读：测试压力、平衡 / 测量时间、泄漏率阈值）、
/// 阶段（充压 → 平衡 → 测量 → 判读 · 泄压）、测试通道、说明；右「结果 · 最近一次 · 11-10 16:00」—— 每路起始 / 结束 psi、泄漏率、阈值、结论。
/// 阈值 = 设备设置的泄漏率阈值（同记录页保压类的判据，<see cref="LeakTestInfo.RatePsiH"/>）。
/// </summary>
public sealed partial class PerfPageVm
{
    /// <summary>泄漏测试勾的通道（记录页「再做一次」预选的、或这台设备此刻空闲的）。</summary>
    private readonly HashSet<int> _leakSel = new();
    /// <summary><see cref="_leakSel"/> 是哪台设备的（换设备时重新按空闲的预选）。</summary>
    private int _leakSelK;
    private readonly Dictionary<int, (LeakSummary? S, DateTime At, string? Error)> _leakCache = new();
    private readonly HashSet<int> _leakFetching = new();
    /// <summary>真机：多久重新取一次最近的结果。</summary>
    public static readonly TimeSpan LeakRefetch = TimeSpan.FromSeconds(30);

    public ObservableCollection<SetRoRow> LeakParams { get; } = new();
    public ObservableCollection<LeakPhaseVm> LeakPhases { get; } = new();
    public ObservableCollection<PtChanVm> LeakChans { get; } = new();
    public ObservableCollection<LeakRowVm> LeakRows { get; } = new();
    [ObservableProperty] private string _leakResSub = "";
    [ObservableProperty] private string? _leakWarn;
    [ObservableProperty] private string _leakEmpty = "";
    [ObservableProperty] private bool _hasLeakRows;
    /// <summary>正在测（真机快照 leakTest.running）：「正在测试 · 测量 42 %」。</summary>
    [ObservableProperty] private string? _leakState;
    [ObservableProperty] private string _leakSelText = "";

    /// <summary>这台设备的泄漏测试设置（没有时 HMI 出厂值）。</summary>
    public static LeakTestInfo LeakOf(DeviceVm? d) => d?.Source.Plant.Leak ?? LeakTestInfo.HmiDefault;

    private static LeakSnap? LeakSnapOf(DeviceVm d) => d.Source.Latest?.LeakTest;

    /// <summary>这一路现在能不能做泄漏测试（样稿：所选通道不能在运行、加热或报警中）：'' = 能，否则原因。</summary>
    public static string LeakBusy(ReactorVm r) =>
        !r.Installed ? "未安装" : r.Own == StateKey.Idle ? "已停用" : RunRowText.Running(r.Own) ? "正在运行" : r.Own == StateKey.Alarm ? "报警中" : r.Heating ? "正在加热" : "";

    private void RefreshLeak()
    {
        var d = Dev;
        var leak = LeakOf(d);
        string N(double v, string f = "0.#") => v.ToString(f, Inv);
        var pars = new List<SetRoRow>
        {
            // 测试压力按设置的压力单位，括号里是设备设置原来的 psi（单位就是 psi 时不写括号）；泄漏率、各路起始 / 结束照旧 psi（HMI 的泄漏测试记录）
            new("测试压力", LeakTestValue(leak.TestPsi), PressureDisplay.Unit, PressureDisplay.Primary == PressureUnit.Psi ? "" : "（" + Math.Round(leak.TestPsi).ToString(Inv) + " psi）"),
            new("平衡时间", N(leak.SettleMin), "min"),
            new("测量时间", N(leak.MeasureMin), "min"),
            new("泄漏率阈值", N(leak.RatePsiH, "0.0##"), "psi/h", "（按温度补偿）"),
        };
        if (!LeakParams.SequenceEqual(pars)) { LeakParams.Clear(); foreach (var r in pars) LeakParams.Add(r); }

        // 阶段：真机正在测时按 HMI 的阶段（fill / equilibrate / measure / vent）标当前；否则最近一次做完了 = 全部完成
        var snap = d == null ? null : LeakSnapOf(d);
        int cur = snap is { Running: true } ? snap.Phase switch { "fill" or "prep" => 0, "equilibrate" => 1, "measure" => 2, "vent" => 3, _ => 0 } : -1;
        var sum = d == null ? null : LeakResult(d);
        string[] names = { "充压至 " + LeakTestValue(leak.TestPsi) + " " + PressureDisplay.Unit, "平衡 " + N(leak.SettleMin) + " min", "测量 " + N(leak.MeasureMin) + " min", "判读 · 泄压" };
        var phases = names.Select((t, i) => new LeakPhaseVm((i + 1).ToString(Inv) + ". " + t, cur >= 0 ? i < cur : sum != null, i == cur)).ToList();
        if (!LeakPhases.SequenceEqual(phases)) { LeakPhases.Clear(); foreach (var ph in phases) LeakPhases.Add(ph); }
        LeakState = snap is { Running: true } ? "正在测试 · " + names[Math.Max(0, cur)] + " · " + Math.Round(snap.Progress * (snap.Progress <= 1 ? 100 : 1)).ToString(Inv) + " %" : null;

        // 通道（换设备时按此刻空闲的预选）
        if (d != null && _leakSelK != d.K)
        {
            _leakSel.Clear();
            foreach (var r in d.Reactors) if (LeakBusy(r).Length == 0) _leakSel.Add(r.N);
            _leakSelK = d.K;
        }
        var rs = d?.Reactors.Where(r => r.Installed).ToList() ?? new List<ReactorVm>();
        while (LeakChans.Count > rs.Count) LeakChans.RemoveAt(LeakChans.Count - 1);
        for (int i = 0; i < rs.Count; i++)
        {
            if (i >= LeakChans.Count || LeakChans[i].N != rs[i].N)
            {
                var c = new PtChanVm(rs[i].N, ToggleChan);
                if (i < LeakChans.Count) LeakChans[i] = c; else LeakChans.Add(c);
            }
            var ch = LeakChans[i];
            var busy = LeakBusy(rs[i]);
            ch.IsOn = _leakSel.Contains(ch.N) && busy.Length == 0;
            ch.Reason = busy.Length > 0 ? ch.Name + busy + " · 不能做泄漏测试" : snap is { Running: true } ? "泄漏测试正在进行" : null;
            ch.Tip = ch.IsOn ? ch.Name + " · 参加泄漏测试 · 点一下不测" : ch.Name + " · 点一下参加";
        }
        int nSel = LeakChans.Count(c => c.IsOn);
        LeakSelText = "已选 " + nSel.ToString(Inv) + " 路" + (nSel > 0 ? " · " + string.Join(" ", LeakChans.Where(c => c.IsOn).Select(c => c.Name)) : "");

        // 结果
        var rows = new List<LeakRowVm>();
        if (sum != null)
        {
            var lim = leak.RatePsiH.ToString("0.0##", Inv);
            foreach (var c in sum.Channels)
            {
                var ok = PerfTexts.Verdict(c, leak.RatePsiH);
                rows.Add(new LeakRowVm("RV" + c.Rv.ToString(Inv), c.StartPsi is double a ? a.ToString("0.0", Inv) : "—", c.EndPsi is double b ? b.ToString("0.0", Inv) : "—",
                    c.RatePsiH is double r ? r.ToString("0.00", Inv) : "—", lim, ok, ok == null ? (c.Detail.Length > 0 ? c.Detail : "未测") : ""));
            }
        }
        if (!LeakRows.SequenceEqual(rows)) { LeakRows.Clear(); foreach (var r in rows) LeakRows.Add(r); }
        HasLeakRows = LeakRows.Count > 0;
        var bad = LeakRows.Where(r => r.IsBad).Select(r => r.Rv).ToList();
        LeakWarn = bad.Count > 0 ? string.Join("、", bad) + " 泄漏率超过阈值 · 检查釜盖密封和接头后重测" : null;
        LeakResSub = sum?.Start is { } st ? "最近一次 · " + st.ToString("MM-dd HH:mm", Inv) : "";
        var err = d != null && _leakCache.TryGetValue(d.K, out var e) ? e.Error : null;
        LeakEmpty = d == null ? "没有设备" : _leakFetching.Contains(d.K) ? "正在读取最近一次的结果…" : err != null ? "读取失败：" + err
            : d.Source.IsDemo || !d.IsOffline ? "这台设备还没有做过泄漏测试" : d.Alias + "未连接 · 连上后读取最近一次的结果";
        RefreshLeakTools();
    }

    /// <summary>最近一次的结果：演示 = 样稿 LEAK；真机 = 缓存（过期 / 测完了重新取）。</summary>
    private LeakSummary? LeakResult(DeviceVm d)
    {
        if (d.Source.IsDemo) return PerfDemo.Leak(d);
        var now = DateTime.UtcNow;
        bool have = _leakCache.TryGetValue(d.K, out var c);
        bool done = LeakSnapOf(d) is { Done: true, RunId: { } rid } && (c.S == null || c.S.RunId != rid);
        if ((!have || now - c.At > LeakRefetch || done) && !d.IsOffline && d.Source is LiveDeviceSource { Connection: not null }) _ = FetchLeakAsync(d);
        return have ? c.S : null;
    }

    private async Task FetchLeakAsync(DeviceVm d)
    {
        if (!_leakFetching.Add(d.K) || d.Source is not LiveDeviceSource { Connection: { } conn }) return;
        try
        {
            var list = await conn.Rest.LeakTestsAsync(CancellationToken.None).ConfigureAwait(true);
            var last = list.Items.OrderByDescending(i => i.Start ?? DateTime.MinValue).ThenByDescending(i => i.RunId, StringComparer.Ordinal).FirstOrDefault();
            LeakSummary? sum = null;
            if (last != null)
            {
                var json = await conn.Rest.LeakTestSummaryAsync(last.RunId, CancellationToken.None).ConfigureAwait(true);
                sum = PerfTexts.ParseLeak(json) is { } s ? s with { Start = s.Start ?? last.Start?.ToLocalTime() } : null;
            }
            _leakCache[d.K] = (sum, DateTime.UtcNow, null);
        }
        catch (Exception ex) { _leakCache[d.K] = (null, DateTime.UtcNow, Fleet.DeviceActions.Describe(ex)); }
        finally { _leakFetching.Remove(d.K); }
        if (IsLeak && Dev?.K == d.K) RefreshLeak();
    }

    private void RefreshLeakTools()
    {
        var d = Dev;
        var snap = d == null ? null : LeakSnapOf(d);
        var chosen = LeakChans.Where(c => c.IsOn).ToList();
        _lkStart.Reason = ControlReason(d) ?? (snap is { Running: true } ? "泄漏测试正在进行" : chosen.Count == 0 ? "先选要测的通道" : null);
        _lkAbort.Reason = null;
        _lkExport.Reason = HasLeakRows ? null : "还没有结果";
    }

    /// <summary>「开始泄漏测试」（真机 POST /leak-tests {channels}，要控制权；测试压力用设备设置的）。</summary>
    public async Task StartLeakAsync()
    {
        var d = Dev;
        if (_lkStart.Reason is { } why) { _shell.Toast(why, ToastKind.Warn); return; }
        var chs = LeakChans.Where(c => c.IsOn).Select(c => c.N).ToArray();
        var what = "泄漏测试：" + string.Join(" ", chs.Select(n => "RV" + n.ToString(Inv))) + " · 充压 → 平衡 → 测量 → 判读";
        if (d!.Source.IsDemo) { _shell.Toast("开始" + what + "（演示）", ToastKind.Info); return; }
        if (d.Source is not LiveDeviceSource { Connection: { } c }) { _shell.Toast(d.Alias + "未连接", ToastKind.Warn); return; }
        try
        {
            var r = await c.Control.StartLeakTestAsync(chs, null, ParallelReactor.Client.IdempotencyKeys.New("pc-leak"), CancellationToken.None).ConfigureAwait(true);
            _shell.Toast(d.Alias + " · 已开始" + what + (r.Result?.RunId is { } id ? "（" + id + "）" : ""), ToastKind.Ok);
            if (Fleet.DeviceActions.WarningText(r.Warnings) is { } w) _shell.Toast(d.Alias + " · " + w, ToastKind.Warn);
        }
        catch (Exception ex) { _shell.Toast(d.Alias + " · 泄漏测试没有开始：" + Fleet.DeviceActions.Describe(ex), ToastKind.Err); }
        _shell.Fleet.RequestRefresh();
    }

    /// <summary>「中止」（停止类：不需要控制权；真机 DELETE /leak-tests/current）。</summary>
    public async Task AbortLeakAsync()
    {
        var d = Dev;
        if (d == null) return;
        if (d.Source.IsDemo || d.Source is not LiveDeviceSource { Connection: { } c }) { _shell.Toast(d.Source.IsDemo ? "当前没有进行中的泄漏测试（演示）" : d.Alias + "未连接", ToastKind.Info); return; }
        if (LeakSnapOf(d) is not { Running: true }) { _shell.Toast(d.Alias + " 当前没有进行中的泄漏测试", ToastKind.Info); return; }
        try
        {
            await c.Rest.AbortLeakTestAsync(CancellationToken.None).ConfigureAwait(true);
            _shell.Toast(d.Alias + " · 泄漏测试已中止（泄压）", ToastKind.Ok);
        }
        catch (Exception ex) { _shell.Toast(d.Alias + " · 中止失败：" + Fleet.DeviceActions.Describe(ex), ToastKind.Err); }
    }

    /// <summary>
    /// 泄漏测试压力（设备设置 leakTest.testPsi）按设置的压力单位：MPa 2 位（同样稿，按 145.04 换算）、bar 2 位、psi 取整（设备原值）。
    /// </summary>
    private static string LeakTestValue(double psi) => PressureDisplay.Primary switch
    {
        PressureUnit.Psi => Math.Round(psi).ToString(Inv),
        PressureUnit.Bar => (psi / PressureUnits.PsiPerBar).ToString("0.00", Inv),
        _ => (psi / SettingsTexts.PsiPerMPa).ToString("0.00", Inv),
    };

    /// <summary>导出目录（测试换成临时目录）：数据目录 / export。</summary>
    public static Func<string> ExportDir { get; set; } = () => Path.Combine(DataDirs.Root, "export");

    /// <summary>「导出结果」：最近一次的各路结果 → 数据目录/export/&lt;设备&gt;-&lt;runId&gt;.csv（UTF-8 BOM）。</summary>
    public string? ExportLeak()
    {
        var d = Dev;
        if (d == null || LeakRows.Count == 0) { _shell.Toast("还没有泄漏测试结果", ToastKind.Info); return null; }
        try
        {
            var sum = LeakResult(d);
            var sb = new StringBuilder("通道,起始 psi,结束 psi,泄漏率 psi/h,阈值 psi/h,结论\n");
            foreach (var r in LeakRows) sb.Append(r.Rv).Append(',').Append(r.Start).Append(',').Append(r.End).Append(',').Append(r.Rate).Append(',').Append(r.Limit).Append(',').Append(r.Verdict).Append('\n');
            var dir = ExportDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, d.Alias + "-" + (sum?.RunId is { Length: > 0 } id ? id : "leak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv)) + ".csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _shell.Toast("已导出泄漏测试结果 → " + path, ToastKind.Ok);
            return path;
        }
        catch (Exception ex) { _shell.Toast("导出失败：" + ex.Message, ToastKind.Err); return null; }
    }
}
