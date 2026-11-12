using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 反应釜页的「程序」面板（样稿 js/pages/reactor.js progPanel / progStatus / flowHTML / condHTML / safetyHTML / noProgHTML；PORT-PLAN §5 M5「程序面板」）。
/// 一个反应釜页一个实例，身份不变；页面每次刷新调 <see cref="Refresh"/> 就地更新（R10）。
/// <list type="bullet">
/// <item>面包屑（视图 ReactorCrumb）：「1号机 › RV1 › 加氢-标准」—— 别名先让位、再省略程序名，「RVn ›」从不切掉；悬停全文 <see cref="CrumbTip"/>。</item>
/// <item>画布顶部一条：状态（点 + 字，宽画布 <see cref="StatusLong"/>、窄画布 <see cref="StatusShort"/>）+ 工具：定位当前步（运行中 / 报警暂停 / 中止才有）、
///   放大（1 → 1.15 → 1.3，按放大后的尺寸重排：每行的卡片变少；一行放不下一张最窄的卡时不能再放大）、缩小、适应（回到 100 % 并定位当前步）。</item>
/// <item>步骤卡片（<see cref="Steps"/>，<see cref="StepCardVm"/>）+ 条件框 + 安全联锁框（<see cref="SafeValue"/> …）；断线时去色（<see cref="Stale"/>）。</item>
/// <item>没有程序（<see cref="HasNoProgram"/>）：程序库（单击一行预览；有控制权时每行一个「下发」，没有时原因写在列表下面一次）+ 选中程序的只读预览；
///   从未连上的设备只有一句说明。</item>
/// <item>真机的程序对不上台账（R5，<see cref="Degraded"/>）：只写设备报的 k/n、状态与消息，不显示步骤与剩余时间（绝不把过期的草稿当成正在运行的程序）。</item>
/// </list>
/// 每行几张卡、卡片多宽由视图按画布的实际宽度算（Core 的 <see cref="StepFlowLayout"/>）；放大档的可用性要画布宽度，视图经 <see cref="SetCanvasWidth"/> 告诉这里。
/// 纯视图模型（App.Tests 直接测）。
/// </summary>
public sealed partial class ReactorProgramVm : ObservableObject
{
    private DeviceVm? _d;
    private string _progKey = "", _previewKey = "";
    private double _outer;

    public ReactorProgramVm(int k, int n)
    {
        K = k;
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        LocateCommand = new RelayCommand(() => LocateRequested?.Invoke(this, true));
        ZoomInCommand = new RelayCommand(() => { if (ZoomInReason is null) SetZoom(Zoom + 1); });
        ZoomOutCommand = new RelayCommand(() => { if (ZoomOutReason is null) SetZoom(Zoom - 1); });
        FitCommand = new RelayCommand(() => SetZoom(0, force: true));
    }

    public int K { get; }
    public int N { get; }
    /// <summary>'RV1'（面包屑中间那一段，从不切掉）。</summary>
    public string Rv { get; }

    // ———————————————————— 面包屑 ————————————————————

    [ObservableProperty] private string _alias = "";
    /// <summary>程序名（没有程序「无程序」；真机对不上台账「RV3 程序」）。</summary>
    [ObservableProperty] private string _progName = ProgramPanelText.NoProgram;
    /// <summary>面包屑全文：'1号机 › RV1 › 加氢-标准'。</summary>
    [ObservableProperty] private string _crumbTip = "";
    /// <summary>面板名（关闭提示「已关闭「程序 · 加氢-标准」」）。</summary>
    [ObservableProperty] private string _panelName = ProgramPanelText.PanelName(null);

    // ———————————————————— 状态行 + 工具 ————————————————————

    /// <summary>有程序（含对不上台账的降级）：画布顶部有状态行（工具只在有步骤卡片时，<see cref="ShowFlow"/>）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFlow), nameof(ZoomShown))] private bool _hasProgram;

    /// <summary>画布上是步骤卡片（有程序、而且不是对不上台账的降级）。</summary>
    public bool ShowFlow => HasProgram && !Degraded;
    [ObservableProperty] private bool _hasStatus;
    [ObservableProperty] private StateKey _statusKey = StateKey.Idle;
    [ObservableProperty] private string _statusWord = "";
    /// <summary>宽画布（程序面板 ≥ 460）状态字后面的说明：'第 3/4 步 · 预计 11:09 结束'（没有时为空）。</summary>
    [ObservableProperty] private string _statusFull = "";
    /// <summary>窄画布：状态字（运行中不写，<see cref="StatusShortWord"/> 为空）+ 短说明。</summary>
    [ObservableProperty] private string _statusShortWord = "";
    [ObservableProperty] private string _statusShortText = "";
    /// <summary>悬停全文：'运行中 · 第 3/4 步 · 预计 11:09 结束'。</summary>
    [ObservableProperty] private string _statusTitle = "";

    /// <summary>「定位当前步」：运行中 / 报警暂停 / 中止（样稿 canLocate）。</summary>
    [ObservableProperty] private bool _canLocate;
    /// <summary>放大档 0 / 1 / 2（1 / 1.15 / 1.3）。</summary>
    [ObservableProperty] private int _zoom;
    [ObservableProperty] private string? _zoomInReason;
    [ObservableProperty] private string? _zoomOutReason = StepFlowLayout.ZoomOutReason(0);
    /// <summary>放大时写在工具左边：'115 %'；100 % 时 null。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomShown))] private string? _zoomText;
    /// <summary>画布上显示的放大倍数：只在有步骤卡片时（<see cref="ShowFlow"/>；对不上台账的降级显示没有工具，复核）。</summary>
    public string? ZoomShown => ShowFlow ? ZoomText : null;

    public ICommand LocateCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand FitCommand { get; }

    /// <summary>要定位当前步：true = 强制（定位 / 放大 / 缩小 / 适应），false = 自动（只在不可见时滚）。视图处理。</summary>
    public event EventHandler<bool>? LocateRequested;

    // ———————————————————— 步骤 ————————————————————

    /// <summary>步骤卡片（身份 = 程序 + 第几步；换了程序才整个换，一次换完）。</summary>
    public StepCardList Steps { get; } = new();
    /// <summary>当前步或中止的那一步（自动滚动 / 定位的目标）；没有为 −1。</summary>
    [ObservableProperty] private int _curIndex = -1;
    /// <summary>自动滚动的键（样稿 scrollKey 去掉每行张数和放大档 —— 那两样视图自己加）：设备 / 反应釜 / 程序 / 当前步 / 状态。</summary>
    [ObservableProperty] private string _scrollKey = "";
    /// <summary>设备断线：卡片与进度去色（样稿 .reactor-flow.is-stale）。</summary>
    [ObservableProperty] private bool _stale;

    // ———— 安全联锁框（画布最后，样稿 safetyHTML） ————
    [ObservableProperty] private string _safeRv = "";
    [ObservableProperty] private string _safeValue = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SafeErr), nameof(SafeMute))] private SafetyTone _safeTone;
    public bool SafeErr => SafeTone == SafetyTone.Err;
    public bool SafeMute => SafeTone == SafetyTone.Mute;
    /// <summary>说明（原文；视图做断行保护 UiText.Keep）。</summary>
    [ObservableProperty] private string _safeNote = "";

    // ———— 真机对不上台账（R5） ————
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFlow), nameof(ZoomShown))] private bool _degraded;
    [ObservableProperty] private string _degradedText = "";

    // ———————————————————— 没有程序 ————————————————————

    [ObservableProperty] private bool _hasNoProgram;
    /// <summary>从未连上的设备：只有一句说明（没有程序库）。</summary>
    [ObservableProperty] private bool _neverConnected;
    [ObservableProperty] private string _noProgTitle = "";
    [ObservableProperty] private string _noProgLead = "";
    /// <summary>程序库（身份 = 程序 id）。</summary>
    public ObservableCollection<LibRowVm> Library { get; } = new();
    /// <summary>程序库有程序（列表框才显示）。</summary>
    [ObservableProperty] private bool _hasLibrary;
    /// <summary>没有控制权时写在列表下面一次的原因（样稿 reasonLine(guard(d, 'control'))）；能下发为 null。</summary>
    [ObservableProperty] private string? _libReason;
    /// <summary>选中的程序 id（单击一行预览）。</summary>
    [ObservableProperty] private string? _libSel;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewTitle = "";
    /// <summary>预览的步骤（只读，虚线卡；换了选中的程序一次换完）。</summary>
    public StepCardList Preview { get; } = new();

    /// <summary>画布里的卡片现在有几步（放大 / 每行张数用：有程序 = 程序的步数，没有 = 预览的步数）。</summary>
    public int FlowSteps => HasProgram ? Steps.Count : Preview.Count;

    // ———————————————————— 刷新 ————————————————————

    /// <summary>像新打开的页面（样稿 render：放大回到 100 %、程序库选第一项、画布滚回顶部、自动滚动重来）。</summary>
    public void ResetView()
    {
        LibSel = null;
        Zoom = 0;
        SyncZoom();
        ScrollKey = "";
        ViewReset?.Invoke(this, EventArgs.Empty);
        if (_d != null) Refresh(_d);
    }

    /// <summary>
    /// 像新打开的页面（<see cref="ResetView"/>）：视图把画布滚回顶部、忘掉上一次自动滚动的键（之后按当前步重新判断）——
    /// 否则上一个截图场景的滚动位置会留下来（自动滚动只在当前步看不见时才滚）。
    /// </summary>
    public event EventHandler? ViewReset;

    /// <summary>
    /// 画布滚动区的外宽（含 8 px 滚动条，样稿 cv.offsetWidth）：放大档能不能用、面板变窄时放大回到 100 %（样稿 measure）。只在变了的时候调。
    /// </summary>
    public void SetCanvasWidth(double outer)
    {
        if (Math.Abs(outer - _outer) < 0.01) return;
        _outer = outer;
        if (Zoom != 0 && !StepFlowLayout.ZoomFits(outer, Zoom)) Zoom = 0;
        SyncZoom();
    }

    /// <summary>画布的外宽（视图给的；0 = 还不知道）。</summary>
    public double CanvasWidth => _outer;

    private void SetZoom(int z, bool force = true)
    {
        Zoom = Math.Clamp(z, 0, StepFlowLayout.Zooms.Count - 1);
        SyncZoom();
        LocateRequested?.Invoke(this, force);
    }

    private void SyncZoom()
    {
        ZoomInReason = StepFlowLayout.ZoomInReason(_outer, Zoom);
        ZoomOutReason = StepFlowLayout.ZoomOutReason(Zoom);
        ZoomText = Zoom == 0 ? null : StepFlowLayout.ZoomText(Zoom);
    }

    /// <summary>外框每次数据变化后（页面显示着时）就地刷新。</summary>
    public void Refresh(DeviceVm? d)
    {
        _d = d;
        var r = d?.Reactor(N);
        if (d is null || r is null || !r.Installed)
        {
            Alias = d?.Alias ?? "";
            ClearProgram();
            HasNoProgram = false;
            return;
        }
        var src = d.Source;
        var model = src.Model;
        bool online = !d.IsOffline;
        Alias = d.Alias;
        var p = r.Program?.Record;
        var pr = r.Program?.Progress;
        var hmi = p is null && !src.IsDemo ? r.Hmi : null;
        string name = p?.Name ?? (hmi is { StepCount: > 0 } ? HmiProgramText.Name(N) : ProgramPanelText.NoProgram);
        ProgName = name;
        CrumbTip = ProgramPanelText.CrumbTitle(d.Alias, N, p is null && hmi is not { StepCount: > 0 } ? null : name);
        PanelName = ProgramPanelText.PanelName(p is null && hmi is not { StepCount: > 0 } ? null : name);
        var dataText = DeviceTexts.DataText(src.DataAt, d.EverOnline);

        if (p is not null)
        {
            HasNoProgram = false;
            Degraded = false;
            HasProgram = true;
            ApplyStatus(ProgramPanelText.Status(p, pr, online, dataText, d.ContextFor(N).Now));
            CanLocate = p.Status is ProgramStatus.Running or ProgramStatus.Held or ProgramStatus.Aborted or ProgramStatus.Failed;
            Stale = !online;
            SyncCards(Steps, ref _progKey, p, model, d.Alias, pr, online, preview: false);
            int cur = -1;
            for (int i = 0; i < p.Steps.Count; i++)
            {
                var s = Progress.StepState(p, i);
                if (s is SegmentState.Cur or SegmentState.Abort) { cur = i; break; }
            }
            CurIndex = cur;
            ScrollKey = K.ToString(CultureInfo.InvariantCulture) + "/" + N.ToString(CultureInfo.InvariantCulture) + "/" + (p.Id ?? p.Name) + "/" + p.StepIndex.ToString(CultureInfo.InvariantCulture) + "/" + p.Status;
            ApplySafety(d, r, model, online);
            ClearLibrary();
            return;
        }

        if (hmi is { StepCount: > 0 } h)
        {
            // 真机：设备上有程序，但台账对不上（或不是本机下发的）—— 只写 HMI 报的进度（R5）
            HasNoProgram = false;
            HasProgram = true;
            Degraded = true;
            var snap = new ParallelReactor.Api.ChannelProgramSnap(h.Running, h.StepIndex, h.StepCount, h.State, h.Message, h.StartedBy, h.StartedAt, h.WaitingGas);
            ApplyStatus(ProgramPanelText.HmiStatus(snap, online, dataText));
            DegradedText = ReactorPageText.DegradedText((src as LiveDeviceSource)?.LedgerReason(N));
            CanLocate = false;
            Stale = !online;
            if (Steps.Count > 0) Steps.Clear();
            _progKey = "";
            CurIndex = -1;
            ScrollKey = "";
            ApplySafety(d, r, model, online);
            ClearLibrary();
            return;
        }

        // 没有程序：程序库 + 预览（从未连上的设备只有一句说明）
        HasProgram = false;
        Degraded = false;
        HasStatus = false;
        CanLocate = false;
        if (Steps.Count > 0) Steps.Clear();
        _progKey = "";
        CurIndex = -1;
        ScrollKey = "";
        HasNoProgram = true;
        NoProgTitle = ReactorPageText.NoProgramTitle(N);
        NeverConnected = !d.EverOnline;
        if (NeverConnected)
        {
            NoProgLead = ReactorPageText.NeverConnectedLead(d.Alias);
            ClearLibrary();
            return;
        }
        NoProgLead = ReactorPageText.LibraryLead(d.Alias, model.Label);
        var lib = src.Library(N);
        var sel = lib.FirstOrDefault(x => x.Id == LibSel) ?? lib.FirstOrDefault();
        // 每行的「下发」只在有控制权时出现；没有控制权 / 断线时只留选择与预览，原因写在列表下方一次（样稿 noProgHTML）
        string ctl = d.Actions.GuardText(GuardKind.Control);
        LibReason = ctl.Length == 0 ? null : ctl;
        SyncLibrary(d, lib, sel, ctl.Length == 0);
        if (sel is null)
        {
            HasPreview = false;
            if (Preview.Count > 0) Preview.Clear();
            _previewKey = "";
            return;
        }
        HasPreview = true;
        PreviewTitle = ReactorPageText.PreviewTitle(sel.Name);
        SyncCards(Preview, ref _previewKey, ReactorPageText.Preview(sel), model, d.Alias, null, online, preview: true);
        OnPropertyChanged(nameof(FlowSteps));
    }

    private void ApplyStatus(ProgramStatusLine? s)
    {
        HasStatus = s is not null;
        if (s is null) return;
        StatusKey = s.Key;
        StatusWord = s.Word;
        StatusFull = s.Full;
        StatusShortWord = s.NoWord ? "" : s.Word;
        StatusShortText = s.ShortText;
        StatusTitle = s.Title;
    }

    private void ApplySafety(DeviceVm d, ReactorVm r, ModelProfile model, bool online)
    {
        var src = d.Source;
        string? err = null;
        foreach (var a in d.AlarmList)
            if (a.Ch == N && a.IsErr) { err = a.Text; break; }
        var box = ProgramPanelText.Safety(N, model, online, src.DataAt, src.Estop.Active, r.Own == StateKey.Alarm, err);
        SafeRv = box.RvText;
        SafeValue = box.ValueText;
        SafeTone = box.Tone;
        SafeNote = box.Note;
    }

    /// <summary>
    /// 卡片按身份就地更新：程序（id / 名称 / 步数）换了才整个换（新的一批卡片，自动滚动重来）。
    /// 新的一批先填好内容、再一次换进集合（一个 Reset）：画布只同步一次，卡片建出来就是对的内容（复核 c2-ui-1：以前 Clear + 逐张 Add，画布每加一张同步一遍）。
    /// </summary>
    private void SyncCards(StepCardList list, ref string key, ProgramRecord p, ModelProfile model, string alias, ProgressInfo? pr, bool online, bool preview)
    {
        string k = (p.Id ?? "") + "|" + p.Name + "|" + p.Steps.Count.ToString(CultureInfo.InvariantCulture);
        bool fresh = k != key || list.Count != p.Steps.Count;
        IReadOnlyList<StepCardVm> cards = list;
        if (fresh)
        {
            var made = new StepCardVm[p.Steps.Count];
            for (int i = 0; i < made.Length; i++) made[i] = new StepCardVm(i);
            cards = made;
        }
        foreach (var c in cards) c.Apply(p, model, N, alias, pr, online, preview, plain: false);
        if (!fresh) return;
        key = k;
        list.ReplaceAll(cards);
        OnPropertyChanged(nameof(FlowSteps));
    }

    private void SyncLibrary(DeviceVm d, IReadOnlyList<ProgramDef> lib, ProgramDef? sel, bool canLoad)
    {
        for (int i = Library.Count - 1; i >= 0; i--)
            if (!lib.Any(x => x.Id == Library[i].Id)) Library.RemoveAt(i);
        for (int i = 0; i < lib.Count; i++)
        {
            var def = lib[i];
            int at = -1;
            for (int j = 0; j < Library.Count; j++) if (Library[j].Id == def.Id) { at = j; break; }
            LibRowVm row;
            if (at < 0) { row = new LibRowVm(this, def.Id); Library.Insert(Math.Min(i, Library.Count), row); }
            else
            {
                row = Library[at];
                if (at != i) Library.Move(at, i);
            }
            row.Name = def.Name;
            row.Note = ReactorPageText.LibraryNote(def);
            row.NoteTip = def.Note ?? "";
            row.IsSelected = ReferenceEquals(def, sel);
            row.ShowLoad = canLoad;
            string why = canLoad ? d.Actions.Why(DeviceAction.ProgLoad, new ActionPayload(N: N, Id: def.Id)) : "";
            row.LoadReason = why.Length == 0 ? null : why;
        }
        HasLibrary = Library.Count > 0;
    }

    private void ClearLibrary()
    {
        if (Library.Count > 0) Library.Clear();
        HasLibrary = false;
        LibReason = null;
        HasPreview = false;
        if (Preview.Count > 0) Preview.Clear();
        _previewKey = "";
    }

    private void ClearProgram()
    {
        HasProgram = false;
        HasStatus = false;
        Degraded = false;
        CanLocate = false;
        if (Steps.Count > 0) Steps.Clear();
        _progKey = "";
        CurIndex = -1;
        ScrollKey = "";
        ClearLibrary();
    }

    /// <summary>单击程序库的一行：预览它（样稿 data-rv-lib）。</summary>
    internal void Pick(string id)
    {
        if (LibSel == id) return;
        LibSel = id;
        if (_d != null) Refresh(_d);
    }

    /// <summary>程序库一行的「下发」（需要控制权，D6 的「下发」；执行时守卫再查一次）。</summary>
    internal void Load(string id)
    {
        if (_d is { } d) _ = d.Actions.RunAsync(DeviceAction.ProgLoad, new ActionPayload(N: N, Id: id));
    }
}

/// <summary>程序库的一行（样稿 .reactor-lib-row）：名称、说明 · 步数（悬停说明原文）、选中、「下发」（有控制权时才有；不能下发时带原因）。</summary>
public sealed partial class LibRowVm : ObservableObject
{
    public LibRowVm(ReactorProgramVm owner, string id)
    {
        Id = id;
        PickCommand = new RelayCommand(() => owner.Pick(Id));
        LoadCommand = new RelayCommand(() => owner.Load(Id));
    }

    public string Id { get; }
    public ICommand PickCommand { get; }
    public ICommand LoadCommand { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _noteTip = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _showLoad;
    [ObservableProperty] private string? _loadReason;
}

/// <summary>步骤卡片的集合（程序面板的 <see cref="ReactorProgramVm.Steps"/> / <see cref="ReactorProgramVm.Preview"/>）：换程序时整批换掉，只发一个 Reset。</summary>
public sealed class StepCardList : ObservableCollection<StepCardVm>
{
    /// <summary>整批换成 <paramref name="items"/>：一个 Reset（Count、索引器各通知一次）—— 画布（StepFlowPanel）只同步一次。</summary>
    public void ReplaceAll(IEnumerable<StepCardVm> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var c in items) Items.Add(c);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
