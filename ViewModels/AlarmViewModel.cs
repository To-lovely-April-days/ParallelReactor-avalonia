using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>报警与事件界面（ALARM Tab）：列表 + 过滤 + 统计侧栏。</summary>
public partial class AlarmViewModel : ViewModelBase
{
    public ObservableCollection<AlarmItem> All { get; } = new();
    public ObservableCollection<AlarmItem> Filtered { get; } = new();

    [ObservableProperty] private string _filter = "all";

    public int Total => All.Count;
    public int ActiveCount => All.Count(a => a.Stat == "active");

    private readonly Services.DataLogger? _logger;

    /// <summary>开机为空：界面列表只显示本次运行期间的报警与事件（重启后清空）；
    /// 每条同时追加到数据目录的 events-YYYYMMDD.csv（带日期与毫秒的完整时间戳），见 <see cref="Services.DataLogger"/>。</summary>
    public AlarmViewModel(Services.DataLogger? logger = null)
    {
        _logger = logger;
        ApplyFilter();
    }

    public bool IsEmpty => Filtered.Count == 0;

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    [ViewEntry("切换列表过滤（只改界面）")]
    private void SetFilter(string f) => Filter = f;

    /// <summary>主视图模型（构造完成后由 MainViewModel 赋值）：确认报警经它的命令层。</summary>
    internal MainViewModel? Main { get; set; }

    private long _seq;

    /// <summary>按 Id 找一条报警 / 事件（命令层 TryAckAlarm 用）。</summary>
    internal AlarmItem? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>运行时追加一条事件（置顶，"未处理"状态），并刷新过滤列表与统计。返回新条目。</summary>
    public AlarmItem Log(string kind, string rv, string msg, string sub)
    {
        var now = System.DateTimeOffset.Now;
        _logger?.Event(now, kind, rv, msg, sub);
        var item = new AlarmItem
        {
            Id = (++_seq).ToString(System.Globalization.CultureInfo.InvariantCulture),
            K = kind, Rv = rv, Msg = msg, Sub = sub,
            Time = now.ToString("HH:mm:ss"),
            At = now,
            Stat = kind == "info" ? AlarmLifecycle.Done : AlarmLifecycle.Active,
        };
        All.Insert(0, item);
        ApplyFilter();
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(ActiveCount));
        PublishAlarm(item);
        return item;
    }

    // 按条件键登记的未解除报警（ot:N 超温 / op:N 超压 / comm:K 模块掉线），条件消失时自动清除
    private readonly AlarmIndex<AlarmItem> _index = new();

    /// <summary>
    /// 记一条可自动清除的报警：同 <see cref="Log"/>，另外登记到条件键 key 下（info 不登记）。
    /// 之后 <see cref="Clear"/>(key) 会把该键下的全部条目置为已解除。
    /// </summary>
    public AlarmItem Raise(string key, string kind, string rv, string msg, string sub)
    {
        var item = Log(kind, rv, msg, sub);
        if (kind != "info") _index.Raise(key, item);
        return item;
    }

    /// <summary>该条件键下是否还有登记的（未解除的）报警。</summary>
    public bool HasActive(string key) => _index.Count(key) > 0;

    /// <summary>
    /// 报警条件消失：该键下登记的条目 未处理 → 已解除、已确认 → 已完成（历史保留，标出解除时刻），
    /// 再记一条 info「解除」事件（界面列表 + 事件文件）。只清报警列表，不改通道状态（通道仍需人工复位）。
    /// 返回被清除的条目数（无登记时为 0，解除事件照记）。
    /// </summary>
    public int Clear(string key, string rv, string msg, string sub)
    {
        var items = _index.TakeActive(key);
        var t = System.DateTimeOffset.Now.ToString("HH:mm:ss");
        int n = 0;
        var nowOff = System.DateTimeOffset.Now;
        foreach (var a in items)
        {
            var next = AlarmLifecycle.OnCleared(a.Stat);
            if (next == a.Stat) continue;
            a.Stat = next;
            a.ClearedTime = t;
            a.ClearedAt = nowOff;
            n++;
            PublishAlarm(a);
        }
        Log("info", rv, msg, n > 0 ? $"{sub} · 自动解除 {n} 条报警" : sub);   // 内含 ApplyFilter 与计数刷新
        return n;
    }

    /// <summary>人工确认一条报警（预留：界面暂无入口）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryAckAlarm))]
    private void Ack(AlarmItem? a)
    {
        if (a != null && Main is { } m) m.Local(m.Ops.TryAckAlarm(MainViewModel.Src, a.Id));
    }

    /// <summary>确认的状态迁移本身（命令层调用）。</summary>
    internal void AckCore(AlarmItem a)
    {
        a.Stat = AlarmLifecycle.OnAck(a.Stat);
        ApplyFilter();
        OnPropertyChanged(nameof(ActiveCount));
        PublishAlarm(a);
    }

    // ============ 对外接口的只读视图（GET /alarms、WS alarm）：列表副本放进 EventStore，接口线程不碰本 VM ============

    /// <summary>列表有变化（新增 / 解除 / 确认）时把整表副本与变化的那条交给事件存储。无事件存储时不做任何事。</summary>
    private void PublishAlarm(AlarmItem changed)
    {
        var ev = _logger?.Events;
        if (ev == null) return;
        var all = new Services.AlarmDto[All.Count];
        for (int i = 0; i < all.Length; i++) all[i] = ToDto(All[i]);
        ev.PublishAlarms(all, ToDto(changed));
    }

    internal static Services.AlarmDto ToDto(AlarmItem a) => new(a.Id, a.K, a.Rv, Services.EventKinds.ChannelOf(a.Rv), a.Msg, a.Sub,
        a.At.UtcDateTime, a.Stat, a.ClearedAt?.UtcDateTime);

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var a in All.Where(Match)) Filtered.Add(a);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Match(AlarmItem a) => Filter switch
    {
        "all" => true,
        "active" => a.Stat == AlarmLifecycle.Active,
        _ => a.K == Filter,
    };

}

/// <summary>一条报警/事件记录。Stat：active 未处理 / ack 已确认 / cleared 已解除 / done 已完成（见 <see cref="AlarmLifecycle"/>）。</summary>
public partial class AlarmItem : ObservableObject
{
    /// <summary>本次运行内唯一的编号（1 起递增），远程 /alarms/{id}/ack 用。</summary>
    public string Id { get; init; } = "";
    public string K { get; set; } = "info";
    public string Rv { get; set; } = "";
    public string Msg { get; set; } = "";
    public string Time { get; set; } = "";

    /// <summary>产生时刻（完整时间，对外接口 raisedAt）。</summary>
    public System.DateTimeOffset At { get; init; }

    /// <summary>自动解除的时刻（完整时间，对外接口 clearedAt；未解除为 null）。</summary>
    public System.DateTimeOffset? ClearedAt { get; set; }

    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _stat = AlarmLifecycle.Done;

    /// <summary>条件消失（自动解除）的时刻 HH:mm:ss，未解除为空。</summary>
    [ObservableProperty] private string _clearedTime = "";

    public bool HasCleared => ClearedTime.Length > 0;

    partial void OnSubChanged(string value) => OnPropertyChanged(nameof(HasSub));
    partial void OnStatChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }
    partial void OnClearedTimeChanged(string value)
    {
        OnPropertyChanged(nameof(HasCleared));
        // 解除时刻放在最前：Sub 单行省略显示，放末尾会被长文字截掉
        if (value.Length > 0) Sub = $"{value} 解除" + (Sub.Length > 0 ? " · " + Sub : "");
    }

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

    public string StatusText => Stat switch
    {
        AlarmLifecycle.Active => "未处理",
        AlarmLifecycle.Ack => "已确认",
        AlarmLifecycle.Cleared => "已解除",
        _ => "已完成",
    };
    public IBrush StatusBrush => Stat switch
    {
        AlarmLifecycle.Active => B("#e0394c"),
        AlarmLifecycle.Ack => B("#c9820f"),
        AlarmLifecycle.Cleared => B("#5fae14"),
        _ => B("#7d8694"),
    };
}
