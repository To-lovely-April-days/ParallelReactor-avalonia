using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Models;

namespace ParallelReactor.Control.ViewModels;

/// <summary>plan-canvas 里绝对定位的一个元素（坐标 = mock 的 CSS px）。</summary>
public abstract partial class CanvasItemVm : ObservableObject
{
    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;
    [ObservableProperty] private double _width;
}

/// <summary>步骤卡的一节（加热器 / 进气阀 SV / 搅拌电机）。</summary>
public sealed partial class StepSectionVm : ObservableObject
{
    public StepSectionVm(string deviceLabel, string mode, string label, string value, string unit, string? editKey)
    { DeviceLabel = deviceLabel; _mode = mode; Label = label; _value = value; Unit = unit; EditKey = editKey; _editText = value; }
    public string DeviceLabel { get; }
    [ObservableProperty] private string _mode;
    public string Label { get; }
    [ObservableProperty] private string _value;
    public string Unit { get; }
    /// <summary>可编辑的 params 键（null = 只读）。</summary>
    public string? EditKey { get; }
    public bool Editable => EditKey != null;
    [ObservableProperty] private string _editText;
    [ObservableProperty] private bool _isEditing;
    public string Display => Value + (Unit.Length > 0 ? " " + Unit : "");
    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(Display));
}

public sealed partial class StepCardVm : CanvasItemVm
{
    public StepCardVm(int index, ProgramStepDoc step) { Index = index; Step = step; }
    public int Index { get; }
    public ProgramStepDoc Step { get; }
    public string Title => $"{Index}: {(Step.StepType?.Zh() ?? Step.Type)}";
    [ObservableProperty] private bool _isGreen;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isEditing;
    public ObservableCollection<StepSectionVm> Sections { get; } = new();
}

public sealed partial class DurationVm : CanvasItemVm
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _strong = "";
}
public sealed class FlowLinkVm : CanvasItemVm { }
public sealed class DividerVm : CanvasItemVm { }
public sealed partial class HardwareCheckVm : CanvasItemVm
{
    [ObservableProperty] private string _rv = "RV1 :";
    [ObservableProperty] private string _result = "= 通过";
}
public sealed partial class HardwareNoteVm : CanvasItemVm
{
    [ObservableProperty] private string _text = "急停未触发 · 压力在安全范围";
}

/// <summary>
/// 程序编辑器：把 <see cref="ProgramDoc"/> 排成 mock 的卡片布局（每行 2 张：100/259，行距 283），编辑态把设定值写回 params。
/// HMI 没有 GET program，草稿只在 PC 侧（ProgramDraftStore）。
/// </summary>
public sealed partial class PlanEditorVm : ObservableObject
{
    public ObservableCollection<CanvasItemVm> Items { get; } = new();
    [ObservableProperty] private string _reactorName = "RV1";
    [ObservableProperty] private string _programName = "加氢测试-01";
    [ObservableProperty] private string _rootName = "系统视图";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _canvasWidth = 560;
    [ObservableProperty] private double _canvasHeight = 700;
    [ObservableProperty] private string _hardwareResult = "= 通过";
    [ObservableProperty] private string _hardwareNote = "急停未触发 · 压力在安全范围";

    public ProgramDoc? Program { get; private set; }
    public int Rv { get; private set; } = 1;
    private int _globalRpm;
    private int _currentStep = -1;
    private DividerVm[] _dividers = Array.Empty<DividerVm>();
    private HardwareCheckVm? _check;
    private HardwareNoteVm? _note;

    partial void OnCanvasWidthChanged(double value)
    {
        foreach (var d in _dividers) d.Width = Math.Max(60, value - 56 - 45);
    }

    public void SetContext(int rv, string reactorName, ProgramDoc program, int globalRpm, int currentStep)
    {
        Rv = rv; ReactorName = reactorName; Program = program; ProgramName = program.Name; _globalRpm = globalRpm; _currentStep = currentStep;
        Rebuild();
    }

    public void UpdateLive(int globalRpm, int currentStep, bool running, string hardwareResult, string note)
    {
        HardwareResult = hardwareResult; HardwareNote = note;
        if (_check != null) _check.Result = hardwareResult;
        if (_note != null) _note.Text = note;
        bool changed = globalRpm != _globalRpm || currentStep != _currentStep;
        _globalRpm = globalRpm; _currentStep = currentStep;
        if (!changed) return;
        foreach (var it in Items)
        {
            if (it is not StepCardVm c) continue;
            c.IsCurrent = running && c.Index - 1 == currentStep;
            foreach (var s in c.Sections)
                if (s.DeviceLabel.StartsWith("搅拌", StringComparison.Ordinal) && s.Mode == "共用转速")
                { s.Value = globalRpm.ToString(CultureInfo.InvariantCulture); if (!IsEditing) s.EditText = s.Value; }
        }
    }

    public void Rebuild()
    {
        Items.Clear();
        var prog = Program;
        if (prog == null) return;
        int n = prog.Steps.Count;
        int rows = Math.Max(1, (n + 1) / 2);
        var dividers = new System.Collections.Generic.List<DividerVm>();
        double lastTemp = 25, lastPres = 0;
        for (int i = 0; i < n; i++)
        {
            int row = i / 2, col = i % 2;
            double top = 22 + row * 283;
            var step = prog.Steps[i];
            var card = new StepCardVm(i + 1, step) { Left = col == 0 ? 100 : 259, Top = top, Width = col == 0 ? 132 : 140, IsGreen = col == 1 };
            card.IsCurrent = _currentStep == i;
            BuildSections(card, step, ref lastTemp, ref lastPres);
            Items.Add(card);
            var dur = new DurationVm { Left = col == 0 ? 143 : 300, Top = top + 197, Width = col == 0 ? 125 : 126 };
            (dur.Text, dur.Strong) = DurationOf(step);
            Items.Add(dur);
            if (col == 1) Items.Add(new FlowLinkVm { Left = 232, Top = top + 194, Width = 66 });
        }
        for (int r = 0; r < rows; r++)
        {
            var d = new DividerVm { Left = 56, Top = 291 + r * 283, Width = Math.Max(60, CanvasWidth - 101) };
            dividers.Add(d); Items.Add(d);
        }
        _dividers = dividers.ToArray();
        double checkTop = rows == 1 ? 329 : 219 + (rows - 1) * 283 + 58;
        _check = new HardwareCheckVm { Left = 112, Top = checkTop, Width = 125, Rv = ReactorName + " :", Result = HardwareResult };
        _note = new HardwareNoteVm { Left = 112, Top = checkTop + 47, Width = 132, Text = HardwareNote };
        Items.Add(_check); Items.Add(_note);
        CanvasHeight = checkTop + 47 + 40 + 30;
    }

    private static readonly string StirLabel = "搅拌电机（全局 RV1–RV8）:";

    private void BuildSections(StepCardVm card, ProgramStepDoc s, ref double lastTemp, ref double lastPres)
    {
        string rpm = _globalRpm.ToString(CultureInfo.InvariantCulture);
        var type = s.StepType;
        string Num(string k, double d, string f = "0.##") => Get(s, k, d).ToString(f, CultureInfo.InvariantCulture);
        string Sel(string k, string d) => s.Params.TryGetValue(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? d : d;
        string gasLabel(string k, string d) => ProgramSchemaCodes.LabelOf(s.Type, k, Sel(k, d)) ?? Sel(k, d);
        switch (type)
        {
            case StepType.Heat:
                lastTemp = Get(s, "temp", 150);
                card.Sections.Add(new("加热器 :", Sel("mode", "fixed") == "curve" ? "曲线升温" : "固定升温", "设定值", Num("temp", 150, "0.00"), "℃", "temp"));
                card.Sections.Add(new("进气阀 SV :", "关闭", "压力", "—", "psi", null));
                card.Sections.Add(new(StirLabel, "共用转速", "设定值", rpm, "rpm", null));
                break;
            case StepType.Press:
                lastPres = Get(s, "pres", 50);
                card.Sections.Add(new("加热器 :", "保持", "设定值", lastTemp.ToString("0.00", CultureInfo.InvariantCulture), "℃", null));
                card.Sections.Add(new("进气阀 SV :", gasLabel("gas", "A") + (Sel("regulate", "on") == "on" ? " · 稳压保持" : " · 目标后保持"), "目标压力", Num("pres", 50, "0.0"), "psi", "pres"));
                card.Sections.Add(new(StirLabel, "共用转速", "设定值", rpm, "rpm", null));
                break;
            case StepType.React:
                card.Sections.Add(new("加热器 :", "保持", "设定值", lastTemp.ToString("0.00", CultureInfo.InvariantCulture), "℃", null));
                card.Sections.Add(new("进气阀 SV :", "补压保持", "压力", lastPres.ToString("0.0", CultureInfo.InvariantCulture), "psi", null));
                card.Sections.Add(new(StirLabel, "共用转速", "设定值", rpm, "rpm", null));
                break;
            case StepType.End:
                {
                    var mode = Sel("mode", "seal");
                    card.Sections.Add(new("加热器 :", "关闭 · 降温", "降至", Num("cool", 40, "0.00"), "℃", "cool"));
                    card.Sections.Add(new("排空阀 :", mode == "vent" ? "打开" : mode == "purge" ? "吹扫" : mode == "quench" ? "淬灭" : "关闭 · 密封", "泄压至", mode == "vent" ? "0.0" : "—", "psi", null));
                    card.Sections.Add(new(StirLabel, "共用转速", "设定值", "0", "rpm", null));
                    break;
                }
            case StepType.Purge:
                card.Sections.Add(new("加热器 :", "保持", "设定值", lastTemp.ToString("0.00", CultureInfo.InvariantCulture), "℃", null));
                card.Sections.Add(new("进气阀 SV :", gasLabel("gas", "inert") + " 吹扫", "循环", Num("cycles", 10, "0"), "次", "cycles"));
                card.Sections.Add(new(StirLabel, "共用转速", "设定值", rpm, "rpm", null));
                break;
            case StepType.Wait:
                card.Sections.Add(new("加热器 :", "保持", "设定值", lastTemp.ToString("0.00", CultureInfo.InvariantCulture), "℃", null));
                card.Sections.Add(new("进气阀 SV :", "关闭", "压力", lastPres > 0 ? lastPres.ToString("0.0", CultureInfo.InvariantCulture) : "—", "psi", null));
                card.Sections.Add(new(StirLabel, gasLabel("mode", "fixed"), "时长", Num("time", 30, "0.##"), "min", "time"));
                break;
            case StepType.Stir:
                card.Sections.Add(new("加热器 :", "保持", "设定值", lastTemp.ToString("0.00", CultureInfo.InvariantCulture), "℃", null));
                card.Sections.Add(new("进气阀 SV :", "关闭", "压力", "—", "psi", null));
                card.Sections.Add(new(StirLabel, "程序设定", "设定值", Num("rpm", 1000, "0"), "rpm", "rpm"));
                break;
            default:
                card.Sections.Add(new("步骤 :", s.Type, "参数", s.Params.Count.ToString(CultureInfo.InvariantCulture), "项", null));
                break;
        }
    }

    private (string, string) DurationOf(ProgramStepDoc s)
    {
        string rv = ReactorName + ": ";
        string M(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        return s.StepType switch
        {
            StepType.Heat => (rv + "步骤时间", "> " + M(Get(s, "wait", 15)) + " mins"),
            StepType.Press => (rv + "结束条件", "压力 ≥ " + M(Get(s, "pres", 50)) + " psi"),
            StepType.React => (rv + "步骤时间", "> " + M(Get(s, "time", 120)) + " mins"),
            StepType.Wait => (rv + "步骤时间", "> " + M(Get(s, "time", 30)) + " mins"),
            StepType.Stir => (rv + "步骤时间", "> " + M(Get(s, "dur", 5)) + " mins"),
            StepType.Purge => (rv + "步骤时间", "> " + M(Get(s, "cycles", 10) * (Get(s, "fill", 3) + Get(s, "vent", 5) + 1) / 60.0) + " mins"),
            StepType.End => (rv + "结束条件", "温度 < " + M(Get(s, "cool", 40)) + " ℃"),
            _ => (rv + "步骤", "—"),
        };
    }

    private static double Get(ProgramStepDoc s, string key, double d)
        => s.Params.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : d;

    /// <summary>进入编辑：EditText = 当前值。</summary>
    public void BeginEdit()
    {
        IsEditing = true;
        foreach (var it in Items) if (it is StepCardVm c) { c.IsEditing = true; foreach (var s in c.Sections) { s.EditText = s.Value; s.IsEditing = s.Editable; } }
    }

    /// <summary>保存：解析各可编辑节的数字写回 params，返回错误文案（null = 成功）。</summary>
    public string? Save()
    {
        var prog = Program;
        if (prog == null) { IsEditing = false; return null; }
        foreach (var it in Items)
        {
            if (it is not StepCardVm c) continue;
            foreach (var s in c.Sections)
            {
                if (s.EditKey == null) continue;
                var txt = s.EditText.Trim();
                if (txt.EndsWith(s.Unit, StringComparison.Ordinal)) txt = txt[..^s.Unit.Length].Trim();
                if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return $"步骤 {c.Index}「{s.Label}」不是有效数字：{s.EditText}";
                c.Step.Params[s.EditKey] = JsonSerializer.SerializeToElement(v);
            }
        }
        IsEditing = false;
        Rebuild();
        return null;
    }

    public void CancelEdit() { IsEditing = false; Rebuild(); }
}
