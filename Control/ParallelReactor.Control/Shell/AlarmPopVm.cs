using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Shell;

/// <summary>报警弹窗队列里的一条：设备 + 报警（身份 = 设备号 : 报警 Id，同样稿 apKey）。</summary>
public readonly record struct PopItem(int K, string Id, int? Ch, string Code, string Text, DateTime At)
{
    public string Key => K.ToString(CultureInfo.InvariantCulture) + ":" + Id;
}

/// <summary>
/// 报警弹窗的队列规则（样稿 alarmPop / APOP_CUR / SNOOZE）：纯逻辑，单元测试直接调。
/// <list type="bullet">
/// <item>候选 = 各台设备未确认、未解除的「报警」级（错误）报警（提醒不弹；急停那一条不弹 —— 急停有自己的对话框和红框），按设备号、再按设备的报警顺序。</item>
/// <item>稍后处理（Esc / × /「稍后处理」/「定位」）= 这一条本次不再弹（按键记）；确认了自然出队。</item>
/// <item>当前条还在队列里就保持（翻页停在原处），否则回到第一条。</item>
/// </list>
/// </summary>
public sealed class PopQueue
{
    private readonly HashSet<string> _snoozed = new(StringComparer.Ordinal);
    private string? _cur;

    public IReadOnlyList<PopItem> List { get; private set; } = Array.Empty<PopItem>();

    /// <summary>当前显示的一条；没有为 null。</summary>
    public PopItem? Current
    {
        get
        {
            if (List.Count == 0) return null;
            foreach (var x in List) if (x.Key == _cur) return x;
            return List[0];
        }
    }

    public int Index
    {
        get
        {
            if (Current is not { } c) return -1;
            for (int i = 0; i < List.Count; i++) if (List[i].Key == c.Key) return i;
            return -1;
        }
    }

    /// <summary>
    /// 按最新的报警重算队列（<paramref name="all"/> = 设备号 + 当前报警）。<paramref name="first"/> = 排在最前的额外几条（v0.8.1：急停没有全部确认，
    /// 见 <see cref="AlarmPopVm.EstopCode"/>）。
    /// </summary>
    public void Update(IEnumerable<(int K, AlarmEntry A)> all, IEnumerable<PopItem>? first = null)
    {
        List = (first ?? Array.Empty<PopItem>()).Concat(all.Where(x => x.A.IsErr && !x.A.Acked && x.A.Active && !x.A.IsEstop)
                  .Select(x => new PopItem(x.K, x.A.Id, x.A.Ch, x.A.Code, x.A.Text, x.A.At)))
                  .Where(x => !_snoozed.Contains(x.Key))
                  .ToList();
        _cur = Current?.Key;
    }

    /// <summary>重新弹出某一条（设备栏的红行「阀门未确认关闭」）：忘掉它的稍后处理，下一次 Update 后显示它。</summary>
    public void Bring(string key)
    {
        _snoozed.Remove(key);
        _cur = key;
    }

    /// <summary>稍后处理当前这一条。</summary>
    public void Snooze()
    {
        if (Current is { } c) SnoozeKey(c.Key);
    }

    public void SnoozeKey(string key)
    {
        _snoozed.Add(key);
        List = List.Where(x => x.Key != key).ToList();
        if (_cur == key) _cur = List.Count > 0 ? List[0].Key : null;
    }

    public bool Next() => Move(+1);
    public bool Prev() => Move(-1);

    private bool Move(int d)
    {
        int i = Index + d;
        if (i < 0 || i >= List.Count) return false;
        _cur = List[i].Key;
        return true;
    }

    /// <summary>重新开始（截图前）：忘掉稍后处理的。</summary>
    public void Reset()
    {
        _snoozed.Clear();
        _cur = null;
        List = Array.Empty<PopItem>();
    }
}

/// <summary>报警弹窗「急停没有全部确认」的一行（v0.8.1：程序 / 加热 / 搅拌 / 阀门；没确认的一行标红）。</summary>
public sealed record EstopRowVm(string Part, string Done, string State, bool Bad, bool Ok);

/// <summary>报警弹窗里的一块读数（样稿 .avs > div：标题 13 t-3 · 值 24/600 · 单位 13 t-4；bad = 红）。</summary>
public sealed record PopTile(string Label, string Value, string Unit, bool Bad);

/// <summary>报警弹窗的小趋势图（样稿 .ach svg）：近 60 min 的点、上限、设定、报警时刻、时间窗。</summary>
public sealed record PopTrend(ChartPoint[] Points, int Count, double T0, double T1, double? Limit, double? Sp, double? AlarmT);

/// <summary>
/// 报警弹窗（融合版 F3，样稿 alarmPop / .apop）：有未确认的「报警」级报警时直接弹出（各台设备都算；提醒不弹）。
/// 标题（报警名）+「1号机 · RV4 · 报警 · 14:03:18 发生 · 已持续 27 min」+ 翻页 1 / 2 + ×（稍后处理）；读数块（当前 / 上限 / 设定）；近 60 min 小趋势（上限红虚线、设定灰点线、报警时刻）；
/// 「设备已自动处理」+ 说明；建议（样稿 AL_TIP）；底栏：报警声音（开关，声音本身在 F8）· 稍后处理 · 定位到 RVn · 停止 RVn · 确认报警（主要）。
/// 对话框开着时不弹（同样稿：遮罩在时隐藏）。
/// </summary>
public sealed partial class AlarmPopVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly PopQueue _q = new();

    public AlarmPopVm(ShellVm shell)
    {
        _shell = shell;
        LaterCommand = new RelayCommand(Later);
        PrevCommand = new RelayCommand(() => { if (_q.Prev()) Show(); });
        NextCommand = new RelayCommand(() => { if (_q.Next()) Show(); });
        LocateCommand = new RelayCommand(Locate);
        StopCommand = new RelayCommand(StopCh);
        AckCommand = new RelayCommand(Ack);
    }

    public PopQueue Queue => _q;

    public ICommand LaterCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand LocateCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AckCommand { get; }

    /// <summary>不弹（截图参数 apop=0，同样稿 APOP_OFF）。</summary>
    public bool Off { get; set; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private bool _paged;
    [ObservableProperty] private string _pageText = "";
    [ObservableProperty] private bool _canPrev;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private IReadOnlyList<PopTile> _tiles = Array.Empty<PopTile>();
    [ObservableProperty] private bool _hasTrend;
    [ObservableProperty] private string _trendCap = "";
    [ObservableProperty] private string _trendRange = "";
    [ObservableProperty] private PopTrend? _trend;
    [ObservableProperty] private bool _hasSp;
    [ObservableProperty] private string _autoText = "";
    [ObservableProperty] private string? _tip;
    [ObservableProperty] private bool _hasCh;
    [ObservableProperty] private string _locateLabel = "定位";
    [ObservableProperty] private string _stopLabel = "停止";
    [ObservableProperty] private string? _stopReason;
    [ObservableProperty] private string? _ackReason;
    /// <summary>报警声音（有未确认的报警级报警时每 30 s 响一次）：和设置页「常规 › 报警声音」是同一个设置（融合版 F8），在这里改直接保存。</summary>
    [ObservableProperty] private bool _soundOn = true;

    partial void OnSoundOnChanged(bool value)
    {
        if (_shell.Prefs.Current.AlarmSound != value) _shell.Prefs.SetAndSave(p => p with { AlarmSound = value });
    }

    /// <summary>报警名（文字第一个「 · 」之前）与设备已做的（之后）。</summary>
    public static (string Name, string Auto) Split(string text)
    {
        int i = text.IndexOf(" · ", StringComparison.Ordinal);
        return i > 0 ? (text[..i], text[(i + 3)..]) : (text, "");
    }

    /// <summary>压力类报警（样稿 /压力/）：读数块和小趋势用压力，否则用温度。</summary>
    public static bool IsPressure(string code, string name) => code == AlarmCodes.PressureHigh || name.Contains("压力", StringComparison.Ordinal) || name.Contains("超压", StringComparison.Ordinal);

    /// <summary>「已持续 27 min」（至少 1 min）。</summary>
    public static string Lasting(DateTime at, DateTime now) => "已持续 " + Math.Max(1, (int)Math.Round((now - at).TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";

    /// <summary>v0.8.1：急停没有全部确认（不是设备报警簿里的一条，按急停分项算出来，排在最前）的代码 / Id。</summary>
    public const string EstopCode = "ESTOP-PART", EstopId = "estop-part";

    /// <summary>当前这一条是「急停没有全部确认」（显示四行，不显示读数块、趋势）。</summary>
    [ObservableProperty] private bool _isEstop;
    [ObservableProperty] private IReadOnlyList<EstopRowVm> _estopRows = Array.Empty<EstopRowVm>();
    /// <summary>「定位到…」按钮（通道报警 = 定位到 RVn；急停 = 定位到设备）。</summary>
    [ObservableProperty] private bool _canLocate;
    /// <summary>不是急停的那几块（读数、自动处理）。</summary>
    [ObservableProperty] private bool _notEstop = true;

    /// <summary>急停后有部件没确认的设备 → 弹窗的一条。</summary>
    private IEnumerable<PopItem> EstopItems()
    {
        var now = _shell.Fleet.Now;
        foreach (var d in _shell.Fleet.Devices)
            if (d.EstopActive && d.Marks.Estop is { } es && es.AnyUnconfirmed(now))
                yield return new PopItem(d.K, EstopId, null, EstopCode, "急停没有全部确认", es.Since ?? d.Source.Estop.At ?? now);
    }

    /// <summary>设备栏红行「…未确认 · 到设备旁确认」：把这台的急停弹窗重新弹出来。</summary>
    public void ShowEstop(int k)
    {
        Off = false;
        _q.Bring(new PopItem(k, EstopId, null, EstopCode, "", default).Key);
        Refresh();
    }

    /// <summary>重算（数据变了 / 对话框开关了）。</summary>
    public void Refresh()
    {
        var all = _shell.Fleet.Devices.SelectMany(d => d.AlarmList.Select(a => (d.K, a)));
        var es = EstopItems().ToList();
        // 新出现的「急停没有全部确认」直接翻到它（排在最前，比正在看的那条报警要紧）
        foreach (var e in es) if (_estopSeen.Add(e.Key)) _q.Bring(e.Key);
        _estopSeen.RemoveWhere(k => es.All(e => e.Key != k));
        _q.Update(all, es);
        Show();
    }

    private readonly HashSet<string> _estopSeen = new(StringComparer.Ordinal);

    /// <summary>截图前：忘掉稍后处理的，按 <see cref="Off"/> 重新决定。</summary>
    public void Reset(bool off)
    {
        Off = off;
        _q.Reset();
        _estopSeen.Clear();
        Refresh();
    }

    private void Show()
    {
        var cur = _q.Current;
        if (Off || cur is not { } c || _shell.Dialogs.IsOpen || _shell.Fleet.Device(c.K) is not { } d)
        {
            IsOpen = false;
            return;
        }
        IsOpen = true;
        if (c.Code == EstopCode) { ShowEstopItem(c, d); return; }
        IsEstop = false;
        NotEstop = true;
        var (name, auto) = Split(c.Text);
        var now = _shell.Fleet.Now;
        var rv = c.Ch is int n ? "RV" + n.ToString(CultureInfo.InvariantCulture) : "整机";
        Title = name;
        Sub = d.Alias + " · " + rv + " · 报警 · " + c.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " 发生 · " + Lasting(c.At, now);
        int i = _q.Index;
        Paged = _q.List.Count > 1;
        PageText = (i + 1).ToString(CultureInfo.InvariantCulture) + " / " + _q.List.Count.ToString(CultureInfo.InvariantCulture);
        CanPrev = i > 0;
        CanNext = i < _q.List.Count - 1;
        AutoText = auto.Length > 0 ? auto : "已记录 · 等待人工处理";
        Tip = HomeTexts.AlarmTips.TryGetValue(name, out var t) ? t : null;
        HasCh = c.Ch is int;
        CanLocate = HasCh;
        LocateLabel = "定位到 " + rv;
        StopLabel = "停止 " + rv;

        var r = c.Ch is int ch ? d.Reactor(ch) : null;
        bool isP = IsPressure(c.Code, name);
        var plant = d.Source.Plant;
        if (r != null)
        {
            Tiles = isP
                ? new[]
                {
                    // 压力按设置的单位（MPa 2 位小数、bar 2 位、psi 1 位）
                    new PopTile("当前压力", PressureDisplay.Fixed(r.PressureMPa, 2), PressureDisplay.Unit, true),
                    new PopTile("报警上限", PressureDisplay.Fixed(plant.OverPressureMPa, 2), PressureDisplay.Unit, false),
                    new PopTile("设定压力", r.PTargetMPa is double s && s > 0 ? PressureDisplay.Fixed(s, 2) : "—", PressureDisplay.Unit, false),
                }
                : new[]
                {
                    new PopTile("当前温度", r.TempText, "°C", true),
                    new PopTile("报警上限", plant.OverTempC is double lt ? lt.ToString("0", CultureInfo.InvariantCulture) : "—", "°C", false),
                    new PopTile("设定温度", r.SpC is double st ? st.ToString("0.0", CultureInfo.InvariantCulture) : "—", "°C", false),
                };
            var h = d.Source.History.Channel(r.N);
            var hn = d.Source.History.Now;
            if (h != null && h.Count > 0)
            {
                var buf = Array.Empty<ChartPoint>();
                int cnt = h.CopyPoints(isP ? HistoryField.PressureMPa : HistoryField.TempC, hn, 60, ref buf);
                double? sp = isP ? (r.PTargetMPa is > 0 ? r.PTargetMPa : null) : r.SpC;
                Trend = new PopTrend(buf, cnt, ChartTime.Ms(hn.AddMinutes(-60)), ChartTime.Ms(hn), isP ? plant.OverPressureMPa : plant.OverTempC, sp, ChartTime.Ms(c.At));
                HasSp = sp != null;
                HasTrend = cnt > 1;
                TrendCap = (isP ? PressureDisplay.Label("压力") : "温度 °C") + " · 近 60 min";
                TrendRange = hn.AddMinutes(-60).ToString("HH:mm", CultureInfo.InvariantCulture) + " – 现在";
            }
            else { HasTrend = false; Trend = null; }
            StopReason = Nz(d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(r.N)));
        }
        else
        {
            Tiles = Array.Empty<PopTile>();
            HasTrend = false;
            Trend = null;
            StopReason = "设备级报警 · 没有要停的通道";
        }
        AckReason = Nz(d.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: c.Id)));
    }

    /// <summary>
    /// 急停没有全部确认（样稿 act.js axEstopPop）：「1号机 · 14:29:52 本机发出急停 · 3 / 4 项已确认」+ 四行（没确认的红）+ 建议；
    /// 底栏：稍后处理 · 定位到1号机 · 确认报警（确认设备的「急停已触发」那一条）。
    /// </summary>
    private void ShowEstopItem(PopItem c, DeviceVm d)
    {
        var now = _shell.Fleet.Now;
        var es = d.Marks.Estop!;
        IsEstop = true;
        NotEstop = false;
        var rows = es.Rows(now);
        var bad = rows.Where(r => r.Bad).Select(r => r.Part).ToList();
        var (ok, of) = es.Confirmed(now);   // 含「程序」一行（急停时程序一律中止）
        Title = "急停没有全部确认";
        var by = d.Source.Estop.By is { Length: > 0 } b && b.Contains(_shell.Fleet.ClientName, StringComparison.Ordinal) ? "本机发出急停" : "急停";
        Sub = d.Alias + " · " + c.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + by + " · " + ok.ToString(CultureInfo.InvariantCulture) + " / "
            + of.ToString(CultureInfo.InvariantCulture) + " 项已确认";
        EstopRows = rows.Where(r => !r.Na).Select(r => new EstopRowVm(r.Part, r.Done, r.Ok ? "已确认" : r.Bad ? "没有收到确认" : "等待确认", r.Bad, r.Ok)).ToList();
        int i = _q.Index;
        Paged = _q.List.Count > 1;
        PageText = (i + 1).ToString(CultureInfo.InvariantCulture) + " / " + _q.List.Count.ToString(CultureInfo.InvariantCulture);
        CanPrev = i > 0;
        CanNext = i < _q.List.Count - 1;
        Tiles = Array.Empty<PopTile>();
        HasTrend = false;
        Trend = null;
        HasCh = false;
        CanLocate = true;
        LocateLabel = "定位到" + d.Alias;
        Tip = "到设备旁确认" + string.Join("、", bad) + "已经" + (bad.Count == 1 && bad[0] == "阀门" ? "关闭" : "停下") + "，确认前不要打开釜盖；必要时关闭气瓶总阀。急停复位只能在设备触屏上进行。";
        StopReason = null;
        var estopAlarm = d.AlarmList.FirstOrDefault(a => a.IsEstop && !a.Acked);
        AckReason = estopAlarm is null ? null : Nz(d.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: estopAlarm.Id)));
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;

    private void Later()
    {
        _q.Snooze();
        Show();
    }

    private void Locate()
    {
        if (_q.Current is not { } c) return;
        if (c.Code == EstopCode)
        {
            _q.SnoozeKey(c.Key);
            _shell.Go(AppPage.Home);
            _shell.SelectDevice(c.K);
            Show();
            return;
        }
        _q.SnoozeKey(c.Key);
        _shell.Go(AppPage.Home);
        if (c.Ch is int n) _shell.OpenChannel(c.K, n); else _shell.SelectDevice(c.K);
        Show();
    }

    /// <summary>「停止 RVn」：和检查器的「停止该通道」一样先弹确认框（2026-11-18 用户：停止一律先确认）。确认框开着时报警弹窗自动让开，关掉后再回来。</summary>
    private void StopCh()
    {
        if (_q.Current is not { Ch: int n } c || _shell.Fleet.Device(c.K) is null) return;
        _shell.OpenDialog("stopch", c.K.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Ack()
    {
        if (_q.Current is not { } c || _shell.Fleet.Device(c.K) is not { } d) return;
        if (c.Code == EstopCode)
        {
            // 急停没有全部确认：确认设备的「急停已触发」那一条（还没确认的话），这一条本次不再弹
            if (d.AlarmList.FirstOrDefault(a => a.IsEstop && !a.Acked) is { } ea) _ = d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: ea.Id));
            _q.SnoozeKey(c.Key);
            Show();
            return;
        }
        var vm = d.Alarms.FirstOrDefault(a => a.Id == c.Id);
        if (vm != null && vm.AckCommand.CanExecute(null)) vm.AckCommand.Execute(null);
        else _ = d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: c.Id));
        _q.SnoozeKey(c.Key);   // 已经确认：不等数据回来就从队列里拿掉（确认失败时下一次刷新不会再弹 —— 报警列表里仍可确认）
        Show();
    }
}
