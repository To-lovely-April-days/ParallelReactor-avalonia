using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 自动泄漏测试（惰性气体低压保压）：选通道 → 脉冲充压到测试压力（到压即关该路 SV）→ 总管泄压 → 平衡 →
/// 测量压降（温度补偿 + 最小二乘拟合）→ 自动判读 → 尝试经排空阀泄压并核实。
/// 阀门或压力采集未配置 / 离线时拒绝启动（见 <see cref="MainViewModel.GasBlockReason"/>）。
/// </summary>
public partial class LeakViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private const double FillTimeoutSec = 180;   // 充压阶段最长时间
    private const double SampleSec = 10;         // 平衡 / 测量阶段的采样间隔（秒）

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isDiagOpen;
    [ObservableProperty] private bool _started;     // 已点「开始测试」
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _failed;      // 结果红色（不通过 / 中止 / 失败）
    [ObservableProperty] private bool _leakFailed;  // 确有通道泄漏超标（显示维修指南）
    [ObservableProperty] private double _progress;  // 0–100
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _subTitle = "";
    [ObservableProperty] private string _introText = "";
    [ObservableProperty] private string _introNote = "";

    public ObservableCollection<LeakCell> Cells { get; } = new();
    public ObservableCollection<LeakStage> Stages { get; } = new();
    public List<DiagStep> DiagSteps { get; } = new();

    public bool ShowStartBtn => !Started;
    public bool ShowDiagBtn => Done && LeakFailed;
    public string CloseText => Running ? "后台运行" : "关闭";
    public string DiagHeadText => $"检测到通道压降超过 {_main.Settings.LeakRate:0.#} psi/hr 阈值，按优先级排查：";

    public IBrush ResultBrush => Failed ? B("#e0394c") : B("#5fae14");
    public IBrush ResultBg => Failed ? B("#14E0394C") : B("#14A3E635");
    public IBrush ResultBorder => Failed ? B("#66E0394C") : B("#66A3E635");

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public LeakViewModel(MainViewModel main)
    {
        _main = main;
        BuildDiag();
        Reset();
    }

    partial void OnStartedChanged(bool value) => OnPropertyChanged(nameof(ShowStartBtn));
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CloseText));
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(ShowDiagBtn));
    partial void OnLeakFailedChanged(bool value) => OnPropertyChanged(nameof(ShowDiagBtn));
    partial void OnFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(ResultBrush));
        OnPropertyChanged(nameof(ResultBg));
        OnPropertyChanged(nameof(ResultBorder));
    }

    [RelayCommand]
    private void Open()
    {
        if (!Running) Reset();   // 测试在后台进行时重新打开只是查看进度
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        if (Running) _main.Toast("ok", "泄漏测试在后台继续 · 点工具栏「泄漏测试」查看进度");
    }

    /// <summary>中止测试（同「全部停止」：序列收到取消后全关阀）。</summary>
    [RelayCommand]
    private void Abort() => _main.AbortGas();

    [RelayCommand]
    private void ShowDiag()
    {
        IsOpen = false;
        OnPropertyChanged(nameof(DiagHeadText));
        IsDiagOpen = true;
    }

    [RelayCommand]
    private void CloseDiag() => IsDiagOpen = false;

    [RelayCommand]
    private void Start()
    {
        if (Running) return;
        var picked = Cells.Where(c => c.CanPick && c.Selected).ToList();
        if (picked.Count == 0) { _main.Toast("warn", "请至少选择一个测试通道"); return; }
        // 阀门 / 压力采集未配置或离线一律拒绝：充压必须按实测压力闭环，并全程做超压监视
        if (_main.GasBlockReason(needPressure: true) is { } why) { _main.Toast("err", why); return; }
        if (_main.Settings.LeakTestPsi >= _main.Settings.OverPressure * 0.9)
        {
            _main.Toast("err", "测试压力须低于超压报警值的 90% · 请在「设置 → 参数与报警」调整");
            return;
        }
        _ = RunAsync(picked);
    }

    // ============ 测试流程 ============

    private async Task RunAsync(List<LeakCell> picked)
    {
        var s = _main.Settings;
        double target = s.LeakTestPsi, thr = s.LeakRate;
        var ids = picked.Select(c => c.Id).ToArray();

        Started = true; Running = true; Done = false; Failed = false; LeakFailed = false; Progress = 0;
        foreach (var c in Cells)
        {
            c.Locked = true;
            if (picked.Contains(c)) { c.Status = ""; c.Detail = "…"; }
            else { c.Status = "skip"; c.Detail = c.CanPick ? "不测" : "运行中"; }
        }
        var ct = _main.BeginGas("泄漏测试", ids);
        _rec = new LeakRecord(DateTimeOffset.Now);
        _main.Logger.AddMarker($"泄漏测试开始 {_rec.RunId}");
        _main.Alarm.Log("info", "系统", "泄漏测试开始",
            $"{Rvs(ids)} · 惰性气体 {target:0.#} psi · 平衡 {s.LeakSettleMin:0.#} min / 测量 {s.LeakMeasureMin:0.#} min · 逐点数据 leak/{_rec.RunId}.csv");

        var tested = new List<LeakCell>();
        var overFill = new List<int>();   // 充压冲过测试压力的通道（减压阀调得过高）
        Dictionary<int, double>? rates = null;
        string? fail = null;
        bool aborted = false;
        bool? vented = null;
        try
        {
            SetStage(0);
            PhaseText = "关闭全部阀门，读取初始压力…";
            await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
            await _main.HoldAsync(ValveFrame.AllClosed(), PurgePlan.DeadSec, ct);
            var p0 = await SampleAsync(LeakRecord.Prep, picked);
            var fillList = new List<LeakCell>();
            foreach (var c in picked)
            {
                c.Pressure = p0[c.Id - 1];
                // 釜压已明显高于测试压力：软件不一定能泄压（取决于进气单向阀方向），跳过
                if (c.Pressure > target * 1.25) { c.Status = "skip"; c.Detail = "初始压力过高"; }
                else fillList.Add(c);
            }
            if (fillList.Count == 0) throw new GasFaultException("所选通道初始压力均高于测试压力，请先泄压");

            tested = await FillAsync(fillList, target, ct);
            if (tested.Count == 0) throw new GasFaultException("没有通道充压成功");
            overFill = tested.Where(c => c.Pressure > target * 1.25).Select(c => c.Id).ToList();

            // 总管泄压：关气源后开排空，避免总管余压经 SV 补进釜里、掩盖泄漏
            PhaseText = "总管泄压…";
            await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
            await _main.HoldAsync(ValveFrame.AllClosed(), PurgePlan.DeadSec, ct);
            await _main.ApplyValvesAsync(ValveFrame.VentOnly(), ct);
            await _main.HoldAsync(ValveFrame.VentOnly(), 3, ct);
            await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
            Progress = 12;

            SetStage(1);
            await SettleAsync(tested, s.LeakSettleMin, ct);

            SetStage(2);
            rates = await MeasureAsync(tested, s.LeakMeasureMin, ct);

            SetStage(3);
            Judge(tested, rates, thr);
            vented = await TryVentAsync(tested, ct);
        }
        catch (OperationCanceledException) { aborted = true; }
        catch (GasFaultException ex) { fail = ex.Message; }
        catch (Exception ex) { fail = $"执行异常：{ex.Message}"; }
        finally
        {
            await _main.CloseAllValvesAsync();
            _main.EndGas();
            Running = false;
        }

        Finish(picked, tested, overFill, rates, thr, vented, aborted, fail);
        try { SaveRecord(picked, rates, target, thr, aborted, fail); }
        catch (Exception ex) { _main.Alarm.Log("warn", "系统", "泄漏测试逐点数据未保存", ex.Message); }
    }

    // ============ 逐点数据（P0-3）：每次读压都记一行，结束后整文件写到 数据目录/leak/ ============

    private LeakRecord? _rec;

    /// <summary>读一次 8 路压力（同 <see cref="MainViewModel.ReadPressuresAsync"/>），并把 cells 的这一点记入逐点数据。</summary>
    private async Task<double[]> SampleAsync(string phase, IEnumerable<LeakCell> cells)
    {
        var smp = await _main.ReadPressureSampleAsync();
        if (_rec != null)
            foreach (var c in cells)
                _rec.Add(c.Id, phase, smp.At, smp.Raw[c.Id - 1], smp.Psi[c.Id - 1], TempOf(c.Id));
        return smp.Psi.ToArray();
    }

    /// <summary>写本次测试的逐点 CSV + 汇总 CSV / JSON（后台写盘；中止 / 失败时也写已采到的数据）。</summary>
    private void SaveRecord(List<LeakCell> picked, Dictionary<int, double>? rates, double target, double thr, bool aborted, string? fail)
    {
        var rec = _rec;
        _rec = null;
        if (rec == null) return;
        var s = _main.Settings;
        var sum = rec.Summarize(picked.Select(c => (c.Id, c.Status, c.Detail)), rates);
        var pars = new
        {
            testPsi = target, thresholdPsiPerH = thr, settleMin = s.LeakSettleMin, measureMin = s.LeakMeasureMin,
            sampleSec = SampleSec, aborted, fail,
        };
        _main.Logger.WriteFile($"leak/{rec.RunId}.csv", rec.SamplesCsv());
        _main.Logger.WriteFile($"leak/{rec.RunId}-summary.csv", rec.SummaryCsv(sum));
        _main.Logger.WriteFile($"leak/{rec.RunId}-summary.json", rec.SummaryJson(sum, pars));
        _main.Logger.AddMarker($"泄漏测试结束 {rec.RunId}");
    }

    /// <summary>
    /// 脉冲充压：每轮开惰性气体 + 未到压通道的 SV，按各通道估算的增益分别定开阀时长、到时逐路关 SV，
    /// 全关后静置 1s 读压。目标留 2% 容差；连续 4 轮几乎不升压的通道：≥ 80% 目标按「减压阀限压」接受，否则判充压失败。
    /// 釜压不可能超过惰性气体减压阀的出口压力，这是低压测试的根本保障。
    /// </summary>
    private async Task<List<LeakCell>> FillAsync(List<LeakCell> cells, double target, CancellationToken ct)
    {
        double tol = Math.Max(0.5, target * 0.02);
        var ok = new List<LeakCell>();
        var pend = new List<LeakCell>();
        foreach (var c in cells)
        {
            if (c.Pressure >= target - tol) { ok.Add(c); c.Detail = "已在测试压力"; }
            else { pend.Add(c); c.Detail = "充压中"; }
        }

        var gain = new Dictionary<int, double>();   // psi / 开阀秒
        var stall = new Dictionary<int, int>();
        var sw = Stopwatch.StartNew();
        while (pend.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed.TotalSeconds > FillTimeoutSec)
            {
                foreach (var c in pend) { c.Status = "bad"; c.Detail = "充压超时"; }
                break;
            }

            // 各通道本轮开阀时长：首轮 0.2s 试探，之后按增益只补剩余的 60%，从下方逼近、不冲过
            var plan = pend.Select(c =>
            {
                double sec = gain.TryGetValue(c.Id, out var g) && g > 0.05 ? 0.6 * (target - c.Pressure) / g : 0.2;
                return (Cell: c, Sec: Math.Clamp(sec, 0.1, 2.0));
            }).OrderBy(x => x.Sec).ToList();
            var before = pend.ToDictionary(c => c.Id, c => c.Pressure);

            var open = plan.Select(x => x.Cell.Id).ToList();
            var frame = ValveFrame.Gas(0, open.ToArray());
            var t0 = Stopwatch.StartNew();
            await _main.ApplyValvesAsync(frame, ct);
            foreach (var (c, sec) in plan)
            {
                double wait = sec - t0.Elapsed.TotalSeconds;
                if (wait > 0) await _main.HoldAsync(frame, wait, ct);   // 保持期间同样做超压监视 + 心跳
                open.Remove(c.Id);
                frame = open.Count > 0 ? ValveFrame.Gas(0, open.ToArray()) : ValveFrame.AllClosed();
                await _main.ApplyValvesAsync(frame, ct);
            }

            await _main.HoldAsync(ValveFrame.AllClosed(), 1.0, ct);   // 关阀后静置再读，避开充气瞬间的动态压力
            var p = await SampleAsync(LeakRecord.Fill, cells);   // 逐点数据记全部待充通道（含已到压的），不只本轮开阀的
            _main.ThrowIfGasUnsafe();
            foreach (var (c, sec) in plan)
            {
                c.Pressure = p[c.Id - 1];
                double rise = c.Pressure - before[c.Id];
                gain[c.Id] = rise / sec;
                stall[c.Id] = rise < 0.3 ? stall.GetValueOrDefault(c.Id) + 1 : 0;
                if (c.Pressure >= target - tol)
                {
                    pend.Remove(c); ok.Add(c);
                    c.Detail = c.Pressure > target * 1.25 ? "超出测试压力" : "已到压";
                }
                else if (stall[c.Id] >= 4)
                {
                    pend.Remove(c);
                    if (c.Pressure >= target * 0.8) { ok.Add(c); c.Detail = "减压阀限压"; }
                    else { c.Status = "bad"; c.Detail = "充压不上"; }
                }
                else c.Detail = $"充压 {Math.Clamp(c.Pressure / target * 100, 0, 99):0}%";
            }
            int doneN = cells.Count - pend.Count;
            Progress = 10.0 * doneN / Math.Max(1, cells.Count);
            PhaseText = $"充压中 · {doneN}/{cells.Count} 路完成";
        }
        return ok;
    }

    /// <summary>平衡：全关静置，周期读压（同时做超压监视）。</summary>
    private async Task SettleAsync(List<LeakCell> tested, double minutes, CancellationToken ct)
    {
        double total = minutes * 60;
        var sw = Stopwatch.StartNew();
        foreach (var c in tested) c.Detail = "平衡中";
        while (true)
        {
            double el = sw.Elapsed.TotalSeconds;
            var p = await SampleAsync(LeakRecord.Equilibrate, tested);
            _main.ThrowIfGasUnsafe();
            foreach (var c in tested) c.Pressure = p[c.Id - 1];
            Progress = 12 + 23 * Math.Min(1, el / total);
            PhaseText = $"平衡中 · 剩余 {Mmss(total - el)}";
            if (el >= total) break;
            await _main.HoldAsync(ValveFrame.AllClosed(), Math.Min(SampleSec, total - el), ct);
        }
    }

    /// <summary>测量：周期采样压力，按釜温折算到测量起点温度后最小二乘拟合压降速率（psi/hr）。</summary>
    private async Task<Dictionary<int, double>> MeasureAsync(List<LeakCell> tested, double minutes, CancellationToken ct)
    {
        double total = minutes * 60;
        var series = tested.ToDictionary(c => c.Id, _ => new List<(double Min, double Psi)>());
        var t0 = tested.ToDictionary(c => c.Id, c => TempOf(c.Id));
        if (_rec != null) foreach (var kv in t0) _rec.TRef[kv.Key] = kv.Value;   // 逐点数据的补偿参考温度 = 判读用的同一参考
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double el = sw.Elapsed.TotalSeconds;
            var p = await SampleAsync(LeakRecord.Measure, tested);
            _main.ThrowIfGasUnsafe();
            double vmin = el / 60;   // 测量已进行的分钟数
            foreach (var c in tested)
            {
                double psi = p[c.Id - 1], t = TempOf(c.Id), tr = t0[c.Id];
                double comp = double.IsNaN(t) || double.IsNaN(tr) ? psi : LeakMath.Compensate(psi, t, tr);
                var list = series[c.Id];
                list.Add((vmin, comp));
                c.Pressure = psi;
                c.Detail = vmin >= 3
                    ? $"≈ {Math.Max(0, LeakMath.DropRatePerHour(list)):0.00} psi/hr"
                    : $"-{Math.Max(0, list[0].Psi - comp):0.00} psi";
            }
            Progress = 35 + 62 * Math.Min(1, el / total);
            PhaseText = $"测量中 · 剩余 {Mmss(total - el)}";
            if (el >= total) break;
            await _main.HoldAsync(ValveFrame.AllClosed(), Math.Min(SampleSec, total - el), ct);
        }
        return series.ToDictionary(kv => kv.Key, kv => LeakMath.DropRatePerHour(kv.Value));
    }

    private void Judge(List<LeakCell> tested, Dictionary<int, double> rates, double thr)
    {
        foreach (var c in tested)
        {
            double r = rates[c.Id];
            c.Status = r < thr ? "ok" : "bad";
            c.Detail = $"{Math.Max(0, r):0.00} psi/hr";
        }
    }

    /// <summary>
    /// 测试结束后尝试经排空阀泄压：开排空 + 被测通道 SV（不开任何气源）6s，再看釜压是否明显下降。
    /// 气路中的单向阀只允许 总管→釜 时，釜压不会下降——据此提示操作员手动泄压，而不是假定已泄压。
    /// </summary>
    private async Task<bool> TryVentAsync(List<LeakCell> tested, CancellationToken ct)
    {
        PhaseText = "尝试经排空阀泄压…";
        var ids = tested.Select(c => c.Id).ToArray();
        var before = await SampleAsync(LeakRecord.Vent, tested);
        var f = ValveFrame.VentOnly(ids);
        await _main.ApplyValvesAsync(f, ct);
        await _main.HoldAsync(f, 6, ct);
        await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
        await _main.HoldAsync(ValveFrame.AllClosed(), 0.5, ct);
        var after = await SampleAsync(LeakRecord.Vent, tested);
        foreach (var c in tested) c.Pressure = after[c.Id - 1];
        return tested.All(c => after[c.Id - 1] < Math.Max(2.0, before[c.Id - 1] * 0.2));
    }

    private void Finish(List<LeakCell> picked, List<LeakCell> tested, List<int> overFill, Dictionary<int, double>? rates,
        double thr, bool? vented, bool aborted, string? fail)
    {
        Done = true;
        if (rates != null)
        {
            _main.Settings.LastLeakTestUtc = DateTime.UtcNow;   // 提醒周期从这次算起
            _main.PersistSoon();

            var judged = picked.Where(c => c.Status is "ok" or "bad").ToList();   // 含充压失败的通道
            var bad = judged.Where(c => c.Status == "bad").ToList();
            LeakFailed = bad.Count > 0;
            Failed = LeakFailed;
            Progress = 100;
            SetStage(Stages.Count);
            PhaseText = "测试结束";
            ResultTitle = LeakFailed ? "✕ 测试不通过" : "✓ 测试通过";
            ResultText = (LeakFailed
                    ? $"{judged.Count - bad.Count}/{judged.Count} 路通过；{Rvs(bad.Select(c => c.Id))} 压降超标或充压失败。" +
                      "常见原因：面密封 O 圈、注射口密封、夹具未拧紧、进气接头松动。"
                    : $"全部 {judged.Count} 路压降均 < {thr:0.#} psi/hr，密封良好。")
                + (overFill.Count > 0 ? $" {Rvs(overFill)} 充压超出测试压力，请检查惰性气体减压阀是否调得过高。" : "")
                + VentNote(vented, tested)
                + (fail != null ? $" 泄压阶段出错：{fail}。" : "");
            _main.Alarm.Log(LeakFailed ? "warn" : "info", "系统", LeakFailed ? "泄漏测试不通过" : "泄漏测试通过",
                string.Join(" · ", judged.Select(c => $"RV{c.Id} {c.Detail}")));
            _main.Toast(LeakFailed ? "err" : "ok", LeakFailed ? $"泄漏测试不通过 · {Rvs(bad.Select(c => c.Id))}" : "泄漏测试通过");
        }
        else
        {
            Failed = true;
            LeakFailed = false;
            foreach (var c in picked.Where(c => c.Status == "")) c.Detail = "未完成";
            PhaseText = aborted ? "已中止" : "已停止";
            ResultTitle = aborted ? "测试已中止" : "测试失败";
            ResultText = (aborted ? "已按要求中止，阀门已全关。" : $"{fail}。阀门已全关。")
                + "如已充压，釜内仍有压力，拆釜前请先泄压。";
            _main.Alarm.Log(aborted ? "warn" : "err", "系统", aborted ? "泄漏测试中止" : "泄漏测试失败",
                aborted ? "手动中止 · 阀门已全关" : $"{fail} · 阀门已全关");
            _main.Toast(aborted ? "warn" : "err", aborted ? "泄漏测试已中止 · 阀门全关" : $"泄漏测试失败 · {fail}");
        }
    }

    private static string VentNote(bool? vented, List<LeakCell> tested)
    {
        if (vented == true) return " 测试后已经排空阀泄压。";
        double maxP = tested.Count > 0 ? tested.Max(c => c.Pressure) : 0;
        return vented == false
            ? $" 注意：经排空阀泄压后釜压未明显下降，仍约 {Units.FormatP(maxP)}（进气单向阀很可能只允许气体由总管进釜），拆釜前请先手动泄压。"
            : " 泄压未完成，釜内仍有测试压力，拆釜前请先泄压。";
    }

    // ============ 辅助 ============

    /// <summary>釜温 ℃（用于压力温度补偿）；温控离线或读数无效时返回 NaN（不补偿）。</summary>
    private double TempOf(int id)
        => _main.FindReactor(id) is { TOnline: true, T: > 1 } r ? r.T : double.NaN;

    private static string Rvs(IEnumerable<int> ids) => string.Join("、", ids.Select(i => "RV" + i));

    private static string Mmss(double sec)
    {
        int s = (int)Math.Max(0, Math.Round(sec));
        return $"{s / 60:00}:{s % 60:00}";
    }

    private void SetStage(int idx)
    {
        for (int i = 0; i < Stages.Count; i++)
            Stages[i].State = i < idx ? "done" : i == idx ? "cur" : "";
    }

    /// <summary>未开始时复位：按当前设置刷新说明 / 阶段，重建通道（运行中的通道不可选）。</summary>
    private void Reset()
    {
        Started = false; Done = false; Failed = false; LeakFailed = false; Progress = 0;
        PhaseText = ""; ResultTitle = ""; ResultText = "";

        var s = _main.Settings;
        double target = s.LeakTestPsi;
        SubTitle = $"Auto Leak Test · 惰性气体 {Units.FormatP(target)} · 阈值 < {s.LeakRate:0.#} psi/hr";
        IntroText =
            $"1.  开惰性气体，经各 RV 进气阀把所选通道脉冲充压到 {Units.FormatP(target)}（{target:0.#} psi），到压即关该路\n" +
            $"2.  关闭气源、排空总管，静置平衡 {StepDef.N(s.LeakSettleMin)} 分钟\n" +
            $"3.  连续 {StepDef.N(s.LeakMeasureMin)} 分钟记录压力，按温度补偿后拟合压降速率\n" +
            $"4.  自动判读：压降 ≥ {s.LeakRate:0.#} psi/hr 判为不通过；结束后尝试经排空阀泄压并核实";
        IntroNote =
            $"惰性气体减压阀请预先调到略高于测试压力（约 {Units.FormatP(target * 1.2)}），不要调高：釜压不会超过减压阀出口压力。" +
            "被测釜需已装好密封、温度稳定（建议 ≤ 40 ℃）。" +
            (_main.GasBlockReason(needPressure: true) is { } why ? $" 当前无法开始：{why}。" : "");

        Stages.Clear();
        Stages.Add(new LeakStage { Name = $"1. 充压至 {Units.FormatP(target)}" });
        Stages.Add(new LeakStage { Name = $"2. 平衡 {StepDef.N(s.LeakSettleMin)} 分钟" });
        Stages.Add(new LeakStage { Name = $"3. 测量 {StepDef.N(s.LeakMeasureMin)} 分钟" });
        Stages.Add(new LeakStage { Name = "4. 判读 · 泄压" });

        Cells.Clear();
        foreach (var r in _main.Reactors.Take(8))
        {
            bool canPick = !r.IsRunning;
            Cells.Add(new LeakCell
            {
                Id = r.Id,
                CanPick = canPick,
                Selected = canPick,
                Pressure = r.P,
                Detail = canPick ? "待测" : "运行中",
            });
        }
    }

    private void BuildDiag()
    {
        DiagSteps.Add(new DiagStep(1, "面密封 O 圈（最常见）", "拆下搅拌组件，检查该 RV 的面密封 O 圈是否压扁、有切口或残留物，必要时更换。"));
        DiagSteps.Add(new DiagStep(2, "注射口密封", "在注射口涂皂水，看是否冒泡。冒泡需清洗或更换注射口 O 圈。"));
        DiagSteps.Add(new DiagStep(3, "沟槽密封 O 圈", "面密封 O 圈更换后仍不通过时再考虑。"));
        DiagSteps.Add(new DiagStep(4, "夹具螺栓", "是否按对角顺序均匀拧紧？过松会漏，过紧会损坏螺纹。"));
        DiagSteps.Add(new DiagStep(5, "进气接头", "检查该 RV 进气管路接头是否松动，可涂皂水检漏。"));
    }
}

/// <summary>泄漏测试里单路 RV 的实时单元（开始前点击切换是否参与测试）。</summary>
public partial class LeakCell : ObservableObject
{
    public int Id { get; init; }
    public bool CanPick { get; init; }                   // 运行中的通道不可选
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private bool _locked;           // 测试开始后锁定选择
    [ObservableProperty] private double _pressure;       // psi
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";    // "" | ok | bad | skip

    public string RvName => $"RV{Id}";
    public string PressureText => Units.PValue(Pressure);
    public string UnitText => " " + Units.PLabel;
    public double CellOpacity => Selected ? 1.0 : 0.4;

    partial void OnPressureChanged(double value) => OnPropertyChanged(nameof(PressureText));
    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(CellOpacity));
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(CellBg));
        OnPropertyChanged(nameof(CellBorder));
        OnPropertyChanged(nameof(DetailBrush));
    }

    [RelayCommand]
    private void Toggle()
    {
        if (Locked || !CanPick) return;
        Selected = !Selected;
        Detail = Selected ? "待测" : "不测";
    }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public IBrush CellBg => Status == "bad" ? B("#12E0394C") : B("#FFF1F2F4");
    public IBrush CellBorder => Status == "ok" ? B("#5984CC16") : Status == "bad" ? B("#6BE0394C") : B("#14000000");
    public IBrush DetailBrush => Status == "ok" ? B("#5fae14") : Status == "bad" ? B("#e0394c") : B("#54545e");
}

/// <summary>泄漏测试阶段指示。</summary>
public partial class LeakStage : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _state = "";   // "" | cur | done

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(Weight));
    }

    public IBrush Brush => State == "cur" ? new SolidColorBrush(Color.Parse("#5fae14"))
        : State == "done" ? new SolidColorBrush(Color.Parse("#7d8694"))
        : new SolidColorBrush(Color.Parse("#66666f"));
    public FontWeight Weight => State == "cur" ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>维修指南一步。</summary>
public class DiagStep
{
    public int No { get; }
    public string Lead { get; }
    public string Body { get; }
    public string NoText => No.ToString();
    public DiagStep(int no, string lead, string body) { No = no; Lead = lead; Body = body; }
}
