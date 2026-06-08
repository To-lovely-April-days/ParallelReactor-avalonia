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

    public AlarmViewModel()
    {
        Seed();
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand] private void SetFilter(string f) => Filter = f;

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var a in All.Where(Match)) Filtered.Add(a);
    }

    private bool Match(AlarmItem a) => Filter switch
    {
        "all" => true,
        "active" => a.Stat == "active",
        _ => a.K == Filter,
    };

    private void Seed()
    {
        void A(string k, string rv, string msg, string sub, string t, string stat)
            => All.Add(new AlarmItem { K = k, Rv = rv, Msg = msg, Sub = sub, Time = t, Stat = stat });

        A("err", "RV5", "压力超过 510 psi 阈值", "测得 511.8 psi，触发硬报警，需人工确认", "14:32:45", "active");
        A("warn", "RV4", "升温 14 分钟仍未达到 SP", "当前 117.4°C / SP 150°C，建议检查传热液", "14:30:12", "active");
        A("info", "系统", "已开始数据记录", "run_20260602_1018.ear", "14:18:33", "done");
        A("warn", "RV3", "压力初值偏离 SP 较多", "升压完成 8 分钟仍偏离 5 psi 以上", "14:22:01", "ack");
        A("info", "系统", "8 路 RV 全部达到 SP 温度", "最大偏差 1.8°C，合格", "14:21:58", "done");
        A("info", "RV2", "检测到注射事件", "压降 -2.3 psi，已自动重置气体计数", "14:20:12", "done");
        A("warn", "系统", "泄漏测试已 6 天未运行", "上次：2026-05-27", "14:18:00", "ack");
        A("info", "系统", "温控、IO、驱动模块全部连接", "3 路 RS485 在线 · 错误 0", "14:17:30", "done");
        A("err", "RV5", "超压报警（前一次运行）", "2026-05-30 11:45 · 已确认", "05-30 11:45", "done");
        A("warn", "RV3", "CO 羰基化 - RV3 升温慢", "2026-06-01 09:14", "06-01 09:14", "done");
        A("info", "系统", "气体消耗计数已重置", "操作人：admin", "06-01 09:05", "done");
        A("info", "系统", "软件版本已升级到 v2.4.1", "增加 P&ID 视图与一键泄漏测试", "05-30 18:00", "done");
    }
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
    public IBrush StatusBrush => Stat switch { "active" => B("#e0394c"), "ack" => B("#f0a830"), _ => B("#a4a4b0") };
}
