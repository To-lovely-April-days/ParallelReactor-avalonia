using ParallelReactor.Commands;
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

    /// <summary>运行记录。开机为空：运行记录随运行框架（下一轮）生成并落盘，此前不显示任何示例数据。</summary>
    public ObservableCollection<RunRecord> Runs { get; } = new();
    [ObservableProperty] private RunRecord? _selected;

    // 详情区汇总曲线：按记录里的实测数据绘制（暂无记录时为空）
    public List<Point> SparkLine { get; private set; } = new();
    public List<Point> SparkArea { get; private set; } = new();
    [ObservableProperty] private IBrush _sparkColor = new SolidColorBrush(Color.Parse("#5fae14"));

    public bool HasSelection => Selected != null;
    public bool IsEmpty => Runs.Count == 0;

    public DataViewModel(MainViewModel main)
    {
        _main = main;
        Runs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(RunRecord? value)
    {
        SparkColor = new SolidColorBrush(Color.Parse(value?.Status == "err" ? "#e0394c" : "#5fae14"));
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand, ViewEntry("切到曲线页")] private void ViewCurve() => _main.SwitchTab("graph");
    [RelayCommand, ViewEntry("未实现，只提示")] private void ExportEar() => _main.Toast("warn", "导出 .ear 尚未实现");
    [RelayCommand, ViewEntry("未实现，只提示")] private void ToTemplate() => _main.Toast("warn", "转为程序模板尚未实现");
    [RelayCommand, ViewEntry("未实现，只提示")] private void CopyParams() => _main.Toast("warn", "复制参数到当前运行尚未实现");
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
    public string PText => Models.Units.PValue(P);   // 按全局压力单位换算
    public string PUnit => Models.Units.PLabel;
    public string NoteFull => $"备注：{Note}";

    public IBrush StatusBrush => Status switch { "warn" => B("#c9820f"), "err" => B("#e0394c"), _ => B("#5fae14") };
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
