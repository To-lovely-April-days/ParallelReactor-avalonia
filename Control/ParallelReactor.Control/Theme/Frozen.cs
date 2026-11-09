using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 冻结（演示冻结、截图；PORT-PLAN §4.4「.frozen」）：根元素带 frozen 类，同时设置可继承的 <see cref="IsFrozenProperty"/>。
/// Styles/Frozen.axaml 让 .frozen 下所有控件的 Transitions 失效；自带动画的控件（ProgressPr 的呼吸等）读 IsFrozen 自己停。
/// </summary>
public static class Frozen
{
    public static readonly AttachedProperty<bool> IsFrozenProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsFrozen", typeof(Frozen), false, inherits: true);

    public static bool GetIsFrozen(AvaloniaObject o) => o.GetValue(IsFrozenProperty);
    public static void SetIsFrozen(AvaloniaObject o, bool value) => o.SetValue(IsFrozenProperty, value);

    /// <summary>给根元素加 / 去 frozen（类 + 继承属性）。</summary>
    public static void Apply(StyledElement root, bool frozen = true)
    {
        root.Classes.Set("frozen", frozen);
        root.SetValue(IsFrozenProperty, frozen);
    }
}
