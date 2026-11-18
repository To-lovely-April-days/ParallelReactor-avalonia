using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Pid;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Settings.Pid;

/// <summary>
/// 设置 → 温控 / PID（每台设备；样稿 pidBody，2026-11-18 用户确认的自整定设计）：
/// <list type="number">
/// <item><b>档位卡片</b>（GET /pid 的 bands）：名字、温区、「n/m 路已整定」；下限 ≥ 本机温度上限的档禁用（「超过本机上限 200 °C」）。</item>
/// <item><b>左表</b>：这一档每路的 P / I / D（手改失焦 / 回车即写入这一档，SetPidAsync）、整定温度、整定日期（窄屏不显示）、状态
///   （已整定 / 未整定 / 运行中 · 不能整定 / 停用 / 整定中 · 第 k/3 次振荡 / 整定完成）；勾选要整定的通道。</item>
/// <item><b>右边「自整定」</b>：整定温度（本档 ∩ [40, TMaxSp]，默认 80 / 150 / 250）、所选通道、之后、说明、「开始自整定（n 路）」
///   （没有控制权 / 没勾通道 / 温度不在档里时带原因禁用）→ 每路 StartAutotuneAsync(n, tempC, band)，几路同时整定。</item>
/// <item><b>整定监视</b>（这台设备有通道在整定时）：标题（档、温度、通道、已用时间）+「取消整定」（先确认，CancelAutotuneAsync）；
///   两张图（温度 PV + 整定温度虚线、加热输出 %，不共用纵轴；数据 = 曲线缓存，横轴 = 距开始的分钟，「现在」竖线）；
///   振荡步骤 第 1/2/3 次（<see cref="OscillationCounter"/>：AI-8 不报告进度，只能数曲线）。</item>
/// <item><b>结束</b>：某路的 autotuning 由真变假、又不是取消的 → 重读 GET /pid，给出「P 旧 → 新 · I … · D … · 已存入…档」+ 说明，表里换成新值；
///   结果一直留着，直到换档或离开这一节。</item>
/// </list>
/// 打开这一节、每次整定开始 / 结束、手改之后读 GET /pid（演示 = DemoFleet.PidOf）。
/// </summary>
public sealed partial class PidSectionVm : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ShellVm _shell;
    private readonly Func<DeviceVm?> _dev;
    private readonly Func<bool> _visible;
    private readonly Dictionary<IDeviceSource, DevState> _states = new();
    private readonly HashSet<int> _sel = new();
    private bool _syncing, _quiet;
    private DeviceVm? _lastDev;
    private bool _wasVisible;

    public PidSectionVm(ShellVm shell, Func<DeviceVm?> dev, Func<bool> visible)
    {
        _shell = shell;
        _dev = dev;
        _visible = visible;
        StartCommand = new AsyncRelayCommand(StartAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        CancelCommand = new RelayCommand(() => { if (_dev() is { } d) _shell.OpenDialog(PidCancelDialogVm.Name0, d.K.ToString(Inv)); });
        shell.Dialogs.Register(PidCancelDialogVm.Name0, arg => PidCancelDialogVm.Create(shell, this, arg));
    }

    // ———————————————————— 每台设备的状态 ————————————————————

    /// <summary>一次整定（本机发起的，或在页面上第一次看到的）。</summary>
    internal sealed class TuneRun
    {
        public int Band;
        public string BandName = "";
        public double Lo, Hi;
        public double Sp;
        /// <summary>开始时刻（本地，设备曲线的时钟）。</summary>
        public DateTime Start;
        public readonly List<int> Ch = new();
        public readonly Dictionary<int, PidValues> Old = new();
        /// <summary>看到过 autotuning = true 的通道。</summary>
        public readonly HashSet<int> Seen = new();
        /// <summary>整定结束的时刻（本地）。</summary>
        public readonly Dictionary<int, DateTime> Ended = new();
        public readonly Dictionary<int, PidResultVm> Results = new();
        public bool Shown;
        public bool Done => Ch.Count > 0 && Ch.All(Ended.ContainsKey);
        public bool Reported;
    }

    private sealed class DevState
    {
        public PidDto? Data;
        public bool Reading, ReadAgain;
        /// <summary>看到有通道在整定、GET /pid 还没写「在整定」时又读了几次。</summary>
        public int StaleReads;
        /// <summary>刚取消的通道：遥测还写着「在整定」时不当作新的整定（看到一次「不在整定」后放开）。</summary>
        public readonly HashSet<int> Ignore = new();
        public string? Error;
        public TuneRun? Run;
    }

    private DevState State(DeviceVm d)
    {
        if (!_states.TryGetValue(d.Source, out var s)) _states[d.Source] = s = new DevState();
        return s;
    }

    /// <summary>这台设备此刻的整定（测试用）。</summary>
    internal TuneRun? RunOf(DeviceVm d) => _states.TryGetValue(d.Source, out var s) ? s.Run : null;

    /// <summary>最后一次读到的 GET /pid（测试用）。</summary>
    public PidDto? DataOf(DeviceVm d) => _states.TryGetValue(d.Source, out var s) ? s.Data : null;

    /// <summary>读写这台设备的口子（测试可以换）。</summary>
    public Func<DeviceVm, IPidPort?> PortFor { get; set; } = PidPorts.For;

    // ———————————————————— 绑定 ————————————————————

    public ObservableCollection<PidBandCardVm> Bands { get; } = new();
    public ObservableCollection<PidRowVm> Rows { get; } = new();
    public ObservableCollection<PidChipVm> Chips { get; } = new();
    public ObservableCollection<PidStepVm> Steps { get; } = new();
    public ObservableCollection<PidResultVm> Results { get; } = new();

    /// <summary>本地只读 / 未连接的说明条（能写时 null）。</summary>
    [ObservableProperty] private string? _lockText;
    /// <summary>还没读到 PID 时的说明（读到了为 null）。</summary>
    [ObservableProperty] private string? _noData;
    [ObservableProperty] private bool _hasData;
    /// <summary>窄屏（&lt; 1600）：不显示整定日期、输入 56 宽、右边 440。</summary>
    [ObservableProperty] private bool _isNarrow;
    /// <summary>选中的档（GET /pid 的 index；默认中温档）。</summary>
    [ObservableProperty] private int _band = 1;
    [ObservableProperty] private string _bandName = "";
    [ObservableProperty] private string _tableNote = "";

    // 右边：表单
    [ObservableProperty] private bool _showForm = true;
    [ObservableProperty] private string _formTitle = "";
    /// <summary>整定温度（输入框的文字，°C）。</summary>
    [ObservableProperty] private string _spText = "150";
    [ObservableProperty] private bool _spBad;
    [ObservableProperty] private string _spRange = "";
    [ObservableProperty] private bool _hasChips;
    [ObservableProperty] private string _chipHint = "在左边表格里勾选（运行中的通道不能整定）";
    [ObservableProperty] private string _afterText = "";
    /// <summary>说明的后半（「整定温度选这一档里<b>平时最常用的反应温度</b>：整出的」之后，带区间）。</summary>
    [ObservableProperty] private string _formNoteC = "";
    [ObservableProperty] private string _startLabel = "开始自整定（0 路）";
    [ObservableProperty] private string? _startReason = "先勾选通道";
    [ObservableProperty] private bool _starting;
    public ICommand StartCommand { get; }

    // 右边：整定监视
    [ObservableProperty] private bool _showMonitor;
    [ObservableProperty] private bool _monDone;
    [ObservableProperty] private string _monTitle = "";
    [ObservableProperty] private string _monSub = "";
    [ObservableProperty] private PidChartData? _chart;
    [ObservableProperty] private string _stepNote = "仪表在整定温度附近通断加热，振荡 2–3 次后自动算出 PID（通常 20–40 min，不报告进度）";
    [ObservableProperty] private string _okNote = "";
    [ObservableProperty] private bool _hasOkNote;
    [ObservableProperty] private bool _canCancel = true;
    [ObservableProperty] private string? _cancelReason;
    public ICommand CancelCommand { get; }

    partial void OnSpTextChanged(string value) { if (!_syncing) Render(); }

    // ———————————————————— 刷新 ————————————————————

    /// <summary>页面 / 设备 / 数据变了（SettingsPageVm.Refresh，每秒）：先看整定的状态有没有变（每台有整定的设备都看），再画这一台。</summary>
    public void Refresh()
    {
        var d = _dev();
        bool vis = _visible();
        IsNarrow = !_shell.Layout.IsWide;
        // 离开（换了这一节 / 换了设备）：给出过的结果不再留着；回来时重新读
        if (!vis || !ReferenceEquals(d, _lastDev))
        {
            foreach (var (src, st) in _states)
                if (st.Run is { Done: true, Shown: true } && (!vis || d == null || !ReferenceEquals(src, d.Source))) st.Run = null;
            if (_lastDev != null && !ReferenceEquals(d, _lastDev)) _sel.Clear();
        }
        if (vis && d != null && (!_wasVisible || !ReferenceEquals(d, _lastDev))) Read(d);
        _wasVisible = vis;
        _lastDev = d;
        foreach (var dv in _shell.Fleet.Devices) Poll(dv);
        if (vis) Render();
    }

    /// <summary>读 GET /pid（正在读时记下再读一次）。演示同步完成。</summary>
    private void Read(DeviceVm d)
    {
        var st = State(d);
        var port = PortFor(d);
        if (port == null) { st.Error = "这个数据源不提供 PID"; return; }
        if (!port.CanRead) { st.Error = null; return; }
        if (st.Reading) { st.ReadAgain = true; return; }
        _ = ReadAsync(d, st, port);
    }

    private async Task ReadAsync(DeviceVm d, DevState st, IPidPort port)
    {
        st.Reading = true;
        try
        {
            do
            {
                st.ReadAgain = false;
                try
                {
                    st.Data = await port.ReadAsync(CancellationToken.None).ConfigureAwait(true);
                    st.Error = null;
                }
                catch (Exception ex) { st.Error = DeviceActions.Describe(ex); }
            } while (st.ReadAgain);
        }
        finally { st.Reading = false; }
        AfterRead(d, st);
        Poll(d);
        if (_visible() && ReferenceEquals(_dev(), d)) Render();
    }

    /// <summary>
    /// 看这台设备的整定有没有开始 / 结束：
    /// 有通道在整定、页面还不知道 → 建一个整定（档 / 温度取 GET /pid 的 tuneBand / tuneSpC；开始时刻 = 演示给的，否则此刻）；
    /// 某路看到过「在整定」、现在不在了 → 记下结束时刻（取消的不算：取消时整定已经拿掉了），重读 PID 给出结果。
    /// </summary>
    private void Poll(DeviceVm d)
    {
        if (!_states.TryGetValue(d.Source, out var st) && !_visible()) return;
        st ??= State(d);
        var port = PortFor(d);
        if (port == null) return;
        var now = d.Source.History.Now;
        var tuning = d.Reactors.Where(r => r.Installed).Select(r => r.N)
            .Where(n => port.Autotuning(n) ?? st.Data?.Channels.FirstOrDefault(c => c.Id == n)?.Autotuning ?? false).ToList();
        st.Ignore.RemoveWhere(n => !tuning.Contains(n));
        tuning.RemoveAll(st.Ignore.Contains);
        var run = st.Run;
        if (tuning.Count == 0) st.StaleReads = 0;
        if (tuning.Count > 0 && (run == null || run.Done))
        {
            // 先拿到一份「已经在整定」的 GET /pid（档位、整定温度；PID 本身要到整定结束才变 → 就是整定前的值，结果拿它对比）
            // （读了 3 次还没写「在整定」就不等了：档位 / 温度按当时的设定值推断）
            bool fresh = st.Data != null && tuning.Any(n => st.Data.Channels.FirstOrDefault(c => c.Id == n)?.Autotuning == true);
            if (!fresh && (st.Data == null || st.StaleReads < 3))
            {
                if (!st.Reading) { st.StaleReads++; Read(d); }
                return;
            }
            st.StaleReads = 0;
            run = st.Run = NewRun(d, st.Data!, tuning, port, now);
        }
        else if (run != null && !run.Done)
        {
            // 整定中又有通道开始（触屏上另起的同一档）：并进这一次；别的档的不并
            foreach (var n in tuning)
            {
                if (run.Ch.Contains(n) || st.Data is not { } data) continue;
                var tb = data.Channels.FirstOrDefault(c => c.Id == n)?.TuneBand;
                if (tb != null && tb != run.Band) continue;
                run.Ch.Add(n);
                AddOld(run, data, n);
            }
        }
        if (run == null || run.Done) return;
        bool ended = false;
        foreach (var n in run.Ch)
        {
            if (run.Ended.ContainsKey(n)) continue;
            if (tuning.Contains(n)) { run.Seen.Add(n); continue; }
            // 看到过在整定、现在不在了 → 结束；本机发起后一直没看到（设备没开始）1 min 后也算结束
            if (run.Seen.Contains(n) || (now - run.Start).TotalSeconds > 60)
            {
                run.Ended[n] = now;
                ended = true;
            }
        }
        if (ended) Read(d);
    }

    private TuneRun NewRun(DeviceVm d, PidDto data, IReadOnlyList<int> tuning, IPidPort port, DateTime now)
    {
        var c0 = data.Channels.FirstOrDefault(c => c.Id == tuning[0]);
        var bands = BandsOf(data);
        double sp = c0?.TuneSpC ?? d.Reactor(tuning[0])?.SpC ?? 0;
        int band = c0?.TuneBand ?? PidTune.BandFor(bands, sp) ?? Band;
        var b = bands.FirstOrDefault(x => x.Index == band) ?? bands.FirstOrDefault();
        var run = new TuneRun
        {
            Band = band, Sp = sp, BandName = b?.Name ?? "",
            Lo = b?.LoC ?? 0, Hi = b?.HiC ?? 0,
            Start = tuning.Select(n => port.StartedAt(n)).FirstOrDefault(t => t != null) ?? now,
        };
        foreach (var n in tuning)
        {
            var c = data.Channels.FirstOrDefault(x => x.Id == n);
            if (c?.TuneBand is int tb && tb != band) continue;   // 别的档的整定（触屏上另起的）不放进这一次
            run.Ch.Add(n);
            AddOld(run, data, n);
        }
        if (run.Ch.Count == 0) { run.Ch.Add(tuning[0]); AddOld(run, data, tuning[0]); }
        if (_visible() && ReferenceEquals(_dev(), d) && Band != band) SetBand(band, keepRun: true);
        return run;
    }

    private static void AddOld(TuneRun run, PidDto data, int n)
    {
        if (Cell(data, n, run.Band) is { } v) run.Old[n] = new PidValues(v.P, v.I, v.D);
    }

    /// <summary>读到 PID 之后：结束了的通道给出结果（新值、是否真的存入了：来历的完成时刻晚于这次开始）。</summary>
    private void AfterRead(DeviceVm d, DevState st)
    {
        var run = st.Run;
        if (run == null || st.Data == null) return;
        foreach (var n in run.Ch)
        {
            if (!run.Ended.ContainsKey(n) || run.Results.ContainsKey(n)) continue;
            var v = Cell(st.Data, n, run.Band);
            var old = run.Old.TryGetValue(n, out var o) ? o : v is { } x ? new PidValues(x.P, x.I, x.D) : default;
            var now = v is { } y ? new PidValues(y.P, y.I, y.D) : old;
            bool saved = v?.TunedAt is DateTime at && at.ToLocalTime() >= run.Start.AddMinutes(-2);
            var rv = "RV" + n.ToString(Inv);
            run.Results[n] = new PidResultVm(n, rv, RunRowText.ChBrush(n), PidTexts.DiffParts(old, now), "已存入" + run.BandName,
                saved ? null : PidTexts.NotSaved, saved ? PidTexts.Diff(old, now, run.BandName) : PidTexts.NotSaved);
        }
        if (run.Done && !run.Reported && run.Ch.All(run.Results.ContainsKey))
        {
            run.Reported = true;
            if (_quiet) return;   // 地址参数摆出来的「整定完成」（截图）不弹提示
            bool all = run.Results.Values.All(r => r.Ok);
            _shell.Toast(d.Alias + " " + PidTexts.Rvs(run.Ch) + (all ? " 自整定完成 · 新 PID 已存入" + run.BandName : " 自整定结束 · 有通道没有存入新 PID"),
                all ? ToastKind.Ok : ToastKind.Warn);
        }
    }

    private static PidBandValueSnap? Cell(PidDto data, int n, int band) =>
        data.Channels.FirstOrDefault(c => c.Id == n)?.Bands.FirstOrDefault(b => b.Index == band);

    private static List<PidBand> BandsOf(PidDto data)
    {
        var bs = data.Bands.OrderBy(b => b.Index).ToList();
        return bs.Select((b, i) => new PidBand(b.Index, b.Name, b.LoC, b.HiC, i == bs.Count - 1)).ToList();
    }

    private double TMax(DeviceVm d) => SettingsPageVm.SettingsOf(d)?.Limits.TempSpMaxC is double t && t > 0 ? t : d.Model.Limits.TempMaxC;

    // ———————————————————— 画 ————————————————————

    private void Render()
    {
        var d = _dev();
        _syncing = true;
        try { RenderCore(d); }
        finally { _syncing = false; }
    }

    private void RenderCore(DeviceVm? d)
    {
        if (d == null) { HasData = false; NoData = "没有设备"; LockText = null; Rows.Clear(); Bands.Clear(); ShowMonitor = false; ShowForm = false; return; }
        var st = State(d);
        bool remote = d.Lease == LeaseState.Remote;
        LockText = d.IsOffline ? d.Alias + "未连接 · 连上并申请控制权后可以改 PID 和自整定"
            : !remote ? d.Alias + "为本地只读 · 申请控制权后可以改 PID 和自整定" : null;
        var data = st.Data;
        HasData = data != null;
        NoData = data != null ? null
            : st.Error is { } e ? "读取 PID 失败：" + e
            : d.IsOffline ? d.Alias + "还没有连上 · 连上后读取 PID"
            : "正在读取 PID…";
        if (data == null) { Rows.Clear(); Bands.Clear(); ShowMonitor = false; ShowForm = false; return; }

        double tmax = TMax(d);
        var bands = BandsOf(data);
        if (!bands.Any(b => b.Index == Band)) SetBand(bands.Count > 1 ? bands[1].Index : bands[0].Index, keepRun: true);
        var band = bands.First(b => b.Index == Band);
        var range = PidTune.Range(band, tmax);
        BandName = band.Name;
        var reactors = d.Reactors.Where(r => r.Installed).ToList();
        bool Off(ReactorVm r) => r.Own == StateKey.Idle;

        // 档位卡片
        while (Bands.Count > bands.Count) Bands.RemoveAt(Bands.Count - 1);
        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            if (i >= Bands.Count || Bands[i].Index != b.Index)
            {
                var c = new PidBandCardVm(b.Index, k => PickBand(k));
                if (i < Bands.Count) Bands[i] = c; else Bands.Add(c);
            }
            var card = Bands[i];
            var rg = PidTune.Range(b, tmax);
            int all = reactors.Count(r => !Off(r));
            int tuned = reactors.Count(r => !Off(r) && Cell(data, r.N, b.Index)?.TunedAt != null);
            card.Name = b.Name;
            card.Range = PidTexts.Num(b.LoC) + " – " + PidTexts.Num(b.HiC) + " °C";
            card.Status = PidTune.BandStatus(tuned, all, rg.Usable, tmax);
            card.Usable = rg.Usable;
            card.Reason = rg.Usable ? null : "超过本机温度上限 " + PidTexts.Num(tmax) + " °C";
            card.IsOn = b.Index == Band;
        }
        TableNote = "表里是" + band.Name + "（" + PidTexts.Num(band.LoC) + "–" + PidTexts.Num(band.HiC) + " °C）每一路的 PID；加热时按设定温度所在的档自动套用。手改 P / I / D 立即写入并存档。";

        // 这一档的整定
        var run = st.Run;
        var now = d.Source.History.Now;
        var counts = new Dictionary<int, OscCount>();
        if (run != null)
            foreach (var n in run.Ch) counts[n] = OscillationCounter.Count(Samples(d, run, n, now), run.Sp);
        bool estop = d.EstopActive;
        var port = PortFor(d);

        // 表
        while (Rows.Count > reactors.Count) Rows.RemoveAt(Rows.Count - 1);
        for (int i = 0; i < reactors.Count; i++)
        {
            var r = reactors[i];
            if (i >= Rows.Count || Rows[i].N != r.N)
            {
                var row = new PidRowVm(r.N, RunRowText.ChBrush(r.N), Commit, Check);
                if (i < Rows.Count) Rows[i] = row; else Rows.Add(row);
            }
            var w = Rows[i];
            w.Syncing = true;
            try
            {
                bool off = Off(r), busy = r.IsRunning || estop;
                bool inTun = run != null && run.Band == Band && run.Ch.Contains(r.N);
                bool tunNow = inTun && !run!.Ended.ContainsKey(r.N);
                var cell = Cell(data, r.N, Band);
                // 不在这一次整定里、却在整定的通道（触屏上另起的、别的档的）
                bool tuningNow = port?.Autotuning(r.N) ?? data.Channels.FirstOrDefault(c => c.Id == r.N)?.Autotuning ?? false;
                var tunOther = !inTun && tuningNow ? data.Channels.FirstOrDefault(c => c.Id == r.N) : null;
                w.Off = off;
                w.HasValues = !off && cell != null;
                if (cell != null && !w.Pending)
                {
                    w.P = PidTexts.P(cell.P);
                    w.I = PidTexts.Sec(cell.I);
                    w.D = PidTexts.Sec(cell.D);
                }
                w.TunedSp = !off && cell?.TunedSpC is double tsp ? PidTexts.Num(tsp) + " °C" : "—";
                w.TunedAt = !off && cell?.TunedAt is DateTime ta ? ta.ToLocalTime().ToString("MM-dd", Inv) : "—";
                w.TunedTip = !off && cell?.ManualEditedAt is DateTime me ? "整定之后在 " + me.ToLocalTime().ToString("MM-dd HH:mm", Inv) + " 手改过" : null;
                w.CanCheck = remote && !d.IsOffline && range.Usable && !off && !busy && run == null && tunOther == null;
                w.CheckReason = w.CanCheck ? null
                    : off ? "停用的通道不能整定" : busy ? "运行中的通道不能整定" : !range.Usable ? "超过本机温度上限"
                    : run is { Done: true } ? "整定结果显示中 · 换一档再回来可以重新选" : run != null ? "正在整定 · 结束后再选" : tunOther != null ? "这一路正在整定" : !remote || d.IsOffline ? "先申请控制权" : null;
                if (!w.CanCheck) _sel.Remove(r.N);
                w.Checked = _sel.Contains(r.N) || inTun;
                w.IsOn = w.Checked;
                w.CanEdit = remote && !d.IsOffline && !off && !inTun && tunOther == null && cell != null;
                w.EditReason = w.CanEdit ? null : off ? "停用的通道" : inTun || tunOther != null ? "整定中不能改 PID" : "先申请控制权";
                (w.Status, w.Tone) = off ? ("停用", PidTone.Mute)
                    : inTun && !tunNow ? (run!.Results.TryGetValue(r.N, out var res) && res.Failed ? ("整定没有完成", PidTone.Warn) : ("整定完成", PidTone.Run))
                    : inTun ? ("整定中 · 第 " + (counts.TryGetValue(r.N, out var oc2) ? oc2.Current : 1).ToString(Inv) + "/3 次振荡", PidTone.Brand)
                    : tunOther != null ? ("整定中（" + (bands.FirstOrDefault(b => b.Index == tunOther.TuneBand)?.Name ?? "其它档") + "）", PidTone.Brand)
                    : busy ? ("运行中 · 不能整定", PidTone.Mute)
                    : cell?.TunedAt != null ? (cell.ManualEditedAt != null ? "已整定 · 手改过" : "已整定", PidTone.Mute)
                    : ("未整定", PidTone.Warn);
            }
            finally { w.Syncing = false; }
        }

        // 右边
        ShowMonitor = run != null;
        ShowForm = run == null;
        if (run != null) RenderMonitor(d, run, counts, now);
        else RenderForm(d, band, range, remote);
    }

    private void RenderForm(DeviceVm d, PidBand band, PidTuneRange range, bool remote)
    {
        FormTitle = "自整定 · " + band.Name;
        double? sp = double.TryParse((SpText ?? "").Trim(), NumberStyles.Float, Inv, out var v) ? v : null;
        double tmax = TMax(d);
        SpBad = PidTune.TempReason(band, tmax, sp) != null && range.Usable;
        SpRange = range.Usable ? "本档 " + range.Text : "超过本机上限 " + PidTexts.Num(tmax) + " °C";
        var chosen = _sel.OrderBy(n => n).ToList();
        var chips = chosen.Select(n => new PidChipVm("RV" + n.ToString(Inv), RunRowText.ChBrush(n))).ToList();
        if (Chips.Count != chips.Count || !Chips.Select(c => c.Rv).SequenceEqual(chips.Select(c => c.Rv)))
        {
            Chips.Clear();
            foreach (var c in chips) Chips.Add(c);
        }
        HasChips = chips.Count > 0;
        AfterText = "完成后自动存入" + band.Name + "，通道停止加热";
        FormNoteC = " PID 用于整个区间（" + PidTexts.Num(band.LoC) + "–" + PidTexts.Num(band.HiC) + " °C），离整定温度越近越准。整定时釜内应是平时的溶液量、密封、常压或惰性气体保护。";
        StartLabel = "开始自整定（" + chosen.Count.ToString(Inv) + " 路）";
        StartReason = Starting ? "正在开始…"
            : d.IsOffline ? d.Alias + "未连接"
            : !remote ? "先申请控制权"
            : !range.Usable ? "超过本机温度上限"
            : chosen.Count == 0 ? "先勾选通道"
            : PidTune.TempReason(band, tmax, sp) is { } why ? why
            : null;
    }

    private void RenderMonitor(DeviceVm d, TuneRun run, Dictionary<int, OscCount> counts, DateTime now)
    {
        bool done = run.Done;
        if (done && _visible()) run.Shown = true;
        var end = done ? run.Ended.Values.Max() : now;
        int elapsed = (int)Math.Floor(Math.Max(0, (end - run.Start).TotalMinutes));
        MonDone = done;
        MonTitle = done ? "整定完成" : "正在自整定";
        MonSub = run.BandName + " · 整定温度 " + PidTexts.Num(run.Sp) + " °C · " + PidTexts.Rvs(run.Ch) + " · 已用 " + elapsed.ToString(Inv) + " min";
        CanCancel = !done && (d.Source.Conn.CanStop);
        CancelReason = CanCancel ? null : d.Alias + "未连接";

        // 曲线
        double nowM = Math.Max(0, (now - run.Start).TotalMinutes);
        double endM = Math.Max(0, (end - run.Start).TotalMinutes);
        double total = endM <= 36 ? 40 : Math.Ceiling(endM / 12) * 12 + 4;
        var lines = new List<PidChartLine>();
        foreach (var n in run.Ch)
        {
            var s = Samples(d, run, n, run.Ended.TryGetValue(n, out var e) ? e : now);
            lines.Add(new PidChartLine(n, RunRowText.ChColor(n), s.Select(x => x.M).ToArray(), s.Select(x => x.Pv ?? double.NaN).ToArray(),
                s.Select(x => x.Out ?? double.NaN).ToArray()));
        }
        Chart = new PidChartData(run.Sp, total, done ? null : nowM, lines);

        // 振荡步骤（几路同时整定：按最慢的一路）
        int cyc = counts.Count == 0 ? 0 : run.Ch.Where(n => !run.Ended.ContainsKey(n)).Select(n => counts.TryGetValue(n, out var c) ? c.Completed : 0).DefaultIfEmpty(PidTune.Cycles).Min();
        var steps = Enumerable.Range(1, PidTune.Cycles).Select(i => new PidStepVm("第 " + i.ToString(Inv) + " 次振荡", i <= cyc, i == cyc + 1)).ToList();
        if (Steps.Count != steps.Count || !Steps.SequenceEqual(steps))
        {
            Steps.Clear();
            foreach (var x in steps) Steps.Add(x);
        }

        // 结果
        var res = run.Ch.Where(run.Results.ContainsKey).Select(n => run.Results[n]).ToList();
        if (Results.Count != res.Count || !Results.SequenceEqual(res))
        {
            Results.Clear();
            foreach (var r in res) Results.Add(r);
        }
        HasOkNote = done && res.Count > 0 && res.Any(r => r.Ok);
        OkNote = PidTexts.OkNote(run.BandName, run.Lo, run.Hi);
    }

    /// <summary>这次整定第 n 路的曲线（曲线缓存里开始之后、<paramref name="end"/> 之前的采样）。</summary>
    private static List<TuneSample> Samples(DeviceVm d, TuneRun run, int n, DateTime end)
    {
        var list = new List<TuneSample>();
        var h = d.Source.History.Channel(n);
        if (h == null) return list;
        var t0 = run.Start.ToUniversalTime();
        var t1 = end.ToUniversalTime().AddSeconds(1);
        for (int i = h.LowerBound(t0); i < h.Count; i++)
        {
            var s = h[i];
            if (s.T > t1) break;
            if (s.Gap) { list.Add(new TuneSample((s.T - t0).TotalMinutes, null, null)); continue; }
            list.Add(new TuneSample((s.T - t0).TotalMinutes, s.TempC, s.HeaterPct));
        }
        return list;
    }

    // ———————————————————— 操作 ————————————————————

    private void PickBand(int k)
    {
        var d = _dev();
        if (d == null) return;
        var st = State(d);
        if (st.Data != null && BandsOf(st.Data).FirstOrDefault(b => b.Index == k) is { } b && !PidTune.Range(b, TMax(d)).Usable)
        {
            _shell.Toast("超过本机温度上限 " + PidTexts.Num(TMax(d)) + " °C · 这一档不能用", ToastKind.Info);
            return;
        }
        SetBand(k, keepRun: false);
        Render();
    }

    /// <summary>换档：清掉勾选、整定温度回到这一档的默认；给出过的结果不再留着（正在整定的照旧）。</summary>
    private void SetBand(int k, bool keepRun)
    {
        var d = _dev();
        if (!keepRun && d != null && State(d).Run is { Done: true } ) State(d).Run = null;
        bool changed = Band != k;
        Band = k;
        if (!changed && keepRun) return;
        _sel.Clear();
        if (d != null && State(d).Data is { } data && BandsOf(data).FirstOrDefault(b => b.Index == k) is { } b)
        {
            bool was = _syncing;
            _syncing = true;
            SpText = PidTexts.Num(PidTune.DefaultTemp(b, TMax(d)));
            _syncing = was;
        }
    }

    private void Check(PidRowVm row)
    {
        if (row.Checked && row.CanCheck) _sel.Add(row.N);
        else _sel.Remove(row.N);
        Render();
    }

    /// <summary>勾选 / 取消一路（测试、地址参数 pidsel）。</summary>
    public void Select(int n, bool on = true)
    {
        if (on) _sel.Add(n); else _sel.Remove(n);
        Render();
    }

    /// <summary>
    /// 「开始自整定（n 路）」：每路 StartAutotuneAsync(n, 整定温度, 档)（同时发，几路一起整定）；成功的几路成为这一次整定（开始时刻 = 此刻，记下整定前的 PID），
    /// 失败的几路各给一条原因。之后重读 PID。
    /// </summary>
    public async Task StartAsync()
    {
        var d = _dev();
        if (d == null) return;
        Render();
        if (StartReason is { } why) { _shell.Toast(why, ToastKind.Warn); return; }
        var port = PortFor(d);
        var st = State(d);
        if (port == null || st.Data == null) return;
        var band = BandsOf(st.Data).First(b => b.Index == Band);
        double sp = double.Parse(SpText.Trim(), NumberStyles.Float, Inv);
        var chosen = _sel.OrderBy(n => n).ToList();
        Starting = true;
        try
        {
            var tasks = chosen.Select(async n =>
            {
                try { await port.StartAsync(n, sp, band.Index, CancellationToken.None).ConfigureAwait(true); return (n, (string?)null); }
                catch (Exception ex) { return (n, (string?)DeviceActions.Describe(ex)); }
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(true);
            var ok = results.Where(r => r.Item2 == null).Select(r => r.n).ToList();
            foreach (var (n, err) in results.Where(r => r.Item2 != null))
                _shell.Toast(d.Alias + " RV" + n.ToString(Inv) + " 没有开始自整定：" + err, ToastKind.Err);
            if (ok.Count > 0)
            {
                var run = new TuneRun
                {
                    Band = band.Index, BandName = band.Name, Lo = band.LoC, Hi = band.HiC, Sp = sp,
                    Start = ok.Select(n => port.StartedAt(n)).FirstOrDefault(t => t != null) ?? d.Source.History.Now,
                };
                foreach (var n in ok) { run.Ch.Add(n); AddOld(run, st.Data, n); }
                st.Run = run;
                _sel.Clear();
                _shell.Toast("开始自整定：" + d.Alias + " " + PidTexts.Rvs(ok) + " · " + band.Name + " · " + PidTexts.Num(sp) + " °C", ToastKind.Ok);
            }
        }
        finally { Starting = false; }
        Read(d);
        Poll(d);
        Render();
    }

    /// <summary>「取消整定」确认之后（PidCancelDialogVm）：每路 CancelAutotuneAsync（停止类，不要控制权）；整定拿掉，PID 不改。</summary>
    public async Task CancelAsync(DeviceVm d)
    {
        var st = State(d);
        var run = st.Run;
        var port = PortFor(d);
        if (run == null || run.Done || port == null) return;
        var chs = run.Ch.Where(n => !run.Ended.ContainsKey(n)).ToList();
        st.Run = null;
        st.Ignore.UnionWith(chs);
        var results = await Task.WhenAll(chs.Select(async n =>
        {
            try { await port.CancelAsync(n, CancellationToken.None).ConfigureAwait(true); return (string?)null; }
            catch (Exception ex) { return "RV" + n.ToString(Inv) + "：" + DeviceActions.Describe(ex); }
        })).ConfigureAwait(true);
        var errs = results.Where(e => e != null).ToList();
        if (errs.Count == 0) _shell.Toast("已取消自整定 · " + d.Alias + " " + PidTexts.Rvs(chs) + " 停止加热，PID 未改", ToastKind.Info);
        else _shell.Toast(d.Alias + " 取消自整定失败：" + string.Join("；", errs), ToastKind.Err);
        Read(d);
        Render();
    }

    /// <summary>手改 P / I / D（失焦 / 回车）：三个数都对、和设备的不一样 → 写入这一档（SetPidAsync，要控制权），写完重读；不对 → 提示并恢复。</summary>
    private void Commit(PidRowVm row) => _ = CommitAsync(row);

    public async Task<bool> CommitAsync(PidRowVm row)
    {
        var d = _dev();
        if (d == null) return false;
        var st = State(d);
        var cell = st.Data == null ? null : Cell(st.Data, row.N, Band);
        if (cell == null) return false;
        bool Num(string s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, Inv, out v) && double.IsFinite(v);
        if (!Num(row.P, out var p) || !Num(row.I, out var i) || !Num(row.D, out var dd) || p <= 0 || i < 0 || dd < 0)
        {
            _shell.Toast(row.Rv + "：P 须大于 0，I、D（s）不能小于 0", ToastKind.Warn);
            Render();
            return false;
        }
        if (Same(p, cell.P) && Same(i, cell.I) && Same(dd, cell.D)) return false;
        if (row.EditReason is { } why) { _shell.Toast(why, ToastKind.Warn); Render(); return false; }
        var port = PortFor(d);
        if (port == null) return false;
        int band = Band;
        var what = row.Rv + " " + BandName + " PID（P " + PidTexts.P(p) + " · I " + PidTexts.Sec(i) + " s · D " + PidTexts.Sec(dd) + " s）";
        row.Pending = true;
        try
        {
            await port.SetPidAsync(row.N, band, p, i, dd, CancellationToken.None).ConfigureAwait(true);
            _shell.Toast(d.Alias + " · 已写入 " + what, ToastKind.Ok);
            return true;
        }
        catch (Exception ex)
        {
            _shell.Toast(d.Alias + " · PID 写入失败：" + DeviceActions.Describe(ex), ToastKind.Err);
            return false;
        }
        finally
        {
            row.Pending = false;
            Read(d);
            Render();
        }
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

    // ———————————————————— 地址参数 / 还原 ————————————————————

    /// <summary>回到刚打开时的样子：中温档、没有勾选、整定温度 150；忘掉读到的数据和给出的结果（截图前；演示数据会整个重来）。</summary>
    public void Reset()
    {
        _states.Clear();
        _sel.Clear();
        _syncing = true;
        Band = 1;
        SpText = "150";
        _syncing = false;
        _wasVisible = false;
        _lastDev = null;
    }

    /// <summary>
    /// 地址参数（同样稿）：<c>pidsel=5[,6]</c> 勾选；<c>tune=run</c> = 演示的 RV5 在中温档 150 °C 整定了 26 min；<c>tune=done</c> = 整定了 36 min 刚结束（给出结果）。
    /// </summary>
    public void ApplyParams(Func<string, string?> x)
    {
        Reset();
        var d = _dev();
        if (d == null) return;
        Refresh();
        if (x("pidsel") is { Length: > 0 } ps)
            foreach (var part in ps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, NumberStyles.Integer, Inv, out var n)) _sel.Add(n);
        if (x("tune") is "run" or "done" && d.Source is DemoDeviceSource demo)
        {
            bool done = x("tune") == "done";
            demo.Fleet.SeedAutotune(demo.Device, 5, 150, 1, done ? 36 : 26);
            Refresh();
            if (done)
            {
                _quiet = true;
                try
                {
                    demo.Fleet.FinishAutotune(demo.Device, 5);   // 演示数据变了会同步刷新页面：先静音
                    Refresh();
                }
                finally { _quiet = false; }
            }
        }
        Refresh();
    }
}
