using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>
/// 单路程序（融合版 F6，样稿 PAGES.prog 的 render）：
/// <list type="bullet">
/// <item>步骤面板：「共 9 步 · 约 10 h 31 min」+ 校验通过 / N 处待改 + 适用型号 + 上移 / 下移 / 删除；在跑的程序锁住（琥珀条 + 复制为新程序）；
///   从旧格式转换来的程序顶上一条「已从旧格式转换，请检查」（STEPS-SPEC §3）；预计温度 / 压力曲线（上下两张，不用双轴）；
///   添加步骤（程序步骤 v0.8 的 8 种，插在「结束」之前）；步骤列表（序号、类型、名字、两项摘要、待改、估计时长：按条件「约」、反应「≤」）；
///   待改项清单（含下发前检查的「拒绝」）与提示（反应前没有充压、先充压后升温、没有放空，STEPS-SPEC §4）。</item>
/// <item>「结束」固定在最后：不能删、不能挪，别的步骤也不能挪到它后面。</item>
/// <item>参数面板：选中那一步的名字和参数（来自设备的程序 schema：单位、范围；压力按设置的单位，MPa / bar 时并写约合 psi；按 visibleWhen 只显示用得到的字段）；曲线升温的段表。</item>
/// <item>下发面板：通道小块（在跑 / 停用的不能选）、选全部空闲 / 清空、溶液体积（所选各路相同，0.1 mL，必填）、预运行检查、下发并运行。</item>
/// </list>
/// </summary>
public sealed partial class OneVm : ObservableObject
{
    private readonly ProgramPageVm _page;
    private DeviceVm? _d;
    private ProgramSchema? _schema;
    private string _fieldKey = "";
    private readonly SortedSet<int> _tch = new() { 5 };
    private int _tchDev = 1;

    public OneVm(ProgramPageVm page)
    {
        _page = page;
        UpCommand = new RelayCommand(() => Move(-1));
        DownCommand = new RelayCommand(() => Move(1));
        DeleteCommand = new RelayCommand(Delete);
        DupCommand = new RelayCommand(page.Duplicate);
        IdleCommand = new RelayCommand(SelectIdle);
        ClearCommand = new RelayCommand(() => { _tch.Clear(); Refresh(); });
        CheckCommand = new RelayCommand(page.OpenCheck);
        RunCommand = new AsyncRelayCommand(SendAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AddSegCommand = new RelayCommand(AddSeg);
        // 「结束」固定在最后、只有一个（STEPS-SPEC §0），不在添加列表里
        AddTypes = StepTypeCodes.All.Where(t => t != StepType.End).Select(t => new AddStepVm(t, () => Add(t))).ToArray();
    }

    /// <summary>选中的步骤（样稿 PG.step，默认第 3 步）。</summary>
    public int StepIndex { get; set; } = 2;

    /// <summary>像新打开（截图）：第 3 步、只选 RV5、溶液体积 5.0。</summary>
    public void Reset()
    {
        StepIndex = 2;
        _tch.Clear();
        _tch.Add(5);
        _tchDev = 1;
        VolText = "5.0";
        _fieldKey = "";
    }

    // ———————————————————— 步骤面板 ————————————————————

    [ObservableProperty] private string _progName = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private bool _valid = true;
    [ObservableProperty] private string _issueTag = "";
    [ObservableProperty] private string _appliesTo = "";
    [ObservableProperty] private string? _upReason, _downReason, _deleteReason;
    [ObservableProperty] private bool _locked;
    [ObservableProperty] private string _lockText = "";
    [ObservableProperty] private ProgramPlanResult? _plan;
    [ObservableProperty] private int _planSel;
    public IReadOnlyList<AddStepVm> AddTypes { get; }
    public ObservableCollection<StepRowVm> Steps { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    [ObservableProperty] private bool _hasIssues;
    /// <summary>下发前检查的提示（不拦：反应前没有充压、先充压后升温、没有放空）。</summary>
    public ObservableCollection<string> Hints { get; } = new();
    [ObservableProperty] private bool _hasHints;
    /// <summary>从旧格式转换来的（编辑器顶上的提示条，STEPS-SPEC §3）。</summary>
    [ObservableProperty] private bool _migrated;
    /// <summary>转换时旧值超出新范围被收紧的说明（每处一行；没有为空串）。</summary>
    [ObservableProperty] private string _migNote = "";
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DupCommand { get; }

    // ———————————————————— 参数面板 ————————————————————

    [ObservableProperty] private string _paramSub = "";
    [ObservableProperty] private string _stepName = "";
    partial void OnStepNameChanged(string value)
    {
        if (_stepNameSync) return;
        if (_page.Sel is { } p && StepIndex < p.Steps.Count && p.Steps[StepIndex].Name != value && !Locked)
        {
            p.Steps[StepIndex].Name = value.Trim().Length > 0 ? value.Trim() : p.Steps[StepIndex].Name;
            _page.MarkDirty(p.Id);
            _page.Refresh(_page.Shell.Fleet);
        }
    }
    public ObservableCollection<FieldVm> Fields { get; } = new();
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _isCurve;
    public ObservableCollection<SegVm> Segments { get; } = new();
    public ICommand AddSegCommand { get; }
    [ObservableProperty] private bool _hasStep;

    // ———————————————————— 下发面板 ————————————————————

    public ObservableCollection<ChipVm> Chips { get; } = new();
    [ObservableProperty] private string _selCountText = "";
    [ObservableProperty] private string? _idleReason;
    /// <summary>溶液体积（mL，所选各路相同；空 = 没填）。</summary>
    [ObservableProperty] private string _volText = "5.0";
    partial void OnVolTextChanged(string value) { if (_d != null) Refresh(); }
    [ObservableProperty] private bool _volBad;
    [ObservableProperty] private string _volHint = "";
    /// <summary>下发面板顶上的说明条（本地只读 / 型号不对）；null = 没有。</summary>
    [ObservableProperty] private string? _sendBar;
    [ObservableProperty] private bool _sendBarAmb;
    [ObservableProperty] private string? _runReason;
    [ObservableProperty] private string _runTip = "下发并运行";
    [ObservableProperty] private string? _checkReason;
    [ObservableProperty] private string? _stopReason;
    public ICommand IdleCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand RunCommand { get; }

    /// <summary>勾选的通道（按通道号）。</summary>
    public IReadOnlyList<int> SelectedChannels => _tch.ToArray();

    /// <summary>溶液体积的值（NaN = 没填 / 不是数）。</summary>
    public double Vol => double.TryParse(VolText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>步进 0.1 mL（上下键 / 滚轮）。</summary>
    public void StepVol(int dir)
    {
        double v = double.IsFinite(Vol) ? Vol : 5;
        VolText = JsNumber.ToFixed(Math.Max(0, Math.Round(v + dir * 0.1, 1)), 1);
    }

    // ———————————————————— 刷新 ————————————————————

    internal void Refresh() { if (_d != null && _schema != null) Refresh(_d, _schema); }

    public void Refresh(DeviceVm d, ProgramSchema schema)
    {
        _d = d;
        _schema = schema;
        if (_tchDev != d.K) { _tchDev = d.K; _tch.Clear(); }
        var p = _page.Sel;
        if (p is null) { HasStep = false; return; }
        if (StepIndex >= p.Steps.Count) StepIndex = Math.Max(0, p.Steps.Count - 1);
        var used = _page.UsedBy(p.Id);
        Locked = used.Count > 0;
        LockText = Locked ? "正在 " + string.Join("、", used.Select(u => u.D.Alias).Distinct()) + " " + string.Join("、", used.Select(u => "RV" + u.N)) + " 运行 · 运行中的程序不能直接修改" : "";
        var iss = ProgramIssues.Of(p.Steps, schema, MatrixPlan.ModelLabelOf(p.Model));
        var plan = ProgramPlanner.Plan(p.Steps);
        ProgName = p.Name + (_page.IsDirty(p.Id) ? " · 未保存" : "");
        TotalText = "共 " + p.Steps.Count + " 步 · 约 " + ProgramPlanner.Hm(plan.Total);
        Valid = iss.Count == 0;
        IssueTag = Valid ? "校验通过" : iss.Count + " 处待改";
        AppliesTo = "适用 " + MatrixPlan.ModelLabelOf(p.Model);
        bool isEnd = StepIndex < p.Steps.Count && p.Steps[StepIndex].Type == StepType.End;
        bool beforeEnd = StepIndex + 1 < p.Steps.Count && p.Steps[StepIndex + 1].Type == StepType.End;
        UpReason = Locked ? "运行中的程序不能直接修改" : isEnd ? EndFixed : StepIndex <= 0 ? "已经是第一步" : null;
        DownReason = Locked ? "运行中的程序不能直接修改" : isEnd ? EndFixed : beforeEnd ? "「结束」固定在最后，不能挪到它后面" : StepIndex >= p.Steps.Count - 1 ? "已经是最后一步" : null;
        DeleteReason = Locked ? "运行中的程序不能直接修改" : isEnd ? EndFixed : p.Steps.Count < 2 ? "至少要留一步" : null;
        Migrated = p.Migrated;
        MigNote = p.Migrated ? string.Join("\n", p.MigrationNotes) : "";
        foreach (var a in AddTypes) { a.Reason = Locked ? "运行中的程序不能直接修改 · 先复制为新程序" : null; a.Label = schema.Of(a.Type)?.Label ?? a.Type.Zh(); }
        Plan = plan;
        PlanSel = StepIndex;
        SyncSteps(p, schema, plan, iss);
        Issues.Clear();
        foreach (var x in iss) Issues.Add(x.Msg);
        HasIssues = iss.Count > 0;
        Hints.Clear();
        foreach (var x in ProgramChecks.Of(p.Steps, schema).Where(x => x.Level == CheckLevel.Warn)) Hints.Add(x.Msg);
        HasHints = Hints.Count > 0;
        SyncParams(p, schema);
        SyncSend(d, p, iss.Count);
    }

    private void SyncSteps(PcProgram p, ProgramSchema schema, ProgramPlanResult plan, IReadOnlyList<ProgramIssue> iss)
    {
        while (Steps.Count > p.Steps.Count) Steps.RemoveAt(Steps.Count - 1);
        for (int i = 0; i < p.Steps.Count; i++)
        {
            if (i >= Steps.Count) Steps.Add(new StepRowVm(this, i));
            var s = p.Steps[i];
            var row = Steps[i];
            var kv = ProgramPlanner.Summary(s, schema);
            row.No = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            row.TypeLabel = schema.Of(s.Type)?.Label ?? s.Type.Zh();
            row.Name = s.Name;
            row.K1 = kv.Count > 0 ? kv[0].K : "";
            row.V1 = kv.Count > 0 ? kv[0].V : "";
            row.K2 = kv.Count > 1 ? kv[1].K : "";
            row.V2 = kv.Count > 1 ? kv[1].V : "";
            row.Bad = iss.Any(x => x.I == i);
            row.Dur = ProgramPlanner.Dur(plan.Bounds[i]);
            row.IsEnd = s.Type == StepType.End;
            row.IsSelected = i == StepIndex;
        }
    }

    internal void Select(int i)
    {
        if (StepIndex == i) return;
        StepIndex = i;
        Refresh();
    }

    private void SyncParams(PcProgram p, ProgramSchema schema)
    {
        HasStep = p.Steps.Count > 0;
        if (!HasStep) { Fields.Clear(); Segments.Clear(); return; }
        var s = p.Steps[StepIndex];
        var spec = schema.Of(s.Type);
        ParamSub = "第 " + (StepIndex + 1).ToString("00", CultureInfo.InvariantCulture) + " 步 · " + (spec?.Label ?? s.Type.Zh());
        if (StepName != s.Name) { _stepNameSync = true; StepName = s.Name; _stepNameSync = false; }
        Note = spec?.NoteFor(s);
        IsCurve = s.Type == StepType.Heat && s.S("mode") == "curve";
        IsEndStep = s.Type == StepType.End;
        // 压力单位换了（设置 → 常规）也重建：字段的单位、范围、小数位跟着变；显示哪些字段（visibleWhen）变了也重建
        string vis = spec is null ? "" : string.Concat(spec.Fields.Select(f => Shown(s, f) ? "1" : "0"));
        string key = p.Id + "|" + StepIndex + "|" + s.Type + "|" + vis + "|" + schema.GetHashCode() + "|" + PressureDisplay.Version;
        if (key != _fieldKey || Fields.Count != (spec?.Fields.Count(f => Shown(s, f)) ?? 0))
        {
            _fieldKey = key;
            Fields.Clear();
            if (spec != null) foreach (var f in spec.Fields.Where(f => Shown(s, f))) Fields.Add(new FieldVm(this, f));
            Segments.Clear();
        }
        foreach (var f in Fields) f.Apply(s, Locked);
        while (Segments.Count > (IsCurve ? s.Segments.Count : 0)) Segments.RemoveAt(Segments.Count - 1);
        if (IsCurve)
            for (int j = 0; j < s.Segments.Count; j++)
            {
                if (j >= Segments.Count) Segments.Add(new SegVm(this, j));
                Segments[j].Apply(s.Segments[j], spec?.SegmentFields, Locked);
            }
    }

    private bool _stepNameSync;

    /// <summary>选中的是「结束」（没有参数：参数面板写固定的收尾）。</summary>
    [ObservableProperty] private bool _isEndStep;

    private const string EndFixed = "「结束」固定在最后，不能删、不能挪";

    /// <summary>这个字段现在显示（visibleWhen：例如保持的目标温度只在「温度 ≥ / ≤」时有；曲线升温时定值的目标温度 / 速率 / 平衡换成段表）。</summary>
    private static bool Shown(PcStep s, FieldSpec f) => f.Visible(s);

    private void SyncSend(DeviceVm d, PcProgram p, int issues)
    {
        string ctl = d.Actions.GuardText(GuardKind.Control);
        bool can = ctl.Length == 0;
        bool same = d.Model.Key == p.Model;
        SendBar = !can ? d.Alias + "为本地只读 · 在工具条「申请控制」后才能下发"
            : !same ? "程序适用 " + MatrixPlan.ModelLabelOf(p.Model) + "，" + d.Alias + " 是 " + d.Model.Label + " · 先选同型号的程序" : null;
        SendBarAmb = can && !same;
        bool estop = d.Source.Estop.Active;
        var rs = d.Reactors.Where(r => r.Installed).ToList();
        while (Chips.Count > rs.Count) Chips.RemoveAt(Chips.Count - 1);
        for (int i = 0; i < rs.Count; i++)
        {
            if (i >= Chips.Count) Chips.Add(new ChipVm(this));
            var r = rs[i];
            bool busy = r.IsRunning || estop;
            bool off = r.Own == StateKey.Idle;
            bool dis = busy || off || !can || !same;
            if (dis) _tch.Remove(r.N);
            var c = Chips[i];
            c.N = r.N;
            c.Rv = r.Rv;
            c.Status = busy ? "运行中" : off ? "停用" : Home.RunRowText.Word(r.Own, r.Cause, r.OwnWord);
            c.Disabled = dis;
            c.Tip = busy ? r.Rv + " 正在运行「" + (r.Program?.Name ?? "程序") + "」" : off ? r.Rv + " 已停用 · 触屏启用后才能下发" : !can ? ctl : !same ? "型号不对" : "勾选 = 下发到 " + r.Rv;
            c.SetChecked(_tch.Contains(r.N));
        }
        SelCountText = "已选 " + _tch.Count + " 路";
        IdleReason = !can ? ctl : !same ? "型号不对" : null;
        string vb = MatrixPlan.VolBad(Vol, ProgramPageVm.VolMaxOf(d));
        VolBad = _tch.Count > 0 && vb.Length > 0;
        VolHint = VolBad ? vb : "所选各路相同 · 用于计算吸气量 · 每路不同请用条件矩阵";
        CheckReason = can ? null : ctl;
        RunReason = !can ? d.Alias + "为本地只读 · 先申请控制权"
            : estop ? d.Alias + "急停中 · 需在触屏复位"
            : !same ? "程序适用 " + MatrixPlan.ModelLabelOf(p.Model) + " · 先选同型号的程序"
            : _tch.Count == 0 ? "先在右下选择目标通道"
            : issues > 0 ? "先改掉待改项（" + issues + " 处）"
            : vb.Length > 0 ? "溶液体积" + vb : null;
        RunTip = _tch.Count > 0 ? "下发到 " + d.Alias + " " + _tch.Count + " 路并运行" : "下发并运行";
        StopReason = d.Source.Conn.CanStop ? null : d.Alias + "未连接";
    }

    internal void Toggle(int n, bool on)
    {
        if (on) _tch.Add(n); else _tch.Remove(n);
        Refresh();
    }

    /// <summary>
    /// 预选这些通道（融合版 F8：记录页「用这组参数再做一次」）：先清掉，再勾上其中现在能下发的（运行中 / 停用 / 型号不对的跳过）。返回实际勾上的。
    /// </summary>
    public IReadOnlyList<int> Preselect(IEnumerable<int> channels)
    {
        Refresh();
        _tch.Clear();
        foreach (var n in channels)
            if (Chips.FirstOrDefault(c => c.N == n) is { Disabled: false }) _tch.Add(n);
        Refresh();
        return _tch.OrderBy(n => n).ToList();
    }

    private void SelectIdle()
    {
        if (_d is null) return;
        foreach (var c in Chips) if (!c.Disabled) _tch.Add(c.N);
        Refresh();
    }

    // ———————————————————— 编辑 ————————————————————

    private PcProgram? Editable => !Locked && _page.Sel is { } p ? p : null;

    private void Touch(PcProgram p)
    {
        _page.MarkDirty(p.Id);
        _page.Refresh(_page.Shell.Fleet);
    }

    private void Add(StepType t)
    {
        if (Editable is not { } p || _schema is null || t == StepType.End) return;
        int at = Math.Min(StepIndex + 1, p.Steps.Count);
        // 插在「结束」之前（结束固定在最后）
        int end = p.Steps.FindIndex(x => x.Type == StepType.End);
        if (end >= 0 && at > end) at = end;
        p.Steps.Insert(at, PcStep.New(t, _schema));
        StepIndex = at;
        Touch(p);
    }

    private void Move(int dir)
    {
        if (Editable is not { } p) return;
        int j = StepIndex + dir;
        if (j < 0 || j >= p.Steps.Count) return;
        if (p.Steps[StepIndex].Type == StepType.End || p.Steps[j].Type == StepType.End) return;
        (p.Steps[StepIndex], p.Steps[j]) = (p.Steps[j], p.Steps[StepIndex]);
        StepIndex = j;
        Touch(p);
    }

    private void Delete()
    {
        if (Editable is not { } p || p.Steps.Count < 2 || p.Steps[StepIndex].Type == StepType.End) return;
        p.Steps.RemoveAt(StepIndex);
        StepIndex = Math.Max(0, StepIndex - 1);
        Touch(p);
    }

    internal void SetNum(string key, double v)
    {
        if (Editable is not { } p || StepIndex >= p.Steps.Count) return;
        var s = p.Steps[StepIndex];
        if (s.Num.TryGetValue(key, out var old) && (old.Equals(v) || (double.IsNaN(old) && double.IsNaN(v)))) return;
        s.Num[key] = v;
        Touch(p);
    }

    internal void SetSel(string key, string code)
    {
        if (Editable is not { } p || StepIndex >= p.Steps.Count) return;
        var s = p.Steps[StepIndex];
        if (s.S(key) == code) return;
        s.Sel[key] = code;
        if (s.Type == StepType.Heat && key == "mode" && code == "curve" && s.Segments.Count == 0) s.Segments.Add(new CurveSeg(s.N("temp", 80), 3, 20));
        Touch(p);
    }

    internal void SetSeg(int j, string key, double v)
    {
        if (Editable is not { } p || StepIndex >= p.Steps.Count) return;
        var s = p.Steps[StepIndex];
        if (j >= s.Segments.Count) return;
        var g = s.Segments[j];
        s.Segments[j] = key switch { "temp" => g with { Temp = v }, "rate" => g with { Rate = v }, _ => g with { Hold = v } };
        Touch(p);
    }

    internal void DelSeg(int j)
    {
        if (Editable is not { } p || StepIndex >= p.Steps.Count) return;
        var s = p.Steps[StepIndex];
        if (j < s.Segments.Count && s.Segments.Count > 1) { s.Segments.RemoveAt(j); Touch(p); }
    }

    private void AddSeg()
    {
        if (Editable is not { } p || StepIndex >= p.Steps.Count) return;
        var s = p.Steps[StepIndex];
        if (s.Segments.Count >= 16) { _page.Shell.Toast("曲线最多 16 段", ToastKind.Info); return; }
        var last = s.Segments.Count > 0 ? s.Segments[^1] : new CurveSeg(80, 3, 20);
        s.Segments.Add(last with { Hold = 30 });
        Touch(p);
    }

    // ———————————————————— 下发 ————————————————————

    /// <summary>下发并运行（样稿 pg-run）：所选各路 = 同一个程序 + 同一个溶液体积。</summary>
    public async Task SendAsync()
    {
        Refresh();
        if (_d is not { } d || _page.Sel is not { } p) return;
        if (RunReason is { } why) { _page.Shell.Toast(why, ToastKind.Warn); return; }
        var list = _tch.ToArray();
        double vol = Math.Round(Vol, 1);
        var r = await ProgramSender.SendAsync(d, list.Select(n => new SendItem(n, p, vol)).ToArray()).ConfigureAwait(true);
        var (text, kind) = ProgramSender.Summary(d, p.Name, r);
        foreach (var n in r.Started) _tch.Remove(n);
        _page.Shell.Toast(text, kind);
        _page.Refresh(_page.Shell.Fleet);
    }
}

/// <summary>「添加步骤」的一个按钮。</summary>
public sealed partial class AddStepVm : ObservableObject
{
    public AddStepVm(StepType t, Action add) { Type = t; _label = t.Zh(); Command = new RelayCommand(add); }
    public StepType Type { get; }
    public ICommand Command { get; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _reason;
}

/// <summary>步骤列表的一行（样稿 .pgs）。</summary>
public sealed partial class StepRowVm : ObservableObject
{
    public StepRowVm(OneVm owner, int i) { I = i; PickCommand = new RelayCommand(() => owner.Select(I)); }
    public int I { get; }
    public ICommand PickCommand { get; }
    [ObservableProperty] private string _no = "";
    [ObservableProperty] private string _typeLabel = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _k1 = "", _v1 = "", _k2 = "", _v2 = "";
    [ObservableProperty] private bool _bad;
    [ObservableProperty] private string _dur = "";
    [ObservableProperty] private bool _isSelected;
    /// <summary>「结束」（固定在最后：序号旁画锁）。</summary>
    [ObservableProperty] private bool _isEnd;
}

/// <summary>参数面板的一个字段（数值：文字 + 单位 + 范围；sel：下拉）。</summary>
public sealed partial class FieldVm : ObservableObject
{
    private readonly OneVm _owner;
    private bool _sync;

    public FieldVm(OneVm owner, FieldSpec f)
    {
        _owner = owner;
        Spec = f;
        Options = f.Options;
    }

    public FieldSpec Spec { get; }
    public string Label => Spec.Label;
    /// <summary>单位（压力按设置的压力单位）。</summary>
    public string Unit => Spec.ShowUnit;
    public bool IsSelect => Spec.IsSelect;
    public bool IsNum => !Spec.IsSelect;
    public IReadOnlyList<FieldOption> Options { get; }
    /// <summary>输入框宽：单位长（「mmol/min」「°C/min」）时加宽，数字不被单位挤掉（同一列里其余字段仍是 128）。</summary>
    public double InputWidth => Unit.Length > 5 ? 156 : 128;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private FieldOption? _selected;
    [ObservableProperty] private bool _bad;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeShown))] private string _range = "";

    /// <summary>
    /// 界面上的范围：压力的「≈ 363 psi」另起一行、不带括号（参数面板 1920 / 1366 下都放不下一行，Avalonia 又不在全角括号前断行，会从「–」处断开）；
    /// 两段各自用不断行的空格连起来。<see cref="Range"/> 仍是一行的原文（悬停、测试）。
    /// </summary>
    public string RangeShown
    {
        get
        {
            int i = Range.IndexOf("（≈ ", StringComparison.Ordinal);
            static string Nb(string t) => t.Replace(' ', '\u00A0');
            return i < 0 ? Range : Nb(Range[..i]) + "\n" + Nb(Range[(i + 1)..].TrimEnd('）'));   // 第二行「≈ 363 psi」（1366 下带全角括号放不下）
        }
    }
    [ObservableProperty] private bool _locked;

    partial void OnTextChanged(string value)
    {
        if (_sync) return;
        // 输入是界面单位（压力按设置：MPa 2 位、bar 1 位、psi 整数），存内部单位（压力 MPa）
        double v = double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? Spec.FromInput(x) : double.NaN;
        _owner.SetNum(Spec.Key, v);
    }

    partial void OnSelectedChanged(FieldOption? value)
    {
        if (_sync || value is null) return;
        _owner.SetSel(Spec.Key, value.Code);
    }

    public void Apply(PcStep s, bool locked)
    {
        _sync = true;
        Locked = locked;
        if (IsSelect)
        {
            var code = s.S(Spec.Key, Spec.Options.Count > 0 ? Spec.Options[0].Code : "");
            Selected = Options.FirstOrDefault(o => o.Code == code) ?? (Options.Count > 0 ? Options[0] : null);
        }
        else
        {
            double v = s.N(Spec.Key);
            string t = double.IsFinite(v) ? Spec.Num(v) : "";
            if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cur) || Math.Abs(cur - Spec.Shown(v)) > 1e-9 || !double.IsFinite(v)) Text = t;
            Bad = Spec.Bad(v);
            // 压力另写约合多少 psi（样稿 rng：「0.01 – 3.15（≈ 363 psi）」）—— 只在 MPa 时写（bar 的范围多一位整数，加上会放不下；psi 本身就是）
            // 超范围时不写（琥珀的范围 + 图标已经够了；窄屏的参数面板放不下第二行的「≈ psi」，会把 psi 拆开）
            Range = Spec.RangeText + (Spec.IsPressure && PressureDisplay.Primary == PressureUnit.MPa && double.IsFinite(v) && !Bad ? "（≈ " + JsNumber.ToFixed(Units.MpaToPsi(v), 0) + " psi）" : "");
        }
        _sync = false;
    }
}

/// <summary>曲线升温的一段（段表的一行）。</summary>
public sealed partial class SegVm : ObservableObject
{
    private readonly OneVm _owner;
    private bool _sync;

    public SegVm(OneVm owner, int j)
    {
        _owner = owner;
        J = j;
        No = (j + 1).ToString(CultureInfo.InvariantCulture);
        DeleteCommand = new RelayCommand(() => owner.DelSeg(J));
    }

    public int J { get; }
    public string No { get; }
    public ICommand DeleteCommand { get; }
    [ObservableProperty] private string _temp = "", _rate = "", _hold = "";
    [ObservableProperty] private bool _badTemp, _badRate, _badHold;
    [ObservableProperty] private bool _locked;

    partial void OnTempChanged(string value) => Set("temp", value);
    partial void OnRateChanged(string value) => Set("rate", value);
    partial void OnHoldChanged(string value) => Set("hold", value);

    private void Set(string key, string text)
    {
        if (_sync) return;
        _owner.SetSeg(J, key, double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN);
    }

    public void Apply(CurveSeg g, IReadOnlyList<FieldSpec>? fs, bool locked)
    {
        _sync = true;
        Locked = locked;
        Temp = JsNumber.ToFixed(g.Temp, 1);
        Rate = JsNumber.ToFixed(g.Rate, 1);
        Hold = JsNumber.ToString(g.Hold);
        FieldSpec? F(string k) => fs?.FirstOrDefault(x => x.Key == k);
        BadTemp = F("temp")?.Bad(g.Temp) ?? false;
        BadRate = F("rate")?.Bad(g.Rate) ?? false;
        BadHold = F("hold")?.Bad(g.Hold) ?? false;
        _sync = false;
    }
}

/// <summary>下发面板的一个通道小块（样稿 .pgc）：RV、状态、勾选；在跑 / 停用 / 没有控制权时不能选。</summary>
public sealed partial class ChipVm : ObservableObject
{
    private readonly OneVm _owner;
    private bool _sync;

    public ChipVm(OneVm owner) => _owner = owner;

    public int N { get; set; }
    [ObservableProperty] private string _rv = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _disabled;
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_sync) _owner.Toggle(N, value);
    }

    internal void SetChecked(bool v)
    {
        _sync = true;
        IsChecked = v;
        _sync = false;
    }
}
