using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Models;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>「新建程序」窗口左边的一项：空白程序 / 一个内置模板 / 一个我的模板。</summary>
public sealed partial class TplItemVm : ObservableObject
{
    public TplItemVm(PcTemplate? template, Action<TplItemVm> pick)
    {
        Template = template;
        PickCommand = new RelayCommand(() => pick(this));
    }

    /// <summary>null = 空白程序。</summary>
    public PcTemplate? Template { get; }
    public bool IsBlank => Template is null;
    public bool IsOwn => Template is { BuiltIn: false };
    /// <summary>地址参数 / 测试用的键：blank 或模板 id。</summary>
    public string Key => Template?.Id ?? "blank";
    public string Icon => IsBlank ? "plus" : IsOwn ? "doc" : "beaker";
    public ICommand PickCommand { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>「新建程序」底栏左边的「程序名称」一栏（和「创建程序」是一件事，所以放在底栏；DialogHost 里有它的 DataTemplate）。</summary>
public sealed record ProgNameFoot(NewProgramDialogVm Owner);

/// <summary>预览里的一步（只读，同步骤列表的一行：序号 · 类型 · 名字 · 两个关键参数 · 预计用时）。</summary>
public sealed record TplStepVm(string No, string TypeLabel, string Name, string K1, string V1, string K2, string V2, string Dur);

/// <summary>
/// 新建程序（设计稿 tpl-design 方向 A，对话框名「pgnew」，参数 = 预选项的键 blank / T1 / U…）：左边选「空白程序」或一个模板
/// （内置模板 · 随软件提供；我的模板 · 存在本机），右边预览步骤，底栏填程序名，「创建程序」。
/// 我的模板在预览右上可以改名、删除（删除先确认）；内置的不能。创建出来的是程序库里的一份新程序（草稿、未保存），模板本身不变。
/// </summary>
public sealed partial class NewProgramDialogVm : DialogVm
{
    public const string DialogName = "pgnew";
    public const string BlankName = "未命名程序";

    private readonly ProgramPageVm _page;
    private readonly DeviceVm _d;
    private readonly FootButton _create;
    private bool _nameTouched, _settingName;

    public NewProgramDialogVm(ProgramPageVm page, DeviceVm d, string? pick = null)
    {
        _page = page;
        _d = d;
        Name = DialogName;
        Arg = pick;
        Title = "新建程序";
        Sub = d.Alias + " · " + d.Model.Label + " · 选一个模板作为起点，或者从空白开始";
        Icon = null;
        Width = 1080;
        BodyPadding = new Avalonia.Thickness(0);
        Blank = new TplItemVm(null, Pick) { Name = "空白程序", Sub = "不用模板，自己一步一步加" };
        StartRenameCommand = new RelayCommand(StartRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        StartDeleteCommand = new RelayCommand(() => { IsRenaming = false; IsDeleting = true; });
        ConfirmDeleteCommand = new RelayCommand(ConfirmDelete);
        CancelDeleteCommand = new RelayCommand(() => IsDeleting = false);
        CreateCommand = new RelayCommand(Create);
        _create = new FootButton("创建程序", CreateCommand, FootKind.Primary, "add-c");
        Footer = new FootBar(new FootButton("取消", new RelayCommand(() => page.Shell.Dialogs.Close(this))), _create);
        Build(pick ?? "T1");
        Body = this;                       // 视图在 DialogHost 的 DataTemplate 里（NewProgramView）
        FooterLeft = new ProgNameFoot(this);   // 底栏左边的「程序名称」一栏（同上）
    }

    public TplItemVm Blank { get; }
    public ObservableCollection<TplItemVm> BuiltIns { get; } = new();
    public ObservableCollection<TplItemVm> Mine { get; } = new();
    public IEnumerable<TplItemVm> All => new[] { Blank }.Concat(BuiltIns).Concat(Mine);

    public ICommand CreateCommand { get; }
    public ICommand StartRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand StartDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    [ObservableProperty] private string _builtInCount = "";
    [ObservableProperty] private string _mineCount = "";
    [ObservableProperty] private bool _mineEmpty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTemplate), nameof(IsOwn), nameof(ShowManage), nameof(ShowTitle))] private TplItemVm? _selected;
    public bool IsBlank => Selected is null or { IsBlank: true };
    public bool IsTemplate => !IsBlank;
    public bool IsOwn => Selected is { IsOwn: true };

    // 预览
    [ObservableProperty] private string _pvTitle = "";
    [ObservableProperty] private string _pvNote = "";
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _stepsText = "";
    [ObservableProperty] private string _durText = "";
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private IReadOnlyList<TplStepVm> _steps = Array.Empty<TplStepVm>();
    /// <summary>「张工 · 11-12 存入 · 来自程序「…」」（我的模板）。</summary>
    [ObservableProperty] private string _byText = "";
    [ObservableProperty] private bool _hasBy;

    // 程序名
    [ObservableProperty] private string _progName = "";
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private bool _hasNameError;

    // 我的模板：改名 / 删除（就地，不另开窗口 —— 同一时间只有一个对话框）
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowManage), nameof(ShowTitle))] private bool _isRenaming;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowManage))] private bool _isDeleting;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private string? _renameError;
    [ObservableProperty] private bool _hasRenameError;
    /// <summary>「改名」「删除」两个按钮（我的模板、没在改名 / 确认删除）。</summary>
    public bool ShowManage => IsOwn && !IsRenaming && !IsDeleting;
    public bool ShowTitle => !IsRenaming;

    partial void OnSelectedChanged(TplItemVm? value) => OnPropertyChanged(nameof(IsBlank));

    partial void OnProgNameChanged(string value)
    {
        if (!_settingName) _nameTouched = true;
        NameError = _nameTouched ? PcTemplate.ProgramNameError(value) : null;
        HasNameError = NameError != null;
        _create.Reason = PcTemplate.ProgramNameError(value);
    }

    partial void OnRenameTextChanged(string value)
    {
        RenameError = Selected?.Template is { } t ? PcTemplate.NameError(value, _page.Templates.For(_d.Model.Key), t.Id) : null;
        HasRenameError = RenameError != null;
    }

    private void Build(string pick)
    {
        string model = _d.Model.Key;
        BuiltIns.Clear();
        Mine.Clear();
        foreach (var t in _page.Templates.For(model))
        {
            var item = new TplItemVm(t, Pick) { Name = t.Name, Sub = SubOf(t) };
            (t.BuiltIn ? BuiltIns : Mine).Add(item);
        }
        BuiltInCount = BuiltIns.Count + " 个";
        MineCount = Mine.Count + " 个";
        MineEmpty = Mine.Count == 0;
        Pick(All.FirstOrDefault(x => x.Key == pick) ?? BuiltIns.FirstOrDefault() ?? Blank);
    }

    private static string SubOf(PcTemplate t) => t.BuiltIn
        ? t.Program.Steps.Count + " 步 · " + ShortOf(t)
        : t.Program.Steps.Count + " 步 · " + t.Program.By + " · " + t.Program.Modified.ToString("MM-dd", CultureInfo.InvariantCulture) + " 存入";

    /// <summary>内置模板列表里的一句话（步骤的走向）。</summary>
    private static string ShortOf(PcTemplate t) => t.Id switch
    {
        "T1" => "气密 → 置换 → 升温 → 充压 → 反应",
        "T2" => "保压看压降，之后放空",
        "T3" => "惰性置换 → 升温 → 充压保持",
        "T4" => "室温 · 不稳压 · 充压后先保持再搅拌",
        _ => "约 " + ProgramPlanner.Hm(ProgramPlanner.Plan(t.Program.Steps).Total),
    };

    private void Pick(TplItemVm item)
    {
        foreach (var x in All) x.IsSelected = ReferenceEquals(x, item);
        Selected = item;
        IsRenaming = false;
        IsDeleting = false;
        SyncPreview();
        if (!_nameTouched) SetName(DefaultName(item));
    }

    private void SetName(string name)
    {
        _settingName = true;
        ProgName = name;
        _settingName = false;
    }

    /// <summary>默认的程序名：模板名（空白 =「未命名程序」）；程序库里已有同名的就加「 2」「 3」…</summary>
    private string DefaultName(TplItemVm item)
    {
        string baseName = item.Template?.Name ?? BlankName;
        string name = baseName;
        for (int k = 2; _page.Store.Programs.Any(p => p.Name == name) && k < 100; k++) name = baseName + " " + k.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    private void SyncPreview()
    {
        if (Selected?.Template is not { } t)
        {
            PvTitle = "空白程序";
            PvNote = "不用模板，从头开始。";
            HasNote = true;
            Steps = Array.Empty<TplStepVm>();
            ByText = "";
            HasBy = false;
            return;
        }
        var schema = _page.SchemaOf(_d);
        var plan = ProgramPlanner.Plan(t.Program.Steps);
        PvTitle = t.Name;
        PvNote = t.Note;
        HasNote = t.Note.Length > 0;
        KindText = t.BuiltIn ? "内置模板" : "我的模板";
        StepsText = t.Program.Steps.Count + " 步";
        DurText = "约 " + ProgramPlanner.Hm(plan.Total);
        ModelText = "适用 " + MatrixPlan.ModelLabelOf(t.Model);
        Steps = t.Program.Steps.Select((s, i) =>
        {
            var kv = ProgramPlanner.Summary(s, schema);
            return new TplStepVm((i + 1).ToString("00", CultureInfo.InvariantCulture), schema.Of(s.Type)?.Label ?? s.Type.Zh(), s.Name,
                kv.Count > 0 ? kv[0].K : "", kv.Count > 0 ? kv[0].V : "", kv.Count > 1 ? kv[1].K : "", kv.Count > 1 ? kv[1].V : "", ProgramPlanner.Dur(plan.Bounds[i]));
        }).ToArray();
        ByText = t.BuiltIn ? "" : t.Program.By + " · " + t.Program.Modified.ToString("MM-dd", CultureInfo.InvariantCulture) + " 存入" + (t.From.Length > 0 ? " · 来自程序「" + t.From + "」" : "");
        HasBy = ByText.Length > 0;
    }

    private void StartRename()
    {
        if (Selected?.Template is not { BuiltIn: false } t) return;
        IsDeleting = false;
        RenameText = t.Name;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        if (Selected is not { Template: { BuiltIn: false } t } item) return;
        if (PcTemplate.NameError(RenameText, _page.Templates.For(_d.Model.Key), t.Id) is { } err) { RenameError = err; HasRenameError = true; return; }
        bool follow = !_nameTouched;
        if (_page.Templates.Rename(t, RenameText) is { } fail) _page.Shell.Toast(fail, ToastKind.Err);
        item.Name = t.Name;
        IsRenaming = false;
        SyncPreview();
        if (follow) SetName(DefaultName(item));
    }

    private void ConfirmDelete()
    {
        if (Selected is not { Template: { BuiltIn: false } t } item) return;
        if (_page.Templates.Remove(t) is { } fail) _page.Shell.Toast(fail, ToastKind.Err);
        else _page.Shell.Toast("已删除模板「" + t.Name + "」· 用它建过的程序不受影响", ToastKind.Info);
        int at = Mine.IndexOf(item);
        Mine.Remove(item);
        MineCount = Mine.Count + " 个";
        MineEmpty = Mine.Count == 0;
        Pick(Mine.Count > 0 ? Mine[Math.Min(at, Mine.Count - 1)] : BuiltIns.FirstOrDefault() ?? Blank);
    }

    private void Create()
    {
        if (PcTemplate.ProgramNameError(ProgName) is { } err)
        {
            _nameTouched = true;
            NameError = err;
            HasNameError = true;
            return;
        }
        _page.CreateProgram(Selected?.Template, ProgName.Trim());
        _page.Shell.Dialogs.Close(this);
    }
}

/// <summary>
/// 存为模板（对话框名「tplsave」）：把当前选中的程序的步骤和参数存进「我的模板」。填模板名称（不能空、不能重名）和说明（可以空）；
/// 适用型号跟随程序。只存步骤和参数，不含样品名、通道、溶液体积；以后再改这个程序，模板不跟着变。
/// </summary>
public sealed partial class SaveTemplateDialogVm : DialogVm
{
    public const string DialogName = "tplsave";

    private readonly ProgramPageVm _page;
    private readonly PcProgram _source;
    private readonly FootButton _ok;
    private bool _touched;

    public SaveTemplateDialogVm(ProgramPageVm page, PcProgram source)
    {
        _page = page;
        _source = source;
        Name = DialogName;
        Title = "存为模板";
        Sub = "把「" + source.Name + "」的步骤和参数存进模板库";
        Icon = null;
        Width = 600;
        ModelText = MatrixPlan.ModelLabelOf(source.Model);
        SaveCommand = new RelayCommand(Save);
        _ok = new FootButton("存为模板", SaveCommand, FootKind.Primary, "flag");
        Footer = new FootBar(new FootButton("取消", new RelayCommand(() => page.Shell.Dialogs.Close(this))), _ok);
        _tplName = UniqueName(source.Name);
        Validate();
        Body = this;                       // 视图在 DialogHost 的 DataTemplate 里（SaveTemplateView）
    }

    public ICommand SaveCommand { get; }
    public string ModelText { get; }
    public string NoteHint => "可以不填 · 最多 " + PcTemplate.NoteMax + " 个字符";
    public string Explain => "模板只保存步骤和参数，不含样品名、通道和溶液体积。存在本机，之后新建程序时可以选它；以后再改这个程序，模板不会跟着变。";

    [ObservableProperty] private string _tplName;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    partial void OnTplNameChanged(string value) { _touched = true; Validate(); }

    private IReadOnlyList<PcTemplate> Scope => _page.Templates.For(_source.Model);

    private string UniqueName(string baseName)
    {
        string b = baseName.Trim();
        if (b.Length > PcTemplate.NameMax) b = b[..PcTemplate.NameMax];
        string name = b;
        for (int k = 2; Scope.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) && k < 100; k++) name = b + " " + k.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    private void Validate()
    {
        string? err = PcTemplate.NameError(TplName, Scope);
        _ok.Reason = err;
        Error = _touched ? err : null;
        HasError = Error != null;
    }

    private void Save()
    {
        if (PcTemplate.NameError(TplName, Scope) is { } err) { _touched = true; Error = err; HasError = true; return; }
        string note = Note.Trim();
        if (note.Length > PcTemplate.NoteMax) note = note[..PcTemplate.NoteMax];
        var fleet = _page.Shell.Fleet;
        var t = PcTemplate.FromProgram(_source, _page.Templates.NewId(), TplName.Trim(), note, fleet.Now, fleet.ClientName.Length > 0 ? fleet.ClientName : "本机");
        if (_page.Templates.Add(t) is { } fail) _page.Shell.Toast(fail, ToastKind.Err);
        else _page.Shell.Toast("已存为模板「" + t.Name + "」· 新建程序时可以选它", ToastKind.Ok);
        _page.Shell.Dialogs.Close(this);
    }
}

/// <summary>
/// 删除程序的确认（对话框名「pgdel」）：从程序库里删掉选中的程序。正在运行的程序不能删（入口已灰掉）；
/// 已经下发到设备上的那份不受影响，记录里的程序名也还在。
/// </summary>
public sealed partial class DeleteProgramDialogVm : DialogVm
{
    public const string DialogName = "pgdel";

    public DeleteProgramDialogVm(ProgramPageVm page, PcProgram p)
    {
        Name = DialogName;
        Title = "删除程序「" + p.Name + "」？";
        Sub = p.Steps.Count + " 步 · " + MatrixPlan.ModelLabelOf(p.Model);
        Icon = "trash";
        Width = 520;
        Text = "会从本机的程序库里删掉这个程序，删了找不回来。已经下发到设备上的不受影响，记录里的程序名也还在。";
        Footer = new FootBar(
            new FootButton("取消", new RelayCommand(() => page.Shell.Dialogs.Close(this))),
            new FootButton("删除", DeleteCommand = new RelayCommand(() => { page.DeleteProgram(p.Id); page.Shell.Dialogs.Close(this); }), FootKind.Primary, "trash"));
        Body = this;
    }

    public ICommand DeleteCommand { get; }

    public string Text { get; }
}
