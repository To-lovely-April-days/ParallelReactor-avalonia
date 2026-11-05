using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ParallelReactor.ViewModels;

/// <summary>报警与事件界面（ALARM Tab）：列表 + 过滤 + 统计侧栏。</summary>
public partial class AlarmViewModel : ViewModelBase
{
    public ObservableCollection<AlarmItem> All { get; } = new();
    public ObservableCollection<AlarmItem> Filtered { get; } = new();

    [ObservableProperty] private string _filter = "all";

    public int Total => All.Count;
    public int ActiveCount => All.Count(a => a.Stat == "active");

    /// <summary>开机为空：只记录本次运行期间真实发生的报警与事件（尚未落盘，重启后清空）。</summary>
    public AlarmViewModel() => ApplyFilter();

    public bool IsEmpty => Filtered.Count == 0;

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand] private void SetFilter(string f) => Filter = f;

    /// <summary>运行时追加一条事件（置顶，"未处理"状态），并刷新过滤列表与统计。</summary>
    public void Log(string kind, string rv, string msg, string sub)
    {
        All.Insert(0, new AlarmItem
        {
            K = kind, Rv = rv, Msg = msg, Sub = sub,
            Time = System.DateTime.Now.ToString("HH:mm:ss"),
            Stat = kind == "info" ? "done" : "active",
        });
        ApplyFilter();
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(ActiveCount));
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var a in All.Where(Match)) Filtered.Add(a);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Match(AlarmItem a) => Filter switch
    {
        "all" => true,
        "active" => a.Stat == "active",
        _ => a.K == Filter,
    };

}

/// <summary>一条报警/事件记录。</summary>
public class AlarmItem
{
    public string K { get; set; } = "info";
    public string Rv { get; set; } = "";
    public string Msg { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Time { get; set; } = "";
    public string Stat { get; set; } = "done";

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public bool HasSub => !string.IsNullOrEmpty(Sub);

    public IBrush BadgeBg => K switch { "err" => B("#2EE0394C"), "warn" => B("#2EF0A830"), _ => B("#2E5B96E4") };
    public IBrush BadgeFg => K switch { "err" => B("#e0394c"), "warn" => B("#f0a830"), _ => B("#7eb6ee") };

    public Geometry IconData => Geometry.Parse(K switch
    {
        "err" => "M18 6L6 18M6 6l12 12",
        "warn" => "M12 8v5 M12 17h.01",
        _ => "M12 8v5 M12 17h.01 M21 12a9 9 0 1 1-18 0 9 9 0 1 1 18 0",
    });

    public string StatusText => Stat switch { "active" => "未处理", "ack" => "已确认", _ => "已完成" };
    public IBrush StatusBrush => Stat switch { "active" => B("#e0394c"), "ack" => B("#c9820f"), _ => B("#7d8694") };
}
