using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using ParallelReactor.Commands;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 屏幕全键盘（QWERTY）。点击输入框时打开，顶部回显「字段名 + 当前输入」，
/// 确认后通过回调把文本（或校验后的数值）写回目标字段。
/// 支持两种模式：Numeric（数值，确认时按 min/max 裁剪）、Text（自由文本）。
/// </summary>
public partial class KeyboardViewModel : ViewModelBase
{
    public enum InputMode { Numeric, Text }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _fieldLabel = "";
    [ObservableProperty] private string _buffer = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _shift;     // 大小写

    private InputMode _mode = InputMode.Text;
    private Func<string, Task<OpResult>>? _onConfirmTextOp;
    private Func<double, Task<OpResult>>? _onConfirmNumberOp;

    /// <summary>返回 OpResult 的确认回调的结果去向（MainViewModel 设为 Local：转成 Toast）。</summary>
    public Action<Task<OpResult>>? ResultSink { get; set; }
    private double _min, _max;

    public string Display => string.IsNullOrEmpty(Buffer) ? " " : Buffer;
    partial void OnBufferChanged(string value) => OnPropertyChanged(nameof(Display));

    /// <summary>
    /// 打开为数值输入（带范围裁剪）。确认回调必须经命令层（<c>Ops.Try*</c>）并返回其 OpResult，
    /// 结果交给 <see cref="ResultSink"/> 统一提示。确认时的限幅保留作纵深防御与本地体验。
    /// </summary>
    public void OpenNumeric(string label, double current, string unit, double min, double max,
        Func<double, Task<OpResult>> onConfirm)
    {
        OpenNumericCore(label, current, unit, min, max);
        _onConfirmNumberOp = onConfirm;
    }

    private void OpenNumericCore(string label, double current, string unit, double min, double max)
    {
        _mode = InputMode.Numeric;
        FieldLabel = label;
        Unit = unit;
        _min = min;
        _max = max;
        // 范围提示：有小数时按小数显示（如 0.3 – 3.2），整数则不带小数点，避免把 0.3 显示成 0
        Hint = $"范围 {min:0.##} – {max:0.##} {unit}";
        Buffer = current == Math.Floor(current) ? ((long)current).ToString() : current.ToString();
        ClearCallbacks();
        Shift = false;
        IsOpen = true;
    }

    private void ClearCallbacks()
    {
        _onConfirmNumberOp = null;
        _onConfirmTextOp = null;
    }

    /// <summary>打开为文本输入；确认回调经命令层并返回其 OpResult（见数值版说明）。</summary>
    public void OpenText(string label, string current, string hint, Func<string, Task<OpResult>> onConfirm)
    {
        OpenTextCore(label, current, hint);
        _onConfirmTextOp = onConfirm;
    }

    private void OpenTextCore(string label, string current, string hint)
    {
        _mode = InputMode.Text;
        FieldLabel = label;
        Unit = "";
        Hint = hint;
        Buffer = current ?? "";
        ClearCallbacks();
        Shift = false;
        IsOpen = true;
    }

    /// <summary>输入一个字符（字母/数字/符号）。受 Shift 影响大小写。</summary>
    [RelayCommand]
    [ViewEntry("键盘输入")]
    private void Key(string k)
    {
        if (Buffer.Length >= 64) return;
        string ch = k;
        if (Shift && k.Length == 1 && char.IsLetter(k[0]))
            ch = k.ToUpperInvariant();
        Buffer += ch;
        if (Shift && k.Length == 1 && char.IsLetter(k[0])) Shift = false;
    }

    [RelayCommand]
    [ViewEntry("键盘输入")]
    private void Backspace()
    {
        if (Buffer.Length > 0) Buffer = Buffer[..^1];
    }

    [RelayCommand]
    [ViewEntry("键盘输入")]
    private void Space() { if (Buffer.Length < 64) Buffer += " "; }

    [RelayCommand]
    [ViewEntry("键盘输入")]
    private void ToggleShift() => Shift = !Shift;

    [RelayCommand]
    [ViewEntry("键盘输入")]
    private void Clear() => Buffer = "";

    [RelayCommand]
    [ViewEntry("只把值交给确认回调（回调本身经命令层）")]
    private void Confirm()
    {
        if (_mode == InputMode.Numeric)
        {
            if (double.TryParse(Buffer, out double v))
            {
                v = Math.Max(_min, Math.Min(_max, v));
                if (_onConfirmNumberOp != null) ResultSink?.Invoke(_onConfirmNumberOp(v));
            }
        }
        else
        {
            var text = Buffer.Trim();
            if (_onConfirmTextOp != null) ResultSink?.Invoke(_onConfirmTextOp(text));
        }
        IsOpen = false;
    }

    [RelayCommand]
    [ViewEntry("关闭键盘")]
    private void Cancel() => IsOpen = false;
}
