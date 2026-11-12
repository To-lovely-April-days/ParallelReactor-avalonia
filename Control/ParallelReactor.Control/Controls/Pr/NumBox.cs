using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrAttach = ParallelReactor.Control.Behaviors.Pr;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 数值输入（样稿 ui.numInput / .pr-numbox）：高 36、圆角 6；左右 32 px 的 −/+（bg-head，悬停 a-tint / a-d，不在 Tab 顺序里），
/// 数值 15 px 600 右对齐，单位 14 px t-3。步进 / 失焦 / 回车时按 Minimum / Maximum 限幅、按 Decimals 位小数格式化（同样稿 toFixed）；
/// 输入的数超出量程时限幅并提示「超出量程 · 已限制为 200.0」（M6，样稿 clampNum 的 warn toast，经 <see cref="OutOfRange"/> 交给 toast 宿主）。
/// 聚焦时 a-line 边框 + 1 px 外圈；b:Pr.Reason 时整体变灰、−/+ 不响应、数值只读、输入框离开 Tab 顺序（M6，样稿 readonly + tabindex=-1）。
/// 回车提交、↑ / ↓ 按 Step 步进是本机加的（样稿只在失焦时提交）。
/// </summary>
public sealed class NumBox : TemplatedControl
{
    /// <summary>
    /// 输入的数超出量程、已限幅（样稿 clampNum：「超出量程 · 已限制为 」+ 限幅后的值，warn）：（控件, 提示文字）。
    /// toast 宿主（Services/ToastService）订阅；没有订阅者时只限幅、不提示。
    /// </summary>
    public static event Action<NumBox, string>? OutOfRange;

    /// <summary>超出量程的提示（样稿 clampNum 的原话 + 限幅后按小数位格式化的值）。</summary>
    public static string OutOfRangeText(string shown) => "超出量程 · 已限制为 " + shown;

    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<NumBox, double?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<NumBox, double>(nameof(Minimum), double.NaN);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<NumBox, double>(nameof(Maximum), double.NaN);
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<NumBox, double>(nameof(Step), 1);
    public static readonly StyledProperty<int> DecimalsProperty = AvaloniaProperty.Register<NumBox, int>(nameof(Decimals));
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<NumBox, string?>(nameof(Unit));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<NumBox, string?>(nameof(Label));
    public static readonly StyledProperty<bool> ShowSteppersProperty = AvaloniaProperty.Register<NumBox, bool>(nameof(ShowSteppers), true);

    public double? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>下限（NaN = 不限）。</summary>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    /// <summary>上限（NaN = 不限）。</summary>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    /// <summary>小数位数（格式化与步进后的取整）。</summary>
    public int Decimals { get => GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>无障碍名（样稿 aria-label）。</summary>
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool ShowSteppers { get => GetValue(ShowSteppersProperty); set => SetValue(ShowSteppersProperty, value); }

    private TextBox? _input;
    private Button? _minus, _plus;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_minus != null) _minus.Click -= OnMinus;
        if (_plus != null) _plus.Click -= OnPlus;
        if (_input != null) { _input.LostFocus -= OnInputLostFocus; _input.KeyDown -= OnInputKeyDown; }
        _input = e.NameScope.Find<TextBox>("PART_Input");
        _minus = e.NameScope.Find<Button>("PART_Minus");
        _plus = e.NameScope.Find<Button>("PART_Plus");
        if (_minus != null) _minus.Click += OnMinus;
        if (_plus != null) _plus.Click += OnPlus;
        if (_input != null)
        {
            _input.LostFocus += OnInputLostFocus;
            _input.KeyDown += OnInputKeyDown;
            Avalonia.Automation.AutomationProperties.SetName(_input, Label ?? Unit ?? "数值");
        }
        UpdateReason();
        ShowValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == DecimalsProperty) ShowValue();
        else if (change.Property == PrAttach.ReasonProperty || change.Property == IsEffectivelyEnabledProperty) UpdateReason();
    }

    private bool Locked => !string.IsNullOrWhiteSpace(PrAttach.GetReason(this)) || !IsEffectivelyEnabled;

    private void UpdateReason()
    {
        if (_input != null)
        {
            _input.IsReadOnly = Locked;
            // 锁着时离开 Tab 顺序（样稿 readonly + tabindex=-1）：Tab 跳过它，停在旁边带原因的「设定」按钮上
            _input.IsTabStop = !Locked;
        }
        if (_minus != null) _minus.IsEnabled = !Locked;
        if (_plus != null) _plus.IsEnabled = !Locked;
    }

    /// <summary>按 Decimals 位格式化（JS toFixed 语义，同样稿）；空值和非有限值显示为空。</summary>
    public string Format(double? v) => v is double d && double.IsFinite(d) ? JsNumber.ToFixed(d, Math.Max(0, Decimals)) : "";

    /// <summary>限幅 + 按小数位取整（Core.Format.NumInput.Clamp）；非有限值（NaN / ±∞）不接受，返回原来的值。</summary>
    public double Clamp(double v) =>
        NumInput.Clamp(v, Minimum, Maximum, Decimals, fallback: NumInput.StepBase(Value, Minimum));

    private void ShowValue()
    {
        if (_input != null && !_input.IsFocused) _input.Text = Format(Value);
    }

    /// <summary>
    /// −/+、↑ / ↓：从输入框里的数步进（样稿 stepNum 读 input.value：打了还没提交的数也算；读不出时从当前值 / 下限起），再限幅；
    /// 输入的数本来就超出量程时同样提示（样稿 clampNum 看的是步进前的输入）。
    /// </summary>
    private void Nudge(int dir)
    {
        if (Locked) return;
        bool typed = NumInput.TryParse(_input?.Text, out var t);
        double cur = typed ? t : NumInput.StepBase(Value, Minimum);
        Value = Clamp(cur + dir * Step);
        if (_input != null) _input.Text = Format(Value);
        if (typed && Outside(cur)) RaiseOutOfRange();
    }

    private void OnMinus(object? sender, RoutedEventArgs e) => Nudge(-1);
    private void OnPlus(object? sender, RoutedEventArgs e) => Nudge(+1);

    /// <summary>超出上下限（NaN = 不限）。</summary>
    private bool Outside(double v) => (!double.IsNaN(Minimum) && v < Minimum) || (!double.IsNaN(Maximum) && v > Maximum);

    private void RaiseOutOfRange() => OutOfRange?.Invoke(this, OutOfRangeText(Format(Value)));

    /// <summary>
    /// 回车 / 失焦时提交：清空 = 没有值（null）；有限的数 → 限幅 + 取整（超出量程时提示「超出量程 · 已限制为 …」，样稿 clampNum）；
    /// 其它（「abc」「NaN」「Infinity」「1e400」…）无效 → 值不变、输入框恢复原值（M1 复核：以前「NaN」会把值变成 NaN，无上下限时「1e400」变成 +∞）。
    /// 全角数字按半角解析。
    /// </summary>
    private void Commit()
    {
        if (_input == null || Locked) return;
        bool outside = false;
        if (string.IsNullOrWhiteSpace(_input.Text)) Value = null;
        else if (NumInput.TryParse(_input.Text, out var v))
        {
            Value = Clamp(v);
            outside = Outside(v);
        }
        _input.Text = Format(Value);
        if (outside) RaiseOutOfRange();
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); e.Handled = true; break;
            case Key.Up: Nudge(+1); e.Handled = true; break;
            case Key.Down: Nudge(-1); e.Handled = true; break;
        }
    }
}
