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
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>
/// 程序页（融合版 F6，样稿 PAGES.prog + 条件矩阵）：左边程序库（按型号、搜索、「运行 N 路」/「N 处待改」）；中间按模式 ——
/// <b>条件矩阵</b>（默认，样稿 MX.mode = 'mx'：同一套步骤、每路填不同条件，见 <see cref="MatrixVm"/>）或 <b>单路程序</b>（步骤列表 + 预计曲线 + 参数，见 <see cref="OneVm"/>）；
/// 右边检查 / 参数 + 下发。工具条按模式换一组按钮（样稿 mxRibbon / PAGES.prog.ribbon）。
/// <list type="bullet">
/// <item>程序库存在本机（Services/ProgramLibraryStore：library.json，第一次打开时迁移旧程序页的 drafts.json）；演示用样稿的程序库。</item>
/// <item>参数的范围取设备的程序 schema（真机 GET /program-schema；演示 / 还没取到时按设备限值本机生成，规则同 HMI）—— <see cref="SchemaOf"/>。</item>
/// <item>下发：每一路 溶液体积（PUT volume）→ 程序（PUT program，记程序台账）→ 运行（program/run），见 <see cref="ProgramSender"/>。</item>
/// <item>预运行检查对话框（<see cref="PreflightDialogVm"/>，名字「pgcheck」）：真机另取 GET /preflight 的硬件项。</item>
/// </list>
/// </summary>
public sealed partial class ProgramPageVm : PageVm, Records.IRedoTarget
{
    private readonly ShellVm _shell;
    private readonly List<RibbonGroup> _toolsOne, _toolsMx;
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private bool _inited;

    public ProgramPageVm(ShellVm shell) : base(AppPage.Program)
    {
        _shell = shell;
        bool demo = shell.Fleet.IsDemo;
        Store = new ProgramLibraryStore(demo, shell.Fleet.Devices.FirstOrDefault()?.Model.Key);
        Experiments = new ExperimentStore(demo);
        One = new OneVm(this);
        Mx = new MatrixVm(this);
        PickLibCommand = new RelayCommand<LibItemVm>(x => { if (x != null) Pick(x.Id); });
        SetModeCommand = new RelayCommand<string>(m => SetMode(m == "one" ? "one" : "mx"));

        // 工具条（样稿 PAGES.prog.ribbon / mxRibbon）
        _new = new RibbonTool("f-add-c", "新建程序", NewProgram, tip: "新建一个程序（本型号）");
        _save = new RibbonTool("f-save", "保存", SaveProgram);
        _dup = new RibbonTool("f-copy", "复制为新程序", Duplicate, tip: "复制成一个新程序，可以修改");
        _imp = new RibbonTool("f-upload", "导入", () => _ = ImportProgramAsync(), tip: "导入程序（HMI 程序 JSON）");
        _exp = new RibbonTool("f-down", "导出", () => _ = ExportProgramAsync(), tip: "导出为 HMI 程序 JSON");
        _chk1 = new RibbonTool("f-task", "预运行检查", () => OpenCheck(), tip: "下发前检查设备、气路和目标通道");
        _run1 = new RibbonTool("f-play", "下发并运行", () => _ = One.SendAsync(), primary: true);
        _stop1 = new RibbonTool("f-stop", "停止程序", StopSelected, tip: "停止所选通道上的程序 · 不需要控制权");
        _toolsOne = new()
        {
            new RibbonGroup("程序", new[] { _new, _save, _dup }),
            new RibbonGroup("文件", new[] { _imp, _exp }),
            new RibbonGroup("运行", new[] { _chk1, _run1, _stop1 }),
        };
        _reset = new RibbonTool("f-reset", "按模板重建", () => Mx.Rebuild(true), tip: "丢掉矩阵里的修改，按所选模板重新生成");
        _savePlan = new RibbonTool("f-save", "保存方案", () => Mx.SavePlan(), tip: "把这份条件矩阵（含样品名）存到本机");
        _impCsv = new RibbonTool("f-upload", "导入 CSV", () => _ = Mx.ImportCsvAsync(), tip: "导入条件矩阵：每路一列");
        _expCsv = new RibbonTool("f-down", "导出 CSV", () => _ = Mx.ExportCsvAsync(), tip: "导出条件矩阵（CSV，每路一列）");
        _chkMx = new RibbonTool("f-task", "预运行检查", () => OpenCheck());
        _runMx = new RibbonTool("f-play", "下发并运行", () => _ = Mx.SendAsync(), primary: true);
        _toolsMx = new()
        {
            new RibbonGroup("方案", new[] { _reset, _savePlan }),
            new RibbonGroup("文件", new[] { _impCsv, _expCsv }),
            new RibbonGroup("运行", new[] { _chkMx, _runMx }),
        };
        shell.Dialogs.Register("pgcheck", _ => CreateCheck());
        if (Store.LoadError is { } le) shell.Toast(le, ToastKind.Warn);
        else if (Store.Migrated > 0) shell.Toast("已把旧程序页的 " + Store.Migrated + " 份草稿迁移到程序库", ToastKind.Info);
        else if (!demo && Store.Programs.Count(p => p.Migrated) is int conv and > 0)
            shell.Toast("程序库里有 " + conv + " 个旧格式的程序，已按新步骤转换 · 打开检查后保存", ToastKind.Info);
    }

    private readonly RibbonTool _new, _save, _dup, _imp, _exp, _chk1, _run1, _stop1, _reset, _savePlan, _impCsv, _expCsv, _chkMx, _runMx;

    internal ShellVm Shell => _shell;
    public ProgramLibraryStore Store { get; }
    public ExperimentStore Experiments { get; }
    public OneVm One { get; }
    public MatrixVm Mx { get; }

    public override IReadOnlyList<RibbonGroup> Tools => IsMatrix ? _toolsMx : _toolsOne;

    // ———————————————————— 模式 ————————————————————

    /// <summary>条件矩阵（默认，同样稿 MX.mode = 'mx'）；false = 单路程序。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOne), nameof(ModeKey))] private bool _isMatrix = true;
    public bool IsOne => !IsMatrix;
    /// <summary>分段控件的值：one / mx。</summary>
    public string ModeKey { get => IsMatrix ? "mx" : "one"; set => SetMode(value); }

    public ICommand SetModeCommand { get; }

    public void SetMode(string? m)
    {
        bool mx = m != "one";
        if (mx == IsMatrix) return;
        IsMatrix = mx;
        if (mx) EnterMatrix();
        RaiseToolsChanged();
        _shell.Page?.Refresh(_shell.Fleet);
        if (!ReferenceEquals(_shell.Page, this)) Refresh(_shell.Fleet);
    }

    /// <summary>第一次进条件矩阵：样稿 mxRender 的 MX.inited —— 选的还是默认的「平行合成程序 A」时换成「催化加氢筛选」（演示）。</summary>
    private void EnterMatrix()
    {
        if (_inited) return;
        _inited = true;
        if (SelId == "A" && Store.Find("H1") != null) SelId = "H1";
    }

    // ———————————————————— 设备 ————————————————————

    /// <summary>当前设备（工具条的设备下拉；程序页的范围是「一台」，不会是 0）。</summary>
    public DeviceVm? Dev => _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) : null;

    [ObservableProperty] private string _devAlias = "";
    [ObservableProperty] private string _devModelLabel = "";
    /// <summary>下发面板的副标题「1号机 · HT-PR8」。</summary>
    [ObservableProperty] private string _sendSub = "";
    /// <summary>窄屏（排版宽 &lt; 1600）：样稿 @media max-width 1599px。</summary>
    [ObservableProperty] private bool _isNarrow;
    /// <summary>矮屏（排版高 &lt; 820）。</summary>
    [ObservableProperty] private bool _isShort;

    // ———————————————————— 程序 schema ————————————————————

    private readonly Dictionary<Guid, (long Rev, ProgramSchemaDto Dto)> _hmiSchemas = new();
    private readonly HashSet<Guid> _fetching = new();

    /// <summary>
    /// 这台设备的程序 schema：真机取到过 GET /program-schema（设置修订号没变）就用它；否则按设备限值本机生成（同 HMI 的规则）。
    /// 搅拌转速再按设备的釜转速上限封顶（PlantInfo.RpmMax，默认 450）。真机在线、还没取到时在后台取，取到后刷新页面。
    /// </summary>
    public ProgramSchema SchemaOf(DeviceVm d)
    {
        var src = d.Source;
        double rpmCap = src.Plant.RpmMax ?? 450;
        long rev = src.Settings?.Revision ?? 0;
        bool hmi = _hmiSchemas.TryGetValue(src.Id, out var got) && got.Rev == rev;
        if (!hmi && src is LiveDeviceSource live && live.Connection is { } c && src.Conn.Online && _fetching.Add(src.Id))
            _ = FetchSchemaAsync(src.Id, c, rev);
        // 同一台设备、同样的来源与限值时返回同一个实例（页面按身份判断要不要重建行 —— 每秒新建会让输入框丢焦点）
        var lim = LimitsOf(d);
        var key = (hmi ? got.Dto : null, rev, lim, rpmCap);
        if (_schemaCache.TryGetValue(src.Id, out var cached) && Equals(cached.Key, key)) return cached.Schema;
        var schema = (hmi ? ProgramSchema.FromDto(got.Dto) : ProgramSchema.Default(lim)).Cap(rpmCap);
        _schemaCache[src.Id] = (key, schema);
        return schema;
    }

    private readonly Dictionary<Guid, (object Key, ProgramSchema Schema)> _schemaCache = new();

    private async Task FetchSchemaAsync(Guid id, ParallelReactor.Client.IReactorConnection c, long rev)
    {
        try
        {
            var dto = await c.Rest.ProgramSchemaAsync(CancellationToken.None).ConfigureAwait(true);
            _hmiSchemas[id] = (dto.SettingsRevision > 0 ? dto.SettingsRevision : rev, dto);
            Refresh(_shell.Fleet);
        }
        catch (Exception) { /* 取不到时继续用本机生成的范围；下次刷新再试（在线时） */ }
        finally { _fetching.Remove(id); }
    }

    /// <summary>本机生成 schema 用的限值：真机设置 limits / alarms，演示按型号表 + 整机数据（超温默认 = 温度上限 + 20 ℃，同样稿 OVER_T 220）。</summary>
    public static SchemaLimits LimitsOf(DeviceVm d)
    {
        var src = d.Source;
        var m = d.Model;
        double tmax = src.Settings?.Limits.TempSpMaxC ?? m.Limits.TempMaxC;
        double over = src.Plant.OverTempC ?? src.Settings?.Alarms.OverTempC ?? tmax + 20;
        double pmax = src.Settings?.Limits.PressureSpMaxPsi ?? Units.MpaToPsi(m.Limits.PressureMaxMPa);
        double op = src.Plant.OverPressureMPa is double opm ? Units.MpaToPsi(opm) : Units.MpaToPsi(m.Limits.PressureMaxMPa);
        double rpm = src.Plant.RpmMax ?? src.Settings?.Limits.RpmMax ?? 450;
        return new SchemaLimits(tmax, over, rpm, pmax, op);
    }

    /// <summary>溶液体积上限 mL：真机设置 limits.volumeMaxMl，否则型号表的最大装液量（10 mL 釜 = 8）。</summary>
    public static double VolMaxOf(DeviceVm d) => d.Source.Settings?.Limits.VolumeMaxMl is double v && v > 0 ? v : d.Model.Vessel.MaxFillMl > 0 ? d.Model.Vessel.MaxFillMl : 8;

    /// <summary>某个型号的程序在程序库里算待改项时用的 schema：当前设备是这个型号就用它的，否则找同型号的设备，再没有就用当前设备的（只差型号限值）。</summary>
    private ProgramSchema SchemaForModel(string model, DeviceVm cur, ProgramSchema curSchema)
    {
        if (cur.Model.Key == model) return curSchema;
        var other = _shell.Fleet.Devices.FirstOrDefault(x => x.Model.Key == model);
        return other != null ? SchemaOf(other) : curSchema;
    }

    // ———————————————————— 程序库 ————————————————————

    public ObservableCollection<LibItemVm> Lib { get; } = new();
    public ICommand PickLibCommand { get; }

    /// <summary>搜索框（样稿 #pgQ：按名字包含）。</summary>
    [ObservableProperty] private string _libQuery = "";
    partial void OnLibQueryChanged(string value) => Refresh(_shell.Fleet);

    /// <summary>型号筛选：cur = 本机型号，all = 全部型号。</summary>
    [ObservableProperty] private string _libScope = "cur";
    partial void OnLibScopeChanged(string value) => Refresh(_shell.Fleet);

    /// <summary>型号筛选的两段（第一段的字 = 当前设备的型号）。</summary>
    public IReadOnlyList<Controls.Pr.SegOption> ScopeOptions { get; } = new[] { new Controls.Pr.SegOption { Value = "cur", Label = "HT-PR8" }, new Controls.Pr.SegOption { Value = "all", Label = "全部型号" } };

    [ObservableProperty] private string _libCount = "";
    [ObservableProperty] private bool _libEmpty;

    /// <summary>选中的程序 id（样稿 PG.sel，默认「平行合成程序 A」）。</summary>
    [ObservableProperty] private string? _selId = "A";

    public PcProgram? Sel => Store.Find(SelId);

    /// <summary>单击程序库的一行（样稿 data-pgsel：换程序、回到第一步）。</summary>
    public void Pick(string id)
    {
        if (SelId == id) return;
        SelId = id;
        One.StepIndex = 0;
        Refresh(_shell.Fleet);
    }

    /// <summary>在跑某个程序的通道（样稿 usedBy：全部设备上程序 id 相同、运行中 / 报警暂停的）。</summary>
    public IReadOnlyList<(DeviceVm D, int N)> UsedBy(string id)
    {
        var list = new List<(DeviceVm, int)>();
        foreach (var d in _shell.Fleet.Devices)
            foreach (var r in d.Reactors)
                if (r.Installed && r.Program?.Record is { IsActive: true } p && p.Id == id) list.Add((d, r.N));
        return list;
    }

    public bool IsDirty(string id) => _dirty.Contains(id);
    internal void MarkDirty(string id) { _dirty.Add(id); }

    private void SyncLib(DeviceVm d, ProgramSchema schema)
    {
        var now = _shell.Fleet.Now;
        string q = LibQuery.Trim();
        var list = Store.Programs.Where(p => (LibScope == "all" || p.Model == d.Model.Key) && (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        for (int i = Lib.Count - 1; i >= 0; i--) if (!list.Any(p => p.Id == Lib[i].Id)) Lib.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            int at = -1;
            for (int j = 0; j < Lib.Count; j++) if (Lib[j].Id == p.Id) { at = j; break; }
            LibItemVm row;
            if (at < 0) { row = new LibItemVm(p.Id); Lib.Insert(Math.Min(i, Lib.Count), row); }
            else { row = Lib[at]; if (at != i) Lib.Move(at, i); }
            var sch = SchemaForModel(p.Model, d, schema);
            int used = UsedBy(p.Id).Count;
            int iss = ProgramIssues.Of(p.Steps, sch, MatrixPlan.ModelLabelOf(p.Model)).Count;
            row.Name = p.Name;
            row.IsSelected = p.Id == SelId;
            row.TagText = used > 0 ? "运行 " + used + " 路" : iss > 0 ? iss + " 处待改" : p.Draft ? "草稿" : "";
            row.TagKind = used > 0 ? "run" : iss > 0 ? "warn" : "";
            row.Line2 = p.Steps.Count + " 步 · 约 " + ProgramPlanner.Hm(ProgramPlanner.Plan(p.Steps).Total) + " · " + MatrixPlan.ModelLabelOf(p.Model)
                + (p.Draft && (used > 0 || iss > 0) ? " · 草稿" : "") + (IsDirty(p.Id) ? " · 未保存" : "");
            row.Line3 = PcProgram.ModText(p.Modified, now) + " · " + p.By;
        }
        LibCount = list.Count + " 个";
        LibEmpty = list.Count == 0;
    }

    // ———————————————————— 刷新 ————————————————————

    public override void Refresh(IFleetView fleet)
    {
        var d = Dev;
        IsNarrow = !_shell.Layout.IsWide;
        IsShort = _shell.Layout.IsShort;
        if (d is null) return;
        DevAlias = d.Alias;
        DevModelLabel = d.Model.Label;
        ScopeOptions[0].Label = d.Model.Label;
        SendSub = d.Alias + " · " + d.Model.Label;
        var schema = SchemaOf(d);
        // 选的程序不是这个型号（换了设备）：条件矩阵自动换成同型号的模板（样稿 mxRender）；单路程序照样稿留着，下发面板写明型号不对
        if (IsMatrix && (Sel is null || Sel.Model != d.Model.Key) && Store.Programs.FirstOrDefault(p => p.Model == d.Model.Key) is { } same) SelId = same.Id;
        if (Sel is null && (Store.Programs.FirstOrDefault(p => p.Model == d.Model.Key) ?? Store.Programs.FirstOrDefault()) is { } first) SelId = first.Id;
        SyncLib(d, schema);
        if (IsMatrix) Mx.Refresh(d, schema);
        else One.Refresh(d, schema);
        SyncTools(d);
    }

    private void SyncTools(DeviceVm d)
    {
        var p = Sel;
        int run = p is null ? 0 : UsedBy(p.Id).Count;
        // 从旧格式转换来的程序没改也能保存（按新格式写回）
        _save.Reason = p is null ? "先选一个程序" : run > 0 ? "运行中的程序不能直接保存 · 先复制为新程序" : !IsDirty(p.Id) && !p.Migrated ? "没有未保存的修改" : null;
        _save.Tip = "保存修改";
        _dup.Reason = p is null ? "先选一个程序" : null;
        _exp.Reason = p is null ? "先选一个程序" : null;
        string ctl = d.Actions.GuardText(GuardKind.Control);
        _chk1.Reason = ctl.Length > 0 ? ctl : null;
        _run1.Reason = One.RunReason;
        _run1.Tip = One.RunTip;
        _stop1.Reason = One.StopReason;
        _chkMx.Reason = null;
        _runMx.Reason = Mx.RunReason;
        _runMx.Tip = Mx.RunTip;
        _impCsv.Reason = Mx.HasPlan ? null : "先选一个模板";
        _expCsv.Reason = Mx.HasPlan ? null : "先选一个模板";
        _savePlan.Reason = Mx.HasPlan ? null : "先选一个模板";
        _reset.Reason = Mx.HasPlan ? null : "先选一个模板";
    }

    // ———————————————————— 程序：新建 / 保存 / 复制 / 导入 / 导出 ————————————————————

    private void NewProgram()
    {
        if (Dev is not { } d) return;
        // 新程序 = 设计稿的加氢筛选模板（气密 → 惰性置换 → 气体 A 置换 → 搅拌 → 升温 → 充压 → 反应 → 放空 → 结束），按需要删改
        var p = ProgramTemplates.Hydrogenation(Store.NewId("N"), d.Model.Key, "未命名程序");
        p.Modified = _shell.Fleet.Now;
        p.By = "本机";
        p.Draft = true;
        Store.Programs.Insert(0, p);
        _dirty.Add(p.Id);
        SelId = p.Id;
        One.StepIndex = 0;
        if (IsMatrix) SetMode("one");
        _shell.Toast("已按加氢筛选模板新建程序 · 按需要删改步骤", ToastKind.Info);
        Refresh(_shell.Fleet);
    }

    private void SaveProgram()
    {
        if (Sel is not { } p || _save.Reason != null) return;
        p.Modified = _shell.Fleet.Now;
        p.By = _shell.Fleet.ClientName.Length > 0 ? _shell.Fleet.ClientName : "本机";
        if (Dev is { } d && ProgramIssues.Of(p.Steps, SchemaOf(d), MatrixPlan.ModelLabelOf(p.Model)).Count == 0) p.Draft = false;
        _dirty.Remove(p.Id);
        if (Store.Save() is { } err) _shell.Toast(err, ToastKind.Err);
        else
        {
            bool conv = p.Migrated;
            p.Migrated = false;   // 已按新格式写盘
            _shell.Toast("已保存「" + p.Name + "」" + (conv ? " · 已按新步骤格式保存" : ""), ToastKind.Ok);
        }
        Refresh(_shell.Fleet);
    }

    internal void Duplicate()
    {
        if (Sel is not { } p) return;
        var c = p.Clone(Store.NewId("D"), p.Name + "（副本）");
        c.Modified = _shell.Fleet.Now;
        c.By = "本机";
        c.Draft = true;
        Store.Programs.Insert(0, c);
        _dirty.Add(c.Id);
        SelId = c.Id;
        _shell.Toast("已复制为「" + c.Name + "」，可以修改", ToastKind.Info);
        Refresh(_shell.Fleet);
    }

    /// <summary>文件对话框（视图给：打开 / 保存，返回路径；取消为 null）。截图 / 测试时没有。</summary>
    public Func<string, string, Task<string?>>? OpenFile { get; set; }
    public Func<string, string, string, Task<string?>>? SaveFile { get; set; }

    private async Task ImportProgramAsync()
    {
        if (Dev is not { } d) return;
        if (OpenFile is null) { _shell.Toast("导入程序：选择 HMI 程序 JSON（这里没有文件对话框）", ToastKind.Info); return; }
        var path = await OpenFile("导入程序", "json").ConfigureAwait(true);
        if (path is null) return;
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(true);
            var doc = ParallelReactor.Client.ProgramDoc.FromJson(text, System.IO.Path.GetFileNameWithoutExtension(path));
            var p = PcProgram.FromDoc(doc, d.Model.Key, Store.NewId("I"));
            p.Modified = _shell.Fleet.Now;
            p.By = "本机";
            p.Draft = true;
            Store.Programs.Insert(0, p);
            _dirty.Add(p.Id);
            SelId = p.Id;
            _shell.Toast("已导入「" + p.Name + "」（" + p.Steps.Count + " 步）· 保存后才存进程序库", ToastKind.Ok);
            Refresh(_shell.Fleet);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or FormatException or UnauthorizedAccessException)
        {
            _shell.Toast("导入失败：" + ex.Message, ToastKind.Err);
        }
    }

    private async Task ExportProgramAsync()
    {
        if (Sel is not { } p) return;
        if (SaveFile is null) { _shell.Toast("导出「" + p.Name + "」：这里没有文件对话框", ToastKind.Info); return; }
        var path = await SaveFile("导出程序", p.Name + ".json", "json").ConfigureAwait(true);
        if (path is null) return;
        try
        {
            await System.IO.File.WriteAllTextAsync(path, p.ToDoc().ToJson()).ConfigureAwait(true);
            _shell.Toast("已导出「" + p.Name + "」→ " + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _shell.Toast("导出失败：" + ex.Message, ToastKind.Err); }
    }

    /// <summary>停止所选通道上的程序（停止类：不需要控制权；先确认 —— 用户规定停止一律先确认）。</summary>
    private void StopSelected()
    {
        if (Dev is not { } d) return;
        var sel = One.SelectedChannels;
        if (sel.Count == 0) { _shell.Toast("停止程序：先在下发面板勾选通道，或在主页的运行表里选一路", ToastKind.Info); return; }
        _shell.OpenDialog("stopch", d.K.ToString(CultureInfo.InvariantCulture) + ":" + sel[0].ToString(CultureInfo.InvariantCulture));
    }

    // ———————————————————— 预运行检查 ————————————————————

    /// <summary>打开预运行检查（两种模式共用一个对话框，内容按模式）。</summary>
    public void OpenCheck() => _shell.OpenDialog("pgcheck");

    private DialogVm? CreateCheck() => Dev is { } d ? new PreflightDialogVm(this, d, IsMatrix) : null;

    // ———————————————————— 地址参数（截图 / 走查） ————————————————————

    /// <summary>
    /// 像新打开的页面（截图前）：回到默认模式（条件矩阵）、默认程序（A，进矩阵时换 H1）、第 3 步、下发面板只选 RV5、溶液体积 5.0；矩阵按模板重建。
    /// 地址参数：pgm=one|mx（模式）、pgbank=2（16 路设备看 RV9–16）、pgvol=n:（第 n 路的溶液体积清空，演示校验）、pgstep=n（单路程序选中第 n 步，截图看那一步的参数）、
    /// pgp=id（选这个程序，例如 H1 加氢筛选、OX 旧格式转换来的）、pgset=键:值,…（改选中那一步的参数，截图看 visibleWhen：例如 until:tempBelow、cycles:0）。
    /// </summary>
    /// <summary>
    /// 记录页「用这组参数再做一次」（融合版 F7 → F8 接上）：切到单路程序、载入那个程序（按程序库 id，找不到按名字），预选那次会话的通道里此刻空闲的。
    /// 程序库里没有这个程序时返回 false（记录页提示手动做法）。
    /// </summary>
    public bool AcceptRedo(Records.RedoRequest request, out string? toast)
    {
        toast = null;
        if (request.Kind != Records.RedoKind.Program) return false;
        var prog = Store.Find(request.ProgramId) ?? Store.Programs.FirstOrDefault(p => p.Name == request.ProgramName);
        if (prog == null) return false;
        if (_shell.Nav.DeviceK != request.DeviceK) _shell.SelectDevice(request.DeviceK);
        SetMode("one");
        SelId = prog.Id;
        One.StepIndex = 0;
        Refresh(_shell.Fleet);
        var got = One.Preselect(request.Channels);
        var skipped = request.Channels.Except(got).Concat(request.Busy).Distinct().OrderBy(n => n).ToList();
        string Rvs(IEnumerable<int> ns) => string.Join("、", ns.Select(n => "RV" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        toast = "已载入「" + prog.Name + "」· 预选 " + (got.Count == 0 ? "没有空闲的通道" : Rvs(got))
            + (skipped.Count > 0 ? "（" + Rvs(skipped) + " 正在运行 / 停用，没选）" : "")
            + (prog.Migrated ? " · 这个程序是旧格式、已按新步骤转换，请先检查" : "") + " · 装好样品、填好溶液体积后点「下发并运行」";
        return true;
    }

    public override void ApplyParams(Func<string, string?> x)
    {
        _inited = false;
        _dirty.Clear();
        Store.ResetDemo();
        SelId = "A";
        LibQuery = "";
        LibScope = "cur";
        One.Reset();
        Mx.Reset();
        if (x("pgp") is { Length: > 0 } pid && Store.Find(pid) != null) { SelId = pid; _inited = true; }
        IsMatrix = x("pgm") != "one";
        if (IsMatrix) EnterMatrix();
        Refresh(_shell.Fleet);
        if (x("pgbank") is "2") Mx.Bank = 1;
        if (x("pgvol") is { Length: > 0 } pv && pv.Split(':') is { Length: 2 } a && int.TryParse(a[0], out var vn))
        {
            double v = double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vv) ? vv : double.NaN;
            if (IsMatrix) Mx.SetVolume(vn, v);
            else One.VolText = double.IsNaN(v) ? "" : JsNumber.ToFixed(v, 1);
        }
        if (!IsMatrix && int.TryParse(x("pgstep"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step) && step >= 1) One.Select(step - 1);
        if (!IsMatrix && x("pgset") is { Length: > 0 } set)
            foreach (var kv in set.Split(','))
                if (kv.Split(':') is { Length: 2 } a2)
                {
                    if (double.TryParse(a2[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) One.SetNum(a2[0], num);
                    else One.SetSel(a2[0], a2[1]);
                }
        RaiseToolsChanged();
        Refresh(_shell.Fleet);
    }
}

/// <summary>程序库的一行（样稿 .li）：名字 + 标签（运行 N 路 · 绿 / N 处待改 · 琥珀 / 草稿 · 灰）、步数 · 约时长 · 型号、修改时刻 · 人。</summary>
public sealed partial class LibItemVm : ObservableObject
{
    public LibItemVm(string id) => Id = id;
    public string Id { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTag))] private string _tagText = "";
    /// <summary>run / warn / ''（草稿 = 灰）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TagRun), nameof(TagWarn))] private string _tagKind = "";
    [ObservableProperty] private string _line2 = "";
    [ObservableProperty] private string _line3 = "";
    public bool HasTag => TagText.Length > 0;
    public bool TagRun => TagKind == "run";
    public bool TagWarn => TagKind == "warn";
}
