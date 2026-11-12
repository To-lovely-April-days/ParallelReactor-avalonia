using System;
using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 「程序」画布最后的安全联锁框（样稿 safetyHTML，视图模型 = 程序面板的 <see cref="Pages.ReactorProgramVm"/>）：框与说明都是步骤卡宽
/// （<see cref="ApplyWidth"/>，由 StepFlowPanel 给），上方的分隔线横跨整行。
/// </summary>
public partial class SafetyView : UserControl, IStepWidthAware
{
    public SafetyView() => InitializeComponent();

    /// <summary>步骤卡宽（框和说明同宽，样稿 width: var(--step-w)）。</summary>
    public void ApplyWidth(double stepW)
    {
        if (Math.Abs(Box.Width - stepW) < 0.01 && Math.Abs(Note.Width - stepW) < 0.01) return;
        Box.Width = stepW;
        Note.Width = stepW;
    }
}

/// <summary>画布里跟着步骤卡宽变的元素（StepFlowPanel 在排版前告诉它卡宽）。</summary>
public interface IStepWidthAware
{
    void ApplyWidth(double stepW);
}
