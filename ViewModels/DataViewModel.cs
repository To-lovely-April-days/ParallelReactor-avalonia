using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ParallelReactor.ViewModels;

/// <summary>运行记录界面（DATA Tab）：历史运行列表 + 选中详情。</summary>
public partial class DataViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ObservableCollection<RunRecord> Runs { get; } = new();
    [ObservableProperty] private RunRecord? _selected;

    // 详情区汇总曲线（逻辑斯蒂 S 曲线）
    public List<Point> SparkLine { get; private set; } = new();
    public List<Point> SparkArea { get; private set; } = new();
    [ObservableProperty] private IBrush _sparkColor = new SolidColorBrush(Color.Parse("#a3e635"));

    public bool HasSelection => Selected != null;

    public DataViewModel(MainViewModel main)
    {
        _main = main;
        Seed();
        Selected = Runs.Count > 0 ? Runs[0] : null;
    }

    partial void OnSelectedChanged(RunRecord? value)
    {
        BuildSpark(value);
        OnPropertyChanged(nameof(SparkLine));
        OnPropertyChanged(nameof(SparkArea));
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand] private void ViewCurve() => _main.SwitchTab("graph");
    [RelayCommand] private void ExportEar() => _main.Toast("ok", $"（演示）已导出 {Selected?.Id}.ear");
    [RelayCommand] private void ToTemplate() => _main.Toast("ok", "（演示）已转为程序模板");
    [RelayCommand] private void CopyParams() => _main.Toast("ok", "（演示）已复制参数到当前运行");

    private void BuildSpark(RunRecord? r)
    {
        const double W = 320, H = 72; const int N = 40;
        var line = new List<Point>(N);
        for (int i = 0; i < N; i++)
        {
            double x = i / (double)(N - 1) * W;
            double s = 1.0 / (1 + Math.Exp(-((i - 15) / 3.5)));
            double y = H - 8 - (H - 16) * s;
            line.Add(new Point(x, y));
        }
        SparkLine = line;
        SparkArea = new List<Point>(line) { new Point(W, H - 8), new Point(0, H - 8) };
        SparkColor = new SolidColorBrush(Color.Parse(r?.Status == "err" ? "#e0394c" : "#a3e635"));
    }

    private void Seed()
    {
        Runs.Add(new RunRecord { Id = "run_20260602_1018", Name = "加氢-苯甲酸酯 #4", Recipe = "加氢 · Pd/C", Start = "2026-06-02 10:18", Dur = "2h 14m", Rvs = "1-7", Rsd = "4.7%", Status = "ok", Mmol = 14.2, T = 150, P = 325 });
        Runs.Add(new RunRecord { Id = "run_20260601_1532", Name = "烯烃聚合 第二轮", Recipe = "烯烃聚合", Start = "2026-06-01 15:32", Dur = "1h 28m", Rvs = "1-6", Rsd = "8.2%", Status = "ok", Mmol = 11.5, T = 80, P = 340 });
        Runs.Add(new RunRecord { Id = "run_20260601_0902", Name = "CO 羰基化测试", Recipe = "CO 羰基化", Start = "2026-06-01 09:02", Dur = "6h 03m", Rvs = "1-4", Rsd = "12.1%", Status = "warn", Mmol = 8.7, T = 120, P = 148, Note = "RV3 升温慢" });
        Runs.Add(new RunRecord { Id = "run_20260531_1411", Name = "加氢-苯甲酸酯 #3", Recipe = "加氢 · Pd/C", Start = "2026-05-31 14:11", Dur = "2h 11m", Rvs = "1-8", Rsd = "5.3%", Status = "ok", Mmol = 13.9, T = 150, P = 322 });
        Runs.Add(new RunRecord { Id = "run_20260531_0830", Name = "空白泄漏测试", Recipe = "空白泄漏测试", Start = "2026-05-31 08:30", Dur = "0h 41m", Rvs = "1-8", Rsd = "—", Status = "ok", Mmol = 0, T = 25, P = 498 });
        Runs.Add(new RunRecord { Id = "run_20260530_1605", Name = "Rh 均相筛选", Recipe = "加氢 · Rh 均相", Start = "2026-05-30 16:05", Dur = "4h 02m", Rvs = "1-7", Rsd = "6.8%", Status = "ok", Mmol = 9.4, T = 60, P = 200 });
        Runs.Add(new RunRecord { Id = "run_20260530_1011", Name = "加氢-苯甲酸酯 #2", Recipe = "加氢 · Pd/C", Start = "2026-05-30 10:11", Dur = "1h 35m", Rvs = "1-7", Rsd = "—", Status = "err", Mmol = 5.6, T = 150, P = 512, Note = "RV5 超压急停" });
        Runs.Add(new RunRecord { Id = "run_20260529_1320", Name = "加氢-苯甲酸酯 #1", Recipe = "加氢 · Pd/C", Start = "2026-05-29 13:20", Dur = "2h 16m", Rvs = "1-8", Rsd = "7.1%", Status = "ok", Mmol = 14.0, T = 150, P = 325 });
    }
}

/// <summary>一条历史运行记录。</summary>
public class RunRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Recipe { get; set; } = "";
    public string Start { get; set; } = "";
    public string Dur { get; set; } = "";
    public string Rvs { get; set; } = "";
    public string Rsd { get; set; } = "";
    public string Status { get; set; } = "ok";
    public string Note { get; set; } = "";
    public double Mmol { get; set; }
    public int T { get; set; }
    public int P { get; set; }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public bool HasNote => !string.IsNullOrEmpty(Note);
    public string FileMeta => $"{Id}.ear · {Start} · {Dur}";
    public string MmolText => Mmol.ToString("0.#");
    public string TText => T.ToString();
    public string PText => P.ToString();
    public string NoteFull => $"备注：{Note}";

    public IBrush StatusBrush => Status switch { "warn" => B("#f0a830"), "err" => B("#e0394c"), _ => B("#a3e635") };
    public IBrush RsdBrush => StatusBrush;
    public string StatusText => Status switch { "ok" => "✓ 完成", "warn" => "⚠ 警告", _ => "✕ 中止" };
    public IBrush NoteBg => Status == "err" ? B("#12e0394c") : B("#12f0a830");
    public IBrush NoteBar => Status == "err" ? B("#e0394c") : B("#f0a830");
    public IBrush NoteText => Status == "err" ? B("#ffc9ce") : B("#ffe4b8");
    public string NoteTag => Status == "err" ? "中止原因" : Status == "warn" ? "警告" : "备注";
    public Geometry NoteIcon => Geometry.Parse(Status == "err"
        ? "M7.86 2h8.28L22 7.86v8.28L16.14 22H7.86L2 16.14V7.86L7.86 2z M12 8v4 M12 16h.01"
        : "M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z M12 9v4 M12 17h.01");
}
