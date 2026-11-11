using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 步骤下方的条件框（样稿 reactor.js condHTML / 组件陈列的 ui.stepCond；视图模型 = 同一步的 <see cref="StepCardVm"/>）。
/// 框窄时（<see cref="ApplyWidth"/>：画布给的步骤卡宽）先把「结束条件」写成「条件」，再把值 18 → 16 px，最后只留值 —— 读数始终完整、至少留 8 px
/// （Core 的 <see cref="StepFlowLayout.CondFit"/>，按界面字体量字，数字按「0」量，同样稿 ui.textWidth）。组件陈列的普通条件框不改写（样稿 ui.stepCond）。
/// 只在可视树里时订阅视图模型（挂上时订阅并按当前内容重排一次，摘下时退订）：画布拆掉的条件框不被视图模型拽住、也不再跟着量字（复核 c2-ui-1）。
/// </summary>
public partial class StepCondView : UserControl
{
    private static readonly StageTextMeasure TextMeasure = new();
    /// <summary>订阅着的视图模型（只在可视树里时有）。</summary>
    private StepCardVm? _watched;
    private bool _attached;
    private double _width = StepFlowLayout.StepDefault;

    public StepCondView()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>画布排好的步骤卡宽（条件框同宽）：按它决定标签与值的字号。</summary>
    public void ApplyWidth(double stepW)
    {
        if (Math.Abs(stepW - _width) < 0.01) return;
        _width = stepW;
        Apply();
    }

    /// <summary>当前显示的标签（核对用；空 = 只留值）。</summary>
    public string ShownLabel => Label.Text ?? "";

    /// <summary>值用 16 px（核对用）。</summary>
    public bool SmallValue => Value.Classes.Contains("sm");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(_attached ? DataContext as StepCardVm : null);
        Apply();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch(DataContext as StepCardVm);
        // 摘下期间没有订阅：视图模型可能已经变了
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch(null);
    }

    private void Watch(StepCardVm? vm)
    {
        if (ReferenceEquals(vm, _watched)) return;
        if (_watched != null) _watched.PropertyChanged -= OnVm;
        _watched = vm;
        if (vm != null) vm.PropertyChanged += OnVm;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty) Apply();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepCardVm.CondLabel) or nameof(StepCardVm.CondValue) or nameof(StepCardVm.PlainCond)) Apply();
    }

    private void Apply()
    {
        var vm = DataContext as StepCardVm;
        string label = vm?.CondLabel ?? "", value = vm?.CondValue ?? "";
        bool small = false;
        if (vm is { PlainCond: false })
        {
            var family = FontFamily;
            (label, small) = StepFlowLayout.CondFit(label, value, _width,
                (t, size, weight) => TextMeasure.Width(family, StageLayout.Digits0(t), size, weight >= 600));
        }
        if (Label.Text != label) Label.Text = label;
        Label.IsVisible = label.Length > 0;
        if (Value.Text != value) Value.Text = value;
        Value.Classes.Set("sm", small);
    }
}
