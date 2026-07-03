using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>一键自动泄漏测试：说明 → 动画进度 → 自动判读 → 维修指南。</summary>
public partial class LeakViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    // 每路泄漏率 psi/hr（演示）：RV7 超标，RV8 通常为停用通道
    private static readonly double[] Rates = { 0.6, 0.4, 0.8, 1.2, 0.5, 0.7, 5.2, 0 };
    private DispatcherTimer? _timer;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isDiagOpen;
    [ObservableProperty] private bool _started;     // 已点「开始测试」
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private double _progress;  // 0–100
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultText = "";

    public ObservableCollection<LeakCell> Cells { get; } = new();
    public ObservableCollection<LeakStage> Stages { get; } = new();
    public List<DiagStep> DiagSteps { get; } = new();

    public bool ShowStartBtn => !Started;
    public bool ShowDiagBtn => Done && Failed;

    public IBrush ResultBrush => Failed ? B("#e0394c") : B("#a3e635");
    public IBrush ResultBg => Failed ? B("#14E0394C") : B("#14A3E635");
    public IBrush ResultBorder => Failed ? B("#66E0394C") : B("#66A3E635");

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public LeakViewModel(MainViewModel main)
    {
        _main = main;
        BuildStages();
        BuildDiag();
    }

    partial void OnStartedChanged(bool value) => OnPropertyChanged(nameof(ShowStartBtn));
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(ShowDiagBtn));
    partial void OnFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDiagBtn));
        OnPropertyChanged(nameof(ResultBrush));
        OnPropertyChanged(nameof(ResultBg));
        OnPropertyChanged(nameof(ResultBorder));
    }

    [RelayCommand]
    private void Open()
    {
        Stop();
        Started = false; Running = false; Done = false; Failed = false; Progress = 0;
        PhaseText = "";
        BuildCells();
        foreach (var s in Stages) s.State = "";
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        Stop();
        Running = false;
        IsOpen = false;
    }

    [RelayCommand]
    private void Start()
    {
        Started = true; Done = false; Failed = false; Progress = 0; Running = true;
        Tick();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    [RelayCommand]
    private void ShowDiag()
    {
        IsOpen = false;
        IsDiagOpen = true;
    }

    [RelayCommand]
    private void CloseDiag() => IsDiagOpen = false;

    [RelayCommand]
    private void DiagAction(string what) => _main.Toast("ok", $"（演示）{what}");

    private void Tick()
    {
        Progress = Math.Min(100, Progress + 2.5);
        int phase = Progress < 25 ? 0 : Progress < 50 ? 1 : Progress < 100 ? 2 : 3;

        for (int i = 0; i < Stages.Count; i++)
            Stages[i].State = i < phase ? "done" : i == phase ? "cur" : "";

        foreach (var c in Cells)
        {
            if (c.Disabled) { c.Detail = "已停用"; continue; }
            double elapsed = phase >= 2 ? Math.Min(0.5, (Progress - 50) / 50 * 0.5) : 0;
            c.Pressure = 510 - c.Rate * elapsed;
            if (phase < 3)
            {
                c.Status = "";
                c.Detail = phase >= 2 ? $"-{c.Rate * elapsed:0.00} psi" : "…";
            }
            else
            {
                c.Status = c.Rate < _main.Settings.LeakRate ? "ok" : "bad";
                c.Detail = $"{c.Rate:0.0} psi/hr";
            }
        }

        PhaseText = phase < 3
            ? $"进行中 · 剩余约 {40 - (int)Math.Round(40 * Progress / 100)} 分钟（演示加速）"
            : "测试结束";

        if (Progress >= 100)
        {
            Stop();
            Running = false;
            Done = true;
            Finish();
        }
    }

    private void Finish()
    {
        double thr = _main.Settings.LeakRate;
        var tested = Cells.Where(c => !c.Disabled).ToList();
        var failed = tested.Where(c => c.Rate >= thr).ToList();
        int passN = tested.Count(c => c.Rate < thr);
        Failed = failed.Count > 0;
        ResultTitle = Failed ? "✕ 测试不通过" : "✓ 测试通过";
        ResultText = Failed
            ? $"{passN}/{tested.Count} 通道通过；RV{string.Join("、RV", failed.Select(c => c.Id))} 压降超标。常见原因：面密封 O 圈、注射口检验阀 O 圈、或夹具未拧紧。"
            : $"全部 {passN} 个测试通道压降均 < {thr:0.#} psi/hr，系统密封状态良好，可放心运行实验。";
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void BuildCells()
    {
        Cells.Clear();
        var reactors = _main.Reactors;
        for (int i = 0; i < 8; i++)
        {
            bool disabled = i < reactors.Count && reactors[i].State == ReactorState.Idle;
            Cells.Add(new LeakCell
            {
                Id = i + 1,
                Disabled = disabled,
                Rate = Rates[i],
                Pressure = disabled ? 0 : 510,
                Detail = disabled ? "已停用" : "…",
            });
        }
    }

    private void BuildStages()
    {
        Stages.Add(new LeakStage { Name = "1. 充压 N₂ 至 510 psi" });
        Stages.Add(new LeakStage { Name = "2. 平衡 (10 分钟)" });
        Stages.Add(new LeakStage { Name = "3. 测量压降 (30 分钟)" });
        Stages.Add(new LeakStage { Name = "4. 已完成 · 自动判读" });
    }

    private void BuildDiag()
    {
        DiagSteps.Add(new DiagStep(1, "面密封 O 圈（最常见，约 70%）", "拆下搅拌组件，检查目标 RV 的面密封 O 圈是否压扁、有切口或残留物。压扁的 O 圈可放入沸水浸泡 10 分钟恢复弹性。"));
        DiagSteps.Add(new DiagStep(2, "注射口单向阀", "用滴管在注射口注入皂水，看是否冒泡。如冒泡需清洗或更换注射口 O 圈（套件 P/N 900810）。"));
        DiagSteps.Add(new DiagStep(3, "沟槽密封 O 圈", "面密封 O 圈更换后仍不通过时再考虑（约 15%）。"));
        DiagSteps.Add(new DiagStep(4, "夹具螺栓", "是否按对角顺序均匀拧紧？过松会漏，过紧会损坏螺纹。"));
        DiagSteps.Add(new DiagStep(5, "压缩接头", "进气管路接头是否松动。"));
    }
}

/// <summary>泄漏测试里单路 RV 的实时单元。</summary>
public partial class LeakCell : ObservableObject
{
    public int Id { get; init; }
    public bool Disabled { get; init; }
    public double Rate { get; init; }

    [ObservableProperty] private double _pressure;
    [ObservableProperty] private string _detail = "…";
    [ObservableProperty] private string _status = "";   // "" | ok | bad

    public string RvName => $"RV{Id}";
    public string PressureText => Disabled ? "—" : Pressure.ToString("0.0");
    public double CellOpacity => Disabled ? 0.4 : 1.0;

    partial void OnPressureChanged(double value) => OnPropertyChanged(nameof(PressureText));
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(CellBg));
        OnPropertyChanged(nameof(CellBorder));
        OnPropertyChanged(nameof(DetailBrush));
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
