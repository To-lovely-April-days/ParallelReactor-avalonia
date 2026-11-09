using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views.Legacy;

/// <summary>
/// 旧界面的宿主（M2–M7 过渡用；PORT-PLAN §2.5、D5）：旧的 MainView / 程序编辑 / 性能测试视图是在 Fluent 模板上按类名叠旧样式画出来的，
/// 而 M2 起应用级的默认主题换成了 Pr 主题（Styles/Theme/Defaults.axaml）。宿主把这些控件的默认主题在自己的资源里换回 Fluent 的，
/// 并带上 legacy 类，旧视图的样子和行为不变：旧的字号 / 字体 / 配色（9 px、LegacyUiFont、Ink、AppBg）与旧的全局滚动条样式都挂在 .legacy 上
/// （Styles/Panels.axaml，旧样式文件；这里和使用处都不写小字号 —— 字号检查只放行旧样式文件）。
/// M2 起承载程序页与性能测试页（Views/Pages/LegacyProgramPage、PerfPage）；M7 用新组件重做后删除。
/// </summary>
public sealed class LegacyHost : ContentControl
{
    /// <summary>默认主题被 Pr 主题接管的控件类型（与 Defaults.axaml 一致）。</summary>
    private static readonly Type[] Themed =
    {
        typeof(Button), typeof(ToggleButton), typeof(CheckBox), typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem),
        typeof(Menu), typeof(MenuItem), typeof(ContextMenu), typeof(MenuFlyoutPresenter), typeof(FlyoutPresenter), typeof(Separator),
        typeof(ListBox), typeof(ListBoxItem), typeof(ScrollViewer), typeof(ScrollBar), typeof(ToolTip),
    };

    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>旧视图所在的旧样式页面（宿主的 DataContext；旧视图的「展开」「关闭」按钮交给它）。</summary>
    public static ILegacyPage? PageOf(Visual v) => v.FindAncestorOfType<LegacyHost>()?.DataContext as ILegacyPage;

    public LegacyHost()
    {
        Classes.Add("legacy");
        // 外框（ShellView）给字符串内容的 DataTemplate 是 PrText（按 WebKit 量宽）；旧视图要原来的样子：字符串照旧是普通 TextBlock
        // （找 DataTemplate 时先找近的，所以这里的优先）。这正是 11.1 ContentPresenter 对旧视图的默认做法：旧视图的按钮全用旧样式文件的平面模板
        // （Styles/Panels.axaml 的 Button.flat：ContentPresenter 不认访问键），默认就是普通 TextBlock（认访问键的 presenter 才是 AccessText），
        // 所以旧样式的「X TextBlock」后代选择器照旧匹配原来匹配的字（例如「Grid.inspector-field TextBlock」与「运行控制」的「跳过本步」「中止」）。
        // Shot/ShellChecks 核对这一点：旧视图里字符串内容的 presenter 都不认访问键、字都是普通 TextBlock。
        DataTemplates.Add(new FuncDataTemplate<string>((_, _) => new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding(".") }, supportsRecycling: true));
        var fluent = Application.Current?.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent == null) return;
        foreach (var t in Themed)
            if (fluent.TryGetResource(t, ThemeVariant.Light, out var theme) && theme is ControlTheme ct)
                Resources[t] = ct;
    }
}

