using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

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
    private Action<string>? _onConfirmText;
    private Action<double>? _onConfirmNumber;
    private double _min, _max;

    public string Display => string.IsNullOrEmpty(Buffer) ? " " : Buffer;
    partial void OnBufferChanged(string value) => OnPropertyChanged(nameof(Display));

    /// <summary>打开为数值输入（带范围裁剪）</summary>
    public void OpenNumeric(string label, double current, string unit, double min, double max,
        Action<double> onConfirm)
    {
        _mode = InputMode.Numeric;
        FieldLabel = label;
        Unit = unit;
        _min = min;
        _max = max;
        // 范围提示：有小数时按小数显示（如 0.3 – 3.2），整数则不带小数点，避免把 0.3 显示成 0
        Hint = $"范围 {min:0.##} – {max:0.##} {unit}";
        Buffer = current == Math.Floor(current) ? ((long)current).ToString() : current.ToString();
        _onConfirmNumber = onConfirm;
        _onConfirmText = null;
        Shift = false;
        IsOpen = true;
    }

    /// <summary>打开为文本输入</summary>
    public void OpenText(string label, string current, string hint, Action<string> onConfirm)
    {
        _mode = InputMode.Text;
        FieldLabel = label;
        Unit = "";
        Hint = hint;
        Buffer = current ?? "";
        _onConfirmText = onConfirm;
        _onConfirmNumber = null;
        Shift = false;
        IsOpen = true;
    }

    /// <summary>输入一个字符（字母/数字/符号）。受 Shift 影响大小写。</summary>
    [RelayCommand]
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
    private void Backspace()
    {
        if (Buffer.Length > 0) Buffer = Buffer[..^1];
    }

    [RelayCommand]
    private void Space() { if (Buffer.Length < 64) Buffer += " "; }

    [RelayCommand]
    private void ToggleShift() => Shift = !Shift;

    [RelayCommand]
    private void Clear() => Buffer = "";

    [RelayCommand]
    private void Confirm()
    {
        if (_mode == InputMode.Numeric)
        {
            if (double.TryParse(Buffer, out double v))
            {
                v = Math.Max(_min, Math.Min(_max, v));
                _onConfirmNumber?.Invoke(v);
            }
        }
        else
        {
            _onConfirmText?.Invoke(Buffer.Trim());
        }
        IsOpen = false;
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;
}
