using System;
using Avalonia.Media;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// UiFont 按操作系统选择（PORT-PLAN §4.3）。App.Initialize 在创建任何窗口之前写入 Application.Resources["UiFont"]。
/// 内嵌的 Inter（WithInterFont）放在最后，保证有确定的回退。PR_CONTROL_UI_FONT 可整体覆盖（探针 / 对比用）。
/// </summary>
public static class UiFonts
{
    /// <summary>Windows（产品目标）：Segoe UI 西文与数字，微软雅黑 UI / 微软雅黑中文。</summary>
    public const string WindowsStack = "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, fonts:Inter#Inter";

    /// <summary>
    /// macOS（开发与截图）：内嵌的 Inter 放在苹方前面（苹方的数字不支持等宽；Inter 有真 600 字重、+tnum 后数字等宽）。
    /// M1 复核更正了字体探针的结论：.AppleSystemUIFont 经 Skia / HarfBuzz 排版时没有 CoreText / WebKit 的光学尺寸与字距（空格只有 0.205 em），
    /// 西文 / 数字比样稿参照图窄 6–11 %，「 · 」和数字–单位之间显得挤，审计也比样稿和 Windows（Segoe UI 更宽）乐观；
    /// Inter 的宽度与参照图几乎一致，也更接近 Segoe UI。Avalonia 在真窗口里同样用 Skia 排版，所以截图和交互窗口用同一个栈。
    /// </summary>
    public const string MacStack = "fonts:Inter#Inter, PingFang SC";

    /// <summary>macOS 系统字体栈（M1 第一版的选择，西文更窄）：tools/shots.sh 用它把每张截图再审计一遍（PR_CONTROL_UI_FONT），两种宽度下都不许有问题。</summary>
    public const string MacSystemStack = ".AppleSystemUIFont, PingFang SC, fonts:Inter#Inter";

    /// <summary>其它系统（Linux 等）。</summary>
    public const string OtherStack = "fonts:Inter#Inter, Noto Sans CJK SC, Noto Sans SC";

    public static string Stack()
    {
        var env = Environment.GetEnvironmentVariable("PR_CONTROL_UI_FONT");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (OperatingSystem.IsWindows()) return WindowsStack;
        if (OperatingSystem.IsMacOS()) return MacStack;
        return OtherStack;
    }

    public static FontFamily Create() => new(Stack());

    /// <summary>
    /// 只含中文字体的栈（UiFontCjk）。Avalonia 11.1 的字体回退有个缺陷：同一个 TextBlock 里字重不同的几段中文（Run）都走回退时，
    /// 后一段会沿用前一段回退到的字体实例（连字重一起），于是「急停<b>不需要控制权</b>，…」的粗体段画成常规、之后的常规段又可能画成粗体（M1 实测）。
    /// 绕过：字重变化之后的中文 Run 显式用这个字体族（不经回退），例如提示条里的粗体段和它后面的一段。
    /// </summary>
    public const string WindowsCjk = "Microsoft YaHei UI, Microsoft YaHei";
    public const string MacCjk = "PingFang SC";
    public const string OtherCjk = "Noto Sans CJK SC, Noto Sans SC";

    public static string CjkStack()
    {
        if (OperatingSystem.IsWindows()) return WindowsCjk;
        if (OperatingSystem.IsMacOS()) return MacCjk;
        return OtherCjk;
    }

    public static FontFamily CreateCjk() => new(CjkStack());
}
