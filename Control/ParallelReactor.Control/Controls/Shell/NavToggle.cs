using System;
using Avalonia.Controls.Primitives;
using ParallelReactor.Control.Behaviors;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 「当前项」开关（文档页签、图标栏项）：选中状态完全由视图模型决定（IsChecked 单向绑定），点击只执行 Command，
/// 自己从不翻转 —— 点当前页签不会把它「取消选中」，UI 自动化 / HotKey 的 Toggle 也翻不动它（同 pr:PrToggle 的原因保护）。
/// 样式上就是 ToggleButton（主题照常用 PrDocTab / PrSideItem）。
/// </summary>
public sealed class NavToggle : ToggleButton, IReasonGuarded
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle() { }
}
