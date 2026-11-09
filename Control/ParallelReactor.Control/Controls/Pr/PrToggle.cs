using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ParallelReactor.Control.Behaviors;
using PrAttach = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 带 b:Pr.Reason 也拦得住的开关（分段项、面板页签、文档页签、图标栏项…；主题照常用 PrSegItem / PrPTab 等）：
/// 原因存在时 Toggle() 什么也不做，所以 UI 自动化的 Toggle、HotKey 也翻不动它（普通 ToggleButton 只拦得住指针和键盘）。
/// 样式上就是 ToggleButton（StyleKeyOverride）。
/// </summary>
public class PrToggle : ToggleButton, IReasonGuarded
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
        if (PrAttach.IsLocked(this)) return;
        base.Toggle();
    }
}

/// <summary>带 b:Pr.Reason 也拦得住的复选框（主题 PrCheck）：同 <see cref="PrToggle"/>。</summary>
public class PrCheckBox : CheckBox, IReasonGuarded
{
    protected override Type StyleKeyOverride => typeof(CheckBox);

    protected override void Toggle()
    {
        if (PrAttach.IsLocked(this)) return;
        base.Toggle();
    }
}
