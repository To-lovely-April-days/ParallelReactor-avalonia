using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 吸气量用的容积标定（气体膨胀法，计算见 <see cref="VolumeCalib"/>）：
/// 选一路空釜（常温、密封、接近常压）→ 重复 N 次「只开惰性气源给总管充气 → 关气源 → 只开该路 SV 放进釜里、等压力稳定读数」→ 拟合比值。
/// 第一次（或选了「重新标定总管体积」）要做两遍：空釜一遍、按提示加入已知体积的水后再做一遍，同时得出总管体积和该路有效容积；
/// 总管体积已知后，其他通道空釜做一遍即可。结果写入设置（<see cref="AppSettings.EffectiveVolume"/>），吸气量计算自动改用它。
/// 开始 / 中止经命令层（OperatorCommands.Calib，只允许本地：要在设备旁开釜、加水）；面板的格子只用于显示与选择。
/// 安全：釜压不会超过惰性气体减压阀出口压力；每步读压后按超压报警值的 90% 再查一次，气路序列期间的超压联锁照常生效。
/// </summary>
public partial class CalibViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private const double ChargeSec = 2;       // 总管充气时长（只开气源，全部 SV 关）
    private const double EqualizeSec = 20;    // SV 打开后等压力与温度稳定的时长
    private const double MaxStartRatio = 0.5; // 初始釜压须低于上限的这个比例
    private const double MaxTempDrift = 1.5;  // 一遍里釜温最多变化 ℃（等温假设）

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _awaitWater;       // 第一遍做完，等操作员加水后做第二遍
    [ObservableProperty] private bool _redoManifold;     // 总管体积已知时也重新做两遍
    [ObservableProperty] private int _channel;
    [ObservableProperty] private double _waterMl = 5;     // 10 mL 釜：加 5 mL 水
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _failed;

    public ObservableCollection<CalibCell> Cells { get; } = new();

    /// <summary>第一遍（空釜）的结果：通道与比值，等第二遍用。</summary>
    private (int Ch, double Ratio)? _firstPass;

    public CalibViewModel(MainViewModel main)
    {
        _main = main;
        for (int i = 1; i <= 8; i++) Cells.Add(new CalibCell(this, i));
    }

    public bool ManifoldKnown => _main.Settings.ManifoldVolume is > 0;
    /// <summary>这一次要做两遍（空釜 + 加水）。</summary>
    public bool TwoRuns => !ManifoldKnown || RedoManifold;
    public bool ShowRedoToggle => ManifoldKnown && !Running && !AwaitWater;
    public bool ShowStart => !Running && !AwaitWater;
    public bool ShowContinue => !Running && AwaitWater;
    public string CloseText => Running ? "后台运行" : "关闭";
    public string WaterText => $"{WaterMl:0.##} mL";
    public string ModeText => TwoRuns
        ? (ManifoldKnown ? "重新标定总管体积：空釜做一遍，加水后再做一遍（同时得出总管体积和这一路的有效容积）"
                         : "第一次标定：空釜做一遍，加水后再做一遍（同时得出总管体积和这一路的有效容积）。之后其他通道空釜做一遍即可")
        : $"总管体积已知（{_main.Settings.ManifoldVolume:0.##} mL）：空釜做一遍即可";
    public string IntroText =>
        "① 先做泄漏测试，确认密封良好。\n" +
        "② 所选通道：釜内清空、擦干，密封；釜盖打开过一次让釜内为常压；常温，不加热、不搅拌。\n" +
        "③ 惰性气体减压阀调到 50–100 psi（0.35–0.7 MPa）左右，与泄漏测试相同即可。\n" +
        $"④ 系统自动做最多 {VolumeCalib.Steps} 次「总管充气 → 放进釜里 → 等稳定读数」（压力接近减压阀压力后提前结束），每遍约 {VolumeCalib.Steps * (ChargeSec + EqualizeSec + 2) / 60:0.#} 分钟。";
    public string WaterHint => $"第一遍完成。请：① 打开该路排空或釜盖，把釜内泄到常压；② 用移液管准确加入 {WaterMl:0.##} mL 水（也可以用已知体积的金属块）；③ 重新密封，然后点「继续第二遍」。";

    public IBrush ResultBrush => Failed ? B("#e0394c") : B("#5fae14");
    public IBrush ResultBg => Failed ? B("#14E0394C") : B("#14A3E635");
    public IBrush ResultBorder => Failed ? B("#66E0394C") : B("#66A3E635");
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    partial void OnRunningChanged(bool value) => RaiseButtons();
    partial void OnAwaitWaterChanged(bool value) => RaiseButtons();
    partial void OnRedoManifoldChanged(bool value) => RaiseButtons();
    partial void OnWaterMlChanged(double value) { OnPropertyChanged(nameof(WaterText)); OnPropertyChanged(nameof(WaterHint)); }
    partial void OnFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(ResultBrush));
        OnPropertyChanged(nameof(ResultBg));
        OnPropertyChanged(nameof(ResultBorder));
    }
    partial void OnChannelChanged(int value) { foreach (var c in Cells) c.Refresh(); }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowContinue));
        OnPropertyChanged(nameof(ShowRedoToggle));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(TwoRuns));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ManifoldKnown));
        foreach (var c in Cells) c.Refresh();
    }

    /// <summary>第一遍做完、等待加水的通道（命令层校验「继续第二遍」用）。</summary>
    internal int? FirstPassChannel => _firstPass?.Ch;

    // ============ 面板操作（只改面板状态）============

    [RelayCommand]
    [ViewEntry("打开容积标定面板（标定在后台进行时只是查看进度）")]
    private void Open()
    {
        if (!Running && !AwaitWater)
        {
            HasResult = false;
            Progress = 0;
            PhaseText = "";
            if (Channel == 0 || _main.FindReactor(Channel) is { IsRunning: true })
                Channel = _main.Reactors.FirstOrDefault(r => !r.IsRunning)?.Id ?? 0;
        }
        RaiseButtons();
        IsOpen = true;
    }

    [RelayCommand]
    [ViewEntry("关闭面板（标定在后台继续）")]
    private void Close()
    {
        IsOpen = false;
        if (Running) _main.Toast("ok", "容积标定在后台继续 · 点工具栏「容积标定」查看进度");
    }

    [RelayCommand]
    [ViewEntry("切换「重新标定总管体积」（只改面板选择）")]
    private void ToggleRedo() { if (!Running && !AwaitWater) RedoManifold = !RedoManifold; }

    [RelayCommand]
    [ViewEntry("放弃第一遍的结果（只改面板状态）")]
    private void DiscardFirst()
    {
        if (Running) return;
        _firstPass = null;
        AwaitWater = false;
        HasResult = false;
        PhaseText = "";
    }

    [RelayCommand]
    [ViewEntry("打开键盘改加水体积（只改面板的输入值）")]
    private void EditWater()
    {
        if (Running) return;
        _main.Keyboard.OpenNumeric("加水体积", WaterMl, "mL", 0.5, Math.Max(1, Math.Min(_main.Settings.VolMax, _main.Settings.VesselVolume * 0.9)), v =>
        {
            WaterMl = Math.Round(v, 2);
            return OpResult.Silent.AsTask();
        });
    }

    internal void Pick(int id)
    {
        if (Running || AwaitWater) return;
        if (_main.FindReactor(id) is { IsRunning: true }) return;
        Channel = id;
    }

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStartVolumeCalib))]
    private void Start() => _main.Local(_main.Ops.TryStartVolumeCalib(MainViewModel.Src, Channel, withWater: false, WaterMl));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStartVolumeCalib))]
    private void Continue() => _main.Local(_main.Ops.TryStartVolumeCalib(MainViewModel.Src, _firstPass?.Ch ?? Channel, withWater: true, WaterMl));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryAbortVolumeCalib))]
    private void Abort() => _main.Local(_main.Ops.TryAbortVolumeCalib(MainViewModel.Src));

    // ============ 标定流程（命令层守卫通过后调用）============

    /// <summary>做一遍：withWater = false 是空釜那一遍，true 是加水后的第二遍（waterMl = 加入的水 mL）。第一个 await 之前就占用气路。</summary>
    internal async Task RunAsync(int ch, bool withWater, double waterMl)
    {
        Channel = ch;
        Running = true;
        HasResult = false;
        Failed = false;
        Progress = 0;
        var c = _main.FindReactor(ch)!;
        var ct = _main.BeginGas(GasOwner.Calib, $"容积标定 RV{ch}", new[] { ch }, new[] { ch });
        string pass = withWater ? $"第二遍（加水 {waterMl:0.##} mL）" : "空釜";
        _main.Logger.AddMarker($"容积标定开始 RV{ch} {pass}");
        _main.Alarm.Log("info", "系统", $"RV{ch} 容积标定开始", $"{pass} · 惰性气体 · {VolumeCalib.Steps} 次充放");

        var steps = new List<(double Before, double After)>();
        string? fail = null;
        bool aborted = false;
        double t0 = c.T;
        try
        {
            double cap = _main.Settings.OverPressure * SettingSpecs.LeakTestMaxRatio;
            PhaseText = "关闭全部阀门，读取初始压力…";
            await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
            await _main.HoldAsync(ValveFrame.AllClosed(), PurgePlan.DeadSec, ct);
            double p = (await _main.ReadPressuresAsync())[ch - 1];
            if (p > cap * MaxStartRatio)
                throw new GasFaultException($"RV{ch} 初始压力 {Units.FormatP(p)} 偏高 · 请先把釜泄到常压");
            t0 = c.T;

            var sv = ValveFrame.AllClosed();
            sv[ch - 1] = true;
            for (int k = 1; k <= VolumeCalib.Steps; k++)
            {
                PhaseText = $"第 {k}/{VolumeCalib.Steps} 次 · 总管充气";
                await _main.ApplyValvesAsync(ValveFrame.Gas(0), ct);           // 只开惰性气源：全部 SV 关
                await _main.HoldAsync(ValveFrame.Gas(0), ChargeSec, ct);
                await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
                await _main.HoldAsync(ValveFrame.AllClosed(), PurgePlan.DeadSec, ct);

                PhaseText = $"第 {k}/{VolumeCalib.Steps} 次 · 放进 RV{ch}，等压力稳定";
                await _main.ApplyValvesAsync(sv, ct);                           // 只开该路 SV：总管里的气进釜
                await _main.HoldAsync(sv, EqualizeSec, ct);
                double after = (await _main.ReadPressuresAsync())[ch - 1];
                await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
                await _main.HoldAsync(ValveFrame.AllClosed(), PurgePlan.DeadSec, ct);
                _main.ThrowIfGasUnsafe();

                steps.Add((p, after));
                _main.Alarm.Log("info", "系统", $"RV{ch} 容积标定 · 第 {k} 次", $"{Units.FormatP(p)} → {Units.FormatP(after)} · 釜温 {c.TText} °C");
                if (after >= cap)
                    throw new GasFaultException($"压力已到 {Units.FormatP(after)}，接近超压上限 · 请把惰性气体减压阀调低到 100 psi 左右");
                double rise = after - p;
                p = after;
                Progress = 100.0 * k / VolumeCalib.Steps;
                // 已接近减压阀压力（再充也升不了多少）且有效数据够了：提前结束（10 mL 小釜常见）
                if (rise < VolumeCalib.MinRisePsi && steps.Count(s => s.After - s.Before >= VolumeCalib.MinRisePsi) >= VolumeCalib.MinSteps)
                { Progress = 100; break; }
            }
            if (c.TOnline && double.IsFinite(t0) && Math.Abs(c.T - t0) > MaxTempDrift)
                throw new GasFaultException($"釜温变化 {Math.Abs(c.T - t0):0.0} °C（> {MaxTempDrift} °C）· 请在常温、不加热时标定");
        }
        catch (OperationCanceledException) { aborted = true; }
        catch (GasFaultException ex) { fail = ex.Message; }
        catch (Exception ex) { fail = $"执行异常：{ex.Message}"; }
        finally
        {
            await _main.CloseAllValvesAsync();
            _main.EndGas(GasOwner.Calib);
            Running = false;
            _main.Logger.AddMarker($"容积标定结束 RV{ch}");
        }
        Finish(c, withWater, waterMl, steps, aborted, fail);
    }

    private void Finish(Reactor c, bool withWater, double waterMl, List<(double Before, double After)> steps, bool aborted, string? fail)
    {
        int ch = c.Id;
        HasResult = true;
        string residual = steps.Count > 0 ? $" 釜内现有约 {Units.FormatP(steps[^1].After)} 惰性气体，开釜前请先泄压。" : "";
        if (aborted || fail != null)
        {
            Fail(aborted ? "标定已中止" : "标定失败", (aborted ? "已按要求中止，阀门已全关。" : $"{fail}。阀门已全关。") + residual);
            _main.Alarm.Log(aborted ? "warn" : "err", "系统", $"RV{ch} 容积标定{(aborted ? "中止" : "失败")}", fail ?? "手动中止");
            if (withWater) AwaitWater = true;   // 第二遍失败：保留第一遍，可以重做第二遍
            return;
        }

        var (fit, why) = VolumeCalib.FitSteps(steps);
        string pts = string.Join(" · ", steps.Select(s => $"{s.Before:0.00}→{s.After:0.00}"));
        if (fit == null)
        {
            Fail("标定失败", $"{why}。" + residual);
            _main.Alarm.Log("err", "系统", $"RV{ch} 容积标定失败", $"{why} · {pts} psi");
            if (withWater) AwaitWater = true;
            return;
        }
        string fitText = $"比值 {fit.Ratio:0.###} · R² {fit.R2:0.0000} · 减压阀约 {Units.FormatP(fit.SupplyPsi)}";

        if (!withWater && TwoRuns)
        {
            _firstPass = (ch, fit.Ratio);
            AwaitWater = true;
            Failed = false;
            ResultTitle = "第一遍完成（空釜）";
            ResultText = $"{fitText}。" + residual;
            PhaseText = "等待加水";
            _main.Alarm.Log("info", "系统", $"RV{ch} 容积标定 · 空釜完成", $"{fitText} · {pts} psi");
            _main.Toast("ok", $"RV{ch} 空釜一遍完成 · 请按提示加水后继续第二遍");
            return;
        }

        double veff;
        double? vm = null;
        if (withWater)
        {
            if (_firstPass is not { } first || first.Ch != ch) { Fail("标定失败", "找不到这一路的空釜结果 · 请重新开始"); AwaitWater = false; return; }
            var (m, e, why2) = VolumeCalib.SolveTwoRuns(first.Ratio, fit.Ratio, waterMl);
            if (e == null) { Fail("标定失败", $"{why2}。" + residual); AwaitWater = true; _main.Alarm.Log("err", "系统", $"RV{ch} 容积标定失败", why2!); return; }
            veff = e.Value;
            vm = m;
        }
        else
        {
            var (e, why1) = VolumeCalib.SolveOneRun(fit.Ratio, _main.Settings.ManifoldVolume ?? 0);
            if (e == null) { Fail("标定失败", $"{why1}。" + residual); _main.Alarm.Log("err", "系统", $"RV{ch} 容积标定失败", why1!); return; }
            veff = e.Value;
        }

        // 保存：设置里的有效容积 / 标定时间（/ 总管体积），吸气量计算立即改用
        var s = _main.Settings;
        if (s.EffectiveVolume is not { Length: >= 8 }) s.EffectiveVolume = Resize(s.EffectiveVolume);
        if (s.VolumeCalibUtc is not { Length: >= 8 }) s.VolumeCalibUtc = ResizeT(s.VolumeCalibUtc);
        double? old = s.EffectiveVolume[ch - 1];
        s.EffectiveVolume[ch - 1] = Math.Round(veff, 2);
        s.VolumeCalibUtc[ch - 1] = DateTime.UtcNow;
        if (vm is double mv) s.ManifoldVolume = Math.Round(mv, 3);
        _main.PersistSoon();
        _firstPass = null;
        AwaitWater = false;
        RedoManifold = false;
        Failed = false;
        ResultTitle = $"✓ RV{ch} 有效容积 {veff:0.0} mL";
        ResultText = $"{fitText}" + (vm is double v2 ? $" · 总管体积 {v2:0.00} mL" : "")
            + (old is double o ? $" · 原来 {o:0.0} mL" : "")
            + $"。吸气量计算已改用此值（气相体积 = 有效容积 − 溶液体积）。" + residual;
        PhaseText = "标定完成";
        _main.Alarm.Log("info", "系统", $"RV{ch} 容积标定完成",
            $"有效容积 {veff:0.00} mL" + (vm is double v3 ? $" · 总管 {v3:0.000} mL" : $" · 按总管 {s.ManifoldVolume:0.000} mL") + $" · {fitText} · {pts} psi");
        _main.Toast("ok", $"RV{ch} 容积标定完成 · {veff:0.0} mL");
        RaiseButtons();
    }

    private void Fail(string title, string text)
    {
        Failed = true;
        ResultTitle = title;
        ResultText = text;
        PhaseText = title;
    }

    private static double?[] Resize(double?[]? a) { var r = new double?[8]; if (a != null) Array.Copy(a, r, Math.Min(8, a.Length)); return r; }
    private static DateTime?[] ResizeT(DateTime?[]? a) { var r = new DateTime?[8]; if (a != null) Array.Copy(a, r, Math.Min(8, a.Length)); return r; }

    /// <summary>格子显示：该路当前的有效容积与标定日期。</summary>
    internal (string Volume, string Date) Info(int id)
    {
        var s = _main.Settings;
        double? v = s.EffectiveVolume is { } a && a.Length >= id ? a[id - 1] : null;
        DateTime? t = s.VolumeCalibUtc is { } b && b.Length >= id ? b[id - 1] : null;
        return (v is double x ? $"{x:0.0} mL" : "未标定", t is DateTime d ? d.ToLocalTime().ToString("MM-dd") : "");
    }

    internal bool CanPick(int id) => !Running && !AwaitWater && _main.FindReactor(id) is { IsRunning: false };
}

/// <summary>容积标定面板的一个通道格子。</summary>
public partial class CalibCell : ObservableObject
{
    private readonly CalibViewModel _vm;
    public int Id { get; }
    public string RvName => $"RV{Id}";
    public string VolumeText => _vm.Info(Id).Volume;
    public string DateText => _vm.Info(Id).Date;
    public bool Selected => _vm.Channel == Id;
    public double CellOpacity => Selected ? 1.0 : _vm.CanPick(Id) ? 0.55 : 0.3;

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public IBrush CellBg => Selected ? B("#1484CC16") : B("#FFF1F2F4");
    public IBrush CellBorder => Selected ? B("#995FAE14") : B("#14000000");

    public CalibCell(CalibViewModel vm, int id) { _vm = vm; Id = id; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CellOpacity));
        OnPropertyChanged(nameof(CellBg));
        OnPropertyChanged(nameof(CellBorder));
    }

    [RelayCommand]
    [ViewEntry("选择要标定的通道（只改面板选择）")]
    private void Pick() => _vm.Pick(Id);
}
